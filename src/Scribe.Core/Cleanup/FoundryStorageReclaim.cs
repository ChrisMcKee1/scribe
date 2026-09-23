namespace Scribe.Core.Cleanup;

/// <summary>Why Scribe gave back disk space Foundry Local was using.</summary>
public enum FoundryStorageReclaimReason
{
    /// <summary>
    /// The saved provider is not Foundry Local, so its models and hardware runtime were given back:
    /// at startup (leftovers from an earlier session or version), or when the user switched away.
    /// </summary>
    ProviderIsNotFoundryLocal,

    /// <summary>The user switched Foundry Local models, so the models no longer selected were removed.</summary>
    ModelSwitched,

    /// <summary>
    /// Foundry Local is selected but no model is downloaded and AI cleanup is off, so the hardware
    /// runtime fetched while browsing the model list was given back at startup. Setting up Foundry
    /// Local downloads it again.
    /// </summary>
    RuntimeWithoutModel,
}

/// <summary>
/// What one Foundry Local storage reclaim gave back, for a user-facing notice such as "Scribe freed
/// 2.3 GB of unused on-device AI files". Sizes and counts only: no paths, no model names.
/// </summary>
/// <param name="Reason">Why the space was reclaimed.</param>
/// <param name="BytesFreed">Disk space this pass gave back.</param>
/// <param name="ModelsRemoved">Cached models removed through the SDK.</param>
/// <param name="FilesDeleted">
/// Files deleted directly: downloads no Foundry Local runtime in this process had loaded.
/// </param>
/// <param name="RuntimeDeletedAtNextStart">
/// True when hardware-runtime downloads loaded in this process had to stay. They are deleted at the
/// next start, and reported again then, if another provider is still selected.
/// </param>
public sealed record FoundryStorageReclaim(
    FoundryStorageReclaimReason Reason,
    long BytesFreed,
    int ModelsRemoved,
    int FilesDeleted,
    bool RuntimeDeletedAtNextStart);
