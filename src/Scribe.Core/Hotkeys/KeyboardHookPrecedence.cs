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
/// is still in front when it falls due. The moves themselves, the rule that a move waits while a key whose press Scribe
/// swallowed is held, and the rules for an event on its way to a replaced registration are the hook thread's
/// (<c>HotkeyService.HookInstallation</c>). Nothing here waits for the hook thread: a request is a posted thread message.
/// Nothing thrown here reaches the pool thread, which would end the process.
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
    private readonly Action _moveAhead;
    private readonly Action _releaseRetired;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private int _step;
    private bool _disposed;

    /// <param name="foregroundWindow">The window in front now (GetForegroundWindow in production).</param>
    /// <param name="processNameOfWindow">The name of the process that owns a window, or null.</param>
    /// <param name="moveAhead">Asks the hook thread to move its keyboard hook ahead; never waits.</param>
    /// <param name="releaseRetired">Asks the hook thread to release the registrations its moves replaced; never waits.</param>
    /// <param name="logger">Where a remote client coming to the front is recorded, by its process name only.</param>
    /// <param name="time">The clock the delays run on.</param>
    public KeyboardHookPrecedence(
        Func<nint> foregroundWindow,
        Func<nint, string?> processNameOfWindow,
        Action moveAhead,
        Action releaseRetired,
        ILogger logger,
        TimeProvider time)
    {
        _foregroundWindow = foregroundWindow;
        _processNameOfWindow = processNameOfWindow;
        _moveAhead = moveAhead;
        _releaseRetired = releaseRetired;
        _logger = logger;
        _timer = time.CreateTimer(
            static state => ((KeyboardHookPrecedence)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Pool thread: the foreground window changed to <paramref name="window"/>, or a hook installed with it in front. A
    /// remote client starts the sequence over; any other window cancels a move not made yet and the keep-ahead moves, and
    /// keeps the release due if a move was made.
    /// </summary>
    public void OnForegroundChanged(nint window)
    {
        try
        {
            var name = window == 0 ? null : _processNameOfWindow(window);
            var remote = RemoteClientProcesses.IsRemoteClient(name);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (remote)
                {
                    Arm(FirstMoveDue, FirstMoveDelay);
                }
                else if (_step is FirstMoveDue or KeepAheadDue)
                {
                    Arm(Idle, Timeout.InfiniteTimeSpan);
                }
                else if (_step == SecondMoveDue)
                {
                    Arm(ReleaseDue, RetiredGrace);
                }
            }

            if (remote)
            {
                _logger.LogInformation(
                    "Remote desktop client {App} is in front; the keyboard hook moves ahead of its hook and is kept there.",
                    name);
            }
        }
        catch (Exception)
        {
            // A process that went away, or a logger that failed: the next notice or the watchdog makes up for it.
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

    // Pool thread, the timer's tick. Every step asks what is in front first, since the notice for a change can still be on
    // its way: a move is made only if a remote client is still in front, and after a release the keep-ahead moves go on
    // only while one is.
    private void OnTimer()
    {
        try
        {
            int step;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                step = _step;
            }

            var inFront = step != Idle &&
                RemoteClientProcesses.IsRemoteClient(_processNameOfWindow(_foregroundWindow()));
            lock (_gate)
            {
                // A notice that arrived meanwhile decided afresh.
                if (_disposed || _step != step)
                {
                    return;
                }

                switch (step)
                {
                    case FirstMoveDue when inFront:
                        _moveAhead();
                        Arm(SecondMoveDue, SecondMoveDelay);
                        break;
                    case FirstMoveDue:
                        Arm(Idle, Timeout.InfiniteTimeSpan);
                        break;
                    case SecondMoveDue:
                        if (inFront)
                        {
                            _moveAhead();
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
                        _moveAhead();
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

    // Callers hold _gate.
    private void Arm(int step, TimeSpan due)
    {
        _step = step;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }
}

/// <summary>
/// The hop from the hook thread to the pool for a foreground change: the WinEvent callback, on the hook thread, makes one
/// volatile write and a SetEvent, and the handler, which looks the window's process up, runs on a pool thread. Notices
/// that arrive before the handler runs coalesce into the latest window, which is all the handler needs. Nothing thrown by
/// the handler reaches the pool thread.
/// </summary>
internal sealed class ForegroundNotice : IDisposable
{
    private readonly AutoResetEvent _signal = new(false);
    private readonly RegisteredWaitHandle _registration;
    private readonly Action<nint> _onNotice;
    private nint _window;

    public ForegroundNotice(Action<nint> onNotice)
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

    /// <summary>Any thread, the hook thread's WinEvent callback included: the window now in front. Never waits or throws.</summary>
    public void Notify(nint window)
    {
        Volatile.Write(ref _window, window);
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

    private void Run()
    {
        try
        {
            _onNotice(Volatile.Read(ref _window));
        }
        catch (Exception)
        {
            // An exception escaping a pool callback would end the process.
        }
    }
}
