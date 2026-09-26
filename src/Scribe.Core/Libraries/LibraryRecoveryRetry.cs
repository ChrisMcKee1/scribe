using Scribe.Core.Lifecycle;

namespace Scribe.Core.Libraries;

/// <summary>
/// The bounded background retry after a hold-back (contract 9.5, 9.1 step 9). While the published library vocabulary
/// holds content back because the committed generation's journal cannot be listed or read right now (contract 6.6.5),
/// dictation runs on the personal dictionary alone, and the content returns only at the next recovery that publishes.
/// Storage maintenance's hourly pass is too slow a backstop for a transient failure, so a publication that enters a
/// hold-back asks for a maintenance pass right away, and then again on a schedule of its own while the hold-back lasts.
/// </summary>
/// <remarks>
/// <para>
/// Each retry only asks for a pass (storage maintenance's coalesced trigger: due no sooner than its trigger delay after
/// the request and its minimum spacing after the previous pass), and the pass's library step runs the recovery on the
/// maintenance thread, so nothing here does file work, and nothing on <see cref="ILibraryVocabularySource.Current"/>,
/// <see cref="ILibraryVocabularySource.TryHandOff"/>, the keyboard hook or dictation admission waits on it.
/// </para>
/// <para>
/// A request storage maintenance cannot take yet is kept, not dropped: the app publishes its first vocabulary when
/// dictation starts, before maintenance is resolved and started (Grok's G2 on the integration), and maintenance's trigger
/// arms nothing until it starts. So the request that began a hold-back, or a retry that came due, stays owed until the
/// trigger takes it, and maintenance asks for it again when it connects and when it starts
/// (<see cref="MaintenanceStarted"/>), whatever order the shell starts things in. A hold-back that ends first owes nothing.
/// </para>
/// <para>
/// A pass that meets foreground activity skips its remaining light steps, the library step included, and its own
/// follow-up waits out the heavy-work backoff. So every retry is a request of its own on this schedule, never that
/// follow-up: <see cref="DelayBefore"/> doubles from <see cref="FirstDelay"/>, the maintenance minimum spacing, up to
/// <see cref="MaximumDelay"/>, and resets when a publication holds nothing back. It stops for good at shutdown.
/// </para>
/// </remarks>
internal sealed class LibraryRecoveryRetry
{
    /// <summary>The first retry's delay: storage maintenance's minimum spacing, the soonest it runs a second pass anyway.</summary>
    internal static readonly TimeSpan FirstDelay = Persistence.StorageMaintenanceOptions.Default.MinimumSpacing;

    /// <summary>The longest wait between two retries while content stays held back.</summary>
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ClosableTimer? _timer;
    private Func<bool>? _requestRun;
    private bool _heldBack;
    private bool _stopped;
    private int _retries;

    // Under _gate. A request made during the current hold-back that storage maintenance has not taken yet: set before each
    // attempt and cleared only once one is taken, so a start that lands between the two is never missed.
    private bool _owed;

    internal LibraryRecoveryRetry(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Whether a retry is scheduled: the last publication held content back, and shutdown has not begun.</summary>
    internal bool Pending
    {
        get
        {
            lock (_gate)
            {
                return _heldBack && !_stopped;
            }
        }
    }

    /// <summary>Retries asked for since the current hold-back began (the request that began it is not one).</summary>
    internal int Retries
    {
        get
        {
            lock (_gate)
            {
                return _retries;
            }
        }
    }

    /// <summary>
    /// The wait before retry <paramref name="retry"/> (1 for the first after the request that began the hold-back):
    /// <see cref="FirstDelay"/> doubled for each retry before it, never more than <see cref="MaximumDelay"/>.
    /// </summary>
    internal static TimeSpan DelayBefore(int retry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);
        var doublings = Math.Min(retry - 1, 16);
        return TimeSpan.FromTicks(Math.Min(FirstDelay.Ticks << doublings, MaximumDelay.Ticks));
    }

    /// <summary>
    /// How a retry asks for a pass: storage maintenance's coalesced trigger, connected when it takes the janitor. The
    /// delegate answers whether the trigger took the request (false before maintenance starts and after it stops). A request
    /// owed from before the connection is made now.
    /// </summary>
    internal void Connect(Func<bool> requestRun)
    {
        ArgumentNullException.ThrowIfNull(requestRun);
        lock (_gate)
        {
            _requestRun = requestRun;
        }

        MakeOwedRequest();
    }

    /// <summary>
    /// Storage maintenance has started, so its trigger now arms a pass: a request the current hold-back still owes is made
    /// now (Grok's G2 on the integration). Called by <c>StorageMaintenance.Start</c>, outside its lock.
    /// </summary>
    internal void MaintenanceStarted() => MakeOwedRequest();

    /// <summary>
    /// After every publication of the library vocabulary: whether it held content back. The first that does asks for a
    /// pass now and schedules the retries; one that holds nothing back ends them, and whatever was still owed with them.
    /// </summary>
    internal void NotePublished(bool heldBack)
    {
        Func<bool>? request;
        lock (_gate)
        {
            if (_stopped || heldBack == _heldBack)
            {
                return;
            }

            _heldBack = heldBack;
            _retries = 0;
            if (!heldBack)
            {
                _owed = false;
                _timer?.Cancel();
                return;
            }

            _timer ??= new ClosableTimer(OnDue, _time);
            _timer.Schedule(DelayBefore(1));
            _owed = true;
            request = _requestRun;
        }

        Request(request);
    }

    /// <summary>Shutdown: no retry is asked for after this, whatever a publication says. Idempotent.</summary>
    internal void Stop()
    {
        ClosableTimer? timer;
        lock (_gate)
        {
            _stopped = true;
            _heldBack = false;
            timer = _timer;
        }

        timer?.Close();
    }

    private void OnDue()
    {
        Func<bool>? request;
        lock (_gate)
        {
            if (_stopped || !_heldBack || _timer is null)
            {
                return;
            }

            _retries++;
            _timer.Schedule(DelayBefore(_retries + 1));
            _owed = true;
            request = _requestRun;
        }

        Request(request);
    }

    private void MakeOwedRequest()
    {
        Func<bool>? request;
        lock (_gate)
        {
            if (_stopped || !_owed)
            {
                return;
            }

            request = _requestRun;
        }

        Request(request);
    }

    // Outside the gate: the request takes storage maintenance's own lock, and a timer thread must never see a throw. The
    // debt is cleared only once the trigger took a request; one it refused stays owed for the start that follows.
    private void Request(Func<bool>? request)
    {
        bool taken;
        try
        {
            taken = request?.Invoke() == true;
        }
        catch (Exception)
        {
            // Asking for a pass is best effort; the request stays owed, and the next retry, or the hourly pass, asks again.
            return;
        }

        if (taken)
        {
            lock (_gate)
            {
                _owed = false;
            }
        }
    }
}
