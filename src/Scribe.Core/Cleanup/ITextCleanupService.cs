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
    /// Cleans a single transcription. The returned <see cref="CleanupResult.Text"/> is always safe to
    /// inject — on a skip or a runtime failure it is the original input — and the
    /// <see cref="CleanupResult.Outcome"/> tells the caller whether the model ran, was skipped (disabled,
    /// not ready, or empty input), or failed at runtime. Never throws.
    /// <para>
    /// <paramref name="writingStyleOverride"/> swaps the writing-style portion of the system prompt
    /// for this call only (per-app profiles). Blank/null keeps the configured style; overrides reuse
    /// cached per-style agents, so switching apps costs nothing after the first dictation.
    /// </para>
    /// </summary>
    Task<CleanupResult> CleanAsync(
        string text, CancellationToken cancellationToken = default, string? writingStyleOverride = null);

    /// <summary>
    /// Runs a one-off prompt against the currently configured cleanup model and returns the raw text
    /// response, or <c>null</c> when no model is ready or the call fails. Unlike <see cref="CleanAsync"/>
    /// this uses the caller's own system prompt (not the cleanup guardrails), so opt-in helpers such as
    /// AI dictionary suggestions can reuse the user's configured model. Never throws.
    /// </summary>
    Task<string?> CompleteAsync(
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default);

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
