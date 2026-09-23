namespace Scribe.Core.Cleanup;

/// <summary>What the saved cleanup configuration selects, as far as Foundry Local storage is concerned.</summary>
/// <param name="Provider">The saved provider.</param>
/// <param name="Enabled">Whether AI cleanup is switched on.</param>
/// <param name="Alias">The saved Foundry Local model alias, as the user chose it (before any GPU demotion).</param>
internal readonly record struct FoundrySelection(CleanupProvider Provider, bool Enabled, string? Alias)
{
    public bool IsFoundry => Provider == CleanupProvider.FoundryLocal;

    public static FoundrySelection From(CleanupOptions options) =>
        new(options.Provider, options.Enabled, options.FoundryModelAlias);
}

/// <summary>How much of the Foundry Local runtime exists in this process.</summary>
internal enum FoundryRuntimePresence
{
    /// <summary>
    /// No manager was created in this process, so nothing Scribe downloaded is loaded here and its
    /// files can be deleted directly.
    /// </summary>
    None,

    /// <summary>
    /// A manager exists but its catalog was never read. Execution-provider libraries may be loaded,
    /// and the SDK's own removal API needs the catalog, which would register (and download) execution
    /// providers first. Nothing can be reclaimed now without doing the opposite of reclaiming.
    /// </summary>
    ManagerOnly,

    /// <summary>The catalog is live, so the SDK can unload and remove models itself.</summary>
    CatalogLive,
}

/// <summary>What happens to the execution-provider downloads in Scribe's Foundry Local directory.</summary>
internal enum FoundryRuntimeFiles
{
    Keep,

    /// <summary>Delete now: nothing in this process has loaded them.</summary>
    DeleteNow,

    /// <summary>
    /// They are, or may be, loaded native libraries in this process, which Windows will not delete.
    /// The next startup deletes them when the saved provider is still not Foundry Local.
    /// </summary>
    DeferToNextStartup,
}

/// <summary>What to do with the persisted "keep only the selected model" marker.</summary>
internal enum FoundryKeepOnlySelected
{
    Unchanged,

    /// <summary>The user switched Foundry Local models: once the new one is in use, delete the others.</summary>
    Set,

    /// <summary>Every cached model is being removed, so there is nothing left to narrow down.</summary>
    Clear,

    /// <summary>
    /// Foundry Local is the saved provider at startup: once the selected model is in use, delete the
    /// cached variants of every other alias. This also covers models left behind by switches made
    /// before this rule existed. Held for this session only; it is re-armed at every start.
    /// </summary>
    ArmThisSession,
}

/// <summary>Whether Scribe's Foundry Local model cache holds any model, as the janitor sees it on disk.</summary>
internal enum FoundryModelCache
{
    /// <summary>Could not tell: unreadable, too deep, or holding a link. Treated as holding models.</summary>
    Unknown,

    /// <summary>No model. The SDK's own catalog index beside the model folders does not count.</summary>
    Empty,

    HasModels,
}

/// <summary>Which kind of storage work a plan stands for, independent of how it is carried out.</summary>
internal enum FoundryStorageIntent
{
    None,

    /// <summary>Give back everything: the saved provider is not Foundry Local.</summary>
    ReclaimEverything,

    /// <summary>Free memory only: AI cleanup was switched off while Foundry Local stays selected.</summary>
    UnloadOnly,

    /// <summary>
    /// Startup with Foundry Local saved and AI cleanup off. When no model is cached, the
    /// execution-provider downloads came from browsing the model list, not from choosing to load a
    /// model, so they are deleted. Nothing is loaded at startup, which is what makes that safe.
    /// </summary>
    ReviewUnusedRuntime,
}

/// <summary>The storage work one settings change calls for. See <see cref="FoundryStoragePolicy"/>.</summary>
internal sealed record FoundryStoragePlan(
    FoundryStorageIntent Intent,
    bool UnloadAllModels,
    bool StopWebService,
    bool RemoveAllCachedModels,
    bool DeleteModelFiles,
    FoundryRuntimeFiles RuntimeFiles,
    bool ReleaseRuntimeReferences,
    FoundryKeepOnlySelected KeepOnlySelected)
{
    public static FoundryStoragePlan Nothing { get; } = new(
        Intent: FoundryStorageIntent.None,
        UnloadAllModels: false,
        StopWebService: false,
        RemoveAllCachedModels: false,
        DeleteModelFiles: false,
        RuntimeFiles: FoundryRuntimeFiles.Keep,
        ReleaseRuntimeReferences: false,
        KeepOnlySelected: FoundryKeepOnlySelected.Unchanged);

    public bool IsNothing => this == Nothing;
}

/// <summary>A model as the SDK reports it: the variant id, and the family alias it belongs to.</summary>
internal readonly record struct FoundryModelIdentity(string Id, string? Alias);

