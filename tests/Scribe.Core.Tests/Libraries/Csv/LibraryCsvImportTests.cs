using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// Exports and imports (formats 6.8 and plan 3.10): an export read back by an import is the identity for every value and
/// all metadata (X-1); an import decodes strict UTF-8, honours a byte order mark and falls back to the ANSI code page
/// (X-4); metadata is recovered from spreadsheet padding and re-quoting, and the header is found only by its shape (X-5);
/// the writer quotes what the strict reader would otherwise misread (X-6); and imports stop at the limits (X-7).
/// </summary>
public sealed class LibraryCsvImportTests
{
    private const string TriggerHeavy = "''  =+-@\uFF1D\uFF0B\uFF0D\uFF20\t\r\nab,\"#";

    private static readonly LibraryCsvCodec Codec = CsvTestData.Codec;

    [Fact]
    public void Export_then_import_is_the_identity_for_every_value()
    {
        // Seeded property test: trigger-leading values, literal leading apostrophes (one and many), quotes and separators,
        // white space and control prefixes, full-width forms, "#" and header-shaped spoken forms, and metadata holding
        // commas, a trailing comma, quotes (paired or not) and "#".
        var random = new Random(20260929);
        for (var i = 0; i < 4000; i++)
        {
            var content = RandomExportContent(random);

            var bytes = Codec.WriteExport(content);
            var read = Codec.ReadImport(bytes);

            Assert.True(content.Name == read.Name, $"case {i}: name");
            Assert.True(content.Category == read.Category, $"case {i}: category");
            Assert.True(content.Description == read.Description, $"case {i}: description");
            Assert.True(content.BasedOn == read.BasedOn, $"case {i}: based-on");
            Assert.True(content.Rows.Select(r => r.Values).SequenceEqual(read.Terms), $"case {i}: rows");
            Assert.Empty(read.Errors);
            Assert.Equal(1, read.FormulaGuardVersion);
            Assert.Equal(LibraryCsvIssues.None, read.Issues);
            Assert.Equal(new LibraryTextEncoding(65001, true, false, false), read.Encoding);
        }
    }

    [Fact]
    public void An_export_survives_a_spreadsheet_save_apart_from_quotes_the_spreadsheet_drops()
    {
        // Seeded: every metadata value and every row survive a spreadsheet's open and save, except what a spreadsheet
        // changes by dropping quotes it does not need: white space at a value's edges (read trimmed) and a spoken form
        // starting with "#", which then reads as a comment (decision 21 keeps "#" out of the guard; see the report).
        var random = new Random(20260930);
        var lost = 0;
        for (var i = 0; i < 3000; i++)
        {
            var content = RandomExportContent(random);
            var saved = SpreadsheetRoundTrip(Codec.WriteExport(content));

            var read = Codec.ReadImport(saved);

            var expected = content.Rows.Select(r => r.Values)
                .Where(v => !LostToSpreadsheet(v.Spoken))
                .Select(v => v with { Spoken = AfterSpreadsheet(v.Spoken), Written = AfterSpreadsheet(v.Written) })
                .ToList();
            lost += content.Rows.Count - expected.Count;
            Assert.True(content.Name == read.Name, $"case {i}: name");
            Assert.True(content.Category == read.Category, $"case {i}: category");
            Assert.True(content.Description == read.Description, $"case {i}: description");
            Assert.True(content.BasedOn == read.BasedOn, $"case {i}: based-on");
            Assert.True(expected.SequenceEqual(read.Terms), $"case {i}: rows\n{CsvTestData.Show(expected)}\n{CsvTestData.Show(read.Terms)}");
            Assert.Empty(read.Errors);
            Assert.Equal(1, read.FormulaGuardVersion);
        }

        Assert.True(lost > 0, "the generator reaches spoken forms starting with #");
    }

    [Fact]
    public void Every_export_declares_the_guard_and_an_import_reverses_it_only_when_declared()
    {
        var content = CsvTestData.Content("Guarded", "Custom", null, [new("sum", "=SUM(A1)"), new("quote", "'=x")]);
        var text = CsvTestData.Text(Codec.WriteExport(content)).TrimStart('\uFEFF');

        var undeclared = Codec.ReadImport(CsvTestData.Utf8(text.Replace("# formula-guard: 1\r\n", string.Empty, StringComparison.Ordinal)));
        var newer = Codec.ReadImport(CsvTestData.Utf8(text.Replace("# formula-guard: 1", "# formula-guard: 2", StringComparison.Ordinal)));
        var late = Codec.ReadImport(CsvTestData.Utf8(
            text.Replace("# formula-guard: 1\r\n", string.Empty, StringComparison.Ordinal) + "# formula-guard: 1\r\n"));

        Assert.Contains("\r\n# formula-guard: 1\r\npattern,replacement,whole_word,enabled\r\n", text, StringComparison.Ordinal);
        Assert.Null(undeclared.FormulaGuardVersion);
        Assert.Equal(["'=SUM(A1)", "''=x"], undeclared.Terms.Select(t => t.Written));
        Assert.Equal(2, newer.FormulaGuardVersion);
        Assert.Equal(["'=SUM(A1)", "''=x"], newer.Terms.Select(t => t.Written));
        Assert.Null(late.FormulaGuardVersion);
    }

