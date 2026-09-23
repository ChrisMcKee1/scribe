using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;

namespace Scribe.AsrCheck.Scenarios;

/// <summary>Command-line options for <c>--scenarios</c>.</summary>
internal sealed class RunOptions
{
    public const string Categories = "ABCDEFGHIJM";

    public bool Quick { get; private init; }

    public required string Fixtures { get; init; }

    public required string Output { get; init; }

    public required string Report { get; init; }

    public string? Models { get; private init; }

    public int Threads { get; private init; }

    public bool ExpectLifecycleFix { get; private init; }

    public bool KeepDatabase { get; private init; }

    public string Only { get; private init; } = Categories;

    public bool Includes(char category) => Only.Contains(category, StringComparison.Ordinal);

    public static RunOptions? Parse(string[] args, string? repoRoot, out string? error)
    {
        error = null;
        var root = repoRoot ?? Directory.GetCurrentDirectory();
        var output = Value(args, "--out") ?? Path.Combine(root, "artifacts", "scenarios");
        var threads = 0;
        if (Value(args, "--num-threads") is { } threadText
            && (!int.TryParse(threadText, NumberStyles.Integer, CultureInfo.InvariantCulture, out threads) || threads < 0))
        {
            error = $"--num-threads expects a non-negative integer, got '{threadText}'.";
            return null;
        }

        var only = Categories;
        if (Value(args, "--only") is { } onlyText)
        {
            only = new string(onlyText.ToUpperInvariant().Where(char.IsLetter).Distinct().ToArray());
            var unknown = only.Where(c => !Categories.Contains(c, StringComparison.Ordinal)).ToList();
            if (only.Length == 0 || unknown.Count > 0)
            {
                error = $"--only takes category letters from {Categories}; '{onlyText}' is not valid.";
                return null;
            }
        }

        return new RunOptions
        {
            Quick = args.Contains("--quick"),
            Fixtures = Path.GetFullPath(Value(args, "--fixtures") ?? Path.Combine(root, "tests", "fixtures", "speech")),
            Output = Path.GetFullPath(output),
            Report = Path.GetFullPath(Value(args, "--report") ?? Path.Combine(output, "report.json")),
            Models = Value(args, "--models") is { } models ? Path.GetFullPath(models) : null,
            Threads = threads,
            ExpectLifecycleFix = args.Contains("--expect-lifecycle-fix"),
            KeepDatabase = args.Contains("--keep-db"),
            Only = only,
        };
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>
/// <c>dotnet run --project tools/Scribe.AsrCheck -- --scenarios [--quick]</c>: materializes every
/// scenario as a WAV file, plays each one through the production Core pipeline, and reports
/// accuracy, behaviour and cost. Exits 1 only on a robust regression (a failed asserted check);
/// everything else is reported. Exits 2 when the suite cannot run at all.
/// <para>
/// Nothing touches a microphone, speaker, clipboard, keyboard or user data: audio comes from the
/// committed fixtures, the history database is a temporary file under the output folder, the
/// data-root override keeps every path away from the user's own ScribeData, and no cleanup
/// provider is called.
/// </para>
/// </summary>
internal static class ScenarioSuite
{
    private static readonly string Usage = string.Join(Environment.NewLine,
        "Usage: Scribe.AsrCheck --scenarios [--quick] [--out <dir>] [--report <file.json>] [--fixtures <dir>]",
        "                       [--models <dir>] [--num-threads <n>] [--only <letters>] [--expect-lifecycle-fix] [--keep-db]",
        "  --quick                 CI subset (no 60 s and longer dictation, shorter lifecycle windows)",
        "  --out                   where scenario WAVs, manifest.json and the temporary database go",
        "                          (default: <repo>/artifacts/scenarios, which is gitignored)",
        "  --report                JSON report path (default: <out>/report.json)",
        "  --models                speech model directory (sets SCRIBE_MODELS_DIR for this process only)",
        "  --num-threads           recogniser threads; 0 is the production default (auto)",
        "  --only                  categories to run, from A B C D E F G H I J M",
        "  --expect-lifecycle-fix  fail on lifecycle races even when the build does not look fixed",
        "  --keep-db               keep the temporary history database after the run");

    public static int Run(string[] args)
    {
        var clock = Stopwatch.StartNew();
        Console.OutputEncoding = Encoding.UTF8;

        var options = RunOptions.Parse(args, FindRepoRoot(), out var error);
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.Models is not null)
        {
            Environment.SetEnvironmentVariable("SCRIBE_MODELS_DIR", options.Models);
        }

        var hooks = ProductionHooks.Bind();
        var report = new SuiteReport
        {
            Mode = options.Quick ? "quick" : "full",
            StartedUtc = DateTimeOffset.UtcNow,
            Environment = DescribeEnvironment(),
            ProductionHooks = hooks.Availability(),
            OutputDirectory = options.Output,
        };

        PrintHeader(report, options);
        if (hooks.ResampleToTarget is null)
        {
            Console.Error.WriteLine(
                "The production capture conversion (AudioCaptureService.ResampleToTarget(byte[], int, WaveFormat)) " +
                "was not found in this build. Every scenario depends on it, so the suite cannot run.");
            return 2;
        }

        var wavDirectory = Path.Combine(options.Output, "wav");
        var databaseRoot = Path.Combine(options.Output, "history-db");
        try
        {
            ResetOwnedDirectory(wavDirectory, file => file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            ResetOwnedDirectory(databaseRoot, file => Path.GetFileName(file).StartsWith(AppPaths.DatabaseFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        BaseLibrary library;
        try
        {
            library = BaseLibrary.Load(options.Fixtures);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Base fixtures could not be loaded: {ex.Message}");
            return 2;
        }

        // An explicit data root: the database, the model fallback and every other AppPaths location
        // resolve under the output folder, and the legacy-folder migration is off by construction.
        var paths = new AppPaths(databaseRoot);
        var locator = new ModelLocator(paths);
        var modelSamples = Path.Combine(locator.Resolve().Directory, "test_wavs");

        var catalog = ScenarioCatalog.Build(library, modelSamples)
            .Where(s => options.Includes(s.Category) && (!options.Quick || s.Quick))
            .ToList();
        var phaseClock = Stopwatch.StartNew();
        void Phase(string name)
        {
            report.Phases.Add(new PhaseTiming(name, Math.Round(phaseClock.Elapsed.TotalSeconds, 2)));
            phaseClock.Restart();
        }

        var scenarios = Materialize(catalog, library, options, wavDirectory);
        Phase("materialize scenario WAVs");
        Console.WriteLine($"Scenario files: {scenarios.Count} WAVs in {wavDirectory} (manifest.json beside them)");
        Console.WriteLine();

        using var transcription = new TranscriptionService(
            locator,
            Options.Create(new TranscriptionOptions { NumThreads = options.Threads, DecodingMethod = TranscriptionDecoding.Greedy }),
            NullLogger<TranscriptionService>.Instance);
        using var vad = new VadService(locator, NullLogger<VadService>.Instance);

        var load = Stopwatch.StartNew();
        transcription.Initialize();
        load.Stop();
        var vadLoad = Stopwatch.StartNew();
        vad.Initialize();
        vadLoad.Stop();

        report.Engine.ConfiguredThreads = options.Threads;
        report.Engine.EffectiveThreads = hooks.ResolveThreadCount?.Invoke(options.Threads);
        report.Engine.Decoding = TranscriptionDecoding.Greedy;
        report.Engine.ModelLoadMs = Math.Round(load.Elapsed.TotalMilliseconds, 1);
        report.Engine.VadLoadMs = Math.Round(vadLoad.Elapsed.TotalMilliseconds, 1);
        report.Engine.MaxChunkSeconds = hooks.MaxChunkSeconds;
        report.Engine.SilenceAutoStopDefaults = hooks.SilenceDefaults;
        report.Environment.SherpaOnnxVersion = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name?.Equals("sherpa-onnx", StringComparison.OrdinalIgnoreCase) == true)
            ?.GetName().Version?.ToString();
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Engine: recogniser loaded in {0:0} ms ({1} threads, configured {2}), VAD in {3:0} ms, VAD available={4}",
            report.Engine.ModelLoadMs,
            report.Engine.EffectiveThreads?.ToString(CultureInfo.InvariantCulture) ?? "?",
            options.Threads == 0 ? "auto" : options.Threads.ToString(CultureInfo.InvariantCulture),
            report.Engine.VadLoadMs,
            vad.IsAvailable));
        Console.WriteLine();
        Phase("load recogniser and VAD");

        // Disposed explicitly below, before the closed file is measured; the using covers every
        // early exit, and ScribeDatabase.Dispose is idempotent.
        using var database = new ScribeDatabase(paths, NullLogger<ScribeDatabase>.Instance);
        database.Initialize();
        var dictionary = new DictionaryRepository(database);
        dictionary.SaveAll(TestVocabulary.Dictionary);
        var snippets = new SnippetRepository(database);
        snippets.SaveAll(TestVocabulary.Snippets);
        var postProcessor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets);

        using var meter = new ResourceMeter();
        var engine = new ScenarioEngine
        {
            Transcription = transcription,
            Vad = vad,
            Database = database,
            History = new HistoryRepository(database),
            PostProcessor = postProcessor,
            Hooks = hooks,
            Meter = meter,
        };
        var runner = new ScenarioRunner(engine, library, options);

        if (options.Includes('H'))
        {
            Console.WriteLine("H  Post-processing, recogniser-independent cases (production ProcessDetailed(text, text))");
            foreach (var result in PostProcessingChecks.RunSourceCases(postProcessor))
            {
                report.PostProcessing.Add(result);
                Console.WriteLine($"[{(result.Status == CheckStatus.Pass ? "PASS" : "FAIL")}] {result.Name}");
                if (result.Status != CheckStatus.Pass)
                {
                    Console.WriteLine($"         {result.Detail}: expected \"{Escape(result.ExpectedText)}\", got \"{Escape(result.ActualText)}\"");
                }
            }

            Console.WriteLine();
        }

        Phase("test database and recogniser-independent post-processing (H)");
        char? heading = null;
        foreach (var scenario in scenarios)
        {
            if (heading != scenario.Definition.Category)
            {
                if (heading is { } finished)
                {
                    Phase($"scenarios {finished}");
                }

                heading = scenario.Definition.Category;
                Console.WriteLine();
                Console.WriteLine($"{heading}  {CategoryTitle(heading.Value)}");
            }

            var result = runner.Run(scenario);
            report.Scenarios.Add(result);
            ReportPrinter.Scenario(result);
            PrintDetail(result);
        }

        if (heading is { } last)
        {
            Phase($"scenarios {last}");
        }

        report.Engine.ModelId = runner.ModelId;
        Console.WriteLine();

        if (options.Includes('I'))
        {
            report.Lifecycle = RunLifecycle(engine, runner, library, options);
            Phase("lifecycle (I)");
        }

        if (options.Includes('J'))
        {
            runner.FinishHistory(paths.DatabasePath);
        }

        database.Dispose();
        if (options.Includes('J'))
        {
            report.History = runner.History;
            report.History.DatabaseBytesAfterClose = File.Exists(paths.DatabasePath) ? new FileInfo(paths.DatabasePath).Length : 0;
            report.History.Kept = options.KeepDatabase;
            PrintHistory(report.History);
        }

        if (!options.KeepDatabase)
        {
            TryDelete(databaseRoot);
        }

        Phase("close the history database (J)");
        AddGaps(report, library, modelSamples);
        Summarize(report, clock.Elapsed);
        report.Write(options.Report);
        PrintSummary(report, options);
        return report.Summary.ExitCode;
    }

    private static List<MaterializedScenario> Materialize(
        List<ScenarioDefinition> catalog, BaseLibrary library, RunOptions options, string wavDirectory)
    {
        var scenarios = new List<MaterializedScenario>(catalog.Count);
        var manifest = new List<object>(catalog.Count);
        foreach (var definition in catalog)
        {
            var audio = definition.Build();
            var path = Path.Combine(wavDirectory, definition.Name + ".wav");
            DeviceAudio.WriteWav(path, audio.Data, audio.Format);
            var scenario = new MaterializedScenario
            {
                Definition = definition,
                Path = path,
                RelativePath = Path.GetRelativePath(options.Output, path),
                Sha256 = DeviceAudio.Sha256(path),
                Seconds = audio.Data.Length / (double)audio.Format.AverageBytesPerSecond,
                Format = audio.Format,
                Placements = audio.Placements,
                PlacementRate = audio.PlacementRate,
            };
            scenarios.Add(scenario);
            manifest.Add(new
            {
                name = definition.Name,
                category = definition.Category.ToString(),
                title = definition.Title,
                quick = definition.Quick,
                file = scenario.RelativePath,
                sha256 = scenario.Sha256,
                seconds = Math.Round(scenario.Seconds, 3),
                deviceFormat = FormatInfo.From(definition.Device.Label, audio.Format),
                sources = definition.Sources.Select(name => library.Contains(name)
                    ? new { name, voice = library[name].Voice, text = library[name].Text }
                    : new { name, voice = "model sample", text = (string)null! }),
                transform = definition.Transform,
                expectedText = definition.ExpectedText,
                expectedBehavior = definition.ExpectedBehavior,
                thresholds = new
                {
                    minWordOverlap = definition.MinOverlap,
                    parts = definition.Parts?.Select(p => new { p.Name, p.MinOverlap }),
                    nothingToInsert = definition.Expectation == Expectation.NothingToInsert,
                    autoStop = definition.AutoStop?.ToString(),
                    longDictation = definition.Long?.ToString(),
                    vad = definition.UseVad,
                },
                phrases = audio.Placements.Select(p => new
                {
                    clip = p.Clip,
                    startSeconds = Math.Round(p.Start / (double)audio.PlacementRate, 3),
                    endSeconds = Math.Round(p.End / (double)audio.PlacementRate, 3),
                }),
            });
        }

        File.WriteAllText(
            Path.Combine(options.Output, "manifest.json"),
            JsonSerializer.Serialize(new { schema = "scribe-asrcheck-scenario-manifest/1", scenarios = manifest }, SuiteReport.JsonOptions));
        return scenarios;
    }

    private static LifecycleReport RunLifecycle(ScenarioEngine engine, ScenarioRunner runner, BaseLibrary library, RunOptions options)
    {
        Console.WriteLine("I  Lifecycle and concurrency (speech engine)");
        var hooks = engine.Hooks;
        var report = new LifecycleReport
        {
            FixExpected = options.ExpectLifecycleFix || hooks.TranscribeWithCancellation is not null,
            FixExpectedReason = options.ExpectLifecycleFix
                ? "--expect-lifecycle-fix was passed"
                : hooks.TranscribeWithCancellation is not null
                    ? "the build has the cancellable Transcribe overload that ships with the engine lifecycle fixes"
                    : "the build predates the engine lifecycle fixes; races are reported, not asserted",
        };

        var lifecycle = new LifecycleScenarios(engine.Transcription, engine.Vad, hooks, name => Padded(library[name].Samples, name));

        // Everything that wants loaded engines runs first; the unload race runs last because it
        // leaves both engines unloaded, and reloading after it would only pay the load twice.
        report.WarmSteady = lifecycle.WarmSteady("snippet-signature", 20, audio =>
        {
            var trimmed = engine.Vad.Trim(audio);
            var decoded = engine.Transcription.Transcribe(trimmed.IsEmpty ? audio : trimmed);
            engine.PostProcessor.ProcessDetailed(decoded.Text, decoded.Text);
            return decoded;
        });
        var warm = report.WarmSteady;
        report.Checks.Add(new CheckResult("lifecycle: 20 rapid short dictations", CheckStatus.Report, string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.00} s clip: min {1:0} / median {2:0} / p95 {3:0} / max {4:0} ms end to end, mean decode {5:0} ms (RTF {6:0.000}); " +
            "private {7:0} MB before, {8:0} MB after",
            warm.ClipSeconds, warm.MinMs, warm.MedianMs, warm.P95Ms, warm.MaxMs, warm.MeanDecodeMs, warm.MeanRealTimeFactor,
            warm.PrivateMbBefore, warm.PrivateMbAfter)));

