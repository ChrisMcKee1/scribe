namespace Scribe.Core.Lifecycle;

/// <summary>
/// Counts background work whose completion an owner must observe before it releases what that work uses.
/// </summary>
/// <remarks>
/// Completion is tracked apart from any state the work publishes on its way out, on purpose. The dictation controller
/// used to clear its record of the processing task in the same step that published Idle, before that task's own
/// cleanup had finished, so a Dispose landing in that moment saw no work at all and tore dependencies down underneath
/// it. Here a lease is released only by the work itself, as its very last step, and closing stops admission without
/// forgetting anything already admitted.
/// </remarks>
public sealed class InFlightWork
{
    private readonly object _sync = new();
    private int _running;
    private int _faulted;
    private bool _closed;

    /// <summary>Units admitted and not yet completed.</summary>
    public int Running
    {
        get { lock (_sync) { return _running; } }
    }

    /// <summary>True once <see cref="Close"/> has run.</summary>
    public bool IsClosed
    {
        get { lock (_sync) { return _closed; } }
    }

    /// <summary>
    /// Admits one unit of work, or returns null once <see cref="Close"/> has run, so nothing new starts during
    /// teardown. The caller must complete the returned lease exactly when the work has fully finished.
    /// </summary>
    public Lease? TryBegin()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return null;
            }

            _running++;
            return new Lease(this);
        }
    }

    /// <summary>
    /// Stops admitting work. Work already admitted keeps running and is still observed by
    /// <see cref="WaitForDrain"/>. Idempotent.
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for every admitted unit to complete. Never throws for the work's own
    /// failures: those are reported as a count, because the caller is usually a teardown path that must keep going.
    /// </summary>
    public WorkDrainResult WaitForDrain(TimeSpan timeout)
    {
        lock (_sync)
        {
            var drained = MonitorWait.Until(_sync, () => _running == 0, timeout);
            return new WorkDrainResult(drained, _running, _faulted);
        }
    }

    private void End(bool faulted)
    {
        lock (_sync)
        {
            _running--;
            if (faulted)
            {
                _faulted++;
            }

            if (_running == 0)
            {
                Monitor.PulseAll(_sync);
            }
        }
    }

    /// <summary>One admitted unit of work.</summary>
    public sealed class Lease
    {
        private InFlightWork? _owner;

        internal Lease(InFlightWork owner) => _owner = owner;

        /// <summary>
        /// Marks the unit finished, recording whether it ended in an unexpected fault. Only the first call counts.
        /// </summary>
        public void Complete(bool faulted = false) => Interlocked.Exchange(ref _owner, null)?.End(faulted);
    }
}

/// <summary>What <see cref="InFlightWork.WaitForDrain"/> observed.</summary>
/// <param name="Drained">Every admitted unit completed within the wait.</param>
/// <param name="StillRunning">Units still running when the wait ended.</param>
/// <param name="Faulted">Units that have completed with an unexpected fault since the tracker was created.</param>
public readonly record struct WorkDrainResult(bool Drained, int StillRunning, int Faulted);