    [Fact]
    public void An_export_has_the_documented_shape()
    {
        var content = CsvTestData.Content(
            "Team, terms,", "Custom", "Say \"hi\"", [new("=x", "@y"), new("#tag", "#tag"), new("pattern", "a,b")], "github");

        var bytes = Codec.WriteExport(content);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(
            "\"# name: Team, terms,\"\r\n# category: Custom\r\n\"# description: Say \"\"hi\"\"\"\r\n# based-on: github\r\n" +
            "# formula-guard: 1\r\npattern,replacement,whole_word,enabled\r\n'=x,'@y,true,true\r\n\"#tag\",#tag,true,true\r\n" +
            "\"pattern\",\"a,b\",true,true\r\n",
            Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public void WriteExport_refuses_a_line_break_in_metadata_but_quotes_an_unpaired_quote()
    {
        TermValues[] rows = [new("x", "X")];

        Assert.Throws<ArgumentNullException>(() => Codec.WriteExport(null!));
        Assert.Throws<ArgumentException>(() => Codec.WriteExport(CsvTestData.Content("a\nb", "c", null, rows)));
        Assert.Throws<ArgumentException>(() => Codec.WriteExport(CsvTestData.Content("a", "c", "d\re", rows)));

        var read = Codec.ReadImport(Codec.WriteExport(CsvTestData.Content("Team \"Notes", "Custom", null, rows)));

        Assert.Equal("Team \"Notes", read.Name);
        Assert.Equal(rows, read.Terms);
    }

    [Fact]
    public void An_import_reads_strict_utf8_and_honours_a_byte_order_mark()
    {
        const string csv = "# name: \u00C9quipe \u4E2D\npattern,replacement\n\u00E9t\u00E9,Summer\n";

        var plain = Codec.ReadImport(Encoding.UTF8.GetBytes(csv));
        var bom = Codec.ReadImport([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(csv)]);
        var little = Codec.ReadImport([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(csv)]);
        var big = Codec.ReadImport([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(csv)]);

        Assert.Equal(new LibraryTextEncoding(65001, false, false, false), plain.Encoding);
        Assert.Equal(new LibraryTextEncoding(65001, true, false, false), bom.Encoding);
        Assert.Equal(new LibraryTextEncoding(1200, true, false, false), little.Encoding);
        Assert.Equal(new LibraryTextEncoding(1201, true, false, false), big.Encoding);
        foreach (var read in new[] { plain, bom, little, big })
        {
            Assert.Equal("\u00C9quipe \u4E2D", read.Name);
            Assert.Equal([new TermValues("\u00E9t\u00E9", "Summer")], read.Terms);
        }
    }

    [Fact]
    public void Bytes_that_are_not_utf8_and_carry_no_byte_order_mark_read_with_the_ANSI_code_page()
    {
        // What a spreadsheet's plain "CSV (Comma delimited)" save writes on a Western PC: Windows-1252, no byte order mark.
        byte[] bytes = [.. Encoding.ASCII.GetBytes("# name: "), 0xC9, .. Encoding.ASCII.GetBytes("quipe\npattern,replacement\n"), 0xE9, 0x74, 0xE9, .. Encoding.ASCII.GetBytes(",Summer\n")];

        var read = Codec.ReadImport(bytes);

        Assert.Equal(new LibraryTextEncoding(1252, false, true, false), read.Encoding);
        Assert.Equal("\u00C9quipe", read.Name);
        Assert.Equal([new TermValues("\u00E9t\u00E9", "Summer")], read.Terms);
    }

    [Fact]
    public void An_ANSI_read_that_still_meets_invalid_bytes_replaces_and_says_so()
    {
        // Shift-JIS (932) as the ANSI code page: 0x81 opens a two-byte character that 0x20 cannot finish.
        var codec = new LibraryCsvCodec(932);
        byte[] bytes = [.. Encoding.ASCII.GetBytes("pattern,replacement\nx,"), 0x81, 0x20, 0x0A];

        var read = codec.ReadImport(bytes);

        Assert.Equal(new LibraryTextEncoding(932, false, true, true), read.Encoding);
        Assert.Contains('\uFFFD', read.Terms.Single().Written);
    }

    [Fact]
    public void A_byte_order_mark_is_honoured_even_when_the_bytes_after_it_are_broken()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("pattern,replacement\ncaf"), 0xE9, .. Encoding.ASCII.GetBytes(",Cafe\n")];

        var read = Codec.ReadImport(bytes);

        Assert.Equal(new LibraryTextEncoding(65001, true, false, true), read.Encoding);
        Assert.Equal("caf\uFFFD", read.Terms.Single().Spoken);
    }

