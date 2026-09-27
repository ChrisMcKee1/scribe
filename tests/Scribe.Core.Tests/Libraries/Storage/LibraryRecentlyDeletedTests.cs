using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Recently deleted (contract 6.7): deleting and restoring through the journal, the janitor's retention by an injected
/// clock, the entries it never removes (J-8), and a restore the draft also edited as one operation (J-8b, A8).
/// </summary>
public sealed class LibraryRecentlyDeletedTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_deleted_library_moves_into_Recently_deleted_byte_for_byte_and_restores_under_a_new_id_without_overwriting()
    {
        // J-8: delete, then restore while a library with the old id exists again (the restore gets a new id, both off).
        _fixture.Write("team-notes.csv", LibraryStorageFixture.Csv("Team notes", ("a", "A")));
        _fixture.SaveEnabled("team-notes");
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var bytes = File.ReadAllBytes(_fixture.PathOf("team-notes.csv"));
        Changes.Save(service, _fixture.Settings, Changes.Of(start, deletions: [Changes.Delete(start, "team-notes")]));
        var entry = Assert.Single(service.LoadCatalog().RecentlyDeleted);
        Assert.Equal("team-notes", entry.OriginalId);
        Assert.Equal("Team notes", entry.Name);
        Assert.Equal(1, entry.TermCount);
        Assert.Equal(LibraryStorageFixture.Start, entry.DeletedUtc);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_fixture.Paths.LibraryDeletedDir, entry.EntryName)));

        _fixture.Write("team-notes.csv", LibraryStorageFixture.Csv("Team notes again", ("b", "B")));
        var again = service.LoadCatalog();
        var restore = service.ReadRecentlyDeleted(again.RecentlyDeleted.Single())!;
        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            again, recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, restore.Entry.ContentHash, "team-notes-2")]));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(bytes, File.ReadAllBytes(_fixture.PathOf("team-notes-2.csv")));
        Assert.Equal(LibraryStorageFixture.Csv("Team notes again", ("b", "B")), _fixture.Read("team-notes.csv"));
        var reloaded = service.LoadCatalog();
        Assert.Empty(reloaded.RecentlyDeleted);
        Assert.DoesNotContain("team-notes-2", reloaded.LocalState.EnabledIds);
        Assert.False(ContractComposer.IsPermitted(reloaded.LocalState, "team-notes-2", false, reloaded.Find("team-notes-2")!.ContentHash));
    }

    [Fact]
    public void A_restore_target_taken_at_prepare_is_an_outside_edit()
    {
        var (service, catalog, entry) = OneDeletedEntry();
        _fixture.Write("scratch.csv", LibraryStorageFixture.Csv("Someone else's scratch", ("x", "X")));

        var prepared = service.PrepareSave(Changes.Of(
            service.LoadCatalog(), recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, entry.ContentHash, "scratch")]));

        Assert.Equal(LibraryPrepareStatus.OutsideEdit, prepared.Status);
        Assert.Equal(["scratch"], prepared.OutsideEditIds);
        _ = catalog;
    }

    [Fact]
    public void The_janitor_removes_an_entry_after_thirty_days_by_its_own_stamp_and_never_an_unparsable_or_future_one()
    {
        // J-8: at 29 days kept, at 31 removed; a stamp that cannot be parsed, or lies ahead, is never removed; loading never purges.
        var (service, _, entry) = OneDeletedEntry();
        _fixture.Write("deleted/not-a-stamp.lost.csv", LibraryStorageFixture.Csv("Lost", ("l", "L")));
        _fixture.Write("deleted/20991231T235959Z.future.csv", LibraryStorageFixture.Csv("Future", ("f", "F")));
        var entryPath = Path.Combine(_fixture.Paths.LibraryDeletedDir, entry.EntryName);

        _fixture.Time.Advance(TimeSpan.FromDays(400));
        service.LoadCatalog();
        Assert.True(File.Exists(entryPath));

        Assert.Equal(0, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(29)).RecentlyDeletedRemoved);
        Assert.True(File.Exists(entryPath));
        Assert.Equal(1, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(31)).RecentlyDeletedRemoved);
        Assert.False(File.Exists(entryPath));
        Assert.Equal(0, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(3_650)).RecentlyDeletedRemoved);
        Assert.True(_fixture.Exists("deleted/not-a-stamp.lost.csv"));
        Assert.True(_fixture.Exists("deleted/20991231T235959Z.future.csv"));
        Assert.Contains(service.LoadCatalog().RecentlyDeleted, candidate => candidate.EntryName == "not-a-stamp.lost.csv" && candidate.State == LibraryFileState.Unreadable);
    }

    [Fact]
    public void The_janitor_never_removes_an_entry_a_pending_manifest_names()
    {
        // J-8 and rule R6: a manifest still pending (another of its files is locked) protects the entry its delete made.
        _fixture.Write("scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        _fixture.Write("keep.csv", LibraryStorageFixture.Csv("Keep", ("k", "K")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(
            catalog,
            writes: [Changes.Edit(catalog, "keep", LibraryStorageFixture.Content("keep", "Keep", ("k", "K2")))],
            deletions: [Changes.Delete(catalog, "scratch")]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        using (new FileStream(_fixture.PathOf("keep.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, service.CompleteSave(prepared.Save).Status);
            var entry = Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));

            Assert.Equal(0, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(31)).RecentlyDeletedRemoved);
            Assert.True(File.Exists(entry));
        }

        // Once the manifest finishes, the entry's own retention applies.
        Assert.Equal(1, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(31)).RecentlyDeletedRemoved);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
    }

    [Fact]
    public void A_set_aside_manifest_neither_protects_nor_removes_an_entry_it_names()
    {
        // J-8 and rule R6 (G11): the quarantine's expiry never deletes a Recently deleted entry, which keeps its own 30 days.
        var (_, _, deleted) = OneDeletedEntry();
        var entry = Path.Combine(_fixture.Paths.LibraryDeletedDir, deleted.EntryName);

        // A manifest naming the entry, pending when the generation row is lost, so the next start sets it aside.
        const string Id = "00000000000000000000000000000abc";
        var manifest = new LibraryManifest(Id, 3, 2, LibraryStorageFixture.Start,
        [
            new LibraryManifestOperation(0, LibraryOperationKind.Delete, "scratch.csv", "scratch", deleted.ContentHash, To: "deleted/" + deleted.EntryName),
        ]);
        _fixture.WriteBytes("journal/" + LibraryJournalNames.Manifest(3, Id), manifest.ToJson());
        using (var connection = _fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM settings WHERE key = 'libraries.generation';";
            command.ExecuteNonQuery();
        }

        _fixture.Restart();
        var next = _fixture.Service();
        Assert.Equal(1, next.Recover().SetAside);

        Assert.Equal(1, next.Janitor.Run(LibraryStorageFixture.Start.AddDays(15)).QuarantinedRemoved);
        Assert.True(File.Exists(entry));
        Assert.Equal(1, next.Janitor.Run(LibraryStorageFixture.Start.AddDays(31)).RecentlyDeletedRemoved);
        Assert.False(File.Exists(entry));
    }

    [Fact]
    public void Restoring_an_entry_and_editing_it_in_the_same_Save_is_one_restore_operation_of_the_edited_content()
    {
        // J-8b (A8).
        var (service, catalog, entry) = OneDeletedEntry();
        var content = service.ReadRecentlyDeleted(entry)!;
        var edited = LibraryStorageFixture.Content("scratch", "Scratch", ("scratch", "Scratch edited"), ("new", "New"));
        var changes = Changes.Of(
            catalog,
            writes: [new LibraryWrite("scratch", false, LibraryOrigin.Restored, content.Entry.ContentHash, edited)],
            recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, content.Entry.ContentHash, "scratch")]);

        var prepared = service.PrepareSave(changes);
        var manifest = LibraryManifest.Parse(File.ReadAllBytes(Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json")))).Manifest!;
        var operation = Assert.Single(manifest.Operations);
        Assert.Equal(LibraryOperationKind.Restore, operation.Kind);
        Assert.Equal("scratch.csv", operation.Target);
        Assert.Equal(LibraryContentHashing.Of(LibraryStorageFixture.Managed(edited)), operation.Staged);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(prepared.Save).Status);

        _fixture.Restart();
        var reloaded = _fixture.Service().LoadCatalog();
        Assert.Equal(["Scratch edited", "New"], reloaded.Find("scratch")!.Content.Rows.Select(row => row.Values.Written));
        Assert.Empty(reloaded.RecentlyDeleted);
    }

    [Fact]
    public void An_edited_restore_resumed_from_every_crash_point_ends_edited_with_the_entry_gone_or_not_restored_at_all()
    {
        // J-8b, from every crash point.
        var calls = 0;
        foreach (var timing in (FaultingFileSystem.Timing[])[FaultingFileSystem.Timing.Before, FaultingFileSystem.Timing.After])
        {
            for (var n = 1; calls == 0 || n <= calls; n++)
            {
                using var fixture = new LibraryStorageFixture();
                fixture.Write("deleted/20260901T101010Z.scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
                var start = fixture.Service().LoadCatalog();
                var entry = Assert.Single(start.RecentlyDeleted);
                var edited = LibraryStorageFixture.Content("scratch", "Scratch", ("scratch", "Scratch edited"));
                var changes = Changes.Of(
                    start,
                    writes: [new LibraryWrite("scratch", false, LibraryOrigin.Restored, entry.ContentHash, edited)],
                    recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, entry.ContentHash, "scratch")]);
                var files = new FaultingFileSystem { FailAt = calls == 0 ? null : n, FailTiming = timing };
                try
                {
                    Changes.Save(fixture.Service(files), fixture.Settings, changes);
                }
                catch (Exception)
                {
                    // Died here.
                }

                if (calls == 0)
                {
                    calls = files.MutatingCalls;
                    n = 0;
                    continue;
                }

                fixture.Restart();
                var reloaded = fixture.Service().LoadCatalog();
                if (fixture.StoredGeneration == start.Generation + 1)
                {
                    Assert.Equal("Scratch edited", reloaded.Find("scratch")!.Content.Rows[0].Values.Written);
                    Assert.Empty(reloaded.RecentlyDeleted);
                }
                else
                {
                    Assert.Null(reloaded.Find("scratch"));
                    Assert.Single(reloaded.RecentlyDeleted);
                }
            }
        }
    }

    [Fact]
    public void An_entry_changed_on_disk_after_it_was_read_for_a_restore_makes_the_Save_an_outside_edit()
    {
        // J-8b.
        var (service, catalog, entry) = OneDeletedEntry();
        var content = service.ReadRecentlyDeleted(entry)!;
        File.WriteAllText(Path.Combine(_fixture.Paths.LibraryDeletedDir, entry.EntryName), LibraryStorageFixture.Csv("Scratch", ("changed", "Changed")));

        Assert.Null(service.ReadRecentlyDeleted(entry));
        var prepared = service.PrepareSave(Changes.Of(
            catalog, recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.EntryName, content.Entry.ContentHash, "scratch")]));

        Assert.Equal(LibraryPrepareStatus.OutsideEdit, prepared.Status);
        Assert.Equal(["scratch"], prepared.OutsideEditIds);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
    }

    [Fact]
    public void Recently_deleted_entry_names_parse_whole()
    {
        Assert.True(RecentlyDeletedStore.TryParseEntryName("20260924T201000Z.team-terms.csv", out var stamp, out var sequence, out var original));
        Assert.Equal(LibraryStorageFixture.Start, stamp);
        Assert.Equal(1, sequence);
        Assert.Equal("team-terms.csv", original);
        Assert.True(RecentlyDeletedStore.TryParseEntryName("20260924T201000Z-2.Zulu Notes.csv", out _, out sequence, out original));
        Assert.Equal(2, sequence);
        Assert.Equal("Zulu Notes.csv", original);
        Assert.False(RecentlyDeletedStore.TryParseEntryName("20260924T201000Z-1.team-terms.csv", out _, out _, out _));
        Assert.False(RecentlyDeletedStore.TryParseEntryName("20260924T201000Z-02.team-terms.csv", out _, out _, out _));
        Assert.False(RecentlyDeletedStore.TryParseEntryName("20260924T201000Z.team-terms.txt", out _, out _, out _));
        Assert.False(RecentlyDeletedStore.TryParseEntryName("2026-09-24.team-terms.csv", out _, out _, out _));
        Assert.Equal("custom-github", RecentlyDeletedStore.OriginalId("github.csv"));
        Assert.Equal("20260924T201000Z-2.a.csv", RecentlyDeletedStore.NextEntryName("a.csv", LibraryStorageFixture.Start, new HashSet<string> { "20260924T201000Z.a.csv" }));
    }

    private (Scribe.Core.PostProcessing.DictionaryLibraryService Service, LibraryCatalog Catalog, RecentlyDeletedLibrary Entry) OneDeletedEntry()
    {
        _fixture.Write("scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(start, deletions: [Changes.Delete(start, "scratch")]));
        var catalog = service.LoadCatalog();
        return (service, catalog, Assert.Single(catalog.RecentlyDeleted));
    }
}
