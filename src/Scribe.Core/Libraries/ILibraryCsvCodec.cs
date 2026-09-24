namespace Scribe.Core.Libraries;

/// <summary>
/// The library CSV codec: how library files and exchanged CSVs are read and written. Implemented once, by the CSV
/// stream, and shared with the personal dictionary's CSV (pattern P-2): one parser, one writer.
/// </summary>
/// <remarks>
/// Pure and thread-safe: no I/O, no clock, no logging, never throws on content (unusable rows become
/// <see cref="LibraryCsvDocument.Errors"/>). Only a null argument throws.
/// </remarks>
public interface ILibraryCsvCodec
{
    /// <summary>
    /// A managed file in the libraries folder. A file without the <c># scribe-format: 2</c> marker is read exactly as
    /// 0.4.3 read it (a byte order mark decides the encoding, UTF-8 otherwise, an invalid byte becomes U+FFFD and is
    /// reported; 0.4.3's header, comment and trimming rules), so the rows dictation uses never change on upgrade; a
    /// file with the marker gets the strict header, comment and quoting rules. A header-only file is an empty library.
    /// </summary>
    LibraryCsvDocument ReadManaged(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// A managed file for <paramref name="content"/>: UTF-8 without a byte order mark, the <c># name:</c>,
    /// <c># category:</c>, optional <c># description:</c> and <c># based-on:</c> header as raw comment lines 0.4.3
    /// reads, the <c># scribe-format: 2</c> marker, then <c>pattern,replacement,whole_word,enabled</c> rows in saved
    /// order with values faithful (no formula guard). <see cref="ReadManaged"/> of the result gives back the same
    /// metadata and rows.
    /// </summary>
    byte[] WriteManaged(LibraryContent content);

    /// <summary>
    /// A file the user chose to import: strict UTF-8, honouring a byte order mark; bytes that are not valid UTF-8 and
    /// carry no byte order mark decode with the system ANSI code page and say so. A file that declares the formula
    /// guard has it reversed; header lines are recovered from spreadsheet padding and reported.
    /// </summary>
    LibraryCsvDocument ReadImport(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// An export of <paramref name="content"/>: UTF-8 with a byte order mark, the header plus <c># formula-guard: 1</c>,
    /// and the reversible formula guard applied to every value. <see cref="ReadImport"/> of the result gives back every
    /// value exactly, a literal leading apostrophe included.
    /// </summary>
    byte[] WriteExport(LibraryContent content);
}
