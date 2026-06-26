namespace Scribe.Core.Cleanup;

/// <summary>
/// Optional AI post-editor that rewrites raw transcription into cleanly punctuated, grammatical
/// text. It can run on-device (Foundry Local) or against a model the user has deployed in Microsoft
/// Foundry. The contract is intentionally forgiving: cleanup is a best-effort enhancement layered
/// on top of dictation, so it must never throw and must always degrade to the original text when
/// disabled, still loading, or failing.
/// </summary>
public interface ITextCleanupService : IAsyncDisposable
{
    /// <summary>Current engine lifecycle state (see <see cref="CleanupStatus"/>).</summary>
    CleanupStatus Status { get; }

    /// <summary>Human-readable detail for the current status (e.g. download progress, errors).</summary>
    string? StatusDetail { get; }

    /// <summary>Raised whenever <see cref="Status"/> or <see cref="StatusDetail"/> changes.</summary>
    event Action? StatusChanged;

    /// <summary>
    /// Applies a new cleanup configuration (enable/disable, provider, model/deployment). Safe to
    /// call repeatedly and at any time (startup, settings save). When (re)initialization is needed
    /// it happens in the background; the method returns immediately and the pipeline keeps using raw
    /// text until the status is <see cref="CleanupStatus.Ready"/>.
    /// </summary>
    void Configure(CleanupOptions options);

    /// <summary>
    /// Cleans a single transcription. Returns the input unchanged when cleanup is disabled, the
    /// engine is not yet ready, the input is empty/too long, or anything fails or times out.
    /// Never throws.
    /// </summary>
    Task<string> CleanAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lightweight availability probe for the settings UI: initializes the Foundry Local runtime
    /// (without downloading a model) and reports whether it is usable on this machine. Never throws.
    /// Only meaningful for the Foundry Local provider.
    /// </summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the Foundry Local catalog as searchable options for the settings model picker, marking
    /// which models are already downloaded (cached) and which one is currently loaded. Returns an
    /// empty list when Foundry Local is unavailable. Never throws.
    /// </summary>
    Task<IReadOnlyList<FoundryModelOption>> ListFoundryModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the alias of the model currently loaded in the Foundry Local runtime, or <c>null</c>
    /// when none is loaded or Foundry Local is unavailable. Never throws.
    /// </summary>
    Task<string?> GetLoadedFoundryModelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a Foundry Local model into the runtime, downloading it first if needed, and unloads any
    /// other model so only one stays resident at a time. Reports human-readable progress through
    /// <paramref name="progress"/>. Returns <c>true</c> on success. Never throws.
    /// </summary>
    Task<bool> LoadFoundryModelAsync(string alias, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads a Foundry Local model from the runtime to free memory. When <paramref name="alias"/>
    /// is blank, unloads whichever model is currently loaded. Returns <c>true</c> when something was
    /// unloaded. Never throws.
    /// </summary>
    Task<bool> UnloadFoundryModelAsync(string? alias, CancellationToken cancellationToken = default);
}
