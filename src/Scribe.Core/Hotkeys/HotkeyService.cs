using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Global push-to-talk hotkey via a <c>WH_KEYBOARD_LL</c> hook, and a <c>WH_MOUSE_LL</c> hook on the
/// same thread while a binding presses a mouse button. The hooks run on their own thread with a native
/// message pump (required for low-level hooks). The hook callbacks perform only input tracking,
/// optional suppression, and transition enqueueing so they return promptly.
///
/// The callbacks never wait for another thread. Windows calls them by sending a message to the hook
/// thread and silently removes a hook if it answers after LowLevelHooksTimeout (at most 1000 ms),
/// so the input state belongs to the hook thread alone (<see cref="HotkeyEngine"/>). Configuration
/// and commands from other threads are queued through <see cref="HotkeyCommandRouter"/> and applied
/// by the hook thread at its next input event, or sooner when a thread message wakes it; what other
/// threads read is published without a lock; and transitions leave through a queue that never
/// blocks its producer.
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<HotkeyService> _logger;
    private readonly Func<bool?> _desktopReceivesInput;
    private readonly Func<nint> _foregroundWindow;
    private readonly Func<nint, string?> _processNameOfWindow;
    private readonly TimeProvider _time;
    private readonly Func<int> _keyRepeatWindowMs;
    private readonly object _sync = new();
    private readonly HotkeyCommandRouter _router;
    private readonly SuppressedKeyReconciler _reconciler;

    // Serializes capture's start with the leaked-key repair's key-ups (AdmitCapture, SuppressedKeyReconciler). Taken only
    // by the thread that asks for capture and by the repair's pool thread, never by the hook thread, in a callback or
    // between messages.
    private readonly object _repairGate = new();

    /// <summary>
    /// A transition in flight between the hook thread and the consumer thread. The generation
    /// lets the dispatcher drop an Activated that was computed before a state-clearing request
    /// (capture mode, binding update, pause, hook reinstall); without it a recording could start
    /// while capture mode is armed. The activation epoch does the same for a desktop switch, which
    /// the hook thread itself notices, so it cannot advance the requesters' generation; it is the
    /// epoch of <see cref="Engine"/>, the installation that queued the transition, and an
    /// Activated is judged by that engine's epoch alone. Deactivations always dispatch (a redundant
    /// stop is harmless, a missed stop is not). AllowReconcile is false for transitions born from a
    /// state clear: right after a clear the reconciler cannot distinguish a genuinely held key from
    /// a leaked one and could release a key the user is holding. Deactivation says why a
    /// Deactivated happened, and Activation numbers the press an Activated came from
    /// (<see cref="HotkeyTriggerEventArgs.Activation"/>), zero otherwise. KeyViewEpoch is the engine's key view epoch when
    /// the transition was computed (<see cref="HotkeyEngine.KeyViewEpoch"/>), which the repair a release asks for judges
    /// by; zero asks for none.
    /// </summary>
    internal readonly record struct QueuedTransition(
        HotkeyTransition Transition,
        HotkeyTrigger Trigger,
        long Generation,
        bool AllowReconcile,
        HotkeyDeactivation Deactivation = HotkeyDeactivation.Released,
        long ActivationEpoch = 0,
        HotkeyEngine? Engine = null,
        long Activation = 0,
        long KeyViewEpoch = 0);

    private HotkeyTransitionQueue? _transitions;
    private HookInstallation? _installation;
    private Thread? _consumerThread;
    private Timer? _watchdog;
    private long _hookCallbackCount;
    private long _reconcilePasses;
    private long _repairPassesScheduled;
    private int _captureAdmissions;
    private TimeSpan _captureAdmissionWait = TimeSpan.FromMilliseconds(250);
    private readonly HookLivenessProbe _livenessProbe = new();

    // What the log last said about an installation's mouse hook (ReportMouseHookLocked). Guarded by _sync.
    private HookInstallation? _mouseReportFor;
    private bool _mouseReportedInstalled;
    private bool _mouseReportedDrainOnly;
    private int _mouseReportedError;
    private long _mouseReportedLosses;

    // What the log last said about an installation's keyboard hook moves (MaintainKeyboardHookLocked). Guarded by _sync.
    private HookInstallation? _keyboardReportFor;
    private long _keyboardMoveFailuresReported;
    private long _keyboardFoundGoneReported;
    private long _keyboardMovesDeferredReported;
    private long _keyboardMovesDroppedReported;

    public HotkeyService(ILogger<HotkeyService> logger)
        : this(logger, HotkeyBinding.DefaultDictation)
    {
    }

    public HotkeyService(ILogger<HotkeyService> logger, HotkeyBinding binding)
        : this(logger, binding, NativeMethods.ThreadDesktopReceivesInput)
    {
    }

    /// <param name="desktopReceivesInput">
    /// Asked on the hook thread when a desktop-switch notice arrives: whether that thread's desktop is receiving input, or
    /// null when that cannot be told (see <see cref="HotkeyEngine.OnDesktopSwitchNotice"/>). Tests replace the real query.
    /// </param>
    internal HotkeyService(ILogger<HotkeyService> logger, HotkeyBinding binding, Func<bool?> desktopReceivesInput)
        : this(logger, CreateRouter(binding), desktopReceivesInput)
    {
    }

    /// <param name="router">
    /// The configuration side and the engines it builds: the service's own, except in a test that plays the hook thread
    /// itself and passes the router it drives, so this service's requests and its consumer step
    /// (<see cref="DispatchTransition"/>) reach the engine that test drives.
    /// </param>
    /// <param name="desktopReceivesInput">As for the other constructors.</param>
    /// <param name="keyDownInWindows">
    /// Windows' view of a key for the leaked-key repair (GetAsyncKeyState in production); a test scripts it.
    /// </param>
    /// <param name="releaseLeakedKey">The leaked-key repair's key-up (a marked SendInput in production); a test records it.</param>
    /// <param name="foregroundWindow">
    /// The window in front (GetForegroundWindow in production), which the moves ahead of a remote client's keyboard hook
    /// and the watchdog's warning ask about; a test scripts it.
    /// </param>
    /// <param name="processNameOfWindow">The name of a window's process (<see cref="WindowOwners.ProcessNameOf"/>); a test scripts it.</param>
    /// <param name="time">The clock the moves ahead are timed on; a test owns it.</param>
    /// <param name="keyRepeatWindowMs">
    /// How long after the keyboard hook becomes the newest registration an unseen key-down is uncertain
    /// (<see cref="KeyRepeatTiming.ReadUncertaintyWindowMs"/>, read off the hook thread); a test scripts it.
    /// </param>
    internal HotkeyService(
        ILogger<HotkeyService> logger,
        HotkeyCommandRouter router,
        Func<bool?> desktopReceivesInput,
        Func<uint, bool>? keyDownInWindows = null,
        Func<uint, bool>? releaseLeakedKey = null,
        Func<nint>? foregroundWindow = null,
        Func<nint, string?>? processNameOfWindow = null,
        TimeProvider? time = null,
        Func<int>? keyRepeatWindowMs = null)
    {
        _logger = logger;
        _desktopReceivesInput = desktopReceivesInput;
        _foregroundWindow = foregroundWindow ?? WindowOwners.Foreground;
        _processNameOfWindow = processNameOfWindow ?? WindowOwners.ProcessNameOf;
        _time = time ?? TimeProvider.System;
        _keyRepeatWindowMs = keyRepeatWindowMs ?? KeyRepeatTiming.ReadUncertaintyWindowMs;
        _router = router;
        _reconciler = CreateReconciler(
            _router,
            keyDownInWindows ?? NativeMethods.IsKeyLogicallyDown,
            releaseLeakedKey ?? ReleaseLeakedInput,
            _repairGate,
            KeyViewIsWhole);

        // Before either hook can exist: neither callback's first CallNextHookEx or GetAsyncKeyState does the runtime's
        // one-time work for a P/Invoke inside Windows' deadline.
        NativeMethods.PrelinkHookCalls();
    }

    /// <summary>
    /// The leak check over <paramref name="router"/>'s current engine; <paramref name="isLogicallyDown"/> is Windows' view
    /// and <paramref name="releaseInput"/> the repair, which a test replaces. The service's own also takes its gate and
    /// the check of whether the engine's view may be trusted (<see cref="KeyViewIsWhole"/>); a test of the rule itself
    /// leaves both out. Keys only: no mouse button is ever a candidate (see <see cref="SuppressedKeyReconciler"/>).
    /// </summary>
    internal static SuppressedKeyReconciler CreateReconciler(
        HotkeyCommandRouter router,
        Func<uint, bool> isLogicallyDown,
        Func<uint, bool> releaseInput,
        object? gate = null,
        Func<long, bool>? mayJudge = null) =>
        new(isLogicallyDown, router.IsPressed, releaseInput, gate ?? new object(), mayJudge ?? (static _ => true));

    // A key left down gets a marked key-up. Scribe injects no mouse input at all: the leak check never lists a mouse button.
    private static bool ReleaseLeakedInput(uint key) => NativeMethods.SendMarkedKeyEvent((ushort)key, keyUp: true);

    // GetAsyncKeyState on the hook path, rarely: on a press that completes a bare Page Up or Page Down binding while the
    // hook's view shows a modifier held, only about that modifier (see ChordStateMachine), and for the release of a mouse
    // button whose press the hook swallowed, only about that button (see HotkeyEngine.OnMouseButtonEvent). Both delegates
    // are made here, as the service is constructed and before either hook exists; the callbacks only invoke them. The
    // runtime's one-time work for the P/Invoke behind them is done by the constructor too (NativeMethods.PrelinkHookCalls).
    private static HotkeyCommandRouter CreateRouter(HotkeyBinding binding)
    {
        Func<uint, bool> keyDown = NativeMethods.IsKeyLogicallyDown;
        return new(binding, keyDown, keyDown);
    }

    public bool IsRunning { get; private set; }

    public HotkeyBinding Binding => _router.Binding;

    public HotkeyBinding? DictationOnlyBinding => _router.DictationOnlyBinding;

    /// <summary>What the hook asks about a modifier its own view holds (see ChordStateMachine); for tests.</summary>
    internal Func<uint, bool>? WindowsKeyState => _router.WindowsKeyState;

    /// <summary>What the hook asks about a mouse button whose release it owes (see HotkeyEngine); for tests.</summary>
    internal Func<uint, bool>? WindowsButtonState => _router.WindowsButtonState;

    /// <summary>How many desktop switches the current hook's engine has applied.</summary>
    internal long DesktopSwitchesSeen => _router.CurrentEngine?.DesktopSwitches ?? 0;

    /// <summary>How many desktop-switch notices reached the current hook's engine on its own thread; for the wiring test.</summary>
    internal long DesktopSwitchNoticesSeen => _router.CurrentEngine?.DesktopSwitchNotices ?? 0;

    /// <summary>Whether the current installation's mouse hook is registered; for tests.</summary>
    internal bool MouseHookInstalled => CurrentInstallation?.MouseHookInstalled == true;

    /// <summary>How many times the current installation registered its mouse hook; for tests.</summary>
    internal long MouseHookRegistrations => CurrentInstallation?.MouseHookRegistrations ?? 0;

    /// <summary>The current installation's WM_HOTKEY_REFRESH posts, refused posts and handlings; for tests.</summary>
    internal (long Posted, long PostFailures, long Handled) MouseHookRefreshesForTests =>
        CurrentInstallation?.MouseHookRefreshes ?? (0, 0, 0);

    /// <summary>How many of the current installation's renewals found the previous registration gone; for tests.</summary>
    internal long MouseHookLosses => CurrentInstallation?.MouseHookLosses ?? 0;

    /// <summary>The current installation's mouse hook registration, or zero; for tests.</summary>
    internal nint MouseHookHandle => CurrentInstallation?.MouseHookHandle ?? 0;

    /// <summary>How many middle and side button events reached the current engine from the mouse hook; for tests.</summary>
    internal long MouseButtonEventsSeen => _router.CurrentEngine?.MouseButtonEvents ?? 0;

    /// <summary>How many times the current engine was told its mouse hook had been found removed; for tests.</summary>
    internal long MouseHookLossesHandled => _router.CurrentEngine?.MouseHookLossesHandled ?? 0;

    /// <summary>
    /// The engine the hook thread drives, for a test on a desktop with no input to play that thread between its messages
    /// (after <see cref="MaintainMouseHookNow"/> has been answered), never while it is busy.
    /// </summary>
    internal HotkeyEngine? CurrentEngineForTests => _router.CurrentEngine;

    /// <summary>
    /// The reconcile signal the current installation's hook callbacks raise (each installation has its own), or null while
    /// stopped; for tests that play a hook callback between the hook thread's messages.
    /// </summary>
    internal HotkeyReconcileSignal? ReconcileSignalForTests
    {
        get
        {
            lock (_sync)
            {
                return _installation?.ReconcileSignal;
            }
        }
    }

    /// <summary>How many reconcile passes have finished, whatever each one did; for tests that wait for one.</summary>
    internal long ReconcilePassesRun => Interlocked.Read(ref _reconcilePasses);

    /// <summary>
    /// How long capture's start waits for a leaked-key release already being sent; a test that holds that release sets
    /// its own.
    /// </summary>
    internal TimeSpan CaptureAdmissionWaitForTests
    {
        get => _captureAdmissionWait;
        set => _captureAdmissionWait = value;
    }

    /// <summary>Whether a capture start is being admitted right now; for tests that pause the repair it waits for.</summary>
    internal bool CaptureAdmissionPendingForTests => Volatile.Read(ref _captureAdmissions) != 0;

    /// <summary>
    /// Whether the leaked-key repair may judge a key right now, for a request made in the current view
    /// (<see cref="KeyViewIsWhole"/>); for tests.
    /// </summary>
    internal bool KeyRepairAllowedForTests => KeyViewIsWhole(CurrentKeyViewEpoch());

    // Whether the engine's view of the keys may be trusted to judge a leak for a repair asked for when the view had the
    // epoch requestedAt, which the repair checks under _repairGate before each key's reads and again immediately before
    // its key-up: no capture start is being admitted, capture does not own input (from its request until both machines
    // have applied its end, HotkeyCommandRouter.CaptureOwnsInput), a hook runs at all (a pass that reaches its check after
    // Stop has no view: every key Windows holds would look leaked to it), and the current engine's view is still the one
    // the request was made in (HotkeyEngine.KeyViewEpoch: no clear and no new engine since, review round 10, A12). The
    // admission count is read first and the router's request after it: AdmitCapture raises the count before the request
    // and lowers it only after the request is published, so a start in progress is seen by one of the two. The epoch is
    // read last, after the reads the check follows, which the caller's fence keeps before it: the engine takes a new epoch
    // before it clears a key, so reads that saw a cleared key make this see the new epoch.
    private bool KeyViewIsWhole(long requestedAt) =>
        Volatile.Read(ref _captureAdmissions) == 0 &&
        !_router.CaptureOwnsInput &&
        _router.CurrentEngine is { } engine &&
        engine.KeyViewEpoch == requestedAt;

    // The current engine's key view epoch, or one no view has (epochs start at 1) when no engine runs.
    private long CurrentKeyViewEpoch() => _router.CurrentEngine?.KeyViewEpoch ?? -1;

    /// <summary>
    /// One reconcile pass, run now on the calling thread without the settling delay, as if asked for now (in the
    /// current key view) when <paramref name="repairKeys"/>; for tests.
    /// </summary>
    internal void RunReconcilePassForTests(bool repairKeys) => RunReconcilePass(repairKeys ? CurrentKeyViewEpoch() : 0);

    /// <summary>
    /// One reconcile pass for a repair asked for when the engine's key view had <paramref name="requestedAt"/> as its epoch
    /// (<see cref="HotkeyEngine.KeyViewEpoch"/>); for tests of a view that changed after the request.
    /// </summary>
    internal void RunReconcilePassForTests(long requestedAt) => RunReconcilePass(requestedAt);

    /// <summary>The service's leaked-key check, for tests that pause it at its seam.</summary>
    internal SuppressedKeyReconciler ReconcilerForTests => _reconciler;

    /// <summary>How many passes asking for the leaked-key repair have been scheduled, from any source; for tests.</summary>
    internal long RepairPassesScheduledForTests => Interlocked.Read(ref _repairPassesScheduled);

    /// <summary>
    /// How many leaked-key repairs the hook side has asked for, counted where each is asked, on the asking thread: the
    /// current installation's signal and every Deactivated queued for the consumer. For tests that must see a request
    /// without waiting for the pool to schedule it.
    /// </summary>
    internal long RepairRequestsAskedForTests
    {
        get
        {
            lock (_sync)
            {
                return (_installation?.ReconcileSignal.RepairRequests ?? 0) + (_transitions?.RepairRequests ?? 0);
            }
        }
    }

    /// <summary>What the watchdog does for the mouse hook each period, done now; for tests.</summary>
    internal void MaintainMouseHookNow()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                MaintainMouseHookLocked();
            }
        }
    }

    /// <summary>What the watchdog does for the keyboard hook's moves ahead each period, done now; for tests.</summary>
    internal void MaintainKeyboardHookNow()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                MaintainKeyboardHookLocked();
            }
        }
    }

    /// <summary>Asks the hook thread to move the keyboard hook ahead now, as a remote client coming to the front does; for tests.</summary>
    internal void MoveKeyboardHookAheadNow() => CurrentInstallation?.RequestMoveAhead(HookInstallation.ForcedMove, 0);

    /// <summary>A foreground notice for <paramref name="window"/>, published as the hook thread's WinEvent callback publishes one; for tests.</summary>
    internal void NoticeForegroundForTests(nint window) => CurrentInstallation?.NoticeForeground(window);

    /// <summary>Test seam, null in production: run on the hook thread as it takes a move ahead, before it judges it.</summary>
    internal Action? BeforeKeyboardMoveForTests { get; set; }

    /// <summary>How many of the current installation's moves ahead were dropped because the foreground changed first; for tests.</summary>
    internal long KeyboardHookMovesDropped => CurrentInstallation?.KeyboardHookMovesDropped ?? 0;

    /// <summary>How many of the current installation's moves ahead waited for a kept registration's grace to end; for tests.</summary>
    internal long KeyboardHookMovesDeferredForSlots => CurrentInstallation?.KeyboardHookMovesDeferredForSlots ?? 0;

    /// <summary>The wait, in milliseconds, the hook thread last set its retry timer for; for tests.</summary>
    internal long KeyboardMoveRetryDueMsForTests => CurrentInstallation?.MoveRetryDueMs ?? 0;

    /// <summary>How many registrations the current installation's moves replaced it has released; for tests.</summary>
    internal long RetiredKeyboardHooksReleased => CurrentInstallation?.RetiredKeyboardHooksReleased ?? 0;

    /// <summary>Asks the hook thread to retry a move that waited now, as its retry timer does; for tests.</summary>
    internal void RetryDeferredKeyboardMoveNowForTests() => CurrentInstallation?.RequestMoveRetry();

    /// <summary>Asks the hook thread to release what its moves replaced now, whatever its age with <paramref name="force"/>; for tests.</summary>
    internal void ReleaseRetiredKeyboardHooksNow(bool force) => CurrentInstallation?.RequestReleaseRetired(force);

    /// <summary>The current installation's keyboard hook registration, or zero; for tests.</summary>
    internal nint KeyboardHookHandle => CurrentInstallation?.KeyboardHookHandle ?? 0;

    /// <summary>How many registrations the current installation's moves replaced and still keeps; for tests.</summary>
    internal int RetiredKeyboardHooks => CurrentInstallation?.RetiredKeyboardHooks ?? 0;

    /// <summary>How many times the current installation moved its keyboard hook ahead; for tests.</summary>
    internal long KeyboardHookMoves => CurrentInstallation?.KeyboardHookMoves ?? 0;

    /// <summary>How many of the current installation's moves ahead waited for a swallowed key's release; for tests.</summary>
    internal long KeyboardHookMovesDeferred => CurrentInstallation?.KeyboardHookMovesDeferred ?? 0;

    /// <summary>How many replaced registrations the current installation found already gone at release; for tests.</summary>
    internal long RetiredKeyboardHooksFoundGone => CurrentInstallation?.RetiredKeyboardHooksFoundGone ?? 0;

    /// <summary>How many key events came back through a replaced registration and passed untouched; for tests.</summary>
    internal long KeyEventEchoes => CurrentInstallation?.KeyEventEchoes ?? 0;

    /// <summary>How many release requests the current installation's thread has handled, a barrier for tests.</summary>
    internal long RetiredReleaseRequestsHandled => CurrentInstallation?.RetiredReleaseRequestsHandled ?? 0;

    /// <summary>
    /// The current installation's keyboard registration delegates, current and one replaced (or null), for a test that plays
    /// the hook thread between its messages on a desktop with no input.
    /// </summary>
    internal (NativeMethods.LowLevelKeyboardProc Current, NativeMethods.LowLevelKeyboardProc? Replaced)? KeyboardProcsForTests =>
        CurrentInstallation?.KeyboardProcsForTests;

    /// <summary>Whether the current installation is being told of foreground changes; for tests.</summary>
    internal bool ForegroundNoticesInstalled => CurrentInstallation is { ForegroundHookError: null };

    /// <summary>
    /// What the watchdog does when it finds the keyboard hook dead, done now: the hook thread is replaced by a new one
    /// with a new engine; for tests. Returns once the new installation has installed its hooks, or failed to.
    /// </summary>
    internal void ReinstallHookNow()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                ReinstallHookLocked();
            }
        }
    }

    private HookInstallation? CurrentInstallation
    {
        get
        {
            lock (_sync)
            {
                return _installation;
            }
        }
    }

    public event EventHandler<HotkeyTriggerEventArgs>? Activated;

    public event EventHandler<HotkeyTriggerEventArgs>? Deactivated;

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return;
            }

            // A fresh engine in a fresh epoch, built from the published configuration: no consumer
            // exists yet, so nothing from a previous run can leak into this one.
            var transitions = new HotkeyTransitionQueue();
            var (engine, _) = _router.BeginEngine(transitions);
            var installation = new HookInstallation(this, engine, replacesRegistration: false);

            if (!installation.Install(InstallTimeout))
            {
                _router.EndEngine(engine);
                transitions.Complete();
                transitions.Dispose();
                throw new InvalidOperationException(
                    "Failed to install the global keyboard hook.", installation.InstallError);
            }

            _transitions = transitions;
            _installation = installation;
            _consumerThread = new Thread(() => ConsumeTransitions(transitions))
            {
                Name = "Scribe.HotkeyDispatch",
                IsBackground = true,
            };
            _consumerThread.Start();

            // Windows silently removes a low-level hook whose callback misses the OS deadline
            // (documented: no notification of any kind). The watchdog probes liveness so a long
            // GC pause during ASR decode cannot permanently kill push-to-talk.
            Interlocked.Exchange(ref _hookCallbackCount, 0);
            _livenessProbe.Disarm();
            _watchdog = new Timer(_ => WatchdogTick(), null, WatchdogPeriod, WatchdogPeriod);

            IsRunning = true;
            var binding = Binding;
            _logger.LogInformation(
                "Hotkey hook installed for {Binding} ({Mode}).", DescribeBinding(binding), binding.Mode);
            LogMissingDesktopSwitchHook(installation);
            ReportMouseHookLocked(installation);
            installation.NoticeForegroundNow();
        }
    }

    // A warning, not a failure: without it the hook still works, but a switch goes unnoticed.
    private void LogMissingDesktopSwitchHook(HookInstallation installation)
    {
        if (installation.DesktopSwitchHookError is { } error)
        {
            _logger.LogWarning(
                "Desktop switch notifications are unavailable (Win32 error {Error}); a dictation whose key is held as " +
                "the PC locks keeps recording until the key is pressed again, and a Narrator key released on the lock " +
                "screen keeps blocking a bare Page Up or Page Down.",
                error);
        }

        // Also a warning only: without foreground notices the watchdog still reinstalls a hook that a Remote Desktop
        // client's took the keys from, within a period or two.
        if (installation.ForegroundHookError is { } foregroundError)
        {
            _logger.LogWarning(
                "Foreground change notifications are unavailable (Win32 error {Error}); the keyboard hook is not moved " +
                "ahead of a Remote Desktop client's when its window comes to the front.",
                foregroundError);
        }
    }

    public void Stop()
    {
        HookInstallation? installation;
        Thread? consumerThread;
        HotkeyTransitionQueue? transitions;

        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _watchdog?.Dispose();
            _watchdog = null;
            installation = _installation;
            consumerThread = _consumerThread;
            transitions = _transitions;
            _installation = null;
            _consumerThread = null;
            _transitions = null;

            // From here on, requests only update the published configuration; the next Start
            // builds its engine from it.
            _router.EndEngine();
            installation?.RequestQuit();
            transitions?.Complete();
        }

        installation?.Join(JoinTimeout);
        consumerThread?.Join(JoinTimeout);
        transitions?.Dispose();

        _logger.LogInformation("Hotkey hook removed.");
    }

    public void CancelToggle(long activation) => Wake(_router.CancelToggle(activation));

    public void SetCaptureMode(bool enabled)
    {
        if (enabled)
        {
            AdmitCapture();
        }
        else
        {
            // Capture's end needs no exclusion: the repair judges no key until both machines have applied it
            // (HotkeyCommandRouter.CaptureOwnsInput).
            Wake(_router.SetCaptureMode(false));
        }

        _logger.LogInformation("Hotkey binding capture mode {State}.", enabled ? "enabled" : "disabled");
    }

    // The requesting thread (Settings' UI thread): capture's start, serialized with the leaked-key repair's key-ups on
    // _repairGate (review round 9, A11). The repair judges and sends one key at a time inside that gate, checking before
    // the key's reads and again immediately before its key-up that no start is being admitted, capture does not own input
    // and the engine's key view is still the one the repair was asked for in (KeyViewIsWhole). So the count is raised
    // first, which stops the repair at its next check, then the gate is taken, which waits for the one key-up that may
    // already be on its way, and the request is published and posted inside it: every key-up is sent before capture is
    // requested or not at all, and the engine clears its view only after. The wait for the gate is bounded
    // (_captureAdmissionWait, 250 ms): a key-up can take as long as the low-level hooks Windows passes it through
    // (Scribe's own keyboard callback passes a marked key-up on at once; another program's can take up to
    // LowLevelHooksTimeout, 1 second at most), and the UI thread must not wait on them without a bound. The bound is the
    // gate's only: the rest of this call (the router's lock, posting the command, the warning's log call) is not in it.
    // Past it capture starts anyway, and what keeps that safe is the key view epoch (round 10, A12): capture's start and
    // end each give the view a new epoch before they clear it, so the repair's check before each key-up stops every key
    // whose reads could have seen a cleared view. The only key-up that can still reach Windows after capture has started
    // is one already past that check, decided on a view that was whole. Nothing here waits on the hook thread: the gate
    // is never the hook thread's, and posting the command does not wait for it.
    private void AdmitCapture()
    {
        Interlocked.Increment(ref _captureAdmissions);
        var admitted = false;
        try
        {
            admitted = Monitor.TryEnter(_repairGate, _captureAdmissionWait);
            Wake(_router.SetCaptureMode(true));
        }
        finally
        {
            if (admitted)
            {
                Monitor.Exit(_repairGate);
            }

            Interlocked.Decrement(ref _captureAdmissions);
        }

        if (!admitted)
        {
            _logger.LogWarning(
                "Binding capture started while a leaked-key release was still being sent (it outlasted the {Wait} ms wait); " +
                "that one release, decided before the capture, may arrive during it, and no other is sent until the capture " +
                "ends.",
                (int)_captureAdmissionWait.TotalMilliseconds);
        }
    }

    public void SetPaused(bool paused) => OnPauseRequested(paused, _router.SetPaused(paused));

    public void SetPaused(bool paused, long requestSequence)
    {
        if (_router.SetPaused(paused, requestSequence) is { } result)
        {
            OnPauseRequested(paused, result);
        }
        else
        {
            _logger.LogDebug("Ignored a hotkey pause request older than one already applied.");
        }
    }

    private void OnPauseRequested(bool paused, (bool Changed, HotkeyEngine? Wake) result)
    {
        if (!result.Changed)
        {
            return;
        }

        Wake(result.Wake);
        _logger.LogInformation(
            "Hotkey pass-through while paused {State}.", paused ? "enabled" : "disabled");
    }

    public void UpdateBinding(HotkeyBinding binding) => UpdateBindings(binding, DictationOnlyBinding);

    public void UpdateBindings(HotkeyBinding binding, HotkeyBinding? dictationOnlyBinding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var (changed, wake) = _router.UpdateBindings(binding, dictationOnlyBinding);
        if (!changed)
        {
            return;
        }

        Wake(wake);
        _logger.LogInformation(
            "Hotkey bindings updated: standard={Binding} ({Mode}, {Input}), dictation-only={DictationOnly}; mouse hook {MouseHook}.",
            DescribeBinding(binding), binding.Mode, MouseButtons.InputKind(binding),
            dictationOnlyBinding is null
                ? "disabled"
                : $"{DescribeBinding(dictationOnlyBinding)} ({dictationOnlyBinding.Mode}, {MouseButtons.InputKind(dictationOnlyBinding)})",
            MouseButtons.Uses(binding) || MouseButtons.Uses(dictationOnlyBinding) ? "wanted" : "not needed");
    }

    // Makes the hook thread apply queued commands now rather than at its next key event, so the
    // Deactivated that ends a dictation (entering capture, rebinding) is not held back until the
    // user happens to press a key. PostThreadMessage returns without waiting for the thread.
    private static void Wake(HotkeyEngine? engine)
    {
        if (engine is null)
        {
            return;
        }

        var threadId = engine.OwnerThreadId;
        if (threadId == 0 ||
            !NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_COMMANDS, nint.Zero, nint.Zero))
        {
            engine.CancelWake();
        }
    }

    private void ConsumeTransitions(HotkeyTransitionQueue queue)
    {
        try
        {
            while (queue.WaitForWork())
            {
                foreach (var transition in queue.TakeAll())
                {
                    DispatchTransition(transition);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Stop gave up waiting for this thread and released the queue; nothing is left to
            // dispatch.
        }
    }

    /// <summary>
    /// Whether the consumer raises this transition. A Deactivated always goes out (a redundant stop is harmless, a missed
    /// one is not). An Activated computed before a state-clearing request (capture mode, binding update, pause,
    /// reinstall) must not start a recording; the request's own Deactivated may already sit behind it in the queue. The
    /// epoch advances when the request is made, so this holds even while the hook thread has yet to apply it. Nor may an
    /// Activated queued before its engine applied a desktop switch, which would open the microphone after Windows locked:
    /// the engine advances its own activation epoch at the switch, before anything it queues for it. The epoch is the
    /// queuing engine's alone, so a switch a retired engine's hook thread finishes after a reinstall moves nothing the
    /// replacement's activations are judged by (the generation already rejects the retired engine's own).
    /// </summary>
    internal static bool ShouldDispatch(QueuedTransition item, HotkeyCommandRouter router) =>
        item.Transition != HotkeyTransition.Activated ||
        (router.IsCurrent(item.Generation) && item.Engine is { } engine && item.ActivationEpoch == engine.ActivationEpoch);

    // The consumer thread's step for one transition; internal so a test can dispatch one without a hook.
    internal void DispatchTransition(QueuedTransition item)
    {
        try
        {
            if (item.Transition == HotkeyTransition.Activated)
            {
                if (!ShouldDispatch(item, _router))
                {
                    // Shape only: which rule dropped it.
                    if (_router.IsCurrent(item.Generation))
                    {
                        _logger.LogInformation(
                            "Discarded a hotkey activation queued before a desktop switch or a mouse hook found removed.");
                    }
                    else
                    {
                        _logger.LogInformation("Discarded a stale hotkey activation from a superseded state epoch.");
                    }

                    return;
                }

                Activated?.Invoke(this, new HotkeyTriggerEventArgs(item.Trigger, activation: item.Activation));
            }
            else if (item.Transition == HotkeyTransition.Deactivated)
            {
                // Shape only, and only Debug: the engine sends this whenever its arbiter names an owner, and that can be
                // with nothing recording (an activation dropped above as queued before the switch is still followed by
                // this stop, and a press the controller turned away as still processing owns the dictation until the
                // release the controller then asks for reaches the hook). The controller's reason=DesktopSwitch line is
                // the record of a recording actually ended.
                if (item.Deactivation == HotkeyDeactivation.DesktopSwitch)
                {
                    _logger.LogDebug("Desktop switch: stop sent ({Trigger}).", item.Trigger);
                }
                else if (item.Deactivation == HotkeyDeactivation.MouseHookLost)
                {
                    _logger.LogDebug("Mouse hook found removed: stop sent ({Trigger}).", item.Trigger);
                }

                Deactivated?.Invoke(this, new HotkeyTriggerEventArgs(item.Trigger, item.Deactivation));

                // Every release is a cheap moment to verify no suppressed key leaked into the
                // system's logical "down" state (a hook deadline miss lets single events through). The
                // repair judges the key view this release was seen in, or nothing.
                if (item.AllowReconcile)
                {
                    ScheduleReconcile(item.KeyViewEpoch);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A hotkey event handler threw.");
        }
    }

    // Runs the leak check off the hook and consumer threads: GetAsyncKeyState cannot tell inside the
    // hook callback whether the key being processed is down (its async state updates after the callback
    // returns), and the input queue needs a beat to settle after the final suppressed key-up. The hook
    // callbacks never call this themselves; they signal HotkeyReconcileSignal, whose pool wait thread does.
    // The consumer asks for the repair on a dictation's release. repairAt is the key view epoch the repair
    // was asked for in (HotkeyEngine.KeyViewEpoch), or 0 for the mouse hook's sync alone.
    private void ScheduleReconcile(long repairAt)
    {
        if (repairAt != 0)
        {
            Interlocked.Increment(ref _repairPassesScheduled);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            RunReconcilePass(repairAt);
        });
    }

    // One pass. First the mouse hook's sync, which every pass does: an event that settled a debt, swallowed, let through
    // or forgiven by a new press, is the moment a drain-only mouse hook kept only for that debt stops being needed, and
    // the hook thread removes it at the sync this asks for now. Likewise a move ahead of another program's keyboard hook
    // that waited for a swallowed key's release is asked for again, once (stream RD). Then the leaked-key repair, only
    // when a signal asked for it (a key or button release the bindings swallowed, or a dictation's release), each key
    // judged and released under _repairGate and only while the engine's view may be trusted (KeyViewIsWhole, checked
    // there before the key's reads and again immediately before its key-up): never while capture is being admitted or
    // owns input, from Set's request until both machines have applied capture's end, because capture tracks no key and
    // every key the user holds for the chord would look leaked (review rounds 8 and 9, A9 and A11), and never once the
    // engine's view is not the one the request was made in (its epoch changed: a clear, or a new engine; round 10, A12).
    // Whatever stops a pass stops the rest of it, and nothing runs it again: after a clear, and after capture ends, the
    // keys held across it are missing from the engine's view, so a replay would release keys the user still holds; a
    // real leak is repaired at the next trigger. Nothing is logged under the gate.
    private void RunReconcilePass(long repairAt)
    {
        try
        {
            if (CurrentInstallation is { } current)
            {
                if (current is { MouseHookInstalled: true, MouseHookWanted: false })
                {
                    current.RequestMouseHookRefresh();
                }

                // A pass follows the release of a key the bindings swallowed: the moment a move ahead that waited for it
                // may be made (HookInstallation.MoveAhead).
                current.RetryDeferredMoveAhead();
            }

            if (repairAt == 0)
            {
                return;
            }

            var result = _reconciler.ReleaseLeakedKeys(Binding, repairAt);
            if (!result.Interrupted && DictationOnlyBinding is { } dictationOnly)
            {
                var secondaryResult = _reconciler.ReleaseLeakedKeys(dictationOnly, repairAt);
                result = new SuppressedKeyReconciler.Result(
                    result.Released.Concat(secondaryResult.Released).Distinct().ToList(),
                    result.Failed.Concat(secondaryResult.Failed).Distinct().ToList(),
                    secondaryResult.Interrupted);
            }

            if (result.Interrupted)
            {
                _logger.LogDebug(
                    "Leaked-key check stopped: binding capture is starting or owns input, the key view changed since it " +
                    "was asked for, or no hook is running.");
            }

            foreach (var key in result.Released)
            {
                _logger.LogWarning(
                    "Released leaked key 0x{Key:X2}: the system still held it down after the hook " +
                    "suppressed its release (a hook deadline miss let an event bypass suppression).",
                    key);
            }

            foreach (var key in result.Failed)
            {
                _logger.LogWarning(
                    "Key 0x{Key:X2} appears leaked-stuck but the synthetic release was rejected " +
                    "(SendInput blocked, e.g. by UIPI); it will be retried on the next release.",
                    key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suppressed-key reconciliation failed; skipping this pass.");
        }
        finally
        {
            Interlocked.Increment(ref _reconcilePasses);
        }
    }

    // Probe-based liveness for the keyboard hook and upkeep for its moves ahead, and upkeep for the mouse hook (see
    // MaintainMouseHookLocked).
    private void WatchdogTick()
    {
        try
        {
            lock (_sync)
            {
                if (!IsRunning)
                {
                    return;
                }

                ProbeKeyboardHookLocked();
                MaintainKeyboardHookLocked();
                MaintainMouseHookLocked();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey hook watchdog tick failed; will retry next period.");
        }
    }

    // Upkeep for the keyboard hook's moves ahead of a remote client's (HookInstallation.MoveAhead), each period, from the
    // watchdog and never from the hook thread, which must not log. A move Windows refused is reported, by its Win32
    // error, and so is a move that waited for a key Scribe swallowed to be released, by count. A replaced registration
    // found already gone when it was released means Windows removed it, so for a while no registration may have seen the
    // keys, and the key state is no longer to be trusted: the hook is reinstalled with a fresh engine, as for one that
    // stopped receiving events. Replaced registrations whose grace is over are released (the moves' own sequence
    // normally does it first). Callers hold _sync.
    private void MaintainKeyboardHookLocked()
    {
        if (_installation is not { } installation)
        {
            return;
        }

        if (!ReferenceEquals(_keyboardReportFor, installation))
        {
            _keyboardReportFor = installation;
            _keyboardMoveFailuresReported = 0;
            _keyboardFoundGoneReported = 0;
            _keyboardMovesDeferredReported = 0;
            _keyboardMovesDroppedReported = 0;
        }

        var (failures, error) = installation.KeyboardHookMoveFailures;
        if (failures != _keyboardMoveFailuresReported)
        {
            _keyboardMoveFailuresReported = failures;
            _logger.LogWarning(
                "Moving the keyboard hook ahead of a remote desktop client's failed (Win32 error {Error}, {Count} time(s) " +
                "for this hook thread); the current registration stays in place.",
                error,
                failures);
        }

        var deferred = installation.KeyboardHookMovesDeferred;
        if (deferred != _keyboardMovesDeferredReported)
        {
            _keyboardMovesDeferredReported = deferred;
            _logger.LogDebug(
                "A move of the keyboard hook ahead of a remote desktop client's waited for a key Scribe swallowed to be " +
                "released ({Count} time(s) for this hook thread), so the release reaches every hook that saw the press.",
                deferred);
        }

        var dropped = installation.KeyboardHookMovesDropped;
        if (dropped != _keyboardMovesDroppedReported)
        {
            _keyboardMovesDroppedReported = dropped;
            _logger.LogDebug(
                "A move of the keyboard hook ahead of a remote desktop client's was dropped: another window came to the " +
                "front before the hook thread made it ({Count} time(s) for this hook thread).",
                dropped);
        }

        var gone = installation.RetiredKeyboardHooksFoundGone;
        if (gone != _keyboardFoundGoneReported)
        {
            _keyboardFoundGoneReported = gone;
            _logger.LogWarning(
                "A keyboard hook registration replaced by a move ahead was already gone when it was released: Windows " +
                "removed it (a callback that missed the deadline), so keys may have gone unseen. Reinstalling.");
            ReinstallHookLocked();
            return;
        }

        if (installation.RetiredKeyboardHooks > 0)
        {
            installation.RequestReleaseRetired(force: false);
        }

        // A move still waiting is asked for again once a period, whatever held it back: a backstop for a retry its own
        // trigger could not deliver (a timer Windows refused, a posted message lost). Judged afresh on the hook thread.
        installation.RetryDeferredMoveAhead();
    }

    // A marker-tagged key-up for the unassigned VK 0xFF is inert for every app but still traverses
    // the hook, which counts it and then swallows it (KeyboardHookFilter.IsProbe), so it goes no further
    // than Scribe's hook. If nothing at all reached the callback since the PREVIOUS probe was armed, the
    // hook is gone (Windows removes one that missed the deadline, without notification) or a hook ahead
    // of it keeps the keys, and push-to-talk is dead until it is reinstalled. Mouse-only activity cannot
    // false-positive this check because the probe itself is keyboard input.
    //
    // The probe is withheld while the system is idle: injected input resets the power manager's
    // idle timer, so an unconditional probe every period stopped the machine from ever sleeping.
    // See HookLivenessProbe.ShouldWithholdProbe. Callers hold _sync.
    private void ProbeKeyboardHookLocked()
    {
        // On the lock screen / secure desktop the probe cannot reach this desktop's hook,
        // so every tick would look like a dead hook and churn a reinstall all night.
        // Skip the cycle entirely (and forget any probe already in flight) until the
        // interactive desktop is back.
        if (!NativeMethods.CanAccessInputDesktop())
        {
            _livenessProbe.Disarm();
            return;
        }

        if (_livenessProbe.IsHookDead(Interlocked.Read(ref _hookCallbackCount)))
        {
            // Two reasons, and the log cannot tell them apart: Windows removed the hook, or a hook registered after it keeps
            // the keys, as a Remote Desktop client's does once it registers on its window's activation and is called first.
            var inFront = _processNameOfWindow(_foregroundWindow());
            _logger.LogWarning(
                "The keyboard hook stopped receiving events: either Windows removed it (a callback that missed the " +
                "deadline), or another program's keyboard hook, such as a Remote Desktop client's, now receives keys " +
                "first and keeps them. In front: {App} (remote desktop client: {Remote}). Reinstalling.",
                inFront ?? "unknown",
                RemoteClientProcesses.IsRemoteClient(inFront));
            ReinstallHookLocked();
        }

        // The probe is injected input, so Windows counts it as user activity and resets the
        // idle timer the power manager sleeps against. Sending one every period regardless
        // of presence kept machines awake for as long as Scribe ran. Judge the outstanding
        // probe first (above), then go quiet once the system is genuinely idle.
        if (HookLivenessProbe.ShouldWithholdProbe(
                NativeMethods.TryGetSystemIdleTime(), WatchdogPeriod))
        {
            _livenessProbe.Disarm();
            return;
        }

        // Baseline BEFORE sending. Injected input is dispatched into the hook chain while
        // SendInput is still running, so a baseline taken afterwards would already include
        // the callback this probe caused and the probe could never be answered.
        _livenessProbe.Baseline(Interlocked.Read(ref _hookCallbackCount));

        // Arm the next check only when the probe actually left the building; a rejected
        // SendInput (UIPI, desktop switch mid-tick) must not read as a dead hook.
        _livenessProbe.Arm(
            NativeMethods.SendMarkedKeyEvent(NativeMethods.VK_PROBE, keyUp: true));
    }

    // Upkeep for the mouse hook, each period: first report what the hook thread did with it since the last period, then
    // ask that thread to register it afresh while a binding presses a mouse button, or to remove one no binding needs
    // any more (a wake that could not be posted after a change is made good here). Windows removes a low-level hook
    // whose callback misses the deadline, and the mouse hook is the one called for every pointer move, but it is not
    // probed: any mouse input that clicks nothing is a move, and a move reaching the desktop brings back a pointer hidden
    // while typing. Renewing injects nothing, adds nothing to any event and keeps the engine's state, and a registration
    // Windows removed is normally back within one period (best effort: a renewal Windows refuses keeps the old
    // registration, found gone or not, until a later one succeeds); that renewal is also when the engine learns the hook
    // was gone and ends a dictation a mouse button was driving (HotkeyEngine.OnMouseHookLost). Callers hold _sync.
    private void MaintainMouseHookLocked()
    {
        if (_installation is not { } installation)
        {
            return;
        }

        ReportMouseHookLocked(installation);
        if (installation.MouseHookWanted || installation.MouseHookInstalled)
        {
            installation.RequestMouseHookRefresh();
        }
    }

    // Logs each change in an installation's mouse hook once: from the threads that start and watch the hooks, never from
    // the hook thread, which must not log. Shapes only: installed or removed, why (a binding, or drain-only for a release
    // still owed), a Win32 error code, a count. Callers hold _sync.
    private void ReportMouseHookLocked(HookInstallation installation)
    {
        if (!ReferenceEquals(_mouseReportFor, installation))
        {
            _mouseReportFor = installation;
            _mouseReportedInstalled = false;
            _mouseReportedDrainOnly = false;
            _mouseReportedError = 0;
            _mouseReportedLosses = 0;
        }

        var installed = installation.MouseHookInstalled;
        var drainOnly = installed && installation.MouseHookDrainOnly;
        if (installed != _mouseReportedInstalled || drainOnly != _mouseReportedDrainOnly)
        {
            _mouseReportedInstalled = installed;
            _mouseReportedDrainOnly = drainOnly;
            if (drainOnly)
            {
                _logger.LogInformation(
                    "Mouse hook kept drain-only: no hotkey presses a mouse button, but a swallowed press still owes " +
                    "its release.");
            }
            else if (installed)
            {
                _logger.LogInformation("Mouse hook installed: a hotkey presses a mouse button.");
            }
            else
            {
                _logger.LogInformation("Mouse hook removed: no hotkey presses a mouse button and no release is owed.");
            }
        }

        var error = installation.MouseHookError;
        if (error != _mouseReportedError)
        {
            _mouseReportedError = error;
            if (error != 0 && installed)
            {
                _logger.LogWarning(
                    "Renewing the mouse hook failed (Win32 error {Error}); the current registration stays in place.",
                    error);
            }
            else if (error != 0)
            {
                _logger.LogWarning(
                    "Installing the mouse hook failed (Win32 error {Error}); a hotkey on a mouse button does nothing " +
                    "until it installs, which is tried again every {Seconds} s.",
                    error,
                    (int)WatchdogPeriod.TotalSeconds);
            }
        }

        var losses = installation.MouseHookLosses;
        if (losses != _mouseReportedLosses)
        {
            _mouseReportedLosses = losses;
            _logger.LogWarning(
                "The mouse hook was already gone when it was renewed ({Count} time(s) for this hook thread; Windows " +
                "removes a low-level hook whose callback misses the deadline). It is registered again, and any dictation " +
                "a mouse button was driving is ended, since its release may have come while no hook saw the mouse.",
                losses);
        }
    }

    // Tears down only the hook thread and spins a fresh one; the consumer thread, queue and
    // watchdog survive, while key state starts over in a new engine, and the new installation
    // makes its own reconcile signal (review round 11, A14). Callers hold _sync.
    private void ReinstallHookLocked()
    {
        var transitions = _transitions;
        if (transitions is null)
        {
            return;
        }

        // A probe armed against the hook we are about to destroy must never be judged against its
        // replacement: the new hook has raised no callbacks yet and would look dead on the next
        // tick, reinstalling again in a loop.
        _livenessProbe.Disarm();

        var previous = _installation;
        previous?.RequestQuit();
        previous?.Join(JoinTimeout);

        // A new engine rather than the old one on a new thread: if the join timed out, the old
        // hook thread may still be running, and two threads must never share key state. Beginning
        // it also retires the old engine, so that thread can no longer swallow a key or start or
        // stop a dictation.
        var (engine, interrupted) = _router.BeginEngine(transitions);

        // The reinstall may interrupt an active hold/toggle: the held key's eventual release can
        // no longer match the cleared state, so the recording must be stopped explicitly or the
        // microphone stays live until the next press.
        if (interrupted is { } trigger)
        {
            transitions.TryEnqueue(new QueuedTransition(
                HotkeyTransition.Deactivated, trigger, _router.CurrentGeneration, AllowReconcile: false));
        }

        var installation = new HookInstallation(this, engine, replacesRegistration: true);
        _installation = installation;
        if (!installation.Install(InstallTimeout))
        {
            _logger.LogError(
                installation.InstallError,
                "Reinstalling the keyboard hook failed; retrying on the next watchdog tick.");
        }
        else
        {
            _logger.LogInformation("Keyboard hook reinstalled.");
            LogMissingDesktopSwitchHook(installation);
            ReportMouseHookLocked(installation);
            installation.NoticeForegroundNow();
        }
    }

    // Named from the virtual-key codes, as Settings names them: a stored name can be an alias such as "Next".
    private static string DescribeBinding(HotkeyBinding binding) => HotkeyText.Describe(binding);

    public void Dispose() => Stop();

    /// <summary>
    /// One installation of the low-level hooks: its thread, its native handles, its callback
    /// delegates and the engine only that thread may drive. Keeping all of it per installation is
    /// what lets a reinstall proceed while a previous hook thread is still winding down.
    ///
    /// The mouse hook is part of the installation but exists only while a binding presses a mouse
    /// button (<see cref="HotkeyEngine.UsesMouseButtons"/>), or, drain-only, while a swallowed press
    /// still owes its release (<see cref="HotkeyEngine.OwesButtonRelease"/>): the thread installs or removes it between
    /// messages, whenever it has applied a change, so nobody who binds keys alone gets a system-wide
    /// mouse hook, and every mouse event on the desktop waits for this thread only while one is bound.
    /// It is not probed like the keyboard hook: the only probe that clicks nothing is a move, and a
    /// move reaching the desktop brings back a pointer hidden while typing. Instead the watchdog has
    /// the thread register it afresh every period (<see cref="SyncMouseHook"/>), so a registration
    /// Windows removed for missing the callback deadline is normally back within one period, and a dictation a
    /// mouse button was driving meanwhile is ended then (<see cref="HotkeyEngine.OnMouseHookLost"/>).
    /// </summary>
    private sealed class HookInstallation
    {
        private readonly HotkeyService _service;
        private readonly HotkeyEngine _engine;
        private readonly bool _replacesRegistration;

        // Where this installation's clock for the kept registrations' grace starts, on the service's TimeProvider (the
        // system's in production, a test's own in the tests).
        private readonly long _clockOrigin;

        // This installation's own, never shared with its replacement (review round 11, A14): a callback of this
        // installation that is still running after a reinstall (its thread can outlive the 2 s join) writes its repair
        // request here, where it cannot replace the replacement's pending one. Its requests are refused anyway, since
        // their key view is not the current engine's. The hook thread disposes it once its hooks are gone.
        private readonly HotkeyReconcileSignal _reconcileSignal;

        // The keyboard hook's registrations, each with the delegate Windows calls for it, rooted here for as long as the
        // hook can call it (a collected delegate behind a live hook crashes the process on the next key event). Slot 0 is
        // the first registration; a move ahead takes a free slot for the new one. A move registers its new one only while
        // fewer than RetiredHookRegistrations.Capacity replaced ones are kept (it waits otherwise, see MoveAhead), so at
        // most that many plus the current one and the new one are registered at once, and Capacity + 1 slots always leave
        // one free for it. _current is the registration made last: written and read on the hook thread only.
        private readonly KeyboardRegistration[] _registrations;
        private KeyboardRegistration _current;

        // Rooted for the same reason, for the mouse hook this thread installs while a binding uses a mouse button.
        private readonly NativeMethods.LowLevelMouseProc _mouseProc;

        // Rooted for the same reason, for the desktop-switch notifications set up beside the hook.
        private readonly NativeMethods.WinEventProc _desktopSwitchProc;

        // Rooted for the same reason, for the foreground notifications that tell a remote client came to the front.
        private readonly NativeMethods.WinEventProc _foregroundProc;

        // The keyboard events this installation's callback is passing on right now, and the registrations its moves
        // ahead replaced and still keeps (see MoveAhead). Hook thread only; made here, never on the callback path.
        private readonly KeyEventPassOn _passOn = new();
        private readonly RetiredHookRegistrations _retired = new();

        // The pool side of the moves ahead, and the hop that takes a foreground notice there. Disposed by the hook thread
        // once its hooks are gone.
        private readonly ForegroundNotice _foregroundNotice;
        private readonly KeyboardHookPrecedence _precedence;

        // The check a desktop-switch notice needs (see HotkeyEngine.OnDesktopSwitchNotice), one delegate for the whole
        // installation, so a notice allocates nothing.
        private readonly Func<bool?> _desktopReceivesInput;
        private readonly ManualResetEventSlim _installed = new(false);
        private readonly Thread _thread;
        private Exception? _installError;
        private nint _hookId;
        private nint _module;
        private int _abandoned;

        // Written by the hook thread before it sets _installed, read after Install saw it set.
        private bool _desktopSwitchHooked;
        private int _desktopSwitchError;
        private bool _foregroundHooked;
        private int _foregroundError;

        // What the moves ahead did, written by this installation's thread and read by the watchdog and tests.
        private long _moveAheads;
        private long _moveAheadFailures;
        private int _moveAheadError;
        private long _moveAheadsDeferred;
        private long _movesDropped;
        private long _movesDeferredForSlots;
        private long _moveRetryDueMs;
        private long _retiredReleased;

        // The move a swallowed key held back, kept for its retry: its foreground revision and window (see MoveAhead). Hook
        // thread only; _moveAheadDeferred says to other threads that one waits.
        private long _deferredRevision;
        private nint _deferredWindow;
        private bool _hasDeferredMove;

        // The thread timer that retries a move which waited for a slot (ArmMoveRetry), or zero. Hook thread only.
        private nuint _moveRetryTimer;
        private int _moveAheadDeferred;
        private long _retiredFoundGone;
        private long _echoes;
        private long _releaseRequestsHandled;

        // The mouse hook's registration and what became of the last attempts, written only by this installation's thread
        // between messages and read by the watchdog and tests.
        private nint _mouseHookId;
        private int _mouseHookError;
        private long _mouseHookRegistrations;
        private long _mouseHookLosses;

        // The WM_HOTKEY_REFRESH messages posted to this thread, refused, and handled; for tests (MouseHookRefreshes).
        private long _refreshesPosted;
        private long _refreshPostFailures;
        private long _refreshesHandled;

        /// <param name="replacesRegistration">
        /// True for a reinstall: its registration is the newest in the chain and replaces one of Scribe's that a hook ahead
        /// of it may have been keeping keys from, so the engine opens its uncertainty window as a move ahead does
        /// (<see cref="HotkeyEngine.OnRegisteredAhead"/>). The first install replaces none.
        /// </param>
        public HookInstallation(HotkeyService service, HotkeyEngine engine, bool replacesRegistration)
        {
            _service = service;
            _engine = engine;
            _replacesRegistration = replacesRegistration;
            _clockOrigin = service._time.GetTimestamp();

            // Read here, off the hook thread (it asks Windows for the keyboard repeat settings), and published to the engine,
            // which reads it on the callback's path. Each move ahead asks again, in case the user changed the settings.
            _engine.SetUncertaintyWindow(service._keyRepeatWindowMs());
            _reconcileSignal = new HotkeyReconcileSignal(service.ScheduleReconcile);
            _desktopReceivesInput = service._desktopReceivesInput;
            _registrations = new KeyboardRegistration[RetiredHookRegistrations.Capacity + 1];
            for (var slot = 0; slot < _registrations.Length; slot++)
            {
                _registrations[slot] = new KeyboardRegistration(this);
            }

            _current = _registrations[0];
            _mouseProc = MouseHookCallback;
            _desktopSwitchProc = DesktopSwitchCallback;
            _foregroundProc = ForegroundCallback;
            _precedence = new KeyboardHookPrecedence(
                service._foregroundWindow,
                service._processNameOfWindow,
                PublishedForegroundRevision,
                RequestMoveAhead,
                () => RequestReleaseRetired(force: false),
                service._logger,
                service._time);
            _foregroundNotice = new ForegroundNotice(_precedence.OnForegroundChanged);
            _thread = new Thread(Run)
            {
                Name = "Scribe.HotkeyHook",
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public Exception? InstallError => _installError;

        /// <summary>The reconcile signal this installation's hook callbacks raise; its own, for tests.</summary>
        public HotkeyReconcileSignal ReconcileSignal => _reconcileSignal;

        /// <summary>The engine this installation's thread drives.</summary>
        public HotkeyEngine Engine => _engine;

        /// <summary>
        /// After <see cref="Install"/> succeeded: null when desktop switches are being reported, otherwise the Win32 error
        /// SetWinEventHook left (0 when it left none).
        /// </summary>
        public int? DesktopSwitchHookError => _desktopSwitchHooked ? null : _desktopSwitchError;

        /// <summary>
        /// After <see cref="Install"/> succeeded: null when foreground changes are being reported, otherwise the Win32 error
        /// SetWinEventHook left (0 when it left none).
        /// </summary>
        public int? ForegroundHookError => _foregroundHooked ? null : _foregroundError;

        /// <summary>Any thread: the keyboard hook's current registration.</summary>
        public nint KeyboardHookHandle => Volatile.Read(ref _hookId);

        /// <summary>Any thread: how many registrations a move ahead replaced are still kept.</summary>
        public int RetiredKeyboardHooks => _retired.Count;

        /// <summary>Any thread: how many moves ahead registered the keyboard hook afresh.</summary>
        public long KeyboardHookMoves => Interlocked.Read(ref _moveAheads);

        /// <summary>Any thread: how many moves ahead Windows refused, and the Win32 error of the last one.</summary>
        public (long Failures, int LastError) KeyboardHookMoveFailures =>
            (Interlocked.Read(ref _moveAheadFailures), Volatile.Read(ref _moveAheadError));

        /// <summary>
        /// Any thread: how many moves ahead waited because a key whose press the engine swallowed was still held (see
        /// <see cref="MoveAhead"/>).
        /// </summary>
        public long KeyboardHookMovesDeferred => Interlocked.Read(ref _moveAheadsDeferred);

        /// <summary>Any thread: whether a move ahead is waiting for such a key's release.</summary>
        public bool KeyboardHookMoveDeferred => Volatile.Read(ref _moveAheadDeferred) != 0;

        /// <summary>
        /// Any thread: how many replaced registrations were already gone when they were released, which is how a
        /// registration Windows removed for a missed deadline shows.
        /// </summary>
        public long RetiredKeyboardHooksFoundGone => Interlocked.Read(ref _retiredFoundGone);

        /// <summary>Any thread, for tests: how many key events came back through a replaced registration and passed.</summary>
        public long KeyEventEchoes => Interlocked.Read(ref _echoes);

        /// <summary>
        /// Any thread, for tests: how many release requests this thread has handled, which a test uses as a barrier (the
        /// thread handles its messages in the order they were posted).
        /// </summary>
        public long RetiredReleaseRequestsHandled => Interlocked.Read(ref _releaseRequestsHandled);

        /// <summary>
        /// For a test that plays the hook thread between its messages on a desktop with no input: the delegate of the
        /// current keyboard registration, and of one a move replaced and still keeps, or null. Read after a barrier.
        /// </summary>
        public (NativeMethods.LowLevelKeyboardProc Current, NativeMethods.LowLevelKeyboardProc? Replaced) KeyboardProcsForTests
        {
            get
            {
                var current = _current;
                var replaced = _registrations.FirstOrDefault(r => r.Handle != 0 && !ReferenceEquals(r, current));
                return (current.Proc, replaced?.Proc);
            }
        }

        /// <summary>Any thread: whether the mouse hook is registered right now.</summary>
        public bool MouseHookInstalled => Volatile.Read(ref _mouseHookId) != 0;

        /// <summary>
        /// Any thread: whether this installation needs its mouse hook: a binding presses a mouse button, or a swallowed
        /// press still owes its release, which a drain-only hook must still swallow once the last mouse binding is gone.
        /// </summary>
        public bool MouseHookWanted => !_engine.IsRetired && (_engine.UsesMouseButtons || _engine.OwesButtonRelease);

        /// <summary>Any thread: whether the mouse hook is wanted only to drain a release still owed, for the log.</summary>
        public bool MouseHookDrainOnly => !_engine.IsRetired && !_engine.UsesMouseButtons && _engine.OwesButtonRelease;

        /// <summary>
        /// Any thread: the Win32 error of the last failed attempt to register the mouse hook, or 0 once one succeeded or
        /// none was needed.
        /// </summary>
        public int MouseHookError => Volatile.Read(ref _mouseHookError);

        /// <summary>Any thread: how many times the mouse hook was registered, the first time and every renewal.</summary>
        public long MouseHookRegistrations => Interlocked.Read(ref _mouseHookRegistrations);

        /// <summary>
        /// Any thread: how many renewals found the previous registration already gone (UnhookWindowsHookEx failed on it),
        /// which is what a hook Windows removed looks like.
        /// </summary>
        public long MouseHookLosses => Interlocked.Read(ref _mouseHookLosses);

        /// <summary>Any thread, for tests: the mouse hook's current registration, or zero.</summary>
        public nint MouseHookHandle => Volatile.Read(ref _mouseHookId);

        /// <summary>Starts the hook thread and waits for the hook; false when it failed or timed out.</summary>
        public bool Install(TimeSpan timeout)
        {
            _thread.Start();
            if (_installed.Wait(timeout))
            {
                return Volatile.Read(ref _hookId) != 0;
            }

            // Gave up waiting. A hook that still lands afterwards must not stay installed with
            // nobody listening to it (it would swallow the push-to-talk key for good), so the
            // thread checks this flag once installed, and a thread that already started pumping
            // is told to quit. Each side publishes before it reads the other's flag.
            Interlocked.Exchange(ref _abandoned, 1);
            RequestQuit();
            return false;
        }

        public void RequestQuit()
        {
            var threadId = _engine.OwnerThreadId;
            if (threadId != 0)
            {
                NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
            }
        }

        /// <summary>
        /// Any thread (the watchdog, once a period): asks this installation's thread to apply what is queued and then
        /// register its mouse hook afresh, or install or remove it to match the bindings. Never waits.
        /// </summary>
        public void RequestMouseHookRefresh()
        {
            var threadId = _engine.OwnerThreadId;
            if (threadId != 0 && NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_REFRESH, nint.Zero, nint.Zero))
            {
                Interlocked.Increment(ref _refreshesPosted);
            }
            else
            {
                Interlocked.Increment(ref _refreshPostFailures);
            }
        }

        /// <summary>
        /// Any thread, for tests: how many WM_HOTKEY_REFRESH messages were posted to this thread, how many posts found no
        /// thread or were refused, and how many the thread has handled (counted once its sync is done), so a test that waits
        /// for the mouse hook's removal can say which side stalled.
        /// </summary>
        public (long Posted, long PostFailures, long Handled) MouseHookRefreshes =>
            (Interlocked.Read(ref _refreshesPosted), Interlocked.Read(ref _refreshPostFailures), Interlocked.Read(ref _refreshesHandled));

        /// <summary>
        /// Any thread (the pool side of the moves ahead, tests): asks this installation's thread to register its keyboard
        /// hook afresh, ahead of every hook registered before, keeping its engine (see <see cref="MoveAhead"/>), for the
        /// foreground revision <paramref name="revision"/> and the window <paramref name="window"/> in front the request
        /// was judged on (the message carries both), or unconditionally for <see cref="ForcedMove"/>. Never waits.
        /// </summary>
        public void RequestMoveAhead(long revision, nint window)
        {
            var threadId = _engine.OwnerThreadId;
            if (threadId != 0 && !_engine.IsRetired)
            {
                // Off the hook thread: the keyboard repeat settings, which bound how long after the move an unseen key-down
                // is uncertain, read afresh in case the user changed them.
                _engine.SetUncertaintyWindow(_service._keyRepeatWindowMs());
                NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_MOVE_AHEAD, (nint)revision, window);
            }
        }

        /// <summary>
        /// Any thread: asks this installation's thread to release the registrations its moves replaced, once their grace
        /// is over, or, with <paramref name="force"/> (tests), whatever their age. Never waits.
        /// </summary>
        public void RequestReleaseRetired(bool force)
        {
            var threadId = _engine.OwnerThreadId;
            if (threadId != 0)
            {
                NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_RELEASE_RETIRED, force ? 1 : 0, nint.Zero);
            }
        }

        /// <summary>
        /// Any thread (a reconcile pass, which follows the release of a key the bindings swallowed): asks again for a move
        /// ahead that waited for such a key, once. The move, its foreground revision and window included, stays on the hook
        /// thread, which judges it afresh, whether the foreground it was judged on is still in front included. Never waits.
        /// </summary>
        public void RetryDeferredMoveAhead()
        {
            var threadId = _engine.OwnerThreadId;
            if (Interlocked.Exchange(ref _moveAheadDeferred, 0) != 0 && threadId != 0)
            {
                NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_MOVE_RETRY, nint.Zero, nint.Zero);
            }
        }

        /// <summary>
        /// The service, once the hooks are installed: hands the window in front now to the pool side of the moves ahead,
        /// as a foreground notice would, so a remote client already in front when the hook is installed is kept behind it
        /// too. Never waits.
        /// </summary>
        public void NoticeForegroundNow() => _foregroundNotice.Notify(_service._foregroundWindow());

        // The pool side's read of the latest foreground revision, as a method so the notice made after it in the constructor
        // is read when it is called.
        private long PublishedForegroundRevision() => _foregroundNotice.PublishedRevision;

        /// <summary>Any thread, for tests: publishes a foreground notice for <paramref name="window"/>, as the WinEvent callback does.</summary>
        public void NoticeForeground(nint window) => _foregroundNotice.Notify(window);

        /// <summary>A move asked for by a test: made without the foreground check.</summary>
        public const long ForcedMove = -1;

        /// <summary>Any thread: how many moves ahead were dropped because the foreground they were judged on had changed.</summary>
        public long KeyboardHookMovesDropped => Interlocked.Read(ref _movesDropped);

        /// <summary>Any thread: how many moves ahead waited because every kept registration was still inside its grace.</summary>
        public long KeyboardHookMovesDeferredForSlots => Interlocked.Read(ref _movesDeferredForSlots);

        /// <summary>Any thread, for tests: the wait the retry timer was last set for, in milliseconds.</summary>
        public long MoveRetryDueMs => Interlocked.Read(ref _moveRetryDueMs);

        /// <summary>Any thread: how many replaced registrations were released (found gone or not).</summary>
        public long RetiredKeyboardHooksReleased => Interlocked.Read(ref _retiredReleased);

        /// <summary>Any thread, for tests: asks this installation's thread to retry a move that waited. Never waits.</summary>
        public void RequestMoveRetry()
        {
            var threadId = _engine.OwnerThreadId;
            if (threadId != 0)
            {
                NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_MOVE_RETRY, nint.Zero, nint.Zero);
            }
        }

        public bool Join(TimeSpan timeout) => _thread.Join(timeout);

        private void Run()
        {
            // The handle stays per installation: a hook thread that outlives its 2-second join
            // (and got replaced by the watchdog) must unhook ITS hook on exit, never the
            // replacement's.
            nint hookId = 0;
            nint desktopSwitchHook = 0;
            nint foregroundHook = 0;
            try
            {
                // Before the hook exists, so no hook callback can run inside the peek (a peek also
                // delivers messages sent to this thread, which is how the hook is called).
                NativeMethods.EnsureMessageQueue();

                nint module = NativeMethods.GetModuleHandle(null);
                _module = module;
                var first = _registrations[0];
                _current = first;
                hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, first.Proc, module, 0);
                if (hookId == 0)
                {
                    _installError = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    first.Handle = hookId;
                    Volatile.Write(ref _hookId, hookId);

                    // The queue already exists, so publishing the id makes every later wake and
                    // quit deliverable; commands queued before this point apply here.
                    _engine.AttachOwner(NativeMethods.GetCurrentThreadId());

                    // A reinstall's registration is the newest in the chain, like a move's: from here, for a while, a key-down
                    // of a key the new engine has not seen is not swallowed (HotkeyEngine.OnRegisteredAhead). The tick is
                    // taken now, after the registration, on the clock key events are stamped with. Before _installed is set,
                    // so the service's reinstall returns with it done.
                    if (_replacesRegistration)
                    {
                        _engine.OnRegisteredAhead(unchecked((uint)Environment.TickCount));
                    }

                    // Desktop-switch notices, delivered on this thread by its message loop. On one that finds this
                    // desktop no longer receiving input, the engine resets its key state and ends a recording whose key
                    // is released on the lock screen or a secure desktop, where the hook is not called. Optional: without
                    // it the hook works as before and the service logs that it is missing.
                    desktopSwitchHook = NativeMethods.SetWinEventHook(
                        NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH,
                        NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH,
                        0,
                        _desktopSwitchProc,
                        0,
                        0,
                        NativeMethods.WINEVENT_OUTOFCONTEXT);
                    _desktopSwitchHooked = desktopSwitchHook != 0;
                    _desktopSwitchError = desktopSwitchHook == 0 ? Marshal.GetLastWin32Error() : 0;

                    // Foreground notices, delivered on this thread the same way. The callback only hands the window to the
                    // pool side of the moves ahead (KeyboardHookPrecedence), which looks its process up off this thread.
                    // Optional too: without it the watchdog still reinstalls a hook that stopped receiving keys.
                    foregroundHook = NativeMethods.SetWinEventHook(
                        NativeMethods.EVENT_SYSTEM_FOREGROUND,
                        NativeMethods.EVENT_SYSTEM_FOREGROUND,
                        0,
                        _foregroundProc,
                        0,
                        0,
                        NativeMethods.WINEVENT_OUTOFCONTEXT);
                    _foregroundHooked = foregroundHook != 0;
                    _foregroundError = foregroundHook == 0 ? Marshal.GetLastWin32Error() : 0;

                    // The mouse hook, only if a binding already presses a mouse button. Its failure leaves the keyboard
                    // hook working; the service reports it, and the watchdog's renewals retry it.
                    SyncMouseHook(refresh: false);
                }
            }
            catch (Exception ex)
            {
                _installError = ex;
            }
            finally
            {
                try
                {
                    _installed.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Nobody is waiting any more; the starter already treats the install as failed.
                }
            }

            if (hookId == 0)
            {
                DisposeSignals();
                return;
            }

            if (Volatile.Read(ref _abandoned) == 0)
            {
                // GetMessage returns 0 for WM_QUIT and -1 on failure; both end the pump.
                while (NativeMethods.GetMessage(out NativeMethods.MSG msg, nint.Zero, 0, 0) > 0)
                {
                    // Thread messages have no window, so DispatchMessage would drop these. Each applies the queued
                    // commands and then gives the mouse hook the state the bindings now ask for: outside any hook
                    // callback, so installing or removing it never runs inside one.
                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_HOTKEY_COMMANDS)
                    {
                        _engine.OnWake();
                        SyncMouseHook(refresh: false);
                        continue;
                    }

                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_HOTKEY_REFRESH)
                    {
                        _engine.OnWake();
                        SyncMouseHook(refresh: true);
                        Interlocked.Increment(ref _refreshesHandled);
                        continue;
                    }

                    // The keyboard hook's moves ahead and their clean-up, between messages like the mouse hook's changes.
                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_HOTKEY_MOVE_AHEAD)
                    {
                        MoveAhead((long)msg.wParam, msg.lParam);
                        continue;
                    }

                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_TIMER && _moveRetryTimer != 0 &&
                        (nuint)msg.wParam == _moveRetryTimer)
                    {
                        OnMoveRetryTimer();
                        continue;
                    }

                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_HOTKEY_MOVE_RETRY)
                    {
                        RetryDeferredMove();
                        continue;
                    }

                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_HOTKEY_RELEASE_RETIRED)
                    {
                        ReleaseRetired(force: msg.wParam != 0);
                        Interlocked.Increment(ref _releaseRequestsHandled);
                        continue;
                    }

                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }

            _engine.DetachOwner();
            KillMoveRetryTimer();
            RemoveMouseHook();
            if (foregroundHook != 0)
            {
                NativeMethods.UnhookWinEvent(foregroundHook);
            }

            if (desktopSwitchHook != 0)
            {
                NativeMethods.UnhookWinEvent(desktopSwitchHook);
            }

            // Every registration of this installation's keyboard hook, the ones its moves replaced and the current one:
            // its own handles, never a replacement's (the handles are per installation, see above).
            foreach (var registration in _registrations)
            {
                if (registration.Handle != 0)
                {
                    NativeMethods.UnhookWindowsHookEx(registration.Handle);
                    registration.Handle = 0;
                }
            }

            Volatile.Write(ref _hookId, 0);

            // No callback of this installation can run from here: its hooks are gone, and callbacks run only on this
            // thread, which ends now. A pass this signal already started still runs, and refuses its request.
            DisposeSignals();
        }

        // Hook thread only, at its end: the reconcile signal, and the foreground notice and the pool side of the moves
        // ahead, whose timer may still tick and then asks nothing of a thread that is gone.
        private void DisposeSignals()
        {
            _reconcileSignal.Dispose();
            _foregroundNotice.Dispose();
            _precedence.Dispose();
        }

        // Hook thread only, between messages (WM_HOTKEY_MOVE_AHEAD, or a retry of a move that waited), never inside a hook
        // callback. Registers the keyboard hook afresh, which Windows puts "at the beginning of a hook chain" (Hooks
        // Overview), so this installation's callback is called before every hook registered before it, a Remote Desktop
        // client's included (see KeyboardHookPrecedence). The same engine: no key state, epoch or latch changes, and a key
        // held through the move is still the key the engine holds.
        //
        // Only while the foreground it was judged on is still in front (review round 2, item 5): KeyboardHookPrecedence
        // judged a remote client in front at the foreground revision <revision>, with <window> in front, and a move posted or
        // held back since can be handled after the user left for a local app, where reordering the system-wide chain would
        // put Scribe's hook ahead of, say, a local key remapper for nothing. So right before registering, the move is dropped
        // unless no foreground notice was published since (ForegroundNotice.PublishedRevision, bumped by the WinEvent
        // callback on this thread) and the window is still in front: one GetForegroundWindow through the service's delegate,
        // which covers a change whose WinEvent this thread has not been handed yet. No process is looked up here. A test's
        // forced move (ForcedMove) skips the check.
        //
        // Not while a key whose press the engine swallowed is held (HotkeyEngine.HoldsSwallowedKey): its release would
        // then reach Scribe's hook first and be swallowed there, and a hook that was ahead of Scribe's when the key went
        // down, and may have forwarded that press into a remote session, would never see the release. The remote session
        // would keep the key down, and pressing it again would not help, because Scribe swallows that key. The move waits,
        // counted, with its revision and window kept on this thread, and the reconcile pass that follows the key's
        // release asks for it again (RetryDeferredMoveAhead), when it is judged afresh, the foreground check included; a
        // later move asked for meanwhile replaces it.
        //
        // The registration it replaces is kept for a grace (RetiredHookRegistrations), not released: an event already on
        // its way to it when the new one landed (inside a hook ahead of it) must still find a registration of this
        // installation, or the engine would never see it. Each registration has its own delegate, so the callback knows
        // which one an event came through: through a replaced one, an event the new registration already passed on comes
        // back as an echo and passes untouched (KeyEventPassOn), and an event that entered the chain before the move is
        // judged but never swallowed (KeyboardHookFilter.Route), since the hooks ahead of that registration have already
        // seen it. So no event is missed or decided twice, and none is swallowed behind a hook that saw it. A replaced
        // registration is released once its grace is over, at the next move or when the release is asked for. A
        // registration Windows refuses keeps the current one, counted for the watchdog to report. A retired engine moves
        // nothing: its thread may outlive a reinstall's join, and its hook must not go ahead of the replacement's.
        private void MoveAhead(long revision, nint window)
        {
            _service.BeforeKeyboardMoveForTests?.Invoke();
            if (_engine.IsRetired)
            {
                return;
            }

            if (!StillInFront(revision, window))
            {
                Interlocked.Increment(ref _movesDropped);
                if (_hasDeferredMove && !StillInFront(_deferredRevision, _deferredWindow))
                {
                    ForgetDeferredMove();
                }

                return;
            }

            if (_engine.HoldsSwallowedKey)
            {
                Interlocked.Increment(ref _moveAheadsDeferred);
                DeferMove(revision, window);
                return;
            }

            // Never release a replaced registration inside its grace (review round 2, item 3): with every slot holding one,
            // the move waits, kept with its revision and window like a move a key holds back, until the oldest one's grace
            // ends, when this thread's own timer retries it; a later move asked for meanwhile replaces it.
            ReleaseRetired(force: false);
            if (_retired.IsFull)
            {
                Interlocked.Increment(ref _movesDeferredForSlots);
                DeferMove(revision, window);
                ArmMoveRetry(_retired.MillisecondsUntilOldestExpires(NowMs(), RetiredHookRegistrations.GraceMs));
                return;
            }

            ForgetDeferredMove();
            var registration = FreeRegistration();
            var replacement = registration is null
                ? 0
                : NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, registration.Proc, _module, 0);
            if (registration is null || replacement == 0)
            {
                Volatile.Write(ref _moveAheadError, registration is null ? 0 : Marshal.GetLastWin32Error());
                Interlocked.Increment(ref _moveAheadFailures);
                return;
            }

            registration.Handle = replacement;
            var replaced = _current;
            _current = registration;
            Volatile.Write(ref _hookId, replacement);

            // Ahead of every hook registered before it now: for a while a key-down of a key the engine has not seen is not
            // swallowed, since a hook that was ahead may have kept its press (HotkeyEngine.OnRegisteredAhead). Taken before
            // this thread takes another message, so before any event can reach the new registration.
            _engine.OnRegisteredAhead(unchecked((uint)Environment.TickCount));
            _ = _retired.Retire(replaced.Handle, NowMs());

            Interlocked.Increment(ref _moveAheads);
        }

        // Hook thread only: whether a move judged at the foreground revision <revision> with <window> in front may still be
        // made (see MoveAhead). A published revision the move did not see means the foreground changed since.
        private bool StillInFront(long revision, nint window) =>
            revision == ForcedMove ||
            (revision == _foregroundNotice.PublishedRevision && _service._foregroundWindow() == window);

        // Hook thread only: the move that waited, kept for its retry, and the flag the reconcile pass takes to ask for it.
        private void DeferMove(long revision, nint window)
        {
            _deferredRevision = revision;
            _deferredWindow = window;
            _hasDeferredMove = true;
            Volatile.Write(ref _moveAheadDeferred, 1);
        }

        private void ForgetDeferredMove()
        {
            _hasDeferredMove = false;
            Volatile.Write(ref _moveAheadDeferred, 0);
            KillMoveRetryTimer();
        }

        // Hook thread only: the retry of a move that waited for a kept registration's grace to end, after waitMs, on a thread
        // timer of this thread's own. SetTimer with no window and no TimerProc has the system post WM_TIMER to this thread's
        // queue, which the loop handles between messages like the rest; "If a NULL value for hWnd is passed in along with an
        // nIDEvent of an existing timer, that timer will be replaced", and a wait below USER_TIMER_MINIMUM (10 ms) is raised to
        // it (SetTimer, Learn). A timer Windows refuses leaves the move to the next move asked for, or to the watchdog's
        // retry each period.
        private void ArmMoveRetry(long waitMs)
        {
            Interlocked.Exchange(ref _moveRetryDueMs, waitMs);
            var timer = NativeMethods.SetTimer(0, _moveRetryTimer, (uint)Math.Clamp(waitMs, 1, int.MaxValue), 0);
            if (timer != 0)
            {
                _moveRetryTimer = timer;
            }
        }

        private void OnMoveRetryTimer()
        {
            KillMoveRetryTimer();
            RetryDeferredMove();
        }

        private void KillMoveRetryTimer()
        {
            if (_moveRetryTimer != 0)
            {
                NativeMethods.KillTimer(0, _moveRetryTimer);
                _moveRetryTimer = 0;
            }
        }

        // Hook thread only, between messages (WM_HOTKEY_MOVE_RETRY): the move that waited, if one still does, judged afresh.
        private void RetryDeferredMove()
        {
            if (_hasDeferredMove)
            {
                MoveAhead(_deferredRevision, _deferredWindow);
            }
        }

        // Hook thread only: milliseconds on the service's clock since this installation was made.
        private long NowMs() => (long)_service._time.GetElapsedTime(_clockOrigin).TotalMilliseconds;

        // Hook thread only: a slot no registration holds, or null. There is always one (see _registrations); a move that
        // found none would be refused as a failed registration rather than throw on the hook thread.
        private KeyboardRegistration? FreeRegistration()
        {
            foreach (var registration in _registrations)
            {
                if (registration.Handle == 0 && !ReferenceEquals(registration, _current))
                {
                    return registration;
                }
            }

            return null;
        }

        // Hook thread only, between messages. Releases the replaced registrations whose grace is over, or all of them.
        private void ReleaseRetired(bool force)
        {
            var now = NowMs();
            for (var retired = force ? _retired.TakeAny() : _retired.TakeExpired(now, RetiredHookRegistrations.GraceMs);
                 retired != 0;
                 retired = force ? _retired.TakeAny() : _retired.TakeExpired(now, RetiredHookRegistrations.GraceMs))
            {
                Release(retired);
            }
        }

        // A replaced registration that can no longer be released was already gone: Windows removed it, which it does to a
        // hook that missed its deadline, so for a while no registration may have seen the keys. Counted for the watchdog,
        // which reinstalls the hook for it as for one that stopped receiving events. Its slot is free again either way.
        private void Release(nint handle)
        {
            foreach (var registration in _registrations)
            {
                if (registration.Handle == handle)
                {
                    registration.Handle = 0;
                }
            }

            Interlocked.Increment(ref _retiredReleased);
            if (!NativeMethods.UnhookWindowsHookEx(handle))
            {
                Interlocked.Increment(ref _retiredFoundGone);
            }
        }

        // Hook thread only, between messages, never inside a hook callback. Installs the mouse hook while a binding presses
        // a mouse button, or, drain-only, while a swallowed press still owes its release after the last such binding went
        // (the engine judges that release and nothing else: no binding can use the button), and removes it once neither
        // holds, or once this engine is retired (a pass-through hook would still make every pointer move wait for this
        // thread). With refresh, a hook that is wanted and installed is registered afresh: the new registration first and
        // the old one released after it, both before this thread takes another message, so the engine keeps its state
        // through the change. That is not a seal: Windows installs a hook "at the beginning of a hook chain" and each
        // procedure passes an event on to the next one only, so an event already inside a newer program's hook when the
        // swap happens reaches neither registration. What that can cost is decided where it matters, when an owed
        // release is made, on Windows' own view (HotkeyEngine.OnMouseButtonEvent). A failed registration keeps the one in
        // place. An old registration that can no longer be released was already gone, which is how a hook Windows removed
        // after a missed deadline shows; it is counted for the watchdog to report, and the engine told.
        private void SyncMouseHook(bool refresh)
        {
            var current = _mouseHookId;
            if (!MouseHookWanted)
            {
                Volatile.Write(ref _mouseHookError, 0);
                RemoveMouseHook();
                return;
            }

            if (current != 0 && !refresh)
            {
                return;
            }

            var replacement = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, _module, 0);
            if (replacement == 0)
            {
                Volatile.Write(ref _mouseHookError, Marshal.GetLastWin32Error());
                return;
            }

            // The old registration is released, or found already gone, before the count moves, so a reader that sees
            // the count sees everything this renewal did. Found gone, it was removed by Windows (a missed deadline), and
            // for as long as it was gone no hook saw the mouse: the engine ends a dictation a button was driving, since
            // its release may be the input nobody saw, and drops every release it owed, here between messages and
            // before any button event reaches the new registration. A registration where there was none needs nothing
            // of the kind: no debt is committed while this installation has no mouse hook, and a reinstall's engine
            // starts owing nothing.
            Volatile.Write(ref _mouseHookId, replacement);
            Volatile.Write(ref _mouseHookError, 0);
            if (current != 0 && !NativeMethods.UnhookWindowsHookEx(current))
            {
                Interlocked.Increment(ref _mouseHookLosses);
                _engine.OnMouseHookLost();

                // The loss dropped what the engine owed, so a drain-only hook has nothing left to judge: removed now, in
                // this renewal, rather than at the next sync.
                if (!MouseHookWanted)
                {
                    RemoveMouseHook();
                }
            }

            Interlocked.Increment(ref _mouseHookRegistrations);
        }

        // Hook thread only.
        private void RemoveMouseHook()
        {
            var current = _mouseHookId;
            if (current != 0)
            {
                Volatile.Write(ref _mouseHookId, 0);
                NativeMethods.UnhookWindowsHookEx(current);
            }
        }

        // Runs on this installation's thread, from GetMessage, for every desktop-switch notice. Like the keyboard callback
        // it never waits for another thread and never logs; the engine checks again whether this desktop has actually
        // lost input before it ends anything. The real check is two user32 calls that wait for nothing and report a
        // failure by their return value, which the engine takes as "do nothing".
        private void DesktopSwitchCallback(
            nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
        {
            if (eventType == NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH)
            {
                _engine.OnDesktopSwitchNotice(_desktopReceivesInput);
            }
        }

        // Runs on this installation's thread, from GetMessage, for every foreground change on the desktop. Like the other
        // callbacks it never waits, logs or allocates: one volatile write and a SetEvent hand the window to a pool thread,
        // which looks up whose it is (KeyboardHookPrecedence). A retired installation hands nothing on.
        private void ForegroundCallback(
            nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
        {
            if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND && !_engine.IsRetired)
            {
                _foregroundNotice.Notify(hwnd);
            }
        }

        // Runs on this installation's thread, inside GetMessage, for every keyboard event on the
        // desktop, through the registration it came through (each has its own delegate, see
        // KeyboardRegistration). It takes no lock and never logs: the only shared state it touches is
        // interlocked counters, lock-free queues and kernel events. Its one native query besides
        // CallNextHookEx is GetAsyncKeyState, made only as ChordStateMachine describes, and both are
        // prelinked before the hook exists (NativeMethods.PrelinkHookCalls). CallNextHookEx is not a
        // quick return: it "calls the next hook in the chain" and returns that hook's result (Learn), so
        // how long it takes is up to that hook; its hook handle is ignored, so none is read here. The
        // decision is KeyboardHookFilter.Route's: Scribe's own input passes, except the watchdog's probe,
        // which stops here once counted; while a move ahead keeps a replaced registration, this callback
        // can be called for one event through two registrations, the replaced one inside the new one's
        // CallNextHookEx, and that echo passes untouched, so each event is judged once; and an event
        // that reaches only a replaced registration is judged but never swallowed.
        private nint HookCallback(KeyboardRegistration registration, int nCode, nint wParam, nint lParam)
        {
            // The watchdog's liveness signal. Incremented before any filtering so the synthetic
            // probe (which is marker-tagged and handled below) still proves the hook is installed.
            Interlocked.Increment(ref _service._hookCallbackCount);

            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
            }

            int message = (int)wParam;
            bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
            if (!isDown && !isUp)
            {
                return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
            }

            // Direct field reads instead of PtrToStructure: the callback races a hard OS deadline
            // (LowLevelHooksTimeout) and a miss gets the hook silently removed, so every event must stay
            // as cheap as possible.
            var identity = KeyboardHookFilter.Identity(lParam);
            var extraInfo = (nuint)Marshal.ReadIntPtr(lParam, KeyboardHookFilter.ExtraInfoOffset);
            var route = KeyboardHookFilter.Route(
                _engine, _passOn, ReferenceEquals(registration, _current), identity, isDown, extraInfo);
            if (route.RepairAt != 0)
            {
                // The view this release was judged in: the repair judges that one or none (review round 10, A12).
                _reconcileSignal.Signal(route.RepairAt);
            }

            if (route.Swallow)
            {
                return 1;
            }

            if (!route.TrackPass)
            {
                if (route.Echo)
                {
                    Interlocked.Increment(ref _echoes);
                }

                return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
            }

            _passOn.Enter(identity);
            try
            {
                return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
            }
            finally
            {
                _passOn.Leave();
            }
        }

        // Runs on this installation's thread, inside GetMessage, for every mouse event on the desktop while a binding
        // presses a mouse button, and the pointer waits for it. So the first thing it does, before reading any field or
        // touching the engine, is the one comparison that passes on every message but the middle and side buttons going
        // down or up: moves, the wheels and the left and right buttons cost exactly that and CallNextHookEx, which calls
        // the next hook in the chain and returns its result (Learn), as in the keyboard callback. The comparison and the
        // first CallNextHookEx allocate nothing (MouseButtonRound8Tests measures both cold; Windows' entry into this
        // callback cannot be measured there). The rest is MouseHookFilter's, which like the keyboard callback takes no
        // lock and never logs. CallNextHookEx ignores its hook handle, so none is read here.
        private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
        {
            var message = unchecked((int)wParam);
            if (!MouseHookFilter.IsButtonMessage(message))
            {
                return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
            }

            return MouseHookFilter.Swallows(nCode, message, lParam, _engine, _reconcileSignal)
                ? 1
                : NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }

        /// <summary>
        /// One registration of the installation's keyboard hook, with the delegate Windows calls for it. Windows calls
        /// the procedure a registration was made with, so a delegate of its own per registration is how the callback
        /// knows whether an event came through the current registration or one a move ahead replaced
        /// (<see cref="HookCallback"/>). Made with the installation, never on the callback path; the delegate is rooted
        /// here for as long as a hook can call it.
        /// </summary>
        private sealed class KeyboardRegistration
        {
            private readonly HookInstallation _installation;

            public KeyboardRegistration(HookInstallation installation)
            {
                _installation = installation;
                Proc = Callback;
            }

            public NativeMethods.LowLevelKeyboardProc Proc { get; }

            // The registration's handle while it is registered, otherwise zero. Hook thread only.
            public nint Handle { get; set; }

            private nint Callback(int nCode, nint wParam, nint lParam) =>
                _installation.HookCallback(this, nCode, wParam, lParam);
        }
    }
}

