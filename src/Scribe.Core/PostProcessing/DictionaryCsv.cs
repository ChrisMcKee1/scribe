using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// CSV round-tripping for the user dictionary, so vocabularies can be built in a spreadsheet and
/// shared between people instead of typed row by row in Settings. Format is RFC 4180-style:
/// <c>pattern,replacement,whole_word,enabled</c> with a header row, quoted fields when needed,
/// and <c>#</c> comment lines (which double as instructions in the downloadable template).
/// </summary>
/// <remarks>
/// A thin wrapper over the library CSV codec's shared record reader and field writer (pattern P-2): rows are read by
/// 0.4.3's rules exactly as before (<see cref="LibraryCsvCodec.ReadLegacyRows"/>), and rows are written by the one field
/// writer, which also quotes a first field that starts with <c>#</c> or reads as the header and a value with white space
/// at an edge, so a strict reader takes them as data; this reader skips and trims those exactly as it always did.
/// </remarks>
public static class DictionaryCsv
{
    public const string Header = LibraryCsvRecords.Header;

    /// <summary>
    /// The starter file behind the "Get template" button. Comment lines explain the columns so the
    /// file is self-documenting when it opens in a spreadsheet or editor.
    /// </summary>
    public const string Template =
        """
        # Scribe dictionary template
        #
        # One row per substitution: what the transcriber usually hears, and what you
        # want written instead. Fill it in, then use Import in Scribe's Dictionary
        # settings. Lines starting with # are ignored.
        #
        # pattern     - the spoken word or phrase as it gets transcribed (required)
        # replacement - what to write instead (required)
        # whole_word  - true to match on word boundaries only, false for phrase
        #               replacement anywhere (optional, default true)
        # enabled     - false to keep the row but switch it off (optional, default true)
        #
        pattern,replacement,whole_word,enabled
        azure,Azure,true,true
        cube flow,Kubeflow,true,true
        kay eight ess,K8s,true,true
        """;

    /// <summary>Renders entries as a CSV document (header included), ready to save or share.</summary>
    public static string Export(IEnumerable<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var entry in entries)
        {
            LibraryCsvRecords.AppendRow(sb, entry.Pattern, entry.Replacement, entry.WholeWord, entry.Enabled);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a dictionary CSV. Never throws on content: rows that can't be understood are
    /// reported in <see cref="DictionaryCsvResult.Errors"/> with their line number while the good
    /// rows still import, so one typo in a shared 300-term file doesn't reject the other 299.
    /// </summary>
    public static DictionaryCsvResult Parse(string? csv)
    {
        var (terms, errors) = LibraryCsvCodec.ReadLegacyRows(csv);
        return new DictionaryCsvResult(terms.Select(term => term.ToEntry()).ToList(), errors.Select(Describe).ToList());
    }

    /// <summary>
    /// The message a row error has always been reported with, which the import dialogs show. It quotes an invalid flag
    /// value, which is the user's own text, so it belongs on screen, never in a log.
    /// </summary>
    internal static string Describe(LibraryCsvRowError error) => error.Kind switch
    {
        LibraryCsvRowErrorKind.MissingFields => $"Line {error.Line}: expected at least a pattern and a replacement.",
        LibraryCsvRowErrorKind.EmptySpoken => $"Line {error.Line}: the pattern (spoken form) is empty.",
        LibraryCsvRowErrorKind.InvalidWholeWord => $"Line {error.Line}: whole_word should be true or false, not \"{error.Field}\".",
        LibraryCsvRowErrorKind.InvalidEnabled => $"Line {error.Line}: enabled should be true or false, not \"{error.Field}\".",
        LibraryCsvRowErrorKind.UnclosedQuote => $"Line {error.Line}: quoted field is missing its closing quote.",
        LibraryCsvRowErrorKind.FieldTooLong => $"Line {error.Line}: a value is longer than {LibraryLimits.MaxFieldLength} characters.",
        _ => $"Line {error.Line}: the row could not be read.",
    };
}

/// <summary>Outcome of parsing a dictionary CSV: the usable entries plus per-line errors.</summary>
public sealed record DictionaryCsvResult(
    IReadOnlyList<DictionaryEntry> Entries,
    IReadOnlyList<string> Errors);
