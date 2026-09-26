using Microsoft.Extensions.Logging;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Keeps Scribe's low-level keyboard hook ahead of a Remote Desktop or virtual machine client's. Windows calls the hooks
/// newest first: SetWindowsHookEx "always installs a hook procedure at the beginning of a hook chain", and each procedure
/// passes an event on to the next only by calling CallNextHookEx (Hooks Overview). A Remote Desktop client can register a
/// low-level keyboard hook of its own (the Remote Desktop control, mstscax.dll, imports SetWindowsHookExW,
/// UnhookWindowsHookEx and CallNextHookEx, and its KeyboardHookMode applies Windows key combinations such as Alt+Tab in the
/// remote session, when the client is in focus or in full screen), and once it has registered after Scribe, it sees every
/// key first: the push-to-talk key reaches the remote session before Scribe can swallow it, or is swallowed by the client
/// and never reaches Scribe, and the watchdog's probe goes unanswered. So when such a client's window comes to the front,
/// the hook thread registers its hook afresh, after the client has had time to register its own, which puts Scribe's
/// first again (AutoHotkey documents the same remedy: reinstalling the hook "has the effect of giving it precedence over
/// any hooks previously installed by other processes").
/// <para>
/// The pool side of that, off the hook thread: the foreground notice arrives here (<see cref="OnForegroundChanged"/>), as
/// does the window in front when a hook installs, the window's process is looked up (<see cref="RemoteClientProcesses"/>),
/// and the hook thread is asked to move its hook ahead <see cref="FirstMoveDelay"/> later, when the client has had time to
/// register on its activation, and once more <see cref="SecondMoveDelay"/> after that in case it registered late, then to
/// release the registrations the moves replaced <see cref="RetiredGrace"/> after the last one; and for as long as the
/// client stays in front, to move ahead again every <see cref="KeepAheadPeriod"/>, each followed by its release, in case it
/// registers once more without leaving the front. Every move and every keep-ahead step is made only if a remote client
/// is still in front when it falls due, and carries the foreground revision and the window it was judged on, which the
/// hook thread checks are still current right before it registers. The same remote window noticed again, with no other
/// window published since the last decision (a count the notice carries, since notices coalesce), does not start the
/// sequence over; a notice or a tick older than the newest decision changes nothing (review round 2, items 3 to 5); and a
/// tick in flight when a newer notice is decided cannot apply what it judged before (review round 3, item 2). While no step
/// is scheduled, the watchdog's recovery poll (<see cref="RecoverIfIdle"/>) looks at what is in front once a period and
/// starts the sequence for a remote client there, so a sequence lost while a client stayed in front is restored at the next
/// upkeep; it publishes no notice, so it can neither overrule a newer notice nor drop a move (review round 4, item 1). The
/// moves themselves, the rule that a move waits while a key whose
/// press Scribe swallowed is held or while every kept registration is inside its grace, and the rules for an event on its
/// way to a replaced registration are the hook thread's (<c>HotkeyService.HookInstallation</c>). Nothing here waits for the
/// hook thread: a request is a posted thread message. Nothing thrown here reaches the pool thread, which would end the
/// process.
/// </para>
/// </summary>
internal sealed class KeyboardHookPrecedence : IDisposable
{
    /// <summary>How long after a remote client came to the front the hook is first moved ahead.</summary>
    internal static readonly TimeSpan FirstMoveDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after the first move it is moved ahead again.</summary>
    internal static readonly TimeSpan SecondMoveDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long after the last move the registrations it replaced are released.</summary>
    internal static readonly TimeSpan RetiredGrace = TimeSpan.FromMilliseconds(RetiredHookRegistrations.GraceMs);

    /// <summary>
    /// While a remote client stays in front, how long after the registrations a move replaced are released the hook is
    /// moved ahead again, in case the client registered its hook once more without leaving the front.
    /// </summary>
    internal static readonly TimeSpan KeepAheadPeriod = TimeSpan.FromSeconds(30);

