using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries;

/// <summary>
/// The small amount of behaviour the shared library contract types carry themselves: id comparison, the local state's
/// normalization, lookups and the guards that keep a commit payload to the libraries' own rows. Everything else in
/// those types is data; its rules belong to the streams that produce it.
/// </summary>
public sealed class LibraryContractTests
{
    [Fact]
    public void Term_values_map_to_and_from_dictionary_entries()
    {
        var values = new TermValues("get hub", "GitHub", WholeWord: false, Enabled: false);

        var entry = values.ToEntry();

        Assert.Equal(new DictionaryEntry(0, "get hub", "GitHub", false, false), entry);
        Assert.Equal(values, TermValues.FromEntry(entry));
        Assert.Equal(new TermValues(string.Empty, string.Empty), TermValues.FromEntry(new DictionaryEntry(7, null!, null!)));
    }

    [Fact]
    public void A_custom_row_is_keyed_by_its_spoken_form()
    {
        var row = LibraryRow.Custom(new TermValues("  Get Hub ", "GitHub"));
        var legacy = LibraryRow.Custom(new TermValues("get  hub", "GitHub"));

        Assert.Equal(LibraryTermKey.From("get hub"), row.Key);
        Assert.Equal(TermOrigin.Custom, row.Origin);
        Assert.Null(row.Shipped);
        Assert.Null(row.Edit);
        Assert.Null(row.Review);

        // An older file's row with a double space keeps its own key, as 0.4.3 did (review finding A10).
        Assert.NotEqual(row.Key, legacy.Key);
        Assert.Equal("get  hub", legacy.Key.Value);
    }

    [Fact]
    public void Legacy_markers_compare_library_ids_without_case_and_keys_by_term()
    {
        var marker = new LegacyMarker("Team-Terms", LibraryTermKey.From("get hub"));

        Assert.Equal(marker, new LegacyMarker("team-terms", LibraryTermKey.From(" GET HUB ")));
        Assert.Equal(marker.GetHashCode(), new LegacyMarker("team-terms", LibraryTermKey.From(" GET HUB ")).GetHashCode());
        Assert.NotEqual(marker, new LegacyMarker("team-terms", LibraryTermKey.From("GET  HUB")));
        Assert.NotEqual(marker, new LegacyMarker("team-terms-2", LibraryTermKey.From("get hub")));
        Assert.NotEqual(marker, new LegacyMarker("team-terms", LibraryTermKey.From("kube")));
    }

    [Fact]
    public void Local_state_compares_ids_without_case_and_drops_blanks_and_repeats()
    {
        var state = LibraryLocalState.Create(
            enabledIds: ["GitHub", " team-terms ", null, "  "],
            legacyEnabledIds: ["github"],
            aiPermissions: [new("GitHub", true), new("github", false), new(" ", true)],
            legacyMarkers:
            [
                new LegacyMarker("team-terms", LibraryTermKey.From("get hub")),
                new LegacyMarker("TEAM-TERMS", LibraryTermKey.From("Get Hub")),
                new LegacyMarker(" ", LibraryTermKey.From("kube")),
                new LegacyMarker("team-terms", LibraryTermKey.Empty),
            ],
            aiUpgradeNotice: ["Team-Terms"],
            LocalStateHealth.Ok);

        Assert.Equal(2, state.EnabledIds.Count);
        Assert.Contains("github", state.EnabledIds);
        Assert.Contains("TEAM-TERMS", state.EnabledIds);
        Assert.Contains("GITHUB", state.LegacyEnabledIds);
        Assert.Single(state.AiPermissions);
        Assert.False(state.AiPermissions["GITHUB"]);
        Assert.Single(state.LegacyMarkers);
        Assert.Equal("team-terms", state.LegacyMarkers[0].LibraryId);
        Assert.Contains("team-terms", state.AiUpgradeNotice);
        Assert.Equal(LocalStateHealth.Ok, state.Health);
    }

    [Fact]
    public void The_absent_state_has_nothing_in_it()
    {
        var state = LibraryLocalState.Absent;

        Assert.Empty(state.EnabledIds);
        Assert.Empty(state.LegacyEnabledIds);
        Assert.Empty(state.AiPermissions);
        Assert.Empty(state.LegacyMarkers);
        Assert.Empty(state.AiUpgradeNotice);
        Assert.Empty(state.AcceptedContent);
        Assert.False(state.AiPermissionsLost);
        Assert.Equal(LocalStateHealth.Absent, state.Health);
    }

