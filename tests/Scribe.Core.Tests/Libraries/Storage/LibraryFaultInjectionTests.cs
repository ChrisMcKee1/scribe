using System.Text;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// J-1 (R1, A2, G1): the journal interrupted at every step. A Save that touches every operation kind at once is driven
/// through prepare, the settings commit and completion with the file system failing before, and separately after, the
/// side effect of call n, for every n, and then failing everything, as a process that died there would; the same with
/// the native replace refused (every step of the checked replace), with it stopping in ReplaceFileW's 1177 state, and
/// through recovery itself. After each, a new service over the same folder and database resumes and must load the
/// complete old state or the complete new state, content and local state, never a mix; the native replace is never
/// handed a backup name that exists; and no byte of an outside version seeded mid-Save is lost.
/// </summary>
public sealed class LibraryFaultInjectionTests
{
    private const string EditedCsvName = "team-terms.csv";

    public enum ReplaceMode
    {
        Native,
        Refused,
        LeavesReplacement,
    }

    [Theory]
    [InlineData(ReplaceMode.Native)]
    [InlineData(ReplaceMode.Refused)]
    [InlineData(ReplaceMode.LeavesReplacement)]
    public void Every_interruption_of_a_save_recovers_to_the_whole_old_or_the_whole_new_state(ReplaceMode mode)
    {
        var (old, @new, calls) = Reference(mode);
        Assert.True(calls > 10, $"The Save made only {calls} mutating calls, so the scenario is not exercising the journal.");

        foreach (var timing in (FaultingFileSystem.Timing[])[FaultingFileSystem.Timing.Before, FaultingFileSystem.Timing.After])
        {
            for (var n = 1; n <= calls; n++)
            {
                using var scenario = Scenario.Create();
                var files = Faulting(mode);
                files.FailAt = n;
                files.FailTiming = timing;
                try
                {
                    Changes.Save(scenario.Fixture.Service(files), scenario.Fixture.Settings, scenario.Changes);
                }
                catch (Exception)
                {
                    // The process died here; what it left on disk is what the next one meets.
                }

                Assert.True(files.Faulted, $"call {n} {timing}: the fault never fired");
                Assert.False(files.ReplaceCalledWithExistingBackup, $"call {n} {timing}: the native replace was handed an existing backup");

                // J-10d (1): whatever the crash left, a database holding a library row sits beside a witness.
                Assert.True(
                    !LibraryRowsStored(scenario.Fixture) || scenario.Fixture.Exists("journal/state.witness"),
                    $"call {n} {timing}: a library row without the witness");
                var recovered = Recovered(scenario);
                Assert.True(
                    recovered == old || recovered == @new,
                    $"call {n} {timing} ({mode}): the recovered state is neither the old nor the new one:\n{recovered}\n--- old ---\n{old}\n--- new ---\n{@new}");
                Assert.Equal(scenario.Fixture.StoredGeneration == scenario.Start.Generation + 1 ? @new : old, recovered);
            }
        }
    }

    [Fact]
    public void Every_interruption_of_recovery_still_ends_in_the_new_state()
    {
        var (_, @new, _) = Reference(ReplaceMode.Native);

        // A crash right after the commit, before any file is installed: the next start has the whole completion to do.
        using var counting = Scenario.Create();
        CrashAfterCommit(counting);
        var countingFiles = new FaultingFileSystem();
        counting.Fixture.Restart();
        counting.Fixture.Service(countingFiles).LoadCatalog();
        var calls = countingFiles.MutatingCalls;
        Assert.True(calls > 5);

        foreach (var timing in (FaultingFileSystem.Timing[])[FaultingFileSystem.Timing.Before, FaultingFileSystem.Timing.After])
        {
            for (var n = 1; n <= calls; n++)
            {
                using var scenario = Scenario.Create();
                CrashAfterCommit(scenario);
                var files = new FaultingFileSystem { FailAt = n, FailTiming = timing };
                scenario.Fixture.Restart();
                try
                {
                    scenario.Fixture.Service(files).LoadCatalog();
                }
                catch (Exception)
                {
                    // Recovery died here.
                }

                Assert.Equal(@new, Recovered(scenario));
            }
        }
    }

