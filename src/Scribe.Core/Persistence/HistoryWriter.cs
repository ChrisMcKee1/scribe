using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="IHistoryWriter"/>
/// <remarks>
/// <para>
/// History used to be written synchronously at the end of every dictation, so the controller stayed Processing until
/// SQLite committed and the next press was rejected in the meantime. The dictation now hands its entry here and
/// returns to idle; one consumer commits entries in order through <see cref="HistoryRepository.Add(HistoryEntry, CapturedAudio?)"/>,
/// which keeps the optional audio blob and its history row in one transaction, exactly as before. A history row is
/// still durable only once that transaction commits.
/// </para>
/// <para>
/// A write counts as accepted from the moment its <see cref="Enqueue"/> call begins on an open writer, including while
/// it waits for room. <see cref="WaitForAcceptedWrites"/> is the barrier <see cref="OrderedHistoryRepository"/> uses so
/// that Clear removes everything dictated before the click and a read sees its own writes.
/// </para>
/// <para>
/// History must never wedge dictation, so the wait for room is bounded: a producer that finds no room within
/// <see cref="ProducerWaitBound"/> drops its own entry, logs that as a shape, and lets the dictation carry on.
/// </para>
/// </remarks>
public sealed class HistoryWriter : IHistoryWriter, IDisposable
{
    /// <summary>
    /// Writes held at once: one committing and one waiting behind it. A producer that finds both slots taken waits for
    /// room, which bounds the captures the writer retains to two. The bound is a count, not a size: with
    /// MaxDictationMinutes set to 0 a single capture is unbounded in length, by the user's own choice.
    /// </summary>
    public const int Capacity = 2;

    /// <summary>
    /// How long a producer waits for room before it drops its entry.
    /// </summary>
    /// <remarks>
    /// The producer is the processing thread of a dictation whose text is already in the user's document, and while it
    /// waits the controller stays Processing and turns the next press away. Finding no room means two earlier writes
    /// are outstanding, so the database has already failed to commit for a whole dictation cycle; a healthy commit takes
    /// milliseconds. Five seconds sits far above that and above ordinary transient locks (an antivirus scan, storage
    /// maintenance yielding to dictation), matches the controller's own shutdown wait for processing, and is short
    /// enough that a user who presses again after a stalled dictation is not turned away for long.
    /// </remarks>
    public static readonly TimeSpan ProducerWaitBound = TimeSpan.FromSeconds(5);

    // Dispose runs when the host tears the container down. The controller has normally drained the writer already, so
    // this only matters when a write outlived that drain: it keeps the database from being disposed under it.
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly IHistoryRepository _inner;
    private readonly ILogger<HistoryWriter> _logger;
    private readonly TimeSpan _producerWaitBound;
    private readonly TimeSpan _disposeTimeout;
    private readonly object _sync = new();
    private readonly Queue<PendingWrite> _queue = new();

    // Every ticket accepted and not yet finished: waiting for room, queued, or being written. Its lowest member is the
    // barrier's watermark. A ticket can finish out of order (a producer that gives up waiting leaves before the writes
    // ahead of it commit), which is why this is a set and not a single "settled up to" counter.
    private readonly SortedSet<long> _unfinished = [];

    // Producers still waiting for room, admitted lowest ticket first so commits keep call order.
    private readonly SortedSet<long> _waiting = [];

    private long _issued;
    private bool _consumerActive;
    private bool _inFlight;
    private bool _committing;
    private bool _closed;
    private bool _disposed;
    private bool _dropQueued;
    private int _gaveUpAtClose;
    private int _abandonedQueued;
    private int _droppedForRoom;

    public HistoryWriter(IHistoryRepository inner, ILogger<HistoryWriter> logger)
        : this(inner, logger, ProducerWaitBound, DefaultDisposeTimeout)
    {
    }

