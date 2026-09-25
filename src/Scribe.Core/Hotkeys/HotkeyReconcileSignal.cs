namespace Scribe.Core.Hotkeys;

/// <summary>
/// Lets the hook callbacks ask for a reconcile pass with nothing more than an interlocked write and a SetEvent.
///
/// A thread-pool wait thread watches the event and a pool worker runs the pass, so the request
/// passes through neither the hook thread's own work (queueing pool work from a thread outside the
/// pool goes through a queue whose slow path takes a lock) nor the event dispatcher, whose handlers
/// can block for seconds while a microphone opens. A stuck modifier is healed on the same schedule
/// whatever the dispatcher is doing. Signals that arrive before the pass runs coalesce, which is
/// enough: one pass covers every key and the mouse hook.
///
/// A pass always does the mouse hook's sync (a drain-only hook whose debt has gone is removed), and does the leaked-key
/// repair only when some signal since the last pass asked for it (<see cref="Signal"/>). A mouse event that only settled
/// a debt asks for the sync alone (<see cref="SignalMouseHookSync"/>): the repair judges keys by the engine's view,
/// which a state clear empties, and such an event is no evidence that any key leaked (review round 8, A9). The request
/// is one word, set by the callback before the event and taken by the pass, so a repair asked for is never lost to a
/// sync-only signal that coalesced with it.
/// </summary>
internal sealed class HotkeyReconcileSignal : IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle _registration;
    private readonly Action<bool> _onSignaled;

    // 1 while a signal since the last pass asked for the leaked-key repair. Set before the event, taken by the pass.
    private int _repairKeys;

    /// <param name="onSignaled">
    /// The pass, on a pool thread: true when it should also repair leaked keys, false for the mouse hook's sync alone.
    /// </param>
    public HotkeyReconcileSignal(Action<bool> onSignaled)
    {
        ArgumentNullException.ThrowIfNull(onSignaled);
        _onSignaled = onSignaled;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            static (state, _) => ((HotkeyReconcileSignal)state!).RunPass(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Any thread, including the hook callbacks: asks for a pass that repairs leaked keys too. An interlocked exchange and
    /// a SetEvent; it waits for no other thread and never throws.
    /// </summary>
    public void Signal()
    {
        Interlocked.Exchange(ref _repairKeys, 1);
        Set();
    }

    /// <summary>
    /// Any thread, including the mouse hook callback: asks for a pass that only syncs the mouse hook, unless a repair is
    /// already asked for. A SetEvent; it waits for no other thread and never throws.
    /// </summary>
    public void SignalMouseHookSync() => Set();

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
    }

    // Pool thread: the request is taken as the pass starts, so a signal made while it runs gets a pass of its own.
    private void RunPass() => _onSignaled(Interlocked.Exchange(ref _repairKeys, 0) != 0);

    private void Set()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // The service stopped; a hook thread that outlived it has nobody left to heal for, and
            // an exception escaping the hook callback would take the whole process down.
        }
    }
}
