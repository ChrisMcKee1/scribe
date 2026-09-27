using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// CSV round-tripping for a dictionary <b>library</b>: the same row format as
/// <see cref="DictionaryCsv"/> (<c>pattern,replacement,whole_word,enabled</c>) plus an optional
/// metadata header carried in comment lines, so a single file is self-describing when shared:
/// <code>
/// # name: Microsoft Azure
/// # category: Microsoft
/// # description: Azure services and common acronyms
/// pattern,replacement,whole_word,enabled
/// a p i m,APIM,true,true
/// </code>
/// Files without the header still import (the caller supplies a name from the file name), so any
/// plain dictionary CSV doubles as a library. Row parsing and quoting are delegated to
/// <see cref="DictionaryCsv"/>; this only adds the header layer.
/// </summary>
/// <remarks>
/// A thin wrapper over the library CSV codec (pattern P-2): <see cref="Parse"/> is the codec's reading by 0.4.3's rules
/// (<see cref="LibraryCsvCodec.ReadLegacy"/>), the one a managed file without the format marker gets, so this and the
/// codec cannot drift apart. <see cref="Export"/> keeps 0.4.3's form (raw metadata lines, no format marker); files this
/// version stores go through <see cref="LibraryCsvCodec.WriteManaged"/> instead.
/// </remarks>
public static class DictionaryLibraryCsv
{
    /// <summary>
    /// Parses a library CSV into its metadata (from the comment header, if present) and entries.
    /// Never throws on content: unreadable rows land in <see cref="DictionaryLibraryFile.Errors"/>
    /// with their line number while the good rows still import.
    /// </summary>
    public static DictionaryLibraryFile Parse(string? csv)
    {
        var file = LibraryCsvCodec.ReadLegacy(csv);
        return new DictionaryLibraryFile(
            file.Name,
            file.Category,
            file.Description,
            file.Terms.Select(term => term.ToEntry()).ToList(),
            file.Errors.Select(DictionaryCsv.Describe).ToList());
    }

    /// <summary>
    /// Renders a library as a shareable CSV document: the metadata header followed by the entry rows
    /// in <see cref="DictionaryCsv"/> format.
    /// </summary>
    public static string Export(DictionaryLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var sb = new StringBuilder();
        sb.Append("# name: ").AppendLine(SingleLine(library.Name));
        sb.Append("# category: ").AppendLine(SingleLine(library.Category));
        if (!string.IsNullOrWhiteSpace(library.Description))
        {
            sb.Append("# description: ").AppendLine(SingleLine(library.Description));
        }

        sb.Append(DictionaryCsv.Export(library.Entries));
        return sb.ToString();
    }

    // Metadata is single-line: flatten any control characters so a value can't spill into extra
    // header lines or break the comment convention when re-imported.
    private static string SingleLine(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}

/// <summary>
/// Outcome of parsing a library CSV: the header metadata (any of which may be <see langword="null"/>
/// when the file omits it), the usable entries, and per-line errors.
/// </summary>
public sealed record DictionaryLibraryFile(
    string? Name,
    string? Category,
    string? Description,
    IReadOnlyList<DictionaryEntry> Entries,
    IReadOnlyList<string> Errors);
