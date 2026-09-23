namespace Scribe.Core.Hotkeys;

/// <summary>
/// Lets the hook callback ask for a leaked-key check with nothing more than a SetEvent.
///
/// A thread-pool wait thread watches the event and a pool worker runs the check, so the request
/// passes through neither the hook thread's own work (queueing pool work from a thread outside the
/// pool goes through a queue whose slow path takes a lock) nor the event dispatcher, whose handlers
/// can block for seconds while a microphone opens. A stuck modifier is healed on the same schedule
/// whatever the dispatcher is doing. Signals that arrive before the check runs coalesce, which is
/// enough: one check covers every key.
/// </summary>
internal sealed class HotkeyReconcileSignal : IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle _registration;

    public HotkeyReconcileSignal(Action onSignaled)
    {
        ArgumentNullException.ThrowIfNull(onSignaled);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            static (state, _) => ((Action)state!).Invoke(),
            onSignaled,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Any thread, including the hook callback. Never waits and never throws.</summary>
    public void Signal()
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

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
    }
}
