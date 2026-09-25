using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// O-7: the fixtures the macOS port (stream M1) checks its own overlay against, under
/// <c>tests/fixtures/libraries/edits/</c>: <c>cases.json</c> (what each document reads as, the rows it gives against the
/// rows a version ships, and what collecting rows gives) and the documents it names, under <c>documents/</c>. The cases
/// below render them; the committed files are frozen, and these tests read the files, not the cases, so the files are
/// what is pinned. Regenerate only for a change you mean: set <c>SCRIBE_WRITE_EDITS_FIXTURES=1</c>, run these tests, and
/// review the diff.
/// </summary>
public sealed class BuiltInLibraryEditsFixtureTests
{
    private const string Library = "github";

    private static readonly TermValues GetHub = T("get hub", "GitHub");
    private static readonly TermValues Copilot = T("copilot", "Copilot");
    private static readonly TermValues OctoCat = T("octo cat", "Octocat");
    private static readonly TermValues Actions = T("git hub actions", "GitHub Actions");

    // Four shipped versions of one built-in: v2 corrects two written forms, drops "octo cat" and ships "gh cli" and
    // "git hub"; v3 brings "octo cat" back changed and changes "gh cli"; v4 corrects "get hub" again.
    private static readonly TermValues[] V1 = [GetHub, Copilot, OctoCat, Actions];
    private static readonly TermValues[] V2 =
        [T("get hub", "GitHub, Inc."), T("copilot", "GitHub Copilot"), Actions, T("gh cli", "GitHub CLI"), T("git hub", "Git Hub")];
    private static readonly TermValues[] V3 =
        [T("get hub", "GitHub, Inc."), T("copilot", "GitHub Copilot"), T("octo cat", "The Octocat"), Actions, T("gh cli", "GH CLI")];
    private static readonly TermValues[] V4 =
        [T("get hub", "GitHub Corp"), T("copilot", "GitHub Copilot"), Actions, T("gh cli", "GitHub CLI"), T("git hub", "Git Hub")];

    // v5 is v1 with "octo cat" shipped off (round 2, A1).
    private static readonly TermValues[] V5 = [GetHub, Copilot, T("octo cat", "Octocat", enabled: false), Actions];

    private static readonly (string Name, TermValues[] Rows)[] ShippedVersions = [("v1", V1), ("v2", V2), ("v3", V3), ("v4", V4), ("v5", V5)];

    private static string FixtureDirectory
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
            {
                root = root.Parent;
            }

