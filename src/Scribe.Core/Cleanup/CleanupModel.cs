namespace Scribe.Core.Cleanup;

/// <summary>
/// A Foundry Local model offered for AI text cleanup. <see cref="Alias"/> is the Foundry catalog
/// alias used to download and load the model; <see cref="DisplayName"/> and <see cref="Hint"/> are
/// for the settings UI. The list is deliberately small and curated to text-only instruct models
/// that are fast and obedient at "rewrite, don't answer" tasks. Settings recommends none of them: the hints say what
/// each is like, and the benchmark (docs/local-model-benchmark.md) ranks them.
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
    /// Default model. Qwen2.5 1.5B, measured in the 0.5.2 local benchmark (docs/local-model-benchmark.md): the only
    /// small model in the catalog with a TensorRT-RTX build, so on an NVIDIA RTX GPU it cleaned a dictation in about
    /// 0.4 s, where every Qwen3 build fell back to the processor after Foundry Local's WebGPU build failed to start. On
    /// the processor it was also faster than Qwen3 1.7B, the default until 0.5.1 (6.3 s against 9.2 s typically).
    /// </summary>
    public const string DefaultAlias = "qwen2.5-1.5b";

    /// <summary>
    /// The first choice on a PC with an NVIDIA RTX graphics card and 8 GB or more of its own memory. Measured on an
    /// RTX 5080 in the 0.5.2 local benchmark: 83.0 against Qwen2.5 1.5B's 70.6 from the blind judge, in 0.74 s against
    /// 0.39 s typically, and a 4.7 GB download against 3.3 GB for the RTX builds. Without such a card it would run on
    /// the processor, several times slower than Qwen2.5 1.5B, so it is never the choice there.
    /// </summary>
    public const string LargeGpuAlias = "qwen2.5-7b";

    /// <summary>The model Settings starts a first setup with on this PC's hardware (<paramref name="adapters"/>).</summary>
    public static string DefaultAliasFor(IReadOnlyList<Diagnostics.GraphicsAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return adapters.Any(adapter => adapter.IsNvidiaRtx && adapter.HasAtLeast(8)) ? LargeGpuAlias : DefaultAlias;
    }

    public static IReadOnlyList<CleanupModel> Curated { get; } = new[]
    {
        new CleanupModel("qwen2.5-1.5b", "Qwen2.5 1.5B", "About 1.5 GB. Quick on any PC, with or without a graphics card."),
        new CleanupModel("qwen2.5-7b", "Qwen2.5 7B", "About 4.7 GB. Fast on an NVIDIA RTX graphics card with 8 GB or more. Slow without one."),
        new CleanupModel("qwen3-1.7b", "Qwen3 1.7B", "About 1.3 GB. Scribe's default before version 0.5.2."),

        // Foundry Local 2.x runs these on an NVIDIA graphics card through its CUDA builds (0.5.2 benchmark, RTX 5080:
        // Qwen3 4B 0.72 s, Phi-4 Mini 0.79 s, where 1.2.4 had them on the processor at 16 s). Qwen3.5 2B left the list:
        // every build of it fails with 2.1.0 ("Invalid rank for input: position_ids"), and a saved choice of it says so.
        new CleanupModel("qwen3-4b", "Qwen3 4B", "About 2.7 GB. Fast on an NVIDIA graphics card. Slow without one."),
        new CleanupModel("phi-4-mini", "Phi-4 Mini", "About 3.7 GB. Fast on an NVIDIA graphics card. Slow without one."),
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
