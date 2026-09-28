using System.Diagnostics;
using System.Runtime;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Lifecycle;

/// <summary>What asked for an idle release, for its log line.</summary>
public enum IdleReleaseTrigger
{
    /// <summary>The idle release countdown fell due: no dictation for the configured idle minutes.</summary>
    IdleDeadline,

    /// <summary>Dictation was paused while no recording was live (see <see cref="IdleReleaseRequests.OnPauseChange"/>).</summary>
    PauseRequest,
}

/// <summary>The mode of the idle release's collection, logged by name.</summary>
public enum IdleCollectionMode
{
    /// <summary><see cref="GCCollectionMode.Forced"/>, the collection 0.5.0 made, brought back by <see cref="PerfFlags.ForcedIdleGc"/>.</summary>
    Forced,

    /// <summary><see cref="GCCollectionMode.Aggressive"/>, the default from 0.5.1.</summary>
    Aggressive,
}

/// <summary>What one idle release collection did, in numbers only.</summary>
/// <param name="Mode">The collection mode used.</param>
/// <param name="CommittedBeforeBytes">GC-committed bytes as of the collection before this one.</param>
/// <param name="CommittedAfterBytes">GC-committed bytes after this collection.</param>
/// <param name="HeapAfterBytes">The managed heap after this collection.</param>
/// <param name="Pause">The GC pause time this collection added.</param>
/// <param name="Elapsed">Wall time of the collection call.</param>
public readonly record struct IdleCollectionResult(
    IdleCollectionMode Mode,
    long CommittedBeforeBytes,
    long CommittedAfterBytes,
    long HeapAfterBytes,
    TimeSpan Pause,
    TimeSpan Elapsed);

/// <summary>
/// The runtime calls the idle collection makes, behind a seam so tests record them without touching the test host's GC:
/// the large object heap compaction mode and the collection are both process-wide.
/// </summary>
internal interface IIdleCollector
{
    int MaxGeneration { get; }

    void CompactLargeObjectHeapOnce();

    void Collect(int generation, GCCollectionMode mode, bool blocking, bool compacting);

    (long CommittedBytes, long HeapBytes) Memory();

    TimeSpan TotalPause();

    long Timestamp();

    TimeSpan ElapsedSince(long timestamp);
}

/// <summary>
/// The idle release's one blocking, compacting collection of the oldest generation: the compact step
/// <see cref="IdleModelRelease"/> runs after the unload and after the capture service's retained buffers and scratch pool
/// are released (so it returns them too), and only while the release's claim still holds.
/// Its mode is <see cref="GCCollectionMode.Aggressive"/> from 0.5.1, which "Requests that the garbage collector decommit as
/// much memory as possible" (Learn): 0.5.0's Forced collection compacts but can leave most of the emptied regions committed
/// (the installed app held 263.9 MB committed for a 44.6 MB heap at idle), and Aggressive returns them at the cost of a
/// longer pause (about 18 against 7 ms in the platform pair's surrogate, 22 to 35 against 6 to 17 ms in earlier ones). The
/// maintainer approved it as the new default; <see cref="PerfFlags.ForcedIdleGc"/> brings back the Forced collection for
/// one release, for comparison, and changes nothing else.
/// </summary>
/// <remarks>
/// The trigger, the claim and the call site are the release's own: no second collection, no timer, no process-wide GC
/// setting. The runtime accepts Aggressive only for the oldest generation, blocking and compacting (it throws
/// <see cref="ArgumentException"/> otherwise), which is the call made here. Like the Forced collection, it suspends every
/// managed thread while it runs, the hook threads included; a recording that starts after the release's last check can
/// still meet it (see <see cref="IdleModelRelease"/>).
/// </remarks>
public static class IdleCollection
{
    /// <summary>Runs the collection in the mode <paramref name="flags"/> selects.</summary>
    public static IdleCollectionResult Compact(PerfFlags flags) => Compact(flags, SystemIdleCollector.Instance);

    internal static IdleCollectionResult Compact(PerfFlags flags, IIdleCollector collector)
    {
        ArgumentNullException.ThrowIfNull(flags);
        return Compact(aggressive: !flags.IsOn(PerfFlags.ForcedIdleGc), collector);
    }

    internal static IdleCollectionResult Compact(bool aggressive, IIdleCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);

        var (committedBefore, _) = collector.Memory();
        var pauseBefore = collector.TotalPause();
        var started = collector.Timestamp();

        collector.CompactLargeObjectHeapOnce();
        if (aggressive)
        {
            collector.Collect(collector.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        else
        {
            collector.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        var elapsed = collector.ElapsedSince(started);
        var (committedAfter, heapAfter) = collector.Memory();
        return new IdleCollectionResult(
            aggressive ? IdleCollectionMode.Aggressive : IdleCollectionMode.Forced,
            committedBefore,
            committedAfter,
            heapAfter,
            collector.TotalPause() - pauseBefore,
            elapsed);
    }

    private sealed class SystemIdleCollector : IIdleCollector
    {
        public static readonly SystemIdleCollector Instance = new();

        public int MaxGeneration => GC.MaxGeneration;

        public void CompactLargeObjectHeapOnce() =>
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        public void Collect(int generation, GCCollectionMode mode, bool blocking, bool compacting) =>
            GC.Collect(generation, mode, blocking, compacting);

        public (long CommittedBytes, long HeapBytes) Memory()
        {
            var info = GC.GetGCMemoryInfo();
            return (info.TotalCommittedBytes, info.HeapSizeBytes);
        }

        public TimeSpan TotalPause() => GC.GetTotalPauseDuration();

        public long Timestamp() => Stopwatch.GetTimestamp();

        public TimeSpan ElapsedSince(long timestamp) => Stopwatch.GetElapsedTime(timestamp);
    }
}

/// <summary>When a pause change asks for an idle release.</summary>
public static class IdleReleaseRequests
{
    /// <summary>
    /// A pause made while no recording was live asks for the release right away ("Scribe should stand down"), whether or
    /// not the idle countdown is enabled. A pause that stops a recording releases nothing here: that dictation processes
    /// and returns to idle first. A resume only re-arms the countdown. The lifecycle still decides whether a requested
    /// release may run: it refuses while a dictation processes or finishes, while another release runs, and once closing.
    /// </summary>
    public static bool OnPauseChange(bool paused, PauseChange change) =>
        change.Changed && paused && !change.WasRecording;
}