    [Theory]
    [InlineData("library rows written", false)]
    [InlineData("save committing", false)]
    [InlineData("save committed", true)]
    public void A_settings_step_that_fails_leaves_the_old_state_before_the_commit_and_the_new_one_after(string step, bool committed)
    {
        var (old, @new, _) = Reference(ReplaceMode.Native);
        using var scenario = Scenario.Create();
        scenario.Fixture.Settings.WriteStep = (name, _, _) =>
        {
            if (name == step)
            {
                throw FaultingFileSystem.Injected();
            }
        };

        var saved = Changes.Save(scenario.Fixture.Service(), scenario.Fixture.Settings, scenario.Changes);

        Assert.NotNull(saved.CommitFailure);
        Assert.Equal(committed ? LibrarySaveStatus.Applied : LibrarySaveStatus.NotCommitted, saved.Outcome!.Status);
        scenario.Fixture.Settings.WriteStep = null;
        Assert.Equal(committed ? @new : old, Recovered(scenario));
    }

    [Theory]
    [InlineData("library state committing", false)]
    [InlineData("library state committed", true)]
    public void A_wrapper_whose_state_commit_step_fails_stands_exactly_when_it_committed(string step, bool committed)
    {
        using var scenario = Scenario.Create();
        var before = Recovered(scenario);
        var service = scenario.Fixture.Service();
        var reached = false;
        scenario.Fixture.Settings.WriteStep = (name, _, _) =>
        {
            if (name == step)
            {
                reached = true;
                throw FaultingFileSystem.Injected();
            }
        };

        var removal = Record.Exception(() => service.Remove("old-library"));

        scenario.Fixture.Settings.WriteStep = null;
        Assert.True(reached);
        Assert.Equal(committed, removal is null);
        Assert.Equal(!committed, scenario.Fixture.Exists("old-library.csv"));
        var after = Recovered(scenario);
        if (committed)
        {
            Assert.NotEqual(before, after);
            Assert.DoesNotContain("custom old-library", after, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public void No_byte_of_an_outside_version_seeded_between_prepare_and_completion_is_lost_at_any_interruption()
    {
        var outside = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube from another app")));
        var outsideHash = LibraryContentHashing.Of(outside);
        using var counting = Scenario.Create();
        var countingFiles = new FaultingFileSystem();
        Changes.Save(counting.Fixture.Service(countingFiles), counting.Fixture.Settings, counting.Changes,
            beforeCommit: () => counting.Fixture.WriteBytes(EditedCsvName, outside));
        var calls = countingFiles.MutatingCalls;

        foreach (var timing in (FaultingFileSystem.Timing[])[FaultingFileSystem.Timing.Before, FaultingFileSystem.Timing.After])
        {
            for (var n = 1; n <= calls; n++)
            {
                using var scenario = Scenario.Create();
                var files = new FaultingFileSystem { FailAt = n, FailTiming = timing };
                var seeded = false;
                try
                {
                    Changes.Save(scenario.Fixture.Service(files), scenario.Fixture.Settings, scenario.Changes,
                        beforeCommit: () =>
                        {
                            scenario.Fixture.WriteBytes(EditedCsvName, outside);
                            seeded = true;
                        });
                }
                catch (Exception)
                {
                    // The process died here.
                }

                Recovered(scenario);
                Assert.True(
                    !seeded || scenario.Fixture.AllFiles().Any(file => scenario.Fixture.HashOf(file) == outsideHash),
                    $"call {n} {timing}: the outside version's bytes are gone");
            }
        }
    }

    [Fact]
    public void The_native_replace_is_never_handed_a_backup_that_holds_another_apps_version()
    {
        // E1 lands before completion and E2 right after the first native replace moved E1 into the backup: that backup
        // is resolved (E1 kept) before the next replace, which would otherwise write E2 over the only copy of E1.
        using var fixture = new LibraryStorageFixture();
        fixture.Write(EditedCsvName, LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
        var first = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "First outside")));
        var second = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Second outside")));
        var replaces = 0;
        var files = new FaultingFileSystem();
        files.AfterMutation = (kind, _, _) =>
        {
            if (kind == "replace" && ++replaces == 1)
            {
                fixture.WriteBytes(EditedCsvName, second);
            }
        };
        var service = fixture.Service(files);
        var start = service.LoadCatalog();
        var edited = LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"));

        var saved = Changes.Save(service, fixture.Settings, Changes.Of(start, writes: [Changes.Edit(start, "team-terms", edited)]),
            beforeCommit: () => fixture.WriteBytes(EditedCsvName, first));

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.Equal(2, replaces);
        Assert.False(files.ReplaceCalledWithExistingBackup);
        Assert.Equal(LibraryStorageFixture.Managed(edited), File.ReadAllBytes(fixture.PathOf(EditedCsvName)));
        var kept = fixture.AllFiles().Select(fixture.HashOf).ToHashSet();
        Assert.Contains(LibraryContentHashing.Of(first), kept);
        Assert.Contains(LibraryContentHashing.Of(second), kept);
        Assert.Equal(2, saved.Outcome.KeptVersions.Count(version => version.Kind == LibraryKeptVersionKind.OutsideVersion));
    }

    private static bool LibraryRowsStored(LibraryStorageFixture fixture) =>
        new[] { LibrarySettingKeys.Generation, LibrarySettingKeys.State, LibrarySettingKeys.FileIds }.Any(key => fixture.Row(key) is not null);

    private static (string Old, string New, int Calls) Reference(ReplaceMode mode)
    {
        using var scenario = Scenario.Create();
        var old = Recovered(scenario);
        var files = Faulting(mode);
        var saved = Changes.Save(scenario.Fixture.Service(files), scenario.Fixture.Settings, scenario.Changes);
        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        Assert.False(files.ReplaceCalledWithExistingBackup);
        return (old, Recovered(scenario), files.MutatingCalls);
    }

    private static void CrashAfterCommit(Scenario scenario)
    {
        var service = scenario.Fixture.Service(new FaultingFileSystem());
        var prepared = service.PrepareSave(scenario.Changes);
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        scenario.Fixture.Settings.SaveBundle(scenario.Fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        // CompleteSave never runs: the process ended between the commit and the install.
    }

    private static FaultingFileSystem Faulting(ReplaceMode mode) => new()
    {
        ReplaceOverride = mode switch
        {
            ReplaceMode.Refused => (_, _, _) => throw new IOException("injected refusal", unchecked((int)0x80070498)),
            ReplaceMode.LeavesReplacement => (_, target, backup) =>
            {
                // ReplaceFileW's 1177: the target renamed to the backup, the replacement left under its own name.
                File.Move(target, backup);
                throw new IOException("injected 1177", unchecked((int)0x80070499));
            },
            _ => null,
        },
    };

    // A new process over the same folder and database: recovery at its first load, then what it reads.
    private static string Recovered(Scenario scenario)
    {
        scenario.Fixture.Restart();
        var catalog = scenario.Fixture.Service().LoadCatalog();
        Assert.Equal(0, catalog.FilesAwaitingRelease);
        return Fingerprint(catalog);
    }

    internal static string Fingerprint(LibraryCatalog catalog)
    {
        var text = new StringBuilder();
        text.Append("generation ").Append(catalog.Generation).Append('\n');
        foreach (var library in catalog.Libraries)
        {
            text.Append(library.Content.BuiltIn ? "built-in " : "custom ").Append(library.Content.Id).Append(' ')
                .Append(library.FileName).Append(' ').Append(library.State).Append(' ').Append(library.ContentHash?.Value)
                .Append(": ").AppendJoin("; ", library.Content.Rows.Select(row => row.Values.Spoken + "=" + row.Values.Written + (row.Values.Enabled ? string.Empty : " off")))
                .Append('\n');
        }

        foreach (var entry in catalog.RecentlyDeleted)
        {
            text.Append("deleted ").Append(entry.EntryName).Append(' ').Append(entry.OriginalId).Append(' ').Append(entry.ContentHash?.Value).Append('\n');
        }

        var state = catalog.LocalState;
        text.Append("enabled ").AppendJoin(",", state.EnabledIds.Order(StringComparer.OrdinalIgnoreCase)).Append('\n');
        text.Append("ai ").AppendJoin(",", state.AiPermissions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)).Append('\n');
        text.Append("accepted ").AppendJoin(",", state.AcceptedContent.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value.Value[..8])).Append('\n');
        text.Append("health ").Append(state.Health).Append(" lost ").Append(state.AiPermissionsLost).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// A folder whose first start has been adopted (so a state row and the witness exist), and a Save that writes a
    /// custom library, creates one, creates a built-in's edits document, removes another's, deletes a library into
    /// Recently deleted, restores an entry from it and purges another.
    /// </summary>
    private sealed class Scenario : IDisposable
    {
        private Scenario(LibraryStorageFixture fixture, LibraryCatalog start, LibraryChangeSet changes)
        {
            Fixture = fixture;
            Start = start;
            Changes = changes;
        }

        public LibraryStorageFixture Fixture { get; }

        public LibraryCatalog Start { get; }

        public LibraryChangeSet Changes { get; }

        public static Scenario Create()
        {
            var fixture = new LibraryStorageFixture();
            fixture.Write(EditedCsvName, LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
            fixture.Write("old-library.csv", LibraryStorageFixture.Csv("Old", ("old", "Old")));
            fixture.WriteBytes("edits/data-and-ai.json", JsonEditsOverlay.Document("data-and-ai", ("data lake", "Data Lake Gen2")));
            fixture.Write("deleted/20260901T101010Z.scratch.csv", LibraryStorageFixture.Csv("Scratch", ("scratch", "Scratch")));
            fixture.Write("deleted/20260801T090000Z.junk.csv", LibraryStorageFixture.Csv("Junk", ("junk", "Junk")));
            fixture.SaveEnabled("team-terms", "old-library", "github");
            var start = fixture.Service().LoadCatalog();
            Assert.Equal(1, start.Generation);

            var scratch = start.RecentlyDeleted.Single(entry => entry.OriginalId == "scratch");
            var junk = start.RecentlyDeleted.Single(entry => entry.OriginalId == "junk");
            var changes = Storage.Changes.Of(
                start,
                writes:
                [
                    Storage.Changes.Edit(start, "team-terms", LibraryStorageFixture.Content("team-terms", "Team terms", ("kube", "K8s"), ("north star", "North Star"))),
                    Storage.Changes.Create(LibraryStorageFixture.Content("custom-release-notes", "Release notes", ("sprint", "Sprint"))),
                    new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: JsonEditsOverlay.Edits("github", ("get hub", "GitHub Enterprise"))),
                    new LibraryWrite("data-and-ai", true, LibraryOrigin.Existing, start.Find("data-and-ai")!.ContentHash, Edits: null),
                ],
                deletions: [Storage.Changes.Delete(start, "old-library")],
                recentlyDeleted:
                [
                    new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, scratch.EntryName, scratch.ContentHash, "scratch"),
                    new RecentlyDeletedAction(RecentlyDeletedActionKind.DeletePermanently, junk.EntryName, junk.ContentHash),
                ],
                state: Storage.Changes.With(start.LocalState, enable: ["custom-release-notes", "scratch"]));
            return new Scenario(fixture, start, changes);
        }

        public void Dispose() => Fixture.Dispose();
    }
}
