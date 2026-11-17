using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Integration;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// J-2b (contract 8.1, amended after J's review, J's A8): when completion cannot read the stored generation after
/// SaveBundle, whether the settings transaction committed is not known, and completion says so
/// (<see cref="LibrarySaveStatus.CommitUnknown"/>, never <see cref="LibrarySaveStatus.NotCommitted"/>, which the shell words
/// as a Save that failed). The manifest and its redo images stay pending, neither discarded nor retired on that evidence;
/// new Saves are fenced (PreviousSaveUnfinished, no failure), adoption commits nothing and the wrappers refuse; the
/// permission gate keeps the scope PrepareSave narrowed; every load while the read keeps failing leaves the journal as it
/// is, because a failed read is never taken for an absent or unparsable generation row; and the first read that succeeds
/// settles the manifest by the ordinary recovery rules, installing it or discarding it.
/// </summary>
public sealed class LibraryCommitUnknownTests : IDisposable
{
    private const string UnknownTemplate =
        "Library save outcome unknown for generation {Generation}: the stored generation could not be read ({Failure}); {Operations} prepared operation(s) kept pending.";

    private static readonly string TeamCsv = LibraryStorageFixture.Csv("Team", ("kube", "Kubernetes"));

    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static LibraryContent Edited => LibraryStorageFixture.Content("team", "Team", ("kube", "K8s"));

    // team, other and gone are permitted; newly is on with its AI box off.
    private (DictionaryLibraryService Service, UnreadableGeneration Settings, LibraryCatalog Start, AiVocabularyScope Admitted) Arrange(
        ILibraryFileSystem? files = null)
    {
        _fixture.Write("team.csv", TeamCsv);
        _fixture.Write("other.csv", LibraryStorageFixture.Csv("Other", ("o", "O")));
        _fixture.Write("gone.csv", LibraryStorageFixture.Csv("Gone", ("g", "G")));
        _fixture.Write("newly.csv", LibraryStorageFixture.Csv("Newly", ("n", "N")));
        _fixture.SaveEnabled("team", "other", "gone", "newly");
        var settings = new UnreadableGeneration(_fixture.Settings);
        var service = _fixture.Service(files, settings: settings);
        var first = service.LoadCatalog();
        var off = Changes.Save(service, settings, Changes.Of(first, state: Changes.With(first.LocalState, ai: [("newly", false)])));
        Assert.Equal(LibrarySaveStatus.Applied, off.Outcome!.Status);
        var start = service.LoadCatalog();
        var admitted = service.Current.AiScope;
        Assert.Equal(new[] { "gone", "other", "team" }, admitted.PermittedLibraryIds.Order(StringComparer.Ordinal));
        return (service, settings, start, admitted);
    }

    // The Save edits team and revokes it, deletes gone, grants newly, and leaves other alone.
    private static LibraryChangeSet GrantRevokeAndDelete(LibraryCatalog start) => Changes.Of(
        start,
        writes: [Changes.Edit(start, "team", Edited)],
        deletions: [Changes.Delete(start, "gone")],
        state: Changes.With(start.LocalState, ai: [("team", false), ("newly", true)]));

    private static AiVocabularyScope Holding(LibraryCatalog catalog, string id) =>
        new(catalog.Generation, [KeyValuePair.Create(id, catalog.Find(id)!.ContentHash)]);

    // Every journal file with its bytes, the manifest and its redo images included, compared whole.
    private SortedDictionary<string, string> JournalFiles() => new(
        Directory.EnumerateFiles(_fixture.Paths.LibraryJournalDir, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(_fixture.Paths.LibraryJournalDir, path).Replace('\\', '/'),
            path => Convert.ToHexString(File.ReadAllBytes(path)),
            StringComparer.Ordinal),
        StringComparer.Ordinal);

