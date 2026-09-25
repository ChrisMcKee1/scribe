using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// A Save through the whole pipeline, every operation kind at once: the change set becomes a manifest, the settings
/// transaction commits it, completion installs it, and a fresh service over the same folder and database reads exactly
/// what was saved.
/// </summary>
public sealed class LibrarySaveRoundTripTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Every_operation_kind_commits_installs_and_reads_back()
    {
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        _fixture.Write("old-library.csv", LibraryStorageFixture.Csv("Old", ("old", "Old")));
        _fixture.Write("scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        _fixture.SaveEnabled("team-terms", "old-library", "github");
        var service = _fixture.Service();

        // The first start adopts the existing files (a generation of its own), so the Save below starts from a stored state.
        var start = service.LoadCatalog();
        Assert.Equal(1, start.Generation);
        Assert.True(_fixture.Exists("journal/state.witness"));

        // Scratch goes to Recently deleted first, so the Save can restore it and purge nothing else.
        var removed = Changes.Save(service, _fixture.Settings, Changes.Of(start, deletions: [Changes.Delete(start, "scratch")]));
        Assert.Equal(LibrarySaveStatus.Applied, removed.Outcome!.Status);
        var catalog = service.LoadCatalog();
        var entry = Assert.Single(catalog.RecentlyDeleted);
        var restoredContent = service.ReadRecentlyDeleted(entry);
        Assert.NotNull(restoredContent);

        var edited = LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"), ("north star", "North Star"));
        var created = LibraryStorageFixture.Content("custom-release-notes", "Release notes", ("sprint", "Sprint"));
        var edits = JsonEditsOverlay.Edits("github", ("get hub", "GitHub Enterprise"));
        var changes = Changes.Of(
            catalog,
            writes:
            [
                Changes.Edit(catalog, "team-terms", edited),
                Changes.Create(created),
                new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: edits),
            ],
            deletions: [Changes.Delete(catalog, "old-library")],
            recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, entry.ContentHash, "scratch")],
            state: Changes.With(catalog.LocalState, enable: ["custom-release-notes"]));

        var saved = Changes.Save(service, _fixture.Settings, changes);

        Assert.Equal(LibraryPrepareStatus.Prepared, saved.Prepared.Status);
        Assert.Null(saved.CommitFailure);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(3, saved.Outcome.CommittedGeneration);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        Assert.Equal(LibraryStorageFixture.Managed(created), File.ReadAllBytes(_fixture.PathOf("custom-release-notes.csv")));
        Assert.Equal(JsonEditsOverlay.Instance.WriteEdits(edits), File.ReadAllBytes(_fixture.PathOf("edits/github.json")));
        Assert.False(_fixture.Exists("old-library.csv"));
        Assert.True(_fixture.Exists("scratch.csv"));
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir), path => path.EndsWith(".old-library.csv", StringComparison.Ordinal));

        // Nothing of the journal is left: the manifest retired, its redo folder and install copies gone.
        Assert.Equal(["journal/state.witness"], _fixture.AllFiles().Where(file => file.StartsWith("journal/", StringComparison.Ordinal)));
        Assert.DoesNotContain(_fixture.AllFiles(), file => file.Contains(".scribe-", StringComparison.Ordinal));

        _fixture.Restart();
        var reloaded = _fixture.Service().LoadCatalog();
        Assert.Equal(3, reloaded.Generation);
        Assert.Equal("K8s", reloaded.Find("team-terms")!.Content.Rows[0].Values.Written);
        Assert.Equal(LibraryFileState.Available, reloaded.Find("custom-release-notes")!.State);
        Assert.Contains("custom-release-notes", reloaded.LocalState.EnabledIds);
        Assert.Equal("GitHub Enterprise", reloaded.Find("github")!.Content.Rows.First(row => row.Key == LibraryTermKey.From("get hub")).Values.Written);
        Assert.Null(reloaded.Find("old-library"));
        Assert.NotNull(reloaded.Find("scratch"));
        Assert.Equal("old-library", Assert.Single(reloaded.RecentlyDeleted).OriginalId);

        // Scribe's own writes never read as a replacement: the accepted content is what the Save wrote.
        Assert.Equal(_fixture.HashOf("team-terms.csv"), reloaded.LocalState.AcceptedContent["team-terms"]);
        Assert.Equal(_fixture.HashOf("edits/github.json"), reloaded.LocalState.AcceptedContent["github"]);
    }

    [Fact]
    public void A_purge_deletes_the_entry_for_good()
    {
        _fixture.Write("scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, deletions: [Changes.Delete(catalog, "scratch")]));
        catalog = service.LoadCatalog();
        var entry = Assert.Single(catalog.RecentlyDeleted);

        var purged = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.DeletePermanently, entry.EntryName, entry.ContentHash)]));

        Assert.Equal(LibrarySaveStatus.Applied, purged.Outcome!.Status);
        Assert.Empty(service.LoadCatalog().RecentlyDeleted);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
    }

    [Fact]
    public void Removing_a_built_ins_edits_keeps_the_replaced_document_as_its_previous_copy()
    {
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        var edits = JsonEditsOverlay.Edits("github", ("get hub", "GitHub Enterprise"));
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: edits)]));
        catalog = service.LoadCatalog();
        var document = File.ReadAllBytes(_fixture.PathOf("edits/github.json"));

        var reset = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: null)]));

        Assert.Equal(LibrarySaveStatus.Applied, reset.Outcome!.Status);
        Assert.False(_fixture.Exists("edits/github.json"));
        Assert.Equal(document, File.ReadAllBytes(_fixture.PathOf("edits/github.previous.json")));
        var reloaded = service.LoadCatalog().Find("github")!;
        Assert.True(reloaded.PreviousEditsAvailable);
        Assert.Null(reloaded.ContentHash);
        Assert.DoesNotContain("github", reloaded.Content.Rows.Select(row => row.Values.Written));
    }

    [Theory]
    [InlineData(BuiltInEditsRecovery.None)]
    [InlineData(BuiltInEditsRecovery.BackUpAndReset)]
    public void The_journal_drops_the_accepted_entry_of_every_edits_document_it_removes(BuiltInEditsRecovery recovery)
    {
        // The amended contract (eb79dcc): an accepted entry left for a built-in with no document means the document
        // disappeared outside Scribe, so a removal of Scribe's own drops the entry in the same commit.
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: JsonEditsOverlay.Edits("github", ("get hub", "GitHub Enterprise")))]));
        catalog = service.LoadCatalog();
        Assert.True(catalog.LocalState.AcceptedContent.ContainsKey("github"));

        var removed = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: null, Recovery: recovery)]));

        Assert.Equal(LibrarySaveStatus.Applied, removed.Outcome!.Status);
        Assert.False(_fixture.Exists("edits/github.json"));

        // Dropped by the Save's own commit, not by an adoption reading the removal as a disappearance: no generation but
        // the Save's own, at its publication or at the next start.
        Assert.Equal(catalog.Generation + 1, _fixture.StoredGeneration);
        var generation = _fixture.StoredGeneration;
        _fixture.Restart();
        var reloaded = _fixture.Service().LoadCatalog();
        Assert.False(reloaded.LocalState.AcceptedContent.ContainsKey("github"));
        Assert.Equal(LibraryFileState.Available, reloaded.Find("github")!.State);
        Assert.Equal(generation, _fixture.StoredGeneration);
    }

    // A permitted built-in whose edits document the committed state accepts, and that document's bytes.
    private (Scribe.Core.PostProcessing.DictionaryLibraryService Service, LibraryCatalog Catalog) PermittedBuiltInWithEdits()
    {
        _fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub Enterprise")));
        _fixture.SaveEnabled("github");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, state: Changes.With(catalog.LocalState, ai: [("github", true)])));
        catalog = service.LoadCatalog();
        Assert.True(catalog.LocalState.AcceptedContent.ContainsKey("github"));
        Assert.True(catalog.LocalState.AiPermissions["github"]);
        Assert.Contains("github", service.Current.AiScope.PermittedLibraryIds);
        return (service, catalog);
    }

    [Fact]
    public void Restoring_a_permitted_built_ins_values_drops_its_accepted_entry_so_the_next_load_revokes_nothing()
    {
        // Sub-stream C's request (w1b-c.md, A1 on C): an available built-in with no edits document while an accepted entry
        // remains reads as a document that disappeared outside Scribe, and loses its AI permission. Scribe's own removal
        // drops the entry in the same commit, so the built-in stays permitted by its choice.
        var (service, catalog) = PermittedBuiltInWithEdits();

        var restored = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: null)]));
        Assert.Equal(LibrarySaveStatus.Applied, restored.Outcome!.Status);
        Assert.Equal(catalog.Generation + 1, _fixture.StoredGeneration);
        Assert.Contains("github", service.Current.AiScope.PermittedLibraryIds);
        var generation = _fixture.StoredGeneration;
        var mark = _fixture.Log.Entries.Count;

        _fixture.Restart();
        var reloadedService = _fixture.Service();
        var reloaded = reloadedService.LoadCatalog();

        Assert.Equal(generation, _fixture.StoredGeneration);
        Assert.False(reloaded.LocalState.AcceptedContent.ContainsKey("github"));
        Assert.True(reloaded.LocalState.AiPermissions["github"]);
        Assert.Contains("github", reloaded.LocalState.EnabledIds);
        Assert.Contains("github", reloadedService.Current.AiScope.PermittedLibraryIds);
        Assert.Null(reloadedService.Current.AiScope.PermittedContent["github"]);
        Assert.DoesNotContain(_fixture.Log.Entries.Skip(mark), entry => entry.Message.StartsWith("Adopted ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edits_document_deleted_outside_Scribe_is_replaced_content_and_its_permission_is_revoked()
    {
        // The negative case: the same document gone without a Save of Scribe's, so the accepted entry remains, and the next
        // load's adoption revokes the permission, drops the entry and keeps the built-in on.
        var (service, _) = PermittedBuiltInWithEdits();
        var admitted = service.Current.AiScope;
        var generation = _fixture.StoredGeneration;
        var mark = _fixture.Log.Entries.Count;
        File.Delete(_fixture.PathOf("edits/github.json"));

        _fixture.Restart();
        var reloadedService = _fixture.Service();
        var reloaded = reloadedService.LoadCatalog();

        Assert.Equal(generation + 1, _fixture.StoredGeneration);
        Assert.False(reloaded.LocalState.AcceptedContent.ContainsKey("github"));
        Assert.False(reloaded.LocalState.AiPermissions["github"]);
        Assert.Contains("github", reloaded.LocalState.EnabledIds);
        Assert.DoesNotContain("github", reloadedService.Current.AiScope.PermittedLibraryIds);
        Assert.False(reloadedService.TryHandOff(admitted, () => Assert.Fail("A scope admitted for the vanished document was handed over.")));
        Assert.Contains(_fixture.Log.Entries.Skip(mark), entry => entry.Message.StartsWith("Adopted ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_vanished_edits_document_is_denied_at_once_on_defaults_too_where_no_adoption_runs()
    {
        // ebfa048 (C's A5): the permission rule itself denies a built-in with no document while an accepted entry remains,
        // so a session on defaults, which adopts nothing, never sends it either; and Scribe's own removal, which drops the
        // entry, is not denied that way.
        var (service, _) = PermittedBuiltInWithEdits();
        var admitted = service.Current.AiScope;
        File.Delete(_fixture.PathOf("edits/github.json"));
        var generation = _fixture.StoredGeneration;
        _fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false);

        var onDefaults = _fixture.Service();
        var catalog = onDefaults.LoadCatalog();

        Assert.Equal(generation, _fixture.StoredGeneration);
        Assert.True(catalog.LocalState.AcceptedContent.ContainsKey("github"));
        Assert.DoesNotContain("github", onDefaults.Current.AiScope.PermittedLibraryIds);
        Assert.False(onDefaults.TryHandOff(admitted, () => Assert.Fail("A scope admitted for the vanished document was handed over.")));
    }
}
