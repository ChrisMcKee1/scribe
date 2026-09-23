using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;

namespace Scribe.AsrCheck.Scenarios;

/// <summary>The production services one suite run drives, all constructed against temporary state.</summary>
internal sealed class ScenarioEngine
{
    public required TranscriptionService Transcription { get; init; }

    public required VadService Vad { get; init; }

    public required ScribeDatabase Database { get; init; }

    public required HistoryRepository History { get; init; }

    public required TextPostProcessor PostProcessor { get; init; }

    public required ProductionHooks Hooks { get; init; }

    public required ResourceMeter Meter { get; init; }
}

/// <summary>A scenario written to disk: the WAV the run reads back, and what it was built from.</summary>
internal sealed class MaterializedScenario
{
    public required ScenarioDefinition Definition { get; init; }

    public required string Path { get; init; }

    public required string RelativePath { get; init; }

    public required string Sha256 { get; init; }

    public required double Seconds { get; init; }

    public required WaveFormat Format { get; init; }

    public required IReadOnlyList<Placement> Placements { get; init; }

    public required int PlacementRate { get; init; }
}

internal sealed record PipelineRun(
    PipelineMeasurement Measurement,
    CapturedAudio Captured,
    CapturedAudio DecodeInput,
    TranscriptionResult? Decoded,
    TextPostProcessingResult? Post);

/// <summary>
/// Plays one scenario through the production Core pipeline and records what happened.
/// <para>
/// The stages and their order are the ones <c>DictationController.ProcessAsync</c> runs: the capture
/// service's own format normalization, peak meter and conversion to 16 kHz mono; VAD trim; decode;
/// post-processing with <c>ProcessDetailed(text, text)</c>, which is the controller's call when AI
/// cleanup is off; then the history write for anything that would have been inserted. The
/// controller itself lives in the WPF app and is not referenced here, so its branch decisions (what
/// counts as silent, as no speech, as nothing to insert) are mirrored in <see cref="RunPipeline"/>,
/// and every value those decisions read comes from Core. AI cleanup and text injection are skipped:
/// no provider is called and no input is sent.
/// </para>
/// </summary>
internal sealed class ScenarioRunner(ScenarioEngine engine, BaseLibrary library, RunOptions options)
{
    /// <summary>
    /// A chunked decode keeps nearly every word its phrases produce alone; a collapse like the
    /// 0.3.10 empty-decode report, or a chunk whose text goes missing, loses a large block at once.
    /// The floor sits far below a healthy run and far above either failure.
    /// </summary>
    private const double LongDictationCoverageFloor = 0.85;

    /// <summary>A chunk decoded twice would add a third or more; healthy runs insert a few percent.</summary>
    private const double LongDictationInsertionCeiling = 0.15;

    private readonly Dictionary<string, IReadOnlyList<string>> _references = new(StringComparer.Ordinal);
    private readonly List<double> _addMs = [];
    private readonly Dictionary<string, double> _decodeWallMs = new(StringComparer.Ordinal);
    private int _redecodeBudget = options.Quick ? 2 : int.MaxValue;
    private string? _encodingName;

    public HistoryReport History { get; } = new();

    public string? ModelId { get; private set; }

    public double? DecodeWallMs(string scenario) => _decodeWallMs.TryGetValue(scenario, out var ms) ? ms : null;