/// <summary>
/// Decides when Scribe gives back the disk and memory Foundry Local uses. Pure: callers supply the
/// state, this only decides, so every combination is testable without the native runtime.
/// </summary>
/// <remarks>
/// The rules are the maintainer's, recorded so the reasoning survives the next refactor:
/// <list type="number">
/// <item>Nothing is downloaded by browsing. That half lives in the settings window and in which
/// service calls are allowed to initialize the runtime; this type only covers giving space back.</item>
/// <item>When the saved provider moves away from Foundry Local, unload the models, stop the web
/// service Scribe started, remove the models Scribe downloaded, and delete the execution-provider
/// downloads in Scribe's own Foundry Local directory.</item>
/// <item>When the user switches Foundry Local models, delete the others once the new one is in use,
/// keeping only the selected one.</item>
/// <item>Switching AI cleanup off while Foundry Local stays selected only unloads, to free memory.
/// The files stay so switching it back on is quick.</item>
/// <item>At startup with a provider other than Foundry Local, reclaim what earlier sessions left.
/// This is also how deferred execution-provider deletions complete.</item>
/// <item>At startup with Foundry Local selected: when no model is cached and AI cleanup is off, the
/// execution-provider downloads came from browsing, so they are deleted. When models are cached, the
/// selected alias's variants and the runtime stay, and every other alias's variants are deleted once
/// the selected one is in use. "Only if they swap to it and choose to load the model should it be
/// kept."</item>
/// <item>While another provider stays saved, nothing is reclaimed mid-session. A model the user
/// loaded or listed by hand in the meantime is reclaimed at the next start, never while it may be
/// the thing they just asked for.</item>
/// <item>Deferred work is dropped when the user explicitly loads or lists Foundry Local models after
/// it was scheduled, so it can never delete or unload the result of that request.</item>
/// </list>
/// </remarks>
internal static class FoundryStoragePolicy
{
    /// <summary>
    /// The storage work for a configuration being applied.
    /// </summary>
    /// <param name="previous">
    /// The configuration applied before this one in this process, or null for the first one, which
    /// is the startup configuration.
    /// </param>
    /// <param name="next">The configuration being applied.</param>
    /// <param name="presence">How much of the Foundry Local runtime this process holds.</param>
    public static FoundryStoragePlan OnSettingsApplied(
        FoundrySelection? previous, FoundrySelection next, FoundryRuntimePresence presence)
    {
        if (!next.IsFoundry)
        {
            // Another provider was already saved. Any settings save lands here (the tray AI toggle,
            // or the usage page adding a dictionary term), so reclaiming here would delete a model
            // the user just loaded by hand. The next start reclaims it instead, with nothing loaded.
            if (previous is { IsFoundry: false })
            {
                return FoundryStoragePlan.Nothing;
            }

            return ReclaimEverything(presence);
        }

        // Startup with Foundry Local. Nothing is loaded yet, so the selected alias's variants and the
        // runtime are kept; other aliases' variants go once the selected model is in use, and while
        // cleanup is off the runtime downloads are reviewed against whether any model is cached.
        if (previous is null)
        {
            return FoundryStoragePlan.Nothing with
            {
                Intent = next.Enabled ? FoundryStorageIntent.None : FoundryStorageIntent.ReviewUnusedRuntime,
                KeepOnlySelected = FoundryKeepOnlySelected.ArmThisSession,
            };
        }

        // Switching to Foundry Local downloads what it needs through initialization; nothing to delete.
        if (previous is not { IsFoundry: true } before)
        {
            return FoundryStoragePlan.Nothing;
        }

        var switchedModel = !SameAlias(before.Alias, next.Alias);
        var switchedOff = before.Enabled && !next.Enabled;
        if (!switchedModel && !switchedOff)
        {
            return FoundryStoragePlan.Nothing;
        }

        // Without a runtime in this process nothing can be loaded, so there is nothing to unload.
        var unload = switchedOff && presence != FoundryRuntimePresence.None
            ? ForIntent(FoundryStorageIntent.UnloadOnly, presence)
            : FoundryStoragePlan.Nothing;
        return unload with
        {
            KeepOnlySelected = switchedModel ? FoundryKeepOnlySelected.Set : FoundryKeepOnlySelected.Unchanged,
        };
    }