/// <summary>
/// Lets at most one trigger own the dictation, arbitrated without a lock, and remembers which press owns it (the
/// activation number its Activated carried). One word holds the owner, its press and retirement, so "which dictation did
/// this engine leave running", "which press started it" and "this engine may no longer start or stop one" are settled
/// by one atomic operation rather than flags and reads that a concurrent owner thread could interleave.
/// </summary>
internal sealed class HotkeyTriggerArbiter
{
    // Zero means no trigger owns a dictation, and Retired is terminal. Otherwise the word is the owning press's activation
    // number above its trigger's code, so a press and its trigger are claimed, compared and released together.
    private const long Retired = -1;
    private const int CodeBits = 8;
    private const long CodeMask = (1L << CodeBits) - 1;

    private long _word;

    /// <summary>Any thread: whether a press owns the dictation (false while nobody does, and once retired).</summary>
    public bool HasOwner => IsOwned(Volatile.Read(ref _word));

    /// <summary>Claims the dictation for this press, unless a trigger already owns one or the engine was retired.</summary>
    public bool TryActivate(HotkeyTrigger trigger, long activation) =>
        Interlocked.CompareExchange(ref _word, Encode(trigger, activation), 0) == 0;

    /// <summary>Releases the dictation if <paramref name="trigger"/> owns it; false otherwise, and once retired.</summary>
    public bool TryDeactivate(HotkeyTrigger trigger)
    {
        // Only the owner thread and a retirement write the word, so a failed exchange means the engine was retired.
        var word = Volatile.Read(ref _word);
        return IsOwned(word) && TriggerOf(word) == trigger && Interlocked.CompareExchange(ref _word, 0, word) == word;
    }

