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

    /// <summary>
    /// Human-readable detail for the current status (e.g. download progress, errors), for the
    /// settings window only. It may name the endpoint host the user typed or quote an endpoint's own
    /// error text, so it must never be logged or tagged; <see cref="CleanupResult.FailureReason"/> and
    /// <see cref="CleanupResult.SkipReason"/> carry the diagnostics-safe form.
    /// </summary>
    string? StatusDetail { get; }

    /// <summary>Raised whenever <see cref="Status"/> or <see cref="StatusDetail"/> changes.</summary>
    event Action? StatusChanged;

    /// <summary>
    /// Raised after Scribe gave back disk space Foundry Local was using, and only when something was
    /// actually freed; see <see cref="FoundryStorageReclaim"/> for what it carries. Raised on a
    /// background thread, with no Scribe lock held, so a UI handler must marshal to its dispatcher.
    /// Subscribe before the first <see cref="Configure"/>: the startup reclaim that removes an
    /// earlier session's leftovers runs in the background right after it. Never raised once disposal
    /// has started.
    /// </summary>
    event Action<FoundryStorageReclaim>? FoundryStorageReclaimed;

    /// <summary>
    /// Applies a new cleanup configuration (enable/disable, provider, model/deployment). Safe to
    /// call repeatedly and at any time (startup, settings save). When (re)initialization is needed
    /// it happens in the background; the method returns immediately and the pipeline keeps using raw
    /// text until the status is <see cref="CleanupStatus.Ready"/>.
    /// </summary>
    void Configure(CleanupOptions options);

    /// <summary>
    /// Cleans a single transcription. The returned <see cref="CleanupResult.Text"/> is always safe to
    /// inject; on a skip or a runtime failure it is the original input, and the
    /// <see cref="CleanupResult.Outcome"/> tells the caller whether the model ran, was skipped (disabled,
    /// still starting, or empty input), or failed (unavailable or a runtime failure). Never throws.
    /// <para>
    /// <paramref name="writingStyleOverride"/> swaps the writing-style portion of the system prompt
    /// for this call only (per-app profiles). Blank/null keeps the configured style; overrides reuse
    /// cached per-style agents, so switching apps costs nothing after the first dictation.
    /// </para>
    /// </summary>
    Task<CleanupResult> CleanAsync(
        string text, CancellationToken cancellationToken = default, string? writingStyleOverride = null);

    /// <summary>
    /// One dictation's cleanup with the vocabulary it was admitted with (a vocabulary generation's
    /// <see cref="CleanupVocabulary"/>): its requests carry that vocabulary's glossary, whatever the service was
    /// configured with or has been given since, and every attempt, each chunk and each retry on whichever surface serves
    /// it, is handed over only while <see cref="CleanupVocabulary.Scope"/> is still permitted
    /// (<see cref="Libraries.ILibraryVocabularySource.TryHandOff"/>). An attempt that is not handed over is not sent: its
    /// text stays as dictated, and no failure is reported for it.
    /// </summary>
    AdmittedCleanup Admit(CleanupVocabulary vocabulary);

    /// <summary>
    /// The configuration a one-off request would reach right now (see <see cref="CleanupRecipient"/>), or
    /// null when no model is ready. A caller that asks the user before sending captures this before it asks
    /// and hands it to <c>CompleteAsync</c>, so the request goes where the user agreed or nowhere.
    /// </summary>
    CleanupRecipient? Recipient { get; }

    /// <summary>
    /// Runs a one-off prompt against the currently configured cleanup model and returns its answer. Unlike
    /// <see cref="CleanAsync"/> this uses the caller's own system prompt (not the cleanup guardrails or the
    /// glossary), so opt-in helpers such as AI dictionary suggestions can reuse the user's configured model.
    /// It carries no library vocabulary of its own, so its attempts are handed over under no library scope; a request
    /// whose message carries library terms uses the overload that takes their scope.
    /// <para>
    /// Fails closed: every attempt, the first and each retry the client makes (for GitHub Copilot, the session's
    /// creation and its send), is sent only while the service is serving exactly <paramref name="recipient"/> and is
    /// ready, checked as that attempt is handed over, so a provider saved after the user agreed to send, or cleanup
    /// turned off, never receives what they agreed to send. When that fails before anything was sent the result says
    /// <see cref="CompletionOutcome.RecipientChanged"/> or <see cref="CompletionOutcome.NotReady"/>; when it stops a later
    /// attempt after an earlier one went, <see cref="CompletionOutcome.Failed"/>. Never throws for a failed call;
    /// <see cref="CompletionOutcome.Failed"/> says so.
    /// </para>
    /// </summary>
    Task<CompletionResult> CompleteAsync(
        string systemPrompt, string userMessage, CleanupRecipient recipient, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="CompleteAsync(string, string, CleanupRecipient, CancellationToken)"/> for a request that carries
    /// library vocabulary: the usage insight's labels, whose libraries <paramref name="libraryScope"/> names with the
    /// content each was permitted for (a usage report's <c>LibraryScope</c>). Every attempt, the first and each retry (for
    /// GitHub Copilot, the session's creation and its send), goes only while both hold, both checked as that attempt is
    /// handed over: the service still serves <paramref name="recipient"/> and is ready, and the published library scope
    /// still covers <paramref name="libraryScope"/>. When one no longer holds, that attempt is not sent and the result says
    /// why (<see cref="ScopedCompletionOutcome.LibraryScopeNarrowed"/>, <see cref="ScopedCompletionOutcome.RecipientChanged"/>
    /// or <see cref="ScopedCompletionOutcome.NotReady"/>), with how many requests had left before it. Never throws for a
    /// failed call.
    /// </summary>
    Task<ScopedCompletionResult> CompleteAsync(
        string systemPrompt,
        string userMessage,
        CleanupRecipient recipient,
        Libraries.AiVocabularyScope libraryScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lightweight availability probe for the settings UI: initializes the Foundry Local runtime
    /// (without downloading a model or any execution provider) and reports whether it is usable on
    /// this machine. Never throws. Only meaningful for the Foundry Local provider.
    /// </summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the Foundry Local catalog as searchable options for the settings model picker, marking
    /// which models are already downloaded (cached) and which one is currently loaded. Returns an
    /// empty list when Foundry Local is unavailable. Never throws.
    /// <para>
    /// This is an explicit request: before the first catalog read it downloads and registers the
    /// execution providers for this PC's hardware, which can be several GB. Call it only when the
    /// user asked to list or load models; <see cref="ListFoundryModelsIfInitializedAsync"/> is the
    /// form for merely showing the page. Storage reclaim scheduled before the call is dropped, so it
    /// cannot delete what the call downloads; what the user fetched this way while another provider
    /// stays saved is reclaimed at the next start.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<FoundryModelOption>> ListFoundryModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The same list, but only when the Foundry Local runtime is already running in this process
    /// (because the saved configuration uses it, or the user already asked). Never initializes the
    /// runtime, so it never downloads anything; returns an empty list otherwise. Never throws.
    /// </summary>
    Task<IReadOnlyList<FoundryModelOption>> ListFoundryModelsIfInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the alias of the model currently loaded in the Foundry Local runtime, or <c>null</c>
    /// when none is loaded or the runtime is not running in this process. Never initializes the
    /// runtime. Never throws.
    /// </summary>
    Task<string?> GetLoadedFoundryModelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a Foundry Local model into the runtime, downloading it (and, the first time, the
    /// hardware execution providers) if needed, and unloads any other model so only one stays
    /// resident at a time. An explicit request: storage reclaim or unloading scheduled before the call
    /// is dropped, so it cannot undo the load. Reports human-readable progress through
    /// <paramref name="progress"/>. Returns <c>true</c> on success. Never throws.
    /// </summary>
    Task<bool> LoadFoundryModelAsync(string alias, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads a Foundry Local model from the runtime to free memory. When <paramref name="alias"/>
    /// is blank, unloads whichever model is currently loaded. Returns <c>true</c> when something was
    /// unloaded. Never initializes the runtime. Never throws.
    /// </summary>
    Task<bool> UnloadFoundryModelAsync(string? alias, CancellationToken cancellationToken = default);
}
