namespace Scribe.Core.Settings;

/// <summary>A text-only edit of one dictionary row, with any new ways that write the same text.</summary>
public static class DictionaryWordEditor
{
    public sealed record Result(
        DictionaryEntryBuilder.Row? EditedRow,
        IReadOnlyList<DictionaryEntryBuilder.Row> AddedRows,
        string? Error = null,
        int ErrorFormIndex = 0)
    {
        public bool Succeeded => Error is null;
    }

    public static Result Build(
        IReadOnlyList<DictionaryEntryBuilder.Row> existing,
        int? editedIndex,
        string replacement,
        IReadOnlyList<string> forms)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(forms);
        if (editedIndex is { } index && (index < 0 || index >= existing.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(editedIndex));
        }

        if (forms.Count == 0 || (editedIndex.HasValue && string.IsNullOrWhiteSpace(forms[0])))
        {
            return new(null, [], SettingsDraftValidator.DictionarySpokenEmptyMessage);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < existing.Count; i++)
        {
            if (i != editedIndex && !string.IsNullOrWhiteSpace(existing[i].Pattern))
            {
                seen.Add(existing[i].Pattern!.Trim());
            }
        }

        DictionaryEntryBuilder.Row? edited = null;
        var added = new List<DictionaryEntryBuilder.Row>();
        for (var i = 0; i < forms.Count; i++)
        {
            var form = forms[i];
            if (string.IsNullOrWhiteSpace(form))
            {
                continue;
            }

            var unchanged = i == 0 && editedIndex is { } selected &&
                string.Equals(form, existing[selected].Pattern, StringComparison.Ordinal) &&
                string.Equals(replacement, existing[selected].Replacement, StringComparison.Ordinal);
            if (!seen.Add(form.Trim()) && !unchanged)
            {
                return new(null, [], $"\"{form.Trim()}\" is already in your dictionary. Keep one of the two ways.", i);
            }

            if (i == 0 && editedIndex is { } rowIndex)
            {
                edited = existing[rowIndex] with { Pattern = form, Replacement = replacement };
            }
            else
            {
                added.Add(new(0, form, replacement, WholeWord: true, Enabled: true));
            }
        }

        return edited is null && added.Count == 0
            ? new(null, [], "Type at least one way Scribe hears this word.")
            : new(edited, added.ToArray());
    }

    // WPF can present LF text as CRLF. Opening and accepting an editor must not rewrite an untouched value.
    public static string PreserveUnchangedText(string original, string displayedOriginal, string current) =>
        string.Equals(displayedOriginal, current, StringComparison.Ordinal) ? original : current;
}
