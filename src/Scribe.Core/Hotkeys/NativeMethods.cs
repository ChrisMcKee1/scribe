using System.Runtime.InteropServices;

namespace Scribe.Core.Hotkeys;

/// <summary>P/Invoke surface for the low-level keyboard hook and its message pump.</summary>
internal static partial class NativeMethods
{
    internal const int WH_KEYBOARD_LL = 13;

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const uint WM_QUIT = 0x0012;

    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12; // Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    internal delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    // SetWindowsHookEx takes a managed delegate (the hook needs instance state, so a captured
    // delegate is required rather than an unmanaged function pointer). Delegate marshalling uses
    // classic DllImport; the remaining blittable calls use the source-generated LibraryImport.
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetWindowsHookExW")]
    internal static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    internal static int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax)
        => GetMessageW(out lpMsg, hWnd, wMsgFilterMin, wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(ref MSG lpMsg);

    internal static nint DispatchMessage(ref MSG lpMsg) => DispatchMessageW(ref lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    internal static bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam)
        => PostThreadMessageW(idThread, msg, wParam, lParam);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    /// <summary>High bit of <see cref="GetAsyncKeyState"/>: the system's logical "key is down".</summary>
    internal static bool IsKeyLogicallyDown(uint virtualKey) =>
        (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    private const uint DESKTOP_READOBJECTS = 0x0001;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint hDesktop);

    /// <summary>
    /// True when the current input desktop is the interactive one this process can reach; false on
    /// the lock screen / secure desktop, where synthetic input never arrives at this desktop's hooks.
    /// </summary>
    internal static bool CanAccessInputDesktop()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == 0)
        {
            return false;
        }

        CloseDesktop(desktop);
        return true;
    }

    // --- Synthetic input (leak release + hook liveness probe) --------------------------------

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // AutoHotkey's long-standing "mask key": an unassigned virtual key whose key-up is inert in
    // every mainstream app, safe to inject as a hook liveness probe.
    internal const ushort VK_PROBE = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    // Mirrors InjectionNativeMethods: sequential outer struct with an explicit union keeps the
    // union at the OS's natural alignment on both x86 and x64 without hardcoded offsets.
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Right-hand modifiers and the nav/arrow cluster carry the extended-key flag; a synthetic
    // key-up without it maps to the left-hand sibling and would fail to release the right key.
    private static bool IsExtendedKey(uint virtualKey) => virtualKey is
        0xA3 /* RCtrl */ or 0xA5 /* RAlt */ or 0x5B /* LWin */ or 0x5C /* RWin */ or
        0x21 /* PgUp */ or 0x22 /* PgDn */ or 0x23 /* End */ or 0x24 /* Home */ or
        0x25 or 0x26 or 0x27 or 0x28 /* arrows */ or
        0x2D /* Insert */ or 0x2E /* Delete */ or 0x90 /* NumLock */ or 0x6F /* NumDivide */;

    /// <summary>
    /// Injects a synthetic key event tagged with <see cref="SyntheticInputMarker"/> so the hook
    /// ignores it for chord state (the liveness probe still ticks the callback counter).
    /// </summary>
    internal static bool SendMarkedKeyEvent(ushort virtualKey, bool keyUp)
    {
        var flags = (keyUp ? KEYEVENTF_KEYUP : 0u) |
            (IsExtendedKey(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = flags,
                    dwExtraInfo = SyntheticInputMarker.Value,
                },
            },
        };

        return SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 1;
    }
}
