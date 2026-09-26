using System.Collections.Concurrent;
using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackSaveProtocolTests
{
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
            var harness = ProtocolHarness.Create(owner);
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
            var harness = ProtocolHarness.Create(owner);
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
            var harness = ProtocolHarness.Create(owner);
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
            var harness = ProtocolHarness.Create(owner);
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
        });
    }

    [Theory]
    [MemberData(nameof(RepairCases))]
    public async Task Family5_repair_continuation_writes_only_reference_repairs(string repairCase)
    {
        await ProtocolHarness.RunOnOwnerAsync(async owner =>
        {
            var harness = ProtocolHarness.Create(owner);
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

    private sealed class ProtocolHarness
    {
        private readonly int _owner;
        private readonly Dictionary<string, Hold> _holds = new(StringComparer.OrdinalIgnoreCase);

        private ProtocolHarness(int owner)
        {
            _owner = owner;
            Catalog = DeciderFixture.Standard();
            Workspace = DeciderFixture.Workspace(Catalog);
            Store = new TraceStore(this, Catalog);
            Protocol = new WordPackSaveProtocol(Store);
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
        public bool UseProductionSignature { get; set; }
        public string Draft { get; private set; } = "draft:initial";

        public static ProtocolHarness Create(int owner) => new(owner);

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
            async () =>
            {
                AssertOwner();
                Trace.Add("apply");
                await Gate("publication");
                return new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty);
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
            () => Trace.Add("settings-committed"),
            catalog => Trace.Add($"wordpacks-changed:{catalog.Generation}"));

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

        public async Task WaitForHoldAsync(string name)
        {
            if (_holds.TryGetValue(name, out var hold))
            {
                await Task.WhenAny(hold.Hit.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }

        public void Release(string name)
        {
            if (_holds.TryGetValue(name, out var hold))
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
        public TraceStore(ProtocolHarness harness, LibraryCatalog current)
        {
            _harness = harness;
            Current = current;
        }

        public LibrarySaveStatus Status { get; set; } = LibrarySaveStatus.Applied;
        public LibrarySaveStatus RepairStatus { get; set; } = LibrarySaveStatus.Applied;
        public LibraryPrepareStatus PrepareStatus { get; set; } = LibraryPrepareStatus.Prepared;
        public LibraryPrepareStatus RepairPrepareStatus { get; set; } = LibraryPrepareStatus.Prepared;
        public bool ThrowOnLoad { get; set; }
        public LibraryCatalog Current { get; set; }
        public Dictionary<string, RecentlyDeletedContent> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DeciderFixture.StoreOutcome> PrimaryOutcomes { get; } = [];
        public List<LibraryChangeSet> Prepared { get; } = [];
        public List<PreparedLibrarySave> Completed { get; } = [];
        public List<LibrarySavePayload> RepairPayloads { get; } = [];

        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) => Task.Run(async () =>
        {
            _harness.AssertWorker();
            _harness.Trace.Add("prepare");
            await _harness.Gate("prepare");
            var repair = changes.Writes.Count == 1 && !changes.LocalStateChanged && changes.Deletions.Count == 0 && changes.RecentlyDeletedActions.Count == 0;
            var status = repair ? RepairPrepareStatus : PrepareStatus;
            if (status != LibraryPrepareStatus.Prepared)
            {
                return new LibraryPrepareResult(status, null, [], LibraryIoFailure.None);
            }

            Prepared.Add(changes);
            var payload = new LibrarySavePayload(changes.BaseGeneration, changes.BaseGeneration + 1, changes.LocalState.EnabledIds.ToList(), []);
            return new LibraryPrepareResult(status, new PreparedLibrarySave(changes.DraftRevision, payload, new LibraryChangeCounts(0, 0, 0, 0, 0)), []);
        });

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared) => Task.Run(async () =>
        {
            _harness.AssertWorker();
            _harness.Trace.Add("complete");
            await _harness.Gate("complete");
            Completed.Add(prepared);
            var changes = Prepared[Completed.Count - 1];
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

    private sealed class Hold
    {
        public TaskCompletionSource Hit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public static async Task RunAsync(Func<int, Task> action)
        {
            var previous = Current;
            var context = new PumpContext();
            SetSynchronizationContext(context);
            var owner = Environment.CurrentManagedThreadId;
            var task = action(owner);
            _ = task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            while (!task.IsCompleted || context._queue.Count > 0)
            {
                if (context._queue.TryTake(out var work, 50))
                {
                    SetSynchronizationContext(context);
                    work.Callback(work.State);
                }
            }

            SetSynchronizationContext(previous);
            await task;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.Add((d, state));
            }
        }
    }
}
