namespace Scribe.Core.Cleanup;

/// <summary>
/// Lifecycle of the optional Foundry Local text-cleanup engine. The dictation pipeline only
/// rewrites text when the status is <see cref="Ready"/>; every other state falls back to the
/// raw transcription so a slow download or a missing dependency never blocks dictation.
/// </summary>
public enum CleanupStatus
{
    /// <summary>The feature is switched off in settings.</summary>
    Disabled,

    /// <summary>Foundry is initializing (creating the runtime, registering hardware EPs).</summary>
    Initializing,

    /// <summary>The selected model is downloading or loading into memory.</summary>
    Downloading,

    /// <summary>The model is loaded and ready to clean text.</summary>
    Ready,

    /// <summary>Foundry could not be initialized or the model could not be loaded.</summary>
    Unavailable,
}
