using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// Managed library files, the CSVs in the libraries folder (format 6.4): a file without the format marker reads exactly
/// as 0.4.3 read it, so what dictation applies never changes on upgrade (X-2), what this version writes reads back
/// exactly, in this version and, apart from the rows quoted on purpose, in 0.4.3 (X-3), and managed reads decode as
/// <c>File.ReadAllText</c> did (X-4), with no import limits (X-7).
/// </summary>
public sealed class LibraryCsvManagedTests
{
    private static readonly LibraryCsvCodec Codec = CsvTestData.Codec;

    [Fact]
    public void Every_built_in_csv_reads_as_0_4_3_read_it()
    {
        var count = 0;
        foreach (var (resource, bytes) in CsvTestData.BuiltInCsvs())
        {
            var read = Codec.ReadManaged(bytes);

            CsvTestData.AssertReadsAs043(CsvTestData.ReadAllTextOf(bytes), read, resource);
            Assert.True(read.Terms.Count > 40, resource);
            count++;
        }

        Assert.Equal(11, count);
    }

    [Fact]
    public void The_W1a_fixture_files_read_as_0_4_3_read_them()
    {
        foreach (var (fileName, csv) in LibraryFixture.CustomFiles)
        {
            // Written the way the fixture writes them, File.WriteAllText's UTF-8 without a byte order mark.
            var bytes = CsvTestData.Utf8(csv);

            CsvTestData.AssertReadsAs043(CsvTestData.ReadAllTextOf(bytes), Codec.ReadManaged(bytes), fileName);
        }
    }

    [Fact]
    public void Seeded_legacy_files_read_as_0_4_3_read_them()
    {
        // Seeded property test against 0.4.3's own reader: metadata lines with commas, trailing commas and quotes (paired
        // and not), repeated and two-column headers, quoted "#" fields, comments, blank lines, stray quotes that swallow the
        // rest of a file, lone carriage returns, and UTF-8 with or without a byte order mark or UTF-16 with one, as 0.4.3's
        // File.ReadAllText decoded them.
        var random = new Random(20260925);
        var withRows = 0;
        var withErrors = 0;
        for (var i = 0; i < 4000; i++)
        {
            var text = RandomLegacyFile(random);
            var bytes = random.Next(4) switch
            {
                0 => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
                1 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
                2 => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
                _ => CsvTestData.Utf8(text),
            };

            var read = Codec.ReadManaged(bytes);

            CsvTestData.AssertReadsAs043(text, read, $"case {i}");
            withRows += read.Terms.Count > 0 ? 1 : 0;
            withErrors += read.Errors.Count > 0 ? 1 : 0;
        }

        Assert.True(withRows > 1500 && withErrors > 500, $"coverage: {withRows} with rows, {withErrors} with errors");
    }

    [Theory]
    [InlineData("repeated header", "pattern,replacement\na,A\npattern,replacement,whole_word,enabled\nb,B\n")]
    [InlineData("two-column header", "# name: Two\npattern,replacement\nget hub,GitHub\n")]
    [InlineData("quoted # field", "pattern,replacement\n\"#tag\",Tag\n\" #x\",X\ny,Y\n")]
    [InlineData("metadata with commas", "# name: Team, terms\n# description: a, b, c,\npattern,replacement\nx,X\n")]
    [InlineData("paired quotes in metadata", "# name: Team \"Q3\"\n# category: \"Work\"\npattern,replacement\nx,X\n")]
    [InlineData("quotes that pair across lines", "# name: a\"b\n# category: c\"d\npattern,replacement\nx,X\n")]
    [InlineData("an unpaired quote swallows the rows", "# name: Team \"Notes\npattern,replacement\nx,X\ny,Y\n")]
    [InlineData("the first name line wins even when empty", "# name:\n# name: Second\npattern,replacement\nx,X\n")]
    [InlineData("metadata after the rows is ignored", "pattern,replacement\nx,X\n# name: Late\n")]
    [InlineData("lone carriage returns", "# name: Old Mac\rpattern,replacement\rx,X\ry,Y\r")]
    [InlineData("stray quote inside a field", "pattern,replacement\nsay \"hi\",Hello\nx,X\n")]
    [InlineData("padded rows", "a,A,,\n,,,\nb,B,true,false,extra\n")]
    [InlineData("flags", "a,A,yes,no\nb,B,1,0\nc,C,maybe\nd,D,true,sometimes\n")]
    [InlineData("header in the middle", "a,A\n pattern , anything\nb,B\n")]
    public void Named_legacy_shapes_read_as_0_4_3_read_them(string label, string csv)
    {
        CsvTestData.AssertReadsAs043(csv, Codec.ReadManaged(CsvTestData.Utf8(csv)), label);
    }