        report.ColdReload = lifecycle.ColdReload("greeting", options.Quick ? 1 : 3);
        var cold = report.ColdReload;
        report.Checks.Add(new CheckResult("lifecycle: cold reload after unload", CheckStatus.Report, string.Format(
            CultureInfo.InvariantCulture,
            "warm decode {0:0} ms; after Unload {1} ms wall (median overhead {2:0} ms), reported decode {3} ms; " +
            "VAD trim warm {4:0.0} ms, cold {5:0.0} ms",
            cold.WarmWallMs,
            string.Join("/", cold.ColdWallMs.Select(v => v.ToString("0", CultureInfo.InvariantCulture))),
            cold.MedianColdOverheadMs,
            string.Join("/", cold.ColdReportedDecodeMs.Select(v => v.ToString("0", CultureInfo.InvariantCulture))),
            cold.VadWarmTrimMs,
            cold.VadColdTrimMs)));

        var longAudio = LongCapture(library, engine.Hooks, seconds: 120);
        report.Cancellation = lifecycle.Cancel(longAudio, TimeSpan.FromMilliseconds(500), runner.DecodeWallMs("F-long-120s"));
        var cancel = report.Cancellation;
        if (!cancel.Supported)
        {
            report.Checks.Add(new CheckResult("lifecycle: cancel a 120 s decode after 500 ms", CheckStatus.NotSupported,
                cancel.UncancellableDecodeMs is { } full
                    ? $"{cancel.Outcome}; the 120 s decode ran {full:0} ms uninterrupted, which is how long a shutdown would wait"
                    : cancel.Outcome));
        }
        else
        {
            // Throwing is the contract; completing is acceptable only when the decode had already
            // reached its last chunk, and then the text must be the whole transcript.
            var ok = cancel.Outcome == "canceled"
                || (cancel.Outcome == "completed" && (cancel.Characters ?? 0) >= ExpectedLongCharacters(cancel.AudioSeconds));
            report.Checks.Add(new CheckResult("lifecycle: cancel a 120 s decode after 500 ms",
                ok ? CheckStatus.Pass : CheckStatus.Fail,
                $"{cancel.Outcome} {cancel.ReturnedAfterCancelMs?.ToString("0", CultureInfo.InvariantCulture) ?? "?"} ms after the cancel request " +
                $"(requested at {cancel.CancelRequestedAfterMs?.ToString("0", CultureInfo.InvariantCulture) ?? "?"} ms)" +
                (cancel.Characters is { } chars ? $", returned {chars} characters" : string.Empty)));
        }

