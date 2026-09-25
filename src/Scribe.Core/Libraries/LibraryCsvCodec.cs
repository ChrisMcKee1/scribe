using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Scribe.Core.Libraries;

/// <summary>
/// The library CSV codec (<see cref="ILibraryCsvCodec"/>; formats 6.4 and 6.8): managed files in the libraries folder,
/// read exactly as 0.4.3 read them unless they carry this version's format marker, and exports and imports, which carry
/// the reversible formula guard and survive a spreadsheet's padding and re-quoting. Pure: no I/O, no clock, no logging,
/// thread-safe, and a read never throws on content.
/// </summary>
/// <remarks>
/// <para>
/// Everything rests on the one record reader and field writer, <see cref="LibraryCsvRecords"/>, which the personal
/// dictionary's <see cref="PostProcessing.DictionaryCsv"/> and <see cref="PostProcessing.DictionaryLibraryCsv"/> share
/// (pattern P-2). Rows are read by one of two rule sets:
/// </para>
/// <list type="bullet">
/// <item>0.4.3's, for a managed file without <c># scribe-format: 2</c> in its leading comments: a record whose trimmed first
/// field is <c>pattern</c> is skipped wherever it appears, so is one whose first field starts with <c>#</c> after white
/// space, quoted or not, and every field is trimmed, quoted or not. Nothing dictation applies changes on upgrade.</item>
/// <item>The strict rules (plan 3.10, Astra N3), for a managed file with the marker and for every import: a comment only
/// on an unquoted raw line, the header only as the first data record and only by its shape, only unquoted fields
/// trimmed, and a record of nothing but unquoted blank fields (a spreadsheet's empty row) skipped. So every value this
/// version writes reads back exactly.</item>
/// </list>
/// <para>
/// Metadata has two encodings (review finding A11) and they are never confused. A managed file keeps 0.4.3's raw
/// comment lines, read from the raw text (first line wins, trimmed) and never stripped of anything, so a trailing comma
/// reads back as written. An export writes each metadata line as one CSV field, quoted when it holds a comma, a quote or
/// a line break, and an import rejoins a metadata record's fields and drops a spreadsheet's padding.
/// </para>
/// </remarks>
public sealed class LibraryCsvCodec : ILibraryCsvCodec
{
    private const string NewLine = "\r\n";
    private const string FormatVersion = "2";
    private const int WesternAnsiCodePage = 1252;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly string[] HeaderColumns = ["pattern", "replacement", "whole_word", "enabled"];

    private readonly Encoding _ansi;
    private readonly Encoding _ansiStrict;

    internal LibraryCsvCodec(int ansiCodePage)
    {
        _ansi = AnsiEncoding(ansiCodePage, new DecoderReplacementFallback("\uFFFD"));
        _ansiStrict = AnsiEncoding(ansiCodePage, DecoderFallback.ExceptionFallback);
    }

    /// <summary>The codec with the system ANSI code page as the import fallback, the one a spreadsheet's plain CSV save uses.</summary>
    public static LibraryCsvCodec Instance { get; } = new(SystemAnsiCodePage());

    /// <summary>The code page an import falls back to for bytes that are not UTF-8 and carry no byte order mark.</summary>
    internal int AnsiCodePage => _ansi.CodePage;

    /// <inheritdoc/>
    public LibraryCsvDocument ReadManaged(ReadOnlySpan<byte> bytes)
    {
        var (text, encoding) = DecodeManaged(bytes);
        var header = ReadRawHeader(text);
        var (terms, errors) = string.Equals(header.Format, FormatVersion, StringComparison.Ordinal)
            ? ReadStrictManagedRows(text)
            : ReadLegacyRows(text);
        return new LibraryCsvDocument(
            NullIfBlank(header.Name), NullIfBlank(header.Category), NullIfBlank(header.Description), NullIfBlank(header.BasedOn),
            terms.AsReadOnly(), errors.AsReadOnly(), encoding);
    }

