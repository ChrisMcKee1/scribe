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
/// handed a backup name that exists; and no byte of an outside version seeded mid-Save is lost. The suite runs twice:
/// with J's doubles of the composer and the overlay (<see cref="LibraryFaultInjectionTests"/>), and with the real parts
/// since the integration commit (contract 9.3, <c>Integration.LibraryFaultInjectionRealPartsTests</c>), in classes of
/// their own so the two run side by side.
/// </summary>
public abstract class LibraryFaultInjectionSuite
{
    private const string EditedCsvName = "team-terms.csv";

    public enum ReplaceMode
    {
        Native,
        Refused,
        LeavesReplacement,
    }

    /// <summary>Whether the services are built from C's composer, O's overlay and X's codec rather than J's doubles.</summary>
    protected abstract bool RealParts { get; }

    [Theory]
    [InlineData(ReplaceMode.Native)]
    [InlineData(ReplaceMode.Refused)]
    [InlineData(ReplaceMode.LeavesReplacement)]
    public void Every_interruption_of_a_save_recovers_to_the_whole_old_or_the_whole_new_state(ReplaceMode mode)
    {
        var realParts = RealParts;
        var (old, @new, calls) = Reference(mode, realParts);
        Assert.True(calls > 10, $"The Save made only {calls} mutating calls, so the scenario is not exercising the journal.");

        foreach (var timing in (FaultingFileSystem.Timing[])[FaultingFileSystem.Timing.Before, FaultingFileSystem.Timing.After])
        {
            for (var n = 1; n <= calls; n++)
            {
                using var scenario = Scenario.Create(realParts);
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
                AssertOnlyThePreviousCopyIsWrittenOver(files, scenario.Fixture, $"call {n} {timing} ({mode})");

                // J-10d (1): whatever the crash left, a database holding a library row sits beside a witness.
                Assert.True(
                    !LibraryRowsStored(scenario.Fixture) || scenario.Fixture.Exists("journal/state.witness"),
                    $"call {n} {timing}: a library row without the witness");
                var recovering = new FaultingFileSystem();
                var recovered = Recovered(scenario, recovering);
                AssertOnlyThePreviousCopyIsWrittenOver(recovering, scenario.Fixture, $"recovery after call {n} {timing} ({mode})");
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
        var realParts = RealParts;
        var (_, @new, _) = Reference(ReplaceMode.Native, realParts);

        // A crash right after the commit, before any file is installed: the next start has the whole completion to do.
        using var counting = Scenario.Create(realParts);
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
                using var scenario = Scenario.Create(realParts);
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

                AssertOnlyThePreviousCopyIsWrittenOver(files, scenario.Fixture, $"recovery call {n} {timing}");
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
        var realParts = RealParts;
        var (old, @new, _) = Reference(ReplaceMode.Native, realParts);
        using var scenario = Scenario.Create(realParts);
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
        using var scenario = Scenario.Create(RealParts);
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
        var realParts = RealParts;
        var outside = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube from another app")));
        var outsideHash = LibraryContentHashing.Of(outside);
        using var counting = Scenario.Create(realParts);
        var countingFiles = new FaultingFileSystem();
        Changes.Save(counting.Fixture.Service(countingFiles), counting.Fixture.Settings, counting.Changes,
            beforeCommit: () => counting.Fixture.WriteBytes(EditedCsvName, outside));
        var calls = countingFiles.MutatingCalls;

        foreach (var timing in (FaultingFileSystem.Timing[])[FaultingFileSystem.Timing.Before, FaultingFileSystem.Timing.After])
        {
            for (var n = 1; n <= calls; n++)
            {
                using var scenario = Scenario.Create(realParts);
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
                AssertOnlyThePreviousCopyIsWrittenOver(files, scenario.Fixture, $"outside version, call {n} {timing}");
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
        using var fixture = new LibraryStorageFixture { RealParts = RealParts };
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

    [Theory]
    [InlineData(ReplaceMode.Native)]
    [InlineData(ReplaceMode.Refused)]
    [InlineData(ReplaceMode.LeavesReplacement)]
    public void Only_the_previous_copy_of_an_edits_document_is_ever_written_over(ReplaceMode mode)
    {
        // The single-slot previous copy is the one overwrite rules R1 and R7 allow (contract 6.6.4): Grok's G2 of round 2
        // was ruled that exception, not a finding. Every other move never overwrites, whatever it meets, outside versions
        // at every kind of target included.
        var realParts = RealParts;
        using var scenario = Scenario.Create(realParts);
        var files = Faulting(mode);
        var outside = Encoding.UTF8.GetBytes(LibraryStorageFixture.Csv("Team terms", ("kube", "Kube from another app")));

        var saved = Changes.Save(scenario.Fixture.Service(files), scenario.Fixture.Settings, scenario.Changes, beforeCommit: () =>
        {
            scenario.Fixture.WriteBytes(EditedCsvName, outside);
            scenario.Fixture.WriteBytes("custom-release-notes.csv", outside);
            scenario.Fixture.WriteBytes("edits/github.json", EditsDocument(realParts, "github", ("get hub", "GitHub from another app")));
            scenario.Fixture.WriteBytes("scratch.csv", outside);
        });

        Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome!.Status);
        var previous = Assert.Single(files.OverwriteDestinations);
        Assert.Equal(scenario.Fixture.PathOf("edits/data-and-ai.previous.json"), previous, ignoreCase: true);
        AssertOnlyThePreviousCopyIsWrittenOver(files, scenario.Fixture, mode.ToString());
    }

    // R1's exception, and the only one: a move that may write over a file only ever lands on an edits document's previous copy.
    private static void AssertOnlyThePreviousCopyIsWrittenOver(FaultingFileSystem files, LibraryStorageFixture fixture, string context)
    {
        foreach (var destination in files.OverwriteDestinations)
        {
            Assert.True(
                string.Equals(Path.GetDirectoryName(destination), fixture.Paths.LibraryEditsDir, StringComparison.OrdinalIgnoreCase) &&
                destination.EndsWith(".previous.json", StringComparison.OrdinalIgnoreCase),
                $"{context}: a move wrote over {Path.GetFileName(destination)}");
        }
    }

    private static (string Old, string New, int Calls) Reference(ReplaceMode mode, bool realParts)
    {
        using var scenario = Scenario.Create(realParts);
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
    private static string Recovered(Scenario scenario, FaultingFileSystem? files = null)
    {
        scenario.Fixture.Restart();
        var catalog = scenario.Fixture.Service(files).LoadCatalog();
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
    /// Recently deleted, restores an entry from it and purges another. With the real parts (contract 9.3), the composer,
    /// the overlay and the codec are C's, O's and X's, and the edits documents O's.
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

        public static Scenario Create(bool realParts = false)
        {
            var fixture = new LibraryStorageFixture { RealParts = realParts };
            fixture.Write(EditedCsvName, LibraryStorageFixture.Csv("Team terms", ("kube", "Kubernetes")));
            fixture.Write("old-library.csv", LibraryStorageFixture.Csv("Old", ("old", "Old")));
            fixture.WriteBytes("edits/data-and-ai.json", EditsDocument(realParts, "data-and-ai", ("data lake", "Data Lake Gen2")));
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
                    new LibraryWrite("github", true, LibraryOrigin.Existing, null, Edits: Edits(realParts, "github", ("get hub", "GitHub Enterprise"))),
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

    private static BuiltInLibraryEdits Edits(bool realParts, string libraryId, params (string Key, string Written)[] edits) =>
        realParts ? RealEdits.Written(libraryId, edits) : JsonEditsOverlay.Edits(libraryId, edits);

    private static byte[] EditsDocument(bool realParts, string libraryId, params (string Key, string Written)[] edits) =>
        realParts ? RealEdits.Document(libraryId, edits) : JsonEditsOverlay.Document(libraryId, edits);
}

/// <summary>J-1 with J's doubles of the composer and the overlay, as J's stream wrote it.</summary>
public sealed class LibraryFaultInjectionTests : LibraryFaultInjectionSuite
{
    protected override bool RealParts => false;
}
