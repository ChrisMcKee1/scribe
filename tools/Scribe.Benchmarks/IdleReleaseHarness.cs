using System.Diagnostics;
using System.Text.Json;
using Scribe.Core.Diagnostics;
using Scribe.Core.Lifecycle;

namespace Scribe.Benchmarks;

/// <summary>
/// <c>--idle-release [--flags ForcedIdleGc] [--json file]</c>: one fresh process builds a field-shaped managed heap,
/// empties the session's capture-sized arrays, then runs the idle release's collection through the production seam
/// (<see cref="IdleCollection.Compact(PerfFlags)"/>) exactly once, and reports GC-committed, heap, private and working-set
/// bytes, the collection's pause and wall time, and the cost of the next 64 MiB allocate-and-touch. Run it in several fresh
/// processes per flag state, interleaved, inside the bench lane: decommit is a per-process fact that BenchmarkDotNet's
/// warm iterations cannot measure.
/// </summary>
/// <remarks>
/// The workload is synthetic but shaped on the maintainer's field log: twenty dictations of 5 to 196.9 s (577 s in all),
/// each leaving a 48 kHz mono float capture, grown by doubling from a 30 s reservation as the capture pool does, and its
/// 16 kHz copy, with the previous dictation's arrays kept until the next one ends, over about 42 MiB of long-lived small
/// objects (the installed app's idle gen2 was 42.5 MB). No audio, model, network, window or input is involved.
/// </remarks>
internal static class IdleReleaseHarness
{
    private static readonly double[] DictationSeconds =
        [8, 12, 5, 30, 10, 45, 9, 15, 55, 7, 20, 196.9, 11, 6, 37, 9.5, 14, 25, 8, 53.7];

    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--idle-release", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args, ExecutionEnvironment host)
    {
        var flagsValue = Value(args, "--flags") ?? string.Empty;
        var jsonPath = Value(args, "--json");
        var flags = PerfFlags.Parse(flagsValue);

        var longLived = BuildLongLivedState(42 * 1024 * 1024);
        var peakCommitted = RunSession();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = Snapshot(process);

        var result = IdleCollection.Compact(flags);

        process.Refresh();
        var after = Snapshot(process);

        var next = Stopwatch.StartNew();
        var reuse = new byte[64 * 1024 * 1024];
        for (var i = 0; i < reuse.Length; i += 4096)
        {
            reuse[i] = 1;
        }

        next.Stop();
        GC.KeepAlive(reuse);
        GC.KeepAlive(longLived);

        var report = new
        {
            host = host.Describe(),
            flags = flags.Describe(),
            mode = result.Mode.ToString(),
            peakCommittedMiB = peakCommitted / 1048576.0,
            before = before,
            after = after,
            collectionCommittedBeforeMiB = result.CommittedBeforeBytes / 1048576.0,
            collectionCommittedAfterMiB = result.CommittedAfterBytes / 1048576.0,
            collectionHeapAfterMiB = result.HeapAfterBytes / 1048576.0,
            pauseMs = result.Pause.TotalMilliseconds,
            callMs = result.Elapsed.TotalMilliseconds,
            next64MiBMs = next.Elapsed.TotalMilliseconds,
            gcCount = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = false });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            File.AppendAllText(jsonPath, json + Environment.NewLine);
        }

        return 0;
    }

    private static object Snapshot(Process process)
    {
        var info = GC.GetGCMemoryInfo();
        return new
        {
            gcCommittedMiB = info.TotalCommittedBytes / 1048576.0,
            gcHeapMiB = info.HeapSizeBytes / 1048576.0,
            fragmentedMiB = info.FragmentedBytes / 1048576.0,
            privateMiB = process.PrivateMemorySize64 / 1048576.0,
            workingSetMiB = process.WorkingSet64 / 1048576.0,
        };
    }

    private static List<byte[]> BuildLongLivedState(int bytes)
    {
        var state = new List<byte[]>();
        for (var total = 0; total < bytes; total += 4096)
        {
            var block = new byte[4096];
            block[0] = 1;
            state.Add(block);
        }

        return state;
    }

    // Twenty captures shaped on the field log; the previous dictation's arrays stay alive until the next ends, as a recovery
    // copy and a history entry would, and a little small garbage accompanies each one.
    private static long RunSession()
    {
        long peak = 0;
        float[]? previousCapture = null;
        float[]? previousConverted = null;
        var reservation = 48_000 * 30;
        foreach (var seconds in DictationSeconds)
        {
            var needed = (int)(48_000 * seconds);
            var capture = new float[reservation];
            while (capture.Length < needed)
            {
                var grown = new float[capture.Length * 2];
                Array.Copy(capture, grown, capture.Length);
                capture = grown;
            }

            Touch(capture, needed);
            var converted = new float[(int)(16_000 * seconds)];
            Touch(converted, converted.Length);
            for (var i = 0; i < 2_000; i++)
            {
                _ = new byte[256];
            }

            previousCapture = capture;
            previousConverted = converted;
            peak = Math.Max(peak, GC.GetGCMemoryInfo().TotalCommittedBytes);
        }

        GC.KeepAlive(previousCapture);
        GC.KeepAlive(previousConverted);
        return peak;
    }

    private static void Touch(float[] samples, int count)
    {
        for (var i = 0; i < count; i += 1024)
        {
            samples[i] = i;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
