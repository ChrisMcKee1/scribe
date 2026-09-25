using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Sub-stream O's request (w1b-o.md; contract 3.1.4, 3.3 and 7.2): <c>Apply(shipped, null)</c> means the built-in has no
/// edits document, which yields every shipped row. A built-in whose document exists but cannot be used (unreadable,
/// newer, or held open by another app) is never handed to the overlay as if it had none: the catalog holds its rows back
/// and states why, so a term the user turned off never comes back through composition, the legacy seam or the list.
/// </summary>
public sealed class LibraryPausedBuiltInTests : IDisposable
{
    private const string OffSpoken = "copilot";
    private const string EditedSpoken = "get hub";
    private const string EditedWritten = "GitHub Enterprise";
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<string> Pausing => ["damaged", "newer"];

    // The user turned the shipped "copilot" row off and edited "get hub": the document the tests start from.
    private static byte[] UsersDocument() => JsonEditsOverlay.Instance.WriteEdits(new BuiltInLibraryEdits("github",
    [
        new BuiltInTermEdit(LibraryTermKey.From(OffSpoken), BuiltInTermIntent.Off, new TermValues(OffSpoken, "Copilot"), null),
        new BuiltInTermEdit(
            LibraryTermKey.From(EditedSpoken), BuiltInTermIntent.Edited, new TermValues(EditedSpoken, "GitHub"), new TermValues(EditedSpoken, EditedWritten)),
    ]));

    private Scribe.Core.PostProcessing.DictionaryLibraryService Service(RecordingOverlay overlay, ILibraryFileSystem? files = null) =>
        _fixture.Service(files: files, overlay: overlay);

