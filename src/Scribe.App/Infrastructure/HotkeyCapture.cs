using System.Runtime.InteropServices;
using System.Windows.Input;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Translates WPF keyboard events from the settings UI into a <see cref="HotkeyBinding"/> the
/// low-level hook can match, and renders a binding as friendly text. Right/left modifier
/// variants are preserved (the hook receives distinct virtual-key codes such as VK_RCONTROL),
/// and Alt-involved presses are resolved through <see cref="KeyEventArgs.SystemKey"/>. Keys are
/// named by virtual-key code through <see cref="HotkeyText"/>, never by WPF's <see cref="Key"/>
/// names: that enum gives several keys two names (Page Down is also <c>Key.Next</c>) and does not
/// promise which one <c>ToString</c> returns.
/// </summary>
internal static class HotkeyCapture
{
    private const uint MapVkToChar = 2; // MAPVK_VK_TO_CHAR

    /// <summary>Builds an exact physical one- or two-key binding in the order keys were pressed.</summary>
    public static HotkeyBinding FromKeys(IReadOnlyList<Key> keys, HotkeyMode mode)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count is < 1 or > 2)
        {
            throw new ArgumentException("A hotkey must contain one or two keys.", nameof(keys));
        }

        var primary = (uint)KeyInterop.VirtualKeyFromKey(keys[0]);
        uint? secondary = keys.Count == 2 ? (uint)KeyInterop.VirtualKeyFromKey(keys[1]) : null;

        // Stored for older builds to show; Describe names the keys afresh from their codes.
        var display = string.Join("+", keys.Select(KeyName));
        return new HotkeyBinding(
            primary,
            KeyModifiers.None,
            mode,
            Suppress: true,
            display,
            SecondaryVirtualKey: secondary,
            SuppressChordMembers: secondary is not null);
    }

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

    /// <summary>The name of one key, as the capture box shows it while the user presses it.</summary>
    public static string KeyName(Key key) =>
        HotkeyText.KeyName((uint)KeyInterop.VirtualKeyFromKey(key), LayoutKeyName) ?? key.ToString();

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

    // The punctuation keys type something different on each keyboard layout, so the table leaves them out and the
    // current layout names them by the character they type.
    private static string? LayoutKeyName(uint virtualKey) =>
        KeyNames.FromMappedCharacter(MapVirtualKeyW(virtualKey, MapVkToChar));

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}
