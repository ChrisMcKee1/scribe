using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public static class DictionaryDraftDiff
{
    public static IReadOnlySet<string> ChangedSpokenForms(IEnumerable<DictionaryEntry> loaded, IEnumerable<DictionaryEntry> staged)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(staged);
        var loadedGroups = Groups(loaded);
        var stagedGroups = Groups(staged);
        var keys = loadedGroups.Keys.Concat(stagedGroups.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            loadedGroups.TryGetValue(key, out var loadedRows);
            stagedGroups.TryGetValue(key, out var stagedRows);
            if (!SameGroup(loadedRows ?? [], stagedRows ?? []))
            {
                result.Add(DisplayKey(stagedRows ?? loadedRows ?? [], key));
            }
        }

        return result;
    }

    private static Dictionary<string, List<DictionaryEntry>> Groups(IEnumerable<DictionaryEntry> rows) => rows
        .Where(e => !string.IsNullOrWhiteSpace(e.Pattern))
        .GroupBy(e => Key(e.Pattern), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    private static bool SameGroup(IReadOnlyList<DictionaryEntry> left, IReadOnlyList<DictionaryEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var leftShapes = left.Select(Shape).Order(StringComparer.Ordinal).ToArray();
        var rightShapes = right.Select(Shape).Order(StringComparer.Ordinal).ToArray();
        return leftShapes.SequenceEqual(rightShapes, StringComparer.Ordinal);
    }

    private static string Shape(DictionaryEntry entry) => string.Join("\u001f", entry.Replacement.Trim(), entry.WholeWord, entry.Enabled);

    private static string DisplayKey(IReadOnlyList<DictionaryEntry> rows, string fallback) =>
        rows.FirstOrDefault()?.Pattern.Trim() is { Length: > 0 } value ? value : fallback;

    private static string Key(string value) => value.Trim();
}