    /// <summary>
    /// The concrete actions an intent comes to given how much of the runtime exists right now. The
    /// work runs later than the decision, and the user can reach the Foundry Local section in the
    /// meantime, so it is re-derived at execution time instead of trusting the presence it was
    /// decided with.
    /// </summary>
    /// <param name="intent">The intent being carried out.</param>
    /// <param name="presence">How much of the runtime exists now.</param>
    /// <param name="modelCache">What the model cache holds now. Only the runtime review reads it.</param>
    public static FoundryStoragePlan ForIntent(
        FoundryStorageIntent intent, FoundryRuntimePresence presence, FoundryModelCache modelCache = FoundryModelCache.Unknown) => intent switch
    {
        FoundryStorageIntent.ReclaimEverything => ReclaimEverything(presence),

        // Only a live catalog can have a model loaded by this process.
        FoundryStorageIntent.UnloadOnly => FoundryStoragePlan.Nothing with
        {
            Intent = FoundryStorageIntent.UnloadOnly,
            UnloadAllModels = presence == FoundryRuntimePresence.CatalogLive,
        },

        // Only with no runtime in this process (nothing of it can be loaded) and a cache known to be
        // empty. A cache that cannot be read is treated as holding the selected model.
        FoundryStorageIntent.ReviewUnusedRuntime => presence == FoundryRuntimePresence.None && modelCache == FoundryModelCache.Empty
            ? FoundryStoragePlan.Nothing with
            {
                Intent = FoundryStorageIntent.ReviewUnusedRuntime,
                RuntimeFiles = FoundryRuntimeFiles.DeleteNow,
                KeepOnlySelected = FoundryKeepOnlySelected.Clear,
            }
            : FoundryStoragePlan.Nothing with { Intent = FoundryStorageIntent.ReviewUnusedRuntime },

        _ => FoundryStoragePlan.Nothing,
    };

    /// <summary>
    /// Whether deferred work still matches the configuration that is applied when it finally runs.
    /// A reclaim scheduled for a switch away must not run after the user switched back, an unload for
    /// "cleanup off" must not unload the model a re-enabled cleanup is loading, and nothing scheduled
    /// before an explicit Load or List may undo it.
    /// </summary>
    /// <param name="intent">The deferred work.</param>
    /// <param name="current">The configuration applied now.</param>
    /// <param name="explicitUseSinceScheduled">
    /// True when the user explicitly loaded or listed Foundry Local models after the work was scheduled.
    /// </param>
    public static bool StillApplies(FoundryStorageIntent intent, FoundrySelection? current, bool explicitUseSinceScheduled) =>
        !explicitUseSinceScheduled && intent switch
        {
            FoundryStorageIntent.ReclaimEverything => current is { IsFoundry: false },
            FoundryStorageIntent.UnloadOnly => current is { IsFoundry: true, Enabled: false },
            FoundryStorageIntent.ReviewUnusedRuntime => current is { IsFoundry: true, Enabled: false },
            _ => false,
        };

    /// <summary>
    /// The cached model variants to remove once the selected model is in use, keeping only the
    /// selected one. A variant is kept when its id or its family alias matches any of
    /// <paramref name="selected"/>, which is how the SDK names one model several ways: the family
    /// alias the user picked (<c>qwen3-1.7b</c>), the variant it resolved to
    /// (<c>qwen3-1.7b-generic-gpu:2</c>), and a CPU variant a GPU demotion pinned. A loaded variant
    /// is never removed, and an empty selection removes nothing rather than everything.
    /// </summary>
    public static IReadOnlyList<string> SelectModelsToRemove(
        IEnumerable<string?> selected,
        IEnumerable<FoundryModelIdentity> cached,
        IEnumerable<FoundryModelIdentity> loaded)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in selected)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                keep.Add(name.Trim());
            }
        }

        if (keep.Count == 0)
        {
            return [];
        }

        var loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in loaded)
        {
            if (!string.IsNullOrWhiteSpace(model.Id))
            {
                loadedIds.Add(model.Id.Trim());
            }
        }

        var remove = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in cached)
        {
            var id = model.Id?.Trim();
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;
            }

            var alias = model.Alias?.Trim();
            var isSelected = keep.Contains(id) || (!string.IsNullOrEmpty(alias) && keep.Contains(alias));
            if (!isSelected && !loadedIds.Contains(id))
            {
                remove.Add(id);
            }
        }

        return remove;
    }

    private static FoundryStoragePlan ReclaimEverything(FoundryRuntimePresence presence) => presence switch
    {
        FoundryRuntimePresence.None => FoundryStoragePlan.Nothing with
        {
            Intent = FoundryStorageIntent.ReclaimEverything,
            DeleteModelFiles = true,
            RuntimeFiles = FoundryRuntimeFiles.DeleteNow,
            KeepOnlySelected = FoundryKeepOnlySelected.Clear,
        },

        // The marker stays: if the user switches back to Foundry Local before restarting, the pending
        // "keep only the selected model" still applies, and a restart on another provider deletes the
        // model files and clears it anyway.
        FoundryRuntimePresence.ManagerOnly => FoundryStoragePlan.Nothing with
        {
            Intent = FoundryStorageIntent.ReclaimEverything,
            RuntimeFiles = FoundryRuntimeFiles.DeferToNextStartup,
        },

        _ => new FoundryStoragePlan(
            Intent: FoundryStorageIntent.ReclaimEverything,
            UnloadAllModels: true,
            StopWebService: true,
            RemoveAllCachedModels: true,
            DeleteModelFiles: false,
            RuntimeFiles: FoundryRuntimeFiles.DeferToNextStartup,
            ReleaseRuntimeReferences: true,
            KeepOnlySelected: FoundryKeepOnlySelected.Clear),
    };

    private static bool SameAlias(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? CleanupModelCatalog.DefaultAlias : alias.Trim();
}
