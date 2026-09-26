namespace Scribe.Core.Diagnostics;

/// <summary>
/// Shared mutation generation for History reads. A read may publish only while no completed
/// mutation that can change shown rows has happened since it started.
/// </summary>
public sealed class HistoryReadGeneration
{
    /// <summary>
    /// How many times a read that a mutation made stale starts again on its own before the page stops and offers Try
    /// again. Without a bound, deletions arriving during every read (a retention pass deletes in slices) would keep the
    /// page rereading history and never showing it.
    /// </summary>
    public const int MaxAutomaticRetries = 3;

    private long _generation;

    public long Capture() => Volatile.Read(ref _generation);

    public bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);

    /// <summary>
    /// What to do with a finished read. <paramref name="retriesSoFar"/> counts the automatic retries this logical request
    /// has already made; once it reaches <see cref="MaxAutomaticRetries"/> a stale read gives up instead of retrying.
    /// </summary>
    public HistoryReadCompletion CompleteRead(long generation, bool requestStillCurrent, bool retryWhenStale, int retriesSoFar = 0)
    {
        if (!requestStillCurrent)
        {
            return HistoryReadCompletion.Drop;
        }

        if (IsCurrent(generation))
        {
            return HistoryReadCompletion.Publish;
        }

        if (!retryWhenStale)
        {
            return HistoryReadCompletion.Drop;
        }

        return retriesSoFar < MaxAutomaticRetries ? HistoryReadCompletion.Retry : HistoryReadCompletion.GiveUp;
    }

    /// <summary>
    /// How long automatic retry number <paramref name="retry"/> (1 for the first) waits before it reads again: 150, 300,
    /// then 600 ms, so a burst of deletions can finish before the next read starts.
    /// </summary>
    public static TimeSpan RetryDelay(int retry) =>
        TimeSpan.FromMilliseconds(150 * (1 << Math.Clamp(retry - 1, 0, MaxAutomaticRetries - 1)));

    public long AdvanceForDeletion() => Interlocked.Increment(ref _generation);

    public long CompleteRatingWrite(bool saved) => Interlocked.Increment(ref _generation);
}

public enum HistoryReadCompletion
{
    Publish,
    Retry,
    Drop,

    /// <summary>Still stale after <see cref="HistoryReadGeneration.MaxAutomaticRetries"/> retries: keep what the page shows and offer Try again.</summary>
    GiveUp,
}
