namespace Scribe.Core.Settings;

public enum SettingsPage
{
    Dictation,
    TryDictation,
    AiCleanup,
    Dictionary,
    VoiceSnippets,
    AppProfiles,
    History,
    Usage,
    Advanced,
    Diagnostics,
    About,
}

public sealed record SettingsNavigationItem(
    SettingsPage Page,
    string Label,
    string Group,
    int Position,
    bool IsFirstInGroup);

public static class SettingsNavigation
{
    public static IReadOnlyList<SettingsNavigationItem> Items { get; } =
    [
        new(SettingsPage.Dictation, "Dictation", string.Empty, 1, true),
        new(SettingsPage.TryDictation, "Try dictation", string.Empty, 2, false),
        new(SettingsPage.AiCleanup, "AI cleanup", string.Empty, 3, false),
        new(SettingsPage.Dictionary, "Dictionary", "Personalize", 4, true),
        new(SettingsPage.VoiceSnippets, "Voice snippets", "Personalize", 5, false),
        new(SettingsPage.AppProfiles, "App profiles", "Personalize", 6, false),
        new(SettingsPage.History, "History", "Review", 7, true),
        new(SettingsPage.Usage, "Usage", "Review", 8, false),
        new(SettingsPage.Advanced, "Advanced", "More", 9, true),
        new(SettingsPage.Diagnostics, "Diagnostics", "More", 10, false),
        new(SettingsPage.About, "About", "More", 11, false),
    ];

    public static string Title(SettingsPage page) =>
        Items.First(item => item.Page == page).Label;

    public static bool TryParsePage(string? value, out SettingsPage page)
    {
        page = SettingsPage.Dictation;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = Normalize(value);
        foreach (var item in Items)
        {
            if (Normalize(item.Label) == normalized ||
                Normalize(item.Page.ToString()) == normalized)
            {
                page = item.Page;
                return true;
            }
        }

        return false;
    }

    public static bool TryParseSettingsArgument(IEnumerable<string> args, out SettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(args);

        page = SettingsPage.Dictation;
        foreach (var arg in args)
        {
            const string prefix = "--settings=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return TryParsePage(arg[prefix.Length..], out page);
            }

            if (string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                page = SettingsPage.Dictation;
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..length]);
    }
}
