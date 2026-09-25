using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Scribe.Core.Hotkeys;

/// <summary>P/Invoke surface for the low-level keyboard and mouse hooks and their message pump.</summary>
internal static partial class NativeMethods
{
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const uint WM_QUIT = 0x0012;

    // Private thread messages to the hook thread. WM_APP and above is the range reserved for application-defined
    // messages. The first wakes it to apply queued commands; the second, from the watchdog, also has it register its
    // mouse hook afresh (see HotkeyService.HookInstallation).
    internal const uint WM_APP = 0x8000;
    internal const uint WM_HOTKEY_COMMANDS = WM_APP + 1;
    internal const uint WM_HOTKEY_REFRESH = WM_APP + 2;

    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12; // Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    internal delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    internal delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

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
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetWindowsHookExW")]
    internal static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

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

    internal const uint WM_USER = 0x0400;
    private const uint PM_NOREMOVE = 0x0000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    /// <summary>
    /// Gives the calling thread a message queue if it has none yet, which PostThreadMessage needs
    /// before any other thread can post to it. This is the method the PostThreadMessage
    /// documentation prescribes, a PM_NOREMOVE peek that retrieves and removes nothing; the
    /// documentation is not consistent about which other calls create the queue, so the hook thread
    /// does not rely on SetWindowsHookEx doing it.
    /// </summary>
    internal static void EnsureMessageQueue() => _ = PeekMessageW(out _, 0, WM_USER, WM_USER, PM_NOREMOVE);

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

    /// <summary>
    /// Windows' own view of a mouse button, or null when a zero cannot be trusted (see <see cref="ReadMouseButtonState"/>).
    /// Called in the mouse hook callback, for the release of a press the hook swallowed before a time no hook saw the
    /// mouse, so everything under it waits for nothing, takes no lock of Scribe's and allocates nothing: user32 queries,
    /// and for a zero the foreground process's integrity level, read into a buffer on the stack.
    /// </summary>
    internal static bool? MouseButtonStateInWindows(uint button) =>
        ReadMouseButtonState(
            button, ThreadDesktopReceivesInput, GetForegroundWindow, IsKeyLogicallyDown, WindowAllowsKeyStateReads);