            Assert.NotNull(root);
            return Path.Combine(root.FullName, "tests", "fixtures", "libraries", "edits");
        }
    }

    [Fact]
    public void The_committed_fixtures_are_what_the_cases_render_to()
    {
        var rendered = Render();
        if (Environment.GetEnvironmentVariable("SCRIBE_WRITE_EDITS_FIXTURES") == "1")
        {
            var documents = Path.Combine(FixtureDirectory, "documents");
            Directory.CreateDirectory(documents);
            foreach (var stale in Directory.GetFiles(documents, "*.json"))
            {
                File.Delete(stale);
            }

            foreach (var (name, bytes) in rendered)
            {
                File.WriteAllBytes(Path.Combine(FixtureDirectory, name), bytes);
            }
        }

        var committed = Directory.GetFiles(Path.Combine(FixtureDirectory, "documents"), "*.json")
            .Select(path => "documents/" + Path.GetFileName(path))
            .Append("cases.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(rendered.Keys.Order(StringComparer.Ordinal), committed);
        foreach (var (name, bytes) in rendered)
        {
            Assert.True(
                Normalized(bytes).SequenceEqual(Normalized(File.ReadAllBytes(Path.Combine(FixtureDirectory, name)))),
                $"{name} differs from what the cases render; set SCRIBE_WRITE_EDITS_FIXTURES=1 to regenerate it on purpose.");
        }
    }

    [Fact]
    public void Every_apply_case_gives_the_rows_the_fixture_lists()
    {
        var root = Cases();
        var cases = root.GetProperty("apply");
        Assert.True(cases.GetArrayLength() >= 20);
        foreach (var item in cases.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var shipped = ShippedOf(root, item);
            BuiltInLibraryEdits? edits = null;
            if (item.GetProperty("document").GetString() is { } document)
            {
                var read = BuiltInOverlay.ReadEdits(shipped.Id, File.ReadAllBytes(Path.Combine(FixtureDirectory, document)));
                Assert.True(read.State == LibraryFileState.Available, $"{name}: {read.State}");
                edits = read.Edits;
            }

            var rows = BuiltInOverlay.Apply(shipped, edits);

            var expected = item.GetProperty("rows").EnumerateArray().ToArray();
            Assert.True(expected.Length == rows.Count, $"{name}: {rows.Count} rows\n{Describe(rows)}");
            for (var i = 0; i < rows.Count; i++)
            {
                AssertRow(name, expected[i], rows[i], edits);
            }
        }
    }

    [Fact]
    public void Every_read_case_reads_as_the_fixture_says()
    {
        var cases = Cases().GetProperty("read");
        Assert.True(cases.GetArrayLength() >= 30);
        foreach (var item in cases.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, item.GetProperty("document").GetString()!));

            var read = BuiltInOverlay.ReadEdits(item.GetProperty("library").GetString()!, bytes);

            Assert.True(ParseState(item.GetProperty("state").GetString()!) == read.State, $"{name}: {read.State}");
            var version = item.GetProperty("version");
            Assert.True((version.ValueKind == JsonValueKind.Null ? null : version.GetInt32()) == read.Version, $"{name}: version {read.Version}");
            if (item.TryGetProperty("keys", out var keys))
            {
                Assert.True(
                    keys.EnumerateArray().Select(key => key.GetString()!).SequenceEqual(read.Edits!.Terms.Select(term => term.Key.Value)),
                    $"{name}: keys");
            }
            else
            {
                Assert.Null(read.Edits);
            }
        }
    }

    [Fact]
    public void Every_collect_case_collects_the_document_the_fixture_lists()
    {
        var root = Cases();
        var cases = root.GetProperty("collect");
        Assert.True(cases.GetArrayLength() >= 5);
        foreach (var item in cases.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var shipped = ShippedOf(root, item);
            var committed = ReadDocument(shipped.Id, item.GetProperty("committed").GetString()!);
            var omit = item.GetProperty("omit").EnumerateArray().Select(key => K(key.GetString()!)).ToHashSet();
            var restore = item.GetProperty("restore").EnumerateArray().Select(key => K(key.GetString()!)).ToHashSet();
            var rows = new List<LibraryRow>();
            foreach (var row in BuiltInOverlay.Apply(shipped, committed))
            {
                if (omit.Contains(row.Key))
                {
                    continue;
                }

                if (restore.Contains(row.Key))
                {
                    if (BuiltInOverlay.RestoreShipped(row) is { } restored)
                    {
                        rows.Add(restored);
                    }

                    continue;
                }

                rows.Add(row);
            }

            var collected = BuiltInOverlay.Collect(shipped, committed, rows);

            var expected = item.GetProperty("expected").GetString() is { } path ? ReadDocument(shipped.Id, path) : null;
            AssertSameDocument(expected, collected, name);
        }
    }

    [Fact]
    public void Every_document_Scribe_wrote_is_byte_for_byte_what_the_writer_gives_for_it()
    {
        var cases = Cases();
        var written = cases.GetProperty("apply").EnumerateArray()
            .Select(item => item.GetProperty("document").GetString())
            .Concat(cases.GetProperty("collect").EnumerateArray().SelectMany(item => new[] { item.GetProperty("committed").GetString(), item.GetProperty("expected").GetString() }))
            .OfType<string>()
            .Distinct()
            .ToArray();
        Assert.NotEmpty(written);
        foreach (var path in written)
        {
            var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, path));

            var rewritten = BuiltInOverlay.WriteEdits(BuiltInOverlay.ReadEdits(Library, bytes).Edits!);

            Assert.True(Normalized(bytes).SequenceEqual(rewritten), path);
        }
    }

    private static void AssertRow(string name, JsonElement expected, LibraryRow row, BuiltInLibraryEdits? edits)
    {
        var because = $"{name}, row {row.Key.Value}";
        Assert.True(expected.GetProperty("key").GetString() == row.Key.Value, because);
        Assert.True(ParseOrigin(expected.GetProperty("origin").GetString()!) == row.Origin, $"{because}: origin {row.Origin}");
        Assert.True(ReadValues(expected.GetProperty("values")) == row.Values, $"{because}: values {Describe(row.Values)}");
        Assert.True(ReadOptionalValues(expected.GetProperty("shipped")) == row.Shipped, $"{because}: shipped {Describe(row.Shipped)}");
        var intent = expected.GetProperty("intent");
        if (intent.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(row.Edit);
        }
        else
        {
            Assert.True(intent.GetString() == BuiltInLibraryEditsJson.IntentName(row.Edit!.Intent), $"{because}: intent {row.Edit.Intent}");
            Assert.Same(edits!.Terms.Single(term => term.Key == row.Key), row.Edit);
        }

        var review = expected.GetProperty("review");
        if (review.ValueKind == JsonValueKind.Null)
        {
            Assert.True(row.Review is null, $"{because}: review {row.Review?.Differing}");
        }
        else
        {
            var differing = review.GetProperty("differing").EnumerateArray()
                .Aggregate(TermFields.None, (fields, field) => fields | ParseField(field.GetString()!));
            Assert.True(
                new TermReview(ReadValues(review.GetProperty("yours")), ReadValues(review.GetProperty("updatedBuiltIn")), differing) == row.Review,
                $"{because}: review {row.Review?.Differing}");
        }
    }

    private static BuiltInLibraryEdits ReadDocument(string libraryId, string path)
    {
        var read = BuiltInOverlay.ReadEdits(libraryId, File.ReadAllBytes(Path.Combine(FixtureDirectory, path)));
        Assert.True(read.State == LibraryFileState.Available, $"{path}: {read.State}");
        return read.Edits!;
    }

    private static JsonElement Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory, "cases.json")), new JsonDocumentOptions { AllowDuplicateProperties = false });
        return document.RootElement.Clone();
    }

    private static DictionaryLibrary ShippedOf(JsonElement root, JsonElement item)
    {
        var version = root.GetProperty("shippedVersions").GetProperty(item.GetProperty("shippedVersion").GetString()!);
        return Shipped(item.GetProperty("library").GetString()!, [.. version.EnumerateArray().Select(ReadValues)]);
    }

    private static TermValues ReadValues(JsonElement element) => new(
        element.GetProperty("spoken").GetString()!,
        element.GetProperty("written").GetString()!,
        element.GetProperty("wholeWord").GetBoolean(),
        element.GetProperty("enabled").GetBoolean());

    private static TermValues? ReadOptionalValues(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : ReadValues(element);

    private static LibraryFileState ParseState(string state) => state switch
    {
        "available" => LibraryFileState.Available,
        "unreadable" => LibraryFileState.Unreadable,
        "newer" => LibraryFileState.Newer,
        _ => throw new InvalidDataException(state),
    };

    private static TermOrigin ParseOrigin(string origin) => Enum.GetValues<TermOrigin>().Single(value => OriginName(value) == origin);

    private static TermFields ParseField(string field) => Enum.GetValues<TermFields>().Single(value => value != TermFields.None && FieldName(value) == field);

    private static string OriginName(TermOrigin origin) => JsonNamingPolicy.CamelCase.ConvertName(origin.ToString());

    private static string FieldName(TermFields field) => JsonNamingPolicy.CamelCase.ConvertName(field.ToString());

    // A Windows checkout may turn the files' line feeds into CR LF; JSON does not care, and neither does the pin.
    private static byte[] Normalized(byte[] bytes) => [.. Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\n"))];

    // ---- The cases, and how they render ----

    private sealed record ApplyCase(string Name, string Note, TermValues[] Shipped, BuiltInTermEdit[]? Terms);

    private sealed record ReadCase(string Name, string Note, string Library, byte[] Bytes);

    private sealed record CollectCase(string Name, string Note, TermValues[] Shipped, BuiltInTermEdit[] Committed, string[] Omit, string[] Restore);

    private static IEnumerable<ApplyCase> ApplyCases()
    {
        var enterprise = T("get hub", "GitHub Enterprise");
        yield return new("no-document", "With no document every row is shipped, in shipped order.", V1, null);
        yield return new("edited", "An edit in the version it was made in: the user's values, edited.", V1, [Edited("get hub", GetHub, enterprise)]);
        yield return new(
            "edited-renamed-follows-a-correction-and-meets-a-new-shipped-row",
            "The user changed only Spoken, to \"git hub\": the row is still found by its key \"get hub\", Written takes the shipped correction, nothing asks, and the new shipped row \"git hub\" stands beside it (which one wins that spoken form is composition's).",
            V2,
            [Edited("get hub", GetHub, T("git hub", "GitHub"))]);
        yield return new(
            "edited-both-changed-asks",
            "The user and the new version both changed Written, differently: the user's value stays and the row asks about Written.",
            V2,
            [Edited("get hub", GetHub, enterprise)]);
        yield return new(
            "edited-adopted-is-pinned",
            "The new version ships the user's value: the row is pinned, still authored.",
            V2,
            [Edited("get hub", GetHub, T("get hub", "GitHub, Inc."))]);
        yield return new(
            "edited-kept-asks-once",
            "Keep my changes recorded this version's value as acknowledged, so the same change asks no more.",
            V2,
            [Edited("get hub", GetHub, enterprise, T("get hub", "GitHub, Inc."))]);
        yield return new(
            "edited-kept-asks-again-on-a-new-change",
            "A later version changes Written again, past the acknowledged value: the row asks again.",
            V4,
            [Edited("get hub", GetHub, enterprise, T("get hub", "GitHub, Inc."))]);
        yield return new(
            "edited-several-fields",
            "Fields merge one by one: Spoken and WholeWord are the user's, Written takes the correction.",
            V2,
            [Edited("copilot", Copilot, T("co pilot", "Copilot", wholeWord: false))]);
        yield return new(
            "edited-turned-off-stays-off",
            "An edited row the user turned off stays off when the version corrects its Written.",
            V2,
            [Edited("get hub", GetHub, T("get hub", "GitHub", enabled: false))]);
        yield return new("pinned", "A pinned row in the version whose values it holds.", V1, [Pinned("get hub", GetHub, GetHub)]);
        yield return new(
            "pinned-asks-on-any-change",
            "A pinned row takes no shipped change without asking: the user's values stay, shown as edited, and it asks about Written.",
            V2,
            [Pinned("get hub", GetHub, GetHub)]);
        yield return new(
            "pinned-kept",
            "Keep my changes on a pinned row: acknowledged, so this version's change asks no more.",
            V2,
            [Pinned("get hub", GetHub, GetHub, T("get hub", "GitHub, Inc."))]);
        yield return new("off", "A shipped row the user turned off.", V1, [Off("octo cat", OctoCat)]);
        yield return new("off-row-gone", "The version does not ship the row: the off entry shows nothing, and stays in the document.", V2, [Off("octo cat", OctoCat)]);
        yield return new(
            "off-row-back-changed",
            "The row returns, changed: off again, with the values this version ships.",
            V3,
            [Off("octo cat", OctoCat)]);
        yield return new("added", "A row the user added comes after the shipped rows.", V1, [Added("gh cli", T("gh cli", "GitHub CLI"))]);
        yield return new(
            "added-shipped-alike",
            "A later version ships the added key with the user's values: pinned, authored, in the shipped row's place.",
            V2,
            [Added("gh cli", T("gh cli", "GitHub CLI"))]);
        yield return new(
            "added-shipped-differently",
            "A later version ships the added key with other values: still the user's row, added, with the shipped values beside it.",
            V3,
            [Added("gh cli", T("gh cli", "GitHub CLI"))]);
        yield return new(
            "edited-no-longer-shipped",
            "The version does not ship the edited row: it keeps the user's values as no longer shipped, after the shipped rows.",
            V2,
            [Edited("octo cat", OctoCat, T("octo cat", "The Octocat"))]);
        yield return new(
            "pinned-no-longer-shipped",
            "The same for a pinned row.",
            V2,
            [Pinned("octo cat", OctoCat, OctoCat)]);
        yield return new(
            "order",
            "Shipped rows in shipped order with entries in place, then added and no-longer-shipped rows in document order; an off entry for a row not shipped shows nothing.",
            V2,
            [
                Added("zulu", T("zulu", "Zulu")),
                Edited("octo cat", OctoCat, T("octo cat", "Octo Cat")),
                Off("copilot", Copilot),
                Added("yankee", T("yankee", "Yankee")),
                Off("old term", T("old term", "Old")),
                Edited("get hub", GetHub, enterprise),
            ]);
        yield return new(
            "keys-compare-without-case",
            "An entry's key finds the shipped row whatever the case: \"Get Hub\" edits the shipped \"get hub\", and Spoken, left at its base, takes the shipped spelling.",
            V1,
            [Edited("Get Hub", T("Get Hub", "GitHub"), enterprise with { Spoken = "Get Hub" })]);
        yield return new(
            "text-kept-exactly",
            "Values are stored and applied exactly: inner white space, line breaks, quotes and letters beyond ASCII.",
            V1,
            [
                Added("caf\u00E9", T("caf\u00E9", "Caf\u00E9 \"au lait\"\r\nline two \uD83D\uDE00")),
                Edited("octo cat", OctoCat, T("octo  cat", "Octo\tcat", wholeWord: false)),
            ]);

        // Round 2, A4: a review lists every field the two versions differ in, not only the fields that ask.
        yield return new(
            "edited-review-lists-every-difference",
            "The user changed Spoken and Written; the version changed only Written. Written asks, and the review lists both fields, since Use updated values replaces both.",
            V2,
            [Edited("get hub", GetHub, T("git hub", "GitHub Enterprise"))]);
        yield return new(
            "pinned-review-lists-a-kept-field",
            "Keep my changes was chosen for this Written; the change to WholeWord is new and asks. The review lists both fields.",
            V2,
            [Pinned("get hub", GetHub, T("get hub", "GitHub", wholeWord: false), T("get hub", "GitHub, Inc.", wholeWord: false))]);

        // Round 2, A1: an off row edited while its version shipped it off keeps the check box as the user's own value, so
        // the base holds the value it was turned off from; the entry is what the overlay's Edit gives there.
        BuiltInTermEdit offEdited = Edited("octo cat", OctoCat, T("octo cat", "Octo Cat", enabled: false));
        yield return new(
            "off-then-edited-stays-off-when-shipped-on",
            "Turned off, then edited (Written only) while a version shipped it off: a version that ships it on leaves it off, and nothing asks.",
            V1,
            [offEdited]);
        yield return new(
            "off-then-edited-stays-off-when-shipped-off",
            "The same entry while the version ships it off: off, and nothing asks, since the user and the version agree.",
            V5,
            [offEdited]);

        // Round 2, part 2, G1: Turn on term on an off row while the version in use ships the row off records the user's
        // own value of the check box (on) and inherits the rest; the entry is what the overlay's SetEnabled gives there.
        BuiltInTermEdit turnedOn = Edited("octo cat", T("octo cat", "Octocat", enabled: false), OctoCat);
        yield return new(
            "turned-on-while-shipped-off",
            "Turned back on while the version ships it off: on, as the user chose, and nothing asks.",
            V5,
            [turnedOn]);
        yield return new(
            "turned-on-while-shipped-off-then-shipped-on",
            "The same entry in a version that ships it on: on, and reads as pinned, since the user and the version agree.",
            V1,
            [turnedOn]);
    }

    private static IEnumerable<ReadCase> ReadCases()
    {
        const string values = """{ "spoken": "get hub", "written": "GitHub", "wholeWord": true, "enabled": true }""";
        const string enterprise = """{ "spoken": "get hub", "written": "GitHub Enterprise", "wholeWord": true, "enabled": true }""";
        ReadCase Text(string name, string note, string json, string library = Library) => new(name, note, library, Encoding.UTF8.GetBytes(json));

        yield return new("written-by-scribe", "What Scribe writes.", Library, BuiltInOverlay.WriteEdits(Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise"), T("get hub", "GitHub, Inc.")), Off("octo cat", OctoCat), Added("gh cli", T("gh cli", "GitHub CLI")))));
        yield return Text("unknown-members-ignored", "Members this version does not know are ignored, at every level.", $$"""{ "version": 1, "library": "github", "note": "x", "terms": [ { "key": "get hub", "intent": "edited", "base": {{values}}, "value": { "spoken": "get hub", "written": "GitHub Enterprise", "wholeWord": true, "enabled": true, "color": "blue" }, "since": 2 } ] }""");
        yield return new("byte-order-mark", "A leading UTF-8 byte order mark is skipped.", Library, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "edited", "base": {{values}}, "value": {{enterprise}} } ] }""")]);
        yield return Text("key-trimmed", "A key is read as the term key of its text: trimmed.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "  get hub  ", "intent": "added", "value": {{values}} } ] }""");
        yield return Text("null-values-absent", "An explicit null for values is the same as leaving them out.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "octo cat", "intent": "off", "base": {{values}}, "value": null, "acknowledged": null } ] }""");
        yield return Text("library-id-case", "The library id is compared without case.", """{ "version": 1, "library": "GitHub", "terms": [] }""");
        yield return Text("no-entries", "A document with no entries reads, empty.", """{ "version": 1, "library": "github", "terms": [] }""");
        yield return Text("malformed-json", "Not JSON.", """{ "version": 1, "library": "github", "terms": [ """);
        yield return Text("not-an-object", "The document is not an object.", """[ { "version": 1 } ]""");
        yield return Text("version-missing", "No version.", """{ "library": "github", "terms": [] }""");
        yield return Text("version-text", "A version that is text.", """{ "version": "1", "library": "github", "terms": [] }""");
        yield return Text("version-fraction", "A version that is not an integer literal.", """{ "version": 1.0, "library": "github", "terms": [] }""");
        yield return Text("version-zero", "Version 0.", """{ "version": 0, "library": "github", "terms": [] }""");
        yield return Text("version-two", "Version 2: newer, whatever it holds.", """{ "version": 2, "entries": { "get hub": "renamed" } }""");
        yield return Text("version-huge", "A version too large to count is still newer.", """{ "version": 123456789012345678901234567890 }""");
        yield return Text("intent-unknown", "An intent this version does not know: newer.", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "hidden", "until": "2027" } ] }""");
        yield return Text("intent-other-case", "Intent names are exact: \"Edited\" is not \"edited\".", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "Edited", "base": {{values}}, "value": {{values}} } ] }""");
        yield return Text("intent-unknown-beside-broken-entry", "An unknown intent makes the document newer even beside a broken entry.", """{ "version": 1, "library": "github", "terms": [ { "key": "kube", "intent": "edited" }, { "key": "get hub", "intent": "archived" } ] }""");
        yield return Text("intent-not-text", "An intent that is not text.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": 1, "value": {{values}} } ] }""");
        yield return Text("intent-missing", "An entry without an intent.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "value": {{values}} } ] }""");
        yield return Text("library-other", "Another library's document.", """{ "version": 1, "library": "microsoft-azure", "terms": [] }""");
        yield return Text("library-missing", "No library id.", """{ "version": 1, "terms": [] }""");
        yield return Text("terms-missing", "No list of entries.", """{ "version": 1, "library": "github" }""");
        yield return Text("terms-not-a-list", "Entries that are not a list.", """{ "version": 1, "library": "github", "terms": {} }""");
        yield return Text("entry-not-an-object", "An entry that is not an object.", """{ "version": 1, "library": "github", "terms": [ "get hub" ] }""");
        yield return Text("key-missing", "An entry without a key.", $$"""{ "version": 1, "library": "github", "terms": [ { "intent": "added", "value": {{values}} } ] }""");
        yield return Text("key-blank", "A key that is only white space.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": " \t ", "intent": "added", "value": {{values}} } ] }""");
        yield return Text("key-repeated", "Two entries with one key (keys compare trimmed and without case).", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": {{values}} }, { "key": " Get Hub", "intent": "added", "value": {{values}} } ] }""");
        yield return Text("edited-without-base", "An edited entry needs its base.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "edited", "value": {{values}} } ] }""");
        yield return Text("pinned-without-values", "A pinned entry needs its values.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "pinned", "base": {{values}} } ] }""");
        yield return Text("off-with-values", "An off entry holds no values.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "off", "base": {{values}}, "value": {{values}} } ] }""");
        yield return Text("added-with-base", "An added entry has no base.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "base": {{values}}, "value": {{values}} } ] }""");
        yield return Text("values-missing-a-member", "Values need all four members.", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": "get hub", "written": "GitHub", "wholeWord": true } } ] }""");
        yield return Text("values-member-mistyped", "A flag written as text.", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": "get hub", "written": "GitHub", "wholeWord": "true", "enabled": true } } ] }""");
        yield return Text("member-repeated", "A member given twice, which two readers could take differently.", $$"""{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "key": "kube", "intent": "added", "value": {{values}} } ] }""");
        yield return Text("trailing-comma", "A trailing comma.", """{ "version": 1, "library": "github", "terms": [], }""");
        yield return Text("comment", "A comment.", """{ "version": 1, /* note */ "library": "github", "terms": [] }""");
        yield return Text("second-value", "More after the document.", """{ "version": 1, "library": "github", "terms": [] } { }""");
        yield return Text("unpaired-surrogate", "An escaped unpaired surrogate is not text.", """{ "version": 1, "library": "github", "terms": [ { "key": "get hub", "intent": "added", "value": { "spoken": "get hub", "written": "\uD800", "wholeWord": true, "enabled": true } } ] }""");
    }

    private static IEnumerable<CollectCase> CollectCases()
    {
        // Canonical order: entries of shipped rows in shipped order, then of rows not shipped, then inert off entries.
        BuiltInTermEdit[] committed =
        [
            Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")),
            Off("copilot", Copilot),
            Added("zulu", T("zulu", "Zulu")),
            Edited("octo cat", OctoCat, T("octo cat", "The Octocat")),
            Off("old term", T("old term", "Old")),
        ];
        yield return new("every-row-given", "Collecting the rows a document gives returns that document.", V2, committed, [], []);
        yield return new(
            "rows-left-out",
            "Shipped rows the caller leaves out keep their intents, and the inert off entry stays; the addition and the no-longer-shipped row, which the user can delete, go.",
            V2,
            committed,
            ["get hub", "copilot", "zulu", "octo cat"],
            []);
        yield return new("rows-deleted", "The user deleted the addition and the no-longer-shipped row.", V2, committed, ["zulu", "octo cat"], []);
        yield return new(
            "rows-restored",
            "Restore built-in values ends the intents of the shipped rows; the rows not shipped cannot be restored and go; the inert off entry stays.",
            V2,
            committed,
            [],
            ["get hub", "copilot", "zulu", "octo cat"]);
        yield return new("nothing-left", "With every intent gone there is no document.", V2, [Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")), Added("zulu", T("zulu", "Zulu"))], ["zulu"], ["get hub"]);
    }

    private static SortedDictionary<string, byte[]> Render()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "about",
                "Fixtures for built-in library edits documents (LibrariesDir\\edits\\<id>.json, version 1), frozen once the Windows overlay's tests passed; the macOS port must give the same answers. 'shippedVersions': the rows four versions of one built-in ship, in shipped order. 'apply': the rows a document gives against a version's rows, in saved order (shipped rows in shipped order with entries in place, then added and no-longer-shipped rows in document order). 'read': how bytes read. 'collect': the document collected from the rows a committed document gives, with the rows under 'omit' left out and those under 'restore' restored to their shipped values (or left out when they have none). Values compare ordinally; keys are term keys (trimmed, compared without case). Line endings in these files are not significant.");
            writer.WriteStartObject("shippedVersions");
            foreach (var (name, rows) in ShippedVersions)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(Encoding.UTF8.GetBytes("[" + string.Join(",", rows.Select(values => Encoding.UTF8.GetString(Compact(values)))) + "]"));
            }

            writer.WriteEndObject();
            writer.WriteStartArray("apply");
            foreach (var @case in ApplyCases())
            {
                var shipped = Shipped(Library, @case.Shipped);
                var edits = @case.Terms is null ? null : new BuiltInLibraryEdits(Library, @case.Terms);
                string? document = null;
                if (edits is not null)
                {
                    document = $"documents/apply-{@case.Name}.json";
                    files[document] = BuiltInOverlay.WriteEdits(edits);
                }

                writer.WriteStartObject();
                writer.WriteString("name", @case.Name);
                writer.WriteString("note", @case.Note);
                writer.WriteString("library", Library);
                writer.WriteString("shippedVersion", VersionName(@case.Shipped));
                writer.WriteString("document", document);
                writer.WriteStartArray("rows");
                foreach (var row in BuiltInOverlay.Apply(shipped, edits))
                {
                    WriteRow(writer, row);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("read");
            foreach (var @case in ReadCases())
            {
                var document = $"documents/read-{@case.Name}.json";
                files[document] = @case.Bytes;
                var read = BuiltInOverlay.ReadEdits(@case.Library, @case.Bytes);
                writer.WriteStartObject();
                writer.WriteString("name", @case.Name);
                writer.WriteString("note", @case.Note);
                writer.WriteString("library", @case.Library);
                writer.WriteString("document", document);
                writer.WriteString("state", JsonNamingPolicy.CamelCase.ConvertName(read.State.ToString()));
                if (read.Version is { } version)
                {
                    writer.WriteNumber("version", version);
                }
                else
                {
                    writer.WriteNull("version");
                }

                if (read.Edits is not null)
                {
                    writer.WriteStartArray("keys");
                    foreach (var term in read.Edits.Terms)
                    {
                        writer.WriteStringValue(term.Key.Value);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("collect");
            foreach (var @case in CollectCases())
            {
                var shipped = Shipped(Library, @case.Shipped);
                var committed = new BuiltInLibraryEdits(Library, @case.Committed);
                var committedPath = $"documents/collect-{@case.Name}-committed.json";
                files[committedPath] = BuiltInOverlay.WriteEdits(committed);
                var omit = @case.Omit.Select(K).ToHashSet();
                var restore = @case.Restore.Select(K).ToHashSet();
                var rows = BuiltInOverlay.Apply(shipped, committed)
                    .Where(row => !omit.Contains(row.Key))
                    .Select(row => restore.Contains(row.Key) ? BuiltInOverlay.RestoreShipped(row) : row)
                    .OfType<LibraryRow>()
                    .ToArray();
                var collected = BuiltInOverlay.Collect(shipped, committed, rows);
                string? expected = null;
                if (collected is not null)
                {
                    expected = $"documents/collect-{@case.Name}-expected.json";
                    files[expected] = BuiltInOverlay.WriteEdits(collected);
                }

                writer.WriteStartObject();
                writer.WriteString("name", @case.Name);
                writer.WriteString("note", @case.Note);
                writer.WriteString("library", Library);
                writer.WriteString("shippedVersion", VersionName(@case.Shipped));
                writer.WriteString("committed", committedPath);
                WriteStrings(writer, "omit", @case.Omit);
                WriteStrings(writer, "restore", @case.Restore);
                writer.WriteString("expected", expected);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        files["cases.json"] = [.. buffer.ToArray(), (byte)'\n'];
        return files;
    }

    private static void WriteRow(Utf8JsonWriter writer, LibraryRow row)
    {
        writer.WriteStartObject();
        writer.WriteString("key", row.Key.Value);
        writer.WriteString("origin", OriginName(row.Origin));
        WriteValues(writer, "values", row.Values);
        WriteValues(writer, "shipped", row.Shipped);
        writer.WriteString("intent", row.Edit is null ? null : BuiltInLibraryEditsJson.IntentName(row.Edit.Intent));
        if (row.Review is null)
        {
            writer.WriteNull("review");
        }
        else
        {
            writer.WriteStartObject("review");
            WriteValues(writer, "yours", row.Review.Yours);
            WriteValues(writer, "updatedBuiltIn", row.Review.UpdatedBuiltIn);
            WriteStrings(writer, "differing", Enum.GetValues<TermFields>().Where(field => field != TermFields.None && row.Review.Differing.HasFlag(field)).Select(FieldName));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteValues(Utf8JsonWriter writer, string name, TermValues? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteRawValue(Compact(values));
    }

    // One line per set of values keeps the file readable at a glance; the rest is indented.
    private static byte[] Compact(TermValues values)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("spoken", values.Spoken);
            writer.WriteString("written", values.Written);
            writer.WriteBoolean("wholeWord", values.WholeWord);
            writer.WriteBoolean("enabled", values.Enabled);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string VersionName(TermValues[] rows) => ShippedVersions.Single(version => ReferenceEquals(version.Rows, rows)).Name;

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
