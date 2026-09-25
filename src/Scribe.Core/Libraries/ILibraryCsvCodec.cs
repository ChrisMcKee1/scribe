namespace Scribe.Core.Libraries;

/// <summary>
/// The library CSV codec: how library files and exchanged CSVs are read and written. Implemented once, by the CSV
/// stream, and shared with the personal dictionary's CSV (pattern P-2): one parser, one writer.
/// </summary>
/// <remarks>
/// <para>
/// Pure and thread-safe: no I/O, no clock, no logging, never throws on content it reads (unusable rows become
/// <see cref="LibraryCsvDocument.Errors"/>). Only a null argument throws, and a write handed content the editor would
/// have refused (see <see cref="WriteManaged"/>), a string that is not well-formed UTF-16 included: the writers encode
/// strictly, so no unpaired surrogate is ever written as U+FFFD.
/// </para>
/// <para>
/// Metadata has two encodings (review finding A11). A managed file stores it as raw comment lines, 0.4.3's form, which
/// every older build reads, and a managed read takes each value from its raw line and never removes spreadsheet padding,
/// so a value ending in a comma reads back as written. An export stores each metadata line as one CSV field, quoted
/// when it holds a comma, a quote or a line break, so a spreadsheet that pads or re-quotes the line cannot change the
/// value; an import recognizes a metadata record, quoted or not, only before the column header, where unquoted trailing
/// empty fields are padding (reported) and a comma between non-empty fields is part of the value.
/// </para>
/// </remarks>
public interface ILibraryCsvCodec
{
    /// <summary>
    /// A managed file in the libraries folder. A file without the <c># scribe-format: 2</c> marker is read exactly as
    /// 0.4.3 read it (a byte order mark decides the encoding, UTF-8 otherwise, an invalid byte becomes U+FFFD and is
    /// reported; 0.4.3's header, comment and trimming rules), so the rows dictation uses never change on upgrade; a
    /// file with the marker gets the strict header, comment and quoting rules for its rows. Either way metadata comes
    /// from the raw comment lines, first line wins, trimmed, with no padding recovery. A header-only file is an empty
    /// library; a file with row errors is partly readable (<see cref="LibraryFileState.PartlyReadable"/>).
    /// </summary>
    LibraryCsvDocument ReadManaged(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// A managed file for <paramref name="content"/>: UTF-8 without a byte order mark, the <c># name:</c>,
    /// <c># category:</c>, optional <c># description:</c> and <c># based-on:</c> header as raw comment lines 0.4.3
    /// reads, the <c># scribe-format: 2</c> marker, then <c>pattern,replacement,whole_word,enabled</c> rows in saved
    /// order with values faithful (no formula guard). <see cref="ReadManaged"/> of the result gives back the same
    /// metadata and rows, and so does 0.4.3's reader apart from the rows the strict writer quotes on purpose. Throws
    /// <see cref="ArgumentException"/> for metadata the editor refuses to commit: a value holding a line break, or a
    /// header whose double quotes do not pair (<see cref="LibraryMetadata.ReadsBackInOlderVersions"/>); and for any
    /// value or metadata string that is not well-formed UTF-16, which it never writes as U+FFFD.
    /// </summary>
    byte[] WriteManaged(LibraryContent content);

    /// <summary>
    /// A file the user chose to import: strict UTF-8, honouring a byte order mark; bytes that are not valid UTF-8 and
    /// carry no byte order mark decode with the system ANSI code page and say so. A file that declares the formula
    /// guard has it reversed; metadata is recovered from spreadsheet padding and re-quoting and reported.
    /// </summary>
    LibraryCsvDocument ReadImport(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// An export of <paramref name="content"/>: UTF-8 with a byte order mark, the metadata as quoted-when-needed CSV
    /// fields plus <c># formula-guard: 1</c>, and the reversible formula guard applied to every value.
    /// <see cref="ReadImport"/> of the result gives back every value and all metadata exactly, a literal leading
    /// apostrophe and a trailing comma included; after a spreadsheet opened and saved it, the metadata still reads back
    /// unchanged. Like <see cref="WriteManaged"/>, throws <see cref="ArgumentException"/> for a string that is not
    /// well-formed UTF-16.
    /// </summary>
    byte[] WriteExport(LibraryContent content);
}
