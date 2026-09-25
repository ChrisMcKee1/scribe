using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

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
