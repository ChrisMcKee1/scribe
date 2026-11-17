using System.Collections.Concurrent;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;
using HangGuard = Scribe.Core.Tests.Concurrency.HangGuard;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackSaveProtocolTests
{
    private static readonly MicrophoneSelection Yeti = new("yeti", "Blue Yeti");
    private static readonly MicrophoneSelection Usb = new("usb", "USB Microphone");
    // A hang guard, never the verdict: work that was told to hold always reaches its hold (stream TR round 7).
    private static readonly TimeSpan HoldBound = TimeSpan.FromSeconds(30);

    // The pump's hang guard (stream TR round 7b): an owner thread that has run nothing for this long while its test still
    // waits is stuck. Twice the hold guard, so that a save that never reaches its hold fails there first, naming the hold;
    // this one ends a test stuck anywhere else, such as in an await of the save itself.
    private static readonly TimeSpan PumpIdleBound = HoldBound + HoldBound;

    public static TheoryData<string> ValidationFailures => new()
    {
        "dictionary", "snippet", "hotkey", "duration", "provider", "word pack",
    };

    public static TheoryData<string, string?> HoldAndEditCases => new()
    {
        { "prepare", null },
        { "complete", null },
        { "publication", null },
        { "prepare", "dictionary" },
        { "complete", "snippet" },
        { "publication", "pack" },
        { "publication", "ai" },
        { "publication", "microphone" },
        { "publication", "revert" },
        { "publication", "first-load" },
    };

    public static TheoryData<LibrarySaveStatus> Outcomes => new()
    {
        LibrarySaveStatus.Applied,
        LibrarySaveStatus.AppliedAwaitingRelease,
        LibrarySaveStatus.NotCommitted,
        LibrarySaveStatus.Superseded,
    };

    public static TheoryData<LibraryPrepareStatus> PreparationRefusals => new()
    {
        LibraryPrepareStatus.Stale,
        LibraryPrepareStatus.PreviousSaveUnfinished,
        LibraryPrepareStatus.ReadOnly,
        LibraryPrepareStatus.OutsideEdit,
        LibraryPrepareStatus.Failed,
    };

    public static TheoryData<string> UnknownSettlements => new()
    {
        "target", "base", "other", "failed-then-target", "switch-back", "explicit-settle",
    };

    public static TheoryData<string> RepairCases => new()
    {
        "success", "prepare-refused", "transaction-exception", "not-committed", "unknown", "delayed-success",
    };

    public static TheoryData<string> LateFailures => new()
    {
        "repair-prepare-refused", "repair-prepare-throws", "repair-commit-throws", "repair-not-committed", "repair-unknown", "outcome-unknown",
    };

    public static TheoryData<string, bool> ProductionSignatureCases => new()
    {
        { "toggle", false },
        { "toggle", true },
        { "delete", false },
        { "delete", true },
        { "restore", false },
        { "restore", true },
        { "permanent-delete", false },
        { "permanent-delete", true },
        { "edit-revert", true },
        { "first-load", false },
        { "first-load", true },
    };

    [Fact]
    public void SaveDraftSections_treat_first_load_without_edits_as_the_capture()
    {
        var unloaded = new SaveDraftSections(
            new SaveDraftSections.Section(false, false, "dictionary-empty"),
            new SaveDraftSections.Section(false, false, "snippets-empty"),
            new SaveDraftSections.Section(false, false, "wordpacks-empty"));
        var capture = unloaded.CaptureNow();
        var loadedClean = new SaveDraftSections(
            new SaveDraftSections.Section(true, false, "dictionary-loaded"),
            new SaveDraftSections.Section(true, false, "snippets-loaded"),
            new SaveDraftSections.Section(true, false, "wordpacks-loaded"));
        var loadedDirty = new SaveDraftSections(
            new SaveDraftSections.Section(true, true, "dictionary-edited"),
            new SaveDraftSections.Section(true, false, "snippets-loaded"),
            new SaveDraftSections.Section(true, false, "wordpacks-loaded"));

        Assert.Equal(Sign(unloaded, capture), Sign(loadedClean, capture));
        Assert.NotEqual(Sign(unloaded, capture), Sign(loadedDirty, capture));

        static string Sign(SaveDraftSections sections, SaveDraftSections.Capture capture)
        {
            var draft = new DraftSnapshot();
            return sections.Write(draft, capture).Hash();
        }
    }

    [Fact]
    public void Library_workspace_edit_revision_tracks_user_edits_only()
    {
        var workspace = DeciderFixture.Workspace(DeciderFixture.Standard());
        Assert.Equal(0, workspace.EditRevision);

        workspace.SetEnabled(DeciderFixture.AzureId, true);
        Assert.Equal(1, workspace.EditRevision);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        Assert.Equal(1, workspace.EditRevision);

        var changes = DeciderFixture.Capture(workspace);
        Assert.Equal(1, workspace.EditRevision);
        workspace.MarkSaved(changes, DeciderFixture.Apply(DeciderFixture.Standard(), changes));
        Assert.Equal(1, workspace.EditRevision);
        workspace.Reload(DeciderFixture.Standard());
        Assert.Equal(1, workspace.EditRevision);
        workspace.Rebase(DeciderFixture.Standard());
        Assert.Equal(1, workspace.EditRevision);

        workspace.SetEnabled(DeciderFixture.AzureId, true);
        Assert.Equal(2, workspace.EditRevision);
        workspace.Undo();
        Assert.Equal(3, workspace.EditRevision);
        workspace.Redo();
        Assert.Equal(4, workspace.EditRevision);
    }

    [Fact]
    public void Window_repair_commits_use_library_only_commit()
    {
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "Scribe.slnx")))
        {
            root = Directory.GetParent(root)!.FullName;
        }

        var settings = Path.Combine(root, "src", "Scribe.App", "Settings");
        var window = string.Join("\n", Directory.EnumerateFiles(settings, "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));
        var constructorStart = window.IndexOf("_wordPackSaveProtocol = new WordPackSaveProtocol", StringComparison.Ordinal);
        var constructorEnd = window.IndexOf("_savedAiProvider = _settings.AiCleanupProvider;", constructorStart, StringComparison.Ordinal);
        var constructor = window[constructorStart..constructorEnd];

        Assert.Contains("_settingsRepository.CommitLibraryState(payload)", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("_settingsRepository.SaveBundle", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_store_settles_a_save_through_the_default_route_a_second_completion()
    {
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "Scribe.slnx")))
        {
            root = Directory.GetParent(root)!.FullName;
        }

        var settings = Path.Combine(root, "src", "Scribe.App", "Settings");
        var window = string.Join("\n", Directory.EnumerateFiles(settings, "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));
        var start = window.IndexOf("private sealed class WordPackSaveStore : IWordPackSaveProtocolStore", StringComparison.Ordinal);
        var end = window.IndexOf("private sealed record AcceptedImport", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The window's word pack store was not found.");
        var store = window[start..end];

        // The window's store answers a settlement the way the interface's default does: completing the same preparation
        // again, which is what the library service reports by what recovery settled for that very Save.
        Assert.Contains("Task.Run(() => WordPackSaveSession.Complete(_store, prepared))", store, StringComparison.Ordinal);
        Assert.DoesNotContain("SettleAsync", store, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_settlement_is_a_second_completion_of_the_same_preparation()
    {
        var completions = new List<PreparedLibrarySave>();
        IWordPackSaveProtocolStore store = new CompletionOnlyStore(completions);
        var prepared = new PreparedLibrarySave(1, new LibrarySavePayload(3, 4, [], []), new LibraryChangeCounts(0, 0, 0, 0, 0));

        var outcome = await store.SettleAsync(prepared);

        Assert.Equal(LibrarySaveStatus.Applied, outcome.Status);
        Assert.Same(prepared, Assert.Single(completions));
    }

    [Theory]
    [MemberData(nameof(ValidationFailures))]
    public async Task Family1_validation_barrier_blocks_prepare_and_commit_then_corrected_save_proceeds(string invalidPart)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.ValidationErrors.Add($"invalid {invalidPart}");

            var blocked = await harness.SaveAsync();

            Assert.False(blocked.Success);
            Assert.Empty(harness.Store.Prepared);
            Assert.Empty(harness.Store.Completed);
            Assert.DoesNotContain("commit", harness.Trace);

            harness.ValidationErrors.Clear();
            var saved = await harness.SaveAsync();

            Assert.True(saved.Success);
            Assert.Single(harness.Store.Prepared);
            Assert.Single(harness.Store.Completed);
            Assert.False(harness.Workspace.HasUnsavedChanges);
        });
    }

    [Theory]
    [MemberData(nameof(HoldAndEditCases))]
    public async Task Family2_captured_draft_and_acknowledgement_keep_later_edits_unsaved(string hold, string? edit)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Hold(hold);
            var saveTask = harness.SaveAsync();
            await harness.WaitForHoldAsync(hold);

            if (edit is not null)
            {
                harness.EditDraft(edit);
            }

            harness.Release(hold);
            var result = await saveTask;

            if (edit is "dictionary" or "snippet" or "pack" or "ai" or "microphone")
            {
                Assert.False(result.Success);
                Assert.Contains("changed while saving", result.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.True(result.Success);
            }

            Assert.Equal("draft:pack", harness.CapturedDrafts.Single());
            Assert.Equal("payload", harness.StoredInputs.Single().Kind);
            Assert.Single(harness.Store.Prepared);
            Assert.Single(harness.Store.Completed);
        });
    }

    [Theory]
    [MemberData(nameof(ProductionSignatureCases))]
    public async Task Family2_production_word_pack_signature_ignores_commit_metadata_but_catches_later_edits(string operation, bool editWhileWaiting)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.UseProductionSignature = true;
            if (operation == "restore")
            {
                var deleted = DeciderFixture.Deleted("20260901T100000Z.custom-restored.csv", "custom-restored", "Restored", new TermValues("restore me", "Restore me"));
                harness.Catalog = DeciderFixture.Catalog(
                    harness.Catalog.Libraries,
                    enabled: harness.Catalog.LocalState.EnabledIds,
                    recentlyDeleted: [deleted.Entry]);
                harness.Workspace = DeciderFixture.Workspace(harness.Catalog);
                harness.Store.Current = harness.Catalog;
                harness.Store.Deleted[deleted.Entry.EntryName] = deleted;
                var restored = harness.Workspace.RestoreDeleted(deleted);
                harness.Workspace.SetEnabled(restored, true);
            }
            else if (operation == "delete")
            {
                harness.Workspace.DeleteLibrary("team-terms");
            }
            else if (operation == "permanent-delete")
            {
                var deleted = DeciderFixture.Deleted("20260901T100000Z.custom-purge.csv", "custom-purge", "Purge", new TermValues("purge me", "Purge me"));
                harness.Catalog = DeciderFixture.Catalog(
                    harness.Catalog.Libraries,
                    enabled: harness.Catalog.LocalState.EnabledIds,
                    recentlyDeleted: [deleted.Entry]);
                harness.Workspace = DeciderFixture.Workspace(harness.Catalog);
                harness.Store.Current = harness.Catalog;
                harness.Store.Deleted[deleted.Entry.EntryName] = deleted;
                harness.Workspace.DeletePermanently(deleted.Entry);
            }
            else
            {
                harness.EditPack();
            }

            harness.Hold("publication");
            var save = harness.SaveAsync();
            await harness.WaitForHoldAsync("publication");
            if (operation == "edit-revert")
            {
                harness.Workspace.SetEnabled(DeciderFixture.GitHubId, false);
                harness.Workspace.SetEnabled(DeciderFixture.GitHubId, true);
            }
            else if (editWhileWaiting)
            {
                harness.Workspace.SetEnabled(DeciderFixture.GitHubId, false);
            }

            harness.Release("publication");
            var result = await save;

            Assert.Equal(!editWhileWaiting, result.Success);
            if (editWhileWaiting)
            {
                Assert.Contains("changed while saving", result.Message, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public async Task Single_flight_refuses_settlement_while_save_runs()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Hold("complete");
            var save = harness.SaveAsync();
            await harness.WaitForHoldAsync("complete");

            var settlement = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.False(settlement.Success);
            Assert.Contains("still finishing", settlement.Message, StringComparison.Ordinal);
            harness.Release("complete");
            Assert.True((await save).Success);
        });
    }

    [Fact]
    public async Task Single_flight_refuses_save_while_settlement_runs()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            Assert.False((await harness.SaveAsync()).Success);
            harness.Store.Current = DeciderFixture.Apply(harness.Catalog, harness.Store.Prepared.Single());
            harness.Hold("load");
            var settlement = harness.Protocol.TrySettlePendingAsync(harness.Request());
            await harness.WaitForHoldAsync("load");

            var save = await harness.SaveAsync();

            Assert.False(save.Success);
            Assert.Contains("still finishing", save.Message, StringComparison.Ordinal);
            harness.Release("load");
            Assert.True((await settlement).Success);
        });
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Family3_outcome_matrix_follows_status_not_transaction_shape(LibrarySaveStatus status)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.Status = status;

            // A manifest still waiting for a file to be released answers the same when it is asked again: the genuine fence.
            if (status is LibrarySaveStatus.AppliedAwaitingRelease)
            {
                harness.Store.SettledStatus = LibrarySaveStatus.AppliedAwaitingRelease;
            }

            var result = await harness.SaveAsync();

            Assert.Single(harness.Store.Prepared);
            Assert.Single(harness.Store.Completed);
            if (status is LibrarySaveStatus.Applied)
            {
                Assert.True(result.Success);
                Assert.False(harness.Workspace.HasUnsavedChanges);
            }
            else if (status is LibrarySaveStatus.AppliedAwaitingRelease)
            {
                Assert.False(result.Success);
                Assert.False(harness.Workspace.HasUnsavedChanges);
                Assert.Single(harness.Trace, step => step == "apply");
                Assert.Contains("Scribe will finish saving it after you close it there", result.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(result.Success);
                Assert.True(harness.Workspace.HasUnsavedChanges);
            }
        });
    }

    [Theory]
    [MemberData(nameof(PreparationRefusals))]
    public async Task Family3_preparation_refusals_prepare_no_commit_and_stale_rebases(LibraryPrepareStatus status)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.PrepareStatus = status;
            harness.Store.Current = DeciderFixture.Catalog(harness.Catalog.Libraries, enabled: [DeciderFixture.GitHubId, DeciderFixture.AzureId], generation: 4);

            var result = await harness.SaveAsync();

            Assert.False(result.Success);
            Assert.Empty(harness.Store.Completed);
            Assert.DoesNotContain("commit", harness.Trace);
            if (status == LibraryPrepareStatus.Stale)
            {
                Assert.Equal(4, harness.Workspace.Draft.BaseGeneration);
            }
        });
    }

    [Fact]
    public async Task Outside_edit_result_carries_the_conflicted_pack_id()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.PrepareStatus = LibraryPrepareStatus.OutsideEdit;
            harness.Store.OutsideEditIds.Add(DeciderFixture.AzureId);

            var result = await harness.SaveAsync();

            Assert.False(result.Success);
            Assert.Equal([DeciderFixture.AzureId], result.TargetLibraryIds);
        });
    }

    [Theory]
    [InlineData(LibrarySaveStatus.NotCommitted, true)]
    [InlineData(LibrarySaveStatus.Applied, false)]
    public async Task Family3_transaction_exceptions_still_complete_and_settle_by_status(LibrarySaveStatus status, bool remainsUnsaved)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.Status = status;
            harness.ThrowOnCommit = true;

            var result = await harness.SaveAsync();

            Assert.False(result.Success);
            Assert.Single(harness.Store.Completed);
            Assert.Equal(remainsUnsaved, harness.Workspace.HasUnsavedChanges);

            // The library payload commits in the document's own transaction, so a Save the library completed as committed
            // stored the settings too, whatever the commit call did after its COMMIT (it threw here): they take effect,
            // once. A Save the library says never committed puts nothing into effect.
            Assert.Equal(remainsUnsaved ? 0 : 1, harness.Trace.Count(step => step == "apply"));
        });
    }

    [Theory]
    [MemberData(nameof(UnknownSettlements))]
    public async Task Family4_unknown_lifecycle_settles_only_while_unresolved(string settlement)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            var unknown = await harness.SaveAsync();
            Assert.False(unknown.Success);
            Assert.True(harness.Protocol.HasPendingSave);

            if (settlement == "failed-then-target")
            {
                harness.Store.ThrowOnLoad = true;
                var fenced = await harness.SaveAsync();
                Assert.True(harness.Protocol.HasPendingSave);
                Assert.Single(harness.Store.Prepared);
                Assert.False(fenced.Success);
                harness.Store.ThrowOnLoad = false;
                settlement = "target";
            }

            if (settlement == "switch-back")
            {
                harness.EditPack(backToOriginal: true);
                settlement = "base";
            }

            harness.Store.Current = settlement switch
            {
                "target" or "explicit-settle" => DeciderFixture.Apply(harness.Catalog, harness.Store.Prepared.Single()),
                "other" => DeciderFixture.Catalog(harness.Catalog.Libraries, generation: 99),
                _ => harness.Catalog,
            };

            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var i = 0; i < 12; i++)
            {
                _ = harness.Workspace.CaptureChangeSet();
            }

            var settleDirectly = settlement is "explicit-settle" or "base" or "other";
            var settled = settleDirectly
                ? await harness.Protocol.TrySettlePendingAsync(harness.Request())
                : await harness.SaveAsync();

            Assert.False(harness.Protocol.HasPendingSave);
            if (settlement == "target" || settlement == "explicit-settle")
            {
                Assert.True(settled.Success);
                Assert.False(harness.Workspace.HasUnsavedChanges);
            }
            else
            {
                Assert.False(settled.Success);
                Assert.Contains("weren't saved", settled.Message, StringComparison.Ordinal);
            }

            // Settled to another generation, the draft is rebased onto what is stored, so the workspace and the catalog the
            // shell is handed stay the same generation (the glossary composes against both).
            if (settlement == "other")
            {
                Assert.Equal(99, harness.Workspace.Draft.BaseGeneration);
            }
        });
    }

    [Theory]
    [MemberData(nameof(RepairCases))]
    public async Task Family5_repair_continuation_writes_only_reference_repairs(string repairCase)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            var original = harness.Workspace.CreateLibrary();
            harness.Workspace.AddTerm(original, new TermValues("ga", "general availability"));
            var copy = harness.Workspace.Duplicate(original);
            var keptAs = original + "-7";
            harness.Store.PrimaryOutcomes.Add(new DeciderFixture.StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")]));
            harness.Store.RepairPrepareStatus = repairCase == "prepare-refused" ? LibraryPrepareStatus.PreviousSaveUnfinished : LibraryPrepareStatus.Prepared;
            harness.Store.RepairStatus = repairCase switch
            {
                "not-committed" or "transaction-exception" => LibrarySaveStatus.NotCommitted,
                "unknown" => LibrarySaveStatus.CommitUnknown,
                _ => LibrarySaveStatus.Applied,
            };
            harness.ThrowRepairCommit = repairCase == "transaction-exception";
            harness.Store.Status = repairCase == "delayed-success" ? LibrarySaveStatus.CommitUnknown : LibrarySaveStatus.Applied;
            harness.Hold("complete");
            var save = harness.SaveAsync();
            await harness.WaitForHoldAsync("complete");
            harness.Workspace.SetEnabled(DeciderFixture.GitHubId, false);
            harness.Release("complete");

            var result = await save;
            if (repairCase == "delayed-success" && harness.Protocol.HasPendingSave)
            {
                harness.Store.Current = DeciderFixture.Apply(harness.Store.Current, harness.Store.Prepared.Last(), outcomes: harness.Store.PrimaryOutcomes);
                result = await harness.Protocol.TrySettlePendingAsync(harness.Request());
            }

            if (repairCase == "success" || repairCase == "delayed-success")
            {
                Assert.True(result.Success);
                Assert.False(harness.Workspace.HasPendingReferenceRepairs);
                Assert.Contains(harness.Store.Prepared, change => change.Writes.Count == 1 && change.Writes[0].LibraryId == copy);
                Assert.Contains(keptAs, harness.Workspace.Draft.Find(copy)!.Content.BasedOn, StringComparison.Ordinal);
                Assert.Single(harness.Store.RepairPayloads);
            }
            else
            {
                Assert.False(result.Success);
                Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
                Assert.Contains("reference", result.Message, StringComparison.OrdinalIgnoreCase);
                if (repairCase == "prepare-refused")
                {
                    Assert.DoesNotContain(harness.Store.Prepared, change => change.Writes.Count == 1 && change.Writes[0].LibraryId == copy);
                    Assert.Empty(harness.Store.RepairPayloads);
                }
                else
                {
                    Assert.Contains(harness.Store.Prepared, change => change.Writes.Count == 1 && change.Writes[0].LibraryId == copy);
                    Assert.Single(harness.Store.RepairPayloads);
                }

                Assert.True(harness.Workspace.HasPendingReferenceRepairs || harness.Protocol.HasPendingSave);
            }
        });
    }

    [Fact]
    public async Task Family4_a_save_recovery_settled_stays_saved_when_an_adoption_moved_the_generation_on_before_the_settlement()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            var created = harness.Workspace.CreateLibrary();
            harness.Workspace.AddTerm(created, new TermValues("ga", "general availability"));
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            Assert.False((await harness.SaveAsync()).Success);
            Assert.True(harness.Protocol.HasPendingSave);

            // Recovery installed the Save at its generation, and an adoption then committed the next one before the shell
            // asked: the generation stored is no longer the Save's, but the Save is the one recovery settled as committed.
            // Read by that generation it was a Save another commit superseded, and its new word pack was rebased as a second
            // new one beside the committed copy.
            var installed = DeciderFixture.Apply(harness.Catalog, harness.Store.Prepared.Single());
            harness.Store.Current = AdoptedOnce(installed);
            harness.Store.SettledStatus = LibrarySaveStatus.Applied;

            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.True(settled.Success);
            Assert.False(harness.Protocol.HasPendingSave);
            Assert.False(harness.Workspace.HasUnsavedChanges);
            Assert.Equal(harness.Store.Current.Libraries.Count, harness.Workspace.Draft.Libraries.Count);
            Assert.Equal(harness.Store.Current.Generation, harness.Workspace.Draft.BaseGeneration);
        });
    }

    [Fact]
    public async Task Family4_a_save_recovery_discarded_stays_unsaved_when_an_adoption_reached_the_generation_it_would_have_committed()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            var created = harness.Workspace.CreateLibrary();
            harness.Workspace.AddTerm(created, new TermValues("ga", "general availability"));
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            Assert.False((await harness.SaveAsync()).Success);

            // Recovery discarded the Save, and an adoption then committed the generation it would have: the generation stored
            // is the Save's, and the Save never committed, so its edits are still the user's to save.
            harness.Store.Current = AdoptedOnce(harness.Catalog);
            harness.Store.SettledStatus = LibrarySaveStatus.NotCommitted;

            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.False(settled.Success);
            Assert.Contains("weren't saved", settled.Message, StringComparison.Ordinal);
            Assert.False(harness.Protocol.HasPendingSave);
            Assert.True(harness.Workspace.HasUnsavedChanges);
            Assert.Equal(harness.Store.Current.Generation, harness.Workspace.Draft.BaseGeneration);
        });
    }

    [Theory]
    [MemberData(nameof(LateFailures))]
    public async Task Family6_the_settings_a_save_stored_take_effect_even_when_the_word_pack_work_after_it_fails(string failure)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            if (failure == "outcome-unknown")
            {
                harness.EditPack();
                harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            }
            else
            {
                ArrangeReferenceRepair(harness);
                harness.Store.RepairPrepareStatus = failure == "repair-prepare-refused"
                    ? LibraryPrepareStatus.PreviousSaveUnfinished
                    : LibraryPrepareStatus.Prepared;
                harness.ThrowOnRepairPrepare = failure == "repair-prepare-throws";
                harness.ThrowRepairCommit = failure == "repair-commit-throws";
                harness.Store.RepairStatus = failure switch
                {
                    "repair-unknown" => LibrarySaveStatus.CommitUnknown,
                    "repair-commit-throws" or "repair-not-committed" => LibrarySaveStatus.NotCommitted,
                    _ => LibrarySaveStatus.Applied,
                };
            }

            WordPackSaveProtocolResult? result = null;
            if (failure == "repair-prepare-throws")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SaveAsync());
            }
            else
            {
                result = await harness.SaveAsync();
            }

            // The settings were stored before any of this, and dictation is handed them once, after the store and whatever
            // came of the word packs: a Save never leaves the old settings running over the ones it stored (a history
            // retention of forever stored while the old seven days went on deleting history).
            Assert.Single(harness.Trace, step => step == "apply");
            Assert.True(harness.Trace.IndexOf("commit") < harness.Trace.IndexOf("apply"));
            if (result is not null)
            {
                Assert.False(result.Success);
                Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Family6_the_settings_a_save_stored_take_effect_when_the_shells_bookkeeping_after_the_store_fails(bool withWordPackChange)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            if (withWordPackChange)
            {
                harness.EditPack();
            }

            harness.ThrowOnSettingsCommitted = true;

            if (withWordPackChange)
            {
                var result = await harness.SaveAsync();
                Assert.False(result.Success);
                Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
                Assert.Contains("hit an error", result.Message, StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SaveAsync());
            }

            Assert.Single(harness.Trace, step => step == "apply");
            Assert.True(harness.Trace.IndexOf("settings-committed") < harness.Trace.IndexOf("apply"));
        });
    }

    [Fact]
    public async Task Family6_a_settlement_puts_a_committed_save_into_effect_even_when_its_follow_up_repairs_fail()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            ArrangeReferenceRepair(harness);
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            harness.Store.RepairPrepareStatus = LibraryPrepareStatus.PreviousSaveUnfinished;
            Assert.False((await harness.SaveAsync()).Success);
            harness.Store.Current = DeciderFixture.Apply(
                harness.Catalog, harness.Store.Prepared.Single(), outcomes: harness.Store.PrimaryOutcomes);
            var applied = harness.Trace.Count(step => step == "apply");

            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.False(settled.Success);
            Assert.Contains("reference", settled.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(applied + 1, harness.Trace.Count(step => step == "apply"));
        });
    }

    [Theory]
    [InlineData("unknown outcome", false)]
    [InlineData("unknown outcome", true)]
    [InlineData("reference repair", false)]
    [InlineData("reference repair", true)]
    public async Task Family6_a_committed_save_whose_stored_settings_cannot_be_applied_reports_what_is_known_of_it_and_applies_them_once_they_can_be_read(
        string failure, bool answerFaults)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            if (failure == "unknown outcome")
            {
                harness.EditPack();
                harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            }
            else
            {
                ArrangeReferenceRepair(harness);
                harness.Store.RepairPrepareStatus = LibraryPrepareStatus.PreviousSaveUnfinished;
            }

            harness.ThrowOnApply = !answerFaults;
            harness.FaultOnApply = answerFaults;

            // The commit stands, and the store the settings are read back from cannot be read right now. What the Save
            // reports is what is known of it (the outcome it could not confirm, the reference it could not update), not a
            // failed save, and the application stays owed.
            var result = await harness.SaveAsync();

            Assert.False(result.Success);
            Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
            Assert.Contains(failure == "unknown outcome" ? "couldn't confirm" : "reference", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(failure == "unknown outcome", harness.Protocol.HasPendingSave);
            Assert.True(harness.Protocol.ApplicationOwed);
            Assert.Single(harness.Trace, step => step == "apply");

            // The store reads again: the settlement pays the debt, and puts the settings into effect as stored.
            harness.ThrowOnApply = false;
            harness.FaultOnApply = false;
            if (failure == "unknown outcome")
            {
                harness.Store.Current = DeciderFixture.Apply(harness.Catalog, harness.Store.Prepared.Single());
            }

            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.True(settled.Success);
            Assert.False(harness.Protocol.HasPendingSave);
            Assert.False(harness.Protocol.ApplicationOwed);
            Assert.Equal(2, harness.Trace.Count(step => step == "apply"));

            // The settlement of an unknown outcome reached the store as a second completion of the same preparation: the
            // default route, the one the window's store takes.
            Assert.Equal(failure == "unknown outcome" ? 1 : 0, harness.Store.Settlements.Count);
        });
    }

    [Fact]
    public async Task Family6_a_settings_only_save_whose_stored_settings_cannot_be_applied_is_reported_as_saved_and_the_next_save_applies_them()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.ThrowOnApply = true;

            var first = await harness.SaveAsync();

            Assert.False(first.Success);
            Assert.Contains("Changes saved", first.Message, StringComparison.Ordinal);
            Assert.True(harness.Protocol.ApplicationOwed);
            Assert.False(harness.Protocol.HasPendingSave);
            Assert.Single(harness.Trace, step => step == "commit");

            // The debt never keeps a Save from running: the next one commits and applies, and pays it.
            harness.ThrowOnApply = false;
            var second = await harness.SaveAsync();

            Assert.True(second.Success);
            Assert.False(harness.Protocol.ApplicationOwed);
            Assert.Equal(2, harness.Trace.Count(step => step == "apply"));
        });
    }

    [Fact]
    public async Task Family6_a_settlement_applies_the_committed_settings_even_when_its_reference_repair_throws()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            ArrangeReferenceRepair(harness);
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            Assert.False((await harness.SaveAsync()).Success);
            harness.Store.Current = DeciderFixture.Apply(
                harness.Catalog, harness.Store.Prepared.Single(), outcomes: harness.Store.PrimaryOutcomes);
            harness.ThrowOnRepairPrepare = true;
            var applied = harness.Trace.Count(step => step == "apply");

            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.False(settled.Success);
            Assert.Contains("reference", settled.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(harness.Protocol.HasPendingSave);
            Assert.Equal(applied + 1, harness.Trace.Count(step => step == "apply"));
        });
    }

    // An application a committed Save still owes is tried again on the way out of every operation, whatever it ends in. A
    // settlement that kept nothing, a Save the library did not commit, a refusal, a validation error and a failure each
    // left the old settings running over what the earlier Save stored (a history retention of forever stored over the old
    // seven days) until some Save of its own happened to commit.
    [Theory]
    [InlineData("validation error")]
    [InlineData("preparation refused")]
    [InlineData("not committed")]
    [InlineData("settlement kept nothing")]
    [InlineData("exception")]
    public async Task T08_an_application_an_earlier_save_still_owes_is_tried_again_on_the_way_out_of_every_operation(string exit)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);

            // The earlier Save committed, and the stored settings could not be read back: dictation keeps the old ones.
            harness.ThrowOnApply = true;
            Assert.False((await harness.SaveAsync()).Success);
            Assert.True(harness.Protocol.ApplicationOwed);
            if (exit == "settlement kept nothing")
            {
                // A later Save whose outcome is not known, while the settings still cannot be read.
                harness.EditPack();
                harness.Store.Status = LibrarySaveStatus.CommitUnknown;
                Assert.False((await harness.SaveAsync()).Success);
                Assert.True(harness.Protocol.HasPendingSave);
                Assert.True(harness.Protocol.ApplicationOwed);
            }

            harness.ThrowOnApply = false;
            var applied = harness.Trace.Count(step => step == "apply");

            WordPackSaveProtocolResult? result = null;
            switch (exit)
            {
                case "validation error":
                    harness.ValidationErrors.Add("Fix this setting first.");
                    result = await harness.SaveAsync();
                    Assert.Equal("Fix this setting first.", result.Message);
                    break;
                case "preparation refused":
                    harness.EditPack();
                    harness.Store.PrepareStatus = LibraryPrepareStatus.PreviousSaveUnfinished;
                    result = await harness.SaveAsync();
                    break;
                case "not committed":
                    harness.EditPack();
                    harness.ThrowOnCommit = true;
                    harness.Store.Status = LibrarySaveStatus.NotCommitted;
                    result = await harness.SaveAsync();
                    break;
                case "settlement kept nothing":
                    // The library's receipt for that Save says it never committed, so the edit stays the draft's.
                    result = await harness.Protocol.TrySettlePendingAsync(harness.Request());
                    Assert.False(harness.Protocol.HasPendingSave);
                    Assert.Contains("weren't saved", result.Message, StringComparison.Ordinal);
                    break;
                default:
                    harness.ThrowOnCommit = true;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SaveAsync());
                    break;
            }

            // The operation said what it always says, and the old settings were put into effect once on its way out.
            if (result is not null)
            {
                Assert.False(result.Success);
            }

            Assert.False(harness.Protocol.ApplicationOwed);
            Assert.Equal(applied + 1, harness.Trace.Count(step => step == "apply"));
        });
    }

    [Fact]
    public async Task T08_an_exit_retry_that_fails_leaves_the_debt_owed_and_never_turns_a_settlement_into_a_success()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var reported = new ConcurrentQueue<SettingsApplicationStep>();
            using var debt = new SettingsApplicationDebt(reportFailure: (step, _) => reported.Enqueue(step));
            using var harness = ProtocolHarness.Create(owner, debt);
            harness.ThrowOnApply = true;
            Assert.False((await harness.SaveAsync()).Success);

            // A refusal says what it always says, and its own retry fails too.
            harness.ValidationErrors.Add("Fix this setting first.");
            var refused = await harness.SaveAsync();

            Assert.Equal("Fix this setting first.", refused.Message);
            Assert.Equal(WordPackSaveProtocolSeverity.Error, refused.Severity);
            Assert.True(harness.Protocol.ApplicationOwed);
            Assert.Equal(2, harness.Trace.Count(step => step == "apply"));

            // Nothing is unsettled and the settings still cannot be read: not a success, and still owed.
            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.False(settled.Success);
            Assert.Contains("still using the previous settings", settled.Message, StringComparison.Ordinal);
            Assert.True(harness.Protocol.ApplicationOwed);
            Assert.Equal(3, harness.Trace.Count(step => step == "apply"));
            Assert.Equal([SettingsApplicationStep.Apply, SettingsApplicationStep.Apply, SettingsApplicationStep.Apply], reported.ToArray());
        });
    }

    // A failure putting a committed Save's stored settings into effect (the stored settings cannot be read, applying them
    // throws, the answer faults) reaches the host as a failure for it to describe by its shape, whatever its sink does, and
    // the Save says what is true of it: stored, and dictation is still on the previous settings, not only the previous
    // vocabulary.
    [Theory]
    [InlineData("the read fails", false)]
    [InlineData("the answer faults", false)]
    [InlineData("the vocabulary-only answer faults", false)]
    [InlineData("the read fails", true)]
    public async Task T10_a_failure_putting_the_stored_settings_into_effect_reaches_the_host_and_its_sink_cannot_change_the_save(
        string failure, bool sinkThrows)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var reported = new ConcurrentQueue<(SettingsApplicationStep Step, Exception Failure)>();
            using var debt = new SettingsApplicationDebt(reportFailure: (step, error) =>
            {
                reported.Enqueue((step, error));
                if (sinkThrows)
                {
                    throw new InvalidOperationException("injected: the sink threw");
                }
            });
            using var harness = ProtocolHarness.Create(owner, debt);
            harness.ThrowOnApply = failure == "the read fails";
            harness.FaultOnApply = failure is "the answer faults" or "the vocabulary-only answer faults";
            harness.Reapplied = failure == "the vocabulary-only answer faults"
                ? StoredSettingsReapplied.VocabularyOnly
                : StoredSettingsReapplied.StoredSettings;

            var result = await harness.SaveAsync();

            Assert.False(result.Success);
            Assert.Equal(WordPackSaveProtocolSeverity.Warning, result.Severity);
            Assert.Contains("Changes saved", result.Message, StringComparison.Ordinal);
            Assert.Contains("still using the previous settings", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("vocabulary", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(harness.Protocol.ApplicationOwed);
            Assert.Single(harness.Trace, step => step == "commit");

            var told = Assert.Single(reported);
            Assert.Equal(SettingsApplicationStep.Apply, told.Step);
            Assert.IsType<IOException>(told.Failure);
        });
    }

    [Fact]
    public async Task Vocabulary_only_application_waits_for_recovery_and_keeps_scalar_and_word_pack_failure_notices_distinct()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            foreach (var vocabularyOutcome in new[] { VocabularyRefreshOutcome.Applied, VocabularyRefreshOutcome.TimedOut })
            {
                foreach (var wordPackOutcome in new[] { "none", "awaiting release", "unknown save", "unknown repair", "reference failure" })
                {
                    var reported = new List<SettingsApplicationStep>();
                    using var debt = new SettingsApplicationDebt(reportFailure: (step, _) => reported.Add(step));
                    using var harness = ProtocolHarness.Create(owner, debt);
                    harness.Reapplied = StoredSettingsReapplied.VocabularyOnly;
                    harness.RefreshOutcome = vocabularyOutcome;
                    if (wordPackOutcome is "awaiting release" or "unknown save")
                    {
                        harness.EditPack();
                        harness.Store.Status = wordPackOutcome == "unknown save"
                            ? LibrarySaveStatus.CommitUnknown
                            : LibrarySaveStatus.AppliedAwaitingRelease;
                        harness.Store.SettledStatus = harness.Store.Status;
                    }
                    else if (wordPackOutcome is "unknown repair" or "reference failure")
                    {
                        ArrangeReferenceRepair(harness);
                        harness.Store.RepairStatus = wordPackOutcome == "unknown repair"
                            ? LibrarySaveStatus.CommitUnknown
                            : LibrarySaveStatus.NotCommitted;
                    }

                    harness.Hold("publication");
                    var saving = harness.SaveAsync();
                    await harness.WaitForHoldAsync("publication");
                    Assert.False(saving.IsCompleted);
                    Assert.True(debt.IsOwed);
                    Assert.Empty(reported);
                    harness.Release("publication");
                    var result = await saving;

                    Assert.False(result.Success);
                    Assert.True(debt.IsOwed);
                    Assert.Equal([SettingsApplicationStep.Apply], reported);
                    if (wordPackOutcome == "unknown save")
                    {
                        Assert.True(harness.Protocol.HasPendingSave);
                        Assert.Contains("couldn't confirm", result.Message, StringComparison.Ordinal);
                    }
                    else if (wordPackOutcome is "unknown repair" or "reference failure")
                    {
                        Assert.Equal(wordPackOutcome == "unknown repair", harness.Protocol.HasPendingSave);
                        Assert.Contains("reference still needs", result.Message, StringComparison.Ordinal);
                    }
                    else
                    {
                        Assert.Contains("still using the previous settings", result.Message, StringComparison.Ordinal);
                    }
                }
            }
        });
    }

    [Fact]
    public async Task T10_a_settlement_that_cannot_read_where_a_save_stands_reaches_the_host_and_the_save_stays_unsettled()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var reported = new ConcurrentQueue<(SettingsApplicationStep Step, Exception Failure)>();
            using var debt = new SettingsApplicationDebt(reportFailure: (step, error) => reported.Enqueue((step, error)));
            using var harness = ProtocolHarness.Create(owner, debt);
            harness.EditPack();
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            Assert.False((await harness.SaveAsync()).Success);
            Assert.True(harness.Protocol.HasPendingSave);
            harness.Store.ThrowOnSettle = true;

            var unfinished = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.False(unfinished.Success);
            Assert.Contains("still finishing", unfinished.Message, StringComparison.Ordinal);
            Assert.True(harness.Protocol.HasPendingSave);
            var told = Assert.Single(reported);
            Assert.Equal(SettingsApplicationStep.Settlement, told.Step);
            Assert.IsType<IOException>(told.Failure);

            // Read again, it settles as it would have: nothing was lost or decided by the failure.
            harness.Store.ThrowOnSettle = false;
            harness.Store.Current = DeciderFixture.Apply(harness.Catalog, harness.Store.Prepared.Single());
            var settled = await harness.Protocol.TrySettlePendingAsync(harness.Request());

            Assert.True(settled.Success, settled.Message);
            Assert.False(harness.Protocol.HasPendingSave);
            Assert.Single(reported);
        });
    }

    // The debt belongs to the owner the host made for the process, not to the protocol of the window that raised it: a window
    // that closes takes nothing with it, and the next one finds it owed and pays it with its first settlement.
    [Fact]
    public async Task T12_a_debt_outlives_the_protocol_that_raised_it_and_the_next_windows_first_settlement_pays_it()
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var debt = new SettingsApplicationDebt();
            using var closed = ProtocolHarness.Create(owner, debt);
            closed.ThrowOnApply = true;
            Assert.False((await closed.SaveAsync()).Success);
            Assert.True(debt.IsOwed);

            using var next = ProtocolHarness.Create(owner, debt);

            Assert.True(next.Protocol.ApplicationOwed);
            Assert.False(next.Protocol.HasPendingSave);
            var settled = await next.Protocol.TrySettlePendingAsync(next.Request());

            Assert.True(settled.Success, settled.Message);
            Assert.False(debt.IsOwed);
            Assert.Single(next.Trace, step => step == "apply");
            Assert.DoesNotContain("commit", next.Trace);
        });
    }

    // A receipt that is only AppliedAwaitingRelease is revisable (ILibraryCatalogStore.CompleteSave): the catalog read that
    // follows it runs a recovery attempt, which can finish that very manifest or set it aside. So the draft is marked saved
    // on what the Save is asked after the read, never on the receipt read before it, and a genuine fence, a manifest still
    // waiting for a file, is kept as it was. These hold the catalog read at its first call and let recovery decide there.
    [Theory]
    [InlineData(LibrarySaveStatus.AppliedAwaitingRelease)]
    [InlineData(LibrarySaveStatus.Applied)]
    [InlineData(LibrarySaveStatus.Superseded)]
    [InlineData(LibrarySaveStatus.NotCommitted)]
    [InlineData(LibrarySaveStatus.CommitUnknown)]
    public async Task V01_a_save_completed_awaiting_release_is_marked_saved_only_on_what_it_is_asked_after_the_catalog_read(
        LibrarySaveStatus asked)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.Status = LibrarySaveStatus.AppliedAwaitingRelease;
            harness.Store.SettledSequence.Enqueue(asked);
            harness.Hold("load");

            var saving = harness.SaveAsync();
            await harness.WaitForHoldAsync("load");

            // Recovery sets the manifest aside while the catalog is read: the files hold none of the Save.
            if (asked is LibrarySaveStatus.Superseded or LibrarySaveStatus.NotCommitted)
            {
                harness.Store.Current = harness.Catalog;
            }

            harness.Release("load");
            var result = await saving;

            // The question is asked again after the read, and one catalog read more follows an answer that moved.
            var steps = harness.Trace.Where(step => step is "complete" or "complete-again" or "load").ToArray();
            string[] expected = asked switch
            {
                LibrarySaveStatus.AppliedAwaitingRelease or LibrarySaveStatus.CommitUnknown => ["complete", "load", "complete-again"],
                LibrarySaveStatus.Superseded => ["complete", "load", "complete-again", "load", "load"],
                _ => ["complete", "load", "complete-again", "load"],
            };
            Assert.Equal(expected, steps);
            Assert.Single(harness.Trace, step => step == "apply");
            switch (asked)
            {
                case LibrarySaveStatus.AppliedAwaitingRelease:
                    Assert.False(result.Success);
                    Assert.Contains("Scribe will finish saving it after you close it there", result.Message, StringComparison.Ordinal);
                    Assert.False(harness.Workspace.HasUnsavedChanges);
                    Assert.False(harness.Protocol.HasPendingSave);
                    break;
                case LibrarySaveStatus.Applied:
                    Assert.True(result.Success, result.Message);
                    Assert.False(harness.Workspace.HasUnsavedChanges);
                    Assert.False(harness.Protocol.HasPendingSave);
                    break;
                case LibrarySaveStatus.CommitUnknown:
                    Assert.False(result.Success);
                    Assert.Contains("couldn't confirm", result.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.True(harness.Workspace.HasUnsavedChanges);
                    Assert.True(harness.Protocol.HasPendingSave);
                    break;
                default:
                    // Nothing of the Save is installed: the draft keeps its edit, rebased onto what is stored.
                    Assert.False(result.Success);
                    Assert.True(harness.Workspace.HasUnsavedChanges);
                    Assert.False(harness.Protocol.HasPendingSave);
                    Assert.Equal(harness.Catalog.Generation, harness.Workspace.Draft.BaseGeneration);
                    break;
            }
        });
    }

    [Theory]
    [InlineData(LibrarySaveStatus.AppliedAwaitingRelease)]
    [InlineData(LibrarySaveStatus.Applied)]
    [InlineData(LibrarySaveStatus.Superseded)]
    [InlineData(LibrarySaveStatus.NotCommitted)]
    [InlineData(LibrarySaveStatus.CommitUnknown)]
    public async Task V02_a_settlement_that_reads_awaiting_release_marks_the_draft_saved_only_on_what_it_is_asked_after_the_catalog_read(
        LibrarySaveStatus asked)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Store.Status = LibrarySaveStatus.CommitUnknown;
            Assert.False((await harness.SaveAsync()).Success);
            Assert.True(harness.Protocol.HasPendingSave);

            // Recovery left the manifest unresolved for a file another app holds: installed on its way, until the catalog
            // read that follows decides it.
            harness.Store.Current = DeciderFixture.Apply(harness.Catalog, harness.Store.Prepared.Single());
            harness.Store.SettledSequence.Enqueue(LibrarySaveStatus.AppliedAwaitingRelease);
            harness.Store.SettledSequence.Enqueue(asked);
            harness.Trace.Clear();
            harness.Hold("load");

            var settling = harness.Protocol.TrySettlePendingAsync(harness.Request());
            await harness.WaitForHoldAsync("load");
            if (asked is LibrarySaveStatus.Superseded or LibrarySaveStatus.NotCommitted)
            {
                harness.Store.Current = harness.Catalog;
            }

            harness.Release("load");
            var result = await settling;

            var steps = harness.Trace.Where(step => step is "complete" or "complete-again" or "load").ToArray();
            string[] expected = asked is LibrarySaveStatus.AppliedAwaitingRelease or LibrarySaveStatus.CommitUnknown
                ? ["complete-again", "load", "complete-again"]
                : ["complete-again", "load", "complete-again", "load"];
            Assert.Equal(expected, steps);
            if (asked is LibrarySaveStatus.AppliedAwaitingRelease or LibrarySaveStatus.Applied)
            {
                Assert.True(result.Success, result.Message);
                Assert.False(harness.Protocol.HasPendingSave);
                Assert.False(harness.Workspace.HasUnsavedChanges);
                Assert.Equal(harness.Store.Current.Generation, harness.Workspace.Draft.BaseGeneration);
                Assert.Single(harness.Trace, step => step == "apply");
            }
            else if (asked == LibrarySaveStatus.CommitUnknown)
            {
                Assert.False(result.Success);
                Assert.Contains("still finishing", result.Message, StringComparison.Ordinal);
                Assert.True(harness.Protocol.HasPendingSave);
                Assert.True(harness.Workspace.HasUnsavedChanges);
                Assert.DoesNotContain("apply", harness.Trace);
            }
            else
            {
                Assert.False(result.Success);
                Assert.Contains("weren't saved", result.Message, StringComparison.Ordinal);
                Assert.False(harness.Protocol.HasPendingSave);
                Assert.True(harness.Workspace.HasUnsavedChanges);
                Assert.Equal(harness.Catalog.Generation, harness.Workspace.Draft.BaseGeneration);
                Assert.DoesNotContain("apply", harness.Trace);
            }
        });
    }

    [Theory]
    [InlineData(LibrarySaveStatus.AppliedAwaitingRelease)]
    [InlineData(LibrarySaveStatus.Superseded)]
    [InlineData(LibrarySaveStatus.CommitUnknown)]
    public async Task V03_a_reference_repair_completed_awaiting_release_is_marked_saved_only_on_what_it_is_asked_after_the_catalog_read(
        LibrarySaveStatus asked)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var harness = ProtocolHarness.Create(owner);
            ArrangeReferenceRepair(harness);
            harness.Store.RepairStatus = LibrarySaveStatus.AppliedAwaitingRelease;

            // The Save itself is Applied, which is final and never asked again: the first question is the repair's.
            harness.Store.SettledSequence.Enqueue(asked);

            var result = await harness.SaveAsync();

            var again = harness.Trace.IndexOf("complete-again");
            Assert.Single(harness.Trace, step => step == "complete-again");
            Assert.Equal("load", harness.Trace[again - 1]);
            Assert.True(again > harness.Trace.IndexOf("repair-commit"));
            if (asked == LibrarySaveStatus.AppliedAwaitingRelease)
            {
                Assert.True(result.Success, result.Message);
                Assert.False(harness.Workspace.HasPendingReferenceRepairs);
                Assert.False(harness.Protocol.HasPendingSave);
            }
            else
            {
                Assert.False(result.Success);
                Assert.Contains("reference", result.Message, StringComparison.OrdinalIgnoreCase);
                // This store already installed the repair before revising its receipt. Rebasing can see that content;
                // an unknown receipt cannot consume it. Real-file tests cover a superseded repair that was not installed.
                Assert.Equal(asked == LibrarySaveStatus.CommitUnknown, harness.Workspace.HasPendingReferenceRepairs);
                Assert.Equal(asked == LibrarySaveStatus.CommitUnknown, harness.Protocol.HasPendingSave);
            }
        });
    }

    [Theory]
    [InlineData("ai cleanup", "adopted")]
    [InlineData("ai cleanup", "rolled back")]
    [InlineData("microphone", "adopted")]
    [InlineData("microphone", "rolled back")]
    public async Task Family7_the_save_stores_the_document_it_froze_when_a_tray_change_is_adopted_or_rolled_back_while_it_prepares(
        string setting, string tray)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            using var folder = new TempDatabaseFolder();
            using var database = folder.Open();
            var repository = new SettingsRepository(database);
            var ai = setting == "ai cleanup";

            // Stored: AI cleanup off, microphone Yeti. "Other" is what the tray and the Save each put against it.
            var stored = new AppSettings { EnableAiCleanup = false };
            Yeti.ApplyTo(stored);
            repository.Save(stored);
            var window = new EditingWindow(repository.Load());

            // The choice the Save captures, and the choice the tray asks for or gives up on, per setting.
            Action<AppSettings> storedChoice = ai ? s => s.EnableAiCleanup = false : Yeti.ApplyTo;
            Action<AppSettings> otherChoice = ai ? s => s.EnableAiCleanup = true : Usb.ApplyTo;
            Func<AppSettings, bool> isStoredChoice = ai ? s => !s.EnableAiCleanup : s => MicrophoneSelection.From(s) == Yeti;
            Func<AppSettings, bool> isOtherChoice = ai ? s => s.EnableAiCleanup : s => MicrophoneSelection.From(s) == Usb;
            Action<long> windowKeeps = ai ? revision => window.AdoptAiCleanup(false, revision) : revision => window.AdoptMicrophone(Yeti, revision);
            Action<long> windowTakes = ai ? revision => window.AdoptAiCleanup(true, revision) : revision => window.AdoptMicrophone(Usb, revision);
            Func<long> windowIntent = ai ? () => window.AiCleanup.NewestRevision : () => window.Microphone.NewestRevision;
            ExternalIntents IntentsOf() => ai ? new ExternalIntents(windowIntent(), 0) : new ExternalIntents(0, windowIntent());

            long saveIntent;
            long trayRevision;
            if (tray == "adopted")
            {
                // The user chose the other value in the window, and the Save captures it with that intent.
                otherChoice(window.Settings);
                if (ai)
                {
                    window.AiCleanup.UserChanged();
                }
                else
                {
                    window.Microphone.UserChanged();
                }

                saveIntent = windowIntent();
                trayRevision = ExternalSwitchSync.NextRevision();
            }
            else
            {
                // The tray chose the other value, newer than nothing the window held: the window took it at once, the
                // tray's own write still waiting, and the Save captures it with that intent.
                trayRevision = ExternalSwitchSync.NextRevision();
                windowTakes(trayRevision);
                saveIntent = windowIntent();
                Assert.Equal(trayRevision, saveIntent);
            }

            using var harness = ProtocolHarness.Create(owner);
            harness.EditPack();
            harness.Hold("prepare");
            var candidate = SaveCandidate.Freeze(window.Settings, IntentsOf());
            Assert.True(isOtherChoice(candidate.Document));
            var request = harness.Request() with
            {
                CommitSettings = _ =>
                {
                    harness.Trace.Add("commit");
                    repository.SaveBundle(candidate.Document, null, null, candidate.Intents, null);
                },
                OnSettingsCommitted = () => candidate.Settle(window.Settings, window.AiCleanup, window.Microphone),
            };
            var save = harness.Protocol.SaveAsync(request);
            await harness.WaitForHoldAsync("prepare");

            // While the journal is prepared, the tray's word reaches the window: a newer tray choice, taken at once before its
            // own write has stored it (adopted), or the tray's write failing and the window put back to what dictation is
            // using, under the revision it asked with (rolled back).
            windowKeeps(trayRevision);
            Assert.True(isStoredChoice(window.Settings));

            harness.Release("prepare");
            var result = await save;

            Assert.True(result.Success);

            // The Save stored the document it froze, whatever the editing document held when its commit ran: the choice it
            // captured, not the newer one the tray had not stored, and not the one put back.
            Assert.True(isOtherChoice(repository.Load()));
            Assert.True(isOtherChoice(candidate.Document));
            Assert.Equal(1, harness.Trace.Count(step => step == "commit"));

            if (tray == "adopted")
            {
                // The newer intent is still the window's: its document keeps the tray's choice until the tray's own write
                // settles it, and that write, newer than what the Save carried, is not superseded.
                Assert.True(isStoredChoice(window.Settings));
                Assert.Equal(trayRevision, windowIntent());
                if (ai)
                {
                    repository.Update(storedChoice, ExternalSetting.AiCleanup, trayRevision, out var superseded);
                    Assert.False(superseded);
                }
                else
                {
                    repository.Update(storedChoice, ExternalSetting.Microphone, trayRevision, out var superseded);
                    Assert.False(superseded);
                }

                Assert.True(isStoredChoice(repository.Load()));
            }
            else
            {
                // Nothing newer is held: the editing document takes what was stored, which is the choice the Save captured.
                Assert.True(isOtherChoice(window.Settings));
                Assert.Equal(0, windowIntent());
            }
        });
    }

    // The Family 5 arrangement: a word pack and its copy saved together, the store keeping the original under another id, so
    // the copy's reference needs a follow-up write after the Save.
    private static void ArrangeReferenceRepair(ProtocolHarness harness)
    {
        var original = harness.Workspace.CreateLibrary();
        harness.Workspace.AddTerm(original, new TermValues("ga", "general availability"));
        harness.Workspace.Duplicate(original);
        harness.Store.PrimaryOutcomes.Add(
            new DeciderFixture.StoreOutcome.SavedUnderNewId(original, original + "-7", [new TermValues("theirs", "Theirs")]));
    }

    // What an adoption leaves after it commits a generation of its own: the same libraries and choices, one generation on.
    private static LibraryCatalog AdoptedOnce(LibraryCatalog catalog) =>
        DeciderFixture.Catalog(
            catalog.Libraries,
            enabled: catalog.LocalState.EnabledIds,
            ai: catalog.LocalState.AiPermissions,
            generation: catalog.Generation + 1);

    [Fact]
    public void Editor_to_workspace_clearing_replacement_does_not_grant_removal_intent()
    {
        var workspace = DeciderFixture.Workspace(DeciderFixture.Standard());
        var rowId = DeciderFixture.RowIdOf(workspace, DeciderFixture.GitHubId, "get hub");

        var result = workspace.EditTerm(DeciderFixture.GitHubId, rowId, new TermValues("get hub", string.Empty), removalIntent: false);
        var capture = workspace.CaptureChangeSet();

        Assert.True(result.Applied);
        Assert.Contains(capture.Issues, issue => issue.Kind == LibraryValidationKind.EmptyWrittenWithoutIntent);
    }

    [Fact]
    public void Window_word_pack_editor_grants_removal_intent_only_from_an_explicit_action()
    {
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "Scribe.slnx")))
        {
            root = Directory.GetParent(root)!.FullName;
        }

        var settings = Path.Combine(root, "src", "Scribe.App", "Settings");
        var window = string.Join("\n", Directory.EnumerateFiles(settings, "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));
        var wordPackStart = window.IndexOf("private void LibraryTermRow_PropertyChanged", StringComparison.Ordinal);
        var wordPackEnd = window.IndexOf("private void LibraryTermsGrid_Sorting", wordPackStart, StringComparison.Ordinal);
        var wordPackEditor = window[wordPackStart..wordPackEnd];

        Assert.Contains("removalIntent: false", wordPackEditor, StringComparison.Ordinal);
        Assert.DoesNotContain("removalIntent: true", wordPackEditor, StringComparison.Ordinal);
        Assert.DoesNotContain("string.IsNullOrEmpty(row.Replacement)", wordPackEditor, StringComparison.Ordinal);
    }

    private sealed class ProtocolHarness : IDisposable
    {
        private readonly int _owner;
        private readonly Dictionary<string, Hold> _holds = new(StringComparer.OrdinalIgnoreCase);

        private ProtocolHarness(int owner, SettingsApplicationDebt? debt)
        {
            _owner = owner;
            Catalog = DeciderFixture.Standard();
            Workspace = DeciderFixture.Workspace(Catalog);
            Store = new TraceStore(this, Catalog);
            Protocol = new WordPackSaveProtocol(Store, debt);
            PumpContext.DisposeAtExit(this);
        }

        public LibraryCatalog Catalog { get; set; }
        public LibraryWorkspace Workspace { get; set; }
        public TraceStore Store { get; }
        public WordPackSaveProtocol Protocol { get; }
        public List<string> Trace { get; } = [];
        public List<string> CapturedDrafts { get; } = [];
        public List<(string Kind, LibrarySavePayload? Payload, string Draft)> StoredInputs { get; } = [];
        public List<string> ValidationErrors { get; } = [];
        public bool ThrowOnCommit { get; set; }
        public bool ThrowRepairCommit { get; set; }
        public bool ThrowOnRepairPrepare { get; set; }
        public bool ThrowOnSettingsCommitted { get; set; }

        // Applying the stored settings fails the way production's does when the store cannot be read: the call throws
        // before it returns an answer (ThrowOnApply), or the answer it returns faults (FaultOnApply).
        public bool ThrowOnApply { get; set; }
        public bool FaultOnApply { get; set; }
        public StoredSettingsReapplied Reapplied { get; set; } = StoredSettingsReapplied.StoredSettings;
        public VocabularyRefreshOutcome RefreshOutcome { get; set; } = VocabularyRefreshOutcome.Applied;
        public bool UseProductionSignature { get; set; }
        public string Draft { get; private set; } = "draft:initial";

        public static ProtocolHarness Create(int owner, SettingsApplicationDebt? debt = null) => new(owner, debt);

        public static Task RunOnOwnerAsync(Func<int, Task> action) => PumpContext.RunAsync(action);

        public void EditPack(bool backToOriginal = false)
        {
            AssertOwner();
            Workspace.SetEnabled(DeciderFixture.AzureId, !backToOriginal);
            Draft = backToOriginal ? "draft:initial" : "draft:pack";
        }

        public Task<WordPackSaveProtocolResult> SaveAsync() => Protocol.SaveAsync(Request());

        public WordPackSaveProtocolRequest Request() => new(
            Workspace,
            payload =>
            {
                AssertOwner();
                Trace.Add("commit");
                StoredInputs.Add(("payload", payload, Draft));
                if (ThrowOnCommit)
                {
                    throw new InvalidOperationException("transaction");
                }
            },
            () =>
            {
                AssertOwner();
                Trace.Add("apply");
                if (ThrowOnApply)
                {
                    throw new IOException("injected: the stored settings cannot be read");
                }

                return new StoredSettingsReapplyResult(Reapplied, AppliedAsync());
            },
            () =>
            {
                AssertOwner();
                var draft = ReadDraft();
                CapturedDrafts.Add(draft);
                return draft;
            },
            () =>
            {
                AssertOwner();
                return ReadDraft();
            },
            () => ValidationErrors,
            () =>
            {
                Trace.Add("settings-committed");
                if (ThrowOnSettingsCommitted)
                {
                    throw new InvalidOperationException("bookkeeping");
                }
            },
            catalog => Trace.Add($"wordpacks-changed:{catalog.Generation}"));

        // The answer of the vocabulary generation an application asked for: held at "publication", then applied, or faulted.
        private async Task<VocabularyRefresh> AppliedAsync()
        {
            await Gate("publication");
            if (FaultOnApply)
            {
                throw new IOException("injected: the vocabulary answer faulted");
            }

            return new VocabularyRefresh(RefreshOutcome, VocabularyGeneration.Empty);
        }

        public void EditDraft(string edit)
        {
            AssertOwner();
            switch (edit)
            {
                case "pack":
                    Workspace.SetEnabled(DeciderFixture.GitHubId, false);
                    Draft = "draft:pack-edit";
                    break;
                case "revert":
                case "first-load":
                    Draft = "draft:pack";
                    break;
                default:
                    Draft = "draft:" + edit;
                    break;
            }
        }

        public void Hold(string name) => _holds[name] = new Hold();

        // Fails the test, naming the hold, when the work never reaches it. Going on after a timeout, as a 5 s wait whose
        // result was ignored once did, would judge a save that had not got there yet, as if it had.
        public async Task WaitForHoldAsync(string name)
        {
            if (_holds.TryGetValue(name, out var hold))
            {
                Assert.True(await HangGuard.Completes(hold.Hit.Task, HoldBound), $"The work never reached its \"{name}\" hold.");
            }
        }

        public void Release(string name)
        {
            if (_holds.TryGetValue(name, out var hold))
            {
                hold.Release.TrySetResult();
            }
        }

        // Opens every hold, so work parked at one, or the pool thread the catalog load blocks there, never outlives its test.
        // Called by a test that declares the harness with using, on its way out, and by the pump the harness was made under,
        // when it ends, which covers a test the pump gave up on while it was still pending (stream TR round 7c). Opening a
        // hold twice does nothing.
        public void Dispose()
        {
            foreach (var hold in _holds.Values)
            {
                hold.Release.TrySetResult();
            }
        }

        public async Task Gate(string name)
        {
            if (_holds.TryGetValue(name, out var hold))
            {
                hold.Hit.TrySetResult();
                await hold.Release.Task;
            }
        }

        public void AssertOwner() => Assert.Equal(_owner, Environment.CurrentManagedThreadId);

        public void AssertWorker() => Assert.NotEqual(_owner, Environment.CurrentManagedThreadId);

        private string ReadDraft()
        {
            if (!UseProductionSignature)
            {
                return Draft;
            }

            var snapshot = new DraftSnapshot();
            WordPackDraftSignature.Write(snapshot, Workspace);
            return snapshot.Hash();
        }
    }

    private sealed class TraceStore : IWordPackSaveProtocolStore
    {
        private readonly ProtocolHarness _harness;

        // What each preparation was made for, found by the preparation itself as the library service finds a Save: the
        // first completion installs it, and a second completion of the same preparation is what a settlement asks, answered
        // from what was recorded for that Save, never by counting completions.
        private readonly Dictionary<PreparedLibrarySave, LibraryChangeSet> _changesOf = new();

        public TraceStore(ProtocolHarness harness, LibraryCatalog current)
        {
            _harness = harness;
            Current = current;
        }

        public LibrarySaveStatus Status { get; set; } = LibrarySaveStatus.Applied;
        public LibrarySaveStatus RepairStatus { get; set; } = LibrarySaveStatus.Applied;

        // What the library service has recorded as the outcome of a Save it could not settle at first, whatever the stored
        // generation has become since; unset, a second completion is read by the stored generation, as for a Save nothing
        // settled.
        public LibrarySaveStatus? SettledStatus { get; set; }

        // What the next completions of an already completed preparation answer, in order, before SettledStatus: a revisable
        // receipt (AppliedAwaitingRelease) that recovery revises between two reads.
        public Queue<LibrarySaveStatus> SettledSequence { get; } = new();
        public LibraryPrepareStatus PrepareStatus { get; set; } = LibraryPrepareStatus.Prepared;
        public LibraryPrepareStatus RepairPrepareStatus { get; set; } = LibraryPrepareStatus.Prepared;
        public List<string> OutsideEditIds { get; } = [];
        public bool ThrowOnLoad { get; set; }

        // The read a settlement makes of where the Save stands fails (the second completion of its preparation throws).
        public bool ThrowOnSettle { get; set; }
        public LibraryCatalog Current { get; set; }
        public Dictionary<string, RecentlyDeletedContent> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DeciderFixture.StoreOutcome> PrimaryOutcomes { get; } = [];
        public List<LibraryChangeSet> Prepared { get; } = [];

        // The first completion of each preparation, and every later completion of one (what the protocol's default
        // settlement is: SettleAsync is not overridden here, so a settlement reaches this store as a second completion).
        public List<PreparedLibrarySave> Completed { get; } = [];
        public List<PreparedLibrarySave> Settlements { get; } = [];
        public List<LibrarySavePayload> RepairPayloads { get; } = [];

        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) => Task.Run(async () =>
        {
            _harness.AssertWorker();
            _harness.Trace.Add("prepare");
            await _harness.Gate("prepare");
            var repair = changes.Writes.Count == 1 && !changes.LocalStateChanged && changes.Deletions.Count == 0 && changes.RecentlyDeletedActions.Count == 0;
            if (repair && _harness.ThrowOnRepairPrepare)
            {
                throw new InvalidOperationException("repair prepare");
            }

            var status = repair ? RepairPrepareStatus : PrepareStatus;
            if (status != LibraryPrepareStatus.Prepared)
            {
                return new LibraryPrepareResult(status, null, OutsideEditIds, LibraryIoFailure.None);
            }

            Prepared.Add(changes);
            var payload = new LibrarySavePayload(changes.BaseGeneration, changes.BaseGeneration + 1, changes.LocalState.EnabledIds.ToList(), []);
            var save = new PreparedLibrarySave(changes.DraftRevision, payload, new LibraryChangeCounts(0, 0, 0, 0, 0));
            _changesOf[save] = changes;
            return new LibraryPrepareResult(status, save, []);
        });

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared) => Task.Run(async () =>
        {
            _harness.AssertWorker();
            if (Completed.Contains(prepared))
            {
                _harness.Trace.Add("complete-again");
                await _harness.Gate("complete-again");
                if (ThrowOnSettle)
                {
                    throw new IOException("injected: the settlement could not read where the Save stands");
                }

                Settlements.Add(prepared);

                // A store that cannot read its generation has not settled the Save. One that can answers by what it
                // recorded for that very Save (SettledStatus), else by the generation stored now, as the library service
                // does for a Save recovery never settled.
                if (ThrowOnLoad)
                {
                    return new LibrarySaveOutcome(LibrarySaveStatus.CommitUnknown, prepared.BaseGeneration, 0, [], LibraryIoFailure.None);
                }

                var settled = SettledSequence.Count > 0
                    ? SettledSequence.Dequeue()
                    : SettledStatus
                        ?? (Current.Generation == prepared.Generation
                            ? LibrarySaveStatus.Applied
                            : Current.Generation == prepared.BaseGeneration ? LibrarySaveStatus.NotCommitted : LibrarySaveStatus.Superseded);
                return new LibrarySaveOutcome(settled, Current.Generation, 0, [], LibraryIoFailure.None);
            }

            _harness.Trace.Add("complete");
            await _harness.Gate("complete");
            Completed.Add(prepared);
            var changes = _changesOf[prepared];
            var repair = changes.Writes.Count == 1 && !changes.LocalStateChanged && changes.Deletions.Count == 0 && changes.RecentlyDeletedActions.Count == 0;
            var status = repair ? RepairStatus : Status;
            if (status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
            {
                Current = DeciderFixture.Apply(Current, changes, Deleted, outcomes: repair ? [] : PrimaryOutcomes);
            }

            return new LibrarySaveOutcome(status, status is LibrarySaveStatus.CommitUnknown ? changes.BaseGeneration : Current.Generation, 0, [], LibraryIoFailure.None);
        });

        public Task<LibraryCatalog> LoadCatalogAsync() => Task.Run(() =>
        {
            _harness.AssertWorker();
            _harness.Trace.Add("load");
            _harness.Gate("load").GetAwaiter().GetResult();
            if (ThrowOnLoad)
            {
                throw new InvalidOperationException("load");
            }

            return Current;
        });

        public void CommitRepair(LibrarySavePayload payload)
        {
            _harness.AssertOwner();
            _harness.Trace.Add("repair-commit");
            RepairPayloads.Add(payload);
            if (_harness.ThrowRepairCommit)
            {
                throw new InvalidOperationException("repair");
            }
        }
    }

    // A store that only completes: it overrides nothing the interface defaults, so a settlement reaches it as a completion.
    private sealed class CompletionOnlyStore(List<PreparedLibrarySave> completions) : IWordPackSaveProtocolStore
    {
        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) => throw new NotSupportedException();

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared)
        {
            completions.Add(prepared);
            return Task.FromResult(new LibrarySaveOutcome(LibrarySaveStatus.Applied, prepared.Generation, 0, [], LibraryIoFailure.None));
        }

        public Task<LibraryCatalog> LoadCatalogAsync() => throw new NotSupportedException();

        public void CommitRepair(LibrarySavePayload payload) => throw new NotSupportedException();
    }

    // The settings window's editing document and the records of intent it keeps for what the tray can change, with the two
    // ways the tray's word reaches it (SettingsWindow.AdoptExternalAiCleanup and AdoptExternalMicrophone): the value goes
    // into the document at once, before the tray's own write has stored it.
    private sealed class EditingWindow(AppSettings settings)
    {
        public AppSettings Settings { get; } = settings;
        public ExternalSwitchSync AiCleanup { get; } = new();
        public ExternalChoiceSync<MicrophoneSelection> Microphone { get; } = new();

        public void AdoptAiCleanup(bool enabled, long revision)
        {
            if (AiCleanup.TryAdopt(enabled, revision, canShowNow: true, out _))
            {
                Settings.EnableAiCleanup = enabled;
            }
        }

        public void AdoptMicrophone(MicrophoneSelection selection, long revision)
        {
            if (Microphone.TryAdopt(selection, revision, canShowNow: true, out _))
            {
                selection.ApplyTo(Settings);
            }
        }
    }

    private sealed class Hold
    {
        public TaskCompletionSource Hit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly ConcurrentQueue<IDisposable> _atExit = new();

        // Has what a test sets up on the owner thread disposed when the pump ends, however it ends. The test's own using
        // cannot cover the pump giving up on it: its task is still pending then, and never reaches its way out.
        public static void DisposeAtExit(IDisposable owned)
        {
            if (Current is PumpContext pump)
            {
                pump._atExit.Enqueue(owned);
            }
        }

        // Runs the owner's posted work until the test's task completes. A test whose owner thread has run nothing for
        // PumpIdleBound while the test is still waiting fails, instead of pumping for ever and stopping the run. On every way
        // out, that one included, the pump itself lets go of what the test left held, closes its queue, so nothing posted
        // later waits in it for a pump that has stopped, and gives the owner thread its own context back (stream TR round 7c).
        public static async Task RunAsync(Func<int, Task> action)
        {
            var previous = Current;
            var context = new PumpContext();
            SetSynchronizationContext(context);
            Task task;
            try
            {
                var owner = Environment.CurrentManagedThreadId;
                task = action(owner);
                _ = task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
                var lastRan = Stopwatch.GetTimestamp();
                while (!task.IsCompleted || context._queue.Count > 0)
                {
                    if (context._queue.TryTake(out var work, 50))
                    {
                        SetSynchronizationContext(context);
                        work.Callback(work.State);
                        lastRan = Stopwatch.GetTimestamp();
                    }
                    else if (!task.IsCompleted && Stopwatch.GetElapsedTime(lastRan) > PumpIdleBound)
                    {
                        Assert.Fail(
                            $"The test never finished: nothing ran on its owner thread for {PumpIdleBound.TotalSeconds:0} s " +
                            "while it was still waiting.");
                    }
                }
            }
            finally
            {
                while (context._atExit.TryDequeue(out var owned))
                {
                    owned.Dispose();
                }

                context._queue.CompleteAdding();
                SetSynchronizationContext(previous);
            }

            await task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The pump has ended with its test, so this work has nowhere to run. Post is called from whatever thread
                // completed the awaited task, often the pool's, where a throw would end the test host (stream TR round 7).
            }
        }
    }
}
