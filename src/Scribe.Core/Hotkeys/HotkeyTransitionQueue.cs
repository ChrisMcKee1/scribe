namespace Scribe.Core.Hotkeys;

/// <summary>
/// Carries hotkey transitions from the hook thread (and from state changes that end a dictation) to
/// the dispatch thread.
///
/// <see cref="TryEnqueue"/> never waits and never throws, so it is safe inside the low-level hook
/// callback. Items travel through a <see cref="LockFreeInbox{T}"/> and the dispatcher is woken with a
/// kernel event: <c>EventWaitHandle.Set</c> is a single SetEvent call, whereas
/// <c>ManualResetEventSlim</c> and <c>SemaphoreSlim</c> both signal under a managed monitor that the
/// waiting thread also takes.
/// </summary>
internal sealed class HotkeyTransitionQueue : IDisposable
{
    private readonly LockFreeInbox<HotkeyService.QueuedTransition> _items = new();
    private readonly AutoResetEvent _work = new(false);
    private volatile bool _completed;

    // Every queued transition that will ask the consumer for a leaked-key repair (a key transition's Deactivated), counted
    // on the queuing thread as it is queued; for tests.
    private long _repairRequests;

    public bool IsCompleted => _completed;

    /// <summary>Any thread: how many queued transitions ask the consumer for a repair; for tests.</summary>
    internal long RepairRequests => Interlocked.Read(ref _repairRequests);

    /// <summary>
    /// Any thread. Returns false once shutdown has begun: a final keyboard message can still be in
    /// the hook thread's native queue when the service stops, and that stale transition is simply
    /// discarded. A transition racing <see cref="Complete"/> can be discarded for the same reason.
    /// </summary>
    public bool TryEnqueue(HotkeyService.QueuedTransition transition)
    {
        if (_completed)
        {
            return false;
        }

        if (transition.AllowReconcile && transition.Transition == HotkeyTransition.Deactivated)
        {
            Interlocked.Increment(ref _repairRequests);
        }

        _items.Push(transition);
        Signal();
        return true;
    }

    /// <summary>Stops accepting work and wakes the dispatcher so it can drain what is left and exit.</summary>
    public void Complete()
    {
        _completed = true;
        Signal();
    }

    /// <summary>Dispatcher only. Blocks until there is work; false once completed and drained.</summary>
    public bool WaitForWork()
    {
        while (true)
        {
            if (!_items.IsEmpty)
            {
                return true;
            }

            if (_completed)
            {
                return false;
            }

            _work.WaitOne();
        }
    }

    /// <summary>Dispatcher only (or a test standing in for it).</summary>
    public LockFreeInbox<HotkeyService.QueuedTransition>.Batch TakeAll() => _items.TakeAll();

    public void Dispose() => _work.Dispose();

    private void Signal()
    {
        try
        {
            _work.Set();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already released the dispatcher; there is nobody left to wake, and an
            // exception escaping the hook callback would take the whole process down.
        }
    }
}
