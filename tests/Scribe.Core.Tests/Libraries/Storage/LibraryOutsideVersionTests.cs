using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// Nothing written outside Scribe is lost (rules R1, R3 and R7): an outside save between the pre-image check and the
/// install (J-3), outside saves racing the checked replace (J-3b, A13), two outside versions of one edits document in one
/// unresolved manifest (J-3c, A14), another app taking a planned preservation destination (J-3d, G12), and another app
/// creating a file where a new library was being saved (J-18, G3).
/// </summary>
public sealed class LibraryOutsideVersionTests : IDisposable
{
    private readonly LibraryStorageFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_outside_save_after_the_pre_image_check_is_kept_as_a_new_library_through_either_replace(bool checkedReplace)
    {
        // J-3, case W4.
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("My team terms", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team-terms");
        var files = checkedReplace ? RefusingNativeReplace() : new FaultingFileSystem();
        var service = _fixture.Service(files);
        var start = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team-terms", "My team terms", ("kube", "K8s"));
        var outside = Bytes(LibraryStorageFixture.Csv("My team terms", ("kube", "Kube from another app")));

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]),
            beforeCommit: () => _fixture.WriteBytes("team-terms.csv", outside));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        const string KeptId = "custom-my-team-terms-changed-outside-scribe";
        Assert.Equal(outside, File.ReadAllBytes(_fixture.PathOf(KeptId + ".csv")));
        Assert.Equal(new LibraryKeptVersion("team-terms", LibraryKeptVersionKind.OutsideVersion, KeptId), Assert.Single(saved.Outcome.KeptVersions));

        var reloaded = service.LoadCatalog();
        Assert.NotNull(reloaded.Find(KeptId));
        Assert.DoesNotContain(KeptId, reloaded.LocalState.EnabledIds);
        Assert.False(reloaded.LocalState.AiPermissions[KeptId]);
        Assert.Contains(reloaded.KeptVersions, kept => kept.KeptAsId == KeptId);
        Assert.False(files.ReplaceCalledWithExistingBackup);
    }

    [Fact]
    public void An_outside_save_between_the_checked_rename_and_the_install_is_moved_aside_and_kept_never_overwritten()
    {
        // J-3b, after step 2's rename and before step 4's move.
        var (service, start, edited) = TeamTerms();
        var outside = Bytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube E")));
        var files = RefusingNativeReplace();
        var injected = false;
        files.BeforeMutation = (kind, source, destination) =>
        {
            if (!injected && kind == "move" && source.EndsWith(".scribe-staged", StringComparison.Ordinal) &&
                destination == _fixture.PathOf("team-terms.csv"))
            {
                injected = true;
                File.WriteAllBytes(destination, outside);
            }
        };

        var saved = SaveThrough(files, start, edited);

        Assert.True(injected);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        Assert.Equal(outside, File.ReadAllBytes(_fixture.PathOf("custom-team-terms-changed-outside-scribe.csv")));
        Assert.DoesNotContain(_fixture.AllFiles(), file => file.Contains(".scribe-", StringComparison.Ordinal));
        _ = service;
    }

    [Fact]
    public void An_outside_save_just_before_the_checked_rename_goes_into_the_first_backup_and_is_kept()
    {
        // J-3b, before step 2.
        var (_, start, edited) = TeamTerms();
        var outside = Bytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube E")));
        var files = RefusingNativeReplace();
        var injected = false;
        files.BeforeMutation = (kind, source, destination) =>
        {
            if (!injected && kind == "move" && source == _fixture.PathOf("team-terms.csv") &&
                destination!.EndsWith(".scribe-backup", StringComparison.Ordinal))
            {
                injected = true;
                File.WriteAllBytes(source, outside);
            }
        };

        var saved = SaveThrough(files, start, edited);

        Assert.True(injected);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Status);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        Assert.Equal(outside, File.ReadAllBytes(_fixture.PathOf("custom-team-terms-changed-outside-scribe.csv")));
    }

    [Fact]
    public void A_new_outside_save_after_each_of_three_rounds_defers_the_install_and_every_version_is_kept()
    {
        // J-3b, three rounds: the operation defers rather than race another app, and nothing is overwritten.
        var (_, start, edited) = TeamTerms();
        var files = RefusingNativeReplace();
        var versions = new List<byte[]>();
        files.BeforeMutation = (kind, source, destination) =>
        {
            if (kind == "move" && source.EndsWith(".scribe-staged", StringComparison.Ordinal) && destination == _fixture.PathOf("team-terms.csv"))
            {
                var version = Bytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube E" + versions.Count)));
                versions.Add(version);
                File.WriteAllBytes(destination, version);
            }
        };

        var saved = SaveThrough(files, start, edited);

        Assert.Equal(3, versions.Count);
        Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, saved.Status);
        Assert.Equal(LibraryIoFailure.Other, saved.Failure);
        foreach (var version in versions)
        {
            Assert.Contains(_fixture.AllFiles(), file => File.ReadAllBytes(_fixture.PathOf(file)).SequenceEqual(version));
        }

        // Left alone, the next attempt installs the Save and every version is a library of its own.
        _fixture.Restart();
        _fixture.Service().LoadCatalog();
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(_fixture.PathOf("team-terms.csv")));
        foreach (var version in versions)
        {
            Assert.Contains(_fixture.AllFiles(), file => !file.Contains('/') && File.ReadAllBytes(_fixture.PathOf(file)).SequenceEqual(version));
        }

        Assert.DoesNotContain(_fixture.AllFiles(), file => file.Contains(".scribe-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("create")]
    [InlineData("remove")]
    public void Two_outside_versions_of_an_edits_document_in_one_unresolved_manifest_land_at_distinct_names(string kind)
    {
        // J-3c: another operation stays locked so the manifest stays unresolved while E1, then E2, reach the document.
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        const string Id = "github";
        if (kind != "create")
        {
            _fixture.WriteBytes("edits/github.json", JsonEditsOverlay.Document(Id, ("get hub", "GitHub P")));
        }

        _fixture.SaveEnabled("team-terms", Id);
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var write = kind switch
        {
            "remove" => new LibraryWrite(Id, true, LibraryOrigin.Existing, start.Find(Id)!.ContentHash, Edits: null),
            _ => new LibraryWrite(Id, true, LibraryOrigin.Existing, start.Find(Id)!.ContentHash, Edits: JsonEditsOverlay.Edits(Id, ("get hub", "GitHub S"))),
        };
        var e1 = JsonEditsOverlay.Document(Id, ("get hub", "GitHub E1"));
        var e2 = JsonEditsOverlay.Document(Id, ("get hub", "GitHub E2"));
        var changes = Changes.Of(start, writes: [write, Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")))]);
        var prepared = service.PrepareSave(changes);
        _fixture.WriteBytes("edits/github.json", e1);
        _fixture.Settings.SaveBundle(_fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        using (new FileStream(_fixture.PathOf("team-terms.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(LibrarySaveStatus.AppliedAwaitingRelease, service.CompleteSave(prepared.Save!).Status);
            AssertKept(e1);

            // E2 arrives while the manifest is still unresolved; every attempt, interrupted after each of its side effects
            // and restarted, keeps both versions under their own names.
            _fixture.WriteBytes("edits/github.json", e2);
            for (var n = 1; ; n++)
            {
                var files = new FaultingFileSystem { FailAt = n, FailTiming = FaultingFileSystem.Timing.After };
                _fixture.Restart();
                try
                {
                    _fixture.Service(files).LoadCatalog();
                }
                catch (Exception)
                {
                    // Died after that side effect.
                }

                AssertKept(e1);
                if (!files.Faulted)
                {
                    break;
                }
            }

            AssertKept(e2);
        }

        var names = SetAsideNames();
        Assert.Equal(2, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Another_apps_file_at_a_planned_keep_as_is_skipped_never_overwritten_and_a_restart_makes_no_second_copy()
    {
        // J-3d, the custom keepAs.
        var (service, start, edited) = TeamTerms();
        var outside = Bytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube E")));
        var foreign = Bytes("the other app's own file");

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]),
            beforeCommit: () =>
            {
                _fixture.WriteBytes("team-terms.csv", outside);
                _fixture.WriteBytes("custom-team-terms-changed-outside-scribe.csv", foreign);
            });

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(foreign, File.ReadAllBytes(_fixture.PathOf("custom-team-terms-changed-outside-scribe.csv")));
        Assert.Equal(outside, File.ReadAllBytes(_fixture.PathOf("custom-team-terms-changed-outside-scribe-2.csv")));
        var before = _fixture.AllFiles();

        _fixture.Restart();
        _fixture.Service().LoadCatalog();

        Assert.Equal(before, _fixture.AllFiles());
        Assert.False(_fixture.Exists("custom-team-terms-changed-outside-scribe-3.csv"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("reset")]
    [InlineData("remove")]
    public void Another_apps_file_at_a_planned_set_aside_name_is_skipped_never_overwritten(string kind)
    {
        // J-3d for an edits create (C3), a remove that sets aside (M2) and a remove meeting an outside version (M3).
        const string Id = "github";
        var preImage = JsonEditsOverlay.Document(Id, ("get hub", "GitHub P"));
        if (kind != "create")
        {
            _fixture.WriteBytes("edits/github.json", preImage);
        }

        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var write = kind switch
        {
            "create" => new LibraryWrite(Id, true, LibraryOrigin.Existing, null, Edits: JsonEditsOverlay.Edits(Id, ("get hub", "GitHub S"))),
            "reset" => new LibraryWrite(Id, true, LibraryOrigin.Existing, start.Find(Id)!.ContentHash, Recovery: BuiltInEditsRecovery.BackUpAndReset),
            _ => new LibraryWrite(Id, true, LibraryOrigin.Existing, start.Find(Id)!.ContentHash, Edits: null),
        };
        var outside = JsonEditsOverlay.Document(Id, ("get hub", "GitHub E"));
        var preserved = kind == "reset" ? preImage : outside;
        var foreign = Bytes("the other app's own file");
        var stem = "edits/github." + LibraryJournalNames.FormatStamp(LibraryStorageFixture.Start) + "." +
                   LibraryContentHashing.Prefix(LibraryContentHashing.Of(preserved));

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(start, writes: [write]), beforeCommit: () =>
        {
            if (kind != "reset")
            {
                _fixture.WriteBytes("edits/github.json", outside);
            }

            _fixture.WriteBytes(stem + ".backup.json", foreign);
        });

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(foreign, File.ReadAllBytes(_fixture.PathOf(stem + ".backup.json")));
        Assert.Equal(preserved, File.ReadAllBytes(_fixture.PathOf(stem + "-2.backup.json")));
        Assert.Equal(kind == "reset" ? 0 : 1, saved.Outcome.KeptVersions.Count(version => version.Kind == LibraryKeptVersionKind.EditsSetAside));
        var before = _fixture.AllFiles();
        _fixture.Restart();
        _fixture.Service().LoadCatalog();
        Assert.Equal(before, _fixture.AllFiles());
    }

    [Fact]
    public void A_file_another_app_creates_where_a_new_library_was_being_saved_stays_and_Scribes_bytes_go_under_a_new_id()
    {
        // J-18, case C3.
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        var created = LibraryStorageFixture.Content("custom-release-notes", "Release notes", ("sprint", "Sprint"));
        var foreign = Bytes(LibraryStorageFixture.Csv("Other app", ("other", "Other")));

        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(start, writes: [Changes.Create(created)], state: Changes.With(start.LocalState, enable: ["custom-release-notes"])),
            beforeCommit: () => _fixture.WriteBytes("custom-release-notes.csv", foreign));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(foreign, File.ReadAllBytes(_fixture.PathOf("custom-release-notes.csv")));
        Assert.Equal(LibraryStorageFixture.Managed(created), File.ReadAllBytes(_fixture.PathOf("custom-release-notes-2.csv")));
        Assert.Equal(new LibraryKeptVersion("custom-release-notes", LibraryKeptVersionKind.SavedUnderNewId, "custom-release-notes-2"),
            Assert.Single(saved.Outcome.KeptVersions));

        // The other app's file does not match what the Save accepted: it is adopted as replaced (off, not sent).
        var reloaded = service.LoadCatalog();
        Assert.DoesNotContain("custom-release-notes", reloaded.LocalState.EnabledIds);
        Assert.False(reloaded.LocalState.AiPermissions["custom-release-notes"]);
        Assert.False(reloaded.LocalState.AiPermissions["custom-release-notes-2"]);

        var files = _fixture.AllFiles();
        _fixture.Restart();
        _fixture.Service().LoadCatalog();
        _fixture.Restart();
        _fixture.Service().LoadCatalog();
        Assert.Equal(files, _fixture.AllFiles());
    }

    private (Scribe.Core.PostProcessing.DictionaryLibraryService Service, LibraryCatalog Start, LibraryContent Edited) TeamTerms()
    {
        _fixture.Write("team-terms.csv", LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        _fixture.SaveEnabled("team-terms");
        var service = _fixture.Service();
        var start = service.LoadCatalog();
        return (service, start, LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s")));
    }

    private LibrarySaveOutcome SaveThrough(FaultingFileSystem files, LibraryCatalog start, LibraryContent edited)
    {
        var service = _fixture.Service(files);
        var saved = Changes.Save(service, _fixture.Settings, Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]));
        Assert.False(files.ReplaceCalledWithExistingBackup);
        return saved.Outcome!;
    }

    private static FaultingFileSystem RefusingNativeReplace() => new()
    {
        ReplaceOverride = (_, _, _) => throw new IOException("injected refusal", unchecked((int)0x80070498)),
    };

    private IReadOnlyList<string> SetAsideNames() =>
        [.. _fixture.AllFiles().Where(file => file.StartsWith("edits/", StringComparison.Ordinal) && file.EndsWith(".backup.json", StringComparison.Ordinal))];

    private void AssertKept(byte[] version) =>
        Assert.Contains(SetAsideNames(), file => File.ReadAllBytes(_fixture.PathOf(file)).SequenceEqual(version));
}