        report.UnloadRace = lifecycle.UnloadRace(
            "sentence", TimeSpan.FromSeconds(options.Quick ? 3 : 20), minimumAttempts: options.Quick ? 2 : 3);
        var race = report.UnloadRace;
        var raceFailures = race.RecognizerNotInitialized + race.ObjectDisposed + race.OtherFailures;
        var raceDetail = string.Format(
            CultureInfo.InvariantCulture,
            "{0} attempts in {1:0} s against {2} unload calls: {3} succeeded, {4} 'Recognizer is not initialized', {5} disposed, " +
            "{6} other{7}, {8} untrimmed VAD results, {9} empty decodes",
            race.Attempts, race.WindowSeconds, race.UnloadCalls, race.Succeeded, race.RecognizerNotInitialized, race.ObjectDisposed,
            race.OtherFailures, race.OtherFailureTypes.Count == 0 ? string.Empty : $" ({string.Join(", ", race.OtherFailureTypes)})",
            race.UntrimmedVad, race.EmptyDecodes);
        report.Checks.Add(new CheckResult(
            "lifecycle: decode while another thread unloads",
            report.FixExpected
                ? raceFailures == 0 && race.UntrimmedVad == 0 ? CheckStatus.Pass : CheckStatus.Fail
                : CheckStatus.Report,
            raceDetail));

