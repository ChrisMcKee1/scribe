using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Global push-to-talk hotkey via a <c>WH_KEYBOARD_LL</c> hook. The hook runs on its own
/// thread with a native message pump (required for low-level hooks). The hook callback performs
/// only key-set tracking, optional suppression, and transition enqueueing so it returns promptly.
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

    private readonly ILogger<HotkeyService> _logger;
    private readonly object _sync = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly ChordStateMachine _state;
    private readonly SuppressedKeyReconciler _reconciler;

    /// <summary>
    /// A transition in flight between the hook callback and the consumer thread. The generation
    /// lets the dispatcher drop an Activated that was computed just before a state-clearing
    /// operation (capture mode, binding update, hook reinstall) enqueued its own Deactivated;
    /// without it a recording could start while capture mode is armed. Deactivations always
    /// dispatch (a redundant stop is harmless, a missed stop is not). AllowReconcile is false for
    /// transitions born from a state clear: right after a clear the reconciler cannot distinguish
    /// a genuinely held key from a leaked one and could release a key the user is holding.
    /// </summary>
    internal readonly record struct QueuedTransition(
        HotkeyTransition Transition, long Generation, bool AllowReconcile);

    private volatile HotkeyBinding _binding = HotkeyBinding.Default;
    private BlockingCollection<QueuedTransition>? _queue;
    private Thread? _hookThread;
    private Thread? _consumerThread;
    private Timer? _watchdog;
    private uint _hookThreadId;
    private nint _hookId;
    private long _lastCallbackTick;
    private long _lastProbeTick;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _proc = HookCallback;
        _state = new ChordStateMachine(_binding);
        _reconciler = new SuppressedKeyReconciler(
            NativeMethods.IsKeyLogicallyDown,
            _state.IsPressed,
            key => NativeMethods.SendMarkedKeyEvent((ushort)key, keyUp: true));
    }

    public HotkeyService(ILogger<HotkeyService> logger, HotkeyBinding binding)
        : this(logger)
    {
        _binding = binding;
        _state.UpdateBinding(binding);
    }

    public bool IsRunning { get; private set; }

    public HotkeyBinding Binding => _binding;

    public event EventHandler? Activated;

    public event EventHandler? Deactivated;

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return;
            }

            _queue = new BlockingCollection<QueuedTransition>(new ConcurrentQueue<QueuedTransition>());
            _ = _state.Reset(); // fresh start: no consumer exists yet, so the transition is moot

            using var installed = new ManualResetEventSlim(false);
            Exception? installError = null;

            _hookThread = new Thread(() => RunHookThread(installed, ref installError))
            {
                Name = "Scribe.HotkeyHook",
                IsBackground = true,
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            installed.Wait(TimeSpan.FromSeconds(5));

            if (_hookId == 0)
            {
                _queue.Dispose();
                _queue = null;
                throw new InvalidOperationException(
                    "Failed to install the global keyboard hook.", installError);
            }

            _consumerThread = new Thread(ConsumeTransitions)
            {
                Name = "Scribe.HotkeyDispatch",
                IsBackground = true,
            };
            _consumerThread.Start();

            // Windows silently removes a low-level hook whose callback misses the OS deadline
            // (documented: no notification of any kind). The watchdog probes liveness so a long
            // GC pause during ASR decode cannot permanently kill push-to-talk.
            _lastCallbackTick = Environment.TickCount64;
            _lastProbeTick = 0;
            _watchdog = new Timer(_ => WatchdogTick(), null, WatchdogPeriod, WatchdogPeriod);

            IsRunning = true;
            _logger.LogInformation(
                "Hotkey hook installed for {Binding} ({Mode}).", DescribeBinding(_binding), _binding.Mode);
        }
    }

    public void Stop()
    {
        Thread? hookThread;
        Thread? consumerThread;

        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            _watchdog?.Dispose();
            _watchdog = null;
            hookThread = _hookThread;
            consumerThread = _consumerThread;

            if (_hookThreadId != 0)
            {
                NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
            }

            _queue?.CompleteAdding();
        }

        hookThread?.Join(TimeSpan.FromSeconds(2));
        consumerThread?.Join(TimeSpan.FromSeconds(2));

        lock (_sync)
        {
            _queue?.Dispose();
            _queue = null;
            _hookThread = null;
            _consumerThread = null;
            _hookThreadId = 0;
            _hookId = 0;
            _ = _state.Reset();
        }

        _logger.LogInformation("Hotkey hook removed.");
    }

    public void CancelToggle() => _state.CancelToggle();

    public void SetCaptureMode(bool enabled)
    {
        var (transition, generation) = _state.SetCaptureMode(enabled);
        if (transition != HotkeyTransition.None)
        {
            TryEnqueue(_queue, new QueuedTransition(transition, generation, AllowReconcile: false));
        }

        _logger.LogInformation("Hotkey binding capture mode {State}.", enabled ? "enabled" : "disabled");
    }

    public void UpdateBinding(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_binding == binding)
        {
            return;
        }

        _binding = binding;
        var (transition, generation) = _state.UpdateBinding(binding);
        if (transition != HotkeyTransition.None)
        {
            TryEnqueue(_queue, new QueuedTransition(transition, generation, AllowReconcile: false));
        }

        _logger.LogInformation("Hotkey binding updated to {Binding} ({Mode}).", DescribeBinding(binding), binding.Mode);
    }

    private void RunHookThread(ManualResetEventSlim installed, ref Exception? installError)
    {
        // The handle stays thread-local: a hook thread that outlives its 2-second join (and got
        // replaced by the watchdog) must unhook ITS hook on exit, never the replacement's, and
        // must not clear the shared field if a replacement already owns it.
        nint hookId = 0;
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            nint module = NativeMethods.GetModuleHandle(null);
            hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
            _hookId = hookId;

            if (hookId == 0)
            {
                installError = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            installError = ex;
        }
        finally
        {
            try
            {
                installed.Set();
            }
            catch (ObjectDisposedException)
            {
                // The starter timed out waiting and disposed the event; it already treats the
                // install as failed, so there is nobody left to signal.
            }
        }

        if (hookId == 0)
        {
            return;
        }

        while (NativeMethods.GetMessage(out NativeMethods.MSG msg, nint.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(hookId);
        Interlocked.CompareExchange(ref _hookId, 0, hookId);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        // The watchdog's liveness signal. Written before any filtering so the synthetic probe
        // (which is marker-tagged and skipped below) still proves the hook is installed.
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount64);

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

        // Two direct field reads instead of PtrToStructure: the callback races a hard OS deadline
        // (LowLevelHooksTimeout) and a miss gets the hook silently removed, so every event must
        // stay as cheap as possible.
        var extraInfo = (nuint)Marshal.ReadIntPtr(lParam, ExtraInfoOffset);
        if (extraInfo == SyntheticInputMarker.Value)
        {
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var vkCode = (uint)Marshal.ReadInt32(lParam, VkCodeOffset);
        var update = _state.Process(vkCode, isDown);
        if (update.Transition != HotkeyTransition.None)
        {
            TryEnqueue(_queue, new QueuedTransition(update.Transition, update.Generation, AllowReconcile: true));
        }

        // Toggle mode deactivates on key DOWN, so the deactivation-time reconcile still sees the
        // key physically held and correctly skips it. The suppressed key UP is therefore the one
        // moment that covers every mode: check for a leaked logical "down" right after it.
        if (isUp && update.ShouldSuppress)
        {
            ScheduleReconcile();
        }

        return update.ShouldSuppress ? 1 : NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // A final keyboard message can already be in the hook thread's native queue when Stop marks the
    // managed transition queue complete. BlockingCollection.TryAdd still throws in that race, and an
    // exception escaping a low-level Windows hook callback terminates the process. Shutdown simply
    // discards that stale transition.
    internal static bool TryEnqueue(
        BlockingCollection<QueuedTransition>? queue, QueuedTransition transition)
    {
        if (queue is null)
        {
            return false;
        }

        try
        {
            return queue.TryAdd(transition);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ConsumeTransitions()
    {
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        try
        {
            foreach (var transition in queue.GetConsumingEnumerable())
            {
                DispatchTransition(transition);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Queue completed or disposed during shutdown.
        }
    }

    private void DispatchTransition(QueuedTransition item)
    {
        try
        {
            if (item.Transition == HotkeyTransition.Activated)
            {
                // An Activated computed against state that was since cleared (capture mode,
                // binding update, reinstall) must not start a recording; the clear's own
                // Deactivated may already sit behind it in this queue.
                if (item.Generation != _state.Generation)
                {
                    _logger.LogInformation("Discarded a stale hotkey activation from a superseded state epoch.");
                    return;
                }

                Activated?.Invoke(this, EventArgs.Empty);
            }
            else if (item.Transition == HotkeyTransition.Deactivated)
            {
                Deactivated?.Invoke(this, EventArgs.Empty);

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
    // a beat to settle after the final suppressed key-up.
    private void ScheduleReconcile() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            var result = _reconciler.ReleaseLeakedKeys(_binding);
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
    // app but still traverses the hook. If the probe sent on the PREVIOUS tick never produced a
    // callback, Windows silently removed the hook (documented behavior after a deadline miss) and
    // push-to-talk is dead until it is reinstalled. Mouse-only activity cannot false-positive this
    // check because the probe itself is keyboard input.
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
                    _lastProbeTick = 0;
                    return;
                }

                var lastProbe = _lastProbeTick;
                var lastCallback = Volatile.Read(ref _lastCallbackTick);
                if (lastProbe != 0 && lastCallback < lastProbe)
                {
                    _logger.LogWarning(
                        "The keyboard hook stopped receiving events (Windows removes low-level " +
                        "hooks that miss the callback deadline, without notification). Reinstalling.");
                    ReinstallHookLocked();
                }

                // Arm the next check only when the probe actually left the building; a rejected
                // SendInput (UIPI, desktop switch mid-tick) must not read as a dead hook.
                _lastProbeTick = NativeMethods.SendMarkedKeyEvent(NativeMethods.VK_PROBE, keyUp: true)
                    ? Environment.TickCount64
                    : 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey hook watchdog tick failed; will retry next period.");
        }
    }

    // Tears down only the hook thread and spins a fresh one; the consumer thread, queue, watchdog
    // and chord state survive. Callers hold _sync.
    private void ReinstallHookLocked()
    {
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, nint.Zero, nint.Zero);
        }

        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _hookThreadId = 0;
        _hookId = 0;

        // The reinstall may interrupt an active hold/toggle: the held key's eventual release can
        // no longer match the cleared state, so the recording must be stopped explicitly or the
        // microphone stays live until the next press.
        var (transition, generation) = _state.Reset();
        if (transition != HotkeyTransition.None)
        {
            TryEnqueue(_queue, new QueuedTransition(transition, generation, AllowReconcile: false));
        }

        using var installed = new ManualResetEventSlim(false);
        Exception? installError = null;
        _hookThread = new Thread(() => RunHookThread(installed, ref installError))
        {
            Name = "Scribe.HotkeyHook",
            IsBackground = true,
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        installed.Wait(TimeSpan.FromSeconds(5));

        if (_hookId == 0)
        {
            _logger.LogError(installError, "Reinstalling the keyboard hook failed; retrying on the next watchdog tick.");
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
}
