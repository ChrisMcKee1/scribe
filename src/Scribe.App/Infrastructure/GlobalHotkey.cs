using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

namespace Scribe.App.Infrastructure;

/// <summary>
/// A tap-to-fire global hotkey built on Win32 <c>RegisterHotKey</c> and a message-only window.
/// </summary>
/// <remarks>
/// Deliberately NOT built on <see cref="Scribe.Core.Hotkeys.HotkeyService"/>, even though that class
/// already owns a global keyboard hook. Two reasons, and both are about protecting dictation.
/// <para>
/// The semantics differ. Push-to-talk needs press and release as separate events, which is why it
/// uses <c>WH_KEYBOARD_LL</c>. Opening a palette is a single tap, which is exactly what
/// <c>RegisterHotKey</c> is for, and the OS handles the matching rather than a managed callback.
/// </para>
/// <para>
/// The risk differs more. A low-level hook callback runs inside the OS input path under
/// <c>LowLevelHooksTimeout</c>, and adding a third chord state machine to that callback puts new
/// work on the path every keystroke in the system takes. <c>RegisterHotKey</c> shares no state with
/// the hook, cannot slow it down, and cannot leave a key stranded. If this class breaks, dictation
/// keeps working.
/// </para>
/// </remarks>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x5342; // "SB"

    [Flags]
    private enum Modifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private readonly ILogger _logger;
    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey(ILogger logger) => _logger = logger;

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <summary>True when a binding is currently registered with Windows.</summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// Registers <paramref name="binding"/>, replacing any previous one. A null binding just
    /// unregisters. Returns false when Windows refuses the combination, which happens when another
    /// application already owns it; the caller should tell the user rather than failing silently.
    /// </summary>
    public bool Update(HotkeyBinding? binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unregister();

        if (binding is null || binding.VirtualKey == 0)
        {
            return true;
        }

        EnsureSource();
        if (_source?.Handle is not { } handle || handle == 0)
        {
            return false;
        }

        var modifiers = (uint)Modifiers.NoRepeat;
        if (binding.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= (uint)Modifiers.Control;
        if (binding.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= (uint)Modifiers.Alt;
        if (binding.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= (uint)Modifiers.Shift;
        if (binding.Modifiers.HasFlag(KeyModifiers.Win)) modifiers |= (uint)Modifiers.Win;

        _registered = RegisterHotKey(handle, HotkeyId, modifiers, binding.VirtualKey);
        if (!_registered)
        {
            // Almost always ERROR_HOTKEY_ALREADY_REGISTERED (1409): another app owns this chord.
            _logger.LogWarning(
                "Could not register the text action hotkey {Binding} (win32 error {Error}).",
                binding.DisplayName,
                Marshal.GetLastWin32Error());
        }

        return _registered;
    }

    private void EnsureSource()
    {
        if (_source is not null)
        {
            return;
        }

        // A message-only window (HWND_MESSAGE parent) so it never appears anywhere: no taskbar
        // button, no Alt-Tab entry, no z-order participation.
        var parameters = new HwndSourceParameters("Scribe.TextActionHotkey")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || wParam.ToInt32() != HotkeyId)
        {
            return nint.Zero;
        }

        handled = true;

        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            // A throwing handler must never propagate into the window procedure.
            _logger.LogWarning(ex, "Text action hotkey handler threw.");
        }

        return nint.Zero;
    }

    private void Unregister()
    {
        if (!_registered || _source?.Handle is not { } handle || handle == 0)
        {
            _registered = false;
            return;
        }

        _ = UnregisterHotKey(handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
