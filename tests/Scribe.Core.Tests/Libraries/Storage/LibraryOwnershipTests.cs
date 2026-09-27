using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 2, A4: the journal deletes only bytes it owns. A version it finds at a target or in Recently deleted is first
/// taken by a rename that never overwrites into journal-owned storage, and only that owned copy is judged and deleted,
/// so another app's write landing immediately before any destructive step survives: as a preserved version, back in
/// Recently deleted, or where it was written. Each case injects that write before the first delete of completion.
/// </summary>
public sealed class LibraryOwnershipTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static byte[] Document(string written) => JsonEditsOverlay.Document("github", ("get hub", written));

    private bool Holds(byte[] bytes)
    {
        var hash = LibraryContentHashing.Of(bytes);
        return _fixture.AllFiles().Any(file => _fixture.HashOf(file) == hash);
    }

    // After the first move away from `path`, `recreate` lands there; before the first delete of completion that is not an
    // install copy or a journal file, `late` does. Armed only once prepare is done, so only completion meets them.
    private FaultingFileSystem Racing(string path, byte[]? recreate, byte[] late, Func<bool> armed)
    {
        var target = _fixture.PathOf(path);
        var recreated = false;
        var injected = false;
        return new FaultingFileSystem
        {
            AfterMutation = (kind, source, _) =>
            {
                if (armed() && recreate is not null && !recreated && kind == "move" && string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    recreated = true;
                    File.WriteAllBytes(target, recreate);
                }
            },
            BeforeMutation = (kind, source, _) =>
            {
                if (armed() && !injected && kind == "delete" &&
                    !source.EndsWith(".scribe-staged", StringComparison.OrdinalIgnoreCase) &&
                    !source.StartsWith(_fixture.Paths.LibraryJournalDir, StringComparison.OrdinalIgnoreCase))
                {
                    injected = true;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, late);
                }
            },
        };
    }

    [Fact]
    public void A_remove_that_meets_a_version_it_already_kept_never_deletes_a_newer_one_written_just_before()
    {
        _fixture.WriteBytes("edits/github.json", Document("GitHub P"));
        _fixture.SaveEnabled("github");
        var first = Document("GitHub E1");
        var late = Document("GitHub E2");
        var armed = false;
        var files = Racing("edits/github.json", first, late, () => armed);
        var service = _fixture.Service(files);
        var catalog = service.LoadCatalog();

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: null)]),
            beforeCommit: () =>
            {
                _fixture.WriteBytes("edits/github.json", first);
                armed = true;
            });

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.True(Holds(first));
        Assert.True(Holds(late), "the version written just before a delete is gone");
    }

    [Fact]
    public void A_create_that_meets_a_version_it_already_kept_never_deletes_a_newer_one_written_just_before()
    {
        _fixture.SaveEnabled("github");
        var first = Document("GitHub E1");
        var late = Document("GitHub E2");
        var armed = false;
        var files = Racing("edits/github.json", first, late, () => armed);
        var service = _fixture.Service(files);
        var catalog = service.LoadCatalog();

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: JsonEditsOverlay.Edits("github", ("get hub", "GitHub S")))]),
            beforeCommit: () =>
            {
                _fixture.WriteBytes("edits/github.json", first);
                armed = true;
            });

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.True(Holds(first));
        Assert.True(Holds(late), "the version written just before a delete is gone");
    }

    [Fact]
    public void A_restore_never_consumes_a_Recently_deleted_entry_rewritten_just_before()
    {
        const string Entry = "20260901T101010Z.scratch.csv";
        _fixture.Write("deleted/" + Entry, LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        _fixture.SaveEnabled();
        var late = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch from another app")));
        var armed = false;
        var files = Racing("deleted/" + Entry, null, late, () => armed);
        var service = _fixture.Service(files);
        var catalog = service.LoadCatalog();
        var entry = catalog.RecentlyDeleted.Single();

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, entry.ContentHash, "scratch")]),
            beforeCommit: () => armed = true);

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.True(_fixture.Exists("scratch.csv"));
        Assert.True(Holds(late), "the entry rewritten just before its deletion is gone");
        Assert.Contains(service.LoadCatalog().RecentlyDeleted, deleted => deleted.ContentHash == LibraryContentHashing.Of(late));
    }

    [Fact]
    public void A_purge_never_deletes_a_Recently_deleted_entry_rewritten_just_before()
    {
        const string Entry = "20260801T090000Z.junk.csv";
        _fixture.Write("deleted/" + Entry, LibraryStorageFixture.Csv("Junk", ("junk", "Junk")));
        _fixture.SaveEnabled();
        var late = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Junk", ("junk", "Junk from another app")));
        var armed = false;
        var files = Racing("deleted/" + Entry, null, late, () => armed);
        var service = _fixture.Service(files);
        var catalog = service.LoadCatalog();
        var entry = catalog.RecentlyDeleted.Single();

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.DeletePermanently, entry.EntryName, entry.ContentHash)]),
            beforeCommit: () => armed = true);

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.True(Holds(late), "the entry rewritten just before its deletion is gone");
        Assert.Contains(service.LoadCatalog().RecentlyDeleted, deleted => deleted.ContentHash == LibraryContentHashing.Of(late));
        Assert.DoesNotContain(service.LoadCatalog().RecentlyDeleted, deleted => deleted.ContentHash == entry.ContentHash);
    }
}