    [Fact]
    public void Local_state_binds_content_by_id_without_case_and_keeps_a_lost_permission_state()
    {
        var first = new LibraryContentHash(new string('a', 64));
        var second = new LibraryContentHash(new string('b', 64));
        var state = LibraryLocalState.Create(
            enabledIds: null,
            legacyEnabledIds: null,
            aiPermissions: null,
            legacyMarkers: null,
            aiUpgradeNotice: null,
            LocalStateHealth.Ok,
            acceptedContent: [new(" Team-Terms ", first), new("team-terms", second), new(" ", first), new("github", first)],
            aiPermissionsLost: true);

        Assert.Equal(2, state.AcceptedContent.Count);
        Assert.Equal(second, state.AcceptedContent["TEAM-TERMS"]);
        Assert.Equal(first, state.AcceptedContent["GitHub"]);
        Assert.True(state.AiPermissionsLost);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, LibraryContentHash>)state.AcceptedContent).Add("x", first));
    }

    [Fact]
    public void The_state_context_says_a_first_start_only_when_no_witness_says_otherwise()
    {
        var firstStart = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: false);
        var witnessed = firstStart with { CommitWitnessed = true };

        Assert.False(firstStart.CommitWitnessed);
        Assert.True(witnessed.CommitWitnessed);
        Assert.NotEqual(firstStart, witnessed);
        Assert.Equal(LibraryAdoptionReasons.None, default(LibraryAdoptionReasons));
        Assert.True((LibraryAdoptionReasons.FirstStart | LibraryAdoptionReasons.Discovered).HasFlag(LibraryAdoptionReasons.Discovered));
    }

    [Fact]
    public void A_catalog_carries_the_versions_kept_this_session_and_why_a_save_is_unfinished()
    {
        var kept = new List<LibraryKeptVersion>
        {
            new("team-terms", LibraryKeptVersionKind.OutsideVersion, "team-terms-changed-outside"),
        };
        var plain = new LibraryCatalog(2, [], LibraryLocalState.Absent, [], [], filesAwaitingRelease: 0);
        var pending = new LibraryCatalog(
            3, [], LibraryLocalState.Absent, [], [], filesAwaitingRelease: 1, kept, LibraryIoFailure.SharingViolation);

        kept.Clear();

        Assert.Empty(plain.KeptVersions);
        Assert.Equal(LibraryIoFailure.None, plain.PendingFailure);
        Assert.Single(pending.KeptVersions);
        Assert.Equal(LibraryIoFailure.SharingViolation, pending.PendingFailure);
        Assert.Equal(1, pending.FilesAwaitingRelease);
    }

    [Fact]
    public void Set_aside_journal_files_are_kept_as_long_as_damaged_database_copies()
    {
        Assert.Equal(Scribe.Core.Persistence.StorageRetentionPolicy.DamagedCopyRetentionDays, LibraryLimits.QuarantineRetentionDays);
    }

    [Fact]
    public void Catalog_and_draft_find_libraries_by_id_without_case()
    {
        var github = Library("github", builtIn: true);
        var team = Library("team-terms", builtIn: false);
        var catalog = new LibraryCatalog(
            3,
            [new CatalogLibrary(github, LibraryFileState.Available, null, null), new CatalogLibrary(team, LibraryFileState.Available, "team-terms.csv", null)],
            LibraryLocalState.Absent,
            [],
            [],
            filesAwaitingRelease: 0);
        var draft = new LibraryDraft(
            9,
            3,
            [new DraftLibrary(team, LibraryOrigin.Existing, LibraryFileState.Available)],
            LibraryLocalState.Absent,
            []);

        Assert.Same(team, catalog.Find("TEAM-TERMS")?.Content);
        Assert.Same(github, catalog.Find("GitHub")?.Content);
        Assert.Null(catalog.Find("missing"));
        Assert.Null(catalog.Find(null));
        Assert.Same(team, draft.Find("Team-Terms")?.Content);
        Assert.Null(draft.Find("github"));
        Assert.Equal(3, catalog.Generation);
        Assert.Equal(9, draft.Revision);
        Assert.Equal(3, draft.BaseGeneration);
    }

    [Fact]
    public void A_change_set_is_empty_only_when_it_changes_nothing()
    {
        var empty = new LibraryChangeSet(4, 12, [], [], [], LibraryLocalState.Absent, localStateChanged: false);
        var stateOnly = new LibraryChangeSet(4, 12, [], [], [], LibraryLocalState.Absent, localStateChanged: true);
        var deletion = new LibraryChangeSet(
            4, 12, [], [new LibraryDeletion("team-terms", "team-terms.csv", new LibraryContentHash(new string('a', 64)))], [],
            LibraryLocalState.Absent, localStateChanged: false);

        Assert.True(empty.IsEmpty);
        Assert.False(stateOnly.IsEmpty);
        Assert.False(deletion.IsEmpty);
    }

    [Fact]
    public void A_commit_payload_moves_the_generation_by_one_and_writes_only_library_rows()
    {
        var payload = new LibrarySavePayload(4, 5, ["github"], [new LibrarySettingValue(LibrarySettingKeys.State, "{}")]);
        var prepared = new PreparedLibrarySave(draftRevision: 12, payload, new LibraryChangeCounts(1, 0, 0, 0, 3));

        Assert.Equal(4, prepared.BaseGeneration);
        Assert.Equal(5, prepared.Generation);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LibrarySavePayload(4, 6, null, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LibrarySavePayload(-1, 0, null, []));
        Assert.Throws<ArgumentException>(() => new LibrarySavePayload(4, 5, null, [new LibrarySettingValue("app_settings", "{}")]));
        Assert.Throws<ArgumentException>(() => new LibrarySavePayload(4, 5, null, [new LibrarySettingValue(LibrarySettingKeys.Generation, "9")]));
        Assert.Throws<ArgumentException>(() => new LibrarySavePayload(4, 5, null, [new LibrarySettingValue("Libraries.state", "{}")]));
    }

    [Fact]
    public void Every_library_settings_key_carries_the_library_prefix()
    {
        foreach (var key in new[] { LibrarySettingKeys.Generation, LibrarySettingKeys.State, LibrarySettingKeys.FileIds })
        {
            Assert.StartsWith(LibrarySettingKeys.Prefix, key, StringComparison.Ordinal);
        }
    }

    // CommitUnknown (review finding A8 on the storage stream) was appended, so every earlier status keeps its number.
    [Fact]
    public void Save_statuses_are_only_ever_appended()
    {
        var expected = new (LibrarySaveStatus Status, int Number)[]
        {
            (LibrarySaveStatus.Applied, 0),
            (LibrarySaveStatus.AppliedAwaitingRelease, 1),
            (LibrarySaveStatus.NotCommitted, 2),
            (LibrarySaveStatus.Superseded, 3),
            (LibrarySaveStatus.CommitUnknown, 4),
        };

        Assert.Equal(expected, Enum.GetValues<LibrarySaveStatus>().Select(status => (status, (int)status)).ToArray());
    }

    [Fact]
    public void The_empty_vocabulary_permits_nothing()
    {
        Assert.Empty(LibraryVocabulary.Empty.Entries);
        Assert.Empty(LibraryVocabulary.Empty.AiEntries);
        Assert.Empty(LibraryVocabulary.Empty.AiScope.PermittedLibraryIds);
        Assert.Same(AiVocabularyScope.None, LibraryVocabulary.Empty.AiScope);
        Assert.Contains("github", new AiVocabularyScope(2, [new("GitHub", null)]).PermittedLibraryIds);
    }

    [Fact]
    public void Containers_keep_their_own_copies_of_the_lists_they_are_given()
    {
        var libraries = new List<CatalogLibrary>
        {
            new(Library("team-terms", builtIn: false), LibraryFileState.Available, "team-terms.csv", null),
        };
        var entries = new List<DictionaryEntry> { DictionaryEntry.New("kube", "Kubernetes") };
        var permitted = new List<KeyValuePair<string, LibraryContentHash?>> { new("team-terms", new LibraryContentHash(new string('c', 64))) };
        var catalog = new LibraryCatalog(1, libraries, LibraryLocalState.Absent, [], [], filesAwaitingRelease: 0);
        var vocabulary = new LibraryVocabulary(1, entries, entries, new AiVocabularyScope(1, permitted));

        libraries.Clear();
        entries.Clear();
        permitted.Clear();

        Assert.Single(catalog.Libraries);
        Assert.NotNull(catalog.Find("team-terms"));
        Assert.Single(vocabulary.Entries);
        Assert.Single(vocabulary.AiEntries);
        Assert.Contains("team-terms", vocabulary.AiScope.PermittedLibraryIds);
        Assert.Throws<NotSupportedException>(() => ((ICollection<CatalogLibrary>)catalog.Libraries).Clear());
        Assert.Throws<NotSupportedException>(() => ((ICollection<DictionaryEntry>)vocabulary.Entries).Clear());
    }

    private static LibraryContent Library(string id, bool builtIn) =>
        new(id, builtIn, id, builtIn ? "General" : "Custom", null, [LibraryRow.Custom(new TermValues("a", "A"))]);
}
