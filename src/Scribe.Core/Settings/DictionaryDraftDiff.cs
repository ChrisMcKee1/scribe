using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public static class DictionaryDraftDiff
{
    public static IReadOnlySet<string> ChangedSpokenForms(IEnumerable<DictionaryEntry> loaded, IEnumerable<DictionaryEntry> staged)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(staged);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedByKey = loaded.Where(e => !string.IsNullOrWhiteSpace(e.Pattern)).ToDictionary(e => Key(e.Pattern), e => e, StringComparer.OrdinalIgnoreCase);
        var stagedByKey = staged.Where(e => !string.IsNullOrWhiteSpace(e.Pattern)).ToDictionary(e => Key(e.Pattern), e => e, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in loadedByKey)
        {
            if (!stagedByKey.TryGetValue(key, out var stagedEntry))
            {
                result.Add(entry.Pattern.Trim());
                continue;
            }

            if (!string.Equals(entry.Replacement.Trim(), stagedEntry.Replacement.Trim(), StringComparison.Ordinal)
                || entry.WholeWord != stagedEntry.WholeWord
                || entry.Enabled != stagedEntry.Enabled)
            {
                result.Add(stagedEntry.Pattern.Trim());
            }
        }

        foreach (var (key, entry) in stagedByKey)
        {
            if (!loadedByKey.ContainsKey(key)) result.Add(entry.Pattern.Trim());
        }

        return result;
    }

    private static string Key(string value) => value.Trim();
}
