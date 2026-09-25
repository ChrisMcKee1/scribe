namespace Scribe.Core.Settings;

public static class QuickAddSelection
{
    public static QuickDictionaryAdd.WordRange Reconcile(string? boxText, string? selectedText, QuickDictionaryAdd.WordRange range) =>
        string.Equals((boxText ?? string.Empty).Trim(), (selectedText ?? string.Empty).Trim(), StringComparison.Ordinal)
            ? range
            : QuickDictionaryAdd.WordRange.None;
}
