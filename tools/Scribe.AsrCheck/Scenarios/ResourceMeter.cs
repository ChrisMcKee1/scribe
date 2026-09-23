using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Scribe.AsrCheck.Scenarios;

/// <summary>
/// Measures what one scenario cost the process: peak private bytes (sampled, because ONNX Runtime's
/// arena is native memory the GC never sees), CPU time, and GC activity.
/// <para>
/// Private bytes come from GetProcessMemoryInfo's PrivateUsage (the commit charge) rather than
/// <see cref="Process.PrivateMemorySize64"/>, which snapshots every process on the machine per read
/// and would itself cost measurable CPU at a 10 ms sampling interval.
/// </para>
/// </summary>
internal sealed class ResourceMeter : IDisposable
{
    private readonly Thread _sampler;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _peakGate = new();
    private volatile bool _stopping;
    private long _generation;
    private long _peakPrivateBytes;

    public ResourceMeter()
    {
        _sampler = new Thread(Sample) { IsBackground = true, Name = "scenario-memory-sampler" };
        _sampler.Start();
    }

    public static long PrivateBytes()
    {
        var counters = new ProcessMemoryCountersEx { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Cb) ? (long)counters.PrivateUsage : 0;
    }

    public Snapshot Begin()
    {
        var now = PrivateBytes();
        lock (_peakGate)
        {
            // A new generation discards any sample the sampler took before this window opened.
            _generation++;
            _peakPrivateBytes = now;
        }

        return Capture(now);
    }

    public ResourceUsage End(Snapshot start)
    {
        var endPrivate = PrivateBytes();
        long peak;
        lock (_peakGate)
        {
            _peakPrivateBytes = Math.Max(_peakPrivateBytes, endPrivate);
            peak = _peakPrivateBytes;
        }

        var end = Capture(endPrivate);
        return new ResourceUsage(
            PeakPrivateMb: Mb(peak),
            StartPrivateMb: Mb(start.PrivateBytes),
            EndPrivateMb: Mb(end.PrivateBytes),
            CpuMs: Math.Round((end.Cpu - start.Cpu).TotalMilliseconds, 1),
            Gen0: end.Gen0 - start.Gen0,
            Gen1: end.Gen1 - start.Gen1,
            Gen2: end.Gen2 - start.Gen2,
            GcPauseMs: Math.Round((end.Pause - start.Pause).TotalMilliseconds, 2),
            AllocatedMb: Mb(end.Allocated - start.Allocated));
    }

    public void Dispose()
    {
        _stopping = true;
        _sampler.Join(TimeSpan.FromSeconds(1));
        _process.Dispose();
    }

    public static string Describe()
    {
        using var process = Process.GetCurrentProcess();
        string priority;
        try
        {
            priority = process.PriorityClass.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            priority = "unknown";
        }

        return $"priority={priority} serverGC={GCSettings.IsServerGC} latency={GCSettings.LatencyMode}";
    }

    private Snapshot Capture(long privateBytes) => new(
        privateBytes,
        _process.TotalProcessorTime,
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalPauseDuration(),
        GC.GetTotalAllocatedBytes(precise: false));

    private void Sample()
    {
        while (!_stopping)
        {
            long generation;
            lock (_peakGate)
            {
                generation = _generation;
            }

            var value = PrivateBytes();
            lock (_peakGate)
            {
                if (generation == _generation && value > _peakPrivateBytes)
                {
                    _peakPrivateBytes = value;
                }
            }

            Thread.Sleep(10);
        }
    }

    private static double Mb(long bytes) => Math.Round(bytes / (1024.0 * 1024.0), 1);

    internal readonly record struct Snapshot(
        long PrivateBytes, TimeSpan Cpu, int Gen0, int Gen1, int Gen2, TimeSpan Pause, long Allocated);

    // PROCESS_MEMORY_COUNTERS_EX, field for field.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}

internal sealed record ResourceUsage(
    double PeakPrivateMb,
    double StartPrivateMb,
    double EndPrivateMb,
    double CpuMs,
    int Gen0,
    int Gen1,
    int Gen2,
    double GcPauseMs,
    double AllocatedMb);
