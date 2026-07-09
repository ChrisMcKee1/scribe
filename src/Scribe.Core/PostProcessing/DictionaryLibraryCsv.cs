using System.Text;
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
public static class DictionaryLibraryCsv
{
    /// <summary>Recognized metadata keys in the comment header.</summary>
    private const string NameKey = "name";
    private const string CategoryKey = "category";
    private const string DescriptionKey = "description";

    /// <summary>
    /// Parses a library CSV into its metadata (from the comment header, if present) and entries.
    /// Never throws on content: unreadable rows land in <see cref="DictionaryLibraryFile.Errors"/>
    /// with their line number while the good rows still import.
    /// </summary>
    public static DictionaryLibraryFile Parse(string? csv)
    {
        string? name = null, category = null, description = null;

        if (!string.IsNullOrEmpty(csv))
        {
            using var reader = new StringReader(csv);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    TryReadMeta(trimmed, NameKey, ref name);
                    TryReadMeta(trimmed, CategoryKey, ref category);
                    TryReadMeta(trimmed, DescriptionKey, ref description);
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    continue; // tolerate blank lines before the header
                }

                break; // reached the header/data — metadata only lives at the top
            }
        }

        var parsed = DictionaryCsv.Parse(csv);
        return new DictionaryLibraryFile(
            NullIfBlank(name), NullIfBlank(category), NullIfBlank(description), parsed.Entries, parsed.Errors);
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

    // Reads "# key: value" (case-insensitive key, tolerant of spacing) into value; first line wins.
    private static void TryReadMeta(string commentLine, string key, ref string? value)
    {
        if (value is not null)
        {
            return;
        }

        var body = commentLine.TrimStart('#').TrimStart();
        if (body.Length <= key.Length ||
            !body.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
            body[key.Length] != ':')
        {
            return;
        }

        value = body[(key.Length + 1)..].Trim();
    }

    // Metadata is single-line: flatten any control characters so a value can't spill into extra
    // header lines or break the comment convention when re-imported.
    private static string SingleLine(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
