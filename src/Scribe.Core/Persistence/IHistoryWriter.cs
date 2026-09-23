using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>
/// Commits dictation history in the background, one write at a time, in the order the writes were queued.
/// </summary>
public interface IHistoryWriter
{
    /// <summary>
    /// Queues one history entry, with its optional captured audio, for an ordered background commit. Every field, the
    /// timestamp included, is taken exactly as given at this call, and the writer takes ownership of
    /// <paramref name="audio"/>: the caller must not reuse or mutate it afterwards.
    /// </summary>
    /// <remarks>
    /// Normally returns at once. It blocks only while one write is committing and another is already waiting, which
    /// bounds the captures the writer retains to two, so the only thread it can ever hold up is the producer's own, and
    /// then only for a bounded time: when no room appears within the writer's wait bound, this entry is dropped with a
    /// warning and the call returns false, so history persistence can never wedge dictation. Also returns false, and
    /// records nothing, once the writer has closed. Never throws for a persistence failure: a failed write is logged as
    /// a shape and dropped, with no retry.
    /// </remarks>
    /// <param name="dictationId">The per-dictation ordinal stamped on the writer's log lines. Diagnostic only.</param>
    bool Enqueue(HistoryEntry entry, CapturedAudio? audio, long dictationId = 0);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> until every write accepted before this call has finished (committed,
    /// failed, dropped or abandoned). Writes accepted after the call are not waited for. Returns false when the wait
    /// timed out.
    /// </summary>
    bool WaitForAcceptedWrites(TimeSpan timeout);

    /// <summary>
    /// Stops accepting writes and waits up to <paramref name="timeout"/> for the accepted ones to finish. Writes still
    /// queued behind a write that outlives the wait are abandoned rather than committed later. Idempotent.
    /// </summary>
    HistoryDrainResult Complete(TimeSpan timeout);
}

/// <summary>What a <see cref="IHistoryWriter.Complete"/> call left behind.</summary>
/// <param name="Drained">Nothing accepted is still queued or committing.</param>
/// <param name="StillWriting">
/// Writes that were still executing when the wait ended (at most one). They run to completion on their own.
/// </param>
/// <param name="Abandoned">
/// Accepted writes that will never be committed: queued behind a write that outlived a wait, or still waiting for room
/// when the writer closed.
/// </param>
public readonly record struct HistoryDrainResult(bool Drained, int StillWriting, int Abandoned);