    /// <inheritdoc/>
    public byte[] WriteManaged(LibraryContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var metadata = HeaderToWrite.Of(content, nameof(content));
        if (!LibraryMetadata.ReadsBackInOlderVersions(metadata.Name, metadata.Category, metadata.Description, metadata.BasedOn))
        {
            // 0.4.3's reader would stay inside a quoted field when the rows begin and find none of them (A11); the editor
            // never commits such a header, so this is a bug upstream, stopped before it writes a file 0.4.3 misreads.
            throw new ArgumentException(
                "The library's name, category, description and based-on id hold double quotes that do not pair.", nameof(content));
        }

        var text = new StringBuilder();
        text.Append("# name: ").Append(metadata.Name).Append(NewLine);
        text.Append("# category: ").Append(metadata.Category).Append(NewLine);
        if (metadata.Description is not null)
        {
            text.Append("# description: ").Append(metadata.Description).Append(NewLine);
        }

        if (metadata.BasedOn is not null)
        {
            text.Append("# based-on: ").Append(metadata.BasedOn).Append(NewLine);
        }

        text.Append("# scribe-format: ").Append(FormatVersion).Append(NewLine);
        AppendRows(text, content, guard: false);
        return Utf8WithoutBom.GetBytes(text.ToString());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A file larger than <see cref="LibraryLimits.MaxImportBytes"/> is not decoded at all
    /// (<see cref="LibraryCsvIssues.SizeLimitExceeded"/>); its <see cref="LibraryCsvDocument.Encoding"/> says only what a
    /// byte order mark declares. Rows past <see cref="LibraryLimits.MaxTermsPerLibrary"/> data records are not read
    /// (<see cref="LibraryCsvIssues.RowLimitExceeded"/>), and a value longer than <see cref="LibraryLimits.MaxFieldLength"/>
    /// after the guard is reversed makes its row a <see cref="LibraryCsvRowErrorKind.FieldTooLong"/> error. A byte order
    /// mark is honoured even when the bytes after it are broken, which are then replaced and reported
    /// (<see cref="LibraryTextEncoding.InvalidBytesReplaced"/>).
    /// </remarks>
    public LibraryCsvDocument ReadImport(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > LibraryLimits.MaxImportBytes)
        {
            var declared = DecodeManaged(bytes[..4]).Encoding with { InvalidBytesReplaced = false };
            return new LibraryCsvDocument(null, null, null, null, [], [], declared, Issues: LibraryCsvIssues.SizeLimitExceeded);
        }

        var (text, encoding) = DecodeImport(bytes);
        return ReadImportText(text, encoding);
    }

    /// <inheritdoc/>
    public byte[] WriteExport(LibraryContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var metadata = HeaderToWrite.Of(content, nameof(content));

        var text = new StringBuilder();
        AppendMetadataLine(text, "# name: " + metadata.Name);
        AppendMetadataLine(text, "# category: " + metadata.Category);
        if (metadata.Description is not null)
        {
            AppendMetadataLine(text, "# description: " + metadata.Description);
        }

        if (metadata.BasedOn is not null)
        {
            AppendMetadataLine(text, "# based-on: " + metadata.BasedOn);
        }

        text.Append("# formula-guard: ").Append(LibraryFormulaGuard.Version.ToString(CultureInfo.InvariantCulture)).Append(NewLine);
        AppendRows(text, content, guard: true);
        return [.. Utf8Bom, .. Utf8WithoutBom.GetBytes(text.ToString())];
    }

    /// <summary>
    /// A library CSV read by 0.4.3's rules, metadata and rows: what <see cref="PostProcessing.DictionaryLibraryCsv.Parse"/>
    /// returns, and what <see cref="ReadManaged"/> reads from a file without the format marker.
    /// </summary>
    internal static LegacyLibraryCsv ReadLegacy(string? text)
    {
        var header = ReadRawHeader(text);
        var (terms, errors) = ReadLegacyRows(text);
        return new LegacyLibraryCsv(
            NullIfBlank(header.Name), NullIfBlank(header.Category), NullIfBlank(header.Description), terms.AsReadOnly(), errors.AsReadOnly());
    }

    /// <summary>
    /// Rows read by 0.4.3's rules (<c>DictionaryCsv.Parse</c>): blank records, records whose first field starts with
    /// <c>#</c> after white space and records whose trimmed first field is <c>pattern</c> are skipped, quoted or not;
    /// every field is trimmed, quoted or not; the flags default to true.
    /// </summary>
    internal static (List<TermValues> Terms, List<LibraryCsvRowError> Errors) ReadLegacyRows(string? text)
    {
        var terms = new List<TermValues>();
        var errors = new List<LibraryCsvRowError>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (terms, errors);
        }

