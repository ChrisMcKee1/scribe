using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// How Scribe states a hotkey to the user and in the log. Every key is named from its virtual-key code through
/// <see cref="KeyNames"/>, the way the hook matches it, never from a name stored with the binding, so an old binding
/// stored as "Next" reads "Page Down" everywhere it is shown.
/// </summary>
public static class HotkeyText
{
    /// <summary>
    /// The name of one key: its canonical name when it means the same on every layout, otherwise what
    /// <paramref name="layoutName"/> says (the shell asks the current keyboard layout), otherwise null.
    /// </summary>
    public static string? KeyName(uint virtualKey, Func<uint, string?>? layoutName = null) =>
        KeyNames.Of(virtualKey) ?? NullIfBlank(layoutName?.Invoke(virtualKey));

    /// <summary>
    /// Describes a binding the way Settings shows it: "Page Down", "Ctrl+Shift+Space", "Right Ctrl+Right Shift". Only a
    /// binding with a key that neither the table nor <paramref name="layoutName"/> can name falls back to the name stored
    /// when it was bound, and one with no stored name to the key's code.
    /// </summary>
    public static string Describe(HotkeyBinding binding, Func<uint, string?>? layoutName = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var primary = KeyName(binding.VirtualKey, layoutName);
        string? secondary = null;
        var named = primary is not null;
        if (binding.SecondaryVirtualKey is { } second)
        {
            secondary = KeyName(second, layoutName);
            named &= secondary is not null;
        }

        if (!named && !string.IsNullOrWhiteSpace(binding.DisplayName))
        {
            return FromStoredName(binding, binding.DisplayName.Trim(), layoutName);
        }

        var parts = ModifierNames(binding.Modifiers);
        parts.Add(primary ?? Code(binding.VirtualKey));
        if (binding.SecondaryVirtualKey is { } secondKey)
        {
            parts.Add(secondary ?? Code(secondKey));
        }

        return string.Join("+", parts);
    }

    /// <summary>
    /// What the welcome says about the push-to-talk gesture, for the keys this session actually uses and how they are
    /// pressed. Null settings (not loaded yet) gives wording that names no key rather than guessing one.
    /// </summary>
    public static (string Title, string Body) Gesture(AppSettings? settings, Func<uint, string?>? layoutName = null)
    {
        if (settings?.Hotkey is not { } dictation)
        {
            return (
                "Hold, speak, release",
                "Hold your push-to-talk key and start talking. Release when you are done, and the text appears wherever your cursor is.");
        }

        var key = Describe(dictation, layoutName);
        var (title, body) = dictation.Mode == HotkeyMode.Toggle
            ? ("Press, speak, press again",
               $"Press {key} and start talking. Press it again when you are done, and the text appears wherever your cursor is.")
            : ("Hold, speak, release",
               $"Hold {key} and start talking. Release when you are done, and the text appears wherever your cursor is.");

        if (settings.DictationOnlyHotkey is { } dictationOnly)
        {
            body += $" {Verb(dictationOnly.Mode)} {Describe(dictationOnly, layoutName)} instead to dictate without AI cleanup.";
        }

        return (title, body);
    }

    /// <summary>"Hold" or "Press", for a sentence that starts with how a binding is used.</summary>
    internal static string Verb(HotkeyMode mode) => mode == HotkeyMode.Toggle ? "Press" : "Hold";

    // The shape older builds produced: a stored name that may or may not already carry the modifiers and the second key.
    private static string FromStoredName(HotkeyBinding binding, string stored, Func<uint, string?>? layoutName)
    {
        var text = stored;
        if (binding.Modifiers != KeyModifiers.None && !text.Contains('+'))
        {
            var parts = ModifierNames(binding.Modifiers);
            parts.Add(text);
            text = string.Join("+", parts);
        }

        if (binding.SecondaryVirtualKey is { } second && !text.Contains('+'))
        {
            text += "+" + (KeyName(second, layoutName) ?? Code(second));
        }

        return text;
    }

    private static List<string> ModifierNames(KeyModifiers modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return parts;
    }

    private static string Code(uint virtualKey) => $"Key 0x{virtualKey:X2}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
