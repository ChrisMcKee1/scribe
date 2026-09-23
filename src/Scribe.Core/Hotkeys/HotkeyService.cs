using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Global push-to-talk hotkey via a <c>WH_KEYBOARD_LL</c> hook. The hook runs on its own
/// thread with a native message pump (required for low-level hooks). The hook callback performs
/// only key-set tracking, optional suppression, and transition enqueueing so it returns promptly.
///
/// The callback never waits for another thread. Windows calls it by sending a message to the hook
/// thread and silently removes the hook if it answers after LowLevelHooksTimeout (at most 1000 ms),
/// so the key state belongs to the hook thread alone (<see cref="HotkeyEngine"/>). Configuration
/// and commands from other threads are queued through <see cref="HotkeyCommandRouter"/> and applied
/// by the hook thread at its next key event, or sooner when a thread message wakes it; what other
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
    private readonly object _sync = new();
    private readonly HotkeyCommandRouter _router;
    private readonly SuppressedKeyReconciler _reconciler;

    /// <summary>
    /// A transition in flight between the hook thread and the consumer thread. The generation
    /// lets the dispatcher drop an Activated that was computed before a state-clearing request
    /// (capture mode, binding update, pause, hook reinstall); without it a recording could start
    /// while capture mode is armed. Deactivations always dispatch (a redundant stop is harmless, a
    /// missed stop is not). AllowReconcile is false for transitions born from a state clear: right
    /// after a clear the reconciler cannot distinguish a genuinely held key from a leaked one and
    /// could release a key the user is holding.
    /// </summary>
    internal readonly record struct QueuedTransition(
        HotkeyTransition Transition, HotkeyTrigger Trigger, long Generation, bool AllowReconcile);

    private HotkeyTransitionQueue? _transitions;
    private HotkeyReconcileSignal? _reconcileSignal;
    private HookInstallation? _installation;
    private Thread? _consumerThread;
    private Timer? _watchdog;
    private long _hookCallbackCount;
    private readonly HookLivenessProbe _livenessProbe = new();

    public HotkeyService(ILogger<HotkeyService> logger)
        : this(logger, HotkeyBinding.Default)
    {
    }

    public HotkeyService(ILogger<HotkeyService> logger, HotkeyBinding binding)
    {
        _logger = logger;
        _router = new HotkeyCommandRouter(binding);
        _reconciler = new SuppressedKeyReconciler(
            NativeMethods.IsKeyLogicallyDown,
            _router.IsPressed,
            key => NativeMethods.SendMarkedKeyEvent((ushort)key, keyUp: true));
    }

    public bool IsRunning { get; private set; }

    public HotkeyBinding Binding => _router.Binding;

    public HotkeyBinding? DictationOnlyBinding => _router.DictationOnlyBinding;

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

    public void CancelToggle() => Wake(_router.CancelToggle());

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
            "Hotkey bindings updated: standard={Binding} ({Mode}), dictation-only={DictationOnly}.",
            DescribeBinding(binding), binding.Mode,
            dictationOnlyBinding is null ? "disabled" : DescribeBinding(dictationOnlyBinding));
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

    private void DispatchTransition(QueuedTransition item)
    {
        try
        {
            if (item.Transition == HotkeyTransition.Activated)
            {
                // An Activated computed before a state-clearing request (capture mode, binding
                // update, pause, reinstall) must not start a recording; the request's own
                // Deactivated may already sit behind it in this queue. The epoch advances when
                // the request is made, so this holds even while the hook thread has yet to
                // apply it.
                if (!_router.IsCurrent(item.Generation))
                {
                    _logger.LogInformation("Discarded a stale hotkey activation from a superseded state epoch.");
                    return;
                }

                Activated?.Invoke(this, new HotkeyTriggerEventArgs(item.Trigger));
            }
            else if (item.Transition == HotkeyTransition.Deactivated)
            {
                Deactivated?.Invoke(this, new HotkeyTriggerEventArgs(item.Trigger));

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

    // Runs the leak check off the hook and consumer threads: GetAsyncKeyState is meaningless
    // inside the hook callback (async state updates after it returns), and the input queue needs
    // a beat to settle after the final suppressed key-up. The hook callback never calls this
    // itself; it signals HotkeyReconcileSignal, whose pool wait thread does.
    private void ScheduleReconcile() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
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

    // Probe-based liveness: a marker-tagged key-up for the unassigned VK 0xFF is inert for every
    // app but still traverses the hook. If nothing at all reached the callback since the PREVIOUS
    // probe was armed, Windows silently removed the hook (documented behavior after a deadline
    // miss) and push-to-talk is dead until it is reinstalled. Mouse-only activity cannot
    // false-positive this check because the probe itself is keyboard input.
    //
    // The probe is withheld while the system is idle: injected input resets the power manager's
    // idle timer, so an unconditional probe every period stopped the machine from ever sleeping.
    // See HookLivenessProbe.ShouldWithholdProbe.
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey hook watchdog tick failed; will retry next period.");
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
        }
    }

    private static string DescribeBinding(HotkeyBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.DisplayName))
        {
            return binding.DisplayName!;
        }

        var modifiers = binding.Modifiers == KeyModifiers.None ? string.Empty : binding.Modifiers + "+";
        var secondary = binding.SecondaryVirtualKey is { } second ? $"+0x{second:X2}" : string.Empty;
        return $"{modifiers}0x{binding.VirtualKey:X2}{secondary}";
    }

    public void Dispose() => Stop();

    /// <summary>
    /// One installation of the low-level hook: its thread, its native handle, its callback
    /// delegate and the engine only that thread may drive. Keeping all of it per installation is
    /// what lets a reinstall proceed while a previous hook thread is still winding down.
    /// </summary>
    private sealed class HookInstallation
    {
        private readonly HotkeyService _service;
        private readonly HotkeyEngine _engine;
        private readonly HotkeyReconcileSignal _reconcileSignal;

        // Rooted here for as long as the hook can call it: a collected delegate behind a live
        // hook crashes the process on the next key event.
        private readonly NativeMethods.LowLevelKeyboardProc _proc;
        private readonly ManualResetEventSlim _installed = new(false);
        private readonly Thread _thread;
        private Exception? _installError;
        private nint _hookId;
        private int _abandoned;

        public HookInstallation(HotkeyService service, HotkeyEngine engine, HotkeyReconcileSignal reconcileSignal)
        {
            _service = service;
            _engine = engine;
            _reconcileSignal = reconcileSignal;
            _proc = HookCallback;
            _thread = new Thread(Run)
            {
                Name = "Scribe.HotkeyHook",
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public Exception? InstallError => _installError;

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

        public bool Join(TimeSpan timeout) => _thread.Join(timeout);

        private void Run()
        {
            // The handle stays per installation: a hook thread that outlives its 2-second join
            // (and got replaced by the watchdog) must unhook ITS hook on exit, never the
            // replacement's.
            nint hookId = 0;
            try
            {
                // Before the hook exists, so no hook callback can run inside the peek (a peek also
                // delivers messages sent to this thread, which is how the hook is called).
                NativeMethods.EnsureMessageQueue();

                nint module = NativeMethods.GetModuleHandle(null);
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
                    // Thread messages have no window, so DispatchMessage would drop this one.
                    if (msg.hwnd == nint.Zero && msg.message == NativeMethods.WM_HOTKEY_COMMANDS)
                    {
                        _engine.OnWake();
                        continue;
                    }

                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }

            _engine.DetachOwner();
            NativeMethods.UnhookWindowsHookEx(hookId);
        }

        // Runs on this installation's thread, inside GetMessage, for every keyboard event on the
        // desktop. It never waits for another thread and never logs: the only shared state it
        // touches is interlocked counters, lock-free queues and kernel events.
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
    }
}

/// <summary>
/// Lets at most one trigger own the dictation, arbitrated without a lock. The same word records
/// retirement, so "which dictation did this engine leave running" and "this engine may no longer
/// start or stop one" are settled by one atomic operation rather than a flag and a read that a
/// concurrent owner thread could interleave.
/// </summary>
internal sealed class HotkeyTriggerArbiter
{
    // Zero means no trigger is active; Retired is terminal.
    private const int Retired = -1;

    private int _activeCode;

    public bool TryActivate(HotkeyTrigger trigger) =>
        Interlocked.CompareExchange(ref _activeCode, Code(trigger), 0) == 0;

    public bool TryDeactivate(HotkeyTrigger trigger)
    {
        var code = Code(trigger);
        return Interlocked.CompareExchange(ref _activeCode, 0, code) == code;
    }

    /// <summary>
    /// Clears the active trigger and returns it, or <paramref name="fallback"/> when none was
    /// active. Returns null once retired: the retirement already reported the active trigger, so the
    /// caller must not stop anything itself.
    /// </summary>
    public HotkeyTrigger? TryTake(HotkeyTrigger fallback)
    {
        var code = Volatile.Read(ref _activeCode);
        while (code != Retired)
        {
            var observed = Interlocked.CompareExchange(ref _activeCode, 0, code);
            if (observed == code)
            {
                return code == 0 ? fallback : Trigger(code);
            }

            code = observed;
        }

        return null;
    }

    /// <summary>Clears the active trigger, unless retired.</summary>
    public void Reset() => _ = TryTake(HotkeyTrigger.Standard);

    /// <summary>
    /// Refuses everything from now on, and returns the trigger that was still active (a dictation
    /// that was started and never stopped), or null.
    /// </summary>
    public HotkeyTrigger? Retire()
    {
        var code = Interlocked.Exchange(ref _activeCode, Retired);
        return code is 0 or Retired ? null : Trigger(code);
    }

    // Reserve zero for no active trigger so compare-exchange can arbitrate without a lock.
    private static int Code(HotkeyTrigger trigger) => (int)trigger + 1;

    private static HotkeyTrigger Trigger(int code) => (HotkeyTrigger)(code - 1);
}
