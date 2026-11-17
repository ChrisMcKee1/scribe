using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// The Settings Save protocol over the real library service, journal and settings store, settled the way the window settles
/// it: through the interface's default settlement, a second completion of the same preparation. What recovery did with a
/// Save's own manifest decides where that Save stands, never the generation stored now (F-11, R16b), and a commit that fails
/// after its COMMIT still puts the stored settings into effect (R05a).
/// </summary>
public sealed class WordPackSaveRealStoreTests : IDisposable
{
    private const string TeamTerms = "# name: Team terms\npattern,replacement\nkube,Kubernetes\n";

    private readonly RealLibraries _libraries = new();

    public void Dispose() => _libraries.Dispose();

    private LibraryStorageFixture Fixture => _libraries.Fixture;

    private Rig OpenEditing()
    {
        Fixture.Write("team-terms.csv", TeamTerms);
        Fixture.SaveEnabled("team-terms");
        var settings = new SettingsAfterCommit(Fixture.Settings);
        var service = _libraries.Service(settings: settings);
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        Assert.True(workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s")).Applied);
        return new Rig(service, settings, workspace);
    }

    private Rig OpenCreating()
    {
        Fixture.SaveEnabled();
        var settings = new SettingsAfterCommit(Fixture.Settings);
        var service = _libraries.Service(settings: settings);
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        var created = workspace.CreateLibrary();
        Assert.True(workspace.Rename(created, "Release notes").Applied);
        Assert.True(workspace.AddTerm(created, new TermValues("sprint", "Sprint")).Applied);
        workspace.SetEnabled(created, true);
        return new Rig(service, settings, workspace);
    }

    private string[] RedoImages() =>
        Directory.Exists(Fixture.Paths.LibraryJournalDir)
            ? [.. Directory.GetDirectories(Fixture.Paths.LibraryJournalDir)
                .SelectMany(folder => Directory.GetFiles(folder, "*" + LibraryJournalNames.RedoSuffix))]
            : [];

    private string[] Manifests() =>
        Directory.GetFiles(Fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix);

    private void DamageRedoImages(string damage)
    {
        var images = RedoImages();
        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            if (damage == "missing redo image")
            {
                File.Delete(image);
            }
            else
            {
                File.WriteAllBytes(image, [1, 2, 3, 4]);
            }
        }
    }

