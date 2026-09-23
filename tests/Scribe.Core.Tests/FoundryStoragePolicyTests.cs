using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// The maintainer's Foundry Local storage policy, decided without the native runtime. Each rule is
/// pinned by name, and an exhaustive sweep asserts the safety invariants for every combination of
/// previous selection, next selection and how much of the runtime this process holds.
/// </summary>
public sealed class FoundryStoragePolicyTests
{
    private static FoundrySelection Foundry(bool enabled = true, string? alias = "qwen3-1.7b") =>
        new(CleanupProvider.FoundryLocal, enabled, alias);

    private static FoundrySelection Other(CleanupProvider provider = CleanupProvider.AzureFoundry, bool enabled = true) =>
        new(provider, enabled, "qwen3-1.7b");

    [Theory]
    [InlineData(CleanupProvider.AzureFoundry, true)]
    [InlineData(CleanupProvider.AzureFoundry, false)]
    [InlineData(CleanupProvider.OpenAiCompatible, true)]
    [InlineData(CleanupProvider.GitHubCopilot, false)]
    public void Startup_with_another_provider_reclaims_leftover_files_directly(CleanupProvider provider, bool enabled)
    {
        // Rule 5: this is how an existing install's multi-GB execution-provider download goes away.
        // No manager exists yet, so nothing is loaded and the files can simply be deleted.
        var plan = FoundryStoragePolicy.OnSettingsApplied(null, Other(provider, enabled), FoundryRuntimePresence.None);

        Assert.Equal(FoundryStorageIntent.ReclaimEverything, plan.Intent);
        Assert.True(plan.DeleteModelFiles);
        Assert.Equal(FoundryRuntimeFiles.DeleteNow, plan.RuntimeFiles);
        Assert.Equal(FoundryKeepOnlySelected.Clear, plan.KeepOnlySelected);
        Assert.False(plan.UnloadAllModels);
        Assert.False(plan.RemoveAllCachedModels);
        Assert.False(plan.StopWebService);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_with_foundry_local_keeps_the_selected_model_and_arms_the_session(bool enabled)
    {
        // Nothing is deleted by applying the startup configuration itself. Other aliases' variants go
        // once the selected model is in use (armed for this session), and with cleanup off the
        // runtime downloads are reviewed against the cache when the work runs.
        foreach (var presence in Enum.GetValues<FoundryRuntimePresence>())
        {
            var plan = FoundryStoragePolicy.OnSettingsApplied(null, Foundry(enabled), presence);

            Assert.Equal(FoundryKeepOnlySelected.ArmThisSession, plan.KeepOnlySelected);
            Assert.Equal(enabled ? FoundryStorageIntent.None : FoundryStorageIntent.ReviewUnusedRuntime, plan.Intent);
            Assert.False(plan.DeleteModelFiles);
            Assert.False(plan.RemoveAllCachedModels);
            Assert.False(plan.UnloadAllModels);
            Assert.False(plan.StopWebService);
            Assert.Equal(FoundryRuntimeFiles.Keep, plan.RuntimeFiles);
        }
    }

    [Fact]
    public void The_startup_review_deletes_only_the_runtime_and_only_with_no_model_and_no_runtime_here()
    {
        // "Only if they swap to it and choose to load the model should it be kept." No model cached
        // means the runtime came from browsing the list; nothing is loaded at startup.
        var unused = FoundryStoragePolicy.ForIntent(
            FoundryStorageIntent.ReviewUnusedRuntime, FoundryRuntimePresence.None, FoundryModelCache.Empty);

        Assert.Equal(FoundryRuntimeFiles.DeleteNow, unused.RuntimeFiles);
        Assert.Equal(FoundryKeepOnlySelected.Clear, unused.KeepOnlySelected);
        Assert.False(unused.DeleteModelFiles);
        Assert.False(unused.RemoveAllCachedModels);
        Assert.False(unused.UnloadAllModels);
        Assert.False(unused.StopWebService);

        foreach (var presence in Enum.GetValues<FoundryRuntimePresence>())
        {
            foreach (var cache in Enum.GetValues<FoundryModelCache>())
            {
                var plan = FoundryStoragePolicy.ForIntent(FoundryStorageIntent.ReviewUnusedRuntime, presence, cache);
                var deletes = plan.RuntimeFiles == FoundryRuntimeFiles.DeleteNow;

                // A cached model keeps its runtime; an unreadable cache is treated as holding one; a
                // runtime created in this process may be loaded.
                Assert.Equal(presence == FoundryRuntimePresence.None && cache == FoundryModelCache.Empty, deletes);
                Assert.False(plan.DeleteModelFiles);
                Assert.False(plan.RemoveAllCachedModels);
                Assert.Equal(FoundryStorageIntent.ReviewUnusedRuntime, plan.Intent);
            }
        }
    }

    [Fact]
    public void Switching_away_with_a_live_catalog_uses_the_sdk_and_defers_the_loaded_runtime()
    {
        // Rule 2. Models go through the SDK's own removal; execution-provider libraries registered in
        // this process are loaded native code Windows will not delete, so they wait for a restart.
        var plan = FoundryStoragePolicy.OnSettingsApplied(Foundry(), Other(), FoundryRuntimePresence.CatalogLive);

        Assert.Equal(FoundryStorageIntent.ReclaimEverything, plan.Intent);
        Assert.True(plan.UnloadAllModels);
        Assert.True(plan.StopWebService);
        Assert.True(plan.RemoveAllCachedModels);
        Assert.True(plan.ReleaseRuntimeReferences);
        Assert.False(plan.DeleteModelFiles);
        Assert.Equal(FoundryRuntimeFiles.DeferToNextStartup, plan.RuntimeFiles);
        Assert.Equal(FoundryKeepOnlySelected.Clear, plan.KeepOnlySelected);
    }

    [Fact]
    public void Switching_away_with_only_a_manager_defers_everything_rather_than_downloading_to_delete()
    {
        // The SDK's removal needs the catalog, and reading the catalog first registers (downloads)
        // execution providers: reclaiming now would do the opposite of reclaiming.
        var plan = FoundryStoragePolicy.OnSettingsApplied(Foundry(), Other(), FoundryRuntimePresence.ManagerOnly);

        Assert.Equal(FoundryStorageIntent.ReclaimEverything, plan.Intent);
        Assert.Equal(FoundryRuntimeFiles.DeferToNextStartup, plan.RuntimeFiles);
        Assert.False(plan.DeleteModelFiles);
        Assert.False(plan.RemoveAllCachedModels);
        Assert.False(plan.UnloadAllModels);
        Assert.Equal(FoundryKeepOnlySelected.Unchanged, plan.KeepOnlySelected);
    }

    [Fact]
    public void Switching_away_before_any_runtime_existed_deletes_the_files_now()
    {
        var plan = FoundryStoragePolicy.OnSettingsApplied(Foundry(), Other(), FoundryRuntimePresence.None);

        Assert.True(plan.DeleteModelFiles);
        Assert.Equal(FoundryRuntimeFiles.DeleteNow, plan.RuntimeFiles);
    }

    [Fact]
    public void Staying_on_another_provider_never_reclaims_mid_session()
    {
        // Every settings save lands here: the tray AI toggle, or the usage page adding a dictionary
        // term. Reclaiming on it deleted a model the user had just loaded by hand. What they loaded or
        // listed while another provider stayed saved is reclaimed at the next start instead.
        foreach (var presence in Enum.GetValues<FoundryRuntimePresence>())
        {
            Assert.True(FoundryStoragePolicy.OnSettingsApplied(Other(), Other(CleanupProvider.OpenAiCompatible), presence).IsNothing);
            Assert.True(FoundryStoragePolicy.OnSettingsApplied(Other(enabled: true), Other(enabled: false), presence).IsNothing);
            Assert.True(FoundryStoragePolicy.OnSettingsApplied(Other(), Other(), presence).IsNothing);
        }

        // The next start is where it goes.
        var nextStart = FoundryStoragePolicy.OnSettingsApplied(null, Other(), FoundryRuntimePresence.None);
        Assert.Equal(FoundryStorageIntent.ReclaimEverything, nextStart.Intent);
    }

    [Fact]
    public void Switching_to_foundry_local_never_deletes_anything()
    {
        foreach (var presence in Enum.GetValues<FoundryRuntimePresence>())
        {
            Assert.True(FoundryStoragePolicy.OnSettingsApplied(Other(), Foundry(), presence).IsNothing);
        }
    }

    [Fact]
    public void Switching_cleanup_off_while_foundry_stays_selected_only_unloads()
    {
        // Rule 4: free the memory, keep the files, so switching it back on is quick.
        var plan = FoundryStoragePolicy.OnSettingsApplied(Foundry(true), Foundry(false), FoundryRuntimePresence.CatalogLive);

        Assert.Equal(FoundryStorageIntent.UnloadOnly, plan.Intent);
        Assert.True(plan.UnloadAllModels);
        Assert.False(plan.RemoveAllCachedModels);
        Assert.False(plan.DeleteModelFiles);
        Assert.False(plan.StopWebService);
        Assert.Equal(FoundryRuntimeFiles.Keep, plan.RuntimeFiles);
        Assert.Equal(FoundryKeepOnlySelected.Unchanged, plan.KeepOnlySelected);
    }

    [Fact]
    public void Switching_cleanup_off_with_no_runtime_has_nothing_to_unload()
    {
        Assert.True(FoundryStoragePolicy.OnSettingsApplied(Foundry(true), Foundry(false), FoundryRuntimePresence.None).IsNothing);
    }

    [Fact]
    public void Switching_cleanup_back_on_does_nothing()
    {
        Assert.True(FoundryStoragePolicy.OnSettingsApplied(Foundry(false), Foundry(true), FoundryRuntimePresence.CatalogLive).IsNothing);
    }

    [Fact]
    public void Switching_models_marks_the_others_for_removal_once_the_new_one_is_in_use()
    {
        // Rule 3. The deletion itself waits until the new model is actually serving; the plan only
        // records the intent, persistently, so a restart in between still finishes it.
        var plan = FoundryStoragePolicy.OnSettingsApplied(
            Foundry(alias: "qwen3-1.7b"), Foundry(alias: "phi-4"), FoundryRuntimePresence.CatalogLive);

        Assert.Equal(FoundryKeepOnlySelected.Set, plan.KeepOnlySelected);
        Assert.Equal(FoundryStorageIntent.None, plan.Intent);
        Assert.False(plan.UnloadAllModels);
        Assert.False(plan.RemoveAllCachedModels);
    }

    [Fact]
    public void Switching_models_and_turning_cleanup_off_together_unloads_and_marks()
    {
        var plan = FoundryStoragePolicy.OnSettingsApplied(
            Foundry(true, "qwen3-1.7b"), Foundry(false, "phi-4"), FoundryRuntimePresence.CatalogLive);

        Assert.Equal(FoundryStorageIntent.UnloadOnly, plan.Intent);
        Assert.Equal(FoundryKeepOnlySelected.Set, plan.KeepOnlySelected);
    }

    [Theory]
    [InlineData("qwen3-1.7b", "QWEN3-1.7B")]
    [InlineData("qwen3-1.7b", "  qwen3-1.7b  ")]
    [InlineData(null, "qwen3-1.7b")]
    [InlineData("", "qwen3-1.7b")]
    public void The_same_model_spelled_differently_is_not_a_switch(string? before, string after)
    {
        // Blank means the default alias, which is what the service itself resolves it to.
        var plan = FoundryStoragePolicy.OnSettingsApplied(
            Foundry(alias: before), Foundry(alias: after), FoundryRuntimePresence.CatalogLive);

        Assert.True(plan.IsNothing);
    }

    [Fact]
    public void An_intent_is_rederived_for_the_runtime_that_exists_when_it_runs()
    {
        Assert.True(FoundryStoragePolicy.ForIntent(FoundryStorageIntent.None, FoundryRuntimePresence.CatalogLive).IsNothing);

        Assert.False(FoundryStoragePolicy.ForIntent(FoundryStorageIntent.UnloadOnly, FoundryRuntimePresence.ManagerOnly).UnloadAllModels);
        Assert.True(FoundryStoragePolicy.ForIntent(FoundryStorageIntent.UnloadOnly, FoundryRuntimePresence.CatalogLive).UnloadAllModels);

        Assert.Equal(
            FoundryRuntimeFiles.DeleteNow,
            FoundryStoragePolicy.ForIntent(FoundryStorageIntent.ReclaimEverything, FoundryRuntimePresence.None).RuntimeFiles);
        Assert.Equal(
            FoundryRuntimeFiles.DeferToNextStartup,
            FoundryStoragePolicy.ForIntent(FoundryStorageIntent.ReclaimEverything, FoundryRuntimePresence.CatalogLive).RuntimeFiles);
    }

    [Fact]
    public void Deferred_work_only_runs_while_the_configuration_it_was_scheduled_for_still_applies()
    {
        Assert.True(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.ReclaimEverything, Other(), false));
        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.ReclaimEverything, Foundry(), false));
        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.ReclaimEverything, null, false));

        Assert.True(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.UnloadOnly, Foundry(enabled: false), false));
        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.UnloadOnly, Foundry(enabled: true), false));
        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.UnloadOnly, Other(enabled: false), false));

        // The runtime review is for Foundry Local with cleanup off; turning cleanup on means the user
        // chose to load a model, and switching away is the full reclaim's business.
        Assert.True(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.ReviewUnusedRuntime, Foundry(enabled: false), false));
        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.ReviewUnusedRuntime, Foundry(enabled: true), false));
        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.ReviewUnusedRuntime, Other(enabled: false), false));

        Assert.False(FoundryStoragePolicy.StillApplies(FoundryStorageIntent.None, Other(), false));
    }

    [Fact]
    public void An_explicit_load_or_list_after_scheduling_drops_every_kind_of_deferred_work()
    {
        // Otherwise a queued reclaim could delete, or a queued unload undo, what the user just asked for.
        foreach (var intent in Enum.GetValues<FoundryStorageIntent>())
        {
            foreach (var current in Selections)
            {
                Assert.False(FoundryStoragePolicy.StillApplies(intent, current, explicitUseSinceScheduled: true));
            }
        }
    }

    public static TheoryData<int> AllCombinations()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Selections.Length * Selections.Length * 3 * 2; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static readonly FoundrySelection?[] Selections =
    [
        null,
        Foundry(true),
        Foundry(false),
        Other(CleanupProvider.AzureFoundry, true),
        Other(CleanupProvider.OpenAiCompatible, false),
        Other(CleanupProvider.GitHubCopilot, true),
    ];

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Every_combination_respects_the_safety_invariants(int index)
    {
        var aliasChanges = index % 2 == 1;
        var presence = (FoundryRuntimePresence)(index / 2 % 3);
        var nextIndex = index / 6 % Selections.Length;
        var previousIndex = index / (6 * Selections.Length);

        var previous = Selections[previousIndex];
        if (Selections[nextIndex] is not { } nextSelection)
        {
            return; // "next" is always a real configuration
        }

        var next = aliasChanges && nextSelection.IsFoundry ? nextSelection with { Alias = "phi-4" } : nextSelection;
        var plan = FoundryStoragePolicy.OnSettingsApplied(previous, next, presence);

        if (next.IsFoundry)
        {
            // Nothing a Foundry Local configuration uses is ever deleted by applying it.
            Assert.False(plan.DeleteModelFiles);
            Assert.False(plan.RemoveAllCachedModels);
            Assert.False(plan.StopWebService);
            Assert.NotEqual(FoundryRuntimeFiles.DeleteNow, plan.RuntimeFiles);
            Assert.NotEqual(FoundryRuntimeFiles.DeferToNextStartup, plan.RuntimeFiles);
            Assert.NotEqual(FoundryStorageIntent.ReclaimEverything, plan.Intent);
        }

        // Files are only deleted directly when no manager exists in this process.
        if (plan.DeleteModelFiles || plan.RuntimeFiles == FoundryRuntimeFiles.DeleteNow)
        {
            Assert.Equal(FoundryRuntimePresence.None, presence);
        }

        // Only a live catalog can unload or remove models through the SDK.
        if (plan.UnloadAllModels || plan.RemoveAllCachedModels || plan.StopWebService)
        {
            Assert.Equal(FoundryRuntimePresence.CatalogLive, presence);
        }

        // The "keep only the selected model" marker is only set by a genuine Foundry-to-Foundry switch.
        if (plan.KeepOnlySelected == FoundryKeepOnlySelected.Set)
        {
            Assert.True(previous is { IsFoundry: true } && next.IsFoundry && aliasChanges);
        }

        // The session arm is only the startup configuration, and only for Foundry Local.
        if (plan.KeepOnlySelected == FoundryKeepOnlySelected.ArmThisSession)
        {
            Assert.True(previous is null && next.IsFoundry);
        }

        // Nothing is reclaimed while another provider stays saved, whatever this process holds.
        if (previous is { IsFoundry: false } && !next.IsFoundry)
        {
            Assert.True(plan.IsNothing);
        }

        // An intent without an action would schedule work that does nothing, and vice versa.
        var hasAction = plan.UnloadAllModels || plan.RemoveAllCachedModels || plan.DeleteModelFiles ||
            plan.StopWebService || plan.RuntimeFiles != FoundryRuntimeFiles.Keep;
        if (plan.Intent == FoundryStorageIntent.None)
        {
            Assert.False(hasAction);
        }
    }

    [Fact]
    public void Keeping_only_the_selected_model_matches_variants_to_their_family_alias()
    {
        var cached = new[]
        {
            new FoundryModelIdentity("qwen3-1.7b-generic-gpu:2", "qwen3-1.7b"),
            new FoundryModelIdentity("qwen3-1.7b-generic-cpu:2", "qwen3-1.7b"),
            new FoundryModelIdentity("phi-4-generic-cpu:1", "phi-4"),
            new FoundryModelIdentity("mistral-nemo-12b-instruct-generic-gpu:1", "mistral-nemo-12b-instruct"),
        };

        var remove = FoundryStoragePolicy.SelectModelsToRemove(["qwen3-1.7b"], cached, []);

        Assert.Equal(["phi-4-generic-cpu:1", "mistral-nemo-12b-instruct-generic-gpu:1"], remove);
    }

    [Fact]
    public void An_exact_variant_the_user_or_a_demotion_pinned_is_kept()
    {
        var cached = new[]
        {
            new FoundryModelIdentity("qwen3-1.7b-generic-cpu:2", "qwen3-1.7b"),
            new FoundryModelIdentity("phi-4-generic-cpu:1", "phi-4"),
        };

        var remove = FoundryStoragePolicy.SelectModelsToRemove(
            ["some-other-family", "PHI-4-GENERIC-CPU:1"], cached, []);

        Assert.Equal(["qwen3-1.7b-generic-cpu:2"], remove);
    }

    [Fact]
    public void A_loaded_model_is_never_removed()
    {
        var cached = new[]
        {
            new FoundryModelIdentity("phi-4-generic-cpu:1", "phi-4"),
            new FoundryModelIdentity("qwen3-1.7b-generic-cpu:2", "qwen3-1.7b"),
        };
        var loaded = new[] { new FoundryModelIdentity("phi-4-generic-cpu:1", "phi-4") };

        var remove = FoundryStoragePolicy.SelectModelsToRemove(["mistral-nemo-12b-instruct"], cached, loaded);

        Assert.Equal(["qwen3-1.7b-generic-cpu:2"], remove);
    }

    [Fact]
    public void An_empty_selection_removes_nothing_rather_than_everything()
    {
        var cached = new[] { new FoundryModelIdentity("phi-4-generic-cpu:1", "phi-4") };

        Assert.Empty(FoundryStoragePolicy.SelectModelsToRemove([null, "", "   "], cached, []));
        Assert.Empty(FoundryStoragePolicy.SelectModelsToRemove([], cached, []));
    }

    [Fact]
    public void Duplicate_and_blank_cache_entries_are_ignored()
    {
        var cached = new[]
        {
            new FoundryModelIdentity("phi-4-generic-cpu:1", "phi-4"),
            new FoundryModelIdentity("PHI-4-GENERIC-CPU:1", "phi-4"),
            new FoundryModelIdentity("", "phi-4"),
        };

        Assert.Equal(["phi-4-generic-cpu:1"], FoundryStoragePolicy.SelectModelsToRemove(["qwen3-1.7b"], cached, []));
    }
}
