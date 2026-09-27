using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// J-23 (G15): names in <c>edits\</c> are recognized by suffix, in a fixed order, so no set-aside copy, however it was
/// named, and no last good copy is ever a library; a retired built-in's document is listed as its kept rows; and the
/// document of an id neither shipped nor retired is nobody's, never read, listed, rewritten or deleted.
/// </summary>
public sealed class LibraryEditsRecognitionTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Scribe.Core.PostProcessing.DictionaryLibraryService Service(ILibraryFileSystem? files = null, params string[] retired) =>
        new(_fixture.Paths, _fixture.Settings, _fixture.Log, (files is null ? _fixture.Parts() : _fixture.Parts(files)) with { RetiredBuiltInIds = retired });

    private static void AssertOnlyShippedAndCustom(LibraryCatalog catalog, params string[] custom)
    {
        Assert.Equal(custom, catalog.Libraries.Where(library => !library.Content.BuiltIn).Select(library => library.Content.Id));
        Assert.All(catalog.Libraries.Where(library => library.Content.BuiltIn), library =>
            Assert.Contains(library.Content.Id, CustomLibraryStore.ShippedIds));
    }

    [Fact]
    public void After_Back_up_and_reset_the_set_aside_copy_is_no_library_and_the_built_in_is_as_shipped()
    {
        _fixture.Write("edits/github.json", "{ this document is damaged");
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Assert.Equal(LibraryFileState.Unreadable, catalog.Find("github")!.State);

        Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Recovery: BuiltInEditsRecovery.BackUpAndReset)]));

        var setAside = Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryEditsDir, "*.backup.json"));
        Assert.Equal("{ this document is damaged", File.ReadAllText(setAside));
        var reloaded = Service().LoadCatalog();
        AssertOnlyShippedAndCustom(reloaded);
        Assert.Empty(reloaded.RetiredBuiltInEdits);
        Assert.Equal(LibraryFileState.Available, reloaded.Find("github")!.State);
        Assert.Null(reloaded.Find("github")!.ContentHash);
    }

    [Fact]
    public void After_Restore_the_previous_copy_the_paused_document_is_set_aside_and_the_previous_one_is_in_use()
    {
        _fixture.Write("edits/github.json", "{ this document is damaged");
        _fixture.WriteBytes("edits/github.previous.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub Previous")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Assert.True(catalog.Find("github")!.PreviousEditsAvailable);

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Recovery: BuiltInEditsRecovery.RestorePrevious)]));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Empty(saved.Outcome.KeptVersions);
        Assert.Equal("{ this document is damaged", File.ReadAllText(Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryEditsDir, "*.backup.json"))));
        var reloaded = Service().LoadCatalog();
        AssertOnlyShippedAndCustom(reloaded);
        Assert.Contains(reloaded.Find("github")!.Content.Rows, row => row.Values.Written == "GitHub Previous");
    }

    [Fact]
    public void An_outside_version_a_write_preserves_is_set_aside_under_a_name_no_loader_lists()
    {
        _fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub P")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: JsonEditsOverlay.Edits("github", ("get hub", "GitHub S")))]),
            beforeCommit: () => _fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub E"))));

        Assert.Equal(LibraryKeptVersionKind.EditsSetAside, Assert.Single(saved.Outcome!.KeptVersions).Kind);
        var reloaded = Service().LoadCatalog();
        AssertOnlyShippedAndCustom(reloaded);
        Assert.Empty(reloaded.RetiredBuiltInEdits);
        Assert.Contains(reloaded.Find("github")!.Content.Rows, row => row.Values.Written == "GitHub S");
    }

    [Fact]
    public void Hand_placed_set_aside_and_previous_copies_are_never_libraries_while_a_retired_built_ins_own_document_is_listed()
    {
        var retired = JsonEditsOverlay.Document("old-pack", ("old term", "Old Term"));
        _fixture.WriteBytes("edits/github.20260101T000000Z.backup.json", JsonEditsOverlay.Document("github", ("a", "Round 2")));
        _fixture.WriteBytes("edits/github.20260101T000000Z.0123456789abcdef-2.backup.json", JsonEditsOverlay.Document("github", ("a", "Retry")));
        _fixture.WriteBytes("edits/old-pack.previous.json", retired);
        _fixture.WriteBytes("edits/old-pack.json", retired);

        var catalog = Service(null, "old-pack").LoadCatalog();

        AssertOnlyShippedAndCustom(catalog);
        var kept = Assert.Single(catalog.RetiredBuiltInEdits);
        Assert.Equal("old-pack", kept.LibraryId);
        Assert.Equal("Old Term", Assert.Single(kept.AuthoredTerms).Written);
        Assert.Null(catalog.Find("old-pack"));
        Assert.Equal(LibraryFileState.Available, catalog.Find("github")!.State);
    }

    [Fact]
    public void Every_retired_id_stays_out_of_the_catalog_however_many_retirements_there_have_been()
    {
        _fixture.WriteBytes("edits/old-pack.json", JsonEditsOverlay.Document("old-pack", ("a", "A")));
        _fixture.WriteBytes("edits/older-pack.json", JsonEditsOverlay.Document("older-pack", ("b", "B")));

        var catalog = Service(null, "older-pack", "old-pack").LoadCatalog();

        Assert.Equal(["old-pack", "older-pack"], catalog.RetiredBuiltInEdits.Select(edits => edits.LibraryId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(catalog.Libraries, library => library.Content.Id is "old-pack" or "older-pack");
    }

    [Fact]
    public void The_document_of_an_id_neither_shipped_nor_retired_is_never_read_listed_rewritten_or_deleted()
    {
        var bytes = Encoding.UTF8.GetBytes("{ a newer version's built-in }");
        _fixture.WriteBytes("edits/scribe.newer-pack.json", bytes);
        var reads = new List<string>();
        var files = new FaultingFileSystem
        {
            ReadFault = path =>
            {
                reads.Add(path);
                return null;
            },
        };
        var service = Service(files);

        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: JsonEditsOverlay.Edits("github", ("get hub", "GitHub S")))]));
        service.Janitor.Run(LibraryStorageFixture.Start.AddDays(400));

        Assert.DoesNotContain(reads, path => path.EndsWith("scribe.newer-pack.json", StringComparison.Ordinal));
        Assert.DoesNotContain(files.Journal, entry => entry.Contains("scribe.newer-pack", StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(_fixture.PathOf("edits/scribe.newer-pack.json")));
        AssertOnlyShippedAndCustom(service.LoadCatalog());
        Assert.Empty(service.LoadCatalog().RetiredBuiltInEdits);
    }
}