    private FileStream HoldTeamTerms() =>
        new(Fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.Read);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_superseded_reference_repair_rebases_without_losing_edits_and_the_next_Save_succeeds(bool afterCatalog)
    {
        var rig = OpenCreating();
        var original = Assert.Single(rig.Workspace.Draft.Libraries, library => !library.Content.BuiltIn).Content.Id;
        var copy = rig.Workspace.Duplicate(original);
        rig.Store.BeforeComplete = () =>
        {
            rig.Store.BeforeComplete = null;
            Fixture.Write(original + ".csv", "# name: Outside\npattern,replacement\noutside,Outside\n");
        };
        FileStream? hold = null;
        rig.Store.BeforeRepairCommit = payload =>
        {
            rig.Store.BeforeRepairCommit = null;
            Assert.True(rig.Workspace.Rename(copy, "Later draft name").Applied);
            Assert.True(rig.Workspace.AddTerm(copy, new TermValues("later", "Later")).Applied);
            if (afterCatalog)
            {
                hold = new FileStream(Fixture.PathOf(copy + ".csv"), FileMode.Open, FileAccess.Read, FileShare.Read);
                rig.Store.BeforeCatalogLoad = () =>
                {
                    hold.Dispose();
                    DamageRedoImages("missing redo image");
                };
            }
            else
            {
                Fixture.Settings.Set(LibrarySettingKeys.Generation,
                    (payload.Generation + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        };

        WordPackSaveProtocolResult first;
        try
        {
            first = await rig.Protocol.SaveAsync(rig.Request());
        }
        finally
        {
            hold?.Dispose();
        }

        Assert.False(first.Success);
        Assert.Contains("reference", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.True(rig.Workspace.HasPendingReferenceRepairs);
        Assert.True(rig.Workspace.HasUnsavedChanges);
        var draft = rig.Workspace.Draft.Find(copy)!;
        Assert.Equal("Later draft name", draft.Content.Name);
        Assert.Contains(draft.Content.Rows, row => row.Values == new TermValues("later", "Later"));
        Assert.NotEqual(original, draft.Content.BasedOn);
        Assert.Equal(Fixture.StoredGeneration, rig.Workspace.Draft.BaseGeneration);
        Assert.Equal(Fixture.StoredGeneration, rig.RefreshedGenerations[^1]);
        Assert.Equal(LibrarySaveStatus.Superseded, rig.Service.CompleteSave(rig.Store.Prepared[1]).Status);

        rig.RetentionDays = 17;
        var again = await rig.Protocol.SaveAsync(rig.Request());

        Assert.True(again.Success, again.Message);
        Assert.Equal(17, Fixture.Settings.Load().HistoryRetentionDays);
        Assert.False(rig.Workspace.HasUnsavedChanges);
        Assert.False(rig.Workspace.HasPendingReferenceRepairs);
        var saved = rig.Service.LoadCatalog().Find(copy)!.Content;
        Assert.Equal(draft.Content.BasedOn, saved.BasedOn);
        Assert.Equal("Later draft name", saved.Name);
        Assert.Contains(saved.Rows, row => row.Values == new TermValues("later", "Later"));
    }

    // A receipt that is only AppliedAwaitingRelease is revisable: the catalog read the protocol makes next runs a recovery
    // attempt, which here sets the manifest aside (another app lets go of the file, and the redo image the install needs is
    // gone before recovery can install it). The draft is marked saved on what the Save is asked after that read, so its
    // edit stays unsaved over files that were never replaced, never consumed against a catalog that holds none of it.
    [Theory]
    [InlineData("missing redo image")]
    [InlineData("corrupt redo image")]
    public async Task A_Save_completed_awaiting_release_whose_manifest_the_catalog_read_sets_aside_keeps_its_edit_unsaved(string damage)
    {
        var rig = OpenEditing();
        var hold = HoldTeamTerms();
        rig.Store.BeforeCatalogLoad = () =>
        {
            hold.Dispose();
            DamageRedoImages(damage);
        };

        WordPackSaveProtocolResult result;
        try
        {
            result = await rig.Protocol.SaveAsync(rig.Request());
        }
        finally
        {
            hold.Dispose();
        }

        var prepared = Assert.Single(rig.Store.Prepared);
        Assert.False(result.Success);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.True(rig.Workspace.HasUnsavedChanges);
        Assert.Equal("K8s", RealLibraries.Row(rig.Workspace, "team-terms", "kube").Row.Values.Written);
        Assert.Equal(TeamTerms, Fixture.Read("team-terms.csv"));
        Assert.Empty(Manifests());
        Assert.Equal(LibrarySaveStatus.Superseded, rig.Service.CompleteSave(prepared).Status);

        // The settings the Save stored in the same transaction took effect once, whatever became of the word pack files.
        Assert.Single(rig.AppliedRetention);

        // The edit was rebased onto what is stored, so saving again installs it.
        var again = await rig.Protocol.SaveAsync(rig.Request());

        Assert.True(again.Success, again.Message);
        Assert.False(rig.Workspace.HasUnsavedChanges);
        Assert.Equal("K8s", rig.Service.LoadCatalog().Find("team-terms")!.Content.Rows.Single().Values.Written);
    }

    [Theory]
    [InlineData("missing redo image")]
    [InlineData("corrupt redo image")]
    public async Task A_settlement_that_finds_a_manifest_unresolved_keeps_the_edit_unsaved_when_the_catalog_read_sets_it_aside(string damage)
    {
        var rig = OpenEditing();
        rig.Settings.FailAfterNextCommit = true;
        var unknown = await rig.Protocol.SaveAsync(rig.Request());
        Assert.False(unknown.Success);
        Assert.True(rig.Protocol.HasPendingSave);
        rig.Settings.Failing = false;

        // Recovery finishes what it can while another app holds the file and leaves the manifest unresolved, so the receipt
        // the settlement reads first is AppliedAwaitingRelease; the catalog read then sets the manifest aside.
        var hold = HoldTeamTerms();
        rig.Store.BeforeCatalogLoad = () =>
        {
            hold.Dispose();
            DamageRedoImages(damage);
        };

        WordPackSaveProtocolResult settled;
        try
        {
            settled = await rig.Protocol.TrySettlePendingAsync(rig.Request());
        }
        finally
        {
            hold.Dispose();
        }

        Assert.False(settled.Success);
        Assert.Contains("weren't saved", settled.Message, StringComparison.Ordinal);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.True(rig.Workspace.HasUnsavedChanges);
        Assert.Equal("K8s", RealLibraries.Row(rig.Workspace, "team-terms", "kube").Row.Values.Written);
        Assert.Equal(TeamTerms, Fixture.Read("team-terms.csv"));
        Assert.Empty(Manifests());
        Assert.Equal(LibrarySaveStatus.Superseded, rig.Service.CompleteSave(Assert.Single(rig.Store.Prepared)).Status);
    }

    [Fact]
    public async Task A_commit_that_fails_after_its_COMMIT_puts_the_stored_settings_into_effect_through_the_libraries_receipt()
    {
        // SQLite has committed the document and the library payload when the hook throws, so the commit call fails after the
        // Save stored everything: the settings were stored, and the library completes the Save as committed.
        var rig = OpenEditing();
        rig.RetentionDays = 0;
        Fixture.Settings.WriteStep = (step, _, _) =>
        {
            if (step == "save committed")
            {
                throw FaultingFileSystem.Injected();
            }
        };

        WordPackSaveProtocolResult result;
        try
        {
            result = await rig.Protocol.SaveAsync(rig.Request());
        }
        finally
        {
            Fixture.Settings.WriteStep = null;
        }

        Assert.Equal(0, Fixture.Settings.Load().HistoryRetentionDays);
        Assert.Equal("K8s", rig.Service.LoadCatalog().Find("team-terms")!.Content.Rows.Single().Values.Written);

        // What dictation runs on follows what is stored: the retention the Save stored was applied once, and the word pack
        // change stands. The Save is reported by the error after the word pack save, never as a failed Save.
        Assert.Equal([0], rig.AppliedRetention);
        Assert.False(result.Success);
        Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
        Assert.False(rig.Workspace.HasUnsavedChanges);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.False(rig.Protocol.ApplicationOwed);
    }

    [Theory]
    [InlineData("missing redo image")]
    [InlineData("corrupt redo image")]
    [InlineData("rival manifest of the same generation")]
    public async Task A_Save_whose_manifest_recovery_set_aside_is_not_saved_however_the_stored_generation_reads(string damage)
    {
        var rig = OpenEditing();
        rig.Settings.FailAfterNextCommit = true;
        var unknown = await rig.Protocol.SaveAsync(rig.Request());
        var prepared = Assert.Single(rig.Store.Prepared);
        Assert.False(unknown.Success);
        Assert.True(rig.Protocol.HasPendingSave);
        Assert.Equal(prepared.Generation, Fixture.StoredGeneration);

        // Recovery cannot install the Save's files: an image it needs is gone or fails its hash, or another manifest holds the
        // generation. The generation stored is still the Save's own, which says nothing of whether its files were installed.
        switch (damage)
        {
            case "missing redo image":
                Assert.NotEmpty(RedoImages());
                foreach (var image in RedoImages())
                {
                    File.Delete(image);
                }

                break;
            case "corrupt redo image":
                Assert.NotEmpty(RedoImages());
                foreach (var image in RedoImages())
                {
                    File.WriteAllBytes(image, [1, 2, 3, 4]);
                }

                break;
            default:
                File.WriteAllText(
                    Path.Combine(Fixture.Paths.LibraryJournalDir, LibraryJournalNames.Manifest(prepared.Generation, new string('a', LibraryJournalNames.ManifestIdLength))),
                    "{}");
                break;
        }

        rig.Settings.Failing = false;
        Assert.Equal(prepared.Generation, Fixture.StoredGeneration);
        var settled = await rig.Protocol.TrySettlePendingAsync(rig.Request());

        // Not saved: the edit stays the draft's, the files were never replaced, and the answer is the same however often it is asked.
        Assert.False(settled.Success);
        Assert.Equal(WordPackSaveProtocolSeverity.Warning, settled.Severity);
        Assert.Contains("weren't saved", settled.Message, StringComparison.Ordinal);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.True(rig.Workspace.HasUnsavedChanges);
        Assert.Equal("K8s", RealLibraries.Row(rig.Workspace, "team-terms", "kube").Row.Values.Written);
        Assert.Equal(TeamTerms, Fixture.Read("team-terms.csv"));
        Assert.Empty(Manifests());
        Assert.Equal(LibrarySaveStatus.Superseded, rig.Service.CompleteSave(prepared).Status);
        Assert.Equal(LibrarySaveStatus.Superseded, rig.Service.CompleteSave(prepared).Status);

        // The edits were rebased onto what is stored, so saving again installs them.
        var again = await rig.Protocol.SaveAsync(rig.Request());

        Assert.True(again.Success, again.Message);
        Assert.False(rig.Workspace.HasUnsavedChanges);
        Assert.Equal("K8s", rig.Service.LoadCatalog().Find("team-terms")!.Content.Rows.Single().Values.Written);
        Assert.NotEqual(TeamTerms, Fixture.Read("team-terms.csv"));
    }

    [Fact]
    public async Task A_Save_recovery_finishes_but_cannot_install_yet_is_saved_and_stays_fenced_until_the_file_is_released()
    {
        var rig = OpenEditing();
        rig.Settings.FailAfterNextCommit = true;
        var unknown = await rig.Protocol.SaveAsync(rig.Request());
        Assert.False(unknown.Success);
        Assert.True(rig.Protocol.HasPendingSave);
        rig.Settings.Failing = false;

        WordPackSaveProtocolResult settled;
        LibraryPrepareStatus fenced;
        using (new FileStream(Fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Another app reads the file, so the install waits for it: the Save committed, and recovery's manifest for it is
            // unresolved, not set aside. That is saved, never a Save to try again.
            settled = await rig.Protocol.TrySettlePendingAsync(rig.Request());
            var catalog = rig.Service.LoadCatalog();
            fenced = rig.Service.PrepareSave(Changes.Of(catalog, state: Changes.With(catalog.LocalState, disable: ["team-terms"]))).Status;
        }

        Assert.True(settled.Success, settled.Message);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.False(rig.Workspace.HasUnsavedChanges);
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, fenced);
        Assert.Equal(TeamTerms, Fixture.Read("team-terms.csv"));
        Assert.Single(Manifests());

        // Once the file is released, recovery installs it and the library takes new Saves again.
        var released = rig.Service.LoadCatalog();

        Assert.Equal("K8s", released.Find("team-terms")!.Content.Rows.Single().Values.Written);
        Assert.NotEqual(TeamTerms, Fixture.Read("team-terms.csv"));
        Assert.Empty(Manifests());
        Assert.Equal(
            LibraryPrepareStatus.Prepared,
            rig.Service.PrepareSave(Changes.Of(released, state: Changes.With(released.LocalState, disable: ["team-terms"]))).Status);
    }

    [Fact]
    public async Task A_new_word_pack_whose_Save_recovery_installed_is_saved_once_when_an_adoption_moved_the_generation_on_before_the_settlement()
    {
        var rig = OpenCreating();
        rig.Settings.FailAfterNextCommit = true;
        var unknown = await rig.Protocol.SaveAsync(rig.Request());
        var prepared = Assert.Single(rig.Store.Prepared);
        Assert.False(unknown.Success);
        Assert.True(rig.Protocol.HasPendingSave);

        // The load that recovers the Save installs it at its generation, and adopts a file another app dropped meanwhile as a
        // generation of its own, so the generation stored is one past the Save's.
        Fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        rig.Settings.Failing = false;
        var recovered = rig.Service.LoadCatalog();
        Assert.Equal(prepared.Generation + 1, recovered.Generation);
        Assert.NotNull(recovered.Find("dropped"));
        Assert.Single(recovered.Libraries, library => library.Content.Name == "Release notes");

        var settled = await rig.Protocol.TrySettlePendingAsync(rig.Request());

        // Saved: rebasing the draft onto the catalog as if the Save had been superseded would add the committed pack again.
        Assert.True(settled.Success, settled.Message);
        Assert.False(rig.Protocol.HasPendingSave);
        Assert.False(rig.Workspace.HasUnsavedChanges);
        Assert.Single(rig.Workspace.Draft.Libraries, library => library.Content.Name == "Release notes");
        Assert.Single(rig.Service.LoadCatalog().Libraries, library => library.Content.Name == "Release notes");
    }

    private sealed class Rig
    {
        public Rig(DictionaryLibraryService service, SettingsAfterCommit settings, LibraryWorkspace workspace)
        {
            Service = service;
            Settings = settings;
            Workspace = workspace;
            Store = new RealStore(service, settings);
            Protocol = new WordPackSaveProtocol(Store);
        }

        public DictionaryLibraryService Service { get; }

        public SettingsAfterCommit Settings { get; }

        public LibraryWorkspace Workspace { get; }

        public RealStore Store { get; }

        public WordPackSaveProtocol Protocol { get; }

        /// <summary>The retention the Save's document carries: the scalar a stale application would leave in force.</summary>
        public int RetentionDays { get; set; } = 90;

        /// <summary>The retention stored at each moment the shell was asked to put the stored settings into effect.</summary>
        public List<int> AppliedRetention { get; } = [];
        public List<long> RefreshedGenerations { get; } = [];

        public WordPackSaveProtocolRequest Request() => new(
            Workspace,
            payload =>
            {
                var document = Settings.Load();
                document.HistoryRetentionDays = RetentionDays;
                Settings.SaveBundle(document, null, null, default, payload);
            },
            () =>
            {
                AppliedRetention.Add(Settings.Load().HistoryRetentionDays);
                return new StoredSettingsReapplyResult(StoredSettingsReapplied.StoredSettings,
                    Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty)));
            },
            () => "draft",
            () => "draft",
            OnWordPacksChanged: catalog => RefreshedGenerations.Add(catalog.Generation));
    }

    /// <summary>
    /// The window's store: preparation, completion and the catalog load go straight to the library service, and a settlement
    /// is the interface's default, a second completion of the same preparation. Nothing here overrides it.
    /// </summary>
    private sealed class RealStore(DictionaryLibraryService service, ISettingsRepository settings) : IWordPackSaveProtocolStore
    {
        public List<PreparedLibrarySave> Prepared { get; } = [];

        /// <summary>Runs once, just before the next catalog read, which is the recovery attempt a revisable receipt waits on.</summary>
        public Action? BeforeCatalogLoad { get; set; }
        public Action? BeforeComplete { get; set; }
        public Action<LibrarySavePayload>? BeforeRepairCommit { get; set; }

        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes)
        {
            var result = WordPackSaveSession.Prepare(service, changes);
            if (result.Save is { } save)
            {
                Prepared.Add(save);
            }

            return Task.FromResult(result);
        }

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared)
        {
            BeforeComplete?.Invoke();
            return Task.FromResult(WordPackSaveSession.Complete(service, prepared));
        }

        public Task<LibraryCatalog> LoadCatalogAsync()
        {
            var before = BeforeCatalogLoad;
            BeforeCatalogLoad = null;
            before?.Invoke();
            return Task.FromResult(WordPackSaveSession.LoadCatalog(service));
        }

        public void CommitRepair(LibrarySavePayload payload)
        {
            BeforeRepairCommit?.Invoke(payload);
            settings.CommitLibraryState(payload);
        }
    }

