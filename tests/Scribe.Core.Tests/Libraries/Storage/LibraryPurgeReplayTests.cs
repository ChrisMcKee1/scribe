using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Round 3, A12: a purge of an entry listed unreadable (no pre-image hash) takes the entry unread, once, and the claim it
/// took it into is the only evidence that it did. That claim stays until the whole manifest retires, surviving a retry
/// and a restart, and counting in the done-condition, so another attempt at the same manifest never takes, and never
/// deletes, a file written to the entry's name afterwards. The sibling write stays locked, so the manifest stays
/// unresolved while the other app writes; the entry's read fails at the load that lists it, which is what makes it
/// unreadable to the draft.
/// </summary>
public sealed class LibraryPurgeReplayTests : IDisposable
{
    private const string Entry = "20260801T090000Z.junk.csv";

    private static readonly byte[] Original = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Junk", ("junk", "Junk")));

    private static readonly byte[] Later = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Junk", ("junk", "Junk written afterwards")));

    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public enum Retry
    {
        /// <summary>The same process tries the manifest again.</summary>
        SameProcess,

        /// <summary>The next process tries it again.</summary>
        Restart,

        /// <summary>The process dies right after the first delete of anything the purge took, and the next one retries.</summary>
        CrashAfterFirstDelete,
    }

    private static LibraryContent Edited => LibraryStorageFixture.Content("team", "Team", ("kube", "K8s"));

    private string EntryPath => _fixture.PathOf("deleted/" + Entry);

    private bool IsClaim(string path) =>
        string.Equals(Path.GetDirectoryName(path), _fixture.Paths.LibraryDeletedDir, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).StartsWith("~g", StringComparison.Ordinal);

    private bool Holds(byte[] bytes)
    {
        var hash = LibraryContentHashing.Of(bytes);
        return _fixture.AllFiles().Any(file => _fixture.HashOf(file) == hash);
    }

    // A library to write and an entry whose read fails at the load that lists it: the draft sees it unreadable.
    private (DictionaryLibraryService Service, LibraryCatalog Start, RecentlyDeletedLibrary Entry) Arrange(FaultingFileSystem files)
    {
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.WriteBytes("deleted/" + Entry, Original);
        _fixture.SaveEnabled("team");
        files.ReadFault = path => string.Equals(path, EntryPath, StringComparison.OrdinalIgnoreCase) ? FaultingFileSystem.SharingViolation() : null;
        var service = _fixture.Service(files);
        var start = service.LoadCatalog();
        files.ReadFault = null;
        var entry = Assert.Single(start.RecentlyDeleted);
        Assert.Null(entry.ContentHash);
        return (service, start, entry);
    }

    private static LibraryChangeSet WriteAndPurge(LibraryCatalog start, RecentlyDeletedLibrary entry) => Changes.Of(
        start,
        writes: [Changes.Edit(start, "team", Edited)],
        recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.DeletePermanently, entry.EntryName, entry.ContentHash)]);

    [Theory]
    [InlineData(Retry.SameProcess)]
    [InlineData(Retry.Restart)]
    [InlineData(Retry.CrashAfterFirstDelete)]
    public void An_unversioned_purge_retried_behind_a_deferred_sibling_never_takes_a_file_written_to_its_entry_name_afterwards(Retry retry)
    {
        var files = new FaultingFileSystem();
        var (service, start, entry) = Arrange(files);
        if (retry == Retry.CrashAfterFirstDelete)
        {
            files.AfterMutation = (kind, path, _) =>
            {
                if (kind == "delete" && IsClaim(path))
                {
                    files.CrashNow();
                }
            };
        }

        using (new FileStream(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                Changes.Save(service, _fixture.Settings, WriteAndPurge(start, entry));
            }
            catch (Exception) when (retry == Retry.CrashAfterFirstDelete)
            {
                // The process died here.
            }

            Assert.False(File.Exists(EntryPath), "the purge did not take the entry");
            Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));

            // Another app writes to the entry's name while the manifest waits for the sibling write.
            File.WriteAllBytes(EntryPath, Later);
            if (retry != Retry.SameProcess)
            {
                _fixture.Restart();
                service = _fixture.Service();
            }

            var pending = service.LoadCatalog();

            Assert.Equal(Later, File.ReadAllBytes(EntryPath));
            Assert.Equal(1, pending.FilesAwaitingRelease);
            Assert.Equal(1, service.Recover().FilesAwaitingRelease);
            Assert.Equal(Later, File.ReadAllBytes(EntryPath));
        }

        // Released: the write installs, the manifest retires, and only then does the entry it took leave for good.
        var settled = service.LoadCatalog();

        Assert.Equal(0, settled.FilesAwaitingRelease);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.Equal(Later, File.ReadAllBytes(EntryPath));
        Assert.Contains(settled.RecentlyDeleted, deleted => deleted.ContentHash == LibraryContentHashing.Of(Later));
        Assert.DoesNotContain(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir), IsClaim);
        Assert.False(Holds(Original), "the entry the user deleted permanently is still on disk");
    }

    [Fact]
    public void A_crash_at_retirement_right_after_the_purge_evidence_goes_never_lets_a_retry_take_the_later_file()
    {
        // The manifest leaves first, the retirement point, and the evidence only after it: a retry never meets a pending
        // manifest without its evidence.
        var files = new FaultingFileSystem();
        var (service, start, entry) = Arrange(files);
        using (new FileStream(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Changes.Save(service, _fixture.Settings, WriteAndPurge(start, entry));
            File.WriteAllBytes(EntryPath, Later);
        }

        files.AfterMutation = (kind, path, _) =>
        {
            if (kind == "delete" && IsClaim(path))
            {
                files.CrashNow();
            }
        };
        try
        {
            service.LoadCatalog();
        }
        catch (Exception)
        {
            // The process died here.
        }

        _fixture.Restart();
        var settled = _fixture.Service().LoadCatalog();

        Assert.Equal(Later, File.ReadAllBytes(EntryPath));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.Contains(settled.RecentlyDeleted, deleted => deleted.ContentHash == LibraryContentHashing.Of(Later));
        Assert.False(Holds(Original), "the entry the user deleted permanently is still on disk");
    }

    [Fact]
    public void An_unversioned_purge_whose_manifest_is_set_aside_puts_the_entry_it_took_back_beside_the_later_file()
    {
        // The claim that is the purge's evidence is still Recently deleted bytes: a set-aside returns it, never loses it.
        var files = new FaultingFileSystem();
        var (service, start, entry) = Arrange(files);
        using (new FileStream(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Changes.Save(service, _fixture.Settings, WriteAndPurge(start, entry));
            File.WriteAllBytes(EntryPath, Later);
            using (var connection = _fixture.Database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM settings WHERE key = 'libraries.generation';";
                command.ExecuteNonQuery();
            }

            _fixture.Restart();
            Assert.Equal(1, _fixture.Service().Recover().SetAside);
        }

        Assert.Equal(Later, File.ReadAllBytes(EntryPath));
        Assert.True(Holds(Original), "the entry the purge took was lost when its manifest was set aside");
        Assert.DoesNotContain(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir), IsClaim);
        Assert.Equal(2, Directory.GetFiles(_fixture.Paths.LibraryDeletedDir, "*.csv").Length);
    }
}