        foreach (var record in LibraryCsvRecords.Read(text, errors))
        {
            // A record always has a field, so 0.4.3's "no fields" case is the single blank field.
            var fields = record.Fields;
            if ((fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0].Text)) ||
                fields[0].Text.TrimStart().StartsWith('#') ||
                string.Equals(fields[0].Text.Trim(), "pattern", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Count < 2)
            {
                errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.MissingFields));
                continue;
            }

            var spoken = fields[0].Text.Trim();
            if (spoken.Length == 0)
            {
                errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.EmptySpoken));
                continue;
            }

            if (!TryParseFlag(fields.Count > 2 ? fields[2].Text : null, out var wholeWord))
            {
                errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.InvalidWholeWord, fields[2].Text.Trim()));
                continue;
            }

            if (!TryParseFlag(fields.Count > 3 ? fields[3].Text : null, out var enabled))
            {
                errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.InvalidEnabled, fields[3].Text.Trim()));
                continue;
            }

            terms.Add(new TermValues(spoken, fields[1].Text.Trim(), wholeWord, enabled));
        }

        return (terms, errors);
    }

    /// <summary>
    /// A managed file's bytes decoded exactly as <c>File.ReadAllText</c> decoded them for 0.4.3 (it is this reader over
    /// the file): a byte order mark decides between UTF-8, UTF-16 and UTF-32, UTF-8 otherwise, and a byte that is not
    /// valid becomes U+FFFD, which the encoding reports.
    /// </summary>
    internal static (string Text, LibraryTextEncoding Encoding) DecodeManaged(ReadOnlySpan<byte> bytes)
    {
        string text;
        Encoding detected;
        using (var reader = new StreamReader(
                   new MemoryStream(bytes.ToArray(), writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
            detected = reader.CurrentEncoding;
        }

        var markLength = detected.CodePage switch
        {
            65001 => bytes.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0,
            1200 or 1201 => 2,
            12000 or 12001 => 4,
            _ => 0,
        };
        var valid = detected.CodePage == 65001
            ? Utf8.IsValid(bytes[markLength..])
            : DecodesWithoutReplacement(
                Encoding.GetEncoding(detected.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                bytes[markLength..]);
        return (text, new LibraryTextEncoding(detected.CodePage, markLength > 0, AnsiFallback: false, InvalidBytesReplaced: !valid));
    }

    /// <summary>
    /// An import's bytes decoded strictly (plan 3.10, Opus N1): a byte order mark is honoured, bytes that are valid UTF-8
    /// are UTF-8, and anything else decodes with the ANSI code page, which is what a spreadsheet's plain CSV save writes.
    /// </summary>
    internal (string Text, LibraryTextEncoding Encoding) DecodeImport(ReadOnlySpan<byte> bytes)
    {
        var decoded = DecodeManaged(bytes);
        if (decoded.Encoding.ByteOrderMark || !decoded.Encoding.InvalidBytesReplaced)
        {
            return decoded;
        }

        return (_ansi.GetString(bytes), new LibraryTextEncoding(
            _ansi.CodePage, ByteOrderMark: false, AnsiFallback: true, InvalidBytesReplaced: !DecodesWithoutReplacement(_ansiStrict, bytes)));
    }

    private static LibraryCsvDocument ReadImportText(string text, LibraryTextEncoding encoding)
    {
        var header = new HeaderValues();
        var terms = new List<TermValues>();
        var errors = new List<LibraryCsvRowError>();
        var issues = LibraryCsvIssues.None;
        var beforeData = true;
        var reverseGuard = false;
        var dataRecords = 0;

        foreach (var record in LibraryCsvRecords.Read(text, errors))
        {
            if (IsBlank(record))
            {
                continue;
            }

            if (beforeData)
            {
                // Before the first data record, a record whose first field starts with "#" is metadata or a comment,
                // quoted or not: an export quotes a metadata line that holds a comma or a quote.
                if (record.Fields[0].Text.TrimStart().StartsWith('#'))
                {
                    if (TakeMetadataRecord(header, record))
                    {
                        issues |= LibraryCsvIssues.HeaderPaddingRemoved;
                    }

                    continue;
                }

                beforeData = false;
                reverseGuard = ParseVersion(header.FormulaGuard) == LibraryFormulaGuard.Version;
                if (IsHeader(record))
                {
                    continue;
                }
            }
            else if (record.RawComment)
            {
                continue;
            }

            if (dataRecords == LibraryLimits.MaxTermsPerLibrary)
            {
                issues |= LibraryCsvIssues.RowLimitExceeded;
                break;
            }

            dataRecords++;
            ReadStrictRow(record, reverseGuard, capFields: true, terms, errors);
        }

        return new LibraryCsvDocument(
            NullIfBlank(header.Name), NullIfBlank(header.Category), NullIfBlank(header.Description), NullIfBlank(header.BasedOn),
            terms.AsReadOnly(), errors.AsReadOnly(), encoding, ParseVersion(header.FormulaGuard), issues);
    }

    private static (List<TermValues> Terms, List<LibraryCsvRowError> Errors) ReadStrictManagedRows(string text)
    {
        var terms = new List<TermValues>();
        var errors = new List<LibraryCsvRowError>();
        var firstDataRecord = true;
        foreach (var record in LibraryCsvRecords.Read(text, errors))
        {
            // Metadata lines are raw comment lines, so they are skipped here like any other comment; their values come
            // from the raw text (ReadRawHeader), never from these records.
            if (record.RawComment || IsBlank(record))
            {
                continue;
            }

            if (firstDataRecord)
            {
                firstDataRecord = false;
                if (IsHeader(record))
                {
                    continue;
                }
            }

            ReadStrictRow(record, reverseGuard: false, capFields: false, terms, errors);
        }

        return (terms, errors);
    }

    private static void ReadStrictRow(
        CsvRecord record, bool reverseGuard, bool capFields, List<TermValues> terms, List<LibraryCsvRowError> errors)
    {
        var fields = record.Fields;
        if (fields.Count < 2)
        {
            errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.MissingFields));
            return;
        }

        var spoken = StrictValue(fields[0]);
        var written = StrictValue(fields[1]);
        if (reverseGuard)
        {
            spoken = LibraryFormulaGuard.Decode(spoken);
            written = LibraryFormulaGuard.Decode(written);
        }

        // A spoken form of nothing but white space has the empty key and can never match, so it is as empty as "".
        if (string.IsNullOrWhiteSpace(spoken))
        {
            errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.EmptySpoken));
            return;
        }

        if (capFields && (spoken.Length > LibraryLimits.MaxFieldLength || written.Length > LibraryLimits.MaxFieldLength))
        {
            var tooLong = spoken.Length > LibraryLimits.MaxFieldLength ? spoken : written;
            errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.FieldTooLong, tooLong));
            return;
        }

        if (!TryParseFlag(fields.Count > 2 ? fields[2].Text : null, out var wholeWord))
        {
            errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.InvalidWholeWord, StrictValue(fields[2])));
            return;
        }

        if (!TryParseFlag(fields.Count > 3 ? fields[3].Text : null, out var enabled))
        {
            errors.Add(new LibraryCsvRowError(record.Line, LibraryCsvRowErrorKind.InvalidEnabled, StrictValue(fields[3])));
            return;
        }

        terms.Add(new TermValues(spoken, written, wholeWord, enabled));
    }

    // Only an unquoted field is trimmed: every edge white space this version writes is inside quotes.
    private static string StrictValue(CsvField field) => field.Quoted ? field.Text : field.Text.Trim();

    // Nothing but unquoted blank fields: a blank line, or a spreadsheet's empty row (",,,").
    private static bool IsBlank(CsvRecord record)
    {
        foreach (var field in record.Fields)
        {
            if (field.Quoted || !string.IsNullOrWhiteSpace(field.Text))
            {
                return false;
            }
        }

        return true;
    }

    // "pattern" and then a prefix of "replacement", "whole_word", "enabled", at least two columns, compared trimmed and
    // without case, with a spreadsheet's trailing empty fields ignored. A quoted two-column record could be a real row
    // ("pattern" written "replacement"), so it must be unquoted; with a third column it cannot be one, because
    // "whole_word" is not a flag, so a spreadsheet that quotes every text cell keeps its header.
    private static bool IsHeader(CsvRecord record)
    {
        var fields = record.Fields;
        var count = fields.Count;
        while (count > 0 && !fields[count - 1].Quoted && fields[count - 1].Text.Length == 0)
        {
            count--;
        }

        if (count < 2 || count > HeaderColumns.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(fields[i].Text.Trim(), HeaderColumns[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return count > 2 || (!fields[0].Quoted && !fields[1].Quoted);
    }

    // A metadata record's line is its fields rejoined with commas, less the trailing unquoted empty fields a spreadsheet
    // pads a line with. One rule covers both shapes an import meets: an export writes each metadata line as one field
    // (quoted when it holds a comma or a quote), so whatever a spreadsheet adds after it is padding and the join is that
    // field; a raw line (0.4.3's exports) that a spreadsheet split at its commas comes back whole, and so does a raw line
    // read directly, apart from any double quote in it, which a CSV reader consumes. Returns whether padding was dropped
    // from a line that supplied a value, which the preview reports.
    private static bool TakeMetadataRecord(HeaderValues header, CsvRecord record)
    {
        var fields = record.Fields;
        var count = fields.Count;
        while (count > 1 && !fields[count - 1].Quoted && fields[count - 1].Text.Length == 0)
        {
            count--;
        }

        var line = count == 1 ? fields[0].Text : string.Join(',', fields.Take(count).Select(field => field.Text));
        return header.Take(line.TrimStart()) && count < fields.Count;
    }

    // 0.4.3's leading comment block (DictionaryLibraryCsv.Parse): comment lines and blank lines up to the first other line.
    private static HeaderValues ReadRawHeader(string? text)
    {
        var header = new HeaderValues();
        if (string.IsNullOrEmpty(text))
        {
            return header;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                header.Take(trimmed);
                continue;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            break;
        }

        return header;
    }

    private static void AppendRows(StringBuilder text, LibraryContent content, bool guard)
    {
        text.Append(LibraryCsvRecords.Header).Append(NewLine);
        foreach (var row in content.Rows ?? [])
        {
            var values = row?.Values ?? throw new ArgumentException("A library row has no values.", nameof(content));
            var spoken = values.Spoken ?? string.Empty;
            var written = values.Written ?? string.Empty;
            if (guard)
            {
                spoken = LibraryFormulaGuard.Encode(spoken);
                written = LibraryFormulaGuard.Encode(written);
            }

            LibraryCsvRecords.AppendRow(text, spoken, written, values.WholeWord, values.Enabled);
            text.Append(NewLine);
        }
    }

    private static void AppendMetadataLine(StringBuilder text, string line)
    {
        LibraryCsvRecords.AppendMetadataField(text, line);
        text.Append(NewLine);
    }

    private static bool TryParseFlag(string? field, out bool value)
    {
        value = true;
        if (string.IsNullOrWhiteSpace(field))
        {
            return true; // optional column
        }

        switch (field.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1":
                value = true;
                return true;
            case "false" or "no" or "0":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static int? ParseVersion(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool DecodesWithoutReplacement(Encoding strict, ReadOnlySpan<byte> bytes)
    {
        try
        {
            strict.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static Encoding AnsiEncoding(int codePage, DecoderFallback fallback) =>
        CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ReplacementFallback, fallback)
        ?? Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, fallback);

    // GetACP through the provider; Windows-1252 when the system code page is one the provider has no table for (UTF-8).
    private static int SystemAnsiCodePage() =>
        CodePagesEncodingProvider.Instance.GetEncoding(0)?.CodePage ?? WesternAnsiCodePage;

    /// <summary>The metadata a write puts in the header, with a description or based-on id only when there is one.</summary>
    private sealed record HeaderToWrite(string Name, string Category, string? Description, string? BasedOn)
    {
        public static HeaderToWrite Of(LibraryContent content, string paramName)
        {
            var header = new HeaderToWrite(
                content.Name ?? string.Empty,
                content.Category ?? string.Empty,
                string.IsNullOrWhiteSpace(content.Description) ? null : content.Description,
                string.IsNullOrWhiteSpace(content.BasedOn) ? null : content.BasedOn);
            foreach (var value in new[] { header.Name, header.Category, header.Description, header.BasedOn })
            {
                // One raw line per value is what 0.4.3 reads; the editor commits every value in that form.
                if (value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                {
                    throw new ArgumentException(
                        "A library's name, category, description and based-on id must each be one line.", paramName);
                }
            }

            return header;
        }
    }

    /// <summary>
    /// The header values found so far, each by 0.4.3's rule (<c>TryReadMeta</c>): <c># key: value</c> with the key
    /// compared without case after any number of leading <c>#</c> and white space, the value trimmed, and the first line
    /// for a key winning even when its value is empty.
    /// </summary>
    private sealed class HeaderValues
    {
        public string? Name;
        public string? Category;
        public string? Description;
        public string? BasedOn;
        public string? Format;
        public string? FormulaGuard;

        /// <summary>Takes <paramref name="commentLine"/> (starting with <c>#</c>) and says whether it supplied a value.</summary>
        public bool Take(string commentLine)
        {
            var body = commentLine.TrimStart('#').TrimStart();
            return TryTake(body, "name", ref Name) ||
                   TryTake(body, "category", ref Category) ||
                   TryTake(body, "description", ref Description) ||
                   TryTake(body, "based-on", ref BasedOn) ||
                   TryTake(body, "scribe-format", ref Format) ||
                   TryTake(body, "formula-guard", ref FormulaGuard);
        }

        private static bool TryTake(string body, string key, ref string? value)
        {
            if (value is not null ||
                body.Length <= key.Length ||
                !body.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                body[key.Length] != ':')
            {
                return false;
            }

            value = body[(key.Length + 1)..].Trim();
            return true;
        }
    }
}

/// <summary>A library CSV read by 0.4.3's rules: its header metadata, the rows it could use and the rows it could not.</summary>
internal sealed record LegacyLibraryCsv(
    string? Name,
    string? Category,
    string? Description,
    IReadOnlyList<TermValues> Terms,
    IReadOnlyList<LibraryCsvRowError> Errors);
