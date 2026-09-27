using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Quarantine, held manifests and orphans (contract 6.6.6 and 6.6.7): a lost generation row with a manifest pending
/// (J-4, G5 and G11), the whole-files step and the check a set-aside or a discard waits for (J-4b, G11 and G14), a session
/// on defaults (J-19), and journal names parsed whole, so one operation never takes another's backups (J-21, A19).
/// </summary>
public sealed class LibraryQuarantineTests : IDisposable
{
    private static readonly string Stamp = LibraryJournalNames.FormatStamp(LibraryStorageFixture.Start);
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_lost_generation_sets_the_manifest_aside_and_its_expiry_removes_only_its_own_journal_files()
    {
        // J-4: a manifest fully installed but not yet retired when the generation row was lost.
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        _fixture.Write("old-library.csv", LibraryStorageFixture.Csv("Old", ("old", "Old")));
        _fixture.SaveEnabled("team-terms", "old-library");
        var files = new FaultingFileSystem();
        var service = _fixture.Service(files);
        var start = service.LoadCatalog();
        var created = LibraryStorageFixture.Content("custom-release-notes", "Release notes", ("sprint", "Sprint"));
        var outside = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube from another app")));
        var prepared = service.PrepareSave(Changes.Of(
            start,
            writes: [Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"))), Changes.Create(created)],
            deletions: [Changes.Delete(start, "old-library")]));
        var manifest = ManifestName();
        _fixture.WriteBytes("team-terms.csv", outside);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        files.BeforeMutation = (kind, path, _) =>
        {
            if (kind == "delete" && path.EndsWith(LibraryJournalNames.ManifestSuffix, StringComparison.Ordinal))
            {
                files.CrashNow();
            }
        };
        service.CompleteSave(prepared.Save);
        const string KeptAs = "custom-team-terms-changed-outside-scribe.csv";
        Assert.True(_fixture.Exists(KeptAs));
        Assert.True(_fixture.Exists("custom-release-notes.csv"));
        var entry = Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryDeletedDir));
        Assert.True(_fixture.Exists("journal/" + manifest));

        LoseGeneration();
        _fixture.Restart();
        var recovering = _fixture.Service();
        var recovered = recovering.Recover();

        Assert.Equal(1, recovered.SetAside);
        var setAside = "journal/" + manifest.Replace(LibraryJournalNames.ManifestSuffix, ".set-aside." + Stamp + ".json", StringComparison.Ordinal);
        Assert.True(_fixture.Exists(setAside));
        var redoFolder = "journal/" + manifest[..^LibraryJournalNames.ManifestSuffix.Length];
        Assert.True(Directory.Exists(_fixture.PathOf(redoFolder)));
        Assert.NotEmpty(Directory.GetFiles(_fixture.PathOf(redoFolder)));

        // The next Save works beside the quarantine, under names that never collide with its files.
        var afterLoss = recovering.LoadCatalog();
        var next = Changes.Save(recovering, _fixture.Settings, Changes.Of(afterLoss, writes: [Changes.Create(LibraryStorageFixture.Content("custom-next", "Next", ("n", "N")))]));
        Assert.Equal(LibrarySaveStatus.Applied, next.Outcome!.Status);
        Assert.True(_fixture.Exists(setAside));

        Assert.Equal(0, recovering.Janitor.Run(LibraryStorageFixture.Start.AddDays(13)).QuarantinedRemoved);
        Assert.True(_fixture.Exists(setAside));
        Assert.Equal(1, recovering.Janitor.Run(LibraryStorageFixture.Start.AddDays(15)).QuarantinedRemoved);
        Assert.False(_fixture.Exists(setAside));
        Assert.False(Directory.Exists(_fixture.PathOf(redoFolder)));

        // No library file the manifest named went with it.
        Assert.True(_fixture.Exists("custom-release-notes.csv"));
        Assert.True(_fixture.Exists(KeptAs));
        Assert.True(File.Exists(entry));
        Assert.True(_fixture.Exists("custom-next.csv"));

