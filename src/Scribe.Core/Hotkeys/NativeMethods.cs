using System.Reflection;
using System.Runtime.InteropServices;
using Scribe.Core.TextInjection;

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
    // mouse hook afresh (see HotkeyService.HookInstallation). The third has it register its keyboard hook afresh, ahead
    // of a Remote Desktop client's, and the fourth release the registrations those moves replaced (wParam 1: whatever
    // their age, for tests).
    internal const uint WM_APP = 0x8000;
    internal const uint WM_HOTKEY_COMMANDS = WM_APP + 1;
    internal const uint WM_HOTKEY_REFRESH = WM_APP + 2;
    internal const uint WM_HOTKEY_MOVE_AHEAD = WM_APP + 3;
    internal const uint WM_HOTKEY_RELEASE_RETIRED = WM_APP + 4;

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
    /// Before either hook exists (the service's constructor): does the runtime's one-time work for the two P/Invokes the
    /// hook callbacks call, CallNextHookEx in both and GetAsyncKeyState for the modifier rule and an owed button release.
    /// <see cref="Marshal.Prelink"/> "executes one-time method setup tasks without calling the method", which Learn lists
    /// as verifying the signature, locating and loading the DLL and locating the entry point, work each P/Invoke's first
    /// call otherwise does, and which allocated on that call (48 bytes for each of these two, measured in the test host
    /// before round 8), inside Windows' deadline. Found by the entry point the import names, so a generated wrapper around
    /// an import is covered too. Returns the entry points it prelinked; for a test.
    /// </summary>
    internal static IReadOnlyList<string> PrelinkHookCalls()
    {
        var prelinked = new List<string>(2);
        foreach (var method in typeof(NativeMethods).GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0 &&
                method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint is { } entryPoint and
                    ("CallNextHookEx" or "GetAsyncKeyState"))
            {
                Marshal.Prelink(method);
                prelinked.Add(entryPoint);
            }
        }

        return prelinked;
    }

    internal const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;

    // "The foreground window has changed. The system sends this event even if the foreground window has changed to
    // another window in the same thread." (Event Constants)
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
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

    /// <summary>
    /// Injects a synthetic key event tagged with <see cref="SyntheticInputMarker"/> so the hook
    /// ignores it for chord state (the liveness probe still ticks the callback counter).
    /// </summary>
    internal static bool SendMarkedKeyEvent(ushort virtualKey, bool keyUp) =>
        SendInput(1, [MarkedKeyEvent(virtualKey, keyUp)], Marshal.SizeOf<INPUT>()) == 1;

    /// <summary>The event <see cref="SendMarkedKeyEvent"/> sends, with its scan code from the foreground window's layout.</summary>
    internal static INPUT MarkedKeyEvent(ushort virtualKey, bool keyUp) =>
        BuildMarkedKeyEvent(virtualKey, keyUp, MarkedKeyScanCode(virtualKey, KeyScanCodes.ForForegroundLayout));

    /// <summary>
    /// The scan code a marked key event carries: the layout's for every key (the leaked-key repair's key-ups), so a Remote
    /// Desktop or virtual machine client, which forwards keys by scan code, forwards the key that was pressed; none for the
    /// watchdog's probe, an unassigned virtual key the layout is not asked about.
    /// </summary>
    internal static KeyScanCode MarkedKeyScanCode(ushort virtualKey, Func<uint, KeyScanCode> scanCodeOf) =>
        virtualKey == VK_PROBE ? KeyScanCode.None : scanCodeOf(virtualKey);

    /// <summary>
    /// A marked key event: the virtual key, the scan code and extended flag <paramref name="scan"/> gives it, and never
    /// KEYEVENTF_SCANCODE, so Windows takes the key from wVk as it always has (see <see cref="KeyScanCodes"/>). Right-hand
    /// modifiers and the navigation cluster stay extended: a synthetic key-up without the flag maps to the left-hand
    /// sibling and would fail to release the right key.
    /// </summary>
    internal static INPUT BuildMarkedKeyEvent(ushort virtualKey, bool keyUp, KeyScanCode scan) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scan.Code,
                dwFlags = KeyScanCodes.Flags(
                    scan with { Extended = scan.Extended || KeyScanCodes.IsExtendedVirtualKey(virtualKey) }, keyUp),
                dwExtraInfo = SyntheticInputMarker.Value,
            },
        },
    };

    // The INPUT type for mouse input. Scribe injects none (the leak check covers keys only); the tests build theirs with it.
    internal const uint INPUT_MOUSE = 0;
}
