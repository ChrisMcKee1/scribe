using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// One finished log entry. It carries text and nothing else: never the caller's state object, a
/// scope or an <see cref="System.Diagnostics.Activity"/>, so nothing a caller still owns waits in the queue.
/// </summary>
/// <param name="Timestamp">
/// Local time the entry was logged, already rendered into <paramref name="Text"/>. Notices the writer adds
/// take it from the entry they precede. The day file is chosen when the entry is written, not from this.
/// </param>
/// <param name="Level">Severity, used by the daily budget.</param>
/// <param name="Text">The complete formatted entry, without a trailing newline.</param>
public readonly record struct LogRecord(DateTime Timestamp, LogLevel Level, string Text);

/// <summary>Destination for batches of log entries. Only ever called by one thread at a time.</summary>
public interface ILogRecordSink
{
    /// <summary>
    /// Writes the batch in order. Implementations should absorb their own failures; anything that does
    /// escape costs that batch and nothing else. The list is reused afterwards, so do not keep it.
    /// </summary>
    void Write(IReadOnlyList<LogRecord> batch);
}

/// <summary>Limits for a <see cref="BackgroundLogWriter"/>.</summary>
public sealed record BackgroundLogWriterOptions
{
    /// <summary>Most entries the queue holds before new ones are dropped.</summary>
    public int MaxQueuedRecords { get; init; } = 8192;

    /// <summary>Most characters the queue holds before new entries are dropped.</summary>
    public long MaxQueuedChars { get; init; } = 2L * 1024 * 1024;

    /// <summary>Most entries handed to the sink in one write.</summary>
    public int MaxBatchRecords { get; init; } = 512;

    /// <summary>
    /// How long a prompt entry (a warning or worse, or a session marker) waits to be written. One that meets
    /// a full queue first waits up to this long for room as well, so its caller can wait up to twice this
    /// bound in the worst case: 500 ms with the default.
    /// </summary>
    public TimeSpan PromptWriteTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long <see cref="BackgroundLogWriter.Dispose"/> waits for the queue to drain.</summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tests turn this off to drive batches by hand with <see cref="BackgroundLogWriter.PumpOnce"/>. With
    /// no writer thread, prompt entries do not wait.
    /// </summary>
    internal bool StartWriterThread { get; init; } = true;
}

/// <summary>
/// A bounded in-memory queue with a single background writer, so that a logging call costs its caller
/// the formatting and a short lock rather than a file open, append and close.
/// <para>
/// This is the shape the .NET logging guidance asks for when the store is slow: log methods are
/// synchronous, so add the entry to an in-memory queue and let a background worker write it. The
/// callers that motivated it are the hotkey transition thread and the UI thread, both of which used to
/// wait on the disk for every Debug and Information line.
/// </para>
/// <para>
/// <b>Overflow drops the newest entry</b> and counts it; the next entry that finds space is preceded by
/// one "N log line(s) dropped" notice, so a reader sees exactly where the gap is, and a flush or dispose
/// that arrives first writes the notice itself. <b>Prompt entries</b> (see <see cref="RequiresPromptWrite"/>)
/// additionally wait until they have been written, and for room when the queue is full, each for up to
/// <see cref="BackgroundLogWriterOptions.PromptWriteTimeout"/>, so a caller waits twice that at worst,
/// because the lines that explain a crash are worthless if they die in the queue with it.
/// </para>
/// <para>
/// After <see cref="Dispose"/> entries are written on the caller's thread; if the writer thread was still
/// stuck in a write when the dispose bound ran out, they wait in the queue and it writes them when it
/// returns. No member throws.
/// </para>
/// </summary>
public sealed class BackgroundLogWriter : IDisposable
{
    // Set while this thread is inside the sink. An entry logged from there is queued behind the batch
    // being written; waiting for it, or draining inline, would wait on itself.
    [ThreadStatic]
    private static bool t_inSink;

    private readonly ILogRecordSink _sink;
    private readonly BackgroundLogWriterOptions _options;
    private readonly int _maxBatch;

    // Guards the queue and the counters. Never held across a sink write.
    private readonly object _gate = new();

    // Serializes sink writes between the writer thread and inline drains after disposal, and is taken
    // before _gate so the order entries leave the queue is the order they are written.
    private readonly object _sinkGate = new();

    private readonly Queue<LogRecord> _queue = new();
    private readonly List<LogRecord> _batch;
    private readonly Thread? _writerThread;

    private long _queuedChars;
    private long _accepted;
    private long _taken;
    private long _completed;
    private int _droppedSinceNotice;
    private long _droppedTotal;
    private DateTime _lastDropTimestamp;
    private bool _writerWaiting;
    private int _completionWaiters;
    private bool _stopping;
    private bool _synchronous;
    private bool _disposed;

