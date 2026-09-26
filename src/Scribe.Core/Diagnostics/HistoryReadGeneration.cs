namespace Scribe.Core.Diagnostics;

/// <summary>
/// Shared mutation generation for History reads. A read may publish only while no completed
/// mutation that can change shown rows has happened since it started.
/// </summary>
public sealed class HistoryReadGeneration
{
    private long _generation;

    public long Capture() => Volatile.Read(ref _generation);

    public bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);

    public long AdvanceForDeletion() => Interlocked.Increment(ref _generation);

    public long CompleteRatingWrite(bool saved) =>
        saved ? Interlocked.Increment(ref _generation) : Volatile.Read(ref _generation);
}