    [Fact]
    public void The_shared_instance_falls_back_to_the_system_ANSI_code_page()
    {
        Assert.Equal(CodePagesEncodingProvider.Instance.GetEncoding(0)?.CodePage ?? 1252, LibraryCsvCodec.Instance.AnsiCodePage);
        Assert.Equal(1252, Codec.AnsiCodePage);
    }

    [Fact]
    public void Metadata_records_before_the_header_are_read_whether_or_not_they_are_quoted()
    {
        const string csv =
            "\"# name: Team, terms\",,,\r\n" +
            "# category: Work,,,\r\n" +
            "# description: a, b,,\r\n" +
            "\"# based-on: github\"\r\n" +
            "# formula-guard: 1,,,\r\n" +
            "pattern,replacement,whole_word,enabled\r\n" +
            "x,'=X,TRUE,FALSE\r\n" +
            "# name: Late\r\n";

        var read = Codec.ReadImport(CsvTestData.Utf8(csv));

        Assert.Equal("Team, terms", read.Name);
        Assert.Equal("Work", read.Category);
        Assert.Equal("a, b", read.Description);
        Assert.Equal("github", read.BasedOn);
        Assert.Equal(1, read.FormulaGuardVersion);
        Assert.Equal(LibraryCsvIssues.HeaderPaddingRemoved, read.Issues);
        Assert.Equal([new TermValues("x", "=X", WholeWord: true, Enabled: false)], read.Terms);
    }

    [Fact]
    public void Padding_is_reported_only_when_it_was_dropped_from_a_value_in_use()
    {
        var clean = Codec.ReadImport(CsvTestData.Utf8("# name: Team, terms, more\n# category: Work\npattern,replacement\nx,X\n"));
        var template = Codec.ReadImport(CsvTestData.Utf8("# Scribe dictionary template,,,\n# name: Team\npattern,replacement\nx,X\n"));
        var repeated = Codec.ReadImport(CsvTestData.Utf8("# name: Team\n# name: Other,,,\npattern,replacement\nx,X\n"));
        var inner = Codec.ReadImport(CsvTestData.Utf8("# description: a,,b\npattern,replacement\nx,X\n"));

        Assert.Equal("Team, terms, more", clean.Name);
        Assert.Equal(LibraryCsvIssues.None, clean.Issues);
        Assert.Equal(LibraryCsvIssues.None, template.Issues);
        Assert.Equal("Team", repeated.Name);
        Assert.Equal(LibraryCsvIssues.None, repeated.Issues);
        Assert.Equal("a,,b", inner.Description);
        Assert.Equal(LibraryCsvIssues.None, inner.Issues);
    }

    [Theory]
    [InlineData("pattern,replacement", true)]
    [InlineData("Pattern, Replacement ,Whole_Word", true)]
    [InlineData("pattern,replacement,whole_word,enabled", true)]
    [InlineData("pattern,replacement,whole_word,enabled,,", true)]
    [InlineData("\"pattern\",\"replacement\",\"whole_word\",\"enabled\"", true)]
    [InlineData("\"pattern\",\"replacement\",\"whole_word\"", true)]
    [InlineData("\"pattern\",\"replacement\"", false)]
    [InlineData("\"pattern\",replacement", false)]
    [InlineData("pattern,Pattern", false)]
    [InlineData("pattern,replacement,enabled", false)]
    [InlineData("pattern", false)]
    [InlineData("pattern,replacement,whole_word,enabled,extra", false)]
    public void The_first_record_is_the_header_only_by_its_shape(string first, bool isHeader)
    {
        var read = Codec.ReadImport(CsvTestData.Utf8(first + "\nx,X\n"));

        Assert.Equal(isHeader ? 1 : 2, read.Terms.Count + read.Errors.Count);
        Assert.Equal(new TermValues("x", "X"), read.Terms[^1]);
    }

