namespace Scribe.Core.Cleanup;

/// <summary>How a cleanup service's disposal ended.</summary>
internal enum CleanupDisposalOutcome
{
    NotDisposed,

    /// <summary>Every admitted operation finished, then the shared resources were released.</summary>
    Released,

    /// <summary>
    /// An operation outlived the drain timeout, so nothing it might still be using was disposed.
    /// </summary>
    LeftToProcessExit,
}

/// <summary>
/// Counts the operations that may still be using the cleanup service's shared resources, and lets
/// disposal close admission and then wait for every admitted one to finish.
/// </summary>
/// <remarks>
/// This is deliberately a counter rather than a lock. Holding a lock for the lifetime of every
/// operation would serialize a model-list read behind a multi-gigabyte model download; counting
/// only has to answer "may the shared resources be released yet", which does not need the
/// operations to exclude each other. Admission and closing are atomic with respect to each other,
/// so an operation is either admitted before close (and waited for) or refused (and never touches
/// anything).
/// </remarks>
internal sealed class CleanupOperationTracker
{
    private readonly object _sync = new();
    private int _active;
    private bool _closed;
    private TaskCompletionSource? _drained;

    /// <summary>True once <see cref="Close"/> has run. Admission is refused from then on.</summary>
    public bool IsClosed
    {
        get { lock (_sync) { return _closed; } }
    }

    /// <summary>How many admitted operations have not yet finished.</summary>
    public int ActiveCount
    {
        get { lock (_sync) { return _active; } }
    }

    /// <summary>
    /// Admits one operation, or returns null once closed. Dispose the lease exactly when the
    /// operation has stopped using shared resources; disposing it twice is harmless.
    /// </summary>
    public Lease? TryEnter()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return null;
            }

            _active++;
            return new Lease(this);
        }
    }

    /// <summary>
    /// Closes admission and returns a task that completes when every admitted operation has
    /// released its lease. Idempotent: later calls return the same drain.
    /// </summary>
    public Task Close()
    {
        lock (_sync)
        {
            _closed = true;
            if (_active == 0)
            {
                return Task.CompletedTask;
            }

            // Asynchronous continuations, because the last Exit happens on whatever thread finished
            // the last operation, and the disposer's continuation must not run inline on it.
            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _active--;
            if (_closed && _active == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    /// <summary>One admitted operation. Releases its slot exactly once.</summary>
    public sealed class Lease : IDisposable
    {
        private CleanupOperationTracker? _owner;

        internal Lease(CleanupOperationTracker owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
