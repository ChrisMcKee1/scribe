using System.Runtime.InteropServices;
using System.Windows.Input;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Maps WPF key and mouse events from the settings UI to the virtual-key codes the low-level hooks
/// match, and renders a binding as friendly text. Right/left modifier variants are preserved (the
/// hook receives distinct virtual-key codes such as VK_RCONTROL), and Alt-involved presses are
/// resolved through <see cref="KeyEventArgs.SystemKey"/>. What those codes become is decided in Core
/// (<see cref="HotkeyCaptureSession"/>). Keys are named by virtual-key code through
/// <see cref="HotkeyText"/>, never by WPF's <see cref="Key"/> or <see cref="MouseButton"/> names: that
/// enum gives several keys two names (Page Down is also <c>Key.Next</c>) and does not promise which one
/// <c>ToString</c> returns.
/// </summary>
internal static class HotkeyCapture
{
    private const uint MapVkToChar = 2; // MAPVK_VK_TO_CHAR
    private const uint MapVkToVscEx = 4; // MAPVK_VK_TO_VSC_EX

    /// <summary>A capture that names keys by the current keyboard layout, as Settings shows them.</summary>
    public static HotkeyCaptureSession NewSession() => new(LayoutKeyName);

    /// <summary>The virtual-key code of the key a WPF key event is for, Alt-involved presses included.</summary>
    public static uint VirtualKeyOf(KeyEventArgs e) =>
        (uint)KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);

    /// <summary>
    /// The virtual-key code of a WPF mouse button: WPF names the side buttons XButton1 and XButton2, which are Back
    /// (VK_XBUTTON1) and Forward (VK_XBUTTON2). The left and right buttons map too, so the capture can refuse them.
    /// </summary>
    public static uint VirtualKeyOf(MouseButton button) => button switch
    {
        MouseButton.Left => MouseButtons.Left,
        MouseButton.Right => MouseButtons.Right,
        MouseButton.Middle => MouseButtons.Middle,
        MouseButton.XButton1 => MouseButtons.Back,
        MouseButton.XButton2 => MouseButtons.Forward,
        _ => 0,
    };

    /// <summary>
    /// Builds a binding from a captured key press. A lone modifier key (the common push-to-talk
    /// case, e.g. Right Ctrl) becomes a standalone trigger with no modifiers; any other key keeps
    /// the modifiers currently held. The key is suppressed only when it is a modifier or carries
    /// modifiers, so binding a bare printable key never globally swallows that character.
    /// </summary>
    public static HotkeyBinding FromKeyEvent(KeyEventArgs e, HotkeyMode mode)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (IsModifierKey(key))
        {
            return new HotkeyBinding(vk, KeyModifiers.None, mode, Suppress: true, KeyName(key));
        }

        var modifiers = CurrentModifiers();
        var suppress = modifiers != KeyModifiers.None;
        var binding = new HotkeyBinding(vk, modifiers, mode, suppress);
        return binding with { DisplayName = Describe(binding) };
    }

    /// <summary>Renders an existing binding (mode included) as user-facing text.</summary>
    public static string Describe(HotkeyBinding binding) => HotkeyText.Describe(binding, LayoutKeyName);

    /// <summary>What the welcome says about the push-to-talk gesture, with keys named as Settings names them.</summary>
    public static (string Title, string Body) Gesture(AppSettings? settings) => HotkeyText.Gesture(settings, LayoutKeyName);

    /// <summary>
    /// The name of one key, as the capture box shows it while the user presses it and as a new binding stores it: the
    /// canonical name, else the current layout's, else the key's code. Never <c>Key.ToString()</c>, which can give a
    /// Korean keyboard's Hangul key the name "KanaMode" (both are 0x15).
    /// </summary>
    public static string KeyName(Key key) =>
        HotkeyText.KeyNameOrCode((uint)KeyInterop.VirtualKeyFromKey(key), LayoutKeyName);

    public static bool IsReservedWindowsChord(HotkeyBinding binding)
    {
        var keys = PhysicalKeys(binding);
        return keys.Contains(Key.LWin) || keys.Contains(Key.RWin);
    }

    public static string? AccessibilityRisk(HotkeyBinding binding)
    {
        var keys = PhysicalKeys(binding);
        if (binding.Mode != HotkeyMode.Hold)
        {
            return null;
        }

        if (keys.Count == 1 && keys[0] == Key.RightShift)
        {
            return "Holding Right Shift can activate Windows FilterKeys.";
        }

        if (keys.Count == 1 && keys[0] == Key.NumLock)
        {
            return "Holding Num Lock can activate Windows ToggleKeys.";
        }

        if (keys.Count == 1 && IsModifierKey(keys[0]))
        {
            return "Modifier-only hold keys can interact with Windows StickyKeys when that feature is enabled.";
        }

        return null;
    }

    private static List<Key> PhysicalKeys(HotkeyBinding binding)
    {
        var keys = new List<Key> { KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey) };
        if (binding.SecondaryVirtualKey is { } second)
        {
            keys.Add(KeyInterop.KeyFromVirtualKey((int)second));
        }

        return keys;
    }

    private static KeyModifiers CurrentModifiers()
    {
        var result = KeyModifiers.None;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) result |= KeyModifiers.Control;
        if (mods.HasFlag(ModifierKeys.Alt)) result |= KeyModifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Shift)) result |= KeyModifiers.Shift;
        if (mods.HasFlag(ModifierKeys.Windows)) result |= KeyModifiers.Win;
        return result;
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    // The punctuation keys and the IME keys mean something different on each keyboard layout, so the table leaves them
    // out and the current layout names them: by the character the key types, else, for a key that types none (an IME
    // key, say), by the name the layout gives its scan code.
    private static string? LayoutKeyName(uint virtualKey) =>
        KeyNames.FromMappedCharacter(MapVirtualKeyW(virtualKey, MapVkToChar)) ?? LayoutKeyNameText(virtualKey);

    // GetKeyNameTextW takes a keyboard message's lParam: the scan code in bits 16 to 23 and the extended-key flag in bit
    // 24, which MAPVK_VK_TO_VSC_EX reports as an 0xE0 or 0xE1 prefix in the high byte.
    private static string? LayoutKeyNameText(uint virtualKey)
    {
        var scanCode = MapVirtualKeyW(virtualKey, MapVkToVscEx);
        if ((scanCode & 0xFF) == 0)
        {
            return null;
        }

        var lParam = (int)((scanCode & 0xFF) << 16);
        if ((scanCode & 0xFF00) is 0xE000 or 0xE100)
        {
            lParam |= 1 << 24;
        }

        var buffer = new char[64];
        var length = GetKeyNameTextW(lParam, buffer, buffer.Length);
        return length > 0 ? KeyNames.FromLayoutKeyName(new string(buffer, 0, length), virtualKey) : null;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int lParam, [Out] char[] lpString, int cchSize);
}