    [Fact]
    public void Only_the_first_record_can_be_the_header()
    {
        var read = Codec.ReadImport(CsvTestData.Utf8("pattern,replacement\nx,X\npattern,replacement\n\"pattern\",Pattern\n"));

        Assert.Equal([new TermValues("x", "X"), new TermValues("pattern", "replacement"), new TermValues("pattern", "Pattern")], read.Terms);
    }

    [Fact]
    public void After_the_header_a_quoted_hash_is_data_and_an_unquoted_one_is_a_comment()
    {
        const string csv =
            "# name: Tags\npattern,replacement,whole_word,enabled\n\"#tag\",Tag,true,true\n#tag,Comment,true,true\n" +
            "  #also,a comment\n\" #spaced\",Spaced,true,true\n,,,\n";

        var read = Codec.ReadImport(CsvTestData.Utf8(csv));

        Assert.Equal([new TermValues("#tag", "Tag"), new TermValues(" #spaced", "Spaced")], read.Terms);
        Assert.Empty(read.Errors);
    }

    [Fact]
    public void A_headerless_file_takes_metadata_and_comments_only_before_its_first_row()
    {
        const string csv = "# name: Loose\n\"#quoted\",comment before the rows\na,A\n# name: Ignored\n\"#tag\",Tag\nb,B\n";

        var read = Codec.ReadImport(CsvTestData.Utf8(csv));

        Assert.Equal("Loose", read.Name);
        Assert.Equal([new TermValues("a", "A"), new TermValues("#tag", "Tag"), new TermValues("b", "B")], read.Terms);
    }

    [Fact]
    public void Only_unquoted_fields_are_trimmed_and_date_and_time_like_values_stay_text()
    {
        const string csv =
            "pattern,replacement,whole_word,enabled\n  spaced  , trimmed ,TRUE,FALSE\n\" kept \",\" kept too \",yes,no\n" +
            "1/2/2026,2-Jan,1,0\n12:30,1:23:45 PM,,\n";

        var read = Codec.ReadImport(CsvTestData.Utf8(csv));

        Assert.Equal(
            [
                new TermValues("spaced", "trimmed", WholeWord: true, Enabled: false),
                new TermValues(" kept ", " kept too ", WholeWord: true, Enabled: false),
                new TermValues("1/2/2026", "2-Jan", WholeWord: true, Enabled: false),
                new TermValues("12:30", "1:23:45 PM"),
            ],
            read.Terms);
    }

    [Theory]
    [InlineData("#tag", true)]
    [InlineData(" #tag", true)]
    [InlineData("pattern", true)]
    [InlineData("PATTERN", true)]
    [InlineData(" x", true)]
    [InlineData("x ", true)]
    [InlineData("x\u00A0", true)]
    [InlineData("\tx", true)]
    [InlineData("a,b", true)]
    [InlineData("a\"b", true)]
    [InlineData("a\rb", true)]
    [InlineData("a\nb", true)]
    [InlineData("a#b", false)]
    [InlineData("patterns", false)]
    [InlineData("a\tb", false)]
    [InlineData("", false)]
    public void The_writer_quotes_what_the_strict_reader_would_otherwise_misread(string value, bool firstFieldQuoted)
    {
        var secondFieldQuoted = value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ||
                                (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));

