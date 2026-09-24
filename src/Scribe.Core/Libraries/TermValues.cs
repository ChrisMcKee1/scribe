using Scribe.Core.Models;

namespace Scribe.Core.Libraries;

/// <summary>
/// One term's four stored values: how it is heard (<see cref="Spoken"/>, the dictionary's pattern), how it is written
/// (<see cref="Written"/>, the replacement), whether it matches whole words only, and whether it is on. The same shape
/// a library CSV row and a <see cref="DictionaryEntry"/> have.
/// </summary>
/// <remarks>
/// Storage is faithful: values are kept exactly as read or committed, so a long or multi-line <see cref="Written"/>
/// loads, edits, exports and imports unchanged. When a row is committed the editor trims both values, as the dictionary
/// editor does, and collapses white space inside <see cref="Spoken"/> (<see cref="LibraryTermKey.Normalize"/>); nothing
/// else rewrites either value, and inner line breaks in <see cref="Written"/> are never flattened. An empty
/// <see cref="Written"/> is a removal rule (the matched words are left out), which is valid in storage; only the editor
/// asks for the intent before a new or cleared value may be saved that way. Neither string is ever null.
/// </remarks>
public sealed record TermValues(string Spoken, string Written, bool WholeWord = true, bool Enabled = true)
{
    /// <summary>The values of <paramref name="entry"/>; a null pattern or replacement reads as empty.</summary>
    public static TermValues FromEntry(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new TermValues(entry.Pattern ?? string.Empty, entry.Replacement ?? string.Empty, entry.WholeWord, entry.Enabled);
    }

    /// <summary>These values as a not-yet-persisted dictionary entry (Id 0), the shape the post-processor compiles.</summary>
    public DictionaryEntry ToEntry() => new(0, Spoken, Written, WholeWord, Enabled);
}