    [Fact]
    public void What_this_version_writes_reads_back_exactly()
    {
        // Seeded property test over what the editor can hold: faithful values (a value an import brought with white space at
        // its edges, a multi-line written form, a legacy row with irregular spacing), spoken forms that start with "#" or
        // read as the header, and metadata with commas, a trailing comma, paired quotes, "#" and non-ASCII letters.
        var random = new Random(20260926);
        for (var i = 0; i < 3000; i++)
        {
            var content = RandomEditorContent(random);

            var bytes = Codec.WriteManaged(content);
            var read = Codec.ReadManaged(bytes);

            Assert.True(Equals(content.Name, read.Name), $"case {i}: name");
            Assert.True(Equals(content.Category, read.Category), $"case {i}: category");
            Assert.True(Equals(content.Description, read.Description), $"case {i}: description");
            Assert.True(Equals(content.BasedOn, read.BasedOn), $"case {i}: based-on");
            Assert.True(content.Rows.Select(r => r.Values).SequenceEqual(read.Terms), $"case {i}: rows");
            Assert.Empty(read.Errors);
            Assert.Equal(new LibraryTextEncoding(65001, false, false, false), read.Encoding);
            Assert.Null(read.FormulaGuardVersion);
            Assert.Equal(LibraryCsvIssues.None, read.Issues);
        }
    }

    [Fact]
    public void What_this_version_writes_reads_the_same_in_0_4_3_apart_from_the_rows_quoted_on_purpose()
    {
        // 0.4.3 skips every row whose first field starts with "#" or reads as the header, quoted or not, and trims every
        // value; everything else, and the metadata, it reads exactly as written.
        var random = new Random(20260927);
        for (var i = 0; i < 3000; i++)
        {
            var content = RandomEditorContent(random);

            var legacy = Legacy043LibraryCsv.Parse(CsvTestData.Text(Codec.WriteManaged(content)));

            var expected = content.Rows.Select(r => r.Values)
                .Where(v => !v.Spoken.TrimStart().StartsWith('#') &&
                            !string.Equals(v.Spoken.Trim(), "pattern", StringComparison.OrdinalIgnoreCase))
                .Select(v => v with { Spoken = v.Spoken.Trim(), Written = v.Written.Trim() });
            Assert.True(Equals(content.Name, legacy.Name), $"case {i}: name");
            Assert.True(Equals(content.Category, legacy.Category), $"case {i}: category");
            Assert.True(Equals(content.Description, legacy.Description), $"case {i}: description");
            Assert.True(expected.SequenceEqual(legacy.Entries.Select(TermValues.FromEntry)), $"case {i}: rows");
            Assert.Empty(legacy.Errors);
        }
    }