        Assert.Equal(firstFieldQuoted, LibraryCsvRecords.NeedsQuotes(value, firstField: true));
        Assert.Equal(secondFieldQuoted, LibraryCsvRecords.NeedsQuotes(value, firstField: false));
        if (value.Length > 0)
        {
            TermValues[] rows = [new(value, value), new("next", "Next")];
            var content = CsvTestData.Content("Quoting", "Custom", null, rows);
            Assert.Equal(rows, Codec.ReadImport(Codec.WriteExport(content)).Terms);
            Assert.Equal(rows, Codec.ReadManaged(Codec.WriteManaged(content)).Terms);
        }
    }

    [Fact]
    public void An_import_refuses_a_field_longer_than_the_cap()
    {
        // The cap applies to the value an import would store, so a guarded value one character over it on disk is fine.
        var atCap = new string('a', LibraryLimits.MaxFieldLength);
        var overCap = atCap + "a";
        var csv = $"pattern,replacement\n{atCap},ok\n{overCap},too long\nok,{overCap}\n\"'={atCap[1..]}\",guarded at the cap\n";

        var read = Codec.ReadImport(CsvTestData.Utf8("# formula-guard: 1\n" + csv));

        Assert.Equal([atCap, "=" + atCap[1..]], read.Terms.Select(t => t.Spoken));
        Assert.Equal(
            [
                new LibraryCsvRowError(4, LibraryCsvRowErrorKind.FieldTooLong, overCap),
                new LibraryCsvRowError(5, LibraryCsvRowErrorKind.FieldTooLong, overCap),
            ],
            read.Errors);
        Assert.Empty(Codec.ReadManaged(CsvTestData.Utf8(csv)).Errors);
    }

    [Fact]
    public void An_import_reads_no_more_rows_than_the_cap()
    {
        var csv = new StringBuilder("# name: Many\npattern,replacement\n");
        for (var i = 0; i < LibraryLimits.MaxTermsPerLibrary + 3; i++)
        {
            csv.Append(i == 7 ? "broken" : "t" + i).Append(i == 7 ? "\n" : ",T\n");
        }

        var read = Codec.ReadImport(CsvTestData.Utf8(csv.ToString()));

        Assert.Equal(LibraryCsvIssues.RowLimitExceeded, read.Issues);
        Assert.Equal(LibraryLimits.MaxTermsPerLibrary - 1, read.Terms.Count);
        Assert.Single(read.Errors);
        Assert.Equal("t" + (LibraryLimits.MaxTermsPerLibrary - 1), read.Terms[^1].Spoken);
        Assert.Equal(LibraryLimits.MaxTermsPerLibrary + 2, Codec.ReadManaged(CsvTestData.Utf8(csv.ToString())).Terms.Count);
    }

    [Fact]
    public void An_import_does_not_read_a_file_past_the_size_cap()
    {
        // The filler is one long comment after the rows, so the file at the cap reads as three records.
        var header = Encoding.UTF8.GetBytes("# name: Huge\npattern,replacement\nx,X\n#");
        var atCap = new byte[LibraryLimits.MaxImportBytes];
        header.CopyTo(atCap, 0);
        atCap.AsSpan(header.Length).Fill((byte)'a');
        byte[] overCap = [0xFF, 0xFE, .. atCap[..^1]];

        var read = Codec.ReadImport(atCap);
        var refused = Codec.ReadImport(overCap);

        Assert.Equal([new TermValues("x", "X")], read.Terms);
        Assert.Equal(LibraryCsvIssues.None, read.Issues);
        Assert.Equal(LibraryCsvIssues.SizeLimitExceeded, refused.Issues);
        Assert.Empty(refused.Terms);
        Assert.Empty(refused.Errors);
        Assert.Null(refused.Name);
        Assert.Equal(new LibraryTextEncoding(1200, true, false, false), refused.Encoding);
    }

    internal static LibraryContent RandomExportContent(Random random)
    {
        const string metadata = "ab ,#\u00E9\u4E2D\"'=";
        var name = MetadataValue(random, metadata);
        var category = MetadataValue(random, metadata);
        var description = random.Next(3) == 0 ? null : MetadataValue(random, metadata);
        var basedOn = random.Next(3) == 0 ? "custom-team" : null;
        var alphabet = random.Next(2) == 0 ? TriggerHeavy : CsvTestData.Adversarial;
        var rows = Enumerable.Range(0, random.Next(0, 8)).Select(_ => CsvTestData.Term(random, alphabet)).ToList();
        return CsvTestData.Content(name, category, description, rows, basedOn);
    }

    private static string MetadataValue(Random random, string alphabet)
    {
        var value = LibraryMetadata.Commit(CsvTestData.Random(random, alphabet, 12));
        return value.Length == 0 ? "n" : value;
    }

    private static byte[] SpreadsheetRoundTrip(byte[] export)
    {
        var text = Encoding.UTF8.GetString(export.AsSpan(3));
        return [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(CsvTestData.SpreadsheetSave(text))];
    }

    // What a value is after a spreadsheet save: exact when the export quoted it for a comma, a quote or a line break
    // (the spreadsheet quotes it again), otherwise its unquoted text, which the import trims.
    private static string AfterSpreadsheet(string value)
    {
        var encoded = LibraryFormulaGuard.Encode(value);
        return encoded.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? value : LibraryFormulaGuard.Decode(encoded.Trim());
    }

    // A spoken form the export quoted only because it starts with "#" loses its quotes in a spreadsheet and reads as a comment.
    private static bool LostToSpreadsheet(string spoken)
    {
        var encoded = LibraryFormulaGuard.Encode(spoken);
        return encoded.TrimStart().StartsWith('#') && encoded.AsSpan().IndexOfAny(",\"\r\n") < 0;
    }
}
