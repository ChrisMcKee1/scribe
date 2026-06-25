namespace Scribe.Core.Models;

/// <summary>
/// A single user-dictionary substitution applied during post-processing. When
/// <see cref="WholeWord"/> is set the pattern matches on word boundaries; otherwise it is a
/// plain (case-insensitive) phrase replacement.
/// </summary>
public sealed record DictionaryEntry(
    long Id,
    string Pattern,
    string Replacement,
    bool WholeWord = true,
    bool Enabled = true)
{
    /// <summary>A not-yet-persisted entry (Id 0).</summary>
    public static DictionaryEntry New(string pattern, string replacement, bool wholeWord = true) =>
        new(0, pattern, replacement, wholeWord);
}