    /// <summary>
    /// Clears the active trigger and returns it, or <paramref name="fallback"/> when none was
    /// active. Returns null once retired: the retirement already reported the active trigger, so the
    /// caller must not stop anything itself.
    /// </summary>
    public HotkeyTrigger? TryTake(HotkeyTrigger fallback)
    {
        var word = Volatile.Read(ref _word);
        while (word != Retired)
        {
            var observed = Interlocked.CompareExchange(ref _word, 0, word);
            if (observed == word)
            {
                return word == 0 ? fallback : TriggerOf(word);
            }

            word = observed;
        }

        return null;
    }

    /// <summary>Clears the active trigger, unless retired.</summary>
    public void Reset() => _ = TryTake(HotkeyTrigger.Standard);

    /// <summary>
    /// Clears the active trigger and returns it; null when no trigger owns a dictation, or once retired. Unlike
    /// <see cref="TryTake"/>, whose fallback suits a state clear reporting a machine's own deactivation, this names only a
    /// dictation this engine really started, which a desktop switch needs: a machine can still report itself active
    /// after the arbiter refused it, or after the owner was released.
    /// </summary>
    public HotkeyTrigger? TryTakeActive()
    {
        var word = Volatile.Read(ref _word);
        while (IsOwned(word))
        {
            var observed = Interlocked.CompareExchange(ref _word, 0, word);
            if (observed == word)
            {
                return TriggerOf(word);
            }

            word = observed;
        }

        return null;
    }

