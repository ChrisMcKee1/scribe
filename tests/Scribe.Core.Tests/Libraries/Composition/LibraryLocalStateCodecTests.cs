using System.Text.Json;
using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The <c>libraries.state</c> row (W1b contracts 6.2, acceptance C-7): its round trip, its required members, and every
/// row this version must not read as a healthy state.
/// </summary>
public sealed class LibraryLocalStateCodecTests
{
    private static readonly LibraryStateContext Stored = new(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: true, CommitWitnessed: true);

    private static readonly IReadOnlyList<LibraryIdentity> Libraries =
    [
        BuiltInIdentity("github"), BuiltInIdentity("microsoft-365"), CustomIdentity("team-terms"), CustomIdentity("Zulu Notes"),
        CustomIdentity("custom-private", "private.csv"),
    ];

    [Fact]
    public void A_state_reads_back_as_it_was_encoded()
    {
        var state = State(
            enabled: ["github", "team-terms", "Zulu Notes", "custom-private"],
            ai: [("team-terms", true), ("Zulu Notes", true), ("custom-private", false), ("github", true)],
            markers: [("team-terms", "get hub"), ("Zulu Notes", "north  star")],
            accepted: [("team-terms", H1), ("Zulu Notes", H2), ("custom-private", H3), ("github", H4)],
            lost: true,
            notice: ["team-terms"]);

        var encoded = LibraryComposer.Instance.EncodeLocalState(state, null, Libraries, Libraries);
        var read = LibraryComposer.Instance.ReadLocalState(encoded.EnabledLibraryIds, encoded.StateValue, Libraries, Stored);

        Assert.Equal(LocalStateHealth.Ok, read.Health);
        Assert.Equal(state.EnabledIds.Order(), read.EnabledIds.Order());
        Assert.Equal(state.AiPermissions.OrderBy(p => p.Key), read.AiPermissions.OrderBy(p => p.Key));
        Assert.Equal(state.LegacyMarkers, read.LegacyMarkers);
        Assert.Equal("north  star", read.LegacyMarkers[1].Key.Value);
        Assert.Equal(state.AcceptedContent.OrderBy(p => p.Key), read.AcceptedContent.OrderBy(p => p.Key));
        Assert.Equal(state.AiUpgradeNotice, read.AiUpgradeNotice);
        Assert.True(read.AiPermissionsLost);
        Assert.Equal(encoded.EnabledLibraryIds.Order(), read.LegacyEnabledIds.Order());
    }

    [Fact]
    public void An_enabled_library_kept_from_AI_cleanup_stays_enabled_and_out_of_the_projection()
    {
        var state = State(enabled: ["team-terms", "custom-private"], ai: [("team-terms", true), ("custom-private", false)],
            accepted: [("team-terms", H1), ("custom-private", H2)]);

        var encoded = LibraryComposer.Instance.EncodeLocalState(state, null, Libraries, Libraries);

        Assert.Equal(["team-terms"], encoded.EnabledLibraryIds);
        using var row = JsonDocument.Parse(encoded.StateValue!);
        Assert.Equal(1, row.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["custom-private", "team-terms"], Strings(row.RootElement.GetProperty("enabled")));   // private.csv ranks first
        Assert.Equal(["team-terms"], Strings(row.RootElement.GetProperty("legacyProjection")));
        Assert.Equal(["team-terms"], Strings(row.RootElement.GetProperty("ai").GetProperty("on")));
        Assert.Equal(["custom-private"], Strings(row.RootElement.GetProperty("ai").GetProperty("off")));
        Assert.False(row.RootElement.GetProperty("aiPermissionsLost").GetBoolean());
    }

