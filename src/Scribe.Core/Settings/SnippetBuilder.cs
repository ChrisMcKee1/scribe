using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Pure builder for the desired snippet state edited in the settings window, mirroring
/// <see cref="DictionaryEntryBuilder"/>: rows in, built <see cref="Snippet"/> list plus the first
/// duplicate trigger phrase out, testable without the WPF editor.
/// </summary>
public static class SnippetBuilder
{
    /// <summary>
    /// One editor row: identity and the raw trigger phrase, template, and enabled flag. <paramref name="KeepAsStored"/>
    /// marks a stored row the user hasn't changed: it is kept even when incomplete, because a Save stores the whole list
    /// and would otherwise delete a legacy snippet the user never touched while saving another one.
    /// </summary>
    public readonly record struct Row(long Id, string? Phrase, string? Template, bool Enabled, bool KeepAsStored = false);

    /// <summary>
    /// The built snippets plus <see cref="DuplicateIndex"/>: the position in the input list of the
    /// first new or changed row whose trimmed trigger phrase (case-insensitive) repeats another row, or -1; and
    /// <see cref="IncludedRows"/>, the input position of each built snippet, in order, so a caller knows exactly which rows
    /// were stored.
    /// </summary>
    public readonly record struct Result(IReadOnlyList<Snippet> Snippets, int DuplicateIndex, IReadOnlyList<int> IncludedRows)
    {
        public bool HasDuplicate => DuplicateIndex >= 0;
    }

    /// <summary>
    /// Builds the snippets from <paramref name="rows"/>, skipping rows with a blank phrase or
    /// template unless they are kept as stored, trimming the phrase, and reporting the first duplicate trigger phrase.
    /// </summary>
    public static Result Build(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // A row kept as stored is written exactly as stored, untrimmed, and never counts as a duplicate of another kept
        // row: storage already holds them side by side, and validation only warns about such a pair. A new or changed row
        // is still checked against every phrase, kept ones included.
        var keptPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.KeepAsStored)
            {
                keptPhrases.Add((row.Phrase ?? string.Empty).Trim());
            }
        }

        var snippets = new List<Snippet>();
        var included = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateIndex = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.KeepAsStored)
            {
                snippets.Add(new Snippet(row.Id, row.Phrase ?? string.Empty, row.Template ?? string.Empty, row.Enabled));
                included.Add(i);
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Phrase) || string.IsNullOrWhiteSpace(row.Template))
            {
                continue;
            }

            var phrase = row.Phrase.Trim();
            if ((!seen.Add(phrase) || keptPhrases.Contains(phrase)) && duplicateIndex < 0)
            {
                duplicateIndex = i;
            }

            snippets.Add(new Snippet(row.Id, phrase, row.Template, row.Enabled));
            included.Add(i);
        }

        return new Result(snippets, duplicateIndex, included);
    }
}
