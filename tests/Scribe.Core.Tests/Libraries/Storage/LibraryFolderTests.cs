using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The journal's files beside the libraries, as older builds and the migrations see them (J-7), the checked replace over
/// real files whenever the native replace is refused (J-14, G6), and the janitor as a light step of storage maintenance
/// (contract 3.1.6).
/// </summary>
public sealed class LibraryFolderTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void No_loader_of_this_or_0_4_3s_shape_and_no_migration_sees_a_journal_file()
    {
        // J-7: every kind of file the journal and its neighbours keep, made the way they are made.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.Write("old.csv", LibraryStorageFixture.Csv("Old", ("old", "Old")));
        _fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub P")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog,
            writes: [new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: JsonEditsOverlay.Edits("github", ("get hub", "GitHub S")))],
            deletions: [Changes.Delete(catalog, "old")]));
        catalog = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(catalog, writes: [Changes.Edit(catalog, "team", LibraryStorageFixture.Content("team", "Team", ("kube", "K8s")))]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        const string Id = "0123456789abcdef0123456789abcdef";
        _fixture.Write($"~g9-{Id}-0.scribe-staged", "staged");
        _fixture.Write($"~g9-{Id}-0.scribe-backup", "backup");
        _fixture.Write($"~g9-{Id}-0-2.scribe-backup", "backup 2");
        _fixture.Write($"edits/~g9-{Id}-1.scribe-backup", "edits backup");
        _fixture.Write($"journal/g9-{Id}.set-aside.20260924T201000Z.json", "{}");
        _fixture.Write($"journal/g9-{Id}.manifest.json.tmp", "{}");
        _fixture.Write($"journal/orphans/~g8-{Id}-0.scribe-backup", "orphan");
        _fixture.Write($"journal/g9-{Id}/0.redo", "redo");

        // 0.4.3's own call, and its whole selection.
        Assert.Equal(["team.csv"], Directory.GetFiles(_fixture.LibrariesDir, "*.csv").Select(Path.GetFileName));
        Assert.Equal(["team"], Legacy043LibrarySelection.Libraries(_fixture.LibrariesDir).Where(library => !library.BuiltIn).Select(library => library.Id));

        // Every kind of file is really there.
        Assert.True(_fixture.Exists("journal/state.witness"));
        Assert.True(_fixture.Exists("edits/github.previous.json"));
        Assert.NotEmpty(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
        Assert.NotEmpty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.manifest.json"));

        // The startup migration copies only the libraries themselves.
        var destination = Path.Combine(_fixture.Root, "migrated");
        AppPaths.TryMigrateLibraries(_fixture.LibrariesDir, destination);
        Assert.Equal(["team.csv"], Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName));

        // This build's loader lists only the libraries too.
        Assert.Equal(["team"], service.LoadCatalog().Libraries.Where(library => !library.Content.BuiltIn).Select(library => library.Content.Id));
        service.CompleteSave(prepared.Save);
    }

    [Fact]
    public void The_checked_replace_installs_over_real_files_whenever_the_native_replace_is_refused()
    {
        // J-14: the one procedure, over the real file system; only File.Replace itself is refused.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub P")));
        var files = new RefusingNativeReplace();
        var service = _fixture.Service(files);
        var catalog = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team", "Team", ("kube", "K8s"));
        var document = JsonEditsOverlay.Edits("github", ("get hub", "GitHub S"));
        var previous = File.ReadAllBytes(_fixture.PathOf("edits/github.json"));

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(
            catalog, writes: [Changes.Edit(catalog, "team", edited), new LibraryWrite("github", true, LibraryOrigin.Existing, catalog.Find("github")!.ContentHash, Edits: document)]));

        Assert.Equal(2, files.Refusals);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Equal(JsonEditsOverlay.Instance.WriteEdits(document), File.ReadAllBytes(_fixture.PathOf("edits/github.json")));
        Assert.Equal(previous, File.ReadAllBytes(_fixture.PathOf("edits/github.previous.json")));
        Assert.DoesNotContain(_fixture.AllFiles(), file => file.Contains(".scribe-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_read_only_target_is_replaced_by_the_checked_path_and_leaves_no_journal_file_behind()
    {
        // J-14 without any fake: Windows refuses File.Replace over a read-only file, which is the real refusal the checked
        // replace exists for, and a spare backup it leaves read-only must not strand the Save.
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        File.SetAttributes(_fixture.PathOf("team.csv"), FileAttributes.ReadOnly);
        var service = _fixture.Service(PhysicalLibraryFileSystem.Instance);
        var catalog = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team", "Team", ("kube", "K8s"));

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Edit(catalog, "team", edited)]));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.DoesNotContain(_fixture.AllFiles(), file => file.Contains(".scribe-", StringComparison.Ordinal));
    }

    [Fact]
    public void Storage_maintenance_runs_the_library_janitor_as_a_light_step_with_its_own_clock()
    {
        _fixture.Write("scratch.csv", LibraryStorageFixture.Csv("Scratch", ("s", "S")));
        var service = _fixture.Service();
        var catalog = service.LoadCatalog();
        Changes.Save(service, _fixture.Settings, Changes.Of(catalog, deletions: [Changes.Delete(catalog, "scratch")]));
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
        var clock = new ManualTimeProvider(LibraryStorageFixture.Start.AddDays(31));
        using var maintenance = new StorageMaintenance(
            _fixture.Database, new HistoryRepository(_fixture.Database), new CleanupFailureLog(_fixture.Database), NullLogger.Instance,
            clock, StorageMaintenanceOptions.Default, service.Janitor);

        var report = maintenance.RunOnce(_fixture.Settings.Load())!;

        Assert.Equal(1, report.LibraryRetention.RecentlyDeletedRemoved);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
    }

    /// <summary>The real file system, except that File.Replace is refused, as the Store package's redirected folder may.</summary>
    private sealed class RefusingNativeReplace : ILibraryFileSystem
    {
        private readonly ILibraryFileSystem _inner = PhysicalLibraryFileSystem.Instance;

        public int Refusals { get; private set; }

        public bool Exists(string path) => _inner.Exists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAllBytesDurably(string path, ReadOnlySpan<byte> bytes) => _inner.WriteAllBytesDurably(path, bytes);

        public void Replace(string installCopy, string target, string backup)
        {
            Refusals++;
            throw new UnauthorizedAccessException("refused");
        }

        public void Move(string source, string destination, bool overwrite) => _inner.Move(source, destination, overwrite);

        public void Delete(string path) => _inner.Delete(path);

        public IEnumerable<string> EnumerateFiles(string directory, string pattern) => _inner.EnumerateFiles(directory, pattern);

        public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
    }
}
