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
/// sync-only signal that coalesced with it. The word holds the key view epoch the repair was asked for at
/// (<see cref="HotkeyEngine.KeyViewEpoch"/>, never zero), so the pass judges only that view (review round 10, A12).
/// Coalesced requests leave the latest, and that is the newest only because one signal serves one hook installation and
/// only that installation's hook thread asks through it, one request after another (review round 11, A14): a shared
/// signal let a callback of a replaced installation, resuming after the reinstall, replace the replacement's pending
/// request with its own, which the pass then refused.
/// </summary>
internal sealed class HotkeyReconcileSignal : IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle _registration;
    private readonly Action<long> _onSignaled;

    // The key view epoch of the latest request for the leaked-key repair since the last pass, or 0 when none asked for it.
    // Set before the event, taken by the pass.
    private long _repairAt;

    // Every repair this signal has been asked for, counted on the asking thread as it is asked; for tests.
    private long _repairRequests;

    // Every sync-only pass this signal has been asked for, counted the same way; for tests.
    private long _syncRequests;

    /// <param name="onSignaled">
    /// The pass, on a pool thread: the key view epoch to repair keys at, or 0 for the mouse hook's sync alone.
    /// </param>
    public HotkeyReconcileSignal(Action<long> onSignaled)
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

    /// <summary>Any thread: how many repairs this signal has been asked for, counted as each is asked; for tests.</summary>
    internal long RepairRequests => Interlocked.Read(ref _repairRequests);

    /// <summary>
    /// Any thread: how many sync-only passes this signal has been asked for, counted as each is asked, on the asking thread;
    /// for tests that must see what a hook callback asked for without waiting for the pool to run a pass.
    /// </summary>
    internal long SyncRequestsForTests => Interlocked.Read(ref _syncRequests);

    /// <summary>
    /// Any thread: the key view epoch the pending request asks the repair for, or 0 when none does or a pass has taken it;
    /// for tests, which keep it from being taken with <see cref="HoldBeforeTakingForTests"/>.
    /// </summary>
    internal long PendingRepairAtForTests => Interlocked.Read(ref _repairAt);

    /// <summary>
    /// Test seam, null in production: the pool callback waits on it before it takes the request, so a test can publish
    /// several requests before any pass takes one.
    /// </summary>
    internal ManualResetEventSlim? HoldBeforeTakingForTests { get; set; }

    /// <summary>
    /// Any thread, including the hook callbacks: asks for a pass that repairs leaked keys too, judging the key view whose
    /// epoch is <paramref name="keyViewEpoch"/>. Interlocked operations and a SetEvent; it waits for no other thread and
    /// never throws.
    /// </summary>
    public void Signal(long keyViewEpoch)
    {
        Interlocked.Increment(ref _repairRequests);
        Interlocked.Exchange(ref _repairAt, keyViewEpoch);
        Set();
    }

    /// <summary>
    /// Any thread, including the mouse hook callback: asks for a pass that only syncs the mouse hook, unless a repair is
    /// already asked for. An interlocked count (for tests) and a SetEvent; it waits for no other thread and never throws.
    /// </summary>
    public void SignalMouseHookSync()
    {
        Interlocked.Increment(ref _syncRequests);
        Set();
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
    }

    // Pool thread: the request is taken as the pass starts, so a signal made while it runs gets a pass of its own. Nothing
    // the pass throws leaves this callback: an exception escaping a pool callback takes the whole process down (review round
    // 3, item 6: a test's recorder, disposed while a pass that coalesced late was still on its way, took the test host
    // down). The next signal asks again.
    private void RunPass()
    {
        try
        {
            HoldBeforeTakingForTests?.Wait();
        }
        catch (ObjectDisposedException)
        {
            // A test released and disposed its hold while this callback was still on its way; an exception escaping a
            // pool callback would take the whole process down.
        }

        try
        {
            _onSignaled(Interlocked.Exchange(ref _repairAt, 0));
        }
        catch (Exception)
        {
            // See above: never out onto the pool thread.
        }
    }

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
