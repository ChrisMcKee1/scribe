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
    private readonly ILogger<HotkeyService> _logger;
    private readonly object _sync = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly ChordStateMachine _state;

    private volatile HotkeyBinding _binding = HotkeyBinding.Default;
    private BlockingCollection<HotkeyTransition>? _queue;
    private Thread? _hookThread;
    private Thread? _consumerThread;
    private uint _hookThreadId;
    private nint _hookId;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _proc = HookCallback;
        _state = new ChordStateMachine(_binding);
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

            _queue = new BlockingCollection<HotkeyTransition>(new ConcurrentQueue<HotkeyTransition>());
            _state.Reset();

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
            _state.Reset();
        }

        _logger.LogInformation("Hotkey hook removed.");
    }

    public void CancelToggle() => _state.CancelToggle();

    public void UpdateBinding(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_binding == binding)
        {
            return;
        }

        _binding = binding;
        var transition = _state.UpdateBinding(binding);
        if (transition != HotkeyTransition.None)
        {
            _queue?.TryAdd(transition);
        }

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
        if (!isDown && !isUp)
        {
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == SyntheticInputMarker.Value)
        {
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var update = _state.Process(data.vkCode, isDown);
        if (update.Transition != HotkeyTransition.None)
        {
            _queue?.TryAdd(update.Transition);
        }

        return update.ShouldSuppress ? 1 : NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
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

    private void DispatchTransition(HotkeyTransition transition)
    {
        try
        {
            if (transition == HotkeyTransition.Activated)
            {
                Activated?.Invoke(this, EventArgs.Empty);
            }
            else if (transition == HotkeyTransition.Deactivated)
            {
                Deactivated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A hotkey event handler threw.");
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
