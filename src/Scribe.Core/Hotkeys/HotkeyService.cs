using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

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
    // Computed once: the two KBDLLHOOKSTRUCT fields the callback reads. Direct field reads keep
    // the callback under the OS deadline; PtrToStructure would marshal the whole struct per event.
    private static readonly int VkCodeOffset =
        (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.vkCode));
    private static readonly int ExtraInfoOffset =
        (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.dwExtraInfo));

    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<HotkeyService> _logger;
    private readonly Func<bool?> _desktopReceivesInput;
    private readonly object _sync = new();
    private readonly HotkeyCommandRouter _router;
    private readonly SuppressedKeyReconciler _reconciler;

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
    /// (<see cref="HotkeyTriggerEventArgs.Activation"/>), zero otherwise.
    /// </summary>
    internal readonly record struct QueuedTransition(
        HotkeyTransition Transition,
        HotkeyTrigger Trigger,
        long Generation,
        bool AllowReconcile,
        HotkeyDeactivation Deactivation = HotkeyDeactivation.Released,
        long ActivationEpoch = 0,
        HotkeyEngine? Engine = null,
        long Activation = 0);

    private HotkeyTransitionQueue? _transitions;
    private HotkeyReconcileSignal? _reconcileSignal;
    private HookInstallation? _installation;
    private Thread? _consumerThread;
    private Timer? _watchdog;
    private long _hookCallbackCount;
    private readonly HookLivenessProbe _livenessProbe = new();

    // What the log last said about an installation's mouse hook (ReportMouseHookLocked). Guarded by _sync.
    private HookInstallation? _mouseReportFor;
    private bool _mouseReportedInstalled;
    private int _mouseReportedError;
    private long _mouseReportedLosses;

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
    internal HotkeyService(ILogger<HotkeyService> logger, HotkeyCommandRouter router, Func<bool?> desktopReceivesInput)
    {
        _logger = logger;
        _desktopReceivesInput = desktopReceivesInput;
        _router = router;
        _reconciler = CreateReconciler(_router, NativeMethods.IsKeyLogicallyDown, ReleaseLeakedInput);
    }

    /// <summary>
    /// The leak check the service runs, over <paramref name="router"/>'s current engine; <paramref name="isLogicallyDown"/>
    /// is Windows' view and <paramref name="releaseInput"/> the repair, which a test replaces. Keys only: no mouse button
    /// is ever a candidate (see <see cref="SuppressedKeyReconciler"/>).
    /// </summary>
    internal static SuppressedKeyReconciler CreateReconciler(
        HotkeyCommandRouter router, Func<uint, bool> isLogicallyDown, Func<uint, bool> releaseInput) =>
        new(isLogicallyDown, router.IsPressed, releaseInput);

    // A key left down gets a marked key-up. Scribe injects no mouse input at all: the leak check never lists a mouse button.
    private static bool ReleaseLeakedInput(uint key) => NativeMethods.SendMarkedKeyEvent((ushort)key, keyUp: true);

    // GetAsyncKeyState on the hook path, rarely: only on a press that completes a bare Page Up or Page Down binding while
    // the hook's view shows a modifier held, and only about that modifier (see ChordStateMachine).
    private static HotkeyCommandRouter CreateRouter(HotkeyBinding binding) => new(binding, NativeMethods.IsKeyLogicallyDown);

    public bool IsRunning { get; private set; }

    public HotkeyBinding Binding => _router.Binding;

    public HotkeyBinding? DictationOnlyBinding => _router.DictationOnlyBinding;

    /// <summary>What the hook asks about a modifier its own view holds (see ChordStateMachine); for tests.</summary>
    internal Func<uint, bool>? WindowsKeyState => _router.WindowsKeyState;

    /// <summary>How many desktop switches the current hook's engine has applied.</summary>
    internal long DesktopSwitchesSeen => _router.CurrentEngine?.DesktopSwitches ?? 0;

    /// <summary>How many desktop-switch notices reached the current hook's engine on its own thread; for the wiring test.</summary>
    internal long DesktopSwitchNoticesSeen => _router.CurrentEngine?.DesktopSwitchNotices ?? 0;

    /// <summary>Whether the current installation's mouse hook is registered; for tests.</summary>
    internal bool MouseHookInstalled => CurrentInstallation?.MouseHookInstalled == true;

    /// <summary>How many times the current installation registered its mouse hook; for tests.</summary>
    internal long MouseHookRegistrations => CurrentInstallation?.MouseHookRegistrations ?? 0;

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
            var reconcileSignal = new HotkeyReconcileSignal(ScheduleReconcile);
            var (engine, _) = _router.BeginEngine(transitions);
            var installation = new HookInstallation(this, engine, reconcileSignal);

            if (!installation.Install(InstallTimeout))
            {
                _router.EndEngine(engine);
                transitions.Complete();
                transitions.Dispose();
                reconcileSignal.Dispose();
                throw new InvalidOperationException(
                    "Failed to install the global keyboard hook.", installation.InstallError);
            }

            _transitions = transitions;
            _reconcileSignal = reconcileSignal;
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
    }

    public void Stop()
    {
        HookInstallation? installation;
        Thread? consumerThread;
        HotkeyTransitionQueue? transitions;
        HotkeyReconcileSignal? reconcileSignal;

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
            reconcileSignal = _reconcileSignal;
            _installation = null;
            _consumerThread = null;
            _transitions = null;
            _reconcileSignal = null;

            // From here on, requests only update the published configuration; the next Start
            // builds its engine from it.
            _router.EndEngine();
            installation?.RequestQuit();
            transitions?.Complete();
        }

        installation?.Join(JoinTimeout);
        consumerThread?.Join(JoinTimeout);
        transitions?.Dispose();
        reconcileSignal?.Dispose();

        _logger.LogInformation("Hotkey hook removed.");
    }

    public void CancelToggle(long activation) => Wake(_router.CancelToggle(activation));

    public void SetCaptureMode(bool enabled)
    {
        Wake(_router.SetCaptureMode(enabled));
        _logger.LogInformation("Hotkey binding capture mode {State}.", enabled ? "enabled" : "disabled");
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
                // system's logical "down" state (a hook deadline miss lets single events through).
                if (item.AllowReconcile)
                {
                    ScheduleReconcile();
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
    // callback never calls this itself; it signals HotkeyReconcileSignal, whose pool wait thread does.
    private void ScheduleReconcile() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);

            // A swallowed release asks for this check, so it is also the moment a drain-only mouse hook, kept only for
            // that release, stops being needed: the hook thread removes it at its next sync, which this asks for now.
            if (CurrentInstallation is { MouseHookInstalled: true, MouseHookWanted: false } drained)
            {
                drained.RequestMouseHookRefresh();
            }

            var result = _reconciler.ReleaseLeakedKeys(Binding);
            if (DictationOnlyBinding is { } dictationOnly)
            {
                var secondaryResult = _reconciler.ReleaseLeakedKeys(dictationOnly);
                result = new SuppressedKeyReconciler.Result(
                    result.Released.Concat(secondaryResult.Released).Distinct().ToList(),
                    result.Failed.Concat(secondaryResult.Failed).Distinct().ToList());
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
    });

    // Probe-based liveness for the keyboard hook, and upkeep for the mouse hook (see MaintainMouseHookLocked).
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
                MaintainMouseHookLocked();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey hook watchdog tick failed; will retry next period.");
        }
    }

    // A marker-tagged key-up for the unassigned VK 0xFF is inert for every app but still traverses
    // the hook. If nothing at all reached the callback since the PREVIOUS probe was armed, Windows
    // silently removed the hook (documented behavior after a deadline miss) and push-to-talk is dead
    // until it is reinstalled. Mouse-only activity cannot false-positive this check because the probe
    // itself is keyboard input.
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
            _logger.LogWarning(
                "The keyboard hook stopped receiving events (Windows removes low-level " +
                "hooks that miss the callback deadline, without notification). Reinstalling.");
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
    // the hook thread, which must not log. Shapes only: installed or removed, a Win32 error code, a count. Callers hold
    // _sync.
    private void ReportMouseHookLocked(HookInstallation installation)
    {
        if (!ReferenceEquals(_mouseReportFor, installation))
        {
            _mouseReportFor = installation;
            _mouseReportedInstalled = false;
            _mouseReportedError = 0;
            _mouseReportedLosses = 0;
        }

        var installed = installation.MouseHookInstalled;
        if (installed != _mouseReportedInstalled)
        {
            _mouseReportedInstalled = installed;
            if (installed)
            {
                _logger.LogInformation("Mouse hook installed: a hotkey presses a mouse button.");
            }
            else
            {
                _logger.LogInformation("Mouse hook removed: no hotkey presses a mouse button.");
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
    // watchdog survive, while key state starts over in a new engine. Callers hold _sync.
    private void ReinstallHookLocked()
    {
        var transitions = _transitions;
        var reconcileSignal = _reconcileSignal;
        if (transitions is null || reconcileSignal is null)
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

        var installation = new HookInstallation(this, engine, reconcileSignal);
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
        private readonly HotkeyReconcileSignal _reconcileSignal;

        // Rooted here for as long as the hook can call it: a collected delegate behind a live
        // hook crashes the process on the next key event.
        private readonly NativeMethods.LowLevelKeyboardProc _proc;

        // Rooted for the same reason, for the mouse hook this thread installs while a binding uses a mouse button.
        private readonly NativeMethods.LowLevelMouseProc _mouseProc;

        // Rooted for the same reason, for the desktop-switch notifications set up beside the hook.
        private readonly NativeMethods.WinEventProc _desktopSwitchProc;

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

        // The mouse hook's registration and what became of the last attempts, written only by this installation's thread
        // between messages and read by the watchdog and tests.
        private nint _mouseHookId;
        private int _mouseHookError;
        private long _mouseHookRegistrations;
        private long _mouseHookLosses;

        public HookInstallation(HotkeyService service, HotkeyEngine engine, HotkeyReconcileSignal reconcileSignal)
        {
            _service = service;
            _engine = engine;
            _reconcileSignal = reconcileSignal;
            _desktopReceivesInput = service._desktopReceivesInput;
            _proc = HookCallback;
            _mouseProc = MouseHookCallback;
            _desktopSwitchProc = DesktopSwitchCallback;
            _thread = new Thread(Run)
            {
                Name = "Scribe.HotkeyHook",
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public Exception? InstallError => _installError;

        /// <summary>The engine this installation's thread drives.</summary>
        public HotkeyEngine Engine => _engine;

        /// <summary>
        /// After <see cref="Install"/> succeeded: null when desktop switches are being reported, otherwise the Win32 error
        /// SetWinEventHook left (0 when it left none).
        /// </summary>
        public int? DesktopSwitchHookError => _desktopSwitchHooked ? null : _desktopSwitchError;

        /// <summary>Any thread: whether the mouse hook is registered right now.</summary>
        public bool MouseHookInstalled => Volatile.Read(ref _mouseHookId) != 0;

        /// <summary>
        /// Any thread: whether this installation needs its mouse hook: a binding presses a mouse button, or a swallowed
        /// press still owes its release, which a drain-only hook must still swallow once the last mouse binding is gone.
        /// </summary>
        public bool MouseHookWanted => !_engine.IsRetired && (_engine.UsesMouseButtons || _engine.OwesButtonRelease);

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
            if (threadId != 0)
            {
                NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_HOTKEY_REFRESH, nint.Zero, nint.Zero);
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
            try
            {
                // Before the hook exists, so no hook callback can run inside the peek (a peek also
                // delivers messages sent to this thread, which is how the hook is called).
                NativeMethods.EnsureMessageQueue();

                nint module = NativeMethods.GetModuleHandle(null);
                _module = module;
                hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
                if (hookId == 0)
                {
                    _installError = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    Volatile.Write(ref _hookId, hookId);

                    // The queue already exists, so publishing the id makes every later wake and
                    // quit deliverable; commands queued before this point apply here.
                    _engine.AttachOwner(NativeMethods.GetCurrentThreadId());

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
                        continue;
                    }

                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }

            _engine.DetachOwner();
            RemoveMouseHook();
            if (desktopSwitchHook != 0)
            {
                NativeMethods.UnhookWinEvent(desktopSwitchHook);
            }

            NativeMethods.UnhookWindowsHookEx(hookId);
        }

        // Hook thread only, between messages, never inside a hook callback. Installs the mouse hook while a binding presses
        // a mouse button, or, drain-only, while a swallowed press still owes its release after the last such binding went
        // (the engine swallows that release and nothing else: no binding can use the button), and removes it once neither
        // holds, or once this engine is retired (a pass-through hook would still make every pointer move wait for this
        // thread). With refresh, a hook that is wanted and installed is registered
        // afresh: the new registration first and the old one released after it, both before this thread takes another
        // message, so no button event can fall between them and the engine keeps its state through the change. A failed
        // registration keeps the one in place. An old registration that can no longer be released was already gone,
        // which is how a hook Windows removed after a missed deadline shows; it is counted for the watchdog to report.
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
            // its release may be the input nobody saw, here between messages and before any button event reaches the
            // new registration.
            Volatile.Write(ref _mouseHookId, replacement);
            Volatile.Write(ref _mouseHookError, 0);
            if (current != 0 && !NativeMethods.UnhookWindowsHookEx(current))
            {
                Interlocked.Increment(ref _mouseHookLosses);
                _engine.OnMouseHookLost();
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

        // Runs on this installation's thread, inside GetMessage, for every keyboard event on the
        // desktop. It never waits for another thread and never logs: the only shared state it
        // touches is interlocked counters, lock-free queues and kernel events. Its one native query
        // besides CallNextHookEx is GetAsyncKeyState, made only as ChordStateMachine describes.
        private nint HookCallback(int nCode, nint wParam, nint lParam)
        {
            // The watchdog's liveness signal. Incremented before any filtering so the synthetic
            // probe (which is marker-tagged and skipped below) still proves the hook is installed.
            Interlocked.Increment(ref _service._hookCallbackCount);

            if (nCode < 0)
            {
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            int message = (int)wParam;
            bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
            if (!isDown && !isUp)
            {
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Two direct field reads instead of PtrToStructure: the callback races a hard OS
            // deadline (LowLevelHooksTimeout) and a miss gets the hook silently removed, so every
            // event must stay as cheap as possible.
            var extraInfo = (nuint)Marshal.ReadIntPtr(lParam, ExtraInfoOffset);
            if (extraInfo == SyntheticInputMarker.Value)
            {
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            var vkCode = (uint)Marshal.ReadInt32(lParam, VkCodeOffset);
            var decision = _engine.OnKeyEvent(vkCode, isDown);
            if (decision.RequestReconcile)
            {
                _reconcileSignal.Signal();
            }

            return decision.Suppress ? 1 : NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Runs on this installation's thread, inside GetMessage, for every mouse event on the desktop while a binding
        // presses a mouse button, and the pointer waits for it. So the first thing it does, before reading any field or
        // touching the engine, is the one comparison that passes on every message but the middle and side buttons going
        // down or up: moves, the wheels and the left and right buttons cost exactly that. The rest is MouseHookFilter's,
        // which like the keyboard callback never waits, locks or logs. CallNextHookEx ignores its hook handle, so none is
        // read here.
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