    public ScenarioResult Run(MaterializedScenario scenario)
    {
        var definition = scenario.Definition;
        var result = new ScenarioResult
        {
            Name = definition.Name,
            Category = definition.Category.ToString(),
            Title = definition.Title,
            Quick = definition.Quick,
            File = scenario.RelativePath,
            Sha256 = scenario.Sha256,
            Seconds = Math.Round(scenario.Seconds, 3),
            DeviceFormat = FormatInfo.From(definition.Device.Label, scenario.Format),
            Sources = Sources(definition),
            Transform = definition.Transform,
            ExpectedText = definition.ExpectedText,
            ExpectedBehavior = definition.ExpectedBehavior,
            MinOverlap = definition.MinOverlap,
        };

        try
        {
            var (data, fileFormat) = DeviceAudio.ReadWav(scenario.Path);
            if (definition.AutoStop is { } expectation)
            {
                RunAutoStop(result, scenario, data, fileFormat, expectation);
                return result;
            }

            var run = RunPipeline(data, fileFormat, definition.UseVad, definition.ExpectedText);
            result.Pipeline = run.Measurement;
            _decodeWallMs[definition.Name] = run.Measurement.DecodeWallMs ?? 0;
            ModelId ??= run.Decoded?.ModelId;

            if (definition.Name.StartsWith("A-clean-", StringComparison.Ordinal) && run.Decoded is { } reference)
            {
                _references[definition.Sources[0]] = Words.Tokenize(reference.Text);
            }

            AddDecodeChecks(result, definition, run);

            if (definition.ProbeWithoutVad)
            {
                var probe = Stopwatch.StartNew();
                var raw = engine.Transcription.Transcribe(run.Captured);
                probe.Stop();
                result.WithoutVad = new ProbeResult(
                    "the recogniser alone, VAD off", raw.Text.Length, raw.Text, Math.Round(probe.Elapsed.TotalMilliseconds, 1));
                result.Checks.Add(new CheckResult("probe: recogniser with VAD off", CheckStatus.Report,
                    raw.IsEmpty ? "no text" : $"{raw.Text.Length} characters: \"{raw.Text}\""));
            }

            if (definition.Long is { } kind && run.Decoded is { } decodedLong)
            {
                AnalyzeLong(result, scenario, run, decodedLong, kind);
            }

            if (options.Includes('H') && run.Decoded is { } decoded && run.Post is { } post)
            {
                var baseClip = definition.Sources.Count == 1 && library.Contains(definition.Sources[0]) ? definition.Sources[0] : null;
                result.Checks.AddRange(PostProcessingChecks.ForDecode(baseClip, decoded.Text, post));
            }

            if (options.Includes('J') && run.Measurement.WouldStoreHistory && run.Decoded is { } stored)
            {
                result.History = StoreHistory(result, run.DecodeInput, stored, run.Post!.Text);
            }
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            result.Checks.Add(new CheckResult("scenario ran", CheckStatus.Fail, result.Error));
        }

        return result;
    }

    /// <summary>Capture conversion, VAD, decode and post-processing, with the controller's branch decisions.</summary>
    public PipelineRun RunPipeline(byte[] data, WaveFormat fileFormat, bool useVad, string? expectedText)
    {
        var hooks = engine.Hooks;
        var format = hooks.NormalizeFormat?.Invoke(fileFormat) ?? fileFormat;
        var measurement = new PipelineMeasurement { VadEnabled = useVad };
        var window = engine.Meter.Begin();
        try
        {
            // The whole-capture peak is the running maximum of the per-buffer meter, so metering the
            // whole buffer at once gives the value the capture service would hold at stop.
            var peak = hooks.ComputePeak?.Invoke(data, format) ?? 1f;
            measurement.CapturePeakDbfs = Math.Round(AudioTransforms.ToDbfs(peak), 1);
            measurement.CaptureSilent = hooks.SilentCapturePeak is { } silent && peak < silent;
            measurement.Signal = CaptureSignalAnalyzer.Analyze(data, format).Describe();

            var conversion = Stopwatch.StartNew();
            var samples = hooks.ResampleToTarget!(data, data.Length, format);
            conversion.Stop();
            measurement.ConversionMs = Math.Round(conversion.Elapsed.TotalMilliseconds, 2);
            var captured = new CapturedAudio(samples);
            if (captured.IsEmpty)
            {
                measurement.Outcome = PipelineOutcome.EmptyCapture;
                return new PipelineRun(measurement, captured, captured, null, null);
            }

            var decodeInput = captured;
            if (useVad)
            {
                var vadTimer = Stopwatch.StartNew();
                var trimmed = engine.Vad.Trim(captured);
                vadTimer.Stop();
                measurement.VadMs = Math.Round(vadTimer.Elapsed.TotalMilliseconds, 2);
                measurement.SpeechSeconds = engine.Vad.LastSpeechSeconds is { } speech ? Math.Round(speech, 3) : null;
                measurement.TrimmedSeconds = Math.Round(trimmed.Duration.TotalSeconds, 3);
                if (trimmed.IsEmpty)
                {
                    measurement.Outcome = measurement.CaptureSilent ? PipelineOutcome.SilentCapture : PipelineOutcome.VadNoSpeech;
                    return new PipelineRun(measurement, captured, trimmed, null, null);
                }

                decodeInput = trimmed;
            }

            measurement.Chunks = hooks.PlanChunks?.Invoke(decodeInput.Samples, decodeInput.SampleRate).Count;
            var wall = Stopwatch.StartNew();
            var decoded = engine.Transcription.Transcribe(decodeInput);
            wall.Stop();
            measurement.DecodeMs = Math.Round(decoded.DecodeDuration.TotalMilliseconds, 1);
            measurement.DecodeWallMs = Math.Round(wall.Elapsed.TotalMilliseconds, 1);
            measurement.RealTimeFactor = Math.Round(decoded.RealTimeFactor, 4);
            measurement.Characters = decoded.Text.Length;
            measurement.Text = decoded.Text;
            measurement.WordOverlap = expectedText is null ? null : Math.Round(Program.WordOverlap(expectedText, decoded.Text), 4);
            if (decoded.IsEmpty)
            {
                measurement.Outcome = measurement.CaptureSilent ? PipelineOutcome.SilentCapture : PipelineOutcome.NoText;
                return new PipelineRun(measurement, captured, decodeInput, decoded, null);
            }

            measurement.SuspiciouslyTerse = TerseDecodeDetector.IsSuspiciouslyTerse(
                decoded.Text, measurement.SpeechSeconds ?? decodeInput.Duration.TotalSeconds);

            var postTimer = Stopwatch.StartNew();
            var post = engine.PostProcessor.ProcessDetailed(decoded.Text, decoded.Text);
            postTimer.Stop();
            measurement.PostProcessMs = Math.Round(postTimer.Elapsed.TotalMilliseconds, 3);
            measurement.FinalText = post.Text;
            measurement.Replacements = PostProcessingChecks.Records(post);
            if (string.IsNullOrWhiteSpace(post.Text))
            {
                measurement.Outcome = PipelineOutcome.EmptyAfterPostProcess;
                return new PipelineRun(measurement, captured, decodeInput, decoded, post);
            }

            measurement.Outcome = PipelineOutcome.Inserted;
            measurement.WouldInsert = true;
            measurement.WouldStoreHistory = true;
            return new PipelineRun(measurement, captured, decodeInput, decoded, post);
        }
        finally
        {
            measurement.Resources = engine.Meter.End(window);
        }
    }

