namespace Scribe.Core.Settings;

/// <summary>
/// The Dictionary page's two tabs, in the words people read: how much each holds, and the one line on each that says how
/// it differs from the other and leads there. Your words are the spellings a person adds, and they always win; word packs
/// are ready-made lists they turn on. The tabs are the only thing that separates the two, so each line names the other.
/// </summary>
public static class DictionaryTabsText
{
    /// <summary>The Your words tab's line, before its link to the Word packs tab.</summary>
    public const string YourWordsGuide = "Words you add yourself. If a word pack writes a word differently, yours wins.";

    /// <summary>The Your words tab's link to the Word packs tab.</summary>
    public const string YourWordsLink = "Browse word packs";

    /// <summary>The Word packs tab's line, before its link to the Your words tab.</summary>
    public const string WordPacksGuide =
        "Ready-made lists of words, like product names. Turn on the ones you use. Your own words always win.";

    /// <summary>The Word packs tab's link to the Your words tab.</summary>
    public const string WordPacksLink = "Go to Your words";

    /// <summary>What the Your words tab shows under its name: how many words, or nothing until they have loaded.</summary>
    public static string YourWordsSummary(int? words) =>
        words is { } count ? WordPackUiText.WordCount(Math.Max(0, count)) : string.Empty;

    /// <summary>
    /// What the Word packs tab shows under its name: how many word packs are on of how many there are, or nothing until
    /// they have loaded.
    /// </summary>
    public static string WordPacksSummary(int? on, int total)
    {
        if (on is not { } count)
        {
            return string.Empty;
        }

        return total <= 0 ? "No word packs" : $"{Math.Clamp(count, 0, total):N0} of {total:N0} on";
    }

    /// <summary>What a screen reader hears for a tab after its name: its summary, when there is one, then its line.</summary>
    public static string HelpText(string summary, string guide) =>
        string.IsNullOrWhiteSpace(summary) ? guide : $"{summary}. {guide}";
}