    private static bool IsManifestOrRedo(string name) =>
        name.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.Ordinal) ||
        name.EndsWith(LibraryJournalNames.RedoSuffix, StringComparison.Ordinal);

    private bool IsJournal(string directory) =>
        string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), Path.GetFullPath(_fixture.Paths.LibraryJournalDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // The fence while the outcome is unknown, and the gate's narrowed scope: nothing the Save grants, revokes or deletes.
    private void AssertUnknownAndFenced(DictionaryLibraryService service, LibraryCatalog start, AiVocabularyScope admitted)
    {
        var refused = service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["other"])));
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, refused.Status);
        Assert.Equal(LibraryIoFailure.None, refused.Failure);
        Assert.Null(refused.Save);
        Assert.Throws<InvalidOperationException>(() => service.Import("pattern,replacement\nn,N\n", "Notes"));
        Assert.Throws<InvalidOperationException>(() => service.Remove("other"));
        Assert.True(_fixture.Exists("other.csv"));
        Assert.False(_fixture.Exists("notes.csv"));
        Assert.False(service.TryHandOff(admitted, () => Assert.Fail("A scope the Save narrowed was handed over.")));
        Assert.False(service.TryHandOff(Holding(start, "team"), () => Assert.Fail("A library the Save revokes was handed over.")));
        Assert.False(service.TryHandOff(Holding(start, "gone"), () => Assert.Fail("A library the Save deletes was handed over.")));
        Assert.False(service.TryHandOff(Holding(start, "newly"), () => Assert.Fail("A library the Save grants was handed over unsettled.")));
        var ran = false;
        Assert.True(service.TryHandOff(Holding(start, "other"), () => ran = true));
        Assert.True(ran);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void J2b_an_outcome_that_cannot_be_read_is_unknown_stays_pending_and_fenced_and_a_later_read_settles_it(bool committed)
    {
        var (service, settings, start, admitted) = Arrange();
        var changed = new List<long>();
        service.Changed += changed.Add;
        var mark = _fixture.Log.Entries.Count;
        settings.FailAfterCommit = true;
        settings.CommitSucceeds = committed;
        SortedDictionary<string, string>? staged = null;

        // The shell's sequence, completing from a finally: SaveBundle commits (or fails before its commit), and the read
        // of the stored generation that completion makes after it fails.
        var (prepared, outcome, commitFailure) = Changes.Save(service, settings, GrantRevokeAndDelete(start), () => staged = JournalFiles());

        var save = prepared.Save!;
        var stored = committed ? save.Generation : start.Generation;
        Assert.Equal(committed, commitFailure is null);
        Assert.Equal(stored, _fixture.StoredGeneration);
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        Assert.Equal(start.Generation, outcome.CommittedGeneration);
        Assert.Equal(LibraryIoFailure.None, outcome.Failure);
        Assert.Equal(0, outcome.FilesAwaitingRelease);
        Assert.Empty(outcome.KeptVersions);

        // 7.4's line, counts and the generation only.
        var line = Assert.Single(_fixture.Log.Entries.Skip(mark), entry => entry.State.Contains(("{OriginalFormat}", (string?)UnknownTemplate)));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains(("Generation", (string?)save.Generation.ToString(CultureInfo.InvariantCulture)), line.State);
        Assert.Contains(("Operations", (string?)"2"), line.State);
        Assert.DoesNotContain("team", line.Message, StringComparison.OrdinalIgnoreCase);

        // The manifest and its redo images are on disk exactly as prepared: neither discarded nor retired.
        Assert.NotNull(staged);
        Assert.Single(staged.Keys, name => name.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.Ordinal));
        Assert.Contains(staged.Keys, name => name.EndsWith(LibraryJournalNames.RedoSuffix, StringComparison.Ordinal));
        Assert.Equal(staged, JournalFiles());
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.True(_fixture.Exists("gone.csv"));
        AssertUnknownAndFenced(service, start, admitted);

        // Every load while the read keeps failing leaves the journal as it is, whatever runs it: a load, recovery, the
        // janitor. Adoption commits nothing, though a file it would adopt is waiting.
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        Assert.ThrowsAny<Exception>(() => service.LoadCatalog());
        Assert.Equal(LibraryIoFailure.Other, service.Recover().Failure);
        Assert.Equal(1, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(400)).Failed);
        Assert.ThrowsAny<Exception>(() => service.LoadCatalog());
        File.Delete(_fixture.PathOf("dropped.csv"));
        Assert.Equal(staged, JournalFiles());
        Assert.Equal(stored, _fixture.StoredGeneration);
        Assert.Empty(changed);
        AssertUnknownAndFenced(service, start, admitted);

        // The read recovers: the first load settles the manifest by the ordinary recovery rules.
        settings.Failing = false;
        var settled = service.LoadCatalog();

        Assert.DoesNotContain(JournalFiles().Keys, IsManifestOrRedo);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*"));
        if (committed)
        {
            // Installed and retired, and the new scope published: the grant is in force, the revocation and the deletion too.
            Assert.Equal(save.Generation, settled.Generation);
            Assert.Equal(save.Generation, _fixture.StoredGeneration);
            Assert.Equal("K8s", settled.Find("team")!.Content.Rows[0].Values.Written);
            Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
            Assert.Null(settled.Find("gone"));
            Assert.False(_fixture.Exists("gone.csv"));
            Assert.Single(settled.RecentlyDeleted);
            Assert.Equal(new[] { save.Generation }, changed);
            Assert.Equal(save.Generation, service.Current.AiScope.Generation);
            Assert.Equal(new[] { "newly", "other" }, service.Current.AiScope.PermittedLibraryIds.Order(StringComparer.Ordinal));
            Assert.True(service.TryHandOff(Holding(settled, "newly"), () => { }));
            Assert.False(service.TryHandOff(admitted, () => { }));
        }
        else
        {
            // Discarded, nothing of it installed, and the committed scope is back.
            Assert.Equal(start.Generation, settled.Generation);
            Assert.Equal(start.Generation, _fixture.StoredGeneration);
            Assert.Equal("Kubernetes", settled.Find("team")!.Content.Rows[0].Values.Written);
            Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
            Assert.NotNull(settled.Find("gone"));
            Assert.True(_fixture.Exists("gone.csv"));
            Assert.Empty(settled.RecentlyDeleted);
            Assert.Empty(changed);
            Assert.Equal(new[] { "gone", "other", "team" }, service.Current.AiScope.PermittedLibraryIds.Order(StringComparer.Ordinal));
            Assert.True(service.TryHandOff(admitted, () => { }));
            Assert.False(service.TryHandOff(Holding(start, "newly"), () => { }));
        }

        Assert.Equal(
            LibraryPrepareStatus.Prepared,
            service.PrepareSave(Changes.Of(settled, state: Changes.With(settled.LocalState, disable: ["other"]))).Status);
    }

    [Fact]
    public void Completing_the_same_Save_again_is_unknown_while_the_read_fails_and_reports_where_it_stands_once_it_reads()
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);

        var again = service.CompleteSave(prepared.Save!);

        Assert.Equal(LibrarySaveStatus.CommitUnknown, again.Status);
        Assert.Equal(start.Generation, again.CommittedGeneration);
        Assert.Equal(LibraryIoFailure.None, again.Failure);
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));

        settings.Failing = false;
        var settled = service.CompleteSave(prepared.Save!);

        Assert.Equal(LibrarySaveStatus.Applied, settled.Status);
        Assert.Equal(prepared.Save!.Generation, settled.CommittedGeneration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completing_a_Save_again_reports_what_recovery_settled_for_it_whatever_an_adoption_did_to_the_generation_since(bool committed)
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;
        settings.CommitSucceeds = committed;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        var save = prepared.Save!;

        // The read recovers, and a load other than the shell's settles the Save: recovery installs the manifest or discards
        // it, and a file that appeared meanwhile is adopted as a generation of its own. The generation stored is then the
        // Save's own for the one that never committed (the adoption reached the generation it would have committed), and
        // one past it for the one that did, so it says nothing of whether the Save committed.
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        settings.Failing = false;
        var settled = service.LoadCatalog();

        Assert.Equal(committed ? save.Generation + 1 : save.Generation, settled.Generation);
        Assert.Equal(settled.Generation, _fixture.StoredGeneration);
        Assert.NotNull(settled.Find("dropped"));

        var again = service.CompleteSave(save);

        Assert.Equal(committed ? LibrarySaveStatus.Applied : LibrarySaveStatus.NotCommitted, again.Status);
        Assert.Equal(settled.Generation, again.CommittedGeneration);
    }

    private string[] RedoImages() => RedoImagesOf(_fixture);

    private static string[] RedoImagesOf(LibraryStorageFixture fixture) =>
        [.. Directory.GetDirectories(fixture.Paths.LibraryJournalDir)
            .SelectMany(folder => Directory.GetFiles(folder, "*" + LibraryJournalNames.RedoSuffix))];

    private static void DamageRedoImages(LibraryStorageFixture fixture, string damage)
    {
        var images = RedoImagesOf(fixture);
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

    [Theory]
    [InlineData("missing redo image")]
    [InlineData("corrupt redo image")]
    [InlineData("rival manifest of the same generation")]
    [InlineData("manifest that cannot be parsed")]
    public void A_Save_whose_manifest_recovery_set_aside_is_never_reported_applied_whatever_the_stored_generation_reads(string damage)
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        var save = prepared.Save!;
        Assert.Equal(save.Generation, _fixture.StoredGeneration);
        var manifest = Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));

        // The Save committed, and recovery cannot install it: an image it needs is gone or fails its hash, another manifest
        // holds its generation, or the manifest itself cannot be parsed. Recovery sets the manifest aside with nothing of the
        // Save installed, yet the generation stored is still the Save's own, which is all the old answer looked at.
        switch (damage)
        {
            case "missing redo image":
                Assert.NotEmpty(RedoImages());
                Array.ForEach(RedoImages(), File.Delete);
                break;
            case "corrupt redo image":
                Assert.NotEmpty(RedoImages());
                Array.ForEach(RedoImages(), image => File.WriteAllBytes(image, [1, 2, 3, 4]));
                break;
            case "manifest that cannot be parsed":
                File.WriteAllText(manifest, "not a manifest");
                break;
            default:
                File.WriteAllText(
                    Path.Combine(_fixture.Paths.LibraryJournalDir, LibraryJournalNames.Manifest(save.Generation, new string('a', LibraryJournalNames.ManifestIdLength))),
                    "{}");
                break;
        }

        settings.Failing = false;
        var again = service.CompleteSave(save);

        Assert.Equal(LibrarySaveStatus.Superseded, again.Status);
        Assert.Equal(save.Generation, again.CommittedGeneration);
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.True(_fixture.Exists("gone.csv"));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.NotEmpty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*"));

        // The answer is the Save's own from then on, after a load that settles nothing and one that moves the generation on.
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        var moved = service.LoadCatalog();
        Assert.True(moved.Generation > save.Generation);
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
    }

    [Fact]
    public void A_Save_recovery_leaves_unresolved_because_a_file_waits_is_applied_awaiting_release_until_the_file_is_released()
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        var save = prepared.Save!;
        settings.Failing = false;

        LibrarySaveOutcome again;
        LibrarySaveOutcome third;
        LibraryPrepareStatus fenced;
        using (new FileStream(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Recovery finishes what it can and leaves the Save unresolved for the file another app holds: the Save is
            // installed or on its way, so it is applied, never set aside. Its manifest stays, and Saves stay fenced.
            again = service.CompleteSave(save);
            third = service.CompleteSave(save);
            fenced = service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["other"]))).Status;
        }

        Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, again.Status);
        Assert.True(again.FilesAwaitingRelease > 0);
        Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, third.Status);
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, fenced);
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));

        // Released, recovery finishes the install, and the same Save then reads as applied outright.
        var settled = service.LoadCatalog();

        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.Equal(save.Generation, settled.Generation);
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(save).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Save_a_later_read_shows_committed_is_applied_whole_and_stays_fenced_while_its_manifest_cannot_be_listed_or_read(bool unreadable)
    {
        var files = new FaultingFileSystem();
        var (service, settings, start, _) = Arrange(files);
        var changed = new List<long>();
        service.Changed += changed.Add;
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        var save = prepared.Save!;
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);

        // The read recovers, but the journal's manifests cannot be listed, or this one cannot be read (round 3, A13), so
        // recovery neither sees nor finishes it. Readers still apply it whole (never the new state beside the old files),
        // nothing commits, and it stays fenced.
        if (unreadable)
        {
            files.ReadFault = path =>
                path.StartsWith(Path.GetFullPath(_fixture.Paths.LibraryJournalDir), StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.OrdinalIgnoreCase)
                    ? FaultingFileSystem.SharingViolation()
                    : null;
        }
        else
        {
            files.EnumerateFault = (directory, pattern) =>
                IsJournal(directory) && pattern.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.OrdinalIgnoreCase)
                    ? FaultingFileSystem.SharingViolation()
                    : null;
        }

        settings.Failing = false;
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));

        var blind = service.LoadCatalog();

        Assert.Equal(save.Generation, blind.Generation);
        Assert.Equal("K8s", blind.Find("team")!.Content.Rows[0].Values.Written);
        Assert.Null(blind.Find("gone"));
        Assert.True(blind.FilesAwaitingRelease > 0);
        Assert.Contains(service.Current.Entries, entry => entry.Replacement == "K8s");
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement is "Kubernetes" or "G");
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.True(_fixture.Exists("gone.csv"));
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.Equal(save.Generation, _fixture.StoredGeneration);
        Assert.Equal(
            LibraryPrepareStatus.PreviousSaveUnfinished,
            service.PrepareSave(Changes.Of(blind, state: Changes.With(blind.LocalState, disable: ["other"]))).Status);
        Assert.Throws<InvalidOperationException>(() => service.Import("pattern,replacement\nn,N\n", "Notes"));
        Assert.False(service.TryHandOff(Holding(start, "newly"), () => Assert.Fail("A grant was handed over before its Save was settled.")));
        Assert.True(service.TryHandOff(Holding(start, "other"), () => { }));

        // Once the journal lists and reads, recovery finishes it, and the grant is in force.
        files.EnumerateFault = null;
        files.ReadFault = null;
        File.Delete(_fixture.PathOf("dropped.csv"));
        var settled = service.LoadCatalog();

        Assert.Equal(save.Generation, settled.Generation);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.False(_fixture.Exists("gone.csv"));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.Equal(new[] { save.Generation }, changed);
        Assert.True(service.TryHandOff(Holding(settled, "newly"), () => { }));
        Assert.Equal(
            LibraryPrepareStatus.Prepared,
            service.PrepareSave(Changes.Of(settled, state: Changes.With(settled.LocalState, disable: ["other"]))).Status);
    }

    [Fact]
    public void An_Import_whose_generation_cannot_be_read_after_its_commit_is_never_reported_failed_and_installs_once_it_reads()
    {
        // The wrappers settle by the stored generation as a Save does: an unknown outcome is not a change that failed.
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;

        var refused = Assert.Throws<InvalidOperationException>(() => service.Import("pattern,replacement\nn,N\n", "Notes"));

        Assert.Contains("couldn't confirm", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be saved", refused.Message, StringComparison.Ordinal);
        Assert.Equal(start.Generation + 1, _fixture.StoredGeneration);
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        var fenced = service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["other"])));
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, fenced.Status);
        Assert.Equal(LibraryIoFailure.None, fenced.Failure);

        settings.Failing = false;
        var settled = service.LoadCatalog();

        Assert.Equal(start.Generation + 1, settled.Generation);
        Assert.Contains(settled.Libraries, library => library.Content.Name == "Notes" && !library.Content.BuiltIn);
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
    }

    [Fact]
    public void An_unknown_outcome_settles_while_only_set_aside_manifests_cannot_be_listed()
    {
        // Round 4: set-aside manifests are never applied, so a failed listing of them says nothing about the unknown Save.
        // Recovery sees it, installs it and settles it, so its grant is in force; commits wait for that listing to work.
        var files = new FaultingFileSystem();
        var (service, settings, start, _) = Arrange(files);
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        files.EnumerateFault = (directory, pattern) =>
            IsJournal(directory) && pattern.Contains("set-aside", StringComparison.OrdinalIgnoreCase) ? FaultingFileSystem.SharingViolation() : null;
        settings.Failing = false;

        var settled = service.LoadCatalog();

        Assert.Equal(prepared.Save!.Generation, settled.Generation);
        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix));
        Assert.True(service.TryHandOff(Holding(settled, "newly"), () => { }), "the settled Save's grant is not in force");
        Assert.Equal(
            LibraryPrepareStatus.PreviousSaveUnfinished,
            service.PrepareSave(Changes.Of(settled, state: Changes.With(settled.LocalState, disable: ["other"]))).Status);

        files.EnumerateFault = null;
        var listed = service.LoadCatalog();
        Assert.Equal(
            LibraryPrepareStatus.Prepared,
            service.PrepareSave(Changes.Of(listed, state: Changes.With(listed.LocalState, disable: ["other"]))).Status);
    }

    // --- receipts: R16b, T17 and T13 ------------------------------------------------------------------------------------
    // A receipt follows the exact manifest of its Save. One recovery left unresolved (a file waits for release) is installed
    // on its way and stays revisable: a later attempt that sets it aside, or finds it gone, revises it to superseded, so the
    // shell never marks a draft saved over files that were never replaced; one recovery finished stays applied for good.

    private const string RealTeamTerms = "# name: Team terms\npattern,replacement\nkube,Kubernetes\n";

    // One operation, so a held target keeps everything of the Save still to do: nothing of it is installed, nothing half done.
    private static LibraryChangeSet EditTeam(LibraryCatalog start) =>
        Changes.Of(start, writes: [Changes.Edit(start, "team", Edited)]);

    private string[] Manifests() =>
        Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix);

    private FileStream HoldTeam() => new(_fixture.PathOf("team.csv"), FileMode.Open, FileAccess.Read, FileShare.Read);

    // The files are exactly what they were before the Save, and its manifest is quarantined unapplied.
    private void AssertNothingInstalled()
    {
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.True(_fixture.Exists("gone.csv"));
        Assert.Empty(Manifests());
        Assert.NotEmpty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*"));
    }

    [Theory]
    [InlineData("missing redo image", true)]
    [InlineData("missing redo image", false)]
    [InlineData("corrupt redo image", true)]
    [InlineData("corrupt redo image", false)]
    public void A_Save_recovery_left_unresolved_is_not_reported_installed_once_a_later_recovery_sets_its_manifest_aside(
        string damage, bool settlementRecovers)
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, EditTeam(start));
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        var save = prepared.Save!;
        settings.Failing = false;

        using (HoldTeam())
        {
            // A load other than the shell's settles the Save while another app holds the file: recovery finishes what it can
            // and leaves the manifest unresolved, a legitimate state that keeps the library fenced and reads as installed on
            // its way (never as applied outright, and never as a blanket unknown).
            Assert.Equal(0, service.Recover().SetAside);
            Assert.Single(Manifests());
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, service.CompleteSave(save).Status);
            Assert.Equal(
                LibraryPrepareStatus.PreviousSaveUnfinished,
                service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["other"]))).Status);

            // The redo image the install needs goes before the install can happen.
            DamageRedoImages(_fixture, damage);
        }

        // The file is released, and recovery cannot install the manifest: it sets it aside with nothing of the Save installed.
        // Either the settlement itself is the first to run recovery, so the generation stored is still the Save's own, which
        // is all a frozen receipt looked at; or a load that also adopts runs it first and moves the generation on.
        if (!settlementRecovers)
        {
            Assert.Equal(1, service.Recover().SetAside);
            Assert.True(_fixture.StoredGeneration >= save.Generation);
        }

        var again = service.CompleteSave(save);

        Assert.Equal(LibrarySaveStatus.Superseded, again.Status);
        Assert.Equal(0, again.FilesAwaitingRelease);
        if (settlementRecovers)
        {
            Assert.Equal(save.Generation, again.CommittedGeneration);
            Assert.Equal(save.Generation, _fixture.StoredGeneration);
        }

        AssertNothingInstalled();
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);

        // The answer is the Save's own from then on, whatever an adoption does to the generation, and the fence is lifted.
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        var moved = service.LoadCatalog();
        Assert.True(moved.Generation > save.Generation);
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
        Assert.Equal(
            LibraryPrepareStatus.Prepared,
            service.PrepareSave(Changes.Of(moved, state: Changes.With(moved.LocalState, disable: ["other"]))).Status);
    }

    [Theory]
    [InlineData("missing redo image", true)]
    [InlineData("missing redo image", false)]
    [InlineData("corrupt redo image", true)]
    [InlineData("corrupt redo image", false)]
    public void A_Save_completed_awaiting_release_is_superseded_once_a_later_recovery_sets_its_manifest_aside(
        string damage, bool settlementRecovers)
    {
        var (service, settings, start, _) = Arrange();
        LibraryPrepareResult prepared;
        using (HoldTeam())
        {
            LibrarySaveOutcome? outcome;
            (prepared, outcome, _) = Changes.Save(service, settings, EditTeam(start));

            // Committed, and the one file it writes is held: installed on its way, which keeps the library fenced.
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, outcome!.Status);
            Assert.Equal(LibraryIoFailure.SharingViolation, outcome.Failure);
            Assert.Single(Manifests());
            DamageRedoImages(_fixture, damage);
        }

        var save = prepared.Save!;
        if (!settlementRecovers)
        {
            Assert.Equal(1, service.Recover().SetAside);
            Assert.True(_fixture.StoredGeneration >= save.Generation);
        }

        var again = service.CompleteSave(save);

        Assert.Equal(LibrarySaveStatus.Superseded, again.Status);
        Assert.Equal(0, again.FilesAwaitingRelease);
        if (settlementRecovers)
        {
            Assert.Equal(save.Generation, again.CommittedGeneration);
            Assert.Equal(save.Generation, _fixture.StoredGeneration);
        }

        AssertNothingInstalled();
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Save_whose_manifest_recovery_finished_after_leaving_it_unresolved_stays_applied_when_an_adoption_moves_the_generation_on(bool unknown)
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = unknown;
        PreparedLibrarySave save;
        using (HoldTeam())
        {
            var saved = Changes.Save(service, settings, EditTeam(start));
            save = saved.Prepared.Save!;
            Assert.Equal(unknown ? LibrarySaveStatus.CommitUnknown : LibrarySaveStatus.AppliedAwaitingRelease, saved.Outcome!.Status);
            settings.Failing = false;
            Assert.Equal(0, service.Recover().Completed);
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, service.CompleteSave(save).Status);
        }

        // Released: recovery finishes the install and retires the manifest, and a file that appeared meanwhile is adopted as
        // a generation of its own. The Save was installed, so it is applied for good, whatever the generation says after.
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        var settled = service.LoadCatalog();

        Assert.Equal(LibraryStorageFixture.Managed(Edited), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.Empty(Manifests());
        Assert.True(settled.Generation > save.Generation);
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(save).Status);
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(save).Status);
    }

    [Theory]
    [InlineData("missing redo image")]
    [InlineData("corrupt redo image")]
    public void A_Save_whose_redo_image_is_already_gone_at_completion_is_not_reported_installed_and_the_draft_keeps_its_edit(string damage)
    {
        // The shell's sequence over the real parts: completion, then the catalog read, then MarkSaved only for a Save that
        // stands. Completion that could not finish the Save used to report it installed on its way while the very next read
        // set its manifest aside, so MarkSaved took the draft's edit for saved against files that never held it.
        using var libraries = new RealLibraries();
        var fixture = libraries.Fixture;
        fixture.Write("team-terms.csv", RealTeamTerms);
        fixture.SaveEnabled("team-terms");
        var service = libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        Assert.True(workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s")).Applied);

        var saved = libraries.Save(service, workspace, beforeCommit: () => DamageRedoImages(fixture, damage));

        Assert.Equal(LibrarySaveStatus.Superseded, saved.Outcome.Status);
        Assert.False(saved.MarkedSaved);
        Assert.True(workspace.HasUnsavedChanges);
        Assert.Equal("K8s", RealLibraries.Row(workspace, "team-terms", "kube").Row.Values.Written);
        Assert.Equal(RealTeamTerms, fixture.Read("team-terms.csv"));

        // The catalog the shell reads next agrees with the status: the files hold none of the edit, and the draft rebases onto
        // them with its edit still unsaved.
        var catalog = service.LoadCatalog();
        Assert.Equal("Kubernetes", catalog.Find("team-terms")!.Content.Rows.Single().Values.Written);
        Assert.Equal(0, catalog.FilesAwaitingRelease);
        workspace.Rebase(catalog);
        Assert.True(workspace.HasUnsavedChanges);
        Assert.Equal("K8s", RealLibraries.Row(workspace, "team-terms", "kube").Row.Values.Written);
        Assert.Equal(RealTeamTerms, fixture.Read("team-terms.csv"));
    }

    [Fact]
    public void A_Save_still_unsettled_is_not_reported_installed_on_its_way_while_the_catalog_cannot_apply_its_manifest()
    {
        var files = new FaultingFileSystem();
        var (service, settings, start, _) = Arrange(files);
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, EditTeam(start));
        var save = prepared.Save!;
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        DamageRedoImages(_fixture, "corrupt redo image");
        files.EnumerateFault = (directory, pattern) =>
            IsJournal(directory) && pattern.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.OrdinalIgnoreCase)
                ? FaultingFileSystem.SharingViolation()
                : null;
        settings.Failing = false;

        // The generation shows the Save committed, but the journal cannot be listed and the one redo image its manifest needs
        // fails its hash, so the catalog this read produces holds none of it: the answer is not that it stands.
        var blind = service.CompleteSave(save);

        Assert.Equal(LibrarySaveStatus.CommitUnknown, blind.Status);
        Assert.Equal(start.Generation, blind.CommittedGeneration);
        Assert.Equal(LibraryIoFailure.None, blind.Failure);

        // Once the journal lists, recovery sets the manifest aside and the Save is superseded, with the draft's edit to keep.
        files.EnumerateFault = null;
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
        AssertNothingInstalled();
    }

    [Fact]
    public void The_fate_recovery_gave_a_discarded_Save_is_kept_until_the_attempt_that_settles_it()
    {
        // T13: the Save never committed, so recovery discards its manifest; but the stored generation's own manifest cannot
        // be read yet, so that attempt cannot settle the Save, and the next attempt no longer sees the discarded manifest.
        var files = new FaultingFileSystem();
        var (service, settings, start, _) = Arrange(files);
        settings.FailAfterCommit = true;
        settings.CommitSucceeds = false;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        var save = prepared.Save!;
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        Assert.Equal(start.Generation, _fixture.StoredGeneration);
        settings.Failing = false;

        const string OtherId = "0123456789abcdef0123456789abcdef";
        var otherName = LibraryJournalNames.Manifest(start.Generation, OtherId);
        File.WriteAllBytes(
            Path.Combine(_fixture.Paths.LibraryJournalDir, otherName),
            new LibraryManifest(OtherId, start.Generation, start.Generation - 1, LibraryStorageFixture.Start, []).ToJson());
        files.ReadFault = path => path.EndsWith(otherName, StringComparison.OrdinalIgnoreCase) ? FaultingFileSystem.SharingViolation() : null;

        var first = service.Recover();

        Assert.Equal(1, first.Discarded);
        Assert.True(first.Held > 0);
        Assert.DoesNotContain(Manifests(), path => !path.EndsWith(otherName, StringComparison.OrdinalIgnoreCase));

        files.ReadFault = null;
        var second = service.Recover();

        Assert.Equal(1, second.Completed);
        Assert.Equal(0, second.Discarded);
        var again = service.CompleteSave(save);
        Assert.Equal(LibrarySaveStatus.NotCommitted, again.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.True(_fixture.Exists("gone.csv"));
        Assert.Empty(Manifests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_committed_Save_whose_exact_manifest_is_gone_before_recovery_is_superseded_never_applied(bool redoFolderToo)
    {
        var (service, settings, start, _) = Arrange();
        settings.FailAfterCommit = true;
        var (prepared, outcome, _) = Changes.Save(service, settings, GrantRevokeAndDelete(start));
        var save = prepared.Save!;
        Assert.Equal(LibrarySaveStatus.CommitUnknown, outcome!.Status);
        Assert.Equal(save.Generation, _fixture.StoredGeneration);

        // The manifest is lost before any attempt reads it: the generation is the Save's own, and nothing can say what the
        // manifest would have installed.
        File.Delete(Assert.Single(Manifests()));
        if (redoFolderToo)
        {
            foreach (var folder in Directory.GetDirectories(_fixture.Paths.LibraryJournalDir))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        settings.Failing = false;
        var again = service.CompleteSave(save);

        Assert.Equal(LibrarySaveStatus.Superseded, again.Status);
        Assert.Equal(Encoding.UTF8.GetBytes(TeamCsv), File.ReadAllBytes(_fixture.PathOf("team.csv")));
        Assert.True(_fixture.Exists("gone.csv"));
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
        _fixture.Write("dropped.csv", LibraryStorageFixture.Csv("Dropped", ("d", "D")));
        Assert.True(service.LoadCatalog().Generation > save.Generation);
        Assert.Equal(LibrarySaveStatus.Superseded, service.CompleteSave(save).Status);
    }

    [Fact]
    public async Task The_protocol_keeps_the_draft_when_recovery_sets_aside_a_manifest_an_earlier_recovery_left_unresolved()
    {
        // T17 end to end: the Save's outcome is unknown; a load outside the protocol settles it while another app holds the
        // file (unresolved); the redo image goes; a later load sets the manifest aside; only then does the protocol settle.
        using var libraries = new RealLibraries();
        var fixture = libraries.Fixture;
        fixture.Write("team-terms.csv", RealTeamTerms);
        fixture.SaveEnabled("team-terms");
        var settings = new UnreadableGeneration(fixture.Settings);
        var service = libraries.Service(settings: settings);
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        Assert.True(workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s")).Applied);
        var protocol = new WordPackSaveProtocol(new ServiceStore(service, settings));
        var request = new WordPackSaveProtocolRequest(
            workspace,
            payload => settings.SaveBundle(settings.Load(), null, null, default, payload),
            () => new StoredSettingsReapplyResult(StoredSettingsReapplied.StoredSettings,
                Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty))),
            () => "draft",
            () => "draft");
        settings.FailAfterCommit = true;
        var unknown = await protocol.SaveAsync(request);
        Assert.False(unknown.Success);
        Assert.True(protocol.HasPendingSave);
        settings.Failing = false;

        using (new FileStream(fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(0, service.Recover().SetAside);
            DamageRedoImages(fixture, "missing redo image");
        }

        Assert.Equal(1, service.Recover().SetAside);

        var settled = await protocol.TrySettlePendingAsync(request);

        // Not saved: the edit stays the draft's, the files were never replaced, and saving again installs the edit.
        Assert.False(settled.Success);
        Assert.False(protocol.HasPendingSave);
        Assert.True(workspace.HasUnsavedChanges);
        Assert.Equal("K8s", RealLibraries.Row(workspace, "team-terms", "kube").Row.Values.Written);
        Assert.Equal(RealTeamTerms, fixture.Read("team-terms.csv"));

        var again = await protocol.SaveAsync(request);

        Assert.True(again.Success, again.Message);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal("K8s", service.LoadCatalog().Find("team-terms")!.Content.Rows.Single().Values.Written);
        Assert.NotEqual(RealTeamTerms, fixture.Read("team-terms.csv"));
    }

    private sealed class ServiceStore(DictionaryLibraryService service, ISettingsRepository settings) : IWordPackSaveProtocolStore
    {
        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) => Task.FromResult(WordPackSaveSession.Prepare(service, changes));

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared) => Task.FromResult(WordPackSaveSession.Complete(service, prepared));

        public Task<LibraryCatalog> LoadCatalogAsync() => Task.FromResult(WordPackSaveSession.LoadCatalog(service));

        public void CommitRepair(LibrarySavePayload payload) => settings.CommitLibraryState(payload);
    }

    /// <summary>
    /// The real repository, except that every read of the library generation fails while <see cref="Failing"/> is set,
    /// and that with <see cref="FailAfterCommit"/> the next library commit (a SaveBundle with a library payload, or
    /// CommitLibraryState) commits, or without <see cref="CommitSucceeds"/> throws before its commit, and leaves the reads
    /// failing after it.
    /// </summary>
    private sealed class UnreadableGeneration(SettingsRepository inner) : ISettingsRepository
    {
        public bool Failing { get; set; }

        public bool FailAfterCommit { get; set; }

        public bool CommitSucceeds { get; set; } = true;

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
            if (libraries is null)
            {
                inner.SaveBundle(settings, dictionaryEntries, snippets, intents, libraries);
                return;
            }

            Commit(() => inner.SaveBundle(settings, dictionaryEntries, snippets, intents, libraries));
        }

        public void CommitLibraryState(LibrarySavePayload payload) => Commit(() => inner.CommitLibraryState(payload));

        public string? Get(string key) =>
            Failing && key == LibrarySettingKeys.Generation ? throw new IOException("injected: the settings store cannot be read") : inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);

        private void Commit(Action commit)
        {
            var failAfter = FailAfterCommit;
            FailAfterCommit = false;
            try
            {
                if (failAfter && !CommitSucceeds)
                {
                    throw new IOException("injected: the settings transaction did not commit");
                }

                commit();
            }
            finally
            {
                if (failAfter)
                {
                    Failing = true;
                }
            }
        }
    }
}
