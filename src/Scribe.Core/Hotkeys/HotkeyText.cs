using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// How Scribe states a hotkey to the user and in the log. Every key is named from its virtual-key code through
/// <see cref="KeyNames"/>, the way the hook matches it; a name stored with the binding only stands in for a key nothing
/// else can name, so an old binding stored as "Next" reads "Page Down" everywhere it is shown.
/// </summary>
public static class HotkeyText
{
    private const uint VkPrior = 0x21; // Page Up
    private const uint VkNext = 0x22; // Page Down

    /// <summary>
    /// The name of one key: its canonical name when it means the same on every layout, otherwise what
    /// <paramref name="layoutName"/> says (the shell asks the current keyboard layout), otherwise null.
    /// </summary>
    public static string? KeyName(uint virtualKey, Func<uint, string?>? layoutName = null) =>
        KeyNames.Of(virtualKey) ?? NullIfBlank(layoutName?.Invoke(virtualKey));

    /// <summary>
    /// Describes a binding the way Settings shows it: "Page Down", "Ctrl+Shift+Space", "Right Ctrl+Right Shift". Each
    /// key is named on its own: by the table, then by <paramref name="layoutName"/>, then by its own part of the name
    /// stored when it was bound, then by its code. So a chord of a known key and one only the stored name can name keeps
    /// the known key's true name ("Page Down+Oem1", never "Next+Oem1"), and the modifiers always come from the binding.
    /// </summary>
    public static string Describe(HotkeyBinding binding, Func<uint, string?>? layoutName = null)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var (storedPrimary, storedSecondary) = StoredKeyNames(binding);
        var parts = ModifierNames(binding.Modifiers);
        parts.Add(KeyName(binding.VirtualKey, layoutName) ?? storedPrimary ?? Code(binding.VirtualKey));
        if (binding.SecondaryVirtualKey is { } second)
        {
            parts.Add(KeyName(second, layoutName) ?? storedSecondary ?? Code(second));
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

        if (PageKeyFallback(dictation, settings.DictationOnlyHotkey) is { } fallback)
        {
            body += " " + fallback;
        }

        return (title, body);
    }

    /// <summary>"Hold" or "Press", for a sentence that starts with how a binding is used.</summary>
    internal static string Verb(HotkeyMode mode) => mode == HotkeyMode.Toggle ? "Press" : "Hold";

    // Where a keyboard without Page Down or Page Up has them, for the Page keys these bindings use, in the order they use
    // them: many laptops have no such keys, and every other place that names the defaults says so.
    private static string? PageKeyFallback(HotkeyBinding dictation, HotkeyBinding? dictationOnly)
    {
        var pageKeys = new List<uint>(2);
        foreach (var key in new[] { dictation.VirtualKey, dictation.SecondaryVirtualKey, dictationOnly?.VirtualKey, dictationOnly?.SecondaryVirtualKey })
        {
            if (key is VkNext or VkPrior && !pageKeys.Contains(key.Value))
            {
                pageKeys.Add(key.Value);
            }
        }

        static string Arrow(uint key) => key == VkNext ? "Down" : "Up";
        return pageKeys.Count switch
        {
            0 => null,
            1 => $"No {KeyNames.Of(pageKeys[0])} key? Most laptops have it on Fn with the {Arrow(pageKeys[0])} arrow.",
            _ => $"No {KeyNames.Of(pageKeys[0])} or {KeyNames.Of(pageKeys[1])} key? Most laptops have them on Fn with " +
                 $"the {Arrow(pageKeys[0])} and {Arrow(pageKeys[1])} arrows.",
        };
    }

    // Each key's part of the stored name, for a key nothing else can name. Every build stored the keys in the order they
    // were pressed, joined by "+", after any modifiers ("Ctrl+X"), and none put a "+" inside a key's name. The modifiers
    // are named from the binding itself, so the stored words for them are skipped and the last parts belong to the keys.
    // A name with fewer parts than keys named only the first key: older builds added the second key's name when they
    // showed the binding, not when they stored it.
    private static (string? Primary, string? Secondary) StoredKeyNames(HotkeyBinding binding)
    {
        var parts = (binding.DisplayName ?? string.Empty)
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifierWords = ModifierNames(binding.Modifiers);
        var skip = 0;
        while (skip < parts.Length && skip < modifierWords.Count &&
               modifierWords.Contains(parts[skip], StringComparer.OrdinalIgnoreCase))
        {
            skip++;
        }

        var keyParts = parts[skip..];
        var keys = binding.SecondaryVirtualKey is null ? 1 : 2;
        return keyParts.Length switch
        {
            0 => (null, null),
            _ when keyParts.Length < keys => (keyParts[0], null),
            _ when keys == 1 => (keyParts[^1], null),
            _ => (keyParts[^2], keyParts[^1]),
        };
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
