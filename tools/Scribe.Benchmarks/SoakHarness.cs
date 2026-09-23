using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// A repeatable soak of the shipping dictation tail with no microphone, clipboard or input:
/// synthetic device packets through the capture service's own buffer, metering and conversion code,
/// <see cref="TextPostProcessor.ProcessDetailed"/> with a representative dictionary, and history
/// writes into a throwaway SQLite database through the public <see cref="HistoryRepository"/> API.
/// <para>
/// It runs in this process at the priority it was started with, unlike BenchmarkDotNet, so it is
/// the harness for "does this stay flat over hundreds of dictations alongside everything else".
/// Per-cycle timings are summarized per window with the process metrics a leak or growth would move:
/// CPU time, private bytes, handles, threads, GC counts and pause time, and live managed memory
/// measured after a full collection that is excluded from the window it closes.
/// </para>
/// <para>
/// Output is numbers and enum names only. The inputs are invented, and no text, path or device
/// name is printed.
/// </para>
/// </summary>
internal static class SoakHarness
{
    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--soak", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args, ExecutionEnvironment host)
    {
        SoakOptions options;
        try
        {
            options = SoakOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(SoakOptions.Usage);
            return 2;
        }

        // A unique folder under the user's temp directory, never the working directory, so a soak
        // started from the repository leaves nothing in it. Only the folder's own name is printed.
        var workDirectory = Path.Combine(Path.GetTempPath(), "scribe-soak-" + Guid.NewGuid().ToString("N"));
        var shownDirectory = $"%TEMP%\\{Path.GetFileName(workDirectory)}";
        Directory.CreateDirectory(workDirectory);

        // The first Ctrl+C finishes the current cycle and falls through to the cleanup below, so the
        // database is closed and deleted; a second one takes the default path and ends the process.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (!stop.IsCancellationRequested)
            {
                e.Cancel = true;
                stop.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            Console.WriteLine($"Soak database: {shownDirectory} (deleted at the end, and after a first Ctrl+C).");
            return RunCycles(options, host, stop.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            Console.WriteLine(TryDelete(workDirectory)
                ? "Soak database removed."
                : $"Soak database could not be removed: {shownDirectory}.");
        }

        int RunCycles(SoakOptions soak, ExecutionEnvironment environment, CancellationToken token)
        {
            using var database = new ScribeDatabase(new AppPaths(workDirectory), NullLogger<ScribeDatabase>.Instance);
            database.Initialize();
            return new SoakRun(soak, environment, new HistoryRepository(database)).Execute(token);
        }
    }

    private static bool TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        return !Directory.Exists(directory);
    }

    private sealed class SoakRun(SoakOptions options, ExecutionEnvironment host, HistoryRepository history)
    {
        private readonly CaptureBufferPool _pool = new();
        private readonly WaveFormat _format = SyntheticCapture.DefaultDeviceFormat;
        private readonly Queue<DateTimeOffset> _written = new();
        private readonly List<Window> _windows = [];

