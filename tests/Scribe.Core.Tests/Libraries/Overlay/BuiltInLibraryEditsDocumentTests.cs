using System.Text;
using System.Text.Json;
using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// The version 1 edits document (contract 6.5): what reading makes of every kind of bytes (O-4), that writing and
/// reading back is the identity (O-5), the exact form the writer gives, and that no bytes make the reader throw.
/// </summary>
public sealed class BuiltInLibraryEditsDocumentTests
{
    private const string Values = """{ "spoken": "get hub", "written": "GitHub", "wholeWord": true, "enabled": true }""";

    public static TheoryData<string, string> UnreadableDocuments() => new()
    {
        { "malformed JSON", """{ "version": 1, "library": "github", "terms": [ """ },
        { "not an object", """[ { "version": 1 } ]""" },
        { "empty", "" },
        { "no version", """{ "library": "github", "terms": [] }""" },
        { "a version that is text", """{ "version": "1", "library": "github", "terms": [] }""" },
        { "a version with a fraction", """{ "version": 1.0, "library": "github", "terms": [] }""" },
        { "version zero", """{ "version": 0, "library": "github", "terms": [] }""" },
        { "a negative version", """{ "version": -2, "library": "github", "terms": [] }""" },
        { "another library's id", """{ "version": 1, "library": "microsoft-azure", "terms": [] }""" },
        { "no library", """{ "version": 1, "terms": [] }""" },
        { "a library that is not text", """{ "version": 1, "library": 7, "terms": [] }""" },
        { "no terms", """{ "version": 1, "library": "github" }""" },
        { "terms that are not a list", """{ "version": 1, "library": "github", "terms": {} }""" },
        { "a term that is not an object", """{ "version": 1, "library": "github", "terms": [ "get hub" ] }""" },
        { "a term without an intent", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "base": {{Values}}, "value": {{Values}} } ] }""" },
        { "an intent that is not text", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": 1, "base": {{Values}}, "value": {{Values}} } ] }""" },
        { "a term without a key", $$"""{ "version": 1, "library": "github", "terms": [ { "intent": "added", "value": {{Values}} } ] }""" },
        { "a blank key", $$"""{ "version": 1, "library": "github", "terms": [ { "key": " \t ", "intent": "added", "value": {{Values}} } ] }""" },
        { "a key that is not text", $$"""{ "version": 1, "library": "github", "terms": [ { "key": null, "intent": "added", "value": {{Values}} } ] }""" },
        { "keys repeated with other case and spaces", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": {{Values}} }, { "key": " Get Hub", "intent": "added", "value": {{Values}} } ] }""" },
        { "an edit without its base", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "edited", "value": {{Values}} } ] }""" },
        { "a pin without its values", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "pinned", "base": {{Values}} } ] }""" },
        { "an off entry with values", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "off", "base": {{Values}}, "value": {{Values}} } ] }""" },
        { "an addition with a base", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "base": {{Values}}, "value": {{Values}} } ] }""" },
        { "values missing a member", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": "get hub", "written": "GitHub", "wholeWord": true } } ] }""" },
        { "a flag written as text", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": "get hub", "written": "GitHub", "wholeWord": "true", "enabled": true } } ] }""" },
        { "a null spoken form", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": null, "written": "GitHub", "wholeWord": true, "enabled": true } } ] }""" },
        { "values that are a list", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": [ "get hub", "GitHub", true, true ] } ] }""" },
        { "an acknowledgment missing a member", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "edited", "base": {{Values}}, "value": {{Values}}, "acknowledged": { "spoken": "get hub" } } ] }""" },
        { "a member repeated", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "key": "kube", "intent": "added", "value": {{Values}} } ] }""" },
        { "the version repeated", """{ "version": 1, "version": 1, "library": "github", "terms": [] }""" },
        { "a trailing comma", """{ "version": 1, "library": "github", "terms": [], }""" },
        { "a comment", """{ "version": 1, /* hand-edited */ "library": "github", "terms": [] }""" },
        { "a second value after the document", """{ "version": 1, "library": "github", "terms": [] } { }""" },
        { "an escaped unpaired surrogate", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": "get hub", "written": "\uD800", "wholeWord": true, "enabled": true } } ] }""" },
    };

    public static TheoryData<string, string, int?> NewerDocuments() => new()
    {
        { "version 2", """{ "version": 2, "library": "github", "terms": [] }""", 2 },
        { "version 2 shaped differently", """{ "version": 2, "entries": { "get hub": "renamed" } }""", 2 },
        { "a version too large to count", """{ "version": 123456789012345678901234567890, "library": "github" }""", null },
        { "a version beyond 32 bits", """{ "version": 4294967296, "library": "github", "terms": [] }""", null },
        { "an intent this build does not know", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "hidden", "until": "2027" } ] }""", 1 },
        { "an intent in other case", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "Edited", "base": {{Values}}, "value": {{Values}} } ] }""", 1 },
        { "an unknown intent beside a broken entry", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "kube", "intent": "edited" }, { "key": "get hub", "intent": "archived" } ] }""", 1 },
    };

    [Theory]
    [MemberData(nameof(UnreadableDocuments))]
    public void A_document_that_breaks_the_format_is_unreadable_and_gives_nothing_to_apply(string what, string json)
    {
        // O-4. Nothing of an unreadable document is applied: a partial read could bring a turned-off term back (R3).
        var read = BuiltInOverlay.ReadEdits("github", Encoding.UTF8.GetBytes(json));

        Assert.True(LibraryFileState.Unreadable == read.State, $"{what}: {read.State}");
        Assert.Null(read.Edits);
    }

    [Theory]
    [MemberData(nameof(NewerDocuments))]
    public void A_document_from_a_newer_version_is_newer_and_gives_nothing_to_apply(string what, string json, int? version)
    {
        // O-4: a version above 1, or an intent this build does not know, whatever else the document holds.
        var read = BuiltInOverlay.ReadEdits("github", Encoding.UTF8.GetBytes(json));

        Assert.True(LibraryFileState.Newer == read.State, $"{what}: {read.State}");
        Assert.Null(read.Edits);
        Assert.Equal(version, read.Version);
    }

    [Fact]
    public void The_declared_version_of_an_unreadable_document_is_reported_when_there_is_one()
    {
        Assert.Equal(0, BuiltInOverlay.ReadEdits("github", """{ "version": 0 }"""u8).Version);
        Assert.Equal(1, BuiltInOverlay.ReadEdits("github", """{ "version": 1, "library": "azure" }"""u8).Version);
        Assert.Null(BuiltInOverlay.ReadEdits("github", """{ "library": "github" }"""u8).Version);
        Assert.Null(BuiltInOverlay.ReadEdits("github", "not json"u8).Version);
    }

    [Fact]
    public void Bytes_that_are_not_text_are_unreadable()
    {
        byte[][] samples =
        [
            [0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00],
            [0x7B, 0x22, 0x76, 0xC3, 0x28, 0x22, 0x3A, 0x31, 0x7D],
            [0x00, 0x00, 0x00],
            [0xEF, 0xBB, 0xBF],
        ];

        Assert.All(samples, bytes => Assert.Equal(LibraryFileState.Unreadable, BuiltInOverlay.ReadEdits("github", bytes).State));
    }

    [Fact]
    public void A_readable_document_ignores_unknown_members_a_byte_order_mark_and_the_case_of_its_library_id()
    {
        var json = $$"""
            {
              "version": 1,
              "library": "GitHub",
              "note": "kept by a later version",
              "terms": [
                { "key": "  get hub  ", "intent": "edited", "base": {{Values}}, "value": { "spoken": "get hub", "written": "GitHub Enterprise", "wholeWord": true, "enabled": true, "color": "blue" }, "acknowledged": null, "since": 2 },
                { "key": "octo cat", "intent": "off", "base": { "spoken": "octo cat", "written": "Octocat", "wholeWord": false, "enabled": true }, "value": null }
              ]
            }
            """;
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var read = BuiltInOverlay.ReadEdits("github", bytes);

        Assert.Equal(LibraryFileState.Available, read.State);
        Assert.Equal(1, read.Version);
        AssertSameDocument(
            new BuiltInLibraryEdits(
                "GitHub",
                [
                    Edited("get hub", T("get hub", "GitHub"), T("get hub", "GitHub Enterprise")),
                    Off("octo cat", T("octo cat", "Octocat", wholeWord: false)),
                ]),
            read.Edits);
        Assert.Equal("get hub", read.Edits!.Terms[0].Key.Value);
    }

    [Fact]
    public void A_document_with_no_entries_is_readable_and_empty()
    {
        var read = BuiltInOverlay.ReadEdits("github", """{ "version": 1, "library": "github", "terms": [] }"""u8);

        Assert.Equal(LibraryFileState.Available, read.State);
        Assert.Empty(read.Edits!.Terms);
    }

    [Fact]
    public void The_writer_writes_indented_camel_case_utf8_without_a_byte_order_mark_and_leaves_absent_values_out()
    {
        var edits = Document(
            Edited("get hub", T("get hub", "GitHub"), T("git hub", "GitHub \"Enterprise\"\r\nServer"), T("get hub", "GitHub, Inc.")),
            Off("octo cat", T("octo cat", "Octocat", wholeWord: false)),
            Added("caf\u00E9", T("caf\u00E9", "Caf\u00E9 \U0001F600", enabled: false)));

        var bytes = BuiltInOverlay.WriteEdits(edits);

        const string expected = """
            {
              "version": 1,
              "library": "github",
              "terms": [
                {
                  "key": "get hub",
                  "intent": "edited",
                  "base": {
                    "spoken": "get hub",
                    "written": "GitHub",
                    "wholeWord": true,
                    "enabled": true
                  },
                  "value": {
                    "spoken": "git hub",
                    "written": "GitHub \u0022Enterprise\u0022\r\nServer",
                    "wholeWord": true,
                    "enabled": true
                  },
                  "acknowledged": {
                    "spoken": "get hub",
                    "written": "GitHub, Inc.",
                    "wholeWord": true,
                    "enabled": true
                  }
                },
                {
                  "key": "octo cat",
                  "intent": "off",
                  "base": {
                    "spoken": "octo cat",
                    "written": "Octocat",
                    "wholeWord": false,
                    "enabled": true
                  }
                },
                {
                  "key": "caf\u00E9",
                  "intent": "added",
                  "value": {
                    "spoken": "caf\u00E9",
                    "written": "Caf\u00E9 \uD83D\uDE00",
                    "wholeWord": true,
                    "enabled": false
                  }
                }
              ]
            }
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), Encoding.UTF8.GetString(bytes));
        Assert.All(bytes, b => Assert.True(b < 0x80));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.DoesNotContain((byte)'\r', bytes);
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
    }

    [Fact]
    public void The_writer_refuses_a_document_that_would_not_read_back()
    {
        TermValues value = T("get hub", "GitHub");

        BuiltInLibraryEdits[] invalid =
        [
            new(" ", [Added("get hub", value)]),
            new("github", null!),
            new("github", [null!]),
            new("github", [new BuiltInTermEdit(LibraryTermKey.Empty, BuiltInTermIntent.Added, null, value)]),
            new("github", [Added("get hub", value), Added("GET HUB ", value)]),
            new("github", [new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Edited, null, value)]),
            new("github", [new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Off, value, value)]),
            new("github", [new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Added, value, value)]),
            new("github", [new BuiltInTermEdit(K("get hub"), (BuiltInTermIntent)9, value, value)]),
            new("github", [Added("get hub", new TermValues("get hub", null!))]),
            new("github", [Edited("get hub", value, value, new TermValues(null!, "GitHub"))]),
        ];

        Assert.All(invalid, edits => Assert.Throws<ArgumentException>(() => BuiltInOverlay.WriteEdits(edits)));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.WriteEdits(null!));
    }

    [Fact]
    public void A_value_holding_an_unpaired_surrogate_is_written_with_a_replacement_character_as_utf8_writes_it()
    {
        var edits = Document(Added("get hub", T("get hub", "Git\uD800Hub")));

        var read = BuiltInOverlay.ReadEdits("github", BuiltInOverlay.WriteEdits(edits));

        Assert.Equal(LibraryFileState.Available, read.State);
        Assert.Equal("Git\uFFFDHub", read.Edits!.Terms[0].Value!.Written);
    }

    [Fact]
    public void Writing_then_reading_gives_back_the_same_document()
    {
        // O-5: seeded documents of every intent, with Unicode from every plane, line breaks, tabs, control characters,
        // quotes, backslashes, legacy inner white space in keys, and values from empty to 60,000 characters.
        var random = new Random(20260925);
        for (var round = 0; round < 400; round++)
        {
            var edits = RandomDocument(random);

            var bytes = BuiltInOverlay.WriteEdits(edits);
            var read = BuiltInOverlay.ReadEdits(edits.LibraryId.ToUpperInvariant(), bytes);

            Assert.Equal(LibraryFileState.Available, read.State);
            Assert.Equal(BuiltInLibraryEdits.CurrentVersion, read.Version);
            AssertSameDocument(edits, read.Edits, $"round {round}");
            Assert.Equal(bytes, BuiltInOverlay.WriteEdits(read.Edits!));
        }
    }

    [Fact]
    public void No_bytes_make_the_reader_throw()
    {
        // O-4, never throws: valid documents cut short, with bytes flipped, removed, repeated or inserted.
        var random = new Random(4242);
        var seeds = Enumerable.Range(0, 20).Select(_ => BuiltInOverlay.WriteEdits(RandomDocument(random, maxTerms: 4, maxLength: 12))).ToList();
        seeds.Add(Encoding.UTF8.GetBytes("""{ "version": 1, "library": "github", "terms": [ { "key": "a", "intent": "hidden" } ] }"""));
        var states = new HashSet<LibraryFileState>();
        for (var round = 0; round < 6000; round++)
        {
            var bytes = Mutate(random, seeds[random.Next(seeds.Count)]);

            var read = BuiltInOverlay.ReadEdits("github", bytes);

            states.Add(read.State);
            Assert.Contains(read.State, new[] { LibraryFileState.Available, LibraryFileState.Unreadable, LibraryFileState.Newer });
            Assert.Equal(read.State == LibraryFileState.Available, read.Edits is not null);
            if (read.Edits is not null)
            {
                // Whatever reads as a document is one the writer takes, and it reads back the same.
                AssertSameDocument(read.Edits, BuiltInOverlay.ReadEdits("github", BuiltInOverlay.WriteEdits(read.Edits)).Edits);
            }
        }

        Assert.Equal(3, states.Count);
    }

    [Fact]
    public void Reading_refuses_a_missing_library_id()
    {
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.ReadEdits(null!, "{}"u8));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.ReadEdits(" ", "{}"u8));
    }

    internal static BuiltInLibraryEdits RandomDocument(Random random, int maxTerms = 30, int maxLength = 0)
    {
        string[] ids = ["github", "ai-model-names", "microsoft-365", "scribe.new-pack"];
        var terms = new List<BuiltInTermEdit>();
        var keys = new HashSet<LibraryTermKey>();
        var count = random.Next(maxTerms + 1);
        while (terms.Count < count)
        {
            var key = LibraryTermKey.From(RandomText(random, maxLength == 0 ? 40 : maxLength));
            if (key.IsEmpty || !keys.Add(key))
            {
                continue;
            }

            var intent = (BuiltInTermIntent)random.Next(4);
            var @base = intent == BuiltInTermIntent.Added ? null : RandomValues(random, maxLength);
            var value = intent == BuiltInTermIntent.Off ? null : RandomValues(random, maxLength);
            var acknowledged = random.Next(3) == 0 ? RandomValues(random, maxLength) : null;
            terms.Add(new BuiltInTermEdit(key, intent, @base, value, acknowledged));
        }

        return new BuiltInLibraryEdits(ids[random.Next(ids.Length)], terms);
    }

    private static TermValues RandomValues(Random random, int maxLength)
    {
        var longest = maxLength != 0 ? maxLength : random.Next(150) == 0 ? 60_000 : 80;
        return new TermValues(RandomText(random, longest), RandomText(random, longest), random.Next(2) == 0, random.Next(2) == 0);
    }

    private static string RandomText(Random random, int maxLength)
    {
        string[] pieces =
        [
            "a", "Z", "get", " ", "  ", "\t", "\r\n", "\n", "\u00A0", "\u3000", "\u2028", "\u200B", "\u200D", "\"", "\\",
            "/", "'", "<", ">", "&", "+", "\u0000", "\u001F", "\u007F", "\u00E9", "e\u0301", "\u00DF", "\u212A", "\u03A3",
            "\u05E9\u05DC\u05D5\u05DD", "\u0645\u0631\u062D\u0628\u0627", "\u4E2D\u6587", "\uD83D\uDE00", "\uD83D\uDC69\u200D\uD83D\uDCBB",
            "\uFFFD", "\uFEFF", "=SUM(A1)", "#tag", ",",
        ];
        var length = random.Next(maxLength + 1);
        var text = new StringBuilder();
        while (text.Length < length)
        {
            text.Append(pieces[random.Next(pieces.Length)]);
        }

        return text.ToString();
    }

    private static byte[] Mutate(Random random, byte[] seed)
    {
        var bytes = seed.ToList();
        var edits = random.Next(1, 4);
        for (var i = 0; i < edits && bytes.Count > 0; i++)
        {
            var at = random.Next(bytes.Count);
            switch (random.Next(6))
            {
                case 0:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                case 1:
                    bytes[at] = (byte)random.Next(256);
                    break;
                case 2:
                    bytes.RemoveRange(at, Math.Min(random.Next(1, 12), bytes.Count - at));
                    break;
                case 3:
                    bytes.InsertRange(at, bytes.Skip(random.Next(bytes.Count)).Take(random.Next(1, 24)).ToArray());
                    break;
                case 4:
                    bytes.Insert(at, "{}[]\",:0123456789-.eE \\u"u8[random.Next(24)]);
                    break;
                default:
                    var text = Encoding.UTF8.GetString([.. bytes]);
                    string[] swaps = ["edited", "added", "pinned", "off", "true", "false", "null", "1", "2", "github"];
                    var from = swaps[random.Next(swaps.Length)];
                    var index = text.IndexOf(from, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        text = text[..index] + swaps[random.Next(swaps.Length)] + text[(index + from.Length)..];
                        bytes = [.. Encoding.UTF8.GetBytes(text)];
                    }

                    break;
            }
        }

        return [.. bytes];
    }
}
