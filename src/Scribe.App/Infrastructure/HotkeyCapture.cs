using System.Windows.Input;
using Scribe.Core.Models;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Translates WPF keyboard events from the settings UI into a <see cref="HotkeyBinding"/> the
/// low-level hook can match, and renders a binding as friendly text. Right/left modifier
/// variants are preserved (the hook receives distinct virtual-key codes such as VK_RCONTROL),
/// and Alt-involved presses are resolved through <see cref="KeyEventArgs.SystemKey"/>.
/// </summary>
internal static class HotkeyCapture
{
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
        var display = string.Join("+", keys.Select(FriendlyKeyName));
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
            return new HotkeyBinding(vk, KeyModifiers.None, mode, Suppress: true, FriendlyKeyName(key));
        }

        var modifiers = CurrentModifiers();
        var suppress = modifiers != KeyModifiers.None;
        var display = Describe(modifiers, FriendlyKeyName(key));
        return new HotkeyBinding(vk, modifiers, mode, suppress, display);
    }

    /// <summary>Renders an existing binding (mode included) as user-facing text.</summary>
    public static string Describe(HotkeyBinding binding)
    {
        var keyName = binding.DisplayName;
        if (string.IsNullOrWhiteSpace(keyName))
        {
            var key = KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey);
            keyName = FriendlyKeyName(key);
        }
        else if (binding.Modifiers != KeyModifiers.None && !keyName.Contains('+'))
        {
            keyName = Describe(binding.Modifiers, keyName);
        }

        if (binding.SecondaryVirtualKey is { } second && !keyName.Contains('+'))
        {
            keyName += "+" + FriendlyKeyName(KeyInterop.KeyFromVirtualKey((int)second));
        }

        return keyName;
    }

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

    private static string Describe(KeyModifiers modifiers, string keyName)
    {
        if (modifiers == KeyModifiers.None)
        {
            return keyName;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(keyName);
        return string.Join("+", parts);
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

    private static string FriendlyKeyName(Key key) => key switch
    {
        Key.LeftCtrl => "Left Ctrl",
        Key.RightCtrl => "Right Ctrl",
        Key.LeftAlt => "Left Alt",
        Key.RightAlt => "Right Alt",
        Key.LeftShift => "Left Shift",
        Key.RightShift => "Right Shift",
        Key.LWin => "Left Win",
        Key.RWin => "Right Win",
        Key.Space => "Space",
        Key.Return => "Enter",
        _ => key.ToString(),
    };
}