        // The Recently deleted entry keeps its own retention, by its own stamp.
        Assert.Equal(1, recovering.Janitor.Run(LibraryStorageFixture.Start.AddDays(31)).RecentlyDeletedRemoved);
        Assert.False(File.Exists(entry));
    }

    [Fact]
    public void Setting_aside_after_a_crash_inside_the_checked_replace_moves_the_target_back_first()
    {
        // J-4b: the target renamed to its backup, the install copy not yet moved in, then the generation row lost.
        var (preImage, _) = CrashAfterCheckedRename();

        LoseGeneration();
        _fixture.Restart();
        var recovered = _fixture.Service().Recover();

        Assert.Equal(1, recovered.SetAside);
        Assert.Equal(0, recovered.Held);
        Assert.Equal(preImage, File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.LibrariesDir, "*.scribe-backup"));
    }

    [Fact]
    public void A_backup_holding_an_outside_version_is_kept_before_the_set_aside_so_the_expiry_only_deletes_Scribes_copies()
    {
        // J-4b: the native replace moved another app's version into the backup, then the process ended and the row was lost.
        var outside = CrashAfterReplaceWithOutsideVersion();

        LoseGeneration();
        _fixture.Restart();
        var service = _fixture.Service();
        var recovered = service.Recover();

        Assert.Equal(1, recovered.SetAside);
        Assert.Contains(recovered.KeptVersions, kept => kept.Kind == LibraryKeptVersionKind.OutsideVersion);
        Assert.Equal(outside, File.ReadAllBytes(_fixture.PathOf("custom-team-terms-changed-outside-scribe.csv")));
        service.Janitor.Run(LibraryStorageFixture.Start.AddDays(15));
        Assert.Equal(outside, File.ReadAllBytes(_fixture.PathOf("custom-team-terms-changed-outside-scribe.csv")));
    }

    [Theory]
    [InlineData("sharing")]
    [InlineData("access")]
    public void A_manifest_whose_target_cannot_be_moved_back_is_held_pending_until_it_can_and_its_expiry_starts_only_then(string failure)
    {
        // J-4b, the refusal (G14): nothing is set aside while the files cannot be made whole.
        var (preImage, manifest) = CrashAfterCheckedRename();
        LoseGeneration();
        var refused = true;
        var files = new FaultingFileSystem
        {
            MutationFault = (kind, source, destination) =>
                refused && kind == "move" && source.EndsWith(".scribe-backup", StringComparison.Ordinal) && destination == _fixture.PathOf("team-terms.csv")
                    ? failure == "sharing" ? FaultingFileSystem.SharingViolation() : FaultingFileSystem.AccessDenied()
                    : null,
        };
        _fixture.Restart();
        var service = _fixture.Service(files);

        var held = service.Recover();

        Assert.Equal(1, held.Held);
        Assert.Equal(0, held.SetAside);
        Assert.Equal(failure == "sharing" ? LibraryIoFailure.SharingViolation : LibraryIoFailure.AccessDenied, held.Failure);
        AssertHeld(service, manifest, held.Failure);
        Assert.Null(service.LoadCatalog().Find("team-terms"));

        // The janitor at 15 and 30 days deletes nothing of a held manifest: it has no stamp to expire by.
        var before = _fixture.AllFiles();
        service.Janitor.Run(LibraryStorageFixture.Start.AddDays(15));
        service.Janitor.Run(LibraryStorageFixture.Start.AddDays(30));
        Assert.Equal(before, _fixture.AllFiles());

        // Once the fault clears, the next attempt makes the files whole and sets the manifest aside, stamped then.
        _fixture.Time.Advance(TimeSpan.FromDays(30));
        refused = false;
        var cleared = service.Recover();
        Assert.Equal(1, cleared.SetAside);
        Assert.Equal(preImage, File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        var setAside = Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*.json"));
        Assert.Contains(LibraryJournalNames.FormatStamp(LibraryStorageFixture.Start.AddDays(30)), setAside, StringComparison.Ordinal);
        Assert.Equal(0, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(30 + 13)).QuarantinedRemoved);
        Assert.Equal(1, service.Janitor.Run(LibraryStorageFixture.Start.AddDays(30 + 15)).QuarantinedRemoved);
    }

    [Theory]
    [InlineData("disk full")]
    [InlineData("verification")]
    public void A_manifest_whose_outside_backup_cannot_be_preserved_is_held(string failure)
    {
        // J-4b: the preservation refused (a full disk) or its result not holding the bytes (a move that did nothing).
        var outside = CrashAfterReplaceWithOutsideVersion();
        var manifest = ManifestName();
        LoseGeneration();
        var keepAs = _fixture.PathOf("custom-team-terms-changed-outside-scribe.csv");
        var refused = true;
        var files = new FaultingFileSystem
        {
            MutationFault = (kind, _, destination) =>
                refused && failure == "disk full" && kind == "move" && destination == keepAs ? FaultingFileSystem.DiskFull() : null,
            MoveOverride = (_, destination) => refused && failure == "verification" && destination == keepAs,
        };
        _fixture.Restart();
        var service = _fixture.Service(files);

        var held = service.Recover();

        Assert.Equal(1, held.Held);
        Assert.Equal(0, held.SetAside);
        AssertHeld(service, manifest, held.Failure);
        Assert.Contains(_fixture.AllFiles(), file => File.ReadAllBytes(_fixture.PathOf(file)).SequenceEqual(outside));

        refused = false;
        Assert.Equal(1, service.Recover().SetAside);
        Assert.Equal(outside, File.ReadAllBytes(keepAs));
    }

    [Fact]
    public void A_discard_waits_for_the_files_to_be_whole_as_a_set_aside_does()
    {
        // J-4b: a manifest above the stored generation (an older database restored) with a target left in its backup.
        var (preImage, manifest) = CrashAfterCheckedRename();
        SetGeneration(1);
        var refused = true;
        var files = new FaultingFileSystem
        {
            MutationFault = (kind, source, _) =>
                refused && kind == "move" && source.EndsWith(".scribe-backup", StringComparison.Ordinal) ? FaultingFileSystem.SharingViolation() : null,
        };
        _fixture.Restart();
        var service = _fixture.Service(files);

        var held = service.Recover();

        Assert.Equal(1, held.Held);
        Assert.Equal(0, held.Discarded);
        Assert.True(_fixture.Exists("journal/" + manifest));

        refused = false;
        var discarded = service.Recover();
        Assert.Equal(1, discarded.Discarded);
        Assert.Equal(preImage, File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.json"));
    }

    [Fact]
    public void A_target_deleted_outside_Scribe_with_no_backup_does_not_hold_the_manifest()
    {
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")))]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        File.Delete(_fixture.PathOf("team-terms.csv"));
        LoseGeneration();
        _fixture.Restart();

        var recovered = _fixture.Service().Recover();

        Assert.Equal(1, recovered.SetAside);
        Assert.Equal(0, recovered.Held);
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_moves_its_backups_into_orphans_before_the_set_aside_and_a_refused_move_holds_it()
    {
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        const string Id = "0123456789abcdef0123456789abcdef";
        _fixture.Write($"journal/g5-{Id}.manifest.json", "{ not a manifest");
        _fixture.Write($"~g5-{Id}-0.scribe-backup", "the only copy of something");
        var refused = true;
        var files = new FaultingFileSystem
        {
            MutationFault = (kind, _, destination) =>
                refused && kind == "move" && destination is not null && destination.Contains(LibraryJournal.OrphansFolderName, StringComparison.Ordinal)
                    ? FaultingFileSystem.SharingViolation()
                    : null,
        };
        var service = _fixture.Service(files);

        var held = service.Recover();
        Assert.Equal(1, held.Held);
        Assert.True(_fixture.Exists($"journal/g5-{Id}.manifest.json"));
        Assert.True(_fixture.Exists($"~g5-{Id}-0.scribe-backup"));

        refused = false;
        var setAside = service.Recover();
        Assert.Equal(1, setAside.SetAside);
        Assert.Equal("the only copy of something", _fixture.Read($"journal/orphans/~g5-{Id}-0.scribe-backup"));
        Assert.False(_fixture.Exists($"~g5-{Id}-0.scribe-backup"));
        Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, $"g5-{Id}.set-aside.*.json"));
    }

    [Fact]
    public void A_session_on_defaults_writes_nothing_on_its_own_but_finishes_and_discards_by_the_generation_and_commits_the_users_save()
    {
        // J-19 (section 0 rule 8).
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team-terms");
        var service = _fixture.Service();
        var start = service.LoadCatalog();

        // A committed manifest of the stored generation left pending, and one above it that never committed.
        var committed = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")))]));
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, committed.Save!.Payload);
        _fixture.Restart();
        var afterCommit = _fixture.Service(new FaultingFileSystem { FailAt = 1 });
        afterCommit.CompleteSave(committed.Save);
        var aboveId = "fedcba9876543210fedcba9876543210";
        _fixture.Write($"journal/g9-{aboveId}.manifest.json", Encoding.UTF8.GetString(new LibraryManifest(aboveId, 9, 8, LibraryStorageFixture.Start, []).ToJson()));
        _fixture.Write("discovered.csv", LibraryStorageFixture.Csv("Discovered", ("found", "Found")));
        _fixture.Write("~g4-0123456789abcdef0123456789abcdef-0.scribe-staged", "an orphan install copy");
        _fixture.Write("deleted/20260101T000000Z.ancient.csv", LibraryStorageFixture.Csv("Ancient", ("a", "A")));
        var generation = _fixture.StoredGeneration;

        _fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: false);
        _fixture.Restart();
        var onDefaults = _fixture.Service();
        var catalog = onDefaults.LoadCatalog();
        onDefaults.Janitor.Run(LibraryStorageFixture.Start.AddDays(400));

        Assert.Equal(generation, _fixture.StoredGeneration);
        Assert.Equal(LibraryStorageFixture.Managed(LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"))), File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        Assert.False(_fixture.Exists($"journal/g9-{aboveId}.manifest.json"));
        Assert.True(_fixture.Exists("~g4-0123456789abcdef0123456789abcdef-0.scribe-staged"));
        Assert.True(_fixture.Exists("deleted/20260101T000000Z.ancient.csv"));
        Assert.False(catalog.LocalState.AiPermissions.ContainsKey("discovered"));

        var saved = Changes.Save(onDefaults, _fixture.Settings, Changes.Of(catalog, writes: [Changes.Create(LibraryStorageFixture.Content("custom-mine", "Mine", ("m", "M")))]));
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(generation + 1, _fixture.StoredGeneration);
    }

    [Fact]
    public void Eleven_operations_resolve_only_their_own_backups_in_recovery_and_when_made_whole()
    {
        // J-21: operation 10's step 2 done (its target renamed to ~g<G>-<id>-10.scribe-backup), operation 1 holding a
        // backup of its own, and names that do not parse beside them.
        var (preImages, manifest, generation, id) = ElevenOperationCrash();
        var ownOne = Encoding.UTF8.GetBytes("operation 1's own backup");
        _fixture.WriteBytes($"~g{generation}-{id}-1.scribe-backup", ownOne);
        _fixture.Write($"~g{generation}-{id}-01.scribe-backup", "a leading zero");
        _fixture.Write($"~g{generation}-{id[..31]}-1.scribe-backup", "a 31-digit id");
        _fixture.Restart();

        // Ordinary recovery: operation 10 installs from its own series and operation 1 keeps nothing of operation 10's.
        var recovered = _fixture.Service().Recover();

        Assert.Equal(1, recovered.Completed);
        Assert.DoesNotContain(recovered.KeptVersions, kept => kept.LibraryId == "lib-01" && kept.Kind == LibraryKeptVersionKind.OutsideVersion &&
            File.ReadAllBytes(_fixture.PathOf(kept.KeptAsId + ".csv")).SequenceEqual(preImages[10]));
        Assert.Equal(LibraryStorageFixture.Managed(Content(10)), File.ReadAllBytes(_fixture.PathOf("lib-10.csv")));
        Assert.True(_fixture.Exists($"~g{generation}-{id}-01.scribe-backup"));
        Assert.True(_fixture.Exists($"~g{generation}-{id[..31]}-1.scribe-backup"));
        _ = manifest;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eleven_operations_made_whole_give_operation_10_its_own_backup_back(bool retrySuffixes)
    {
        // J-21 with the generation row lost; with retry suffixes the largest suffix is the one restored.
        var (preImages, _, generation, id) = ElevenOperationCrash();
        var ownOne = Encoding.UTF8.GetBytes("operation 1's own backup");
        _fixture.WriteBytes($"~g{generation}-{id}-1.scribe-backup", ownOne);
        byte[] expected = preImages[10];
        if (retrySuffixes)
        {
            _fixture.WriteBytes($"~g{generation}-{id}-1-2.scribe-backup", Encoding.UTF8.GetBytes("operation 1's second backup"));
            expected = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("lib 10", ("t10", "moved aside last")));
            _fixture.WriteBytes($"~g{generation}-{id}-10-2.scribe-backup", expected);
        }

        LoseGeneration();
        _fixture.Restart();
        var recovered = _fixture.Service().Recover();

        Assert.Equal(1, recovered.SetAside);
        Assert.Equal(expected, File.ReadAllBytes(_fixture.PathOf("lib-10.csv")));
        Assert.DoesNotContain(_fixture.AllFiles(), file => !file.Contains('/') && file.StartsWith("custom-lib-01", StringComparison.Ordinal) &&
            File.ReadAllBytes(_fixture.PathOf(file)).SequenceEqual(preImages[10]));
    }

    // --- scenarios ------------------------------------------------------------------------------------------------------

    private (byte[] PreImage, string Manifest) CrashAfterCheckedRename()
    {
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        var preImage = File.ReadAllBytes(_fixture.PathOf("team-terms.csv"));
        var files = new FaultingFileSystem { ReplaceOverride = (_, _, _) => throw new IOException("refused", unchecked((int)0x80070498)) };
        var service = _fixture.Service(files);
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")))]));
        var manifest = ManifestName();
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        files.AfterMutation = (kind, source, destination) =>
        {
            if (kind == "move" && source == _fixture.PathOf("team-terms.csv") && destination!.EndsWith(".scribe-backup", StringComparison.Ordinal))
            {
                files.CrashNow();
            }
        };
        service.CompleteSave(prepared.Save);
        Assert.False(_fixture.Exists("team-terms.csv"));
        Assert.Single(Directory.GetFiles(_fixture.LibrariesDir, "*.scribe-backup"));
        return (preImage, manifest);
    }

    private byte[] CrashAfterReplaceWithOutsideVersion()
    {
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        var outside = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube from another app")));
        var files = new FaultingFileSystem();
        var service = _fixture.Service(files);
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(start, writes: [Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")))]));
        _fixture.WriteBytes("team-terms.csv", outside);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        files.AfterMutation = (kind, _, _) =>
        {
            if (kind == "replace")
            {
                files.CrashNow();
            }
        };
        service.CompleteSave(prepared.Save);
        var backup = Assert.Single(Directory.GetFiles(_fixture.LibrariesDir, "*.scribe-backup"));
        Assert.Equal(outside, File.ReadAllBytes(backup));
        return outside;
    }

    private static LibraryContent Content(int n) =>
        LibraryStorageFixture.Content($"lib-{n:D2}", $"lib {n}", ($"t{n}", $"T{n} saved"));

    // Eleven custom writes through the checked replace, the process ending right after operation 10's rename.
    private (byte[][] PreImages, string Manifest, long Generation, string Id) ElevenOperationCrash()
    {
        var preImages = new byte[11][];
        for (var n = 0; n <= 10; n++)
        {
            _fixture.Write($"lib-{n:D2}.csv", LibraryStorageFixture.Csv($"lib {n}", ($"t{n}", $"T{n}")));
            preImages[n] = File.ReadAllBytes(_fixture.PathOf($"lib-{n:D2}.csv"));
        }

        var files = new FaultingFileSystem { ReplaceOverride = (_, _, _) => throw new IOException("refused", unchecked((int)0x80070498)) };
        var service = _fixture.Service(files);
        var start = service.LoadCatalog();
        var prepared = service.PrepareSave(Changes.Of(start, writes: [.. Enumerable.Range(0, 11).Select(n => Changes.Edit(start, $"lib-{n:D2}", Content(n)))]));
        var manifest = ManifestName();
        LibraryJournalNames.TryParse(manifest, out var name);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        files.AfterMutation = (kind, source, destination) =>
        {
            if (kind == "move" && source == _fixture.PathOf("lib-10.csv") && destination!.EndsWith("-10.scribe-backup", StringComparison.Ordinal))
            {
                files.CrashNow();
            }
        };
        service.CompleteSave(prepared.Save);
        Assert.False(_fixture.Exists("lib-10.csv"));
        Assert.True(_fixture.Exists($"~g{name.Generation}-{name.ManifestId}-10.scribe-backup"));
        return (preImages, manifest, name.Generation, name.ManifestId);
    }

    private void AssertHeld(DictionaryLibraryService service, string manifest, LibraryIoFailure failure)
    {
        Assert.True(_fixture.Exists("journal/" + manifest));
        Assert.Empty(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*.set-aside.*.json"));
        var catalog = service.LoadCatalog();
        Assert.True(catalog.FilesAwaitingRelease > 0);
        var prepared = service.PrepareSave(Changes.Of(catalog, writes: [Changes.Create(LibraryStorageFixture.Content("custom-x", "X", ("x", "X")))]));
        Assert.Equal(LibraryPrepareStatus.PreviousSaveUnfinished, prepared.Status);
        Assert.Equal(failure, prepared.Failure);
        Assert.Throws<InvalidOperationException>(() => service.Import("a,A\n", "a"));
    }

    private string ManifestName() =>
        Path.GetFileName(Assert.Single(Directory.GetFiles(_fixture.Paths.LibraryJournalDir, "*" + LibraryJournalNames.ManifestSuffix)));

    private void LoseGeneration()
    {
        using var connection = _fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = 'libraries.generation';";
        command.ExecuteNonQuery();
    }

    private void SetGeneration(long generation)
    {
        using var connection = _fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE settings SET value = $value WHERE key = 'libraries.generation';";
        command.Parameters.AddWithValue("$value", generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