    private const int Idle = 0;
    private const int FirstMoveDue = 1;
    private const int SecondMoveDue = 2;
    private const int ReleaseDue = 3;
    private const int KeepAheadDue = 4;

    private readonly object _gate = new();
    private readonly Func<nint> _foregroundWindow;
    private readonly Func<nint, string?> _processNameOfWindow;
    private readonly Func<long> _publishedRevision;
    private readonly Action<long, nint> _moveAhead;
    private readonly Action _releaseRetired;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    // Under _gate: the step the sequence is at; the remote window it serves (zero once one that is not a remote client came
    // to the front, or a step found none in front); the newest foreground revision decided (a notice's revision is its
    // publication's, ForegroundNotice) and the publication's change count with it (ForegroundPublication); the schedule, one
    // number per Arm, so a tick knows whether the schedule it read is still the one in force; and when that schedule is due,
    // on _time's clock, or null once its one tick was taken or while nothing is armed (ClosableTimer keeps its schedules the
    // same way).
    private int _step;
    private nint _client;
    private long _decided;
    private long _decidedChanges;
    private long _schedule;
    private long? _dueTimestamp;
    private bool _disposed;

    /// <param name="foregroundWindow">The window in front now (GetForegroundWindow in production).</param>
    /// <param name="processNameOfWindow">The name of the process that owns a window, or null.</param>
    /// <param name="publishedRevision">The revision of the latest foreground notice published (<see cref="ForegroundNotice.PublishedRevision"/>).</param>
    /// <param name="moveAhead">
    /// Asks the hook thread to move its keyboard hook ahead, for the foreground revision and the window in front the step
    /// judged, which the hook thread checks are still current right before it registers; never waits.
    /// </param>
    /// <param name="releaseRetired">Asks the hook thread to release the registrations its moves replaced; never waits.</param>
    /// <param name="logger">Where a remote client coming to the front is recorded, by its process name only.</param>
    /// <param name="time">The clock the delays run on.</param>
    public KeyboardHookPrecedence(
        Func<nint> foregroundWindow,
        Func<nint, string?> processNameOfWindow,
        Func<long> publishedRevision,
        Action<long, nint> moveAhead,
        Action releaseRetired,
        ILogger logger,
        TimeProvider time)
    {
        _foregroundWindow = foregroundWindow;
        _processNameOfWindow = processNameOfWindow;
        _publishedRevision = publishedRevision;
        _moveAhead = moveAhead;
        _releaseRetired = releaseRetired;
        _logger = logger;
        _time = time;
        _timer = time.CreateTimer(
            static state => ((KeyboardHookPrecedence)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Pool thread: the foreground window changed to <paramref name="window"/>, or a hook installed with it in front, as the
    /// notice published with <paramref name="revision"/> and <paramref name="changes"/> (<see cref="ForegroundPublication"/>). A remote client
    /// starts the sequence over; any other window cancels a move not made yet and the keep-ahead moves, and keeps the release
    /// due if a move was made. The notice's pool callbacks can overlap (.NET re-arms a registered wait before it runs its
    /// callback), and the lookup runs before the gate, so a slow lookup can finish after a newer notice was decided: a
    /// revision no newer than the last one decided changes nothing (review round 2, item 4), or an older local window's lookup
    /// would cancel a newer remote schedule. Only the remote window the sequence serves, with no other window published since
    /// the last decision, keeps its sequence as scheduled: notices coalesce, so the same window alone does not prove that
    /// nothing else was in front meanwhile (review round 3, item 2). And a decision that keeps the step reclaims a tick that
    /// took the step's schedule and is still in flight, since that tick judged a foreground older than this notice.
    /// </summary>
    public void OnForegroundChanged(nint window, long revision, long changes)
    {
        try
        {
            var name = window == 0 ? null : _processNameOfWindow(window);
            var remote = RemoteClientProcesses.IsRemoteClient(name);
            lock (_gate)
            {
                if (_disposed || revision <= _decided)
                {
                    return;
                }

                var noOtherWindowSince = changes == _decidedChanges;
                _decided = revision;
                _decidedChanges = changes;
                if (remote && noOtherWindowSince && window == _client && _step != Idle)
                {
                    // The same remote window again, with no other window published since: its sequence goes on as
                    // scheduled (review round 2, item 3), rather than start over and add a move for every repeated notice.
                    ReclaimTickInFlight();
                    return;
                }

                if (remote)
                {
                    _client = window;
                    Arm(FirstMoveDue, FirstMoveDelay);
                }
                else
                {
                    _client = 0;
                    if (_step is FirstMoveDue or KeepAheadDue)
                    {
                        Arm(Idle, Timeout.InfiniteTimeSpan);
                    }
                    else if (_step == SecondMoveDue)
                    {
                        Arm(ReleaseDue, RetiredGrace);
                    }
                    else
                    {
                        ReclaimTickInFlight();
                    }
                }
            }

            if (remote)
            {
                _logger.LogInformation(
                    "Remote desktop client {App} is in front; a move of the keyboard hook ahead of its hook is scheduled, " +
                    "and repeated while it stays in front.",
                    name);
            }
        }
        catch (Exception)
        {
            // A process that went away, or a logger that failed: the next notice or the watchdog's recovery makes up for it.
        }
    }

    /// <summary>Any thread but the hook thread (it takes the gate), for tests: the newest foreground revision decided.</summary>
    internal long DecidedRevisionForTests
    {
        get
        {
            lock (_gate)
            {
                return _decided;
            }
        }
    }

    /// <summary>
    /// The watchdog's thread, once a period (never the hook thread: it takes the gate and looks a process up): the recovery
    /// poll for a sequence that ended while a remote client stayed in front, which no notice starts over until the
    /// foreground changes (a step that read the foreground as the client was losing activation, when Windows has none).
    /// While no step is scheduled it reads the foreground revision, then the window in front, and when a remote client is
    /// there it starts the sequence for it as a notice would; but only if, under the gate at that moment, still no step is
    /// scheduled and no notice was published since that revision (review round 4, item 1). It publishes nothing: it neither
    /// takes the notice's one window slot, which would hide a real notice from the pool, nor advances the revision, which
    /// would drop a move judged on it. A notice published before that revision and still undecided is older than the
    /// window it read, so the sequence it starts decides that revision, and the late notice changes nothing. Nothing thrown
    /// here leaves it.
    /// </summary>
    public void RecoverIfIdle()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _step != Idle)
                {
                    return;
                }
            }

            // The revision before the window, as a step reads them: the window is at least as new as every notice up to it.
            var revision = _publishedRevision();
            var window = _foregroundWindow();
            var name = window == 0 ? null : _processNameOfWindow(window);
            if (!RemoteClientProcesses.IsRemoteClient(name))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || _step != Idle || _publishedRevision() != revision)
                {
                    return;
                }

