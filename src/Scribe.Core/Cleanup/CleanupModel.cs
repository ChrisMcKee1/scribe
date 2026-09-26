namespace Scribe.Core.Cleanup;

/// <summary>
/// A Foundry Local model offered for AI text cleanup. <see cref="Alias"/> is the Foundry catalog
/// alias used to download and load the model; <see cref="DisplayName"/> and <see cref="Hint"/> are
/// for the settings UI. The list is deliberately small and curated to text-only instruct models
/// that are fast and obedient at "rewrite, don't answer" tasks.
/// <see cref="Recommendation"/> is set only on the models the golden-suite benchmark named as
/// on-device winners (see docs/model-leaderboard.md); it is null for everything else so the UI can
/// flag the picks worth defaulting to without editorialising the rest of the list.
/// </summary>
public sealed record CleanupModel(string Alias, string DisplayName, string Hint, string? Recommendation = null);

/// <summary>
/// Curated set of Foundry Local models suitable for low-latency grammar/punctuation cleanup,
/// validated against the live Foundry catalog. Vision and reasoning-heavy models are excluded
/// because cleanup wants a small, deterministic, instruction-following text model.
/// </summary>
public static class CleanupModelCatalog
{
    /// <summary>
    /// Default model. Qwen3 1.7B: newest-generation, ~1.3 GB, chat+tools, and honours the
    /// <c>/no_think</c> directive so it returns corrected text directly with no reasoning preamble.
    /// </summary>
    public const string DefaultAlias = "qwen3-1.7b";

    public static IReadOnlyList<CleanupModel> Curated { get; } = new[]
    {
        new CleanupModel("qwen3-1.7b", "Qwen3 1.7B (recommended)", "About 1.3 GB. Scribe's recommended default."),
        new CleanupModel("qwen2.5-1.5b", "Qwen2.5 1.5B", "About 1.3 GB. Proven and very fast. A safe lightweight choice."),
        new CleanupModel("qwen3.5-2b-text", "Qwen3.5 2B", "About 1.4 GB. Slightly better writing, a little slower."),
        new CleanupModel("qwen3-4b", "Qwen3 4B", "About 2.7 GB. Higher quality. Slower."),
        new CleanupModel("phi-4-mini", "Phi-4 Mini", "About 3.6 GB. Strong grammar."),
        // Golden-suite winners (docs/model-leaderboard.md). Larger downloads than the lightweight
        // defaults, but they top the on-device board: mistral-nemo-12b at ~1.0 s median is the
        // fastest usable local model, and phi-4 earns the best offline quality grade.
        new CleanupModel("mistral-nemo-12b-instruct", "Mistral NeMo 12B", "About 7 GB, large download. Fastest usable on-device model. Real-time feel with solid quality.", "Best on-device balance"),
        new CleanupModel("phi-4", "Phi-4", "About 9 GB, large download. Best offline quality. Slower and a larger download.", "Best on-device quality"),
    };

    /// <summary>Resolves an alias to its descriptor, falling back to the default when unknown.</summary>
    public static CleanupModel Resolve(string? alias)
    {
        if (!string.IsNullOrWhiteSpace(alias))
        {
            foreach (var model in Curated)
            {
                if (string.Equals(model.Alias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    return model;
                }
            }

            // Allow advanced users to type any valid catalog alias even if it is not curated.
            return new CleanupModel(alias, alias, "Custom Foundry Local model.");
        }

        return Curated[0];
    }
}

/// <summary>
/// A live entry from the Foundry Local catalog, surfaced in the searchable model picker.
/// <see cref="Alias"/> is the catalog alias used to load the model; <see cref="Cached"/> means it is
/// already downloaded on this PC, and <see cref="Loaded"/> means it is currently resident in the
/// runtime (only one model is kept loaded at a time). <see cref="ExecutionProvider"/> and
/// <see cref="DeviceType"/> are both reported by the SDK, for example
/// <c>QNNExecutionProvider</c> on the NPU.
/// </summary>
public sealed record FoundryModelOption(
    string Alias,
    bool Cached,
    bool Loaded,
    string? ExecutionProvider = null,
    string? DeviceType = null)
{
    /// <summary>
    /// Plain-language hardware note for the picker, or null when the SDK reports nothing usable.
    /// Foundry Local chooses the provider itself, so this reports what it picked rather than
    /// offering a choice we do not actually control.
    /// </summary>
    public string? ExecutionBuildLabel => FoundryExecutionProviders.Describe(DeviceType, ExecutionProvider);

    /// <summary>Device badge ("NPU", "GPU", "CPU") for the loaded-model line, or null.</summary>
    public string? DeviceLabel => FoundryExecutionProviders.ShortDevice(DeviceType);
}