    /// <summary>The isolated clean decode of a base phrase, which long-dictation alignment expects.</summary>
    public IReadOnlyList<string> ReferenceTokens(string clip)
    {
        if (_references.TryGetValue(clip, out var cached))
        {
            return cached;
        }

        var audio = new CapturedAudio(library[clip].Samples);
        var trimmed = engine.Vad.Trim(audio);
        var decoded = engine.Transcription.Transcribe(trimmed.IsEmpty ? audio : trimmed);
        var tokens = decoded.IsEmpty ? Words.Tokenize(library[clip].Text) : Words.Tokenize(decoded.Text);
        _references[clip] = tokens;
        return tokens;
    }

    /// <summary>Closes the history database and records how large it ended up.</summary>
    public void FinishHistory(string databasePath)
    {
        History.MeanAddMs = _addMs.Count == 0 ? 0 : Math.Round(_addMs.Average(), 2);
        History.MaxAddMs = _addMs.Count == 0 ? 0 : Math.Round(_addMs.Max(), 2);
        History.BytesPerSecondOfAudio = History.StoredAudioSeconds <= 0
            ? 0
            : Math.Round(History.StoredAudioBytes / History.StoredAudioSeconds, 1);
        History.StoredAudioSeconds = Math.Round(History.StoredAudioSeconds, 2);
        History.Encoding = _encodingName;
        History.DatabaseBytesBeforeCheckpoint = FileLength(databasePath);
        History.WalBytesBeforeCheckpoint = FileLength(databasePath + "-wal");
    }

