using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Pure builder for the desired dictionary state edited in the settings grid, kept out of the WPF
/// window so it can be tested without a UI. The window supplies plain rows (mapped from its editable
/// <c>DictionaryRow</c>) and gets back the built <see cref="DictionaryEntry"/> list plus the first
/// row whose spoken form duplicates an earlier one.
/// </summary>
public static class DictionaryEntryBuilder
{
    /// <summary>One editor row: the identity and raw (un-trimmed) field values as typed.</summary>
    public readonly record struct Row(
        long Id, string? Pattern, string? Replacement, bool WholeWord, bool Enabled);

    /// <summary>
    /// The built entries plus <see cref="DuplicateIndex"/>: the position in the input list of the
    /// first row whose trimmed spoken form (case-insensitive) repeats an earlier row, or -1 when
    /// there is no duplicate. Case-insensitive matches how the post-processor and AI glossary treat
    /// patterns.
    /// </summary>
    public readonly record struct Result(IReadOnlyList<DictionaryEntry> Entries, int DuplicateIndex)
    {
        public bool HasDuplicate => DuplicateIndex >= 0;
    }

    /// <summary>
    /// Builds the dictionary entries from <paramref name="rows"/>, skipping blank placeholder rows,
    /// trimming pattern and replacement, and reporting the first duplicate spoken form.
    /// </summary>
    public static Result Build(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var entries = new List<DictionaryEntry>();
        var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateIndex = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (string.IsNullOrWhiteSpace(row.Pattern))
            {
                continue; // skip blank placeholder / incomplete rows
            }

            var pattern = row.Pattern.Trim();
            if (!seenPatterns.Add(pattern) && duplicateIndex < 0)
            {
                duplicateIndex = i;
            }

            var replacement = (row.Replacement ?? string.Empty).Trim();
            entries.Add(new DictionaryEntry(row.Id, pattern, replacement, row.WholeWord, row.Enabled));
        }

        return new Result(entries, duplicateIndex);
    }
}
