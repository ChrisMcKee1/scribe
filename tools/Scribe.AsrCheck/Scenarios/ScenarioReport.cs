using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;

namespace Scribe.AsrCheck.Scenarios;

internal enum CheckStatus
{
    /// <summary>An asserted expectation held.</summary>
    Pass,

    /// <summary>An asserted expectation did not hold: a robust regression, and a non-zero exit.</summary>
    Fail,

    /// <summary>Measured and recorded, deliberately not asserted.</summary>
    Report,

    /// <summary>The build under test lacks what the check needs.</summary>
    NotSupported,
}

internal sealed record CheckResult(string Name, CheckStatus Status, string Detail);

/// <summary>Where the dictation pipeline would have ended for this capture.</summary>
internal enum PipelineOutcome
{
    /// <summary>Text reaches the target app, and history records it.</summary>
    Inserted,

    /// <summary>The capture produced no samples.</summary>
    EmptyCapture,

    /// <summary>Whole-capture peak under the digital-silence bar: the muted-microphone error.</summary>
    SilentCapture,

    /// <summary>VAD found no speech; the capture is discarded quietly.</summary>
    VadNoSpeech,

    /// <summary>The recogniser returned nothing for a capture that was not silent: a lost dictation.</summary>
    NoText,

    /// <summary>Post-processing left nothing to insert.</summary>
    EmptyAfterPostProcess,
}

internal sealed record SourceInfo(string Name, string Voice, string? Text);

internal sealed record PhaseTiming(string Phase, double Seconds);

internal sealed record FormatInfo(string Label, string Encoding, int SampleRate, int Channels, int BitsPerSample, bool ExtensibleHeader)
{
    public static FormatInfo From(string label, WaveFormat format) => new(
        label,
        format.Encoding.ToString(),
        format.SampleRate,
        format.Channels,
        format.BitsPerSample,
        format is WaveFormatExtensible);
}

internal sealed record ReplacementInfo(int Start, int Length, string Pattern, string Replacement, string Kind);

internal sealed class PipelineMeasurement
{
    public double ConversionMs { get; set; }

    public double CapturePeakDbfs { get; set; }

    public bool CaptureSilent { get; set; }

    public string? Signal { get; set; }

    public bool VadEnabled { get; set; }

    public double? VadMs { get; set; }

    public double? TrimmedSeconds { get; set; }

    public double? SpeechSeconds { get; set; }

    public double? DecodeMs { get; set; }

    public double? DecodeWallMs { get; set; }

    public double? RealTimeFactor { get; set; }

    public int? Chunks { get; set; }

    public int Characters { get; set; }

    public string? Text { get; set; }

    public double? WordOverlap { get; set; }

    public bool? SuspiciouslyTerse { get; set; }

    public double? PostProcessMs { get; set; }

    public string? FinalText { get; set; }

    public List<ReplacementInfo>? Replacements { get; set; }

    public PipelineOutcome Outcome { get; set; }

    public bool WouldInsert { get; set; }

    public bool WouldStoreHistory { get; set; }

    public ResourceUsage? Resources { get; set; }
}

internal sealed record ProbeResult(string Description, int Characters, string Text, double DecodeMs);

internal sealed record PartResult(string Name, string ExpectedText, double WordOverlap, double MinOverlap);

internal sealed class HistoryMeasurement
{
    public long BlobId { get; set; }

    public double AddMs { get; set; }

    public long StoredBytes { get; set; }

    public double StoredBytesPerSecond { get; set; }

    public string? Encoding { get; set; }

    public bool SamplesMatchCount { get; set; }

    public double MaxAbsError { get; set; }

    public double? StorageSnrDb { get; set; }

    public double ReadMs { get; set; }

    public bool? Redecoded { get; set; }

    public bool? RedecodeIdentical { get; set; }

    public double? RedecodeOverlap { get; set; }

    public double? RedecodeMs { get; set; }
}

internal sealed class ScenarioResult
{
    public required string Name { get; init; }

    public required string Category { get; init; }

    public required string Title { get; init; }

    public required bool Quick { get; init; }

    public required string File { get; init; }

    public required string Sha256 { get; init; }

    public required double Seconds { get; init; }

    public required FormatInfo DeviceFormat { get; init; }

    public required IReadOnlyList<SourceInfo> Sources { get; init; }

    public required string Transform { get; init; }

    public string? ExpectedText { get; init; }

    public required string ExpectedBehavior { get; init; }

    public double? MinOverlap { get; init; }

    public PipelineMeasurement? Pipeline { get; set; }

    public List<PartResult>? Parts { get; set; }

    public ProbeResult? WithoutVad { get; set; }

    public SeamAnalysis? Long { get; set; }

    public AutoStopMeasurement? AutoStop { get; set; }

    public HistoryMeasurement? History { get; set; }

    public string? Error { get; set; }

    public List<CheckResult> Checks { get; } = [];

    public CheckStatus Status => Checks.Any(c => c.Status == CheckStatus.Fail)
        ? CheckStatus.Fail
        : Checks.Any(c => c.Status == CheckStatus.Pass) ? CheckStatus.Pass : CheckStatus.Report;
}