    [Theory]
    [InlineData("{\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":\"1\",\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1.5,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":0,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":null,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":\"false\",\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":null,\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":{},\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[1],\"legacyProjection\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":\"github\"}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"ai\":{\"on\":[\"Team\"],\"off\":[\"team\"]}}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"ai\":[\"team\"]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"ai\":{\"on\":\"team\"}}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"ai\":{\"on\":[],\"on\":[\"team\"]}}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"accepted\":{\"team\":\"ABCDEF\"}}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"accepted\":{\"team\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"accepted\":{\"team\":5}}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"legacyMarkers\":[{\"library\":1,\"key\":\"x\"}]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"legacyMarkers\":[\"x\"]}")]
    [InlineData("{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[],\"upgradeNotice\":[true]}")]
    [InlineData("{\"version\":1,\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[],\"legacyProjection\":[]}")]
    [InlineData("[1]")]
    [InlineData("\"state\"")]
    [InlineData("{\"version\":1,")]
    [InlineData("")]
    public void A_row_this_version_cannot_read_exactly_is_unreadable_and_permits_nothing(string value)
    {
        var read = LibraryComposer.Instance.ReadLocalState(["github"], value, Libraries, Stored);

        Assert.Equal(LocalStateHealth.Unreadable, read.Health);
        Assert.Empty(read.AiPermissions);
        Assert.False(AiVocabularyPolicy.IsPermitted(read, "github", builtIn: true, content: null));
        Assert.Equal(["github"], read.EnabledIds);
    }

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":2,\"aiPermissionsLost\":\"no\",\"enabled\":7}")]
    [InlineData("{\"version\":99,\"aiPermissionsLost\":false,\"enabled\":[\"team-terms\"],\"legacyProjection\":[]}")]
    public void A_row_from_a_newer_version_is_newer_and_never_written(string value)
    {
        var read = LibraryComposer.Instance.ReadLocalState(["github", "gone"], value, Libraries, Stored);

        Assert.Equal(LocalStateHealth.Newer, read.Health);
        Assert.False(AiVocabularyPolicy.IsPermitted(read, "github", builtIn: true, content: null));
        var encoded = LibraryComposer.Instance.EncodeLocalState(read, null, Libraries, Libraries);
        Assert.Null(encoded.StateValue);
        Assert.Equal(["github", "gone"], encoded.EnabledLibraryIds);
    }

    [Fact]
    public void Optional_members_read_as_empty_unknown_members_are_ignored_and_empty_markers_are_dropped()
    {
        const string value =
            "{\"version\":1,\"aiPermissionsLost\":false,\"enabled\":[\"team-terms\"],\"legacyProjection\":[\"team-terms\"]," +
            "\"ai\":null,\"futureMember\":{\"anything\":[1,2]},\"legacyMarkers\":[{\"library\":\"\",\"key\":\"x\"}," +
            "{\"library\":\"team-terms\",\"key\":\"  \"},{\"library\":\"team-terms\"},{\"library\":\"team-terms\",\"key\":\"kube\"}]}";

        var read = LibraryComposer.Instance.ReadLocalState(["team-terms"], value, Libraries, Stored);

        Assert.Equal(LocalStateHealth.Ok, read.Health);
        Assert.Equal(["team-terms"], read.EnabledIds);
        Assert.Empty(read.AiPermissions);
        Assert.Empty(read.AcceptedContent);
        Assert.Empty(read.AiUpgradeNotice);
        Assert.Equal([new LegacyMarker("team-terms", Key("kube"))], read.LegacyMarkers);
    }

    [Fact]
    public void Ids_no_library_has_leave_the_stored_state_and_stay_in_the_documents_list()
    {
        // 6.2: at a commit, ids with no library leave enabled, ai, legacyMarkers, accepted and upgradeNotice; a legacy id no
        // library has stays in the document's list if it was there, as 0.4.3 keeps it.
        var state = State(
            enabled: ["team-terms", "gone"],
            ai: [("team-terms", true), ("gone", true)],
            markers: [("gone", "kube"), ("team-terms", "get hub")],
            accepted: [("team-terms", H1), ("gone", H2)],
            notice: ["gone", "team-terms"],
            legacy: ["team-terms", "gone", "Old Library"]);

        var encoded = LibraryComposer.Instance.EncodeLocalState(state, null, Libraries, Libraries);

        Assert.Equal(["team-terms", "gone", "Old Library"], encoded.EnabledLibraryIds);
        using var row = JsonDocument.Parse(encoded.StateValue!);
        Assert.Equal(["team-terms"], Strings(row.RootElement.GetProperty("enabled")));
        Assert.Equal(["team-terms"], Strings(row.RootElement.GetProperty("ai").GetProperty("on")));
        Assert.Equal(["team-terms"], row.RootElement.GetProperty("accepted").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["team-terms"], Strings(row.RootElement.GetProperty("upgradeNotice")));
        Assert.Single(row.RootElement.GetProperty("legacyMarkers").EnumerateArray());
        Assert.Equal(["team-terms", "gone", "Old Library"], Strings(row.RootElement.GetProperty("legacyProjection")));
    }

    [Fact]
    public void An_absent_row_is_a_first_start_only_when_nothing_says_a_row_was_lost()
    {
        // C-5b: each of the four context flags alone, the witness included, turns an absent row into a lost one.
        LibraryStateContext[] lost =
        [
            new(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false),
            new(RunningOnDefaults: false, DatabaseRepaired: true, GenerationStored: false),
            new(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: true),
            new(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: false, CommitWitnessed: true),
        ];
        foreach (var context in lost)
        {
            var read = LibraryComposer.Instance.ReadLocalState(["github", "team-terms"], null, Libraries, context);
            Assert.Equal(LocalStateHealth.Unreadable, read.Health);
            Assert.False(AiVocabularyPolicy.IsPermitted(read, "github", true, null));
        }

        var firstStart = LibraryComposer.Instance.ReadLocalState(["github"], null, Libraries, new LibraryStateContext(false, false, false));
        Assert.Equal(LocalStateHealth.Absent, firstStart.Health);
        Assert.True(AiVocabularyPolicy.IsPermitted(firstStart, "github", true, null));
    }

    [Fact]
    public void Reading_never_throws_on_what_it_is_given()
    {
        var random = new Random(20260928);
        const string alphabet = "{}[]\":,01aefrstulnv \\";
        for (var i = 0; i < 2000; i++)
        {
            var value = new string([.. Enumerable.Range(0, random.Next(0, 60)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            var read = LibraryComposer.Instance.ReadLocalState(random.Next(2) == 0 ? null : ["github"], value, Libraries, Stored);
            Assert.NotEqual(LocalStateHealth.Absent, read.Health);
        }
    }

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!);
}
