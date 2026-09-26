using System.Runtime.InteropServices;

namespace Scribe.Core.TextInjection;

/// <summary>A scan code for an injected key event, and whether it is an extended key (one with an 0xE0 prefix).</summary>
internal readonly record struct KeyScanCode(ushort Code, bool Extended)
{
    /// <summary>No scan code: what every VK-based event Scribe injected carried before.</summary>
    public static KeyScanCode None => default;
}

/// <summary>
/// The scan codes of the four VK-based keys text insertion sends: Shift and Return for a typed line break, Ctrl and V for
/// the paste chord. Read once per insertion, from the layout of the window about to receive them.
/// </summary>
internal readonly record struct InjectionKeys(KeyScanCode Shift, KeyScanCode Return, KeyScanCode Control, KeyScanCode V)
{
    public static InjectionKeys From(IInjectionPlatform platform) => new(
        platform.ScanCodeOf(InjectionNativeMethods.VK_SHIFT),
        platform.ScanCodeOf(InjectionNativeMethods.VK_RETURN),
        platform.ScanCodeOf(InjectionNativeMethods.VK_CONTROL),
        platform.ScanCodeOf(InjectionNativeMethods.VK_V));
}

/// <summary>
/// The scan code Windows would give a virtual key, for the VK-based events Scribe injects (text insertion's Shift, Return,
/// Ctrl and V, and the leaked-key repair's key-ups). Remote Desktop and virtual machine clients forward keys by scan code
/// ([MS-RDPBCGR]: a keyboard event's keyCode is "The scancode of the key which triggered the event"), and an injected
/// event's scan code is whatever wScan says ("A hardware scan code for the key", KEYBDINPUT), so with wScan 0 a remote
/// session received scan code 0 for every one of them. KEYEVENTF_SCANCODE is never set ("If specified, wScan identifies
/// the key and wVk is ignored"), so Windows still takes the virtual key from wVk and local apps get exactly the keys they
/// got before.
/// </summary>
internal static partial class KeyScanCodes
{
    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventScanCode = 0x0008;

    // MapVirtualKeyEx: "The uCode parameter is a virtual-key code and is translated into a scan code. If it is a virtual-key
    // code that does not distinguish between left- and right-hand keys, the left-hand scan code is returned. If the scan
    // code is an extended scan code, the high byte of the returned value will contain either 0xe0 or 0xe1".
    private const uint MapVkToVscEx = 4;

    /// <summary>
    /// A MAPVK_VK_TO_VSC_EX answer as the event's wScan and extended flag. An 0xE0 prefix is KEYEVENTF_EXTENDEDKEY's ("the
    /// wScan scan code consists of a sequence of two bytes, where the first byte has a value of 0xE0"). A key with an 0xE1
    /// prefix (Pause) has no scan code KEYBDINPUT can express, so it keeps none, as every key did before. The keys the
    /// leaked-key repair always sent extended (the right-hand modifiers, the Windows keys, the navigation cluster, Num
    /// Lock and the keypad's divide) stay extended whatever the layout answers: MapVirtualKeyEx answers the navigation keys
    /// with the numeric keypad's scan codes and no prefix (measured on the US layout: VK_NEXT gives 0x0051, the keypad's 3,
    /// where the dedicated Page Down key is E0 51), and without the flag a key-up names the keypad's key.
    /// </summary>
    public static KeyScanCode Decode(uint virtualKey, uint vscEx)
    {
        var prefix = vscEx & 0xFF00;
        if (prefix == 0xE100)
        {
            return new KeyScanCode(0, IsExtendedVirtualKey(virtualKey));
        }

        return new KeyScanCode((ushort)(vscEx & 0xFF), prefix == 0xE000 || IsExtendedVirtualKey(virtualKey));
    }

    /// <summary>The keys whose injected events always carry KEYEVENTF_EXTENDEDKEY (see <see cref="Decode"/>).</summary>
    public static bool IsExtendedVirtualKey(uint virtualKey) => virtualKey is
        0xA3 /* RCtrl */ or 0xA5 /* RAlt */ or 0x5B /* LWin */ or 0x5C /* RWin */ or
        0x21 /* PgUp */ or 0x22 /* PgDn */ or 0x23 /* End */ or 0x24 /* Home */ or
        0x25 or 0x26 or 0x27 or 0x28 /* arrows */ or
        0x2D /* Insert */ or 0x2E /* Delete */ or 0x90 /* NumLock */ or 0x6F /* NumDivide */;

    /// <summary>
    /// The scan code of <paramref name="virtualKey"/> on the keyboard layout of the foreground window's thread, the one
    /// that reads the injected keys, or on the calling thread's when there is no foreground window. Off the hook
    /// callbacks only: it asks for the foreground window and its thread.
    /// </summary>
    public static KeyScanCode ForForegroundLayout(uint virtualKey)
    {
        var foreground = InjectionNativeMethods.GetForegroundWindow();
        var thread = foreground == 0 ? 0 : InjectionNativeMethods.GetWindowThreadProcessId(foreground, out _);

        // GetKeyboardLayout: "The identifier of the thread to query, or 0 for the current thread."
        return Decode(virtualKey, MapVirtualKeyExW(virtualKey, MapVkToVscEx, GetKeyboardLayout(thread)));
    }

    /// <summary>
    /// KEYBDINPUT flags for a VK-based event with this scan code: KEYEVENTF_EXTENDEDKEY for an extended key, and never
    /// KEYEVENTF_SCANCODE.
    /// </summary>
    public static uint Flags(KeyScanCode scan, bool keyUp) =>
        (keyUp ? InjectionNativeMethods.KEYEVENTF_KEYUP : 0u) | (scan.Extended ? KeyEventExtendedKey : 0u);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, nint dwhkl);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint idThread);
}
