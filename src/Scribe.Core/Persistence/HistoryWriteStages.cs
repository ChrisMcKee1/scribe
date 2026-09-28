using System.Diagnostics;

namespace Scribe.Core.Persistence;

/// <summary>
/// Where one history write's time went (DATA-O-08 and DATA-A-01, <see cref="Diagnostics.PerfFlags.HistoryStageTiming"/>):
/// a timestamp at each step of <see cref="HistoryRepository.Add(Models.HistoryEntry, Models.CapturedAudio?)"/>, plus the
/// garbage collections that ran meanwhile. One object per write, filled on the writing thread only and read once that
/// write has returned, so no value is ever another write's. Numbers only.
/// </summary>
internal sealed class HistoryWriteStages
{
    private int _gen0;
    private int _gen1;
    private int _gen2;

    internal long Started { get; private set; }

    internal long GateEntered { get; private set; }

    internal long Opened { get; private set; }

    internal long Began { get; private set; }

    internal long BlobColumnsChecked { get; private set; }

    internal long BlobEncoded { get; private set; }

    internal long BlobInserted { get; private set; }

    internal long BlobBytes { get; private set; }

    internal long ColumnsChecked { get; private set; }

    internal long Inserted { get; private set; }

    internal long Committed { get; private set; }

    internal long Closed { get; private set; }

    internal int Gen0Collections { get; private set; }

    internal int Gen1Collections { get; private set; }

    internal int Gen2Collections { get; private set; }

    internal void Start()
    {
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        Started = Stopwatch.GetTimestamp();
    }

    internal void MarkGateEntered() => GateEntered = Stopwatch.GetTimestamp();

    internal void MarkOpened() => Opened = Stopwatch.GetTimestamp();

    internal void MarkBegan() => Began = Stopwatch.GetTimestamp();

    internal void MarkBlobColumnsChecked() => BlobColumnsChecked = Stopwatch.GetTimestamp();

    internal void MarkBlobEncoded(long bytes)
    {
        BlobEncoded = Stopwatch.GetTimestamp();
        BlobBytes = bytes;
    }

    internal void MarkBlobInserted() => BlobInserted = Stopwatch.GetTimestamp();

    internal void MarkColumnsChecked() => ColumnsChecked = Stopwatch.GetTimestamp();

    internal void MarkInserted() => Inserted = Stopwatch.GetTimestamp();

    internal void MarkCommitted() => Committed = Stopwatch.GetTimestamp();

    internal void MarkClosed()
    {
        Closed = Stopwatch.GetTimestamp();
        Gen0Collections = GC.CollectionCount(0) - _gen0;
        Gen1Collections = GC.CollectionCount(1) - _gen1;
        Gen2Collections = GC.CollectionCount(2) - _gen2;
    }

    /// <summary>Microseconds from <paramref name="from"/> to <paramref name="to"/>; 0 when either step did not run.</summary>
    internal static long Microseconds(long from, long to) =>
        from == 0 || to == 0 || to < from ? 0 : (to - from) * 1_000_000 / Stopwatch.Frequency;
}
