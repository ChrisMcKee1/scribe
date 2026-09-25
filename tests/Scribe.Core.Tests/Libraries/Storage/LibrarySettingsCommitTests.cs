using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The settings side of a library commit (contract 3.1.2): the generation check and the one transaction (J-9), the
/// adoption's preserving patch of the enabled list (J-17, A5), and the list that moves only with the state row (J-22, A17).
/// </summary>
public sealed class LibrarySettingsCommitTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private SettingsRepository Settings => _fixture.Settings;

    private static LibrarySavePayload Payload(long expected, IReadOnlyList<string>? list, params (string Key, string? Value)[] rows) =>
        new(expected, expected + 1, list, [.. rows.Select(row => new LibrarySettingValue(row.Key, row.Value))]);

    private static string StateRow(params string[] projection) =>
        new JsonObject
        {
            ["version"] = 1,
            ["enabled"] = new JsonArray([.. projection.Select(id => (JsonNode?)id)]),
            ["legacyProjection"] = new JsonArray([.. projection.Select(id => (JsonNode?)id)]),
            ["aiPermissionsLost"] = false,
        }.ToJsonString();

    private string StoredDocument() => Settings.Get(SettingsRepository.SettingsKey)!;

    [Fact]
    public void A_generation_conflict_writes_nothing_at_all()
    {
        // J-9.
        _fixture.SaveEnabled("github");
        var document = StoredDocument();
        var dictionary = new DictionaryRepository(_fixture.Database);
        var settings = Settings.Load();
        settings.EnabledDictionaryLibraryIds = ["team-terms"];

        var conflict = Assert.Throws<LibraryGenerationConflictException>(() => Settings.SaveBundle(
            settings, [DictionaryEntry.New("kube", "Kubernetes")], null, default, Payload(4, ["team-terms"], (LibrarySettingKeys.State, StateRow("team-terms")))));

        Assert.Equal(0, conflict.Stored);
        Assert.Equal(4, conflict.Expected);
        Assert.Equal(document, StoredDocument());
        Assert.Empty(dictionary.GetAll());
        Assert.Null(Settings.Get(LibrarySettingKeys.State));
        Assert.Null(Settings.Get(LibrarySettingKeys.Generation));
    }

    [Fact]
    public void Rows_generation_and_the_documents_list_commit_together_and_the_caller_takes_the_stored_list()
    {
        // J-9.
        _fixture.SaveEnabled("github");
        var settings = Settings.Load();
        settings.EnabledDictionaryLibraryIds = ["what the window showed"];

        Settings.SaveBundle(settings, null, null, default, Payload(0, ["team-terms", "github"], (LibrarySettingKeys.State, StateRow("team-terms", "github")), (LibrarySettingKeys.FileIds, null)));

        Assert.Equal(1, _fixture.StoredGeneration);
        Assert.Equal(["team-terms", "github"], _fixture.StoredEnabledList());
        Assert.Equal(["team-terms", "github"], settings.EnabledDictionaryLibraryIds);
        Assert.Equal(StateRow("team-terms", "github"), Settings.Get(LibrarySettingKeys.State));
    }

    [Fact]
    public void A_Saves_state_row_stores_as_its_projection_exactly_the_list_the_document_gets()
    {
        // J-9, through the service: the row's legacyProjection equals the list written.
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team-terms", "github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();

        Changes.Save(service, Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, enable: ["microsoft-azure"], disable: ["github"])));

        var row = ContractComposer.ParseRow(Settings.Get(LibrarySettingKeys.State)!)!;
        Assert.Equal(_fixture.StoredEnabledList(), row.Projection);
        Assert.Contains("microsoft-azure", _fixture.StoredEnabledList());
        Assert.DoesNotContain("github", _fixture.StoredEnabledList());
    }

    [Fact]
    public void An_older_builds_Save_shape_leaves_every_library_row_untouched()
    {
        // J-9 (plan open risk 4): 0.4.3's SaveBundle carries no payload.
        Settings.SaveBundle(Settings.Load(), null, null, default, Payload(0, ["github"], (LibrarySettingKeys.State, StateRow("github")), (LibrarySettingKeys.FileIds, "{}")));
        var rows = (Settings.Get(LibrarySettingKeys.Generation), Settings.Get(LibrarySettingKeys.State), Settings.Get(LibrarySettingKeys.FileIds));

        Settings.SaveBundle(AppSettings.CreateDefault(), [DictionaryEntry.New("kube", "Kubernetes")], null, 0);
        Settings.Save(AppSettings.CreateDefault());
        Settings.Update(settings => settings.LaunchOnLogin = true);

        Assert.Equal(rows, (Settings.Get(LibrarySettingKeys.Generation), Settings.Get(LibrarySettingKeys.State), Settings.Get(LibrarySettingKeys.FileIds)));
    }

    [Fact]
    public void A_null_payload_is_exactly_the_save_without_one_and_the_step_names_are_raised_in_order()
    {
        var steps = new List<string>();
        Settings.WriteStep = (step, _, _) => steps.Add(step);

        Settings.SaveBundle(AppSettings.CreateDefault(), null, null, default, libraries: null);
        Settings.SaveBundle(AppSettings.CreateDefault(), null, null, default, Payload(0, ["github"], (LibrarySettingKeys.State, StateRow("github"))));
        Settings.CommitLibraryState(Payload(1, null, (LibrarySettingKeys.State, StateRow("github"))));

        Assert.Equal(
            ["save committing", "save committed", "library rows written", "save committing", "save committed",
             "library rows written", "library state committing", "library state committed"],
            steps);
    }

    [Fact]
    public void The_payload_guards_hold_a_bug_to_its_own_rows()
    {
        Assert.Throws<ArgumentException>(() => Payload(0, null, ("app_settings", "{}")));
        Assert.Throws<ArgumentException>(() => Payload(0, null, (LibrarySettingKeys.Generation, "9")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LibrarySavePayload(0, 2, null, []));
    }

    [Fact]
    public void An_adoption_patches_only_the_bytes_of_the_enabled_lists_value_and_the_older_reader_then_sees_it_off()
    {
        // J-17 (A5): an older build imported and enabled a library; this version adopts it without a Save.
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team-terms");
        _fixture.Service().LoadCatalog();

        // The older build's document, with a member this build does not know.
        var stored = JsonNode.Parse(StoredDocument())!.AsObject();
        stored["futureMember"] = new JsonObject { ["kept"] = "exactly \u00e9 as written" };
        stored["enabledDictionaryLibraryIds"] = new JsonArray("team-terms", "imported");
        var olderDocument = stored.ToJsonString();
        Settings.Set(SettingsRepository.SettingsKey, olderDocument);
        _fixture.Write("imported.csv", LibraryStorageFixture.Csv("Imported", ("secret", "PrivateCanary")));
        Assert.Contains(Legacy043LibrarySelection.EnabledEntries(["team-terms", "imported"], _fixture.LibrariesDir), entry => entry.Replacement == "PrivateCanary");

        _fixture.Restart();
        var catalog = _fixture.Service().LoadCatalog();

        Assert.False(catalog.LocalState.AiPermissions["imported"]);
        var patched = StoredDocument();
        AssertOnlyTheListChanged(olderDocument, patched);
        Assert.Equal("exactly \u00e9 as written", JsonNode.Parse(patched)!["futureMember"]!["kept"]!.GetValue<string>());
        Assert.DoesNotContain(Legacy043LibrarySelection.EnabledEntries(_fixture.StoredEnabledList(), _fixture.LibrariesDir), entry => entry.Replacement == "PrivateCanary");
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1,\"enabledDictionaryLibraryIds\":[\"x\"]}")]
    [InlineData("{}", "{\"enabledDictionaryLibraryIds\":[\"x\"]}")]
    [InlineData("{ \"EnabledDictionaryLibraryIds\" : [ \"old\" ] , \"b\" : 2 }", "{ \"EnabledDictionaryLibraryIds\" : [\"x\"] , \"b\" : 2 }")]
    [InlineData("{\"enabledDictionaryLibraryIds\":null}", "{\"enabledDictionaryLibraryIds\":[\"x\"]}")]
    public void The_patch_replaces_or_appends_the_one_value_and_leaves_every_other_byte(string document, string expected)
    {
        Assert.Equal(expected, SettingsRepository.PatchEnabledLibraryList(document, ["x"]));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"enabledDictionaryLibraryIds\":[],\"EnabledDictionaryLibraryIds\":[]}")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":1} trailing")]
    public void The_patch_refuses_a_document_it_cannot_place_the_member_in(string document)
    {
        Assert.Null(SettingsRepository.PatchEnabledLibraryList(document, ["x"]));
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("unreadable")]
    [InlineData("not an object")]
    [InlineData("twice")]
    [InlineData("none")]
    public void A_commit_that_would_patch_a_document_it_must_not_writes_nothing(string shape)
    {
        // J-17: a lost or unreadable document, one that is not a JSON object, or one holding the member twice.
        switch (shape)
        {
            case "lost":
                Settings.Set(SettingsRepository.LostMarkerKey, "lost");
                break;
            case "unreadable":
                Settings.Set(SettingsRepository.SettingsKey, "{ not json");
                break;
            case "not an object":
                Settings.Set(SettingsRepository.SettingsKey, "[1,2]");
                break;
            case "twice":
                Settings.Set(SettingsRepository.SettingsKey, "{\"enabledDictionaryLibraryIds\":[],\"EnabledDictionaryLibraryIds\":[]}");
                break;
        }

        var document = Settings.Get(SettingsRepository.SettingsKey);

        Assert.Throws<InvalidOperationException>(() => Settings.CommitLibraryState(Payload(0, ["github"], (LibrarySettingKeys.State, StateRow("github")))));

        Assert.Equal(document, Settings.Get(SettingsRepository.SettingsKey));
        Assert.Null(Settings.Get(LibrarySettingKeys.State));
        Assert.Null(Settings.Get(LibrarySettingKeys.Generation));
    }

    [Fact]
    public void A_commit_that_leaves_the_list_alone_never_reads_or_writes_the_document()
    {
        Settings.Set(SettingsRepository.SettingsKey, "{ not json");

        Settings.CommitLibraryState(Payload(0, null, (LibrarySettingKeys.State, StateRow())));

        Assert.Equal("{ not json", Settings.Get(SettingsRepository.SettingsKey));
        Assert.Equal(1, _fixture.StoredGeneration);
    }

    [Fact]
    public void Once_a_state_row_is_stored_whole_document_writes_keep_the_stored_list_whatever_they_carry()
    {
        // J-22 (A17): a window opened before an adoption patched the list saves its whole document.
        _fixture.SaveEnabled("github", "team-terms");
        var window = Settings.Load();
        Settings.CommitLibraryState(Payload(0, ["github"], (LibrarySettingKeys.State, StateRow("github"))));
        Assert.Equal(["github"], _fixture.StoredEnabledList());

        Settings.SaveBundle(window, null, null, 0);
        Assert.Equal(["github"], _fixture.StoredEnabledList());
        Assert.Equal(["github"], window.EnabledDictionaryLibraryIds);

        var stale = AppSettings.CreateDefault();
        stale.EnabledDictionaryLibraryIds = ["github", "team-terms"];
        Settings.Save(stale);
        Assert.Equal(["github"], _fixture.StoredEnabledList());

        var updated = Settings.Update(settings => settings.EnabledDictionaryLibraryIds = ["team-terms"]);
        Assert.Equal(["github"], _fixture.StoredEnabledList());
        Assert.Equal(["github"], updated.EnabledDictionaryLibraryIds);

        // A Save with a payload is the one whole-document write that changes it.
        Settings.SaveBundle(Settings.Load(), null, null, default, Payload(1, ["team-terms"], (LibrarySettingKeys.State, StateRow("team-terms"))));
        Assert.Equal(["team-terms"], _fixture.StoredEnabledList());
    }

    [Fact]
    public void With_no_state_row_stored_a_settings_only_Save_writes_the_windows_list_as_today()
    {
        // J-22, the interim parts' case: no state row is ever written, so the old window's switches work exactly as today.
        _fixture.SaveEnabled("github");
        var window = Settings.Load();
        window.EnabledDictionaryLibraryIds = ["team-terms"];

        Settings.SaveBundle(window, null, null, 0);

        Assert.Equal(["team-terms"], _fixture.StoredEnabledList());
    }

    [Fact]
    public void An_older_builds_change_to_the_list_reaches_both_twins_and_the_next_commit_writes_list_and_projection_together()
    {
        // J-22 with the twins: an older build's Save shape turns github off, then on.
        _fixture.Write("github.csv", LibraryStorageFixture.Csv("Twin", ("private codename", "Nightjar")));
        _fixture.SaveEnabled("github");
        var catalog = _fixture.Service().LoadCatalog();
        Assert.Contains("custom-github", catalog.LocalState.EnabledIds);
        Assert.Contains("github", catalog.LocalState.EnabledIds);

        OlderBuildSaves(_ => []);
        _fixture.Restart();
        var off = _fixture.Service().LoadCatalog();
        Assert.DoesNotContain("github", off.LocalState.EnabledIds);
        Assert.DoesNotContain("custom-github", off.LocalState.EnabledIds);

        var service = _fixture.Service();
        Changes.Save(service, Settings, Changes.Of(off, state: Changes.With(off.LocalState, enable: ["microsoft-azure"])));
        Assert.Equal(_fixture.StoredEnabledList(), ContractComposer.ParseRow(Settings.Get(LibrarySettingKeys.State)!)!.Projection);

        var twinChoice = service.LoadCatalog().LocalState.AiPermissions.GetValueOrDefault("custom-github");
        OlderBuildSaves(list => [.. list, "github"]);
        _fixture.Restart();
        var on = _fixture.Service().LoadCatalog();
        Assert.Contains("github", on.LocalState.EnabledIds);
        Assert.Contains("custom-github", on.LocalState.EnabledIds);
        Assert.Equal(twinChoice, on.LocalState.AiPermissions.GetValueOrDefault("custom-github"));
    }

    // 0.4.3's Save: the whole document with its own list, no library payload and no notion of the state row.
    private void OlderBuildSaves(Func<IReadOnlyList<string>, IReadOnlyList<string>> list)
    {
        var document = JsonNode.Parse(StoredDocument())!.AsObject();
        var current = document["enabledDictionaryLibraryIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        document["enabledDictionaryLibraryIds"] = new JsonArray([.. list(current).Select(id => (JsonNode?)id)]);
        Settings.Set(SettingsRepository.SettingsKey, document.ToJsonString());
    }

    private static void AssertOnlyTheListChanged(string before, string after)
    {
        var start = before.IndexOf("\"enabledDictionaryLibraryIds\":", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var valueStart = start + "\"enabledDictionaryLibraryIds\":".Length;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(before[valueStart..]));
        reader.Read();
        reader.Skip();
        var valueEnd = valueStart + Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(before[valueStart..]), 0, (int)reader.BytesConsumed).Length;
        Assert.StartsWith(before[..valueStart], after, StringComparison.Ordinal);
        Assert.EndsWith(before[valueEnd..], after, StringComparison.Ordinal);
        Assert.NotEqual(before, after);
    }
}
