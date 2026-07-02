using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Global push-to-talk hotkey via a <c>WH_KEYBOARD_LL</c> hook. The hook runs on its own
/// thread with a native message pump (required for low-level hooks). The hook callback does
/// almost no work — it dedupes key-repeat, optionally suppresses the key, and enqueues a
/// transition — so it always returns well within the 300 ms <c>LowLevelHooksTimeout</c>.
/// A separate consumer thread turns transitions into ordered Activated/Deactivated events.
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private enum Transition
    {
        Down,
        Up,
    }

    private readonly ILogger<HotkeyService> _logger;
    private readonly object _sync = new();

    // Held as a field so the GC never collects the delegate while the hook is installed.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;

    private volatile HotkeyBinding _binding = HotkeyBinding.Default;
    private BlockingCollection<Transition>? _queue;
    private Thread? _hookThread;
    private Thread? _consumerThread;
    private uint _hookThreadId;
    private nint _hookId;
    private bool _keyHeld;
    private bool _toggleActive;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _proc = HookCallback;
    }

    public HotkeyService(ILogger<HotkeyService> logger, HotkeyBinding binding)
        : this(logger) => _binding = binding;

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

            _queue = new BlockingCollection<Transition>(new ConcurrentQueue<Transition>());
            _keyHeld = false;
            _toggleActive = false;

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
        }

        _logger.LogInformation("Hotkey hook removed.");
    }

    public void CancelToggle()
    {
        // A bare bool write is atomic; the hook thread reads it on the next key event, matching
        // how UpdateBinding already resets the same flag.
        _toggleActive = false;
    }

    public void UpdateBinding(HotkeyBinding binding)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _keyHeld = false;
        _toggleActive = false;
        _logger.LogInformation("Hotkey binding updated to {Binding} ({Mode}).", DescribeBinding(binding), binding.Mode);
    }

    private void RunHookThread(ManualResetEventSlim installed, ref Exception? installError)
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            nint module = NativeMethods.GetModuleHandle(null);
            _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);

            if (_hookId == 0)
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
            installed.Set();
        }

        if (_hookId == 0)
        {
            return;
        }

        // Pump messages so the low-level hook keeps being serviced. The loop exits when
        // Stop() posts WM_QUIT to this thread (GetMessage returns 0).
        while (NativeMethods.GetMessage(out NativeMethods.MSG msg, nint.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(_hookId);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        int message = (int)wParam;
        bool isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (isDown || isUp)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            HotkeyBinding binding = _binding;

            if (data.vkCode == binding.VirtualKey)
            {
                bool accepted = false;

                if (isDown && ModifiersHeld(binding.Modifiers))
                {
                    if (!_keyHeld)
                    {
                        _keyHeld = true;
                        _queue?.TryAdd(Transition.Down);
                    }

                    accepted = true;
                }
                else if (isUp && _keyHeld)
                {
                    _keyHeld = false;
                    _queue?.TryAdd(Transition.Up);
                    accepted = true;
                }

                if (accepted && binding.Suppress)
                {
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ConsumeTransitions()
    {
        BlockingCollection<Transition>? queue = _queue;
        if (queue is null)
        {
            return;
        }

        try
        {
            foreach (Transition transition in queue.GetConsumingEnumerable())
            {
                DispatchTransition(transition);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Queue completed/disposed during shutdown.
        }
    }

    private void DispatchTransition(Transition transition)
    {
        HotkeyBinding binding = _binding;

        try
        {
            if (binding.Mode == HotkeyMode.Hold)
            {
                if (transition == Transition.Down)
                {
                    Activated?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Deactivated?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            // Toggle mode: react on the down transition only.
            if (transition != Transition.Down)
            {
                return;
            }

            _toggleActive = !_toggleActive;
            if (_toggleActive)
            {
                Activated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Deactivated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A hotkey event handler threw.");
        }
    }

    private static bool ModifiersHeld(KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.None)
        {
            return true;
        }

        if (modifiers.HasFlag(KeyModifiers.Control) && !IsDown(NativeMethods.VK_CONTROL))
        {
            return false;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt) && !IsDown(NativeMethods.VK_MENU))
        {
            return false;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift) && !IsDown(NativeMethods.VK_SHIFT))
        {
            return false;
        }

        if (modifiers.HasFlag(KeyModifiers.Win)
            && !IsDown(NativeMethods.VK_LWIN) && !IsDown(NativeMethods.VK_RWIN))
        {
            return false;
        }

        return true;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string DescribeBinding(HotkeyBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.DisplayName))
        {
            return binding.DisplayName!;
        }

        string mods = binding.Modifiers == KeyModifiers.None ? string.Empty : binding.Modifiers + "+";
        return $"{mods}0x{binding.VirtualKey:X2}";
    }

    public void Dispose() => Stop();
}