        public int Execute(CancellationToken stop)
        {
            var packets = SyntheticCapture.OneSecondOfPackets(_format);
            var reservation = AudioCaptureService.ReservationBytes(_format);
            var processor = RepresentativeWorkload.CreatePostProcessor(options.Dictionary, options.Snippets);
            var shortRaw = RepresentativeWorkload.RawTranscript(WorkloadTextLength.Short);
            var longRaw = RepresentativeWorkload.RawTranscript(WorkloadTextLength.Long);

            // Cleanup off (source is the text) and on (cleaned text over the raw source), short and long.
            var inputs = new[]
            {
                (Text: shortRaw, Source: shortRaw),
                (Text: RepresentativeWorkload.CleanedTranscript(WorkloadTextLength.Short), Source: shortRaw),
                (Text: longRaw, Source: longRaw),
                (Text: RepresentativeWorkload.CleanedTranscript(WorkloadTextLength.Long), Source: longRaw),
            };

            PrintHeader();
            var baseline = ProcessSample.Take();
            var window = new List<CycleTiming>(options.ReportEvery);
            var completed = 0;
            for (var cycle = 1; cycle <= options.Cycles && !stop.IsCancellationRequested; cycle++)
            {
                var input = inputs[(cycle - 1) % inputs.Length];
                window.Add(RunCycle(packets, reservation, processor, input.Text, input.Source));
                completed = cycle;

                if (options.ReleaseBuffersEvery > 0 && cycle % options.ReleaseBuffersEvery == 0)
                {
                    _pool.ReleaseRetained();
                }

                if (options.PruneEvery > 0 && cycle % options.PruneEvery == 0)
                {
                    Prune();
                }

                if (options.PerCycle)
                {
                    PrintCycle(cycle, window[^1]);
                }

                if (window.Count == options.ReportEvery || cycle == options.Cycles || stop.IsCancellationRequested)
                {
                    var closing = ProcessSample.Take();
                    _ = history.GetRecent(20);
                    var live = MeasureLiveManagedBytes();
                    var summary = new Window(
                        cycle - window.Count + 1, cycle, window.ToArray(), baseline, closing, live, _pool.RetainedBytes);
                    _windows.Add(summary);
                    PrintWindow(summary);
                    window.Clear();

                    // The forced collection above belongs to the measurement, not the workload.
                    baseline = ProcessSample.Take();
                }
            }

            if (completed < options.Cycles)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"Stopped by Ctrl+C after {completed} of {options.Cycles} cycles."));
            }

            PrintSummary();
            return 0;
        }

        private CycleTiming RunCycle(
            byte[][] packets, int reservation, TextPostProcessor processor, string text, string source)
        {
            var total = Stopwatch.StartNew();

            var stage = Stopwatch.StartNew();
            var recording = _pool.Rent(reservation);
            CapturedAudio audio;
            double appendMs;
            try
            {
                for (var packet = 0; packet < options.CaptureSeconds * SyntheticCapture.PacketsPerSecond; packet++)
                {
                    _ = AudioCaptureService.AppendChunk(recording, packets[packet % packets.Length], _format);
                }

                appendMs = stage.Elapsed.TotalMilliseconds;
                stage.Restart();
                audio = AudioCaptureService.ConvertCapture(recording, _format, _ => { });
            }
            finally
            {
                _pool.Return(recording, retain: true);
            }

            var convertMs = stage.Elapsed.TotalMilliseconds;

            stage.Restart();
            var processed = processor.ProcessDetailed(text, source);
            var postMs = stage.Elapsed.TotalMilliseconds;

            stage.Restart();
            var timestamp = DateTimeOffset.UtcNow;
            history.Add(
                new HistoryEntry(
                    0,
                    timestamp,
                    processed.Text,
                    (int)audio.Duration.TotalMilliseconds,
                    DecodeMilliseconds: 0,
                    CleanupMilliseconds: null,
                    TargetApp: "soak",
                    AudioBlobId: null,
                    TranscriptionModelId: "synthetic"),
                options.StoreAudio ? audio : null);
            _written.Enqueue(timestamp);
            var historyMs = stage.Elapsed.TotalMilliseconds;

            return new CycleTiming(appendMs, convertMs, postMs, historyMs, total.Elapsed.TotalMilliseconds);
        }

        // Keeps the newest KeepRows entries, exercising the delete path and orphaned-audio cleanup
        // the way retention does, so the database stays bounded however long the soak runs.
        private void Prune()
        {
            if (_written.Count <= options.KeepRows)
            {
                return;
            }

            while (_written.Count > options.KeepRows + 1)
            {
                _written.Dequeue();
            }

            _ = history.PruneOlderThan(_written.Dequeue());
        }

        private static long MeasureLiveManagedBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: false);
        }

        private void PrintHeader()
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Scribe soak: {options.Cycles} cycles of {options.CaptureSeconds}s synthetic capture " +
                $"({_format.SampleRate} Hz, {_format.Channels} ch, {_format.BitsPerSample}-bit {_format.Encoding}), " +
                $"dictionary={options.Dictionary} snippets={options.Snippets} audioHistory={options.StoreAudio} " +
                $"prune every {options.PruneEvery} keeping {options.KeepRows}, release buffers every {options.ReleaseBuffersEvery}"));
            Console.WriteLine($"host: {host.Describe()}");
            Console.WriteLine(
                "Timings are ms per cycle as p50/p95/max. cpu and alloc are per cycle; private, ws and live are MB at the " +
                "window end; live is managed memory after a full collection; retained is the capture buffer kept for the next press.");
            Console.WriteLine(
                "window     append          convert         postproc        history         total           " +
                "cpu    alloc   private  ws       live    retained handles threads gc0  gc1  gc2  pause");
            if (options.PerCycle)
            {
                Console.WriteLine("cycle,append_ms,convert_ms,postproc_ms,history_ms,total_ms");
            }
        }

        private static void PrintCycle(int cycle, CycleTiming timing) =>
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{cycle},{timing.AppendMs:F3},{timing.ConvertMs:F3},{timing.PostProcessMs:F3},{timing.HistoryMs:F3},{timing.TotalMs:F3}"));

        private static void PrintWindow(Window window)
        {
            var cycles = window.Timings.Length;
            var start = window.Start;
            var end = window.End;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{window.First,4}-{window.Last,-5} {Stats(window.Timings.Select(t => t.AppendMs))} " +
                $"{Stats(window.Timings.Select(t => t.ConvertMs))} {Stats(window.Timings.Select(t => t.PostProcessMs))} " +
                $"{Stats(window.Timings.Select(t => t.HistoryMs))} {Stats(window.Timings.Select(t => t.TotalMs))} " +
                $"{(end.Cpu - start.Cpu).TotalMilliseconds / cycles,6:F1} " +
                $"{(end.AllocatedBytes - start.AllocatedBytes) / (double)cycles / Mb,7:F2} " +
                $"{end.PrivateBytes / (double)Mb,8:F1} {end.WorkingSet / (double)Mb,8:F1} " +
                $"{window.LiveManagedBytes / (double)Mb,7:F1} {window.RetainedBytes / (double)Mb,8:F1} " +
                $"{end.Handles,7} {end.Threads,7} {end.Gen0 - start.Gen0,4} {end.Gen1 - start.Gen1,4} {end.Gen2 - start.Gen2,4} " +
                $"{(end.GcPause - start.GcPause).TotalMilliseconds,6:F1}"));
        }

        private void PrintSummary()
        {
            Console.WriteLine();
            Console.WriteLine($"host (end): {ExecutionEnvironment.Capture().Describe()}");
            if (_windows.Count < 3)
            {
                Console.WriteLine("Fewer than three windows; run more cycles for a growth trend.");
                return;
            }

            // The first window carries JIT, cold caches and the first reservations, so the trend is
            // taken from the second window onward.
            var steady = _windows.Skip(1).ToList();
            var first = steady[0];
            var last = steady[^1];
            var span = last.Last - first.Last;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"steady state, cycles {first.Last} to {last.Last} ({span} cycles):"));
            Report("private bytes", first.End.PrivateBytes / (double)Mb, last.End.PrivateBytes / (double)Mb, "MB",
                Slope(steady, w => w.End.PrivateBytes) / 1024, "KB/cycle");
            Report("live managed", first.LiveManagedBytes / (double)Mb, last.LiveManagedBytes / (double)Mb, "MB",
                Slope(steady, w => w.LiveManagedBytes) / 1024, "KB/cycle");
            Report("handles", first.End.Handles, last.End.Handles, "", Slope(steady, w => w.End.Handles), "per cycle");
            Report("threads", first.End.Threads, last.End.Threads, "", Slope(steady, w => w.End.Threads), "per cycle");
            Report("total ms p50", Percentile(first.Timings.Select(t => t.TotalMs), 50),
                Percentile(last.Timings.Select(t => t.TotalMs), 50), "ms",
                Slope(steady, w => Percentile(w.Timings.Select(t => t.TotalMs), 50)), "ms/cycle");

            static void Report(string name, double from, double to, string unit, double slope, string slopeUnit) =>
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {name,-14} {from,10:F1} -> {to,10:F1} {unit,-2} (change {to - from,9:F1}; trend {slope,9:F3} {slopeUnit})"));
        }

        private static double Slope(IReadOnlyList<Window> windows, Func<Window, double> value)
        {
            // Least-squares slope against the cycle number at each window end.
            var xs = windows.Select(w => (double)w.Last).ToArray();
            var ys = windows.Select(value).ToArray();
            var meanX = xs.Average();
            var meanY = ys.Average();
            var numerator = 0d;
            var denominator = 0d;
            for (var index = 0; index < xs.Length; index++)
            {
                numerator += (xs[index] - meanX) * (ys[index] - meanY);
                denominator += (xs[index] - meanX) * (xs[index] - meanX);
            }

            return denominator == 0 ? 0 : numerator / denominator;
        }

        private static string Stats(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Percentile(sorted, 50),5:F1}/{Percentile(sorted, 95),5:F1}/{sorted[^1],5:F1}");
        }

        private static double Percentile(IEnumerable<double> values, int percentile)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            var rank = (int)Math.Ceiling(percentile / 100d * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }

        private const long Mb = 1024 * 1024;
    }

    private sealed record CycleTiming(double AppendMs, double ConvertMs, double PostProcessMs, double HistoryMs, double TotalMs);

    private sealed record Window(
        int First,
        int Last,
        CycleTiming[] Timings,
        ProcessSample Start,
        ProcessSample End,
        long LiveManagedBytes,
        long RetainedBytes);

    private sealed record ProcessSample(
        TimeSpan Cpu,
        long PrivateBytes,
        long WorkingSet,
        int Handles,
        int Threads,
        int Gen0,
        int Gen1,
        int Gen2,
        TimeSpan GcPause,
        long AllocatedBytes)
    {
        public static ProcessSample Take()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new ProcessSample(
                process.TotalProcessorTime,
                process.PrivateMemorySize64,
                process.WorkingSet64,
                process.HandleCount,
                process.Threads.Count,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                GC.GetTotalPauseDuration(),
                GC.GetTotalAllocatedBytes(precise: false));
        }
    }

    private sealed record SoakOptions(
        int Cycles,
        int CaptureSeconds,
        int ReportEvery,
        WorkloadDictionary Dictionary,
        bool Snippets,
        bool StoreAudio,
        int PruneEvery,
        int KeepRows,
        int ReleaseBuffersEvery,
        bool PerCycle)
    {
        public const string Usage =
            "usage: --soak [--cycles 300] [--capture-seconds 8] [--report-every 25] [--dictionary large|small] " +
            "[--no-snippets] [--no-audio-history] [--prune-every 50] [--keep-rows 100] [--release-buffers-every 0] [--per-cycle]";

        public static SoakOptions Parse(string[] args)
        {
            var options = new SoakOptions(
                Cycles: Int(args, "--cycles", 300, minimum: 1),
                CaptureSeconds: Int(args, "--capture-seconds", 8, minimum: 1),
                ReportEvery: Int(args, "--report-every", 25, minimum: 1),
                Dictionary: Value(args, "--dictionary") switch
                {
                    null or "large" => WorkloadDictionary.Large,
                    "small" => WorkloadDictionary.Small,
                    var other => throw new ArgumentException($"Unknown --dictionary '{other}'."),
                },
                Snippets: !Flag(args, "--no-snippets"),
                StoreAudio: !Flag(args, "--no-audio-history"),
                PruneEvery: Int(args, "--prune-every", 50, minimum: 0),
                KeepRows: Int(args, "--keep-rows", 100, minimum: 1),
                ReleaseBuffersEvery: Int(args, "--release-buffers-every", 0, minimum: 0),
                PerCycle: Flag(args, "--per-cycle"));
            return options;
        }

        private static bool Flag(string[] args, string name) =>
            args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

        private static string? Value(string[] args, string name)
        {
            var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : null;
        }

        private static int Int(string[] args, string name, int fallback, int minimum)
        {
            var text = Value(args, name);
            if (text is null)
            {
                return fallback;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
            {
                throw new ArgumentException($"{name} needs a whole number of at least {minimum}.");
            }

            return value;
        }
    }
}
