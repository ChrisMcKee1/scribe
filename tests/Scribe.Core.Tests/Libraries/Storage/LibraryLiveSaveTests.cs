using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// A Save between its prepare and its completion (the fence, review finding A1), a completion another app's lock defers
/// (G10), a Save that does not commit, and the one read path while a committed manifest is pending (G2): J-1b, J-1c, J-2,
/// J-5, J-6 and J-6b.
/// </summary>
public sealed class LibraryLiveSaveTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_load_on_another_thread_neither_discards_nor_replays_the_live_manifest_and_completion_then_applies_it()
    {
        // J-1b: the settings commit is held inside its transaction while another thread loads the catalog.
        using var fixture = new LibraryStorageFixture(fileDatabase: true);
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        fixture.SaveEnabled("team-terms");
        var service = fixture.Service();
        var start = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"));
        var prepared = service.PrepareSave(Changes.Of(
            start, writes: [Changes.Edit(start, "team-terms", edited), Changes.Create(LibraryStorageFixture.Content("custom-notes", "Notes", ("a", "A")))]));
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        var manifests = Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.manifest.json");
        Assert.Single(manifests);
        var before = File.ReadAllBytes(fixture.PathOf("team-terms.csv"));

        using var inCommit = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Settings.WriteStep = (step, _, _) =>
        {
            if (step == "save committing")
            {
                inCommit.Set();
                Assert.True(release.Wait(Bound));
            }
        };
        var commit = Task.Run(() => fixture.Settings.SaveBundle(fixture.Settings.Load(), null, null, default, prepared.Save!.Payload));
        Assert.True(inCommit.Wait(Bound));

        var during = await Task.Run(service.LoadCatalog).WaitAsync(Bound);

        Assert.Equal(start.Generation, during.Generation);
        Assert.Equal(manifests, Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.manifest.json"));
        Assert.Equal(before, File.ReadAllBytes(fixture.PathOf("team-terms.csv")));
        Assert.DoesNotContain(fixture.AllFiles(), file => file.EndsWith(".scribe-staged", StringComparison.Ordinal));
        Assert.Equal("Kubernetes", during.Find("team-terms")!.Content.Rows[0].Values.Written);

        release.Set();
        await commit.WaitAsync(Bound);
        fixture.Settings.WriteStep = null;

        // Committed, not completed: every reader sees the new generation through the manifest, and nothing is installed.
        var afterCommit = await Task.Run(service.LoadCatalog).WaitAsync(Bound);
        Assert.Equal(start.Generation + 1, afterCommit.Generation);
        Assert.Equal("K8s", afterCommit.Find("team-terms")!.Content.Rows[0].Values.Written);
        Assert.Equal(LibraryFileState.AwaitingRelease, afterCommit.Find("team-terms")!.State);
        Assert.NotNull(afterCommit.Find("custom-notes"));
        Assert.Equal(before, File.ReadAllBytes(fixture.PathOf("team-terms.csv")));
        Assert.Equal(manifests, Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.manifest.json"));

        var outcome = service.CompleteSave(prepared.Save!);

        Assert.Equal(LibrarySaveStatus.Applied, outcome.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(fixture.PathOf("team-terms.csv")));
        Assert.True(fixture.Exists("custom-notes.csv"));
        Assert.Empty(Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.manifest.json"));
    }

    [Fact]
    public void An_unresolved_generation_holds_adoption_saves_and_the_wrappers_until_recovery_finishes_it()
    {
        // J-1c.
        using var fixture = new LibraryStorageFixture();
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        fixture.SaveEnabled("team-terms");
        var service = fixture.Service();
        var start = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"));
        var prepared = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]));
        fixture.Settings.SaveBundle(fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        using (new FileStream(fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = service.CompleteSave(prepared.Save!);
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, outcome.Status);
            Assert.Equal(LibraryIoFailure.SharingViolation, outcome.Failure);

            fixture.Write("discovered.csv", LibraryStorageFixture.Csv("Discovered", ("found", "Found")));
            var waiting = service.LoadCatalog();

            // Adoption uses its plan in memory and commits nothing while the generation is unresolved.
            Assert.Equal(2, fixture.StoredGeneration);
            Assert.False(waiting.LocalState.AiPermissions["discovered"]);
            Assert.True(waiting.FilesAwaitingRelease > 0);
            Assert.Equal(LibraryIoFailure.SharingViolation, waiting.PendingFailure);

            var again = service.PrepareSave(Changes.Of(waiting, writes: [Changes.Create(LibraryStorageFixture.Content("custom-x", "X", ("x", "X")))]));
            Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, again.Status);
            Assert.Equal(LibraryIoFailure.SharingViolation, again.Failure);
            Assert.Equal("A library change is still being saved.",
                Assert.Throws<InvalidOperationException>(() => service.Import("a,A\n", "import")).Message);
            Assert.Equal("A library change is still being saved.",
                Assert.Throws<InvalidOperationException>(() => service.Remove("discovered")).Message);
        }

        var released = service.LoadCatalog();

        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(fixture.PathOf("team-terms.csv")));
        Assert.Equal(3, fixture.StoredGeneration);
        Assert.Equal(3, released.Generation);
        Assert.False(released.LocalState.AiPermissions["discovered"]);
        Assert.Empty(Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.set-aside.*"));
        Assert.Empty(Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.manifest.json"));
    }

    [Fact]
    public void A_save_that_does_not_commit_adopts_nothing_and_puts_the_committed_scope_back()
    {
        // J-2: prepare succeeds, SaveBundle throws, completion is NotCommitted, and the user's Cancel reloads the old state.
        using var fixture = new LibraryStorageFixture();
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        fixture.SaveEnabled("team-terms");
        var service = fixture.Service();
        var start = service.LoadCatalog();
        var old = LibraryFaultInjectionTests.Fingerprint(start);
        var admitted = service.Current.AiScope;
        Assert.Contains("team-terms", admitted.PermittedLibraryIds);
        fixture.Settings.WriteStep = (step, _, _) =>
        {
            if (step == "save committing")
            {
                throw FaultingFileSystem.Injected();
            }
        };

        var prepared = service.PrepareSave(Changes.Of(start, state: Changes.With(start.LocalState, disable: ["team-terms"])));
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        Assert.False(service.TryHandOff(admitted, () => Assert.Fail("A revoked scope was handed over.")));
        Assert.ThrowsAny<Exception>(() => fixture.Settings.SaveBundle(fixture.Settings.Load(), null, null, default, prepared.Save!.Payload));
        var outcome = service.CompleteSave(prepared.Save!);

        Assert.Equal(LibrarySaveStatus.NotCommitted, outcome.Status);
        var handedOver = false;
        Assert.True(service.TryHandOff(admitted, () => handedOver = true));
        Assert.True(handedOver);
        fixture.Settings.WriteStep = null;
        Assert.Equal(start.Generation, fixture.StoredGeneration);
        Assert.Equal(old, LibraryFaultInjectionTests.Fingerprint(service.LoadCatalog()));
        Assert.Empty(Directory.GetFiles(fixture.Paths.LibraryJournalDir, "*.json"));
    }

    [Fact]
    public void A_commit_that_reports_failure_after_committing_completes_as_applied()
    {
        // J-5.
        using var fixture = new LibraryStorageFixture();
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        var service = fixture.Service();
        var start = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"));
        fixture.Settings.WriteStep = (step, _, _) =>
        {
            if (step == "save committed")
            {
                throw FaultingFileSystem.Injected();
            }
        };

        var saved = Changes.Save(service, fixture.Settings, Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]));

        Assert.NotNull(saved.CommitFailure);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(fixture.PathOf("team-terms.csv")));
    }

    [Fact]
    public void A_target_another_app_holds_open_defers_the_install_and_readers_use_the_committed_content()
    {
        // J-6, the held-open target.
        using var fixture = new LibraryStorageFixture();
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        fixture.SaveEnabled("team-terms");
        var service = fixture.Service();
        var start = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"));
        var prepared = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]));
        fixture.Settings.SaveBundle(fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        using (new FileStream(fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = service.CompleteSave(prepared.Save!);
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, outcome.Status);
            Assert.Equal(1, outcome.FilesAwaitingRelease);

            var catalog = service.LoadCatalog();
            Assert.Equal(LibraryFileState.AwaitingRelease, catalog.Find("team-terms")!.State);
            Assert.Equal("K8s", catalog.Find("team-terms")!.Content.Rows[0].Values.Written);
            Assert.Contains(service.GetEnabledLibraryEntries(["team-terms"]), entry => entry.Replacement == "K8s");
            Assert.Contains(service.Current.Entries, entry => entry.Replacement == "K8s");
        }

        var recovered = service.Recover();

        Assert.Equal(1, recovered.Completed);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(fixture.PathOf("team-terms.csv")));
        Assert.Equal(LibraryFileState.Available, service.LoadCatalog().Find("team-terms")!.State);
    }

    [Fact]
    public void A_file_another_app_locks_for_reading_awaits_release_with_the_content_last_read_and_none_at_a_fresh_start()
    {
        // J-6, the held-open read: a custom file and an edits document.
        using var fixture = new LibraryStorageFixture();
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document("github", ("get hub", "GitHub Enterprise")));
        fixture.SaveEnabled("team-terms", "github");
        var earlier = fixture.Service();
        earlier.LoadCatalog();

        using (new FileStream(fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(fixture.PathOf("edits/github.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var fresh = fixture.Service().LoadCatalog();
            Assert.Equal(LibraryFileState.AwaitingRelease, fresh.Find("team-terms")!.State);
            Assert.Empty(fresh.Find("team-terms")!.Content.Rows);
            Assert.Equal(LibraryFileState.AwaitingRelease, fresh.Find("github")!.State);
            Assert.Empty(fresh.Find("github")!.Content.Rows);

            var known = earlier.LoadCatalog();
            Assert.Equal(LibraryFileState.AwaitingRelease, known.Find("team-terms")!.State);
            Assert.Equal("Kubernetes", known.Find("team-terms")!.Content.Rows[0].Values.Written);
            Assert.Equal(LibraryFileState.AwaitingRelease, known.Find("github")!.State);
            Assert.Contains(known.Find("github")!.Content.Rows, row => row.Values.Written == "GitHub Enterprise");
            Assert.DoesNotContain(known.Libraries, library => library.State == LibraryFileState.Unreadable);
        }
    }

    [Fact]
    public void A_pending_manifest_is_read_whole_so_no_reader_meets_a_deleted_librarys_rules()
    {
        // J-6b, this process's live preparation after its commit, before any install.
        using var fixture = new LibraryStorageFixture();
        Seed(fixture);
        var service = fixture.Service();
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(PendingChanges(start));
        fixture.Settings.SaveBundle(fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        AssertReadWhole(fixture, service);
        Assert.True(fixture.Exists("old-library.csv"));
        Assert.Equal(LibrarySaveStatus.Applied, service.CompleteSave(prepared.Save!).Status);
    }

    [Fact]
    public void A_manifest_recovery_could_not_finish_is_read_whole_too()
    {
        // J-6b, a manifest an earlier process left behind, which a lock keeps from finishing.
        using var fixture = new LibraryStorageFixture();
        Seed(fixture);
        var start = fixture.Service().LoadCatalog();
        var prepared = fixture.Service().PrepareSave(PendingChanges(start));
        fixture.Settings.SaveBundle(fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        fixture.Restart();
        using (new FileStream(fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertReadWhole(fixture, fixture.Service());
        }
    }

    private static void Seed(LibraryStorageFixture fixture)
    {
        fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        fixture.Write("old-library.csv", LibraryStorageFixture.Csv("Old", ("old", "Old rule")));
        fixture.Write("deleted/20260901T101010Z.scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
        fixture.SaveEnabled("team-terms", "old-library");
    }

    private static LibraryChangeSet PendingChanges(LibraryCatalog start)
    {
        var scratch = start.RecentlyDeleted.Single();
        return Changes.Of(
            start,
            writes: [Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")))],
            deletions: [Changes.Delete(start, "old-library")],
            recentlyDeleted: [new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, scratch.EntryName, scratch.ContentHash, "scratch")],
            state: Changes.With(start.LocalState, enable: ["scratch"]));
    }

    private static void AssertReadWhole(LibraryStorageFixture fixture, Scribe.Core.PostProcessing.DictionaryLibraryService service)
    {
        var catalog = service.LoadCatalog();

        Assert.Equal("K8s", catalog.Find("team-terms")!.Content.Rows[0].Values.Written);
        Assert.Null(catalog.Find("old-library"));
        Assert.NotNull(catalog.Find("scratch"));
        Assert.DoesNotContain(catalog.RecentlyDeleted, entry => entry.OriginalId == "scratch");
        Assert.Contains(catalog.RecentlyDeleted, entry => entry.OriginalId == "old-library");
        Assert.DoesNotContain(service.GetEnabledLibraryEntries(["team-terms", "old-library", "scratch"]), entry => entry.Replacement == "Old rule");
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement == "Old rule");
        Assert.Equal(fixture.StoredGeneration, catalog.Generation);
    }
}
