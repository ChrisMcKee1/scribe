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
}