    /// <summary>
    /// The real settings store, except that with <see cref="FailAfterNextCommit"/> set, the reads of the stored library
    /// generation fail from right after the next library commit (which commits), until <see cref="Failing"/> is cleared: what
    /// makes a completion unable to tell whether its Save committed.
    /// </summary>
    private sealed class SettingsAfterCommit(SettingsRepository inner) : ISettingsRepository
    {
        public bool FailAfterNextCommit { get; set; }

        public bool Failing { get; set; }

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load() => inner.Load();

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) => inner.Update(mutate, revision, out superseded);

        public void SaveBundle(AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets, long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public void SaveBundle(
            AppSettings settings, IReadOnlyList<DictionaryEntry>? dictionaryEntries, IReadOnlyList<Snippet>? snippets,
            ExternalIntents intents, LibrarySavePayload? libraries)
        {
            inner.SaveBundle(settings, dictionaryEntries, snippets, intents, libraries);
            if (libraries is not null && FailAfterNextCommit)
            {
                FailAfterNextCommit = false;
                Failing = true;
            }
        }

        public void CommitLibraryState(LibrarySavePayload payload) => inner.CommitLibraryState(payload);

        public string? Get(string key) =>
            Failing && key == LibrarySettingKeys.Generation ? throw new IOException("injected: the settings store cannot be read") : inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
