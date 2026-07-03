using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Pure builder for the desired snippet state edited in the settings window, mirroring
/// <see cref="DictionaryEntryBuilder"/>: rows in, built <see cref="Snippet"/> list plus the first
/// duplicate trigger phrase out, testable without the WPF editor.
/// </summary>
public static class SnippetBuilder
{
    /// <summary>One editor row: identity and the raw trigger phrase, template, and enabled flag.</summary>
    public readonly record struct Row(long Id, string? Phrase, string? Template, bool Enabled);

    /// <summary>
    /// The built snippets plus <see cref="DuplicateIndex"/>: the position in the input list of the
    /// first row whose trimmed trigger phrase (case-insensitive) repeats an earlier row, or -1.
    /// </summary>
    public readonly record struct Result(IReadOnlyList<Snippet> Snippets, int DuplicateIndex)
    {
        public bool HasDuplicate => DuplicateIndex >= 0;
    }

    /// <summary>
    /// Builds the snippets from <paramref name="rows"/>, skipping rows with a blank phrase or
    /// template, trimming the phrase, and reporting the first duplicate trigger phrase.
    /// </summary>
    public static Result Build(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var snippets = new List<Snippet>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateIndex = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (string.IsNullOrWhiteSpace(row.Phrase) || string.IsNullOrWhiteSpace(row.Template))
            {
                continue;
            }

            var phrase = row.Phrase.Trim();
            if (!seen.Add(phrase) && duplicateIndex < 0)
            {
                duplicateIndex = i;
            }

            snippets.Add(new Snippet(row.Id, phrase, row.Template ?? string.Empty, row.Enabled));
        }

        return new Result(snippets, duplicateIndex);
    }
}