    private static void AssertUsersEditsInForce(Scribe.Core.PostProcessing.DictionaryLibraryService service)
    {
        var github = service.LoadCatalog().Find("github")!;
        Assert.Contains(github.Content.Rows, row => row.Values.Spoken == OffSpoken && !row.Values.Enabled);
        Assert.DoesNotContain(service.GetEnabledLibraryEntries(["github"]), entry => entry.Pattern == OffSpoken);
        Assert.Contains(service.GetEnabledLibraryEntries(["github"]), entry => entry.Replacement == EditedWritten);
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Pattern == OffSpoken);
    }

    private static void AssertHeldBack(Scribe.Core.PostProcessing.DictionaryLibraryService service, RecordingOverlay overlay, LibraryFileState state)
    {
        var github = service.LoadCatalog().Find("github")!;
        Assert.Equal(state, github.State);
        Assert.Empty(github.Content.Rows);

        // The legacy seam composes whatever rows the catalog holds, with no state check of its own: nothing of github.
        Assert.Empty(service.GetEnabledLibraryEntries(["github"]));
        Assert.Empty(service.GetLibraries().Single(library => library.Id == "github").Entries);
        Assert.Empty(service.Current.Entries);
        Assert.Empty(service.Current.AiEntries);
        if (state is LibraryFileState.Unreadable or LibraryFileState.Newer)
        {
            Assert.DoesNotContain("github", service.Current.AiScope.PermittedLibraryIds);
        }

        Assert.Equal(0, overlay.NullApplies("github"));
    }

    [Theory]
    [MemberData(nameof(Pausing))]
    public void An_unusable_edits_document_pauses_its_built_in_and_none_of_its_rows_reach_composition(string how)
    {
        _fixture.WriteBytes("edits/github.json", UsersDocument());
        _fixture.SaveEnabled("github");
        var earlierOverlay = new RecordingOverlay();
        var earlier = Service(earlierOverlay);
        AssertUsersEditsInForce(earlier);

        var unusable = how == "damaged"
            ? System.Text.Encoding.UTF8.GetBytes("{ \"version\": 1, \"library\": \"github\", \"terms\": [ damaged")
            : System.Text.Encoding.UTF8.GetBytes("{ \"version\": 2, \"library\": \"github\", \"terms\": [] }");
        _fixture.WriteBytes("edits/github.json", unusable);
        var expected = how == "damaged" ? LibraryFileState.Unreadable : LibraryFileState.Newer;

        // A process that read the good document earlier pauses too: only a lock keeps the content last read.
        earlierOverlay.Reset();
        AssertHeldBack(earlier, earlierOverlay, expected);
        var freshOverlay = new RecordingOverlay();
        var fresh = Service(freshOverlay);
        AssertHeldBack(fresh, freshOverlay, expected);

        // An unrelated Save leaves the document exactly as it is, and the library paused.
        var catalog = fresh.LoadCatalog();
        var saved = Changes.Save(fresh, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Create(LibraryStorageFixture.Content("custom-notes", "Notes", ("n", "N")))]));
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(unusable, File.ReadAllBytes(_fixture.PathOf("edits/github.json")));
        AssertHeldBack(fresh, freshOverlay, expected);
    }

    [Fact]
    public void A_locked_edits_document_keeps_the_content_last_read_with_its_off_rows_off_and_supplies_nothing_at_a_fresh_start()
    {
        _fixture.WriteBytes("edits/github.json", UsersDocument());
        _fixture.SaveEnabled("github");
        var earlierOverlay = new RecordingOverlay();
        var earlier = Service(earlierOverlay);
        AssertUsersEditsInForce(earlier);

        using (new FileStream(_fixture.PathOf("edits/github.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var freshOverlay = new RecordingOverlay();
            AssertHeldBack(Service(freshOverlay), freshOverlay, LibraryFileState.AwaitingRelease);

            earlierOverlay.Reset();
            Assert.Equal(LibraryFileState.AwaitingRelease, earlier.LoadCatalog().Find("github")!.State);
            AssertUsersEditsInForce(earlier);
            Assert.Equal(0, earlierOverlay.NullApplies("github"));
        }
    }

    [Fact]
    public void An_edits_folder_that_cannot_be_listed_never_reads_as_a_built_in_with_no_document()
    {
        // Enumerating edits\ fails (access denied): the document may well be there, so no built-in may read as having none.
        _fixture.WriteBytes("edits/github.json", UsersDocument());
        _fixture.SaveEnabled("github");
        var earlierOverlay = new RecordingOverlay();
        var files = new UnlistableFolder(_fixture.Paths.LibraryEditsDir);
        var earlier = Service(earlierOverlay, files);
        AssertUsersEditsInForce(earlier);
        var generation = _fixture.StoredGeneration;
        files.Unlistable = true;

        // At a fresh start the built-in supplies nothing; a process that read the document keeps it, the off row off.
        var freshOverlay = new RecordingOverlay();
        var fresh = Service(freshOverlay, files);
        AssertHeldBack(fresh, freshOverlay, LibraryFileState.AwaitingRelease);
        earlierOverlay.Reset();
        Assert.Equal(LibraryFileState.AwaitingRelease, earlier.LoadCatalog().Find("github")!.State);
        AssertUsersEditsInForce(earlier);
        Assert.Equal(0, earlierOverlay.NullApplies("github"));

        // Nothing is planned against a read that could not see the folder: a Save fails and writes nothing.
        var before = _fixture.AllFiles();
        var catalog = fresh.LoadCatalog();
        var prepared = fresh.PrepareSave(Changes.Of(catalog, writes: [Changes.Create(LibraryStorageFixture.Content("custom-notes", "Notes", ("n", "N")))]));
        Assert.Equal(LibraryPrepareStatus.Failed, prepared.Status);
        Assert.Equal(LibraryIoFailure.AccessDenied, prepared.Failure);
        Assert.Equal(before, _fixture.AllFiles());
        Assert.Equal(generation, _fixture.StoredGeneration);
        Assert.Throws<InvalidOperationException>(() => fresh.Import("pattern,replacement\nn,N\n", "Notes"));
        Assert.Equal(before, _fixture.AllFiles());

        // Once it can be listed again, the user's document is read as it is and nothing of it was lost.
        files.Unlistable = false;
        AssertUsersEditsInForce(fresh);
        Assert.Equal(UsersDocument(), File.ReadAllBytes(_fixture.PathOf("edits/github.json")));
    }

    [Fact]
    public void A_first_start_whose_edits_folder_cannot_be_listed_adopts_in_memory_and_commits_nothing()
    {
        _fixture.WriteBytes("edits/github.json", UsersDocument());
        _fixture.Write("team.csv", LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("github", "team");
        var files = new UnlistableFolder(_fixture.Paths.LibraryEditsDir) { Unlistable = true };
        var overlay = new RecordingOverlay();

        var catalog = Service(overlay, files).LoadCatalog();

        Assert.Contains("team", catalog.LocalState.EnabledIds);
        Assert.Equal(0, overlay.NullApplies("github"));
        Assert.Equal(LibraryFileState.AwaitingRelease, catalog.Find("github")!.State);
        Assert.Equal(0, _fixture.StoredGeneration);
        Assert.Null(_fixture.Row(LibrarySettingKeys.State));
        Assert.False(_fixture.Exists("journal/state.witness"));
    }

    [Fact]
    public void An_edits_folder_Windows_will_not_list_is_never_read_as_empty_by_the_real_file_system()
    {
        // Directory.Exists answers false for a folder whose attributes cannot be read, access denied included, so a check
        // before enumerating turns a refused listing into an empty, successful one. Denied listing edits\ and reading its
        // attributes, and listing its parent, the real file system must fail the listing, and the built-in hold back.
        _fixture.WriteBytes("edits/github.json", UsersDocument());
        _fixture.SaveEnabled("github");
        AssertUsersEditsInForce(Service(new RecordingOverlay()));
        var overlay = new RecordingOverlay();

        using (DeniedListing.On(_fixture.Paths.LibraryEditsDir, _fixture.LibrariesDir))
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                PhysicalLibraryFileSystem.Instance.EnumerateFiles(_fixture.Paths.LibraryEditsDir, "*.json").ToList());
            AssertHeldBack(Service(overlay), overlay, LibraryFileState.AwaitingRelease);
        }

        // A folder that is genuinely not there is still an empty listing, and a file where a folder should be is an error.
        Assert.Empty(PhysicalLibraryFileSystem.Instance.EnumerateFiles(Path.Combine(_fixture.Root, "absent"), "*.json"));
        Assert.Empty(PhysicalLibraryFileSystem.Instance.EnumerateDirectories(Path.Combine(_fixture.Root, "absent")));
        Assert.ThrowsAny<IOException>(() =>
            PhysicalLibraryFileSystem.Instance.EnumerateFiles(_fixture.PathOf("edits/github.json"), "*.json").ToList());
        AssertUsersEditsInForce(Service(new RecordingOverlay()));
    }

    /// <summary>Deny access control entries for the current user, removed on dispose so the fixture can delete the folder.</summary>
    private sealed class DeniedListing : IDisposable
    {
        private readonly List<(DirectoryInfo Folder, System.Security.AccessControl.FileSystemAccessRule Rule)> _rules = [];

        public static DeniedListing On(string folder, string parent)
        {
            var denied = new DeniedListing();
            denied.Deny(folder,
                System.Security.AccessControl.FileSystemRights.ListDirectory |
                System.Security.AccessControl.FileSystemRights.ReadAttributes |
                System.Security.AccessControl.FileSystemRights.ReadExtendedAttributes);
            denied.Deny(parent, System.Security.AccessControl.FileSystemRights.ListDirectory);
            return denied;
        }

        public void Dispose()
        {
            foreach (var (folder, rule) in _rules)
            {
                var security = folder.GetAccessControl();
                security.RemoveAccessRuleSpecific(rule);
                folder.SetAccessControl(security);
            }
        }

        private void Deny(string path, System.Security.AccessControl.FileSystemRights rights)
        {
            var folder = new DirectoryInfo(path);
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!, rights,
                System.Security.AccessControl.AccessControlType.Deny);
            var security = folder.GetAccessControl();
            security.AddAccessRule(rule);
            folder.SetAccessControl(security);
            _rules.Add((folder, rule));
        }
    }

    /// <summary>The real file system, except that one folder cannot be listed while <see cref="Unlistable"/> is set.</summary>
    private sealed class UnlistableFolder(string folder) : ILibraryFileSystem
    {
        private readonly ILibraryFileSystem _inner = PhysicalLibraryFileSystem.Instance;

        public bool Unlistable { get; set; }

        public bool Exists(string path) => _inner.Exists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAllBytesDurably(string path, ReadOnlySpan<byte> bytes) => _inner.WriteAllBytesDurably(path, bytes);

        public void Replace(string installCopy, string target, string backup) => _inner.Replace(installCopy, target, backup);

        public void Move(string source, string destination, bool overwrite) => _inner.Move(source, destination, overwrite);

        public void Delete(string path) => _inner.Delete(path);

        public IEnumerable<string> EnumerateFiles(string directory, string pattern) =>
            Unlistable && string.Equals(Path.GetFullPath(directory), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("injected: the folder cannot be listed")
                : _inner.EnumerateFiles(directory, pattern);

        public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
    }
}
