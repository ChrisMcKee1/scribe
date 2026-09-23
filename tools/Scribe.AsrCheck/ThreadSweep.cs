using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.AsrCheck;

/// <summary>
/// Measures decode cost against the one threading control sherpa-onnx exposes,
/// <see cref="TranscriptionOptions.NumThreads"/> (ModelConfig.NumThreads in the pinned
/// OfflineModelConfig). For every thread count and audio length it loads a fresh recognizer the
/// way the app does after an idle release (load plus warm-up), then times the first real decode
/// (cold) and several repeats (warm), reporting wall time, real-time factor and process CPU time.
/// <para>
/// This is a measurement, not a gate: the answer depends on the machine, its core layout and
/// whatever else is running. Every row carries the architecture, emulation and priority it was
/// measured under. It runs at the priority the tool was started with (normally Normal), so it
/// reflects coexistence with other work rather than a boosted benchmark process.
/// </para>
/// </summary>
internal static class ThreadSweep
{
    private const int SampleRate = 16_000;

    public static int Run(string[] args, string threadList, IReadOnlyList<float[]> clips, string decoding)
    {
        IReadOnlyList<int> threadCounts;
        IReadOnlyList<int> durations;
        int repeats;
        try
        {
            threadCounts = ParseList(threadList, "--threads", minimum: 0);
            durations = ParseList(Program.ArgValue(args, "--sweep-seconds") ?? "3,8,20,45", "--sweep-seconds", minimum: 1);
            repeats = ParseList(Program.ArgValue(args, "--sweep-repeats") ?? "5", "--sweep-repeats", minimum: 1)[0];
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var usableClips = clips.Where(clip => clip.Length > 0).ToList();
        if (usableClips.Count == 0)
        {
            Console.Error.WriteLine("No usable fixture audio for the thread sweep.");
            return 1;
        }

        var environment = Describe();
        Console.WriteLine("Thread sweep (sherpa-onnx NumThreads; 0 = the app's automatic choice)");
        Console.WriteLine($"  {environment} logical processors={Environment.ProcessorCount} repeats={repeats} decoding={decoding}");
        Console.WriteLine(
            "  load = Initialize (recognizer construction plus its 0.5 s warm-up decode); cold = the first real decode " +
            "after that; warm = median of the repeats that follow. cpu = process CPU time; cores = cpu / wall.");
        Console.WriteLine();
        Console.WriteLine(
            "  threads audio  load ms (cpu)    cold ms  cold cpu  cold RTF   warm ms (min-max)        warm cpu  warm RTF  x rt   cores  chars  env");

        var rows = new List<Row>();
        var failures = 0;
        foreach (var requested in threadCounts)
        {
            foreach (var seconds in durations)
            {
                var samples = Program.Concatenate(usableClips, SampleRate, seconds, offset: 0);
                var probe = new ThreadCountProbe();
                using var service = new TranscriptionService(
                    new ModelLocator(new AppPaths()),
                    Options.Create(new TranscriptionOptions
                    {
                        NumThreads = requested,
                        DecodingMethod = decoding,
                        AllowUnsafeDecodingMethod = true,
                    }),
                    probe);

                var load = Measure(() => { service.Initialize(); return 0; });
                var audio = new CapturedAudio(samples, SampleRate);
                var cold = Measure(() => service.Transcribe(audio).Text.Length);
                var warm = Enumerable.Range(0, repeats)
                    .Select(_ => Measure(() => service.Transcribe(audio).Text.Length))
                    .ToList();

                var row = new Row(requested, probe.Threads, seconds, load, cold, warm);
                rows.Add(row);
                if (row.Collapsed)
                {
                    failures++;
                }

                Console.WriteLine(row.Format(environment));
            }
        }

        Console.WriteLine();
        PrintBestPerDuration(rows, durations);
        Console.WriteLine($"  measured under: {Describe()}");
        Console.WriteLine();
        return failures;
    }

    private static void PrintBestPerDuration(IReadOnlyList<Row> rows, IReadOnlyList<int> durations)
    {
        Console.WriteLine("  fastest warm decode per audio length (and each thread count relative to it):");
        foreach (var seconds in durations)
        {
            var forDuration = rows.Where(row => row.Seconds == seconds).ToList();
            var best = forDuration.MinBy(row => row.WarmMedianMs);
            if (best is null)
            {
                continue;
            }

            var relative = string.Join("  ", forDuration.Select(row => string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Label}={row.WarmMedianMs / best.WarmMedianMs:0.00}x")));
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {seconds,3}s: best {best.Label} at {best.WarmMedianMs:0} ms | {relative}"));
        }
    }

    private static Sample Measure(Func<int> action)
    {
        var cpuBefore = ProcessCpu();
        var wall = Stopwatch.StartNew();
        var characters = action();
        wall.Stop();
        return new Sample(wall.Elapsed.TotalMilliseconds, (ProcessCpu() - cpuBefore).TotalMilliseconds, characters);
    }

    private static TimeSpan ProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static string Describe()
    {
        var capability = ComputeCapabilityReport.Create(
            RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);
        string priority;
        try
        {
            using var process = Process.GetCurrentProcess();
            priority = process.PriorityClass.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            priority = "unknown";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"process={capability.ProcessArchitecture} os={capability.OsArchitecture} " +
            $"{(capability.IsEmulated ? "EMULATED" : "native")} priority={priority}");
    }

    private static IReadOnlyList<int> ParseList(string text, string name, int minimum)
    {
        var values = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
            {
                throw new FormatException($"{name} expects comma-separated whole numbers of at least {minimum}.");
            }

            values.Add(value);
        }

        if (values.Count == 0)
        {
            throw new FormatException($"{name} needs at least one value.");
        }

        return values;
    }

    private sealed record Sample(double WallMs, double CpuMs, int Characters);

    private sealed record Row(int Requested, int? Threads, int Seconds, Sample Load, Sample Cold, IReadOnlyList<Sample> Warm)
    {
        // "auto(?)" when the service's load message did not carry the count it resolved, rather
        // than a guess at its rule.
        public string Label => Requested == 0
            ? $"auto({(Threads is { } resolved ? resolved.ToString(CultureInfo.InvariantCulture) : "?")})"
            : Requested.ToString(CultureInfo.InvariantCulture);

        public double WarmMedianMs => Median(Warm.Select(sample => sample.WallMs));

        // A decode that returns (almost) nothing for speech-dense fixtures is the collapse the
        // long-audio check hunts; it invalidates the timing of that row rather than improving it.
        public bool Collapsed => Cold.Characters < Seconds * 2 || Warm.Any(sample => sample.Characters < Seconds * 2);

        public string Format(string environment)
        {
            var warmCpu = Median(Warm.Select(sample => sample.CpuMs));
            var audioMs = Seconds * 1000d;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"  {Label,-7} {Seconds,4}s {Load.WallMs,7:0} ({Load.CpuMs,6:0}) {Cold.WallMs,9:0} {Cold.CpuMs,9:0} " +
                $"{Cold.WallMs / audioMs,9:0.000} {WarmMedianMs,9:0} ({Warm.Min(s => s.WallMs),6:0}-{Warm.Max(s => s.WallMs),6:0}) " +
                $"{warmCpu,9:0} {WarmMedianMs / audioMs,9:0.000} {audioMs / WarmMedianMs,5:0.0} " +
                $"{warmCpu / WarmMedianMs,6:0.0} {Warm[0].Characters,6}{(Collapsed ? " COLLAPSE" : string.Empty)}  {environment}");
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            return sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
        }
    }

    /// <summary>
    /// Reads the thread count the service actually configured from the structured value of its
    /// load message, so "auto" rows report the number sherpa-onnx was given. Nothing is formatted
    /// or printed, and Debug, the level at which the service writes decoded text, is never enabled.
    /// </summary>
    private sealed class ThreadCountProbe : ILogger<TranscriptionService>
    {
        public int? Threads { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                return;
            }

            foreach (var (key, value) in values)
            {
                if (key == "Threads" && value is int threads)
                {
                    Threads = threads;
                }
            }
        }
    }
}