        foreach (var check in report.Checks)
        {
            var tag = check.Status switch
            {
                CheckStatus.Pass => "PASS",
                CheckStatus.Fail => "FAIL",
                CheckStatus.NotSupported => "n/a ",
                _ => " -- ",
            };
            Console.WriteLine($"[{tag}] {check.Name}");
            Console.WriteLine($"         {ReportPrinter.Shorten(check.Detail)}");
        }

        Console.WriteLine($"         lifecycle fix expected: {report.FixExpected} ({report.FixExpectedReason})");
        Console.WriteLine();
        return report;
    }

    // Healthy decodes of these fixtures run at 13 to 14 characters per second of audio (AGENTS.md,
    // "What the recogniser is not"). Half of that is far above any partial result a cancellation at a
    // chunk boundary could return, and far below a complete one.
    private static int ExpectedLongCharacters(double seconds) => (int)(seconds * 6.5);

    private static CapturedAudio LongCapture(BaseLibrary library, ProductionHooks hooks, int seconds)
    {
        var audio = ScenarioCatalog.LongDictationAudio(library, seconds, "I-cancel");
        return new CapturedAudio(hooks.ResampleToTarget!(audio.Data, audio.Data.Length, audio.Format));
    }

    private static float[] Padded(float[] clip, string seedName)
    {
        var seed = ScenarioCatalog.StableSeed("I-" + seedName);
        var lead = AudioTransforms.Floor(AudioTransforms.SamplesFor(0.8, AudioTransforms.BaseRate), -70, seed);
        var tail = AudioTransforms.Floor(AudioTransforms.SamplesFor(0.8, AudioTransforms.BaseRate), -70, seed + 1);
        return AudioTransforms.Concat(lead, clip, tail);
    }

    private static void Summarize(SuiteReport report, TimeSpan elapsed)
    {
        var summary = report.Summary;
        summary.Scenarios = report.Scenarios.Count;
        summary.Passed = report.Scenarios.Count(s => s.Status == CheckStatus.Pass);
        summary.Failed = report.Scenarios.Count(s => s.Status == CheckStatus.Fail);
        summary.ReportedOnly = report.Scenarios.Count(s => s.Status == CheckStatus.Report);

        var checks = report.Scenarios.SelectMany(s => s.Checks.Select(c => (Owner: s.Name, Check: c)))
            .Concat(report.PostProcessing.Select(p => (Owner: "H-source", Check: new CheckResult(p.Name, p.Status, p.Detail))))
            .Concat((report.Lifecycle?.Checks ?? []).Select(c => (Owner: "I-lifecycle", Check: c)))
            .ToList();
        summary.Checks = checks.Count;
        summary.FailedChecks = checks.Count(c => c.Check.Status == CheckStatus.Fail);
        summary.Regressions = checks
            .Where(c => c.Check.Status == CheckStatus.Fail)
            .Select(c => $"{c.Owner}: {c.Check.Name}: {c.Check.Detail}")
            .ToList();
        summary.DurationSeconds = Math.Round(elapsed.TotalSeconds, 1);
        summary.ExitCode = summary.FailedChecks == 0 ? 0 : 1;
    }

    private static void AddGaps(SuiteReport report, BaseLibrary library, string modelSamples)
    {
        report.Gaps.Add(
            "Multilingual speech: only en-US synthetic voices (Microsoft David, Zira and Mark) are installed, so every committed phrase is English. " +
            (Directory.Exists(modelSamples)
                ? "The model's own sample recordings (downloaded with the model, never committed) are decoded as category M, reported only because no reference transcripts ship with them."
                : "The model's sample recordings were not found beside the model, so category M did not run."));
        report.Gaps.Add(
            "All speech is synthetic text-to-speech. Real voices, accents, microphones and rooms are approximated by the device, level, noise and reverb transforms, not reproduced.");
        report.Gaps.Add(
            "AI cleanup, text injection, the overlay and the WPF controller are not exercised: no provider is called, no input is sent and the app is not launched. " +
            "The controller's branch decisions are mirrored from DictationController.ProcessAsync.");
        report.Gaps.Add(
            "Post-processing runs with the suite's test dictionary and snippets only; built-in dictionary libraries are not loaded.");
        report.Gaps.Add(
            "Chunk seams that fall inside speech (forced when a capture is just under a multiple of the chunk limit) are reported, not asserted.");
        if (library.Clips.Any(c => c.Role == "numbers"))
        {
            report.Gaps.Add("The numbers and time phrase is reported only: the recogniser's number formatting is not a contract.");
        }
    }

    private static EnvironmentInfo DescribeEnvironment() => new()
    {
        OsDescription = RuntimeInformation.OSDescription,
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Framework = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount,
        ComputeCapability = ComputeCapabilityReport.Detect().Describe(),
        Process = ResourceMeter.Describe(),
        HarnessConfiguration = Configuration(typeof(ScenarioSuite).Assembly),
        CoreConfiguration = Configuration(typeof(TranscriptionService).Assembly),
        CoreVersion = typeof(TranscriptionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(TranscriptionService).Assembly.GetName().Version?.ToString()
            ?? "unknown",
    };

    private static string Configuration(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";

    private static void PrintHeader(SuiteReport report, RunOptions options)
    {
        var environment = report.Environment;
        Console.WriteLine($"Scribe ASR scenario suite ({report.Mode})");
        Console.WriteLine($"  {environment.ComputeCapability}");
        Console.WriteLine($"  os={environment.OsArchitecture} process={environment.ProcessArchitecture} cores={environment.ProcessorCount} {environment.Process}");
        Console.WriteLine($"  core {environment.CoreVersion} ({environment.CoreConfiguration}), harness {environment.HarnessConfiguration}, {environment.Framework}");
        Console.WriteLine($"  fixtures={options.Fixtures}");
        Console.WriteLine($"  out={options.Output}");
        Console.WriteLine($"  categories={options.Only}");
        foreach (var (hook, available) in report.ProductionHooks)
        {
            Console.WriteLine($"  hook {(available ? "bound  " : "MISSING")} {hook}");
        }

        Console.WriteLine();
    }

    private static void PrintDetail(ScenarioResult result)
    {
        if (result.Long is { } analysis)
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "         {0} chunk(s) [{1}] s, coverage {2:P1}, dropped {3}, inserted {4}, doubled {5}, peak private {6:0} MB",
                analysis.Chunks,
                string.Join(", ", analysis.ChunkSeconds.Select(c => c.ToString("0.00", CultureInfo.InvariantCulture))),
                analysis.CoverageVsReference,
                analysis.DroppedTokens,
                analysis.InsertedTokens,
                analysis.DuplicatedTokens,
                result.Pipeline?.Resources?.PeakPrivateMb ?? 0));
            foreach (var seam in analysis.Seams)
            {
                Console.WriteLine($"         seam {seam.Index} at {seam.AtSeconds:0.00}s: {seam.Location} -> {seam.Verdict}");
            }
        }

        if (result.AutoStop is { } stop)
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "         stopped={0} at {1} ms, heard speech={2}, last voiced buffer {3} ms, speech ended {4} ms, peak level {5}",
                stop.Stopped,
                stop.StopAtMs?.ToString("0", CultureInfo.InvariantCulture) ?? "-",
                stop.HeardSpeech,
                stop.LastVoicedMs?.ToString("0", CultureInfo.InvariantCulture) ?? "-",
                stop.SpeechEndMs?.ToString("0", CultureInfo.InvariantCulture) ?? "-",
                stop.PeakLevel.ToString("0.0000", CultureInfo.InvariantCulture)));
        }
    }

    private static void PrintHistory(HistoryReport history)
    {
        Console.WriteLine("J  History storage (temporary database, public HistoryRepository)");
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "         {0} entries stored with audio ({1:0.0} s): {2:0} bytes per second of audio, encoding {3}",
            history.Stored, history.StoredAudioSeconds, history.BytesPerSecondOfAudio, history.Encoding ?? "-"));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "         database {0:0.0} MB + WAL {1:0.0} MB before close, {2:0.0} MB after; add mean {3:0.0} ms, max {4:0.0} ms",
            history.DatabaseBytesBeforeCheckpoint / 1048576.0,
            history.WalBytesBeforeCheckpoint / 1048576.0,
            history.DatabaseBytesAfterClose / 1048576.0,
            history.MeanAddMs,
            history.MaxAddMs));
        Console.WriteLine($"         re-transcribed {history.Redecoded} stored captures, {history.RedecodeIdentical} identical to the original decode");
        Console.WriteLine();
    }

    private static void PrintSummary(SuiteReport report, RunOptions options)
    {
        var summary = report.Summary;
        Console.WriteLine("Gaps");
        foreach (var gap in report.Gaps)
        {
            Console.WriteLine($"  - {gap}");
        }

        Console.WriteLine();
        Console.WriteLine("Phases: " + string.Join(", ", report.Phases.Select(p =>
            string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} s", p.Phase, p.Seconds))));
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} scenarios: {1} passed, {2} failed, {3} reported only; {4} checks, {5} failed; {6:0.0} s",
            summary.Scenarios, summary.Passed, summary.Failed, summary.ReportedOnly, summary.Checks, summary.FailedChecks,
            summary.DurationSeconds));
        foreach (var regression in summary.Regressions)
        {
            Console.WriteLine($"  REGRESSION {ReportPrinter.Shorten(regression)}");
        }

        Console.WriteLine($"Report: {options.Report}");
    }

    private static string CategoryTitle(char category) => category switch
    {
        'A' => "Clean speech",
        'B' => "Device formats through the production capture conversion",
        'C' => "Level and signal",
        'D' => "Noise and room",
        'E' => "Timing and structure",
        'F' => "Long dictation and chunk seams",
        'G' => "Silence auto-stop (toggle mode)",
        'M' => "Model sample recordings in other languages (reported only)",
        _ => string.Empty,
    };

    /// <summary>
    /// Empties a folder this suite owns, refusing when it holds anything the suite did not create, so
    /// a mistyped <c>--out</c> can never delete someone's files.
    /// </summary>
    private static void ResetOwnedDirectory(string path, Func<string, bool> ownedFile)
    {
        if (Directory.Exists(path))
        {
            var foreign = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .FirstOrDefault(entry => Directory.Exists(entry) || !ownedFile(entry));
            if (foreign is not null)
            {
                throw new IOException($"Refusing to clear {path}: it contains {foreign}, which the scenario suite did not create.");
            }

            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A lingering handle on the temporary database is harmless; the next run clears it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Escape(string text) => text.Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>Walks up from the binary to the repo root so defaults work via `dotnet run`.</summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
