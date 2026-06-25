namespace Scribe.Core.Cleanup;

/// <summary>
/// A Foundry Local model offered for AI text cleanup. <see cref="Alias"/> is the Foundry catalog
/// alias used to download and load the model; <see cref="DisplayName"/> and <see cref="Hint"/> are
/// for the settings UI. The list is deliberately small and curated to text-only instruct models
/// that are fast and obedient at "rewrite, don't answer" tasks.
/// </summary>
public sealed record CleanupModel(string Alias, string DisplayName, string Hint);

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
        new CleanupModel("qwen3-1.7b", "Qwen3 1.7B (recommended)", "Newest-gen, ~1.3 GB. Fast and accurate for everyday dictation."),
        new CleanupModel("qwen2.5-1.5b", "Qwen2.5 1.5B", "Proven and very fast, ~1.3 GB. A safe lightweight choice."),
        new CleanupModel("qwen3.5-2b-text", "Qwen3.5 2B", "Latest Qwen3.5 text model, ~1.4 GB. Slightly higher quality."),
        new CleanupModel("qwen3-4b", "Qwen3 4B", "Higher quality, ~2.7 GB. Slower; best on a GPU."),
        new CleanupModel("phi-4-mini", "Phi-4 Mini", "Microsoft Phi-4 Mini, ~3.6 GB. Strong grammar, larger download."),
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