internal sealed class EnvironmentInfo
{
    public required string OsDescription { get; init; }

    public required string OsArchitecture { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required string Framework { get; init; }

    public required int ProcessorCount { get; init; }

    public required string ComputeCapability { get; init; }

    public required string Process { get; init; }

    public required string HarnessConfiguration { get; init; }

    public required string CoreConfiguration { get; init; }

    public required string CoreVersion { get; init; }

    public string? SherpaOnnxVersion { get; set; }
}

internal sealed class EngineInfo
{
    public int ConfiguredThreads { get; set; }

    public int? EffectiveThreads { get; set; }

    public string Decoding { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public double ModelLoadMs { get; set; }

    public double VadLoadMs { get; set; }

    public int? MaxChunkSeconds { get; set; }

    public SilenceTrackerDefaults? SilenceAutoStopDefaults { get; set; }
}

internal sealed class HistoryReport
{
    public string Database { get; set; } = "temporary file database under the output folder (never a user database)";

    public int Stored { get; set; }

    public int Redecoded { get; set; }

    public int RedecodeIdentical { get; set; }

    public double StoredAudioSeconds { get; set; }

    public long StoredAudioBytes { get; set; }

    public double BytesPerSecondOfAudio { get; set; }

    public string? Encoding { get; set; }

    public long DatabaseBytesBeforeCheckpoint { get; set; }

    public long WalBytesBeforeCheckpoint { get; set; }

    public long DatabaseBytesAfterClose { get; set; }

    public double MeanAddMs { get; set; }

    public double MaxAddMs { get; set; }

    public bool Kept { get; set; }
}

internal sealed class SummaryInfo
{
    public int Scenarios { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public int ReportedOnly { get; set; }

    public int Checks { get; set; }

    public int FailedChecks { get; set; }

    public List<string> Regressions { get; set; } = [];

    public double DurationSeconds { get; set; }

    public int ExitCode { get; set; }
}

internal sealed class SuiteReport
{
    public string Schema { get; init; } = "scribe-asrcheck-scenarios/1";

    public required string Mode { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required EnvironmentInfo Environment { get; init; }

    public required IReadOnlyDictionary<string, bool> ProductionHooks { get; init; }

    public EngineInfo Engine { get; } = new();

    public string? OutputDirectory { get; set; }

    public List<ScenarioResult> Scenarios { get; } = [];

    public List<PostProcessingCase> PostProcessing { get; } = [];

    public LifecycleReport? Lifecycle { get; set; }

    public HistoryReport? History { get; set; }

    public List<string> Gaps { get; } = [];

    /// <summary>Wall time per phase, in run order, so a slow CI leg can be attributed.</summary>
    public List<PhaseTiming> Phases { get; } = [];

    public SummaryInfo Summary { get; } = new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Write(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>Console rendering. One line per scenario, with every failed check spelled out beneath it.</summary>
internal static class ReportPrinter
{
    public static void Scenario(ScenarioResult result)
    {
        var pipeline = result.Pipeline;
        var tag = result.Status switch
        {
            CheckStatus.Fail => "FAIL",
            CheckStatus.Pass => "PASS",
            _ => " -- ",
        };

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "[{0}] {1,-38} {2,6:0.0}s",
            tag,
            result.Name,
            result.Seconds);

        if (pipeline is not null)
        {
            line += string.Format(
                CultureInfo.InvariantCulture,
                " vad {0,6} dec {1,7} rtf {2,5} ovl {3,4} post {4,5} ch {5,4} {6}",
                pipeline.VadMs is { } vad ? $"{vad:0}ms" : "off",
                pipeline.DecodeMs is { } decode ? $"{decode:0}ms" : "-",
                pipeline.RealTimeFactor is { } rtf ? rtf.ToString("0.00", CultureInfo.InvariantCulture) : "-",
                pipeline.WordOverlap is { } overlap ? $"{overlap * 100:0}%" : "-",
                pipeline.PostProcessMs is { } post ? $"{post:0.0}ms" : "-",
                pipeline.Characters,
                pipeline.Outcome);
        }

        Console.WriteLine(line);
        if (result.Error is not null)
        {
            Console.WriteLine($"         error: {result.Error}");
        }

        foreach (var check in result.Checks.Where(c => c.Status is CheckStatus.Fail or CheckStatus.NotSupported))
        {
            Console.WriteLine($"         {check.Status}: {check.Name}: {Shorten(check.Detail)}");
        }

        foreach (var check in result.Checks.Where(c => c.Status == CheckStatus.Report && c.Detail.Length > 0))
        {
            Console.WriteLine($"         note: {check.Name}: {Shorten(check.Detail)}");
        }
    }

    /// <summary>Long transcripts belong in the JSON report; the console keeps each line readable.</summary>
    public static string Shorten(string detail) =>
        detail.Length <= MaxConsoleDetail ? detail : string.Concat(detail.AsSpan(0, MaxConsoleDetail), "... (full text in the JSON report)");

    private const int MaxConsoleDetail = 220;
}
