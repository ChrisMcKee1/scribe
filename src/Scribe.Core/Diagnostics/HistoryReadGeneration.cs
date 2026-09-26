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

    public HistoryReadCompletion CompleteRead(long generation, bool requestStillCurrent, bool retryWhenStale)
    {
        if (!requestStillCurrent)
        {
            return HistoryReadCompletion.Drop;
        }

        if (IsCurrent(generation))
        {
            return HistoryReadCompletion.Publish;
        }

        return retryWhenStale ? HistoryReadCompletion.Retry : HistoryReadCompletion.Drop;
    }

    public long AdvanceForDeletion() => Interlocked.Increment(ref _generation);

    public long CompleteRatingWrite(bool saved) => Interlocked.Increment(ref _generation);
}

public enum HistoryReadCompletion
{
    Publish,
    Retry,
    Drop,
}