    public BackgroundLogWriter(ILogRecordSink sink, BackgroundLogWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new BackgroundLogWriterOptions();
        _maxBatch = Math.Max(1, _options.MaxBatchRecords);
        _batch = new List<LogRecord>(Math.Min(_maxBatch, 1024));

        if (!_options.StartWriterThread)
        {
            return;
        }

        try
        {
            var thread = new Thread(RunWriter)
            {
                IsBackground = true,
                Name = "Scribe log writer",
            };
            thread.Start();
            _writerThread = thread;
        }
        catch (Exception)
        {
            // Without a writer thread every entry is written on its caller's thread, which is how this
            // sink used to behave: slower for the caller, but nothing is lost.
            _synchronous = true;
        }
    }

    /// <summary>Entries dropped because the queue was full, over the writer's lifetime.</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _droppedTotal; } }
    }

    /// <summary>Entries accepted but not yet handed to the sink.</summary>
    internal int QueuedCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    /// <summary>Callers currently waiting for a write to finish or for room in the queue.</summary>
    internal int PromptWaiterCount
    {
        get { lock (_gate) { return _completionWaiters; } }
    }

    /// <summary>True once <see cref="Dispose"/> has asked the writer thread to stop.</summary>
    internal bool IsStopping
    {
        get { lock (_gate) { return _stopping; } }
    }

    /// <summary>
    /// Whether an entry must be on disk before its logging call returns. Warning and above, because
    /// those are the lines that explain a crash; and the session start and end markers, because whether
    /// the end marker made it to the file is how a reader tells an orderly quit from a death.
    /// </summary>
    public static bool RequiresPromptWrite(LogLevel level, string? message) =>
        (level >= LogLevel.Warning && level < LogLevel.None) || SessionBanner.IsSessionMarker(message);

    /// <summary>
    /// Queues one entry. A <paramref name="prompt"/> entry also waits until it has been written, and first
    /// for room if the queue is full, each for up to <see cref="BackgroundLogWriterOptions.PromptWriteTimeout"/>;
    /// any other entry returns as soon as it is queued or dropped. Never throws.
    /// </summary>
    public void Write(LogRecord record, bool prompt = false)
    {
        if (record.Text is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                // Only a prompt entry waits for room, and only for its usual short bound: a warning is
                // worth a moment, a Debug line never is. Then the newest entry is the one dropped.
                var waitsForRoom = prompt && !t_inSink && !_synchronous && _writerThread is not null;
                if (!Fits(record.Text.Length) &&
                    !(waitsForRoom && WaitForRoom(record.Text.Length, _options.PromptWriteTimeout)))
                {
                    _droppedSinceNotice++;
                    _droppedTotal++;
                    _lastDropTimestamp = record.Timestamp;
                    return;
                }

                Accept(record);

                if (t_inSink)
                {
                    return;
                }

                if (!_synchronous)
                {
                    if (_writerWaiting)
                    {
                        // PulseAll, not Pulse: flush waiters share this monitor, and waking one of them
                        // instead of the writer would leave the entry sitting in the queue.
                        Monitor.PulseAll(_gate);
                    }

                    if (!prompt || _writerThread is null)
                    {
                        return;
                    }

                    if (WaitForCompletion(_accepted, _options.PromptWriteTimeout) || !_synchronous)
                    {
                        return;
                    }
                }
            }

            // Synchronous mode: after disposal, or if the writer thread could not run.
            DrainInline(_options.PromptWriteTimeout);
        }
        catch (Exception)
        {
            // Logging must never throw into its caller.
        }
    }

    /// <summary>
    /// Waits, up to <paramref name="timeout"/>, until every entry queued before this call has been
    /// written. Returns false if the bound ran out first. A pending dropped-lines notice is queued first,
    /// so a flush at process exit or after a crash never leaves a gap unexplained. Never throws.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        try
        {
            if (t_inSink)
            {
                return false;
            }

            lock (_gate)
            {
                EnqueuePendingDropNotice();
                if (!_synchronous && _writerThread is not null)
                {
                    if (_writerWaiting)
                    {
                        Monitor.PulseAll(_gate);
                    }

                    if (WaitForCompletion(_accepted, timeout))
                    {
                        return true;
                    }

                    if (!_synchronous)
                    {
                        return false;
                    }
                }
            }

            return DrainInline(timeout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes one batch on the calling thread. For tests that run without a writer thread.</summary>
    internal bool PumpOnce()
    {
        lock (_sinkGate)
        {
            return WriteNextBatch();
        }
    }

    /// <summary>
    /// Drains the queue within <see cref="BackgroundLogWriterOptions.DisposeTimeout"/> and stops the
    /// writer thread, queueing a pending dropped-lines notice first. Entries logged afterwards are written
    /// on their caller's thread. Never throws.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            EnqueuePendingDropNotice();
            _disposed = true;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }

        try
        {
            // The writer drains what is queued and then exits, so the join is the bounded flush.
            _writerThread?.Join(_options.DisposeTimeout);
        }
        catch (Exception)
        {
            // A failed join changes nothing below: the sink gate still keeps writers apart.
        }

        lock (_gate)
        {
            _synchronous = true;
            Monitor.PulseAll(_gate);
        }

        // Entries accepted between the stop request and the switch above, if the writer had already
        // gone. Bounded: a writer wedged in a write still holds the sink gate.
        try
        {
            DrainInline(_options.PromptWriteTimeout);
        }
        catch (Exception)
        {
            // Nothing more can be done for them.
        }
    }

    // Caller holds _gate. An empty queue takes any single entry, however long, so one oversized crash
    // report is never turned away just because it exceeds the character bound on its own.
    private bool Fits(int length) =>
        _queue.Count == 0 ||
        (_queue.Count < _options.MaxQueuedRecords && _queuedChars + length <= _options.MaxQueuedChars);

    // Caller holds _gate and has checked Fits.
    private void Accept(LogRecord record)
    {
        if (_droppedSinceNotice > 0)
        {
            // Space is back. The notice goes in ahead of the entry that found it, which puts it exactly
            // where the gap is. It is exempt from the bound: one short line.
            Enqueue(DropNotice(record.Timestamp, _droppedSinceNotice));
            _droppedSinceNotice = 0;
        }

        Enqueue(record);
    }

    // Caller holds _gate. For a flush or dispose that arrives while drops are still unannounced: nothing has
    // been accepted since them, so the end of the queue is exactly where the gap is.
    private void EnqueuePendingDropNotice()
    {
        if (_droppedSinceNotice > 0)
        {
            Enqueue(DropNotice(_lastDropTimestamp, _droppedSinceNotice));
            _droppedSinceNotice = 0;
        }
    }

    // Caller holds _gate. Woken by the writer as it takes and finishes batches.
    private bool WaitForRoom(int length, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + ToWaitMilliseconds(timeout);
        _completionWaiters++;
        try
        {
            while (!Fits(length) && !_synchronous)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
            }

            return Fits(length);
        }
        finally
        {
            _completionWaiters--;
        }
    }

    // Caller holds _gate.
    private void Enqueue(LogRecord record)
    {
        _queue.Enqueue(record);
        _queuedChars += record.Text.Length;
        _accepted++;
    }

    // Negative (including Timeout.InfiniteTimeSpan) means "do not wait": no path here may block forever.
    private static int ToWaitMilliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

    private static LogRecord DropNotice(DateTime timestamp, int dropped)
    {
        var message = string.Create(CultureInfo.InvariantCulture,
            $"{dropped} log line(s) dropped: the log writer fell behind and its queue was full.");
        return new LogRecord(
            timestamp,
            LogLevel.Warning,
            LogLineFormat.Format(timestamp, LogLevel.Warning, LogLineFormat.WriterCategory, message));
    }

    // Caller holds _gate. Monitor.Wait releases it while waiting, so the writer can make progress.
    private bool WaitForCompletion(long target, TimeSpan timeout)
    {
        if (_completed >= target)
        {
            return true;
        }

        var deadline = Environment.TickCount64 + ToWaitMilliseconds(timeout);
        _completionWaiters++;
        try
        {
            while (_completed < target && !_synchronous)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
            }

            return _completed >= target;
        }
        finally
        {
            _completionWaiters--;
        }
    }

    private bool DrainInline(TimeSpan timeout)
    {
        if (t_inSink || !Monitor.TryEnter(_sinkGate, ToWaitMilliseconds(timeout)))
        {
            return false;
        }

        try
        {
            while (WriteNextBatch())
            {
            }

            return true;
        }
        finally
        {
            Monitor.Exit(_sinkGate);
        }
    }

    // Caller holds _sinkGate, which is what keeps batches in queue order.
    private bool WriteNextBatch()
    {
        long batchEnd;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return false;
            }

            while (_batch.Count < _maxBatch && _queue.TryDequeue(out var next))
            {
                _batch.Add(next);
                _queuedChars -= next.Text.Length;
            }

            _taken += _batch.Count;
            batchEnd = _taken;

            // Room just opened up; a prompt entry may be waiting for it.
            if (_completionWaiters > 0)
            {
                Monitor.PulseAll(_gate);
            }
        }

        t_inSink = true;
        try
        {
            _sink.Write(_batch);
        }
        catch (Exception)
        {
            // A failing sink costs this batch, never the writer or the process.
        }
        finally
        {
            t_inSink = false;

            // Written or not, these entries are finished; do not keep their text alive.
            _batch.Clear();
        }

        lock (_gate)
        {
            _completed = batchEnd;
            if (_completionWaiters > 0)
            {
                Monitor.PulseAll(_gate);
            }
        }

        return true;
    }

    private void RunWriter()
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    while (_queue.Count == 0)
                    {
                        if (_stopping)
                        {
                            return;
                        }

                        _writerWaiting = true;
                        try
                        {
                            Monitor.Wait(_gate);
                        }
                        finally
                        {
                            _writerWaiting = false;
                        }
                    }
                }

                lock (_sinkGate)
                {
                    WriteNextBatch();
                }
            }
        }
        catch (Exception)
        {
            // The batch write is already guarded, so this is a defect in the loop itself. Degrade to
            // writing on callers' threads rather than leaving entries in a queue nobody drains.
            try
            {
                lock (_gate)
                {
                    _synchronous = true;
                    Monitor.PulseAll(_gate);
                }
            }
            catch (Exception)
            {
                // Nothing further is possible.
            }
        }
    }
}
