using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.AsrCheck;

/// <summary>
/// Measures the resident cost of the speech recognizer across load, idle, decode, unload and reload.
/// It uses process counters only, so it includes native ONNX Runtime allocations that the managed heap
/// cannot see.
/// </summary>
internal static class ResourceSweep
{
    private const int SampleRate = 16_000;

    public static int Run(string[] args, IReadOnlyList<float[]> clips, string decoding)
    {
        IReadOnlyList<int> durations;
        int idleSeconds;
        try
        {
            durations = ParseList(Program.ArgValue(args, "--resource-seconds") ?? "12,55", "--resource-seconds", minimum: 1);
            idleSeconds = ParseList(Program.ArgValue(args, "--resource-idle-seconds") ?? "20", "--resource-idle-seconds", minimum: 1)[0];
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var usableClips = clips.Where(clip => clip.Length > 0).ToList();
        if (usableClips.Count == 0)
        {
            Console.Error.WriteLine("No usable fixture audio for the resource sweep.");
            return 1;
        }

        var requestedThreads = 0;
        if (Program.ArgValue(args, "--resource-threads") is { } threadText &&
            (!int.TryParse(threadText, NumberStyles.Integer, CultureInfo.InvariantCulture, out requestedThreads) ||
             requestedThreads < 0))
        {
            Console.Error.WriteLine("--resource-threads expects a whole number of at least 0.");
            return 1;
        }

        Console.WriteLine("Resource sweep (process counters)");
        Console.WriteLine($"  threads={requestedThreads} decoding={decoding} idleSeconds={idleSeconds}");
        Console.WriteLine("  columns: phase, wall ms, cpu ms, working set MB, private MB, managed heap MB, threads, chars");
        Console.WriteLine();

        using var service = new TranscriptionService(
            new ModelLocator(new AppPaths()),
            Options.Create(new TranscriptionOptions
            {
                NumThreads = requestedThreads,
                DecodingMethod = decoding,
                AllowUnsafeDecodingMethod = true,
            }),
            NullLogger<TranscriptionService>.Instance);

        Print("process-start", Sample.Zero);

        var load = Measure(() =>
        {
            service.Initialize();
            return 0;
        });
        Print("initialize", load);

        var idleLoaded = Measure(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(idleSeconds));
            return 0;
        });
        Print($"idle-loaded-{idleSeconds}s", idleLoaded);

        foreach (var seconds in durations)
        {
            var samples = Program.Concatenate(usableClips, SampleRate, seconds, offset: 0);
            var audio = new CapturedAudio(samples, SampleRate);
            var decode = Measure(() => service.Transcribe(audio).Text.Length);
            Print($"decode-{seconds}s", decode);
        }

        var unload = Measure(() =>
        {
            service.Unload();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Sleep(TimeSpan.FromSeconds(2));
            return 0;
        });
        Print("unload-plus-gc", unload);

        var idleUnloaded = Measure(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(idleSeconds, 10)));
            return 0;
        });
        Print("idle-unloaded", idleUnloaded);

        var reload = Measure(() =>
        {
            service.Initialize();
            return 0;
        });
        Print("reload", reload);

        return 0;
    }

    private static Sample Measure(Func<int> action)
    {
        var beforeCpu = ProcessCpu();
        var wall = Stopwatch.StartNew();
        var chars = action();
        wall.Stop();
        var cpu = ProcessCpu() - beforeCpu;
        return new Sample(wall.Elapsed.TotalMilliseconds, cpu.TotalMilliseconds, chars, Counters.Read());
    }

    private static TimeSpan ProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static void Print(string phase, Sample sample)
    {
        var counters = sample.Counters ?? Counters.Read();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {phase,-18} {sample.WallMs,8:0} {sample.CpuMs,8:0} {counters.WorkingSetMb,8:0.0} " +
            $"{counters.PrivateMb,8:0.0} {counters.ManagedHeapMb,8:0.0} {counters.Threads,7} {sample.Characters,7}"));
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

    private sealed record Sample(double WallMs, double CpuMs, int Characters, Counters? Counters)
    {
        public static Sample Zero { get; } = new(0, 0, 0, null);
    }

    private sealed record Counters(double WorkingSetMb, double PrivateMb, double ManagedHeapMb, int Threads)
    {
        public static Counters Read()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new Counters(
                ToMb(process.WorkingSet64),
                ToMb(process.PrivateMemorySize64),
                ToMb(GC.GetTotalMemory(forceFullCollection: false)),
                process.Threads.Count);
        }

        private static double ToMb(long bytes) => bytes / 1024d / 1024d;
    }
}