    private void AddDecodeChecks(ScenarioResult result, ScenarioDefinition definition, PipelineRun run)
    {
        var measurement = run.Measurement;
        if (definition.Expectation == Expectation.NothingToInsert)
        {
            var clean = !measurement.WouldInsert && !measurement.WouldStoreHistory;
            result.Checks.Add(new CheckResult(
                "silence: nothing to insert, nothing stored",
                clean ? CheckStatus.Pass : CheckStatus.Fail,
                clean
                    ? $"ended as {measurement.Outcome}"
                    : $"ended as {measurement.Outcome} with \"{measurement.FinalText}\""));
            return;
        }

        if (definition.Parts is { } parts)
        {
            result.Parts = [];
            foreach (var part in parts)
            {
                var overlap = Program.WordOverlap(part.Text, run.Decoded?.Text ?? string.Empty);
                result.Parts.Add(new PartResult(part.Name, part.Text, Math.Round(overlap, 4), part.MinOverlap));
                result.Checks.Add(new CheckResult(
                    $"structure: {part.Name} survives VAD",
                    overlap >= part.MinOverlap ? CheckStatus.Pass : CheckStatus.Fail,
                    $"overlap {overlap:P0} (needs {part.MinOverlap:P0}); trimmed to {measurement.TrimmedSeconds?.ToString() ?? "-"} s"));
            }

            return;
        }

        if (definition.ExpectedText is null)
        {
            result.Checks.Add(new CheckResult("decode", CheckStatus.Report,
                measurement.Outcome == PipelineOutcome.Inserted
                    ? $"{measurement.Characters} characters: \"{measurement.Text}\""
                    : $"ended as {measurement.Outcome}"));
            return;
        }

        var achieved = measurement.WordOverlap ?? 0;
        if (definition.MinOverlap is { } minimum)
        {
            result.Checks.Add(new CheckResult(
                "decode: word overlap",
                achieved >= minimum ? CheckStatus.Pass : CheckStatus.Fail,
                $"overlap {achieved:P0} (needs {minimum:P0}), outcome {measurement.Outcome}"));
        }
        else
        {
            result.Checks.Add(new CheckResult("decode: word overlap", CheckStatus.Report,
                $"overlap {achieved:P0}, outcome {measurement.Outcome}"));
        }
    }

