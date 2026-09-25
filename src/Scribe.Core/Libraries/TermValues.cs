using Scribe.Core.Models;

namespace Scribe.Core.Libraries;

/// <summary>
/// One term's four stored values: how it is heard (<see cref="Spoken"/>, the dictionary's pattern), how it is written
/// (<see cref="Written"/>, the replacement), whether it matches whole words only, and whether it is on. The same shape
/// a library CSV row and a <see cref="DictionaryEntry"/> have.
/// </summary>
/// <remarks>
/// <para>
/// Storage is faithful: values are kept exactly as read or committed, so a long or multi-line <see cref="Written"/>
/// loads, edits, exports and imports unchanged. When a row is committed the editor trims both values, as the dictionary
/// editor does, and collapses white space inside <see cref="Spoken"/> (<see cref="LibraryTermKey.Normalize"/>); nothing
/// else rewrites either value, and inner line breaks in <see cref="Written"/> are never flattened. An empty
/// <see cref="Written"/> is a removal rule (the matched words are left out), which is valid in storage; only the editor
/// asks for the intent before a new or cleared value may be saved that way. Neither string is ever null.
/// </para>
/// <para>
/// Both strings are well-formed UTF-16: no unpaired surrogate. The editor refuses a typed or pasted one with a message,
/// and every writer rejects one rather than store it or turn it into U+FFFD; decoders never produce one.
/// </para>
/// <para>
/// In a built-in row's edit (<see cref="BuiltInTermEdit"/>), authorship is per field (plan 3.3): each of the four values
/// of an edited row is inherited, where the user's value equals the base (the shipped value applies, and an upgrade
/// brings the new one), or authored, where it differs. An edit changes the user's values only in the fields the user
/// changed relative to the row shown, so a field left alone stays inherited, and a field it changes is based on the
/// shipped value in use at that time (<see cref="IBuiltInLibraryOverlay.Edit"/>).
/// </para>
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
