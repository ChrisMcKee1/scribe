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
}
