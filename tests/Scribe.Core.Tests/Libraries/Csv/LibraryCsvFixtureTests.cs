using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// The shared CSV fixtures the macOS port checks itself against (<c>tests/fixtures/libraries/csv</c>): every file under
/// <c>read</c> with the document the codec reads from it (<c>read-cases.json</c>), and every file under <c>write</c> with
/// the content it was written from (<c>write-cases.json</c>). The inputs are defined here, so the committed bytes can be
/// checked against them (a checkout that converted line ends would fail), and the expected documents and written files
/// are pinned: a change to what the codec reads or writes fails here until the fixtures are regenerated on purpose
/// (<c>SCRIBE_WRITE_CSV_FIXTURES=1</c>) and the diff reviewed.
/// </summary>
public sealed class LibraryCsvFixtureTests
{
    internal const string UpdateVariable = "SCRIBE_WRITE_CSV_FIXTURES";

    private const int AnsiCodePage = 1252;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static bool Regenerating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    private static IReadOnlyList<ReadCase> ReadCases { get; } =
    [
        new("managed-legacy-043.csv", "managed", "A file 0.4.3 wrote: raw metadata with commas and paired quotes, quoted rows.",
            Utf8(Crlf("# name: Team terms, 2026\n# category: Custom\n# description: Our \"house\" spellings, acronyms and names\n" +
                      "pattern,replacement,whole_word,enabled\nget hub,GitHub,true,true\n\"acme, inc\",\"Acme \"\"The Best\"\" Inc.\",true,true\n" +
                      "kube,K8s,false,false\n"))),
        new("managed-legacy-quirks.csv", "managed", "0.4.3's rules without the marker: a header anywhere, quoted and unquoted # skipped, every field trimmed, a stray quote inside a field, errors.",
            Utf8("# name: Quirks\n#\n# a comment, with a comma and a \"quote\"\npattern,replacement\na p i m,APIM\n\"#tag\",Tag\n pattern , again\n,,,\n" +
                 "say \"hi\",Hello\ncosmos db,Cosmos DB,maybe\nlonely\n\n  padded  ,  value  \nb,B,no,0\n# name: Late\nx,X\n")),
        new("managed-legacy-unclosed.csv", "managed", "An unpaired quote in the name: 0.4.3 reads no rows at all, and neither does this version.",
            Utf8(Crlf("# name: Team \"Notes\n# category: Custom\npattern,replacement,whole_word,enabled\nget hub,GitHub,true,true\nkube,K8s,true,true\n"))),
        new("managed-format-2.csv", "managed", "A file this version writes: the marker, rows quoted because they start with # or read as the header, edge white space, a multi-line value.",
            Utf8(Crlf("# name: Strict terms\n# category: Custom\n# description: Written by this version\n# based-on: github\n# scribe-format: 2\n" +
                      "pattern,replacement,whole_word,enabled\nget hub,GitHub,true,true\n\"#tag\",Tag,true,true\n\"pattern\",Pattern,true,true\n" +
                      "\" padded \",Kept,true,false\n") + "multi,\"line one\nline two\",false,true\r\n")),
        new("managed-format-2-hand-edited.csv", "managed", "A marked file edited by hand: comments only on raw # lines, the header only first, blank rows skipped, only unquoted fields trimmed.",
            Utf8("# name: Edited\n# scribe-format: 2\npattern,replacement,whole_word,enabled\n# a comment someone added\nx,X,true,true\n  #another comment\n" +
                 "pattern,replacement,true,true\n,,,\n\" q \", w ,true,true\nlonely\n")),
        new("managed-utf8-bom.csv", "managed", "UTF-8 with a byte order mark.",
            [0xEF, 0xBB, 0xBF, .. Utf8(Crlf("# name: Caf\u00e9\npattern,replacement\n\u00e9t\u00e9,Summer\n"))]),
        new("managed-utf16le.csv", "managed", "UTF-16 little endian with a byte order mark.",
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Crlf("# name: Caf\u00e9\npattern,replacement\n\u00e9t\u00e9,Summer\n"))]),
        new("managed-utf16be.csv", "managed", "UTF-16 big endian with a byte order mark.",
            [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Crlf("# name: Caf\u00e9\npattern,replacement\n\u00e9t\u00e9,Summer\n"))]),
        new("managed-invalid-utf8.csv", "managed", "Bytes that are not UTF-8: replaced as 0.4.3 replaced them, and reported.",
            [.. Ascii("# name: Caf"), 0xE9, .. Ascii("\r\npattern,replacement\r\ncaf"), 0xE9, .. Ascii(",Cafe\r\n")]),
        new("import-export.csv", "import", "What an export looks like: a byte order mark, metadata quoted when it holds a comma or a quote, the formula guard.",
            [0xEF, 0xBB, 0xBF, .. Utf8(Crlf("\"# name: Team, terms\"\n# category: Custom\n\"# description: Say \"\"hi\"\", then go\"\n# based-on: github\n" +
                                            "# formula-guard: 1\npattern,replacement,whole_word,enabled\nget hub,GitHub,true,true\nsum,'=SUM(A1),true,true\n" +
                                            "quote,''=literal,true,true\n\"#tag\",#tag,true,true\n\"pattern\",\"a,b\",false,true\nat,'@user,true,false\n"))]),
        new("import-excel-utf8.csv", "import", "That export after a spreadsheet's CSV UTF-8 save: padding, quotes only where needed, TRUE and FALSE, a date and a time. The #tag row lost its quotes and reads as a comment.",
            [0xEF, 0xBB, 0xBF, .. Utf8(Crlf("\"# name: Team, terms\",,,\n# category: Custom,,,\n\"# description: Say \"\"hi\"\", then go\",,,\n# based-on: github,,,\n" +
                                            "# formula-guard: 1,,,\npattern,replacement,whole_word,enabled\nget hub,GitHub,TRUE,TRUE\nsum,'=SUM(A1),TRUE,TRUE\n" +
                                            "#tag,#tag,TRUE,TRUE\npattern,\"a,b\",FALSE,TRUE\nthree four,4-Mar,TRUE,TRUE\nnew year,1/2/2026,TRUE,TRUE\n" +
                                            "half past,12:30,TRUE,FALSE\n"))]),
        new("import-excel-ansi.csv", "import", "A spreadsheet's plain CSV save on a Western PC: Windows-1252, no byte order mark.",
            Ansi(Crlf("# name: \u00c9quipe,,,\n# category: Caf\u00e9,,,\n# formula-guard: 1,,,\npattern,replacement,whole_word,enabled\n" +
                      "\u00e9t\u00e9,Summer,TRUE,TRUE\ncr\u00e8me br\u00fbl\u00e9e,Cr\u00e8me br\u00fbl\u00e9e,TRUE,TRUE\n"))),
        new("import-libreoffice-quote-all.csv", "import", "LibreOffice with Quote all text cells: every text cell quoted, the header too.",
            Utf8("\"# name: Team terms\",,,\n\"# category: Custom\",,,\n\"# formula-guard: 1\",,,\n\"pattern\",\"replacement\",\"whole_word\",\"enabled\"\n" +
                 "\"get hub\",\"GitHub\",TRUE,TRUE\n\"sum\",\"'=SUM(A1)\",TRUE,TRUE\n\"#tag\",\"Tag\",TRUE,TRUE\n")),
        new("import-043-export.csv", "import", "An export 0.4.3 wrote: raw metadata lines, no guard. A comma in a metadata line is rejoined; a quote in one is lost, as a CSV reader must read it.",
            [0xEF, 0xBB, 0xBF, .. Utf8(Crlf("# name: Team terms, 2026\n# category: Custom\n# description: Our \"house\" spellings, acronyms and names\n" +
                                            "pattern,replacement,whole_word,enabled\nget hub,GitHub,true,true\n\"acme, inc\",\"Acme \"\"The Best\"\" Inc.\",true,true\n"))]),
        new("import-043-export-spreadsheet.csv", "import", "That 0.4.3 export after a spreadsheet split its metadata lines at their commas and saved them: the lines are rejoined exactly.",
            [0xEF, 0xBB, 0xBF, .. Utf8(Crlf("# name: Team terms, 2026,,\n# category: Custom,,,\n\"# description: Our \"\"house\"\" spellings\", acronyms and names,,\n" +
                                            "pattern,replacement,whole_word,enabled\nget hub,GitHub,TRUE,TRUE\n\"acme, inc\",\"Acme \"\"The Best\"\" Inc.\",TRUE,TRUE\n"))]),
        new("import-headerless.csv", "import", "No column header: metadata and comments, quoted or not, before the first row; after it only raw # lines are comments.",
            Utf8("# name: Loose\n# a comment, before the rows\n\"#quoted\",counts as a comment before the first row\na,A\n# name: Ignored\n\"#tag\",Tag\nb,B,false\n")),
        new("import-strict-rules.csv", "import", "The strict rules after the header, and every kind of row error.",
            Utf8("# name: Strict\npattern,replacement,whole_word,enabled\n\"#tag\",Tag,true,true\n#comment,ignored,true,true\n  #also a comment\n" +
                 "pattern,replacement,true,true\n,,,\n\" kept \", trimmed ,yes,no\nlonely\n\"\",empty\nx,X,maybe\ny,Y,true,sometimes\nz,\"open\n")),
        new("import-utf16le.csv", "import", "UTF-16 little endian with a byte order mark.",
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Crlf("# name: \u4e2d\u6587\npattern,replacement\n\u00e9t\u00e9,Summer\n"))]),
        new("import-utf16be.csv", "import", "UTF-16 big endian with a byte order mark.",
            [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Crlf("# name: \u4e2d\u6587\npattern,replacement\n\u00e9t\u00e9,Summer\n"))]),
        new("import-bom-invalid.csv", "import", "A UTF-8 byte order mark and then bytes that are not UTF-8: the mark is honoured and the bytes replaced.",
            [0xEF, 0xBB, 0xBF, .. Ascii("pattern,replacement\r\ncaf"), 0xE9, .. Ascii(",Cafe\r\n")]),
        new("import-field-too-long.csv", "import", "A value longer than the per-field cap is skipped with its reason.",
            Utf8(Crlf("pattern,replacement\nshort,ok\nlong," + new string('x', LibraryLimits.MaxFieldLength + 1) + "\n"))),
    ];

    private static IReadOnlyList<WriteCase> WriteCases { get; } =
    [
        new("basic", "An ordinary library.", Content("Team terms", "Custom", "Our spellings", null,
            [new("get hub", "GitHub"), new("kube", "K8s", WholeWord: false), new("azure", "Azure", Enabled: false)])),
        new("quoting", "Values the writer quotes: a # or header-shaped first field, white space at an edge, commas, quotes and line breaks.", Content("Quoting", "Custom", null, null,
            [new("#tag", "#tag"), new("pattern", "Pattern"), new(" Pattern ", "x"), new("edge ", " edge"), new("\u00a0nbsp", "nbsp\u00a0"),
             new("a,b", "c,d"), new("say \"hi\"", "\"quoted\""), new("multi", "line one\nline two"), new("cr", "a\rb"), new("tab\tinside", "tab\tinside")])),
        new("guard", "Values the export guard marks: formula starts, their full-width forms, control characters, leading apostrophes and spaces.", Content("Guard", "Custom", null, null,
            [new("=SUM(A1)", "+1"), new("-x", "@user"), new("\uff1dx", "\uff0bx"), new("\uff0dx", "\uff20x"), new("\tx", "\rx"), new("\nx", "  =x"),
             new("'=x", "''+x"), new("' =x", "a=b"), new("'", "''"), new(" ' =x", "\u3000=x")])),
        new("empty", "A library with no rows and no description.", Content("Empty", "Custom", null, null, [])),
        new("duplicate", "A duplicate, which records the library it was copied from.", Content("GitHub - Copy", "Microsoft", "GitHub product names", "github",
            [new("get hub", "GitHub")])),
        new("astral", "Characters outside the Basic Multilingual Plane (surrogate pairs in UTF-16) in every field: written as four-byte UTF-8, read back whole.", Content(
            "Team \uD83D\uDE00", "Music \uD834\uDD1E", "Faces \uD83D\uDE00, clefs \uD834\uDD1E", "team-\uD83D\uDE00",
            [new("smile \uD83D\uDE00", "\uD83D\uDE00"), new("\uD834\uDD1E", "G clef \uD834\uDD1E", WholeWord: false), new("#\uD83D\uDE00", "=\uD834\uDD1E")])),
    ];

    [Fact]
    public void Every_read_fixture_reads_as_its_expected_document()
    {
        var codec = new LibraryCsvCodec(AnsiCodePage);
        var cases = new JsonArray();
        foreach (var @case in ReadCases)
        {
            var path = CsvTestData.FixturePath("read", @case.File);
            if (Regenerating)
            {
                File.WriteAllBytes(path, @case.Bytes);
            }

            Assert.True(File.Exists(path), $"{@case.File} is missing; set {UpdateVariable}=1 and run the tests once.");
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(@case.Bytes), $"{@case.File} is not stored byte for byte");
            var document = @case.Mode == "managed" ? codec.ReadManaged(@case.Bytes) : codec.ReadImport(@case.Bytes);
            cases.Add(new JsonObject
            {
                ["file"] = "read/" + @case.File,
                ["read"] = @case.Mode,
                ["about"] = @case.About,
                ["expected"] = Describe(document),
            });
        }

        var actual = new JsonObject
        {
            ["about"] = "What the library CSV codec reads from each file under read/: \"managed\" is ReadManaged, a file in the libraries folder; \"import\" is ReadImport, a file the user imports, whose ANSI fallback here is the code page given.",
            ["ansiCodePage"] = AnsiCodePage,
            ["cases"] = cases,
        };
        AssertPinned("read-cases.json", actual);
    }

    [Fact]
    public void Every_write_fixture_is_written_byte_for_byte()
    {
        var codec = new LibraryCsvCodec(AnsiCodePage);
        var cases = new JsonArray();
        foreach (var @case in WriteCases)
        {
            var managed = codec.WriteManaged(@case.Content);
            var export = codec.WriteExport(@case.Content);
            AssertPinnedBytes(@case.Name + ".managed.csv", managed);
            AssertPinnedBytes(@case.Name + ".export.csv", export);
            Assert.Equal(@case.Content.Rows.Select(r => r.Values), codec.ReadManaged(managed).Terms);
            Assert.Equal(@case.Content.Rows.Select(r => r.Values), codec.ReadImport(export).Terms);
            cases.Add(new JsonObject
            {
                ["name"] = @case.Name,
                ["about"] = @case.About,
                ["content"] = new JsonObject
                {
                    ["name"] = @case.Content.Name,
                    ["category"] = @case.Content.Category,
                    ["description"] = @case.Content.Description,
                    ["basedOn"] = @case.Content.BasedOn,
                    ["rows"] = Terms(@case.Content.Rows.Select(r => r.Values)),
                },
                ["managed"] = $"write/{@case.Name}.managed.csv",
                ["export"] = $"write/{@case.Name}.export.csv",
            });
        }

        var actual = new JsonObject
        {
            ["about"] = "What the library CSV codec writes for each content: WriteManaged (a file in the libraries folder) and WriteExport (a file the user shares).",
            ["cases"] = cases,
        };
        AssertPinned("write-cases.json", actual);
    }

    [Fact]
    public void No_shared_fixture_holds_an_em_or_en_dash()
    {
        foreach (var path in Directory.EnumerateFiles(CsvTestData.FixturePath(), "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(path)) + Encoding.Unicode.GetString(File.ReadAllBytes(path));
            Assert.False(text.Contains('\u2014') || text.Contains('\u2013'), path);
        }
    }

    internal static JsonObject Describe(LibraryCsvDocument document) => new()
    {
        ["name"] = document.Name,
        ["category"] = document.Category,
        ["description"] = document.Description,
        ["basedOn"] = document.BasedOn,
        ["terms"] = Terms(document.Terms),
        ["errors"] = new JsonArray(document.Errors.Select(error => (JsonNode)new JsonObject
        {
            ["line"] = error.Line,
            ["kind"] = error.Kind.ToString(),
            ["field"] = error.Field,
        }).ToArray()),
        ["encoding"] = new JsonObject
        {
            ["codePage"] = document.Encoding.CodePage,
            ["byteOrderMark"] = document.Encoding.ByteOrderMark,
            ["ansiFallback"] = document.Encoding.AnsiFallback,
            ["invalidBytesReplaced"] = document.Encoding.InvalidBytesReplaced,
        },
        ["formulaGuardVersion"] = document.FormulaGuardVersion,
        ["issues"] = new JsonArray(Enum.GetValues<LibraryCsvIssues>()
            .Where(issue => issue != LibraryCsvIssues.None && document.Issues.HasFlag(issue))
            .Select(issue => (JsonNode)issue.ToString()).ToArray()),
    };

    private static JsonArray Terms(IEnumerable<TermValues> terms) =>
        new(terms.Select(term => (JsonNode)new JsonObject
        {
            ["spoken"] = term.Spoken,
            ["written"] = term.Written,
            ["wholeWord"] = term.WholeWord,
            ["enabled"] = term.Enabled,
        }).ToArray());

    private static void AssertPinned(string fileName, JsonObject actual)
    {
        var path = CsvTestData.FixturePath(fileName);
        if (Regenerating)
        {
            File.WriteAllText(path, actual.ToJsonString(Json).ReplaceLineEndings("\n") + "\n");
        }

        Assert.True(File.Exists(path), $"{fileName} is missing; set {UpdateVariable}=1 and run the tests once.");
        var expected = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var expectedCases = expected["cases"]!.AsArray();
        var actualCases = actual["cases"]!.AsArray();
        for (var i = 0; i < Math.Max(expectedCases.Count, actualCases.Count); i++)
        {
            var want = i < expectedCases.Count ? expectedCases[i]?.ToJsonString(Json) : "(none)";
            var got = i < actualCases.Count ? actualCases[i]?.ToJsonString(Json) : "(none)";
            Assert.True(
                string.Equals(want, got, StringComparison.Ordinal),
                $"{fileName}, case {i}: the codec now gives\n{got}\nwhere the fixture says\n{want}\n" +
                $"If the change is meant, set {UpdateVariable}=1, run the tests and review the diff.");
        }

        Assert.True(JsonNode.DeepEquals(expected, actual), $"{fileName} differs; set {UpdateVariable}=1 if the change is meant.");
    }

    private static void AssertPinnedBytes(string fileName, byte[] actual)
    {
        var path = CsvTestData.FixturePath("write", fileName);
        if (Regenerating)
        {
            File.WriteAllBytes(path, actual);
        }

        Assert.True(File.Exists(path), $"{fileName} is missing; set {UpdateVariable}=1 and run the tests once.");
        Assert.True(
            File.ReadAllBytes(path).AsSpan().SequenceEqual(actual),
            $"{fileName} differs from what the codec writes; if the change is meant, set {UpdateVariable}=1.");
    }

    private static LibraryContent Content(string name, string category, string? description, string? basedOn, IEnumerable<TermValues> rows) =>
        CsvTestData.Content(name, category, description, rows, basedOn);

    private static string Crlf(string text) => text.ReplaceLineEndings("\r\n");

    private static byte[] Utf8(string text) => CsvTestData.Utf8(text);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Ansi(string text) => CodePagesEncodingProvider.Instance.GetEncoding(AnsiCodePage)!.GetBytes(text);

    private sealed record ReadCase(string File, string Mode, string About, byte[] Bytes);

    private sealed record WriteCase(string Name, string About, LibraryContent Content);
}