    [Fact]
    public void A_managed_file_has_the_documented_shape()
    {
        var content = CsvTestData.Content(
            "Team terms", "Custom", "Words, and names", [new("get hub", "GitHub"), new("#tag", "Tag", Enabled: false)], "github");

        var bytes = Codec.WriteManaged(content);

        Assert.Equal(
            "# name: Team terms\r\n# category: Custom\r\n# description: Words, and names\r\n# based-on: github\r\n" +
            "# scribe-format: 2\r\npattern,replacement,whole_word,enabled\r\nget hub,GitHub,true,true\r\n\"#tag\",Tag,true,false\r\n",
            CsvTestData.Text(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void An_empty_library_is_a_header_only_file()
    {
        var content = CsvTestData.Content("Empty", "Custom", "  ", []);

        var bytes = Codec.WriteManaged(content);
        var read = Codec.ReadManaged(bytes);

        Assert.Equal("# name: Empty\r\n# category: Custom\r\n# scribe-format: 2\r\npattern,replacement,whole_word,enabled\r\n", CsvTestData.Text(bytes));
        Assert.Empty(read.Terms);
        Assert.Empty(read.Errors);
        Assert.Null(read.Description);
    }

    [Fact]
    public void A_marked_file_reads_its_rows_by_the_strict_rules()
    {
        const string csv =
            "# name: Strict\n# scribe-format: 2\npattern,replacement,whole_word,enabled\n" +
            "\"#tag\",Tag,true,true\n" +
            "#a comment\n" +
            "  # another, with a comma\n" +
            "\" padded \",  kept  ,true,true\n" +
            "pattern,replacement,true,true\n" +
            ",,,\n" +
            "\"multi\nline\",\"Written\r\nform\",false,true\n";

        var read = Codec.ReadManaged(CsvTestData.Utf8(csv));

        Assert.Equal(
            [
                new TermValues("#tag", "Tag"),
                new TermValues(" padded ", "kept"),
                new TermValues("pattern", "replacement"),
                new TermValues("multi\nline", "Written\r\nform", WholeWord: false),
            ],
            read.Terms);
        Assert.Empty(read.Errors);
        Assert.Equal("Strict", read.Name);
    }

    [Fact]
    public void The_same_rows_without_the_marker_keep_0_4_3s_rules()
    {
        const string csv =
            "# name: Legacy\npattern,replacement,whole_word,enabled\n\"#tag\",Tag,true,true\n\" padded \",  kept  ,true,true\n" +
            "pattern,replacement,true,true\n,,,\n";

        var read = Codec.ReadManaged(CsvTestData.Utf8(csv));

        Assert.Equal([new TermValues("padded", "kept")], read.Terms);
        Assert.Equal([(6, LibraryCsvRowErrorKind.EmptySpoken)], read.Errors.Select(e => (e.Line, e.Kind)));
        CsvTestData.AssertReadsAs043(csv, read, "unmarked");
    }

    [Theory]
    [InlineData("# scribe-format: 2", true)]
    [InlineData("#scribe-format:2", true)]
    [InlineData("# Scribe-Format:  2  ", true)]
    [InlineData("# scribe-format: 3", false)]
    [InlineData("# scribe-format: 2.0", false)]
    [InlineData("# scribe-format:", false)]
    public void Only_the_format_2_marker_in_the_leading_comments_turns_the_strict_rules_on(string marker, bool strict)
    {
        var csv = marker + "\npattern,replacement\n\"#tag\",Tag\n";
        var late = "pattern,replacement\n\"#tag\",Tag\n" + marker + "\n";

        Assert.Equal(strict ? 1 : 0, Codec.ReadManaged(CsvTestData.Utf8(csv)).Terms.Count);
        Assert.Empty(Codec.ReadManaged(CsvTestData.Utf8(late)).Terms);
    }

    [Fact]
    public void Managed_metadata_comes_from_the_raw_lines_with_nothing_removed()
    {
        // Review finding A11: round 1 recovered spreadsheet padding here too, which ate a trailing comma 0.4.3 keeps.
        const string csv = "# name: Team,,,\n# category: Work, notes,\n# description: \"quoted\", and more\n# based-on: custom-team\npattern,replacement\nx,X\n";

        var read = Codec.ReadManaged(CsvTestData.Utf8(csv));

        Assert.Equal("Team,,,", read.Name);
        Assert.Equal("Work, notes,", read.Category);
        Assert.Equal("\"quoted\", and more", read.Description);
        Assert.Equal("custom-team", read.BasedOn);
        Assert.Equal(LibraryCsvIssues.None, read.Issues);
        CsvTestData.AssertReadsAs043(csv, read, "raw metadata");
    }

    [Fact]
    public void Row_errors_carry_their_line_kind_and_value()
    {
        const string csv =
            "# scribe-format: 2\npattern,replacement\nlonely\n\"\",Empty\n\" \",Blank\nx,X,maybe\ny,Y,true,sometimes\nz,\"open\n";

        var read = Codec.ReadManaged(CsvTestData.Utf8(csv));

        Assert.Equal(
            [
                new LibraryCsvRowError(3, LibraryCsvRowErrorKind.MissingFields),
                new LibraryCsvRowError(4, LibraryCsvRowErrorKind.EmptySpoken),
                new LibraryCsvRowError(5, LibraryCsvRowErrorKind.EmptySpoken),
                new LibraryCsvRowError(6, LibraryCsvRowErrorKind.InvalidWholeWord, "maybe"),
                new LibraryCsvRowError(7, LibraryCsvRowErrorKind.InvalidEnabled, "sometimes"),
                new LibraryCsvRowError(8, LibraryCsvRowErrorKind.UnclosedQuote),
            ],
            read.Errors);
        Assert.Empty(read.Terms);
    }

    [Fact]
    public void A_managed_read_applies_no_import_limit()
    {
        var longValue = new string('w', LibraryLimits.MaxFieldLength + 500);
        var csv = new StringBuilder("# name: Big\npattern,replacement\n");
        csv.Append("long,").Append(longValue).Append('\n');
        for (var i = 0; i < LibraryLimits.MaxTermsPerLibrary + 5; i++)
        {
            csv.Append("t").Append(i).Append(",T\n");
        }

        var read = Codec.ReadManaged(CsvTestData.Utf8(csv.ToString()));

        Assert.Equal(LibraryLimits.MaxTermsPerLibrary + 6, read.Terms.Count);
        Assert.Equal(longValue, read.Terms[0].Written);
        Assert.Empty(read.Errors);
        Assert.Equal(LibraryCsvIssues.None, read.Issues);
    }

    [Fact]
    public void Managed_reads_decode_exactly_as_File_ReadAllText()
    {
        byte[][] table =
        [
            [],
            [0x61],
            [0x61, 0xE2, 0x82],
            [0x61, 0xFF],
            [0xEF, 0xBB],
            [0xEF, 0xBB, 0xBF],
            [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x61],
            [0xFF, 0xFE],
            [0xFF, 0xFE, 0x00],
            [0xFF, 0xFE, 0x61],
            [0xFF, 0xFE, 0x00, 0x00],
            [0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61],
            [0xFE, 0xFF, 0x00, 0x61, 0xD8],
            [0xFE, 0xFF, 0xD8, 0x00, 0x00, 0x61],
            [0xC0, 0x80, 0x61],
            [0xED, 0xA0, 0x80],
            [0xE9, 0x2C, 0x61],
            Encoding.UTF8.GetBytes("# name: \u00C9quipe\npattern,replacement\n\u00E9t\u00E9,Summer\n"),
        ];
        const string alphabet = "\u0000\u0061\u0080\u00BB\u00BF\u00C3\u00A9\u00D8\u00DC\u00E2\u00EF\u00FE\u00FF\u000A\u000D\u0022\u002C";
        var random = new Random(20260928);
        var cases = table.Concat(Enumerable.Range(0, 600).Select(_ =>
            CsvTestData.Random(random, alphabet, 12).Select(c => (byte)c).ToArray()));

        foreach (var bytes in cases)
        {
            var (text, encoding) = LibraryCsvCodec.DecodeManaged(bytes);

            var label = BitConverter.ToString(bytes);
            Assert.True(string.Equals(CsvTestData.ReadAllTextOf(bytes), text, StringComparison.Ordinal), label);
            var detected = DetectedCodePage(bytes);
            var bom = detected == 65001 ? (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0) : detected is 1200 or 1201 ? 2 : 4;
            Assert.True(detected == encoding.CodePage, $"{label}: code page {encoding.CodePage}, expected {detected}");
            Assert.True((bom > 0) == encoding.ByteOrderMark, $"{label}: byte order mark");
            Assert.False(encoding.AnsiFallback, label);
            Assert.True(!IsValid(bytes.AsSpan(bom), detected) == encoding.InvalidBytesReplaced, $"{label}: invalid bytes");
        }
    }

    [Fact]
    public void A_managed_read_of_an_invalid_utf8_file_says_so_and_reads_as_0_4_3_did()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("# name: Caf"), 0xE9, .. Encoding.ASCII.GetBytes("\npattern,replacement\ncaf"), 0xE9, .. Encoding.ASCII.GetBytes(",Cafe\n")];

        var read = Codec.ReadManaged(bytes);

        Assert.Equal(new LibraryTextEncoding(65001, false, false, true), read.Encoding);
        Assert.Equal("Caf\uFFFD", read.Name);
        CsvTestData.AssertReadsAs043(CsvTestData.ReadAllTextOf(bytes), read, "invalid bytes");
    }

    [Fact]
    public void WriteManaged_refuses_metadata_the_editor_never_commits()
    {
        TermValues[] rows = [new("x", "X")];

        Assert.Throws<ArgumentNullException>(() => Codec.WriteManaged(null!));
        foreach (var bad in new[] { "a\nb", "a\rb", "a\r\nb" })
        {
            Assert.Throws<ArgumentException>(() => Codec.WriteManaged(CsvTestData.Content(bad, "Custom", null, rows)));
            Assert.Throws<ArgumentException>(() => Codec.WriteManaged(CsvTestData.Content("n", bad, null, rows)));
            Assert.Throws<ArgumentException>(() => Codec.WriteManaged(CsvTestData.Content("n", "c", bad, rows)));
            Assert.Throws<ArgumentException>(() => Codec.WriteManaged(CsvTestData.Content("n", "c", null, rows, basedOn: bad)));
        }

        // A header whose double quotes do not pair would hide every row from 0.4.3 (A11); paired quotes are fine, and so is
        // a line separator, which neither reader treats as a line break.
        Assert.Throws<ArgumentException>(() => Codec.WriteManaged(CsvTestData.Content("Team \"Notes", "Custom", null, rows)));
        Assert.Throws<ArgumentException>(() => Codec.WriteManaged(CsvTestData.Content("a\"", "b", "c\"d\"e", rows)));
        Assert.NotEmpty(Codec.WriteManaged(CsvTestData.Content("a\"b", "c\"d", null, rows)));
        Assert.NotEmpty(Codec.WriteManaged(CsvTestData.Content("a\u2028b", "c\u0085d", null, rows)));
    }

    internal static LibraryContent RandomEditorContent(Random random)
    {
        const string metadata = "ab ,#\u00E9\u00DF\u4E2D\"'";
        var name = MetadataValue(random, metadata);
        var category = MetadataValue(random, metadata);
        var description = random.Next(3) == 0 ? null : MetadataValue(random, metadata);
        var basedOn = random.Next(3) == 0 ? "github" : null;
        if (!LibraryMetadata.ReadsBackInOlderVersions(name, category, description, basedOn))
        {
            category += "\"";
        }

        var rows = Enumerable.Range(0, random.Next(0, 8)).Select(_ => CsvTestData.Term(random, CsvTestData.Adversarial)).ToList();
        return CsvTestData.Content(name, category, description, rows, basedOn);
    }

    private static string MetadataValue(Random random, string alphabet)
    {
        var value = LibraryMetadata.Commit(CsvTestData.Random(random, alphabet, 12));
        return value.Length == 0 ? "n" : value;
    }

    internal static string RandomLegacyFile(Random random)
    {
        var text = new StringBuilder();
        var newline = random.Next(10) switch { 0 => "\r", < 5 => "\n", _ => "\r\n" };
        var lines = random.Next(0, 14);
        for (var i = 0; i < lines; i++)
        {
            text.Append(RandomLegacyLine(random));
            if (i < lines - 1 || random.Next(3) != 0)
            {
                text.Append(random.Next(8) == 0 ? (random.Next(2) == 0 ? "\n" : "\r\n") : newline);
            }
        }

        return text.ToString();
    }

    private static string RandomLegacyLine(Random random) => random.Next(18) switch
    {
        0 => "# name: " + CsvTestData.Random(random, "ab ,#\u00E9\"", 10),
        1 => "# category: " + CsvTestData.Random(random, "ab ,\u00E9\"", 10),
        2 => "# description: " + CsvTestData.Random(random, "ab ,\u00E9\"", 12),
        3 => "#name:" + CsvTestData.Random(random, "ab ,", 6),
        4 => "  ## Name: " + CsvTestData.Random(random, "ab", 4),
        5 => "# name:",
        6 => random.Next(2) == 0 ? "pattern,replacement,whole_word,enabled" : "pattern,replacement",
        7 => " Pattern , Replacement ",
        8 => string.Empty,
        9 => "   ",
        10 => "# " + CsvTestData.Random(random, "ab, #\"", 12),
        11 => "\"#" + CsvTestData.Random(random, "ab ", 5) + "\",x",
        12 => "\" #" + CsvTestData.Random(random, "ab", 3) + "\",x,false",
        _ => RandomLegacyRow(random),
    };

    private static string RandomLegacyRow(Random random)
    {
        var fields = new List<string> { RandomLegacyField(random) };
        if (random.Next(10) != 0)
        {
            fields.Add(RandomLegacyField(random));
        }

        if (random.Next(2) == 0)
        {
            fields.Add(RandomFlag(random));
            if (random.Next(2) == 0)
            {
                fields.Add(RandomFlag(random));
            }
        }

        if (random.Next(10) == 0)
        {
            fields.Add(RandomLegacyField(random));
        }

        return string.Join(",", fields);
    }

    private static string RandomLegacyField(Random random) => random.Next(12) switch
    {
        0 => "\"" + CsvTestData.Random(random, "ab ,#\"\u00A0\t\n", 6).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        1 => CsvTestData.Random(random, "ab \"\t", 6),
        2 => " " + CsvTestData.Random(random, "ab", 4) + " ",
        3 => string.Empty,
        _ => CsvTestData.Random(random, "abc\u00E9 #'=", 6, 1),
    };

    private static string RandomFlag(Random random) =>
        new[] { "true", "false", "TRUE", " yes ", "no", "1", "0", "maybe", string.Empty, "\"false\"" }[random.Next(10)];

    private static int DetectedCodePage(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "scribe-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllBytes(path, bytes);
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            reader.ReadToEnd();
            return reader.CurrentEncoding.CodePage;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool IsValid(ReadOnlySpan<byte> bytes, int codePage)
    {
        var strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
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

    private static bool Equals(string? written, string? read) =>
        string.Equals(string.IsNullOrWhiteSpace(written) ? null : written.Trim(), read, StringComparison.Ordinal);
}
