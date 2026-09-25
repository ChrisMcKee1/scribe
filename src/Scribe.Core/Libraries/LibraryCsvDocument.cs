namespace Scribe.Core.Libraries;

/// <summary>
/// A library CSV as the codec read it: the header metadata, the rows it could use, the rows it could not, and how the
/// bytes were decoded. Produced for a managed file in the libraries folder, a file the user imports, and a Recently
/// deleted entry.
/// </summary>
/// <param name="Name">The <c># name:</c> header, or null.</param>
/// <param name="Category">The <c># category:</c> header, or null.</param>
/// <param name="Description">The <c># description:</c> header, or null.</param>
/// <param name="BasedOn">The <c># based-on:</c> header (a duplicate's original id), or null.</param>
/// <param name="Terms">The usable rows in file order, values faithful (after the export codec's reversal, for an import that carries its marker).</param>
/// <param name="Errors">The rows that could not be used, in file order.</param>
/// <param name="Encoding">How the bytes were decoded.</param>
/// <param name="FormulaGuardVersion">The <c># formula-guard:</c> version the file declared, or null.</param>
/// <param name="Issues">File-level findings the preview reports.</param>
public sealed record LibraryCsvDocument(
    string? Name,
    string? Category,
    string? Description,
    string? BasedOn,
    IReadOnlyList<TermValues> Terms,
    IReadOnlyList<LibraryCsvRowError> Errors,
    LibraryTextEncoding Encoding,
    int? FormulaGuardVersion = null,
    LibraryCsvIssues Issues = LibraryCsvIssues.None);

/// <summary>
/// A row the codec could not use. <see cref="Field"/> is the offending value, for the import preview only; like every
/// term it is never logged.
/// </summary>
/// <param name="Line">The 1-based line the record starts on.</param>
/// <param name="Kind">Why the row was skipped.</param>
/// <param name="Field">The value that could not be read, when one field was at fault.</param>
public sealed record LibraryCsvRowError(int Line, LibraryCsvRowErrorKind Kind, string? Field = null);

/// <summary>Why a CSV row could not be used.</summary>
public enum LibraryCsvRowErrorKind
{
    /// <summary>Fewer than two fields: no replacement column at all (an empty replacement is a removal rule, not an error).</summary>
    MissingFields,

    /// <summary>The spoken form is empty.</summary>
    EmptySpoken,

    /// <summary>The whole_word column is not true or false.</summary>
    InvalidWholeWord,

    /// <summary>The enabled column is not true or false.</summary>
    InvalidEnabled,

    /// <summary>A quoted field has no closing quote.</summary>
    UnclosedQuote,

    /// <summary>A field is longer than the per-field cap.</summary>
    FieldTooLong,
}

/// <summary>File-level findings the import preview reports.</summary>
[Flags]
public enum LibraryCsvIssues
{
    None = 0,

    /// <summary>
    /// Metadata records held spreadsheet padding (unquoted trailing empty fields) that was dropped. Reported by imports
    /// only: a managed read takes metadata from its raw lines and never removes anything from them.
    /// </summary>
    HeaderPaddingRemoved = 1,

    /// <summary>More rows than the per-file cap; the rows past it were not read.</summary>
    RowLimitExceeded = 2,

    /// <summary>More bytes than the per-file cap; the file was not read.</summary>
    SizeLimitExceeded = 4,
}

/// <summary>How a CSV's bytes were decoded.</summary>
/// <param name="CodePage">The code page used: 65001 for UTF-8, 1200 and 1201 for UTF-16, or the ANSI code page of a fallback.</param>
/// <param name="ByteOrderMark">The bytes began with a byte order mark.</param>
/// <param name="AnsiFallback">The bytes were not valid UTF-8 and had no byte order mark, so the system ANSI code page was used.</param>
/// <param name="InvalidBytesReplaced">
/// Invalid bytes were replaced with U+FFFD, which only a managed file read the way 0.4.3 read it can report; the user
/// is told the file should be imported again.
/// </param>
public readonly record struct LibraryTextEncoding(int CodePage, bool ByteOrderMark, bool AnsiFallback, bool InvalidBytesReplaced);