    /// <summary>
    /// The decision behind <see cref="MouseButtonStateInWindows"/>, over the readings it takes, so a test can script
    /// them. GetAsyncKeyState "works with mouse buttons", but "The return value is zero if the call fails", the same as
    /// up, and it fails when "The current desktop is not the active desktop" and when "UI Privilege Isolation (UIPI)
    /// prevents the calling thread from accessing the foreground thread" (a window of a higher integrity level in front,
    /// such as an elevated app's); its third failure, no DESKTOP_HOOKCONTROL or DESKTOP_JOURNALRECORD access to the
    /// foreground thread's desktop, cannot happen while the desktop checks hold: that desktop is then this thread's own,
    /// and the hooks this thread installed there needed DESKTOP_HOOKCONTROL ("Required to establish any of the window
    /// hooks"). So a reading of down is Windows' own (a failure returns zero), while a reading of up counts only when this
    /// desktop receives input before and after it, the same foreground window is in front before and after it, and
    /// <paramref name="windowAllowsReads"/> says the call could reach that window; anything else is null. That bracket is
    /// the most one call allows, not proof: Windows gives no reason with a zero, so a foreground that turns to a window
    /// the call cannot reach and back between the two samples, or a desktop that goes and comes back, reads as a
    /// trustworthy up. The owed-release decision therefore relies on it only for a release a gap has made uncertain.
    /// </summary>
    internal static bool? ReadMouseButtonState(
        uint button,
        Func<bool?> desktopReceivesInput,
        Func<nint> foregroundWindow,
        Func<uint, bool> isDown,
        Func<nint, bool?> windowAllowsReads)
    {
        if (desktopReceivesInput() != true)
        {
            return null;
        }

        var before = foregroundWindow();
        if (isDown(button))
        {
            return true;
        }

        var after = foregroundWindow();
        return before != 0 && before == after && desktopReceivesInput() == true && windowAllowsReads(before) == true
            ? false
            : null;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    // Room for a TOKEN_MANDATORY_LABEL (its SID_AND_ATTRIBUTES: the SID pointer, the attributes and padding, 16 bytes on
    // 64-bit Windows) and the integrity SID GetTokenInformation writes after it (12 bytes for its one subauthority), kept
    // on the stack so the read allocates nothing. Sid points into this buffer once the call has filled it.
    [StructLayout(LayoutKind.Sequential)]
    private struct MandatoryLabelBuffer
    {
        public nint Sid;
        public int Attributes;
        private int _padding;
        private long _sid0, _sid1, _sid2, _sid3, _sid4, _sid5;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        ref MandatoryLabelBuffer tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [LibraryImport("advapi32.dll")]
    private static partial nint GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial nint GetSidSubAuthority(nint sid, uint subAuthority);

    // This process's integrity level, read once: HotkeyService primes it before any hook exists, so the hook callback only
    // reads the field. Zero until then (the one level that is zero, Untrusted, is not one Scribe runs at).
    private static uint s_ownIntegrityLevel;

    /// <summary>Reads this process's integrity level now, so the hook callback never has to (see HotkeyService.Start).</summary>
    internal static void PrimeOwnIntegrityLevel() => _ = OwnIntegrityLevel();

    private static uint? OwnIntegrityLevel()
    {
        var level = Volatile.Read(ref s_ownIntegrityLevel);
        if (level == 0 && ProcessIntegrityLevel((uint)Environment.ProcessId) is { } read)
        {
            level = read;
            Volatile.Write(ref s_ownIntegrityLevel, level);
        }

        return level == 0 ? null : level;
    }

    /// <summary>
    /// Whether GetAsyncKeyState can reach the thread that owns <paramref name="window"/>: true for this process's own
    /// windows and for a process whose integrity level is no higher than this one's, false for a higher one, which UIPI
    /// keeps the call from, and null when either level cannot be read.
    /// </summary>
    internal static bool? WindowAllowsKeyStateReads(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }

        return IntegrityAllowsReads(OwnIntegrityLevel(), ProcessIntegrityLevel(processId));
    }

    /// <summary>
    /// UIPI's rule, as the reading needs it: a thread can reach the thread of a process whose integrity level is no
    /// higher than its own (UIPI "prevents messages from being received from a lower-integrity-level sender"), and cannot
    /// reach a higher one. Null when either level is unknown.
    /// </summary>
    internal static bool? IntegrityAllowsReads(uint? own, uint? theirs) =>
        own is { } mine && theirs is { } other ? other <= mine : null;

    /// <summary>
    /// A process's mandatory integrity level (the RID of its token's integrity SID: 0x2000 medium, 0x3000 high), or null
    /// when it cannot be read. OpenProcessToken needs only PROCESS_QUERY_LIMITED_INFORMATION. Safe in the hook callback:
    /// three kernel calls that wait for no other thread, two handles closed before it returns, and the label read into a
    /// buffer on the stack.
    /// </summary>
    internal static uint? ProcessIntegrityLevel(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                return null;
            }

            try
            {
                var label = default(MandatoryLabelBuffer);
                if (!GetTokenInformation(
                        token, TokenIntegrityLevel, ref label, Unsafe.SizeOf<MandatoryLabelBuffer>(), out _)
                    || label.Sid == 0)
                {
                    return null;
                }

                // The level is the SID's last subauthority; the SID lies in the buffer, which stays where it is until this
                // method returns.
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Sid));
                return count == 0 ? null : (uint)Marshal.ReadInt32(GetSidSubAuthority(label.Sid, (uint)(count - 1)));
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    internal delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    // Out of context, the callback runs on the thread that set the hook, from its message loop, so on the hook thread it
    // reaches the engine the way the keyboard hook does. Delegate marshalling uses classic DllImport, as for
    // SetWindowsHookEx.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc pfnWinEventProc, uint idProcess, uint idThread,
        uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hWinEventHook);

    private const int UOI_IO = 6;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetThreadDesktop(uint dwThreadId);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationBool(
        nint hObj, int nIndex, out int pvInfo, int nLength, out int lpnLengthNeeded);

    /// <summary>
    /// Whether the calling thread's desktop is the one receiving input: false while the lock screen or a secure desktop
    /// has it, null when that cannot be told. Two quick calls that wait for nothing, safe on the hook thread; the handle
    /// GetThreadDesktop returns needs no closing. This is the check Raymond Chen gives for a desktop-switch notice
    /// ("How can I detect that the system is no longer showing a UAC prompt?", The Old New Thing, 2020).
    /// </summary>
    internal static bool? ThreadDesktopReceivesInput() => DesktopReceivesInput(GetThreadDesktop(GetCurrentThreadId()));

    /// <summary>
    /// Whether <paramref name="desktop"/> is the desktop receiving input, from GetUserObjectInformation with UOI_IO; null
    /// for no handle, or when the query fails (it does for a handle that is not a desktop).
    /// </summary>
    internal static bool? DesktopReceivesInput(nint desktop)
    {
        if (desktop == 0)
        {
            return null;
        }

        return GetUserObjectInformationBool(desktop, UOI_IO, out var receivesInput, sizeof(int), out _)
            ? receivesInput != 0
            : null;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// Time since the last keyboard or mouse input in this session, or null when Windows declines
    /// to report it. This is the same clock the power manager idles against, so it is how the
    /// watchdog tells "someone is at the keyboard" from "the machine is trying to go to sleep".
    /// </summary>
    internal static TimeSpan? TryGetSystemIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // dwTime is a 32-bit GetTickCount stamp that wraps every ~49.7 days. Subtracting in
        // unsigned arithmetic makes the wrap cancel out; a signed compare would read as negative
        // idle for the ~49 days after each rollover.
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
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

    // The INPUT type for mouse input. Scribe injects none (the leak check covers keys only); the tests build theirs with it.
    internal const uint INPUT_MOUSE = 0;
}
