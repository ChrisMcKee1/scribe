using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 2, A2: the journal decides only from a complete inventory. A listing of pending or set-aside manifests that
/// fails (while other listings succeed) leaves the storage unresolved: no generation advances (adoption, a Save, the
/// wrappers), no orphan is removed, so neither a committed Save's redo images nor a quarantined manifest's are deleted;
/// and a redo image that cannot be read right now holds its manifest instead of setting it aside. Enumeration and read
/// failures are injected on their own, with no mutating call failing.
/// </summary>
public sealed class LibraryInventoryTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static IOException Transient() => FaultingFileSystem.SharingViolation();

    private LibraryContent Edited => LibraryStorageFixture.Content("team", "Team", ("kube", "K8s"));

    // A Save of team.csv committed by the settings transaction; the process ended before completion.
    private string CommittedNotCompleted()
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team");
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team", Edited)]));
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        _fixture.Restart();
        return Assert.Single(Directory.GetDirectories(_fixture.Paths.LibraryJournalDir, "g*"));
    }

    private bool IsJournal(string directory) =>
        string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(_fixture.Paths.LibraryJournalDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private void AssertFenced(Scribe.Core.PostProcessing.DictionaryLibraryService service)
    {
        var catalog = service.LoadCatalog();
        var generation = _fixture.StoredGeneration;
        var prepared = service.PrepareSave(Changes.Of(catalog, state: Changes.With(catalog.LocalState, disable: ["team"])));
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, prepared.Status);
        Assert.Throws<InvalidOperationException>(() => service.Import("pattern,replacement\nn,N\n", "Notes"));
        Assert.Equal(generation, _fixture.StoredGeneration);
        Assert.True(catalog.FilesAwaitingRelease > 0);
    }

    [Fact]
    public void A_failed_manifest_listing_removes_no_orphan_and_fences_the_generation_until_it_succeeds()
    {
        var redo = CommittedNotCompleted();
        var files = new FaultingFileSystem
        {
            EnumerateFault = (directory, pattern) =>
                IsJournal(directory) && pattern.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) ? Transient() : null,
        };

        var blind = _fixture.Service(files);
        blind.LoadCatalog();

        Assert.True(Directory.Exists(redo), "a committed Save's redo images were removed as orphans");
        Assert.NotEmpty(Directory.GetFiles(redo));
        AssertFenced(blind);
        Assert.True(Directory.Exists(redo));

        // Once the listing succeeds, the committed Save completes.
        files.EnumerateFault = null;
        var catalog = blind.LoadCatalog();
        Assert.Equal("K8s", catalog.Find("team")!.Content.Rows[0].Values.Written);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
    }

    [Fact]
    public void A_failed_set_aside_listing_keeps_a_quarantined_manifests_own_files_and_fences_the_generation()
    {
        var redo = CommittedNotCompleted();
        using (var connection = _fixture.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM settings WHERE key = 'libraries.generation';";
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, _fixture.Service().Recover().SetAside);
        Assert.True(Directory.Exists(redo));
        _fixture.Restart();
        var files = new FaultingFileSystem
        {
            EnumerateFault = (directory, pattern) =>
                IsJournal(directory) && pattern.Contains("set-aside", StringComparison.OrdinalIgnoreCase) ? Transient() : null,
        };

        var blind = _fixture.Service(files);
        blind.LoadCatalog();

        Assert.True(Directory.Exists(redo), "a quarantined manifest's redo images were removed as orphans");
        Assert.NotEmpty(Directory.GetFiles(redo));
        AssertFenced(blind);
        files.EnumerateFault = null;
        blind.LoadCatalog();
        Assert.True(Directory.Exists(redo));
    }

    [Fact]
    public void A_listing_that_fails_after_recovery_decided_removes_no_orphan()
    {
        // Recovery lists the journal, cannot finish the committed Save (its target is held open), and orphan removal lists
        // again: that second listing fails, and nothing of the unresolved manifest may be taken for an orphan.
        var redo = CommittedNotCompleted();
        var manifestListings = 0;
        var files = new FaultingFileSystem
        {
            EnumerateFault = (directory, pattern) =>
                IsJournal(directory) && pattern.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) && ++manifestListings == 2
                    ? Transient()
                    : null,
        };

        using (new FileStream(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _fixture.Service(files).LoadCatalog();
        }

        Assert.True(manifestListings >= 2);
        Assert.True(Directory.Exists(redo), "an unresolved manifest's redo images were removed as orphans");
        files.EnumerateFault = null;
        Assert.Equal("K8s", _fixture.Service(files).LoadCatalog().Find("team")!.Content.Rows[0].Values.Written);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
    }

    [Fact]
    public void A_redo_image_that_cannot_be_read_right_now_holds_its_manifest_and_never_sets_it_aside()
    {
        var redo = CommittedNotCompleted();
        var locked = true;
        var files = new FaultingFileSystem
        {
            ReadFault = path => locked && path.EndsWith(".redo", StringComparison.OrdinalIgnoreCase) ? Transient() : null,
        };

        var blind = _fixture.Service(files);
        blind.LoadCatalog();

        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*"));
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));
        Assert.True(Directory.Exists(redo));
        AssertFenced(blind);

        locked = false;
        Assert.Equal("K8s", blind.LoadCatalog().Find("team")!.Content.Rows[0].Values.Written);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
    }

    [Fact]
    public void A_failed_listing_of_the_redo_folders_removes_nothing_and_later_orphans_still_go()
    {
        // The folder listing that orphan removal itself makes: its failure removes nothing, and says nothing about orphans.
        CommittedNotCompleted();
        var orphan = Path.Combine(_fixture.Paths.LibraryJournalDir, LibraryJournalNames.RedoFolder(9, "0123456789abcdef0123456789abcdef"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "0.redo"), "spare");
        var files = new FaultingFileSystem
        {
            EnumerateFault = (directory, pattern) => IsJournal(directory) && pattern == "<folders>" ? Transient() : null,
        };

        var service = _fixture.Service(files);
        service.LoadCatalog();

        Assert.True(Directory.Exists(orphan));
        files.EnumerateFault = null;
        service.Recover();
        Assert.False(Directory.Exists(orphan));
        Assert.Equal("K8s", service.LoadCatalog().Find("team")!.Content.Rows[0].Values.Written);
    }
}