    /// <summary>Test seam: the production bounds are real time, which a deterministic test must not depend on.</summary>
    internal HistoryWriter(
        IHistoryRepository inner, ILogger<HistoryWriter> logger, TimeSpan producerWaitBound, TimeSpan disposeTimeout)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _logger = logger;
        _producerWaitBound = producerWaitBound;
        _disposeTimeout = disposeTimeout;
    }

    /// <summary>Test hook: runs under the writer's lock when a producer first has to wait for room.</summary>
    internal Action? ProducerWaiting { get; set; }

    /// <summary>Test hook: runs under the writer's lock when a barrier caller first has to wait for writes.</summary>
    internal Action? BarrierWaiting { get; set; }

    /// <summary>Test hook: runs under the writer's lock when a completion first has to wait for writes.</summary>
    internal Action? CompletionWaiting { get; set; }

    public bool Enqueue(HistoryEntry entry, CapturedAudio? audio, long dictationId = 0)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var queuedAt = Stopwatch.GetTimestamp();
        var outcome = EnqueueOutcome.Refused;
        var waitedForRoom = false;
        var startConsumer = false;
        var outstanding = 0;
        var droppedForRoom = 0;
        lock (_sync)
        {
            if (!_closed)
            {
                var ticket = ++_issued;
                _unfinished.Add(ticket);
                _waiting.Add(ticket);
                MonitorWait.Until(
                    _sync,
                    () => _closed || CanAdmit(ticket),
                    _producerWaitBound,
                    () =>
                    {
                        waitedForRoom = true;
                        ProducerWaiting?.Invoke();
                    });

                var admit = !_closed && CanAdmit(ticket);
                _waiting.Remove(ticket);
                if (admit)
                {
                    _queue.Enqueue(new PendingWrite(ticket, entry, audio, dictationId, queuedAt));
                    startConsumer = !_consumerActive;
                    _consumerActive = true;
                    outcome = EnqueueOutcome.Accepted;
                }
                else
                {
                    // Closing counted this producer as abandoned already; a timeout is this producer's own decision.
                    _unfinished.Remove(ticket);
                    if (!_closed)
                    {
                        droppedForRoom = ++_droppedForRoom;
                        outstanding = Outstanding;
                        outcome = EnqueueOutcome.TimedOut;
                    }
                }

                // Leaving the line in either direction can put the next producer at its head, and can move the watermark
                // a barrier is waiting on.
                Monitor.PulseAll(_sync);
            }
        }

        switch (outcome)
        {
            case EnqueueOutcome.Refused:
                TryLog(logger => logger.LogWarning(
                    "#{Id} dictation history was not recorded: the history writer has shut down.", dictationId));
                return false;

            case EnqueueOutcome.TimedOut:
                TryLog(logger => logger.LogWarning(
                    "#{Id} dictation history was not recorded: no room appeared within {BoundMs} ms because " +
                    "{Outstanding} earlier writes were still outstanding. {Dropped} history entries were dropped this " +
                    "way this session; dictation itself was not affected.",
                    dictationId, (long)_producerWaitBound.TotalMilliseconds, outstanding, droppedForRoom));
                return false;
        }

        if (waitedForRoom)
        {
            TryLog(logger => logger.LogInformation(
                "#{Id} history waited {Ms} ms for room: {Capacity} earlier writes were still outstanding.",
                dictationId, (long)Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds, Capacity));
        }

        if (startConsumer)
        {
            _ = Task.Run(Consume);
        }

        return true;
    }

    public bool WaitForAcceptedWrites(TimeSpan timeout)
    {
        lock (_sync)
        {
            var target = _issued;
            return MonitorWait.Until(
                _sync, () => _unfinished.Count == 0 || _unfinished.Min > target, timeout, BarrierWaiting);
        }
    }

    public HistoryDrainResult Complete(TimeSpan timeout)
    {
        lock (_sync)
        {
            if (!_closed)
            {
                _closed = true;

                // Producers still waiting for room can no longer be admitted; they wake, see the writer closed and
                // return false.
                _gaveUpAtClose = _waiting.Count;
                Monitor.PulseAll(_sync);
            }

            if (MonitorWait.Until(_sync, () => _queue.Count == 0 && !_inFlight, timeout, CompletionWaiting))
            {
                return new HistoryDrainResult(true, 0, _gaveUpAtClose + _abandonedQueued);
            }

            // Whatever is still queued behind a write that outlived the wait is dropped when that write returns, so it
            // can never commit after the owner has moved on to disposing the database.
            _dropQueued = true;
            return new HistoryDrainResult(
                false, _committing ? 1 : 0, _gaveUpAtClose + _abandonedQueued + _queue.Count);
        }
    }

    /// <summary>
    /// Completes the writer with a bounded wait, once. The container can dispose this singleton more than once (it is
    /// also registered as <see cref="IHistoryWriter"/>), and a second call must not wait on a stuck write all over again.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        var result = Complete(_disposeTimeout);
        if (!result.Drained)
        {
            TryLog(logger => logger.LogWarning(
                "History writer disposed with {StillWriting} write still committing and {Abandoned} abandoned; the " +
                "committing write was left to finish on its own.",
                result.StillWriting, result.Abandoned));
        }
    }

    // Caller holds _sync. Writes admitted and not yet finished, the one being written included.
    private int Outstanding => _queue.Count + (_inFlight ? 1 : 0);

    // Caller holds _sync.
    private bool CanAdmit(long ticket) => _waiting.Min == ticket && Outstanding < Capacity;

    private void Consume()
    {
        while (true)
        {
            PendingWrite item;
            bool drop;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    _consumerActive = false;
                    return;
                }

                item = _queue.Dequeue();
                drop = _dropQueued;
                _inFlight = true;
                _committing = !drop;
            }

            var started = Stopwatch.GetTimestamp();
            Exception? failure = null;
            if (!drop)
            {
                try
                {
                    _inner.Add(item.Entry, item.Audio);
                }
                catch (Exception ex)
                {
                    // Isolated to this entry: the dictation it belongs to already finished, and the next write still runs.
                    failure = ex;
                }
            }

            // Reported before the write counts as finished, so anything that waited for it finds its outcome already in
            // the log.
            Report(item, drop, failure, Stopwatch.GetElapsedTime(started));

            lock (_sync)
            {
                _inFlight = false;
                _committing = false;
                _unfinished.Remove(item.Ticket);
                if (drop)
                {
                    _abandonedQueued++;
                }

                Monitor.PulseAll(_sync);
            }
        }
    }

    // Shapes only: counts, durations and error codes. An entry's text never reaches the log, and neither does an
    // exception message, which a future inner failure could build from the very content being written.
    private void Report(PendingWrite item, bool dropped, Exception? failure, TimeSpan writeTime)
    {
        if (dropped)
        {
            TryLog(logger => logger.LogWarning(
                "#{Id} history write was abandoned at shutdown; the entry was not recorded.", item.DictationId));
            return;
        }

        if (failure is not null)
        {
            var sqlite = failure as SqliteException;
            TryLog(logger => logger.LogWarning(
                "#{Id} history write failed after {WriteMs} ms ({Error}, SQLite code {Code}/{ExtendedCode}, " +
                "audio={HasAudio}); the entry was not recorded.",
                item.DictationId,
                (long)writeTime.TotalMilliseconds,
                failure.GetType().Name,
                sqlite?.SqliteErrorCode,
                sqlite?.SqliteExtendedErrorCode,
                item.Audio is not null));
            return;
        }

        TryLog(logger => logger.LogDebug(
            "#{Id} history committed {TotalMs} ms after it was queued (write {WriteMs} ms, audio={HasAudio}).",
            item.DictationId,
            (long)Stopwatch.GetElapsedTime(item.QueuedTimestamp).TotalMilliseconds,
            (long)writeTime.TotalMilliseconds,
            item.Audio is not null));
    }

    // Diagnostics must never take down the writer they describe: a throwing log call here would kill the consumer and
    // strand every write queued behind it.
    private void TryLog(Action<ILogger> write)
    {
        try
        {
            write(_logger);
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }

    private enum EnqueueOutcome
    {
        Refused,
        TimedOut,
        Accepted,
    }

    private sealed record PendingWrite(
        long Ticket,
        HistoryEntry Entry,
        CapturedAudio? Audio,
        long DictationId,
        long QueuedTimestamp);
}