    /// <summary>
    /// Releases the dictation if the press <paramref name="activation"/> still owns it, and returns the trigger that owns
    /// one afterwards: null when nobody does (that press was released here, or nothing owned one) or once retired, and
    /// otherwise the trigger of the newer press that owns it, which keeps it.
    /// </summary>
    public HotkeyTrigger? ReleaseActivation(long activation)
    {
        var word = Volatile.Read(ref _word);
        while (IsOwned(word))
        {
            if (ActivationOf(word) != activation)
            {
                return TriggerOf(word);
            }

            var observed = Interlocked.CompareExchange(ref _word, 0, word);
            if (observed == word)
            {
                return null;
            }

            word = observed;
        }

        return null;
    }

    /// <summary>
    /// Refuses everything from now on, and returns the trigger that was still active (a dictation
    /// that was started and never stopped), or null.
    /// </summary>
    public HotkeyTrigger? Retire()
    {
        var word = Interlocked.Exchange(ref _word, Retired);
        return IsOwned(word) ? TriggerOf(word) : null;
    }

    // Zero is reserved for no owner and Retired is negative, so compare-exchange can arbitrate without a lock and an owned
    // word is always positive.
    private static bool IsOwned(long word) => word > 0;

    private static long Encode(HotkeyTrigger trigger, long activation) => (activation << CodeBits) | ((long)trigger + 1);

    private static HotkeyTrigger TriggerOf(long word) => (HotkeyTrigger)((word & CodeMask) - 1);

    private static long ActivationOf(long word) => word >> CodeBits;
}