                _decided = Math.Max(_decided, revision);
                _client = window;
                Arm(FirstMoveDue, FirstMoveDelay);
            }

            _logger.LogInformation(
                "Remote desktop client {App} is in front with no move scheduled; a move of the keyboard hook ahead of its " +
                "hook is scheduled, and repeated while it stays in front.",
                name);
        }
        catch (Exception)
        {
            // A window or process that went away mid-lookup, or a logger that failed: the next period looks again.
        }
    }

    // Any thread, the hook thread at its end included, which must take no lock: a volatile flag the tick and the notice
    // check under the gate, and the timer's disposal. A tick already past its check may still ask the hook thread once,
    // which a thread that is ending ignores.
    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _timer.Dispose();
    }

    // Pool thread, the timer's tick. A tick is taken only once its schedule is due, and at most once (a tick with nothing
    // armed is dropped, and one that arrives early re-arms for the time that remains), and it acts only if that schedule is
    // still the one in force once it has looked up what is in front, which it does outside the gate: a notice or a newer
    // schedule meanwhile decided afresh. Every step asks what is in front first, since the notice for a change can still be
    // on its way: a move is made only if a remote client is still in front, and after a release the keep-ahead moves go on
    // only while one is.
    private void OnTimer()
    {
        try
        {
            int step;
            long schedule;
            lock (_gate)
            {
                if (_disposed || _dueTimestamp is not { } due)
                {
                    return;
                }

                var remaining = _time.GetElapsedTime(_time.GetTimestamp(), due);
                if (remaining > TimeSpan.Zero)
                {
                    _timer.Change(remaining < MinimumRearm ? MinimumRearm : remaining, Timeout.InfiniteTimeSpan);
                    return;
                }

                _dueTimestamp = null;
                step = _step;
                schedule = _schedule;
            }

            // The revision before the window, so the move is judged on a foreground no older than that revision: a notice
            // published after this read makes the hook thread drop the move (HotkeyService.HookInstallation.MoveAhead).
            long revision = 0;
            nint window = 0;
            bool inFront;
            try
            {
                if (step != Idle)
                {
                    revision = _publishedRevision();
                    window = _foregroundWindow();
                }

                inFront = window != 0 && RemoteClientProcesses.IsRemoteClient(_processNameOfWindow(window));
            }
            catch (Exception)
            {
                // A window or process that went away mid-lookup: taken for no remote client, so no move is made.
                inFront = false;
            }

            lock (_gate)
            {
                if (_disposed || _schedule != schedule)
                {
                    return;
                }

                // A step that finds no remote client in front forgets the one it served, so that client coming back to the
                // front afterwards starts the sequence over (it may register its hook again on its activation).
                if (!inFront)
                {
                    _client = 0;
                }

                switch (step)
                {
                    case FirstMoveDue when inFront:
                        _moveAhead(revision, window);
                        Arm(SecondMoveDue, SecondMoveDelay);
                        break;
                    case FirstMoveDue:
                        Arm(Idle, Timeout.InfiniteTimeSpan);
                        break;
                    case SecondMoveDue:
                        if (inFront)
                        {
                            _moveAhead(revision, window);
                        }

                        Arm(ReleaseDue, RetiredGrace);
                        break;
                    case ReleaseDue:
                        _releaseRetired();
                        if (inFront)
                        {
                            Arm(KeepAheadDue, KeepAheadPeriod);
                        }
                        else
                        {
                            Arm(Idle, Timeout.InfiniteTimeSpan);
                        }

                        break;
                    case KeepAheadDue when inFront:
                        _moveAhead(revision, window);
                        Arm(ReleaseDue, RetiredGrace);
                        break;
                    case KeepAheadDue:
                        Arm(Idle, Timeout.InfiniteTimeSpan);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // As in OnForegroundChanged: never out onto the pool thread.
        }
    }

    // Re-arming for less than this would only spin: the platform timer cannot resolve a shorter wait anyway.
    private static readonly TimeSpan MinimumRearm = TimeSpan.FromMilliseconds(1);

    // Callers hold _gate. A new schedule every time, so a tick of any earlier one can tell it is not the one in force.
    private void Arm(int step, TimeSpan due)
    {
        _step = step;
        _schedule++;
        _dueTimestamp = due == Timeout.InfiniteTimeSpan
            ? null
            : _time.GetTimestamp() + (long)(due.TotalSeconds * _time.TimestampFrequency);
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    // Callers hold _gate, deciding a notice that keeps the step. A step with no due time has had its one tick taken, and that
    // tick is still in flight: it read the foreground before this notice was decided, and would apply what it judged then
    // (review round 3, item 2). The step is armed afresh to run at once, so that tick finds its schedule replaced and does
    // nothing, and a new tick judges the step on a fresh read.
    private void ReclaimTickInFlight()
    {
        if (_step != Idle && _dueTimestamp is null)
        {
            Arm(_step, TimeSpan.Zero);
        }
    }
}
/// <summary>
/// The hop from the hook thread to the pool for a foreground change: the WinEvent callback, on the hook thread, makes an
/// interlocked exchange, one or two interlocked increments and a SetEvent, and the handler, which looks the window's process
/// up, runs on a pool thread. Each notice is published with a revision (<see cref="PublishedRevision"/>), which the handler
/// is handed with the window, so it can tell an older notice's decision from a newer one's; the pool callbacks of a repeating
/// registered wait can overlap. Notices that arrive before the handler runs coalesce into the latest window, and the change
/// count handed with it says whether another window was published between (<see cref="ForegroundPublication"/>, review round
/// 3, item 2). Nothing thrown by the handler reaches the pool thread.
/// </summary>
internal sealed class ForegroundNotice : IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle _registration;
    private readonly Action<nint, long, long> _onNotice;
    private readonly ForegroundPublication _publication = new();

    public ForegroundNotice(Action<nint, long, long> onNotice)
    {
        ArgumentNullException.ThrowIfNull(onNotice);
        _onNotice = onNotice;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            static (state, _) => ((ForegroundNotice)state!).Run(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Any thread: how many notices have been published, which is each notice's revision: the hook thread reads it right
    /// before a move ahead, and the handler is handed it with the window.
    /// </summary>
    public long PublishedRevision => _publication.Revision;

    /// <summary>Any thread, the hook thread's WinEvent callback included: the window now in front. Never waits or throws.</summary>
    public void Notify(nint window)
    {
        _publication.Publish(window);
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // The installation is ending; nobody is left to move a hook for.
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
    }

    // The revision first, then the change count, then the window (ForegroundPublication.Read), so the window is at least as
    // new as the revision says: a newer notice published between the reads hands this call its window with an older
    // revision, which the handler decides too, and then again with the newer revision in the call that newer notice's
    // SetEvent brings. Read the other way round, an older window could be handed on with the newer revision, and the newer
    // notice's own call would then be ignored as no newer. A change counted between the reads is seen by that later call.
    private void Run()
    {
        try
        {
            var (window, revision, changes) = _publication.Read();
            _onNotice(window, revision, changes);
        }
        catch (Exception)
        {
            // An exception escaping a pool callback would end the process.
        }
    }
}

/// <summary>
/// What a foreground notice publishes, lock-free: the latest window, a revision per notice, and a count of changes, the
/// notices whose window differed from the one published before it (review round 3, item 2). Notices coalesce, since the
/// handler reads only the latest window, so two of them for one window with another one's between can reach the handler as
/// one; the change count still tells them apart from a repeat. Publish exchanges the window, counts a change when the window
/// it replaced was another, then counts the revision; Read takes the revision first, then the change count, then the window,
/// so each is at least as new as the one read before it. No lock, no allocation: it runs in the hook thread's WinEvent
/// callback.
/// </summary>
internal sealed class ForegroundPublication
{
    private nint _window;
    private long _changes;
    private long _revision;

    /// <summary>Any thread: how many notices have been published.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Any thread: how many of them published a window other than the one before it.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>Any thread: publishes <paramref name="window"/>.</summary>
    public void Publish(nint window)
    {
        if (Interlocked.Exchange(ref _window, window) != window)
        {
            Interlocked.Increment(ref _changes);
        }

        Interlocked.Increment(ref _revision);
    }

    /// <summary>Any thread: the revision, the change count and the window, in that order.</summary>
    public (nint Window, long Revision, long Changes) Read()
    {
        var revision = Interlocked.Read(ref _revision);
        var changes = Interlocked.Read(ref _changes);
        return (Volatile.Read(ref _window), revision, changes);
    }
}