    private void AnalyzeLong(ScenarioResult result, MaterializedScenario scenario, PipelineRun run, TranscriptionResult decoded, LongKind kind)
    {
        var hooks = engine.Hooks;
        if (hooks.PlanChunks is not { } plan || hooks.MaxChunkSeconds is not { } maxSeconds)
        {
            result.Checks.Add(new CheckResult("long dictation", CheckStatus.NotSupported,
                "this build exposes no TranscriptionChunker.Plan to compare seams against"));
            return;
        }

        var input = run.DecodeInput.Samples;
        var chunks = plan(input, run.DecodeInput.SampleRate);
        var maxSamples = maxSeconds * run.DecodeInput.SampleRate;

        // The chunker's own contract: contiguous spans covering the capture, none over the limit, and
        // a capture at or under the limit decoded whole.
        var contiguous = chunks.Count > 0 && chunks[0].Start == 0
            && chunks.Zip(chunks.Skip(1)).All(pair => pair.First.Start + pair.First.Length == pair.Second.Start)
            && chunks[^1].Start + chunks[^1].Length == input.Length;
        var bounded = chunks.All(c => c.Length <= maxSamples);
        var wholeWhenShort = input.Length > maxSamples || chunks.Count == 1;
        result.Checks.Add(new CheckResult(
            "long dictation: chunk plan",
            contiguous && bounded && wholeWhenShort ? CheckStatus.Pass : CheckStatus.Fail,
            $"{input.Length / (double)run.DecodeInput.SampleRate:0.00} s decoded as {chunks.Count} chunk(s) of at most {maxSeconds} s"));

        var analysis = LongDictationAnalyzer.Analyze(
            scenario.Placements,
            run.Captured.Samples,
            input,
            decoded.Text,
            ReferenceTokens,
            chunks,
            window => engine.Transcription.Transcribe(new CapturedAudio(window, run.DecodeInput.SampleRate)).Text,
            run.DecodeInput.SampleRate,
            assertSeams: kind == LongKind.Natural);
        result.Long = analysis;

        foreach (var seam in analysis.Seams)
        {
            result.Checks.Add(new CheckResult(
                $"long dictation: seam {seam.Index} at {seam.AtSeconds:0.00}s",
                seam.Verdict,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0} ({1:0.0} dBFS); against a seamless decode of {2:0.00} s to {3:0.00} s: {4} word(s) lost, {5} doubled{6}",
                    seam.Location, seam.LevelDbfs, seam.WindowStartSeconds, seam.WindowEndSeconds, seam.LostAtSeam, seam.DoubledAtSeam,
                    seam.MergedOrSplitAtSeam > 0 ? $", {seam.MergedOrSplitAtSeam} merged or split" : string.Empty)));
        }

        var coverage = analysis.CoverageVsReference;
        var insertedShare = analysis.ReferenceTokens == 0 ? 0 : analysis.InsertedTokens / (double)analysis.ReferenceTokens;
        var healthy = coverage >= LongDictationCoverageFloor && insertedShare <= LongDictationInsertionCeiling;
        result.Checks.Add(new CheckResult(
            "long dictation: coverage of the phrases' own decodes",
            kind == LongKind.Natural ? healthy ? CheckStatus.Pass : CheckStatus.Fail : CheckStatus.Report,
            $"{coverage:P1} of {analysis.ReferenceTokens} words kept (needs {LongDictationCoverageFloor:P0}), {analysis.DroppedTokens} dropped, " +
            $"{analysis.InsertedTokens} inserted ({insertedShare:P1}, at most {LongDictationInsertionCeiling:P0}), {analysis.DuplicatedTokens} doubled; " +
            $"{analysis.OrderedCoverageVsSource:P1} of the script in order"));
    }

    private void RunAutoStop(ScenarioResult result, MaterializedScenario scenario, byte[] data, WaveFormat fileFormat, AutoStopExpectation expectation)
    {
        var hooks = engine.Hooks;
        if (hooks.ComputePeak is null)
        {
            result.Checks.Add(new CheckResult("auto-stop", CheckStatus.NotSupported, "AudioCaptureService.ComputePeak is not available"));
            return;
        }

        var format = hooks.NormalizeFormat?.Invoke(fileFormat) ?? fileFormat;
        var measurement = SilenceAutoStop.Run(data, format, scenario.Placements, scenario.PlacementRate, hooks, out var stopByte);
        result.AutoStop = measurement;
        result.Checks.AddRange(SilenceAutoStop.Check(expectation, measurement, hooks.SilenceDefaults));

        var definition = scenario.Definition;
        if (definition.MinOverlap is not { } minimum || !measurement.Stopped)
        {
            return;
        }

        // What the dictation would actually have captured: everything up to the buffer that stopped it.
        var run = RunPipeline(data[..stopByte], fileFormat, useVad: true, definition.ExpectedText);
        result.Pipeline = run.Measurement;
        measurement.CapturedText = run.Measurement.Text;
        measurement.CapturedOverlap = run.Measurement.WordOverlap;
        var achieved = run.Measurement.WordOverlap ?? 0;
        result.Checks.Add(new CheckResult(
            "auto-stop: the captured speech decodes",
            achieved >= minimum ? CheckStatus.Pass : CheckStatus.Fail,
            $"overlap {achieved:P0} (needs {minimum:P0}) over {stopByte / (double)format.AverageBytesPerSecond:0.00} s captured"));

        if (options.Includes('J') && run.Measurement.WouldStoreHistory && run.Decoded is { } decoded)
        {
            result.History = StoreHistory(result, run.DecodeInput, decoded, run.Post!.Text);
        }
    }

    /// <summary>
    /// Stores the capture the way the controller does after an insertion (VAD-trimmed audio beside
    /// the history row), reads it back, and decodes it again: a lossy or broken storage encoding
    /// shows up as a re-decode that no longer matches.
    /// </summary>
    private HistoryMeasurement StoreHistory(ScenarioResult result, CapturedAudio audio, TranscriptionResult decoded, string finalText)
    {
        var measurement = new HistoryMeasurement();
        var entry = new HistoryEntry(
            Id: 0,
            TimestampUtc: DateTimeOffset.UtcNow,
            Text: finalText,
            AudioMilliseconds: (int)decoded.AudioDuration.TotalMilliseconds,
            DecodeMilliseconds: (int)decoded.DecodeDuration.TotalMilliseconds,
            CleanupMilliseconds: null,
            TargetApp: "Scribe.AsrCheck",
            TranscriptionModelId: decoded.ModelId);

        var add = Stopwatch.StartNew();
        var saved = engine.History.Add(entry, audio);
        add.Stop();
        measurement.AddMs = Math.Round(add.Elapsed.TotalMilliseconds, 2);
        _addMs.Add(measurement.AddMs);

        if (saved.AudioBlobId is not { } blobId)
        {
            result.Checks.Add(new CheckResult("history: audio stored", CheckStatus.Report, "the build saved the entry without its audio"));
            return measurement;
        }

        measurement.BlobId = blobId;
        (measurement.StoredBytes, measurement.Encoding) = BlobShape(blobId);
        measurement.StoredBytesPerSecond = Math.Round(measurement.StoredBytes / Math.Max(audio.Duration.TotalSeconds, 1e-6), 1);
        _encodingName ??= measurement.Encoding;
        History.Stored++;
        History.StoredAudioSeconds += audio.Duration.TotalSeconds;
        History.StoredAudioBytes += measurement.StoredBytes;

        var read = Stopwatch.StartNew();
        var back = engine.History.GetAudio(blobId);
        read.Stop();
        measurement.ReadMs = Math.Round(read.Elapsed.TotalMilliseconds, 2);
        if (back is null || back.SampleRate != audio.SampleRate)
        {
            result.Checks.Add(new CheckResult("history: stored audio reads back", CheckStatus.Fail,
                back is null ? "GetAudio returned nothing" : $"read back at {back.SampleRate} Hz, stored at {audio.SampleRate} Hz"));
            return measurement;
        }

        measurement.SamplesMatchCount = back.Samples.Length == audio.Samples.Length;
        double signal = 0, error = 0, maxError = 0;
        for (var i = 0; i < Math.Min(back.Samples.Length, audio.Samples.Length); i++)
        {
            var difference = Math.Abs(back.Samples[i] - (double)audio.Samples[i]);
            maxError = Math.Max(maxError, difference);
            signal += audio.Samples[i] * (double)audio.Samples[i];
            error += difference * difference;
        }

        measurement.MaxAbsError = maxError;
        measurement.StorageSnrDb = error <= 0 ? null : Math.Round(10 * Math.Log10(signal / error), 1);
        result.Checks.Add(new CheckResult("history: stored audio reads back",
            measurement.SamplesMatchCount ? CheckStatus.Pass : CheckStatus.Fail,
            $"{back.Samples.Length} of {audio.Samples.Length} samples, max error {maxError:0.######}, " +
            $"{(measurement.StorageSnrDb is { } snr ? $"{snr} dB SNR" : "lossless")}"));

        if (_redecodeBudget <= 0)
        {
            return measurement;
        }

        _redecodeBudget--;
        var redecode = Stopwatch.StartNew();
        var again = engine.Transcription.Transcribe(back);
        redecode.Stop();
        measurement.Redecoded = true;
        measurement.RedecodeMs = Math.Round(redecode.Elapsed.TotalMilliseconds, 1);
        measurement.RedecodeIdentical = string.Equals(again.Text, decoded.Text, StringComparison.Ordinal);
        measurement.RedecodeOverlap = Math.Round(Program.WordOverlap(decoded.Text, again.Text), 4);
        History.Redecoded++;
        if (measurement.RedecodeIdentical == true)
        {
            History.RedecodeIdentical++;
        }

        // Identical is the expectation; 0.9 tolerates a quantized encoding flipping one token on
        // badly degraded audio while still failing an encoding that damages the signal.
        result.Checks.Add(new CheckResult("history: re-transcription of stored audio",
            measurement.RedecodeIdentical == true || measurement.RedecodeOverlap >= 0.9 ? CheckStatus.Pass : CheckStatus.Fail,
            measurement.RedecodeIdentical == true
                ? "identical to the original decode"
                : $"differs: \"{again.Text}\" (overlap {measurement.RedecodeOverlap:P0})"));
        return measurement;
    }

    private (long Bytes, string Encoding) BlobShape(long blobId)
    {
        using var connection = engine.Database.Open();
        using var size = connection.CreateCommand();
        size.CommandText = "SELECT length(samples) FROM audio_blobs WHERE id = $id;";
        size.Parameters.AddWithValue("$id", blobId);
        var bytes = Convert.ToInt64(size.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

        if (!HasColumn(connection, "audio_blobs", "encoding"))
        {
            return (bytes, "float32 (no encoding column)");
        }

        using var encoding = connection.CreateCommand();
        encoding.CommandText = "SELECT encoding FROM audio_blobs WHERE id = $id;";
        encoding.Parameters.AddWithValue("$id", blobId);
        var code = Convert.ToInt32(encoding.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);

        // Named through the build's own enum when it has one, so the report never guesses a mapping.
        var type = typeof(HistoryRepository).Assembly.GetType("Scribe.Core.Persistence.AudioBlobEncoding");
        var name = type is { IsEnum: true } ? Enum.GetName(type, Enum.ToObject(type, code)) : null;
        return (bytes, $"{name ?? "unknown"} (encoding {code})");
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<SourceInfo> Sources(ScenarioDefinition definition) =>
        definition.Sources
            .Select(name => library.Contains(name)
                ? new SourceInfo(name, library[name].Voice, library[name].Text)
                : new SourceInfo(name, "model sample", null))
            .ToList();

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
