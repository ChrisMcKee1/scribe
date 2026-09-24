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
    private long _activationEpoch;

    public bool IsCompleted => _completed;

    /// <summary>
    /// Any thread. The epoch every Activated carries when it is queued, which the dispatcher compares with the current
    /// one. It lives here because the queue outlives each hook installation's engine.
    /// </summary>
    public long ActivationEpoch => Interlocked.Read(ref _activationEpoch);

    /// <summary>
    /// Hook thread only, when the input desktop switches: every Activated still waiting for the dispatcher was computed
    /// before the switch and must not open the microphone after it. A single interlocked add, so the hook callback never
    /// waits here, and it takes nothing the requesting threads hold.
    /// </summary>
    public void AdvanceActivationEpoch() => Interlocked.Increment(ref _activationEpoch);

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
