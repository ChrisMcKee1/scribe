using System;
using System.Runtime.InteropServices;

namespace Scribe.Overlay.Interop;

/// <summary>
/// Win32 interop for window styling the WinUI 3 AppWindow surface can't express directly:
/// click-through, no-activate, tool-window (off the taskbar/Alt-Tab) extended styles, and explicit
/// top-most z-order. The pill is win-x64 only, so the *Ptr variants are used unconditionally.
/// </summary>
internal static class NativeMethods
{
    // GetWindowLong index for the extended window style.
    internal const int GWL_EXSTYLE = -20;

    // Extended window styles.
    internal const long WS_EX_TRANSPARENT = 0x00000020; // mouse messages pass through (click-through)
    internal const long WS_EX_TOOLWINDOW = 0x00000080; // no taskbar button, hidden from Alt-Tab
    internal const long WS_EX_LAYERED = 0x00080000; // layered (DWM-composited here, not legacy ULW)
    internal const long WS_EX_NOACTIVATE = 0x08000000; // never steals focus from the typing target

    // SetWindowPos special HWNDs and flags.
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    internal static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    internal static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    // DwmSetWindowAttribute attributes (Windows 11 build 22000+). Windows 11 paints a 1px
    // non-client border and rounds the corners of every top-level window — including a borderless
    // OverlappedPresenter — which shows up as a faint rectangle at the edge of the otherwise
    // transparent pill window. Both attributes fail harmlessly (E_INVALIDARG) on Windows 10.
    internal const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const uint DWMWA_BORDER_COLOR = 34;

    // DWM_WINDOW_CORNER_PREFERENCE: never round — the pill draws its own corners in XAML.
    internal const int DWMWCP_DONOTROUND = 1;

    // Special COLORREF for DWMWA_BORDER_COLOR: suppress the border entirely.
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    internal static extern int DwmSetWindowAttributeUint(
        IntPtr hwnd, uint dwAttribute, ref uint pvAttribute, int cbAttribute);
}
