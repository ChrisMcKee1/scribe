using System.ClientModel;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.AI.Foundry.Local;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using FoundryConfiguration = Microsoft.AI.Foundry.Local.Configuration;
using FoundryLogLevel = Microsoft.AI.Foundry.Local.LogLevel;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Scribe.Core.Cleanup;

/// <summary>
/// AI text cleanup that fixes punctuation, capitalization and grammar in transcribed text by
/// sending it to a chat model. Both providers are unified on the Microsoft Agent Framework
/// <see cref="AIAgent"/> primitive, so the call site (<see cref="CleanAsync"/>) is identical
/// regardless of where the model runs and a different backend swaps in with no change to cleanup
/// logic:
/// <list type="bullet">
/// <item><b>Foundry Local</b>: a small instruct model running on this PC via Foundry's local
/// OpenAI-compatible web service, wrapped as an agent with <see cref="ChatClientAgent"/>. Everything
/// stays offline; the ~1 to 2 GB model downloads on first use.</item>
/// <item><b>Microsoft Foundry</b>: a model the user has already deployed in Azure. A Microsoft
/// Foundry <i>project</i> endpoint (<c>…/api/projects/…</c>) and a classic Azure OpenAI account
/// endpoint both use the account's unified OpenAI v1 inference endpoint, wrapped with
/// <see cref="ChatClientAgent"/>.
/// Authentication reuses the user's Azure CLI sign-in (AAD token, optional tenant override) or an
/// optional API key.</item>
/// </list>
/// <para>
/// Design guarantees that keep dictation robust: initialization happens entirely in the background
/// and is fully cancellable; <see cref="CleanAsync"/> never throws and always falls back to the raw
/// transcription unless a clean, bounded result is available; and switching provider/model or
/// toggling the feature is safe at any time.
/// </para>
/// </summary>
internal sealed partial class TextCleanupService : ITextCleanupService
{
    // Cleanup is a quick rewrite of short text; cap latency and input size so a long paragraph or a
    // slow model can never stall the inject path. On any timeout we return the raw text. Azure gets a
    // longer budget than Foundry Local: a cloud round-trip plus a reasoning model's hidden thinking
    // step is slower than a warm on-device model. The Azure ceiling is generous enough for a reasoning
    // ("pro"/o-series) model to finish a real rewrite; fast chat models (e.g. gpt-5.x-mini) return in
    // a couple of seconds regardless, so the cap only ever bites a genuinely slow model.
    private const int CleanupTimeoutSeconds = 12;
    private const int AzureCleanupTimeoutSeconds = 45;

    /*
     * The GitHub Copilot backend needs its own, larger budget.
     *
     * Measured rather than guessed. Asked to punctuate one 22-word sentence, the Copilot CLI took 27
     * seconds and reported 25.5k input tokens: the coding-agent system context travels with every
     * request, so even a trivial edit carries it. Against the 12 second local budget this provider
     * inherited by default, every single call timed out and cleanup fell back to the raw transcript,
     * which looked identical to the model refusing to edit anything.
     *
     * Sized off that measurement with room for a longer dictation. It is a ceiling, not a wait: a
     * fast answer returns as soon as it arrives.
     */
    private const int CopilotCleanupTimeoutSeconds = 120;
    /*
     * Raised from 30. A cloud reasoning deployment answering its first request
     * has a cold start and a thinking pass to get through before a single
     * visible token, and 30 seconds cancelled Grok 4.6 every time. This matches
     * AuxiliaryCompletionTimeoutSeconds, which is the same shape of call.
     *
     * Waiting longer costs nothing the owner sees. Initialization runs in the
     * background and dictation keeps working on raw text throughout; the only
     * thing a longer budget changes is that a slow-but-working deployment now
     * finishes validating instead of being declared broken.
     */
    private const int InitProbeTimeoutSeconds = 90;
    // A local model's first inference includes warm-up, and that grows with parameter count: a 14B
    // model on the CPU cleared 30s, so cleanup was marked Unavailable for a model that then worked
    // fine. The probe runs once per configuration change, so waiting longer here costs nothing on
    // the dictation path and is far better than a false negative the user cannot explain.
    private const int LocalInitProbeTimeoutSeconds = 180;
    // Azure rejects anything below 16 with "integer_below_min_value", so the probe would have failed
    // on every Azure endpoint and marked cleanup Unavailable. The probe only needs the call to
    // succeed, not to produce useful text, so the minimum accepted value is the right choice.
    /*
     * The init probe's output ceiling, and why there are two of them.
     *
     * On a reasoning model the hidden thinking counts against max_output_tokens,
     * which EstimateMaxTokens already says in as many words and gives the cloud
     * path a 512 floor for. The probe hand-rolled its own options and did not,
     * so it asked a reasoning deployment to think and answer inside 16 tokens.
     * That is not a small budget, it is an impossible one: the model cannot emit
     * a visible character, and at a high reasoning effort it spends a long time
     * finding that out.
     *
     * Grok 4.6 is the model that exposed it. Azure documents its reasoning
     * effort as defaulting to HIGH, so the probe never returned, the 30 second
     * budget cancelled it, initialization failed, and the service sat at
     * Initializing forever. Cleanup then skipped every dictation with "enabled
     * but Initializing" and no error ever reached the user, because degrading
     * quietly to raw text is exactly what this service promises to do.
     *
     * Foundry Local keeps 16. Those models are small, run at a fixed low
     * reasoning cost, and the local probe has a 180 second budget anyway.
     */
    private const int InitProbeMaxOutputTokens = 16;
    private const int CloudInitProbeMaxOutputTokens = AzureReasoningHeadroomTokens;

    /*
     * Room for a cloud reasoning model to think before it answers, on top of whatever the visible
     * reply needs. Measured against the slowest thinker we target rather than guessed: Grok 4.6
     * spends ~532 reasoning tokens on a trivial one-sentence edit, and more as the input grows, so
     * this is that figure with room to grow rather than a number that merely looks generous.
     */
    private const int AzureReasoningHeadroomTokens = 4096;

    // How much of the cloud budget one attempt may spend. Measured cleanups on a real deployment
    // run around 2s and peak near 12s, so 25s is far beyond any healthy call and leaves 20s to
    // recover from a stalled connection.
    private const int CloudFirstAttemptTimeoutSeconds = 25;
    private const int TotalCleanupTimeoutSeconds = 90;

    /// <summary>
    /// The operation-wide budget for one dictation, which must never be smaller than the budget a
    /// single call is allowed.
    /// </summary>
    /// <remarks>
    /// <see cref="CopilotCleanupTimeoutSeconds"/> is 120 and the flat total was 90, so the per-call
    /// figure was unreachable: a Copilot call was cancelled by the operation token at 90 seconds no
    /// matter what its own budget said. The measured 27 second round trip sat inside both, which is
    /// exactly why nothing looked wrong. Derived from the per-call budget rather than written as a
    /// second constant, so the two cannot drift apart again.
    /// </remarks>
    internal static TimeSpan TotalBudgetFor(CleanupProvider provider)
    {
        var single = SingleCallBudgetSeconds(provider);
        return TimeSpan.FromSeconds(Math.Max(TotalCleanupTimeoutSeconds, single));
    }

    /// <summary>What one model call may take, by provider. See the constants for the measurements.</summary>
    internal static int SingleCallBudgetSeconds(CleanupProvider provider) => provider switch
    {
        CleanupProvider.GitHubCopilot => CopilotCleanupTimeoutSeconds,
        CleanupProvider.AzureFoundry or CleanupProvider.OpenAiCompatible => AzureCleanupTimeoutSeconds,
        _ => CleanupTimeoutSeconds,
    };
    // Long dictation is split into bounded chunks cleaned sequentially, so a multi-minute capture is
    // still polished instead of skipped or truncated. Each chunk is small enough that the per-chunk
    // token budget never truncates and the per-chunk timeout bounds latency. The chunk ceiling caps
    // worst-case work for a pathologically long hold (20 * 2400 ≈ 48k chars ≈ ~1h of speech).
    private const int ChunkTargetChars = 2400;
    private const int MaxCleanupChunks = 20;

    /// <summary>
    /// Cap on how much of an endpoint's error text is echoed into a status pill or log line. Long
    /// enough for the sentence that names the fault, short enough that a response body cannot flood
    /// the shared log.
    /// </summary>
    private const int MaxServerMessageChars = 2048;
    private const float CleanupTemperature = 0.1f;
    private const string AgentName = "ScribeCleanup";

    // One-off auxiliary completions (e.g. AI dictionary suggestions) are user-initiated and not on the
    // inject path, so they get a generous budget: a bigger structured answer on a slow local model
    // still finishes, and a reasoning model's hidden thinking has room before the visible JSON.
    private const int AuxiliaryCompletionTimeoutSeconds = 90;
    private const int AuxiliaryCompletionMaxTokens = 2048;

    // The transcript is delimited inside the user message so the model reads it as data to rewrite
    // rather than a message addressed to it. Without this, dictation phrased as a request ("hey, can
    // you make sure X is installed") is routinely *answered* ("Sure, I can help with that") instead
    // of cleaned; the raw text alone in the user turn is indistinguishable from a chat message.
    internal const string TranscriptOpenTag = "<transcript>";
    internal const string TranscriptCloseTag = "</transcript>";

    /*
     * Source-generated rather than RegexOptions.Compiled: a compiled regex is emitted and jitted the
     * first time its class loads, which put tens of milliseconds on the first AI-cleaned dictation.
     * The patterns and options are unchanged. One difference is deliberate: a generated regex matches
     * case-insensitively with the invariant culture, where the fields it replaced took whatever
     * culture was current when the class loaded. Under a Turkish or Azeri user culture that meant an
     * ASCII "I" never matched the "i" in these English phrases, so "I'm sorry" and "Is it" went
     * unrecognized; elsewhere the only difference is that U+0130 no longer counts as an "i".
     */
    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock { get; }

    // A model sometimes declines the rewrite and answers with a canned safety refusal ("I'm sorry, but
    // I cannot assist with that request.") instead of the cleaned text. Two intent families detect it:
    // an apology / AI-identity preamble at the very start, or an inability verb paired with a help
    // object anywhere. See LooksLikeRefusal / TrySanitize; a match is only acted on when the raw input
    // isn't phrased the same way, so genuine dictation of these words is preserved.
    [GeneratedRegex(
        @"^\s*(?:i(?:'m| am)\s+(?:sorry|afraid)\b|i apologi[sz]e\b|my apologies\b|as an ai\b|as a language model\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RefusalPreamble { get; }

    [GeneratedRegex(
        @"\b(?:can'?t|cannot|could\s*n'?t|unable to|not able to|won'?t|will not)\s+(?:assist|help|comply|fulfil|fulfill|provide|process|complete|continue)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RefusalInability { get; }

    // Reply/answer guards (siblings of the refusal guards). A weaker model, a small Foundry Local
    // model especially, sometimes REPLIES to the transcript (answers a dictated question, acknowledges
    // a request, or offers help) instead of editing it. Such replies are short and non-empty, so they
    // slip past the empty/ramble/refusal guards; injected, they overwrite the user's words with the
    // model's answer. This is the defect behind "Can you hear me now?" producing "Yeah." A strong model
    // obeys the prompt and never trips these; the guard is the deterministic backstop for weaker ones.
    // See LooksLikeInventedReply. Each pattern is only acted on when the raw input isn't itself phrased
    // that way, so genuinely dictated affirmations, offers and questions are preserved.
    [GeneratedRegex(
        @"^\s*[""']?\s*(?:yes|yeah|yep|yup|sure\s+thing|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|affirmative|you\s+bet|my\s+pleasure)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReplyOpener { get; }

    // The same affirmation/acknowledgement words anywhere in a message. Used as the valve for signal (2):
    // an opener in the model's output that appears nowhere in the raw input was invented by the model.
    [GeneratedRegex(
        @"\b(?:yes|yeah|yep|yup|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|affirmative|you\s+bet)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AffirmationAnywhere { get; }

    [GeneratedRegex(
        @"\b(?:i\s+can\s+(?:help|assist)|i(?:'d|\s+would)\s+be\s+(?:happy|glad)\s+to|(?:happy|glad)\s+to\s+(?:help|assist)|how\s+(?:can|may)\s+i\s+(?:help|assist)|let\s+me\s+(?:help|assist)|i(?:'m|\s+am)\s+here\s+to\s+(?:help|assist)|is\s+there\s+anything\s+else\s+i)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReplyOffer { get; }

    // Loose interrogative test: a trailing "?" or a leading question word/auxiliary. Only gates the
    // affirmation/terse signals below, which also require the output not to be a question, so occasional
    // imprecision here can never reject an ordinary cleaned sentence.
    [GeneratedRegex(
        @"^\s*(?:who|what|what'?s|when|where|why|how|how'?s|which|whose|whom|do|does|did|is|are|am|was|were|can|could|will|would|should|shall|may|might|have|has|had|must)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuestionOpener { get; }

    // Word tokens (letters/digits, inner apostrophes kept) for the terse-answer overlap check.
    [GeneratedRegex(@"[\p{L}\p{Nd}]+(?:'[\p{L}\p{Nd}]+)*")]
    private static partial Regex WordToken { get; }

    // Collapses an endpoint's multi-line error text into the single line a status pill can show.
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun { get; }

    private readonly ILogger<TextCleanupService> _log;

    // What the Foundry Local SDK logs through: _log, with the user profile folded out of the SDK's
    // own raw exception text. See FoundrySdkLogger.
    private readonly FoundrySdkLogger _foundrySdkLog;
    private readonly string _foundryDemotionsPath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // The one-time Foundry Local runtime creation (manager, execution providers, catalog). A gate of
    // its own rather than _initLock, so a catalog read from the settings window never queues behind
    // a model download that holds _initLock for minutes, while two first-time callers still cannot
    // both register execution providers or publish a half-built runtime.
    private readonly SemaphoreSlim _foundryRuntimeGate = new(1, 1);

    // Cancelled when disposal starts. Every admitted operation links its own token to this one.
    private readonly CancellationTokenSource _lifetime = new();

    // Everything that may still be using a shared resource holds a lease from here, and disposal
    // releases nothing until the last lease is returned. See DisposeAsync.
    private readonly CleanupOperationTracker _operations = new();

    private readonly IFoundryLocalHost _foundryHost;

    // Null disarms storage reclaim entirely. Only the app's own instance (constructed with its real
    // AppPaths) arms it; harnesses and tests that construct the service directly never delete files.
    private readonly FoundryLocalStorage? _foundryStorage;

    // The SDK's application data directory, from FoundryLocalStorage.ResolveAppDataDir. Configured on
    // the first (and, the manager being a process-wide singleton, only) manager this process creates.
    private readonly string? _foundryAppDataDir;

    // Serializes the pending model-switch marker's file operations, so a clear that belongs to an
    // earlier switch can never delete the marker a newer switch has just written. Narrow on purpose:
    // it guards one small file and only ever nests _gate for a field read, never the reverse.
    private readonly object _markerSync = new();

    private CleanupOptions _options = CleanupOptions.Disabled;

    // What the most recent user-applied configuration selected, before any GPU demotion. The storage
    // policy decides from the move between two of these. Guarded by _gate.
    private FoundrySelection? _appliedSelection;

    // Bumped by every explicit Load or List of Foundry Local models. Deferred storage work records the
    // value it was scheduled under and is dropped once it changes, so nothing queued before the user
    // asked for a model can delete or unload what that request produced. Guarded by _gate.
    private long _explicitUseEpoch;

    // "Keep only the selected model", armed for this session because Foundry Local was the saved
    // provider at startup. The persisted marker covers switches; this covers leftovers. Guarded by
    // _markerSync, like the marker.
    private bool _keepOnlySelectedArmed;

    private CleanupStatus _status = CleanupStatus.Disabled;

    // Two forms of the same status text. _statusDetail is for the settings window and may name the
    // endpoint host or quote an endpoint's error; _statusReason is the diagnostics-safe form that
    // may reach the log. See CleanupReason.
    private string? _statusDetail;
    private string? _statusReason;

    // Foundry Local runtime (shared across model switches once initialized). The runtime and catalog
    // are published once, under _gate, by EnsureFoundryRuntimeCoreAsync and EnsureFoundryCatalogAsync
    // and read with Volatile.Read. The SDK keeps one manager per process and never lets it be
    // created again, so switching away from Foundry Local unloads and stops it rather than disposing it.
    private IFoundryLocalRuntime? _foundryRuntime;
    private ICatalog? _catalog;
    private string[] _availableExecutionProviders = ["CPUExecutionProvider"];

    // Web-service state, only touched under _initLock.
    private OpenAIClient? _openAiClient;
    private bool _managerReady;
    private bool _webServiceStarted;

    // The Foundry Local model the ready agent addresses, for the "keep only the selected model" pass.
    private FoundryModelIdentity? _pendingFoundryInUse; // handoff from InitFoundryAsync (serialized by _initLock)
    private FoundryModelIdentity? _foundryInUse;        // guarded by _gate

    // The active cleanup agent (Agent Framework). Rebuilt whenever the provider/model/endpoint
    // changes; null until initialization completes or after the feature is disabled.
    private AIAgent? _agent;

    // Per-app profiles swap the writing style per call. The factory builds an agent for a given
    // system prompt against the already-initialized client (pure object construction, no I/O), and
    // built agents are cached per style so an app switch costs nothing after its first dictation.
    // Both are reset together with _agent whenever the provider/model/endpoint changes.
    private Func<string, AIAgent>? _agentFactory;
    private Func<string, AIAgent>? _pendingFactory; // handoff from InitXxx (serialized by _initLock)

    /*
     * The live Copilot session, when that provider is selected.
     *
     * Typed as object rather than CopilotClient on purpose. A field's type is part of this class's
     * metadata and is resolved when the class is first loaded, so naming the type here would load
     * Microsoft.Agents.AI.GitHub.Copilot on every launch and undo the lazy loading that keeping the
     * references inside InitGitHubCopilotAsync exists to buy.
     */
    private object? _copilotClientHandle; // guarded by _gate
    private readonly Dictionary<string, AIAgent> _styleAgents = new(StringComparer.Ordinal);

    private CancellationTokenSource? _configureCts;

    /*
     * Who owns the status, and the invariant that keeps it from ever getting stuck. Guarded by _gate.
     *
     * Initializing or Downloading on its own says nothing about whether anything will finish it. An
     * initialization that a manual model load cancels stops without publishing anything, by design,
     * and the status stays where it was. Every way this went wrong was a write landing after its writer
     * had lost ownership: identical saves coalesced onto a run that no longer existed; a reserved
     * initialization's first status was published after a load had cancelled it and written the
     * terminal one; a superseded run's late failure made its successor's "in progress" look finished,
     * so the load that interrupted the successor never restarted it. Each time, every later dictation
     * skipped cleanup and nothing ever finished the status. The rules:
     *
     *  1. The generation names the status's owner. WriteStatusLocked, the only place the status is
     *     written, accepts a write only on behalf of the current generation, so a writer that has lost
     *     ownership cannot land anything, however late it runs.
     *  2. Ownership changes only inside one _gate critical section that moves the generation and writes
     *     the new owner's status in the same step: ReserveInitializationLocked for a path that starts an
     *     initialization (a save, a restart, a rebuild), which also takes the lease and the token that
     *     initialization will run on and observe; or a disable, a configuration that cannot run, or a
     *     model eviction, each of which ends the generation with a terminal status. Only StatusChanged
     *     is raised later, outside _gate and after the locks the decision was made under are released,
     *     and it carries nothing.
     *  3. Within a generation, the other writer is its initialization, identified by _initWriter while
     *     it holds _initLock, so no manual load or unload decides anything underneath it. It raises its
     *     own progress, outcome and Ready notifications outside _gate but before it releases _initLock.
     *     An outside writer (a dictation whose evicted model would not reload) writes on behalf of the
     *     generation it observed before acting.
     *  4. A path that cancels a live initialization's token without taking ownership
     *     (CancelPendingConfigure) takes over its outcome: it restarts it, or replaces it with what its
     *     own load means (a rebuild, or a terminal status for an evicted model), unless something newer
     *     has taken ownership first.
     *
     * So an "in progress" status is only ever written for a live generation, by the owner whose
     * initialization holds the lease and observes the token, and every cancellation hands the terminal
     * status to whoever cancelled.
     */
    private long _initGeneration;
    private InitPhase _initPhase;

    // The generation whose initialization holds _initLock, or 0 when none does. Guarded by _gate.
    private long _initWriter;

    private int _lastReportedPct = -1;

    // Benchmark-only escape hatch (Scribe.Evals, via InternalsVisibleTo): when set, replaces the
    // per-provider per-call cleanup timeout so the eval harness can measure a model's *true* rewrite
    // latency uncapped, then judge real output, instead of every slow model degrading to raw text at
    // the 12 s/45 s production ceiling. Never set in the shipping app; production keeps the caps.
    internal TimeSpan? CleanupTimeoutOverride { get; set; }

    // Test-only override for the whole multi-chunk operation. Production has one deadline across all
    // chunks; benchmark runs that override the per-call timeout remain intentionally uncapped.
    internal TimeSpan? CleanupTotalTimeoutOverride { get; set; }

    // Benchmark-only generation controls and telemetry. The shipping app leaves these unset, so
    // provider defaults remain unchanged until measured evidence supports a production setting.
    internal ReasoningEffort? ReasoningEffortOverride { get; set; }
    internal int? MaxOutputTokensOverride { get; set; }
    internal bool DisableRetries { get; set; }
    internal Action<UsageDetails>? UsageObserver { get; set; }

    // Test-only: lets a test put a fake transport under every OpenAI client this service builds (the
    // custom endpoint and Foundry Local's loopback client), so the privacy, lifetime and storage paths
    // run against the real OpenAI client and agent without any network.
    internal Action<OpenAIClientOptions>? OpenAIClientOptionsOverride { get; set; }

    // Test-only: stands in for the provider-specific half of initialization (the Copilot CLI handshake,
    // the Azure client construction) and returns the agent factory it would have built. Everything
    // provider-agnostic around it (the readiness probe, publication, the prompt-only rebuild) still
    // runs for real, so those paths are testable for every provider without the CLI or the network.
    internal Func<CleanupOptions, CancellationToken, Task<Func<string, AIAgent>>>? ProviderFactoryForTesting { get; set; }

    // Test-only: the most recently scheduled background storage work, so a test can wait for it
    // deterministically instead of sleeping.
    internal Task LastStorageWork { get; private set; } = Task.CompletedTask;

    // Test-only: when set, deferred storage work waits for it before doing anything, so a test can
    // queue work and then act before it runs.
    internal Task? StorageWorkGateForTesting { get; set; }

    // Test-only: runs once a manual load or unload has decided what its change of resident model means
    // and released the init lock, before that decision is carried out, so a test can land a newer
    // configuration in exactly that gap.
    internal Action? ResidentChangeDecidedForTesting { get; set; }

    // Test-only: stands in for the Copilot CLI session, so disposal's release of it is testable
    // without the CLI. Anything IAsyncDisposable is released exactly like the real client.
    internal object? CopilotSessionForTesting
    {
        set
        {
            lock (_gate)
            {
                _copilotClientHandle = value;
            }
        }
    }

    /// <summary>
    /// How long disposal waits for admitted operations before giving up on releasing the shared
    /// resources they may still be using. Short, because the app waits on it while exiting; a
    /// cooperative operation stops well inside it, and one that does not is left to process exit
    /// rather than having its client, runtime or semaphore disposed underneath it.
    /// </summary>
    internal TimeSpan DisposalDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>What the last disposal did. Test-only observability for the release-or-leak decision.</summary>
    internal CleanupDisposalOutcome DisposalOutcome { get; private set; }

    public TextCleanupService(ILogger<TextCleanupService> log, AppPaths? paths = null)
        : this(
            log,
            paths,
            new FoundryLocalSdkHost(),
            paths is null ? null : FoundryLocalStorage.For(paths))
    {
    }

    internal TextCleanupService(
        ILogger<TextCleanupService> log,
        AppPaths? paths,
        IFoundryLocalHost foundryHost,
        FoundryLocalStorage? foundryStorage)
    {
        var resolvedPaths = paths ?? new AppPaths();
        _log = log;
        _foundrySdkLog = new FoundrySdkLogger(log, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _foundryDemotionsPath = Path.Combine(resolvedPaths.RootDir, "foundry-local-demotions.json");
        _foundryHost = foundryHost;
        _foundryStorage = foundryStorage;

        // The directory reclaim may delete under is, by construction, the one the SDK is told to use.
        _foundryAppDataDir = foundryStorage?.AppDataDir ?? FoundryLocalStorage.ResolveAppDataDir(resolvedPaths);
    }

    public CleanupStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    public string? StatusDetail
    {
        get { lock (_gate) { return _statusDetail; } }
    }

    /// <summary>The diagnostics-safe form of <see cref="StatusDetail"/>.</summary>
    internal string? StatusReason
    {
        get { lock (_gate) { return _statusReason; } }
    }

    public event Action? StatusChanged;

    public event Action<FoundryStorageReclaim>? FoundryStorageReclaimed;

    public void Configure(CleanupOptions options)
    {
        ConfigureCore(options);
    }

    /// <summary>
    /// Applies a configuration the user saved. Recovering an interrupted initialization does not
    /// come through here (see <see cref="RestartCancelledConfigure"/>): the live options it re-runs
    /// carry the demoted alias rather than the one the user saved, so they must never be read as the
    /// user switching models.
    /// </summary>
    private void ConfigureCore(CleanupOptions? options)
    {
        if (_operations.IsClosed)
        {
            return;
        }

        var requested = Normalize(options ?? CleanupOptions.Disabled);
        var effective = ApplyPersistedFoundryDemotion(requested);

        // Read before _gate: the first read loads the Foundry Local assembly, and that is not work to
        // do under the state lock. The flag only ever turns true, and a runtime this service created
        // is seen through its own field inside the lock anyway.
        var managerCreated = _foundryStorage is not null && _foundryHost.IsManagerCreated;

        bool nowDisabled = false;
        bool notActionable = false;
        bool promptRebuilt = false;
        bool statusChanged = false;
        Exception? rebuildFailure = null;
        InitReservation? reservation = null;
        CancellationTokenSource? superseded = null;
        CleanupOperationTracker.Lease? storageLease = null;
        var storagePlan = FoundryStoragePlan.Nothing;
        long storageEpoch = 0;

        lock (_gate)
        {
            if (_operations.IsClosed)
            {
                return;
            }

            var sameConfig = _options == effective;
            var promptOnly = !sameConfig && _options.MatchesIgnoringPrompt(effective);
            _options = effective;

            if (!effective.Enabled)
            {
                superseded = _configureCts;
                var owner = NextGenerationLocked(InitPhase.Idle);
                DropAgents();
                statusChanged = WriteStatusLocked(owner, CleanupStatus.Disabled, null);
                nowDisabled = true;
            }
            else if (!effective.IsActionable)
            {
                superseded = _configureCts;
                var owner = NextGenerationLocked(InitPhase.Idle);
                DropAgents();
                statusChanged = WriteStatusLocked(owner, CleanupStatus.Unavailable, CleanupReason.Same(effective.Provider switch
                {
                    CleanupProvider.AzureFoundry => "Choose an Azure deployment to enable cleanup.",
                    CleanupProvider.OpenAiCompatible => "Enter the endpoint URL and model name to enable cleanup.",
                    _ => "Select a model to enable cleanup.",
                }));
                notActionable = true;
            }
            /*
             * A change to what the prompt says, and nothing else, rebuilds the agent in place.
             *
             * Adding a dictionary term, enabling a library, or editing the writing style or a
             * guardrail prompt all change the options, and each used to drop the agents and
             * re-initialize the provider from scratch. For GitHub Copilot that is another twenty
             * second CLI handshake with every dictation in it inserted raw; for Azure and custom
             * endpoints a fresh readiness probe carrying the whole glossary. Nothing it talks to
             * changed, so the factory the running initialization left behind builds the new default
             * agent with no I/O, the same way CleanAsync builds a per-app writing style agent, and
             * cleanup stays Ready throughout. Per-style agents are dropped because they carry the old
             * prompt too. A prompt too long for a small local model now surfaces on the next cleanup
             * as an ordinary failure with the raw-text fallback, instead of at a probe.
             *
             * Only while serving. During an initialization the options it will publish are the
             * ones it started with, so a prompt change then still restarts it, as before, and a
             * factory that throws falls back to the full restart.
             */
            else if (promptOnly && IsServingLocked() && TryRebuildAgentsLocked(effective, out rebuildFailure))
            {
                promptRebuilt = true;
            }
            /*
             * An identical save while initialization is already running is left alone.
             *
             * This tested only for Ready, so a second Save of the SAME settings restarted the work.
             * Harmless when a provider comes up in a second; expensive for GitHub Copilot, whose CLI
             * handshake takes about twenty seconds. Pressing Save and then Save and close, two
             * seconds apart, threw away the first attempt and started the clock again, and every
             * dictation in that window was skipped with "enabled but still starting".
             *
             * Only a configuration that is serving, or whose initialization is actually live, is
             * covered. A configuration that ended at Unavailable still re-initializes on an identical
             * save, because there the repeat IS the retry. So does one left Initializing by a run that
             * no longer exists (a manual model load cancelled it): coalescing on the status alone made
             * that permanent, since nothing would ever finish it and no save could restart it.
             */
            else if (!(sameConfig && (IsServingLocked() || IsInitializationLiveLocked())))
            {
                // Admitted here, under the same lock that closes admission, so the initialization
                // is either tracked by disposal or never started at all.
                if (ReserveInitializationLocked("Applying new settings…") is not { } reserved)
                {
                    return;
                }

                reservation = reserved;
                superseded = reserved.Superseded;
                statusChanged = reserved.StatusChanged;
                // Drop the stale agents immediately so a dictation fired right after a save can never
                // run against the previous provider/model/prompt; CleanAsync passes through raw text
                // until the rebuilt agent is published, then the next call reflects the new settings.
                DropAgents();
            }

            var previous = _appliedSelection;
            var next = FoundrySelection.From(requested);
            _appliedSelection = next;

            if (_foundryStorage is not null)
            {
                storagePlan = FoundryStoragePolicy.OnSettingsApplied(
                    previous, next, CurrentRuntimePresence(managerCreated));
                if (storagePlan.Intent != FoundryStorageIntent.None)
                {
                    storageLease = _operations.TryEnter();
                    storageEpoch = _explicitUseEpoch;
                }
            }
        }

        // Outside _gate: cancellation runs the token's callbacks synchronously on this thread, and a
        // superseded initialization's continuations must never run inside this lock.
        TryCancel(superseded);

        if (storagePlan.KeepOnlySelected == FoundryKeepOnlySelected.Set)
        {
            // Recorded now rather than when the new model is in use, so a restart before then still
            // deletes the model the user switched away from.
            lock (_markerSync)
            {
                _foundryStorage!.WriteKeepOnlySelected(true);
            }
        }
        else if (storagePlan.KeepOnlySelected == FoundryKeepOnlySelected.ArmThisSession)
        {
            // Before the initialization below starts, so its first Ready already sees it.
            lock (_markerSync)
            {
                _keepOnlySelectedArmed = true;
            }
        }

        if (storageLease is not null)
        {
            var plan = storagePlan;
            var epoch = storageEpoch;
            LastStorageWork = Task.Run(() => RunStorageIntentAsync(plan.Intent, epoch, storageLease));
        }

        if (statusChanged)
        {
            RaiseStatusChanged();
        }

        if (nowDisabled)
        {
            _log.LogInformation("AI cleanup disabled.");
            return;
        }

        if (notActionable)
        {
            return;
        }

        if (rebuildFailure is not null)
        {
            LogProviderFailure(LogLevel.Warning, effective.Provider, rebuildFailure,
                "Rebuilding the cleanup agent for a new prompt failed; re-initializing instead.");
        }

        if (promptRebuilt)
        {
            _log.LogInformation(
                "AI cleanup prompt changed; rebuilt the {Provider} agent in place without reconnecting.", effective.Provider);
            return;
        }

        if (reservation is { } started)
        {
            // The status already reads "Applying new settings…", written when the initialization was
            // reserved, so CleanAsync stops serving the old agent (it gates on Ready) while the new
            // provider/model spins up in the background.
            _log.LogInformation("AI cleanup enabled; preparing {Provider} in the background.", effective.Provider);
            StartInitialization(effective, started);
        }
    }

    // Must be called under _gate, while serving. Builds the default agent for a new prompt from the
    // factory the running initialization left behind: pure object construction against the client it
    // already connected, exactly what CleanAsync does for a per-app writing style. The agents it
    // replaces are dropped, not disposed, as everywhere else: they share that client, and a dictation
    // already holding one finishes on it.
    private bool TryRebuildAgentsLocked(CleanupOptions options, out Exception? failure)
    {
        failure = null;
        if (_agentFactory is not { } factory)
        {
            return false;
        }

        try
        {
            _agent = factory(BuildSystemPrompt(options));
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }

        _styleAgents.Clear();
        return true;
    }

    // Must be called under _gate. Moves ownership of the status to a new generation; see the invariant
    // on _initGeneration.
    private long NextGenerationLocked(InitPhase phase)
    {
        _initPhase = phase;
        return ++_initGeneration;
    }

    // One initialization reserved by ReserveInitializationLocked, started after the caller releases _gate.
    private readonly record struct InitReservation(
        long Generation,
        CancellationToken Token,
        CleanupOperationTracker.Lease Lease,
        CancellationTokenSource? Superseded,
        bool StatusChanged);

    /// <summary>
    /// Reserves a new generation for an initialization and writes its first status, in one step under
    /// <c>_gate</c>. Must be called under that lock, after the caller checked disposal has not begun.
    /// <para>
    /// This is the only way a path starts an initialization (see the invariant on
    /// <c>_initGeneration</c>). The lease is admitted, the token created and the status written here,
    /// so there is no moment in which the generation exists but its status still belongs to someone
    /// else: a load that cancels it from here on finds its "in progress" status already in place and
    /// takes over from that. Starting the initialization, cancelling the superseded token and raising
    /// StatusChanged all wait until the caller has released <c>_gate</c> and any lock it decided under.
    /// An initialization started after it was cancelled simply observes its token and returns its lease.
    /// </para>
    /// </summary>
    private InitReservation? ReserveInitializationLocked(string firstStatus)
    {
        if (_operations.TryEnter() is not { } lease)
        {
            return null;
        }

        var superseded = _configureCts;
        _configureCts = new CancellationTokenSource();
        var generation = NextGenerationLocked(InitPhase.Live);
        var statusChanged = WriteStatusLocked(generation, CleanupStatus.Initializing, CleanupReason.Same(firstStatus));
        return new InitReservation(generation, _configureCts.Token, lease, superseded, statusChanged);
    }

    // Must be called under _gate. The configuration is up and serving dictations.
    private bool IsServingLocked() => _status == CleanupStatus.Ready && _agent is not null;

    // Must be called under _gate. An initialization is running for the current generation and will
    // publish a terminal status itself. Initializing or Downloading alone does not mean that, and a
    // run that has already published Unavailable but not yet returned does not count either, so an
    // identical save straight after a failure is still the retry.
    private bool IsInitializationLiveLocked() =>
        _initPhase == InitPhase.Live && _status is CleanupStatus.Initializing or CleanupStatus.Downloading;

    // Launches a reserved initialization. Its lease was admitted under _gate by the reservation and is
    // returned by InitializeAsync when it finishes, however it finishes.
    private void StartInitialization(CleanupOptions options, InitReservation reservation)
    {
        try
        {
            _ = Task.Run(() => InitializeAsync(options, reservation.Generation, reservation.Token, reservation.Lease));
        }
        catch (Exception)
        {
            reservation.Lease.Dispose();
            throw;
        }
    }

    private enum InitPhase
    {
        /// <summary>No initialization will publish a status: it is terminal, or nothing was started.</summary>
        Idle,

        /// <summary>The current generation's initialization is running and will publish a status.</summary>
        Live,

        /// <summary>
        /// A manual model load cancelled the current generation before it published anything. That
        /// load restarts it unless something newer takes over first.
        /// </summary>
        Interrupted,
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (Exception)
        {
            // Already disposed, or a registered callback threw. Neither may stop reconfiguration.
        }
    }

    // How much of the Foundry Local runtime exists here. Reads only fields; managerCreated is the
    // host's process-wide flag, read by the caller outside any lock.
    private FoundryRuntimePresence CurrentRuntimePresence(bool managerCreated)
    {
        if (Volatile.Read(ref _catalog) is not null)
        {
            return FoundryRuntimePresence.CatalogLive;
        }

        return Volatile.Read(ref _foundryRuntime) is not null || managerCreated
            ? FoundryRuntimePresence.ManagerOnly
            : FoundryRuntimePresence.None;
    }

    // Must be called under _gate.
    private void DropAgents()
    {
        _agent = null;
        _agentFactory = null;
        _styleAgents.Clear();
    }

    public async Task<CleanupResult> CleanAsync(
        string text, CancellationToken cancellationToken = default, string? writingStyleOverride = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CleanupResult.Skip(text);
        }

        // Admission first: after disposal starts nothing may read the agent, and while this lease is
        // held disposal will not release the client the agent talks through.
        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return CleanupResult.Skip(text);
        }

        AIAgent? agent;
        CleanupOptions options;
        lock (_gate)
        {
            if (!_options.Enabled || _status != CleanupStatus.Ready || _agent is null)
            {
                // A failed initialization must use the same visible failure path as a failed call.
                // Otherwise every later dictation silently skips a deployment that never became ready.
                if (_options.Enabled && _status == CleanupStatus.Unavailable)
                {
                    const string fallback = "AI cleanup is unavailable. Check AI cleanup settings and try again.";
                    return new CleanupResult(text, CleanupOutcome.Failed, _statusReason ?? fallback)
                    {
                        DisplayDetail = _statusDetail ?? fallback,
                    };
                }

                // Loading is not a failure, but the log should still explain why cleanup was skipped.
                // The pipeline logs and tags SkipReason, so it is built from the diagnostics-safe
                // status: while connecting, the settings text names the endpoint host.
                if (!_options.Enabled)
                {
                    return CleanupResult.Skip(text);
                }

                return CleanupResult.Skip(text, $"AI cleanup is enabled but {_status} ({_statusReason}).") with
                {
                    DisplayDetail = $"AI cleanup is enabled but {_status} ({_statusDetail}).",
                };
            }

            agent = _agent;
            options = _options;

            // Per-app profile: swap in (or lazily build) the agent for the overriding style. An
            // override matching the configured style falls through to the default agent, and a
            // missing factory (shouldn't happen when Ready) safely degrades to the default too.
            var style = string.IsNullOrWhiteSpace(writingStyleOverride) ? null : writingStyleOverride.Trim();
            if (style is not null &&
                !string.Equals(style, CleanupPrompt.ResolveWritingStyle(options.WritingStyle), StringComparison.Ordinal) &&
                _agentFactory is { } factory)
            {
                if (!_styleAgents.TryGetValue(style, out var styled))
                {
                    // Pure object construction against the already-initialized client; no I/O.
                    styled = factory(BuildSystemPrompt(options with { WritingStyle = style }));
                    _styleAgents[style] = styled;
                }

                agent = styled;
            }
        }

        // Capable frontier models should see the complete dictation so they can make coherent sentence
        // and paragraph decisions. Local-prompt models keep bounded chunks because their context and
        // output budgets are much smaller. The prompt-style choice is explicit for custom endpoints,
        // so a local Ollama server can retain chunking by selecting the Local prompt.
        var chunks = PrepareChunks(text, options);
        string? overflowTail = null;
        if (chunks.Count > MaxCleanupChunks)
        {
            overflowTail = string.Join(' ', chunks.Skip(MaxCleanupChunks));
            chunks = chunks.Take(MaxCleanupChunks).ToList();
        }

        var builder = new StringBuilder(text.Length + 16);
        var failures = 0;
        CleanupReason? firstFailure = null;
        var reloadBudget = new ReloadBudget();

        // The service lifetime is linked in so disposal stops an in-flight model call cooperatively
        // instead of waiting out its full budget.
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var totalTimeout = CleanupTotalTimeoutOverride ??
            (CleanupTimeoutOverride is null ? TotalBudgetFor(options.Provider) : Timeout.InfiniteTimeSpan);
        if (totalTimeout != Timeout.InfiniteTimeSpan)
        {
            totalCts.CancelAfter(totalTimeout);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            string cleanedChunk;
            CleanupReason? error;
            try
            {
                (cleanedChunk, error) = await CleanChunkAsync(agent, options, chunks[i], reloadBudget, totalCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Shutting down is neither a failure nor a time-limit miss; the raw text is kept and
                // nothing is flagged red for a dictation the app is abandoning anyway.
                if (_lifetime.IsCancellationRequested)
                {
                    return CleanupResult.Skip(text, "AI cleanup stopped because Scribe is shutting down.");
                }

                firstFailure ??= CleanupReason.Same("AI cleanup exceeded the total time limit.");
                failures += chunks.Count - i;
                for (var remaining = i; remaining < chunks.Count; remaining++)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(chunks[remaining]);
                }

                break;
            }

            if (error is not null)
            {
                failures++;
                firstFailure ??= error;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(cleanedChunk);
        }

        // Every cleaned segment failed; the user effectively got raw text back (any overflow tail is
        // raw too), so this is a hard failure that drives the visible "intelligence failed" feedback
        // and is recorded to the failure log. This must take precedence over the partial/overflow
        // classification below; otherwise a total failure on an over-length capture would be silently
        // reported as a successful partial clean (no red flash, and a log entry claiming success).
        if (failures == chunks.Count)
        {
            var failure = firstFailure ?? CleanupReason.Same("AI cleanup failed.");
            return new CleanupResult(text, CleanupOutcome.Failed, failure.Diagnostic)
            {
                DisplayDetail = failure.Display,
            };
        }

        if (overflowTail is not null)
        {
            builder.Append(' ').Append(overflowTail);
        }

        // Each chunk was already sanitized individually in CleanChunkAsync (think-block/fence/quote
        // stripping plus the per-chunk ramble guard, which turns an unusable answer into a counted
        // failure). Re-running the full sanitizer over the rejoin would re-apply the ramble guard
        // against the whole input and could silently discard a legitimate multi-chunk clean as
        // "Unchanged"; a trim is all the combined text needs.
        var combined = builder.ToString().Trim();
        var changed = !string.Equals(combined, text, StringComparison.Ordinal);
        var outcome = changed ? CleanupOutcome.Cleaned : CleanupOutcome.Unchanged;

        // Some-but-not-all segments failed, and/or a long tail was left raw: the result is still
        // usable, so record the partial degradation for the Settings log without flashing the hard-
        // failure overlay. Report every condition that applies so the log never implies the retained
        // segments all cleaned successfully when some of them actually failed.
        if (failures > 0 || overflowTail is not null)
        {
            string Describe(string? firstFailureText)
            {
                var parts = new List<string>(2);
                if (failures > 0)
                {
                    parts.Add($"{failures} of {chunks.Count} segments failed ({firstFailureText})");
                }

                if (overflowTail is not null)
                {
                    parts.Add($"the remainder beyond the first {chunks.Count} segments was left raw");
                }

                return "Partial cleanup: " + string.Join("; ", parts) + ".";
            }

            return new CleanupResult(combined, outcome, Describe(firstFailure?.Diagnostic))
            {
                DisplayDetail = Describe(firstFailure?.Display),
            };
        }

        return new CleanupResult(combined, outcome);
    }

    public async Task<string?> CompleteAsync(
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt) || string.IsNullOrWhiteSpace(userMessage))
        {
            return null;
        }

        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return null;
        }

        AIAgent agent;
        CleanupOptions options;
        lock (_gate)
        {
            // Reuse the initialized client's agent factory, but with the caller's own system prompt
            // instead of the cleanup guardrails. Unavailable until a model is configured and Ready, so
            // callers get a clean null (and can fall back) rather than an exception when AI is off.
            if (_status != CleanupStatus.Ready || _agentFactory is not { } factory)
            {
                return null;
            }

            options = _options;
            agent = factory(BuildAuxiliarySystemPrompt(options, systemPrompt)); // pure object construction against the initialized client
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(AuxiliaryCompletionTimeoutSeconds));

            var chatOptions = new ChatOptions { MaxOutputTokens = AuxiliaryCompletionMaxTokens };
            if (options.Provider == CleanupProvider.FoundryLocal)
            {
                chatOptions.Temperature = CleanupTemperature;
            }

            var runOptions = new ChatClientAgentRunOptions(chatOptions);
            var result = await agent.RunAsync(userMessage, options: runOptions, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            // Normalize here too: this is the path for the AI usage insight and AI dictionary
            // suggestions, both of which surface free-form model prose in Settings. Cleanup output is
            // covered by TrySanitize, which this path deliberately skips (it has no raw transcript to
            // compare against), so without this the house style would hold for dictation but not here.
            return SanitizeAuxiliaryCompletion(result.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProviderFailure(LogLevel.Warning, options.Provider, ex, "Auxiliary AI completion failed; returning null.");
            return null;
        }
    }

    /*
     * Logs a failure that came back from a model provider.
     *
     * The file log writes an exception's ToString(), and provider exceptions embed the endpoint:
     * HttpRequestException and SocketException append "(host:port)", and ClientResultException
     * carries the server's error body verbatim. So every provider, Foundry Local included, is logged
     * by its shape (see CleanupFailureShape) and never by the exception, which keeps Scribe's own
     * lines to one rule. What Foundry Local's GPU to CPU demotion is diagnosed from survives in the
     * shape as the classification and the execution-provider identifiers (DescribeFailureShape).
     *
     * The SDK's own log lines are a different stream: it logs its failures, raw exception text
     * included, through the logger it is given. That is FoundrySdkLogger, which folds only the user
     * profile. Microsoft's download hosts and the loopback port stay, because they are not user data
     * and they are what makes an SDK download or startup failure diagnosable.
     */
    private void LogProviderFailure(LogLevel level, CleanupProvider provider, Exception exception, string message)
    {
        try
        {
            _log.Log(level, "{Message} ({Provider}; {Failure})", message, provider, DescribeFailureShape(exception));
        }
        catch (Exception)
        {
            // Logging must never turn a handled provider failure into an unhandled one.
        }
    }

    /// <summary>
    /// The diagnostics-safe shape of a provider failure, with this service's own classification of
    /// what went wrong where it has one, and the execution providers a Foundry Local failure names.
    /// Never throws.
    /// </summary>
    internal static string DescribeFailureShape(Exception? exception)
    {
        if (exception is null)
        {
            return CleanupFailureShape.Describe(null);
        }

        string? kind = null;
        var executionProviders = string.Empty;
        try
        {
            kind = exception switch
            {
                OperationCanceledException or TimeoutException => "timeout",
                _ when IsModelNotLoaded(exception) => "model-not-loaded",
                _ when IsGpuShaderIncompatibility(exception) => "gpu-shader",
                _ when IsExecutionProviderUnavailable(exception) => "execution-provider-unavailable",
                _ when IsConnectivityFailure(exception) => "connectivity",
                _ => null,
            };

            // Identifiers only (CleanupFailureShape.SanitizeCode), never the text around them: these
            // are what tell a missing CUDA provider apart from a broken WebGPU shader.
            if (CleanupFailureShape.SanitizeCode(TryParseRequiredExecutionProvider(exception)) is { } required)
            {
                executionProviders += " requires=" + required;
            }

            var available = TryParseAvailableExecutionProviders(exception)
                .Select(CleanupFailureShape.SanitizeCode)
                .OfType<string>()
                .Take(8)
                .ToList();
            if (available.Count > 0)
            {
                executionProviders += " available=" + string.Join(",", available);
            }
        }
        catch (Exception)
        {
            // Classification is a bonus; the shape below is the part that must always be produced.
        }

        return CleanupFailureShape.Describe(exception, kind) + executionProviders;
    }

    // Cleans a single chunk. Returns the cleaned text and a null error on success, or the raw chunk and
    // a human-readable error when the model call throws, times out, or returns nothing usable. Never
    // throws; a failed segment falls back to its raw text so dictation is never lost.
    private async Task<(string Text, CleanupReason? Error)> CleanChunkAsync(
        AIAgent agent, CleanupOptions options, string chunk, ReloadBudget reload, CancellationToken cancellationToken)
    {
        // Azure and BYO endpoints share the longer budget: both may be a cloud round-trip to a
        // reasoning model whose hidden thinking precedes the visible rewrite. GitHub Copilot is
        // remote too, and slower again, so it is in this group with a budget of its own.
        var isCloud = options.Provider is CleanupProvider.AzureFoundry
            or CleanupProvider.OpenAiCompatible
            or CleanupProvider.GitHubCopilot;
        var budget = CleanupTimeoutOverride
            ?? TimeSpan.FromSeconds(SingleCallBudgetSeconds(options.Provider));

        // A cloud connection that has sat idle can be silently dead: the request goes out and
        // nothing ever comes back, so the entire budget drains and the dictation falls back to raw
        // text, while an attempt moments later succeeds in about a second. Spending the whole
        // budget on one attempt makes that stall unrecoverable, so the first attempt gets a slice
        // large enough for any healthy call and a stall still leaves room to try again.
        // Benchmarks pin the timeout explicitly and want exactly one attempt.
        /*
         * Copilot is excluded from the stall retry, and would break without the exclusion.
         *
         * The retry exists for an idle HTTPS connection that has died silently: the request goes out,
         * nothing comes back, and slicing the budget leaves room for a second attempt that usually
         * succeeds at once. The Copilot backend is not that shape. It talks over stdin/stdout to a
         * child process on this machine, so there is no idle socket to go stale.
         *
         * And the slice would cut every call short: CloudFirstAttemptTimeoutSeconds is 25 and a
         * measured Copilot round trip is 27, so a first attempt would be abandoned two seconds before
         * the answer arrived, every time. It gets its whole budget in one attempt.
         */
        var retryOnStall = isCloud
            && options.Provider != CleanupProvider.GitHubCopilot
            && CleanupTimeoutOverride is null;
        var firstAttempt = retryOnStall
            ? TimeSpan.FromSeconds(CloudFirstAttemptTimeoutSeconds)
            : budget;

        var attempt = await RunChunkAttemptAsync(
            agent, options, chunk, firstAttempt, cancellationToken).ConfigureAwait(false);

        // Foundry Local evicts a resident model under memory pressure, and any other model load on the
        // machine evicts it too. The agent keeps working against a model id the runtime no longer has,
        // so every dictation from then on fails with a 400 the user cannot act on. Reload it and retry
        // rather than degrading for the rest of the session. Once per dictation: if the reload does not
        // take, hammering the runtime for every remaining chunk only delays the raw-text fallback.
        if (ShouldAttemptModelReload(attempt.Evicted, options.Provider, reload.Used))
        {
            reload.Used = true;
            switch (await TryReloadEvictedModelAsync(options, cancellationToken).ConfigureAwait(false))
            {
                case ReloadOutcome.Reloaded:
                    attempt = await RunChunkAttemptAsync(
                        agent, options, chunk, budget, cancellationToken).ConfigureAwait(false);
                    break;

                // Loading a 12B model takes minutes, far longer than one dictation may wait, so this
                // is the normal outcome for exactly the large models that get evicted. Saying the
                // reload failed would be wrong and would send the user to Settings for nothing.
                case ReloadOutcome.StillLoading:
                    attempt = attempt with
                    {
                        Error = CleanupReason.Same(
                            "The on-device cleanup model had been unloaded and is loading again. " +
                            "This dictation used raw text; give it a moment and try again."),
                    };
                    break;

                case ReloadOutcome.Superseded:
                    attempt = attempt with
                    {
                        Error = CleanupReason.Same(
                            "AI cleanup settings changed during this dictation, so it used raw text."),
                    };
                    break;
            }
        }

        if (!attempt.Stalled || !retryOnStall)
        {
            return (attempt.Text, attempt.Error);
        }

        _log.LogWarning(
            "AI cleanup stalled for {Seconds:F0}s; retrying once before falling back to raw text.",
            firstAttempt.TotalSeconds);

        var remaining = budget - firstAttempt;
        var retry = await RunChunkAttemptAsync(
            agent, options, chunk, remaining, cancellationToken).ConfigureAwait(false);
        return (retry.Text, retry.Error);
    }

    /// <summary>
    /// One reload attempt per dictation. A mutable holder rather than a parameter because the decision
    /// spans every chunk of a single <see cref="CleanAsync"/> call.
    /// </summary>
    private sealed class ReloadBudget
    {
        public bool Used;
    }

    /// <summary>
    /// Whether an evicted model should be reloaded and the chunk retried. Only Foundry Local can be
    /// reloaded (a remote endpoint's residency is not ours to manage), and only once per dictation:
    /// if the reload does not take, retrying it for every remaining chunk only delays the raw-text
    /// fallback the user is waiting on.
    /// </summary>
    internal static bool ShouldAttemptModelReload(bool evicted, CleanupProvider provider, bool alreadyUsed) =>
        evicted && provider == CleanupProvider.FoundryLocal && !alreadyUsed;

    // Reloads the configured Foundry Local model after the runtime evicted it. The existing agent is
    // still valid: it addresses the model by id, and the id is unchanged across a reload, so nothing
    // has to be rebuilt.
    private async Task<ReloadOutcome> TryReloadEvictedModelAsync(CleanupOptions options, CancellationToken ct)
    {
        // A dictation that started before cleanup was switched off, or before the provider or model
        // changed, must not load its old model back in. Unloading on "cleanup off" is exactly what
        // evicts it, and reloading here would silently undo that.
        long observed;
        lock (_gate)
        {
            if (!_options.Enabled || _options != options)
            {
                return ReloadOutcome.Superseded;
            }

            observed = _initGeneration;
        }

        _log.LogWarning(
            "Foundry Local evicted the cleanup model {Alias}; reloading it before falling back to raw text.",
            options.FoundryModelAlias);

        var reloaded = await LoadFoundryModelCoreAsync(options.FoundryModelAlias, progress: null, ct)
            .ConfigureAwait(false);
        if (reloaded)
        {
            _log.LogInformation("Reloaded the cleanup model {Alias}.", options.FoundryModelAlias);
            return ReloadOutcome.Reloaded;
        }

        // A cancelled reload is not a failed one: the dictation's own deadline can expire while a
        // multi-gigabyte model is still loading, and the load usually completes moments later. Marking
        // cleanup Unavailable there would switch the feature off for a model that is about to be ready.
        if (ct.IsCancellationRequested)
        {
            _log.LogInformation(
                "Reloading {Alias} did not finish within this dictation; leaving cleanup enabled.",
                options.FoundryModelAlias);
            return ReloadOutcome.StillLoading;
        }

        // A genuine failure: drop the stale Ready status so Settings stops claiming cleanup works. On
        // behalf of the generation this dictation checked before reloading: a save, a restart or a
        // decision made since owns the status, and this failure says nothing about any of them.
        PublishStatus(
            observed,
            CleanupStatus.Unavailable,
            CleanupReason.Same($"The on-device model '{options.FoundryModelAlias}' was unloaded and could not be reloaded."));
        return ReloadOutcome.Failed;
    }

    private enum ReloadOutcome
    {
        Reloaded,

        /// <summary>The load outlived this dictation's deadline; the model is likely ready shortly.</summary>
        StillLoading,

        Failed,

        /// <summary>The configuration changed during the dictation, so its model is no longer wanted.</summary>
        Superseded,
    }

    // One model call. Stalled is true only when this attempt's own budget expired, which is the
    // recoverable case; a caller cancellation still propagates. Evicted is true when the endpoint
    // reported the model is no longer loaded, which a reload can fix.
    private async Task<ChunkAttempt> RunChunkAttemptAsync(
        AIAgent agent,
        CleanupOptions options,
        string chunk,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            // The system prompt is baked into the agent at creation, so we only send the delimited
            // transcript and run statelessly (no thread); each dictation is independent, with no
            // history to grow.
            var runOptions = new ChatClientAgentRunOptions(BuildChatOptions(options, chunk));
            var result = await agent.RunAsync(BuildUserMessage(chunk), options: runOptions, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            if (result.Usage is { } usage)
            {
                UsageObserver?.Invoke(usage);
            }

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                return new ChunkAttempt(chunk, CleanupReason.Same("AI cleanup returned no text."), false, false);
            }

            // A non-empty answer can still be unusable (only a think-block, an empty fence, or an
            // over-long ramble). TrySanitize rejects those; treat a rejection as a per-chunk failure
            // so an all-rejected dictation falls back to raw AND surfaces the red "intelligence
            // failed" overlay instead of being logged as a silent unchanged success.
            if (!TrySanitize(result.Text, chunk, out var cleaned))
            {
                var reason = LooksLikeRefusal(result.Text)
                    ? "AI cleanup was declined by the model; used raw text."
                    : "AI cleanup returned unusable output.";
                return new ChunkAttempt(chunk, CleanupReason.Same(reason), false, false);
            }

            return new ChunkAttempt(cleaned, null, false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real caller cancellation (e.g. app shutdown) must propagate, not be treated as a
            // per-segment timeout; otherwise we'd keep calling the model after the user gave up.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            LogProviderFailure(LogLevel.Debug, options.Provider, ex, "AI cleanup timed out for a segment.");
            return new ChunkAttempt(chunk, DescribeFailureReason(ex, options.Provider), true, false);
        }
        catch (Exception ex)
        {
            LogProviderFailure(LogLevel.Debug, options.Provider, ex, "AI cleanup failed for a segment; using raw text.");
            return new ChunkAttempt(chunk, DescribeFailureReason(ex, options.Provider), false, IsModelNotLoaded(ex));
        }
    }

    private readonly record struct ChunkAttempt(string Text, CleanupReason? Error, bool Stalled, bool Evicted);

    // Splits text into chunks no longer than <paramref name="targetChars"/>, breaking on the last
    // sentence-ending punctuation in the back of each window when possible, else the last whitespace,
    // and never mid-word unless a single run has no break at all. Raw ASR output is often lightly
    // punctuated, so the whitespace fallback guarantees bounded chunks for unpunctuated speech.
    internal static List<string> ChunkForCleanup(string text, int targetChars)
    {
        text = text.Trim();
        if (text.Length <= targetChars)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= targetChars)
            {
                var tail = text.AsSpan(start).Trim();
                if (!tail.IsEmpty)
                {
                    chunks.Add(tail.ToString());
                }

                break;
            }

            var window = text.AsSpan(start, targetChars);
            var minBreak = (int)(targetChars * 0.6);

            var breakAt = LastSentenceBreak(window, minBreak);
            if (breakAt < 0)
            {
                breakAt = window.LastIndexOf(' ');
            }

            if (breakAt < minBreak)
            {
                // No sentence or word boundary in range (e.g. one very long run); hard split.
                breakAt = targetChars - 1;
            }

            var piece = text.AsSpan(start, breakAt + 1).Trim();
            if (!piece.IsEmpty)
            {
                chunks.Add(piece.ToString());
            }

            start += breakAt + 1;
        }

        return chunks;
    }

    internal static List<string> PrepareChunks(string text, CleanupOptions options)
    {
        var frontierPrompt = CleanupPrompt.ResolvePromptStyle(options.PromptStyle, options.Provider) ==
            CleanupPromptStyle.Frontier;
        return frontierPrompt ? [text.Trim()] : ChunkForCleanup(text, ChunkTargetChars);
    }

    private static int LastSentenceBreak(ReadOnlySpan<char> window, int minIndex)
    {
        for (var i = window.Length - 1; i >= minIndex; i--)
        {
            if (window[i] is '!' or '?' or '\n' ||
                (window[i] == '.' && (i == window.Length - 1 || char.IsWhiteSpace(window[i + 1]))))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Turns a per-chunk cleanup failure into a message that names the actual fault. A user on the
    /// Store build spent a week on "AI cleanup error: ClientResultException", which is the exception
    /// type and nothing else: no status, and none of the server's own explanation. The HTTP status and
    /// the endpoint's message are the highest-signal things we have, so they drive the text.
    /// This is the settings-window form; see <see cref="DescribeFailureReason"/> for why there are two.
    /// </summary>
    internal static string DescribeFailure(Exception ex, CleanupProvider provider) =>
        DescribeFailureReason(ex, provider).Display;

    /// <summary>
    /// The failure in both forms. The diagnostic form is the same fixed sentence without the
    /// endpoint's own text: that text is whatever the server chose to send back, and servers
    /// routinely quote the request URL, the host, a deployment name, or even part of the request, so
    /// it is only ever shown, never logged or tagged.
    /// </summary>
    internal static CleanupReason DescribeFailureReason(Exception ex, CleanupProvider provider)
    {
        if (ex is OperationCanceledException or TimeoutException)
        {
            return CleanupReason.Same("AI cleanup timed out.");
        }

        var status = ExtractHttpStatus(ex);
        var detail = DescribeServerMessage(ex);

        // The endpoint's own message is appended where it adds something; it is empty often enough
        // (a transport failure has no response body) that every branch has to survive without it.
        CleanupReason WithDetail(string sentence) =>
            new(sentence, string.IsNullOrEmpty(detail) ? sentence : sentence + " " + detail);

        if (IsModelNotLoaded(ex))
        {
            return provider == CleanupProvider.FoundryLocal
                ? CleanupReason.Same(
                    "The on-device cleanup model is no longer loaded in Foundry Local, and reloading it " +
                    "failed. Something else likely evicted it (another app, or a model loaded from the " +
                    "foundry CLI). Reopen Settings and load the model again.")
                : WithDetail($"The endpoint reports that model is not loaded ({status}).");
        }

        if (IsGpuShaderIncompatibility(ex))
        {
            return provider == CleanupProvider.FoundryLocal
                ? WithDetail("This model variant cannot run on this GPU. In Foundry Local, pick a CPU variant " +
                             "of the model and try again.")
                : WithDetail("This model variant cannot run on the endpoint GPU. Pick a CPU variant or use " +
                             "a different model variant.");
        }

        if (IsExecutionProviderUnavailable(ex))
        {
            return provider == CleanupProvider.FoundryLocal
                ? WithDetail("This model variant requires an execution provider that is not available on this PC. " +
                             "Pick a different model in Settings.")
                : WithDetail("This model variant requires an execution provider that is not available on the endpoint. " +
                             "Pick a different model variant.");
        }

        return status switch
        {
            400 => WithDetail("The AI endpoint rejected the request (400)."),

            401 or 403 => provider switch
            {
                CleanupProvider.FoundryLocal => WithDetail($"Foundry Local refused the request ({status})."),
                CleanupProvider.AzureFoundry => WithDetail(
                    $"The AI endpoint rejected the Azure access ({status}). Check the sign-in and role " +
                    "assignment, then try again."),
                _ => CleanupReason.Same(
                    $"The AI endpoint rejected the credentials ({status}). Check the API key, then try again."),
            },

            404 => provider == CleanupProvider.FoundryLocal
                ? CleanupReason.Same(
                    "Foundry Local no longer recognises the cleanup model (404). Reopen Settings and " +
                    "pick the model again.")
                : WithDetail("The AI endpoint could not find that model (404). Check the model name."),

            429 => CleanupReason.Same("The AI endpoint is throttling requests (429). Wait a moment and try again."),

            >= 500 => WithDetail($"The AI endpoint returned a server error ({status}). This is usually transient."),

            _ when IsConnectivityFailure(ex) => provider == CleanupProvider.FoundryLocal
                ? CleanupReason.Same("Couldn't reach Foundry Local. Make sure it is installed and running.")
                : CleanupReason.Same("Couldn't reach the AI endpoint. Check the endpoint URL and your network."),

            _ when status > 0 => WithDetail($"The AI endpoint returned {status}."),

            // Last resort. Still better than the bare type name: the message usually names the fault.
            // The type name alone is what the diagnostic form keeps; the message may embed a host.
            _ => WithDetail($"AI cleanup error: {ex.GetType().Name}."),
        };
    }

    /// <summary>
    /// True when the failure is Foundry Local's "Model '&lt;id&gt;' is not loaded" 400, which it returns
    /// after evicting a resident model. Matched on the text because the endpoint reports it with a
    /// null error code, so the status alone cannot distinguish it from a malformed request. The raw
    /// response body is checked as well as the message: the message shape is a client-library detail,
    /// and losing this signal costs the reload that makes cleanup self-heal.
    /// </summary>
    internal static bool IsModelNotLoaded(Exception? ex) =>
        CleanupFailureShape.Walk(ex).Any(current =>
            MentionsUnloadedModel(CleanupFailureShape.MessageOf(current)) ||
            MentionsUnloadedModel(ReadResponseBody(current)));

    private static bool MentionsUnloadedModel(string? text) =>
        text?.Contains("is not loaded", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool MentionsGpuShaderIncompatibility(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("WebGPU", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ShaderModule", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("compute pipeline", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MentionsExecutionProviderUnavailable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("Cannot load model", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("execution provider, which is not available", StringComparison.OrdinalIgnoreCase);
    }

    // The same bounded walk as CleanupFailureShape: recursing into each aggregate's list and then
    // carrying on down the same chain made the work exponential in the depth of a nested failure.
    private static bool IsGpuShaderIncompatibility(Exception? ex) =>
        CleanupFailureShape.Walk(ex).Any(current =>
            MentionsGpuShaderIncompatibility(CleanupFailureShape.MessageOf(current)) ||
            MentionsGpuShaderIncompatibility(ReadResponseBody(current)));

    private static bool IsExecutionProviderUnavailable(Exception? ex) =>
        CleanupFailureShape.Walk(ex).Any(current =>
            MentionsExecutionProviderUnavailable(CleanupFailureShape.MessageOf(current)) ||
            MentionsExecutionProviderUnavailable(ReadResponseBody(current)));

    private static string? TryParseRequiredExecutionProvider(Exception? ex)
    {
        foreach (var current in CleanupFailureShape.Walk(ex))
        {
            if (TryParseRequiredExecutionProvider(CleanupFailureShape.MessageOf(current)) is { } parsed)
            {
                return parsed;
            }

            if (TryParseRequiredExecutionProvider(ReadResponseBody(current)) is { } bodyParsed)
            {
                return bodyParsed;
            }
        }

        return null;
    }

    private static string? TryParseRequiredExecutionProvider(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const string requires = "it requires the";
        const string unavailable = "execution provider, which is not available";
        var start = text.IndexOf(requires, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += requires.Length;
        var end = text.IndexOf(unavailable, start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
        {
            return null;
        }

        return text[start..end].Trim().Trim('\'', '"');
    }

    private static string[] TryParseAvailableExecutionProviders(Exception? ex)
    {
        foreach (var current in CleanupFailureShape.Walk(ex))
        {
            if (TryParseAvailableExecutionProviders(CleanupFailureShape.MessageOf(current)) is { Length: > 0 } parsed)
            {
                return parsed;
            }

            if (TryParseAvailableExecutionProviders(ReadResponseBody(current)) is { Length: > 0 } bodyParsed)
            {
                return bodyParsed;
            }
        }

        return [];
    }

    private static string[] TryParseAvailableExecutionProviders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        const string marker = "Available EPs:";
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return [];
        }

        var open = text.IndexOf('[', start);
        var close = open >= 0 ? text.IndexOf(']', open + 1) : -1;
        if (open < 0 || close <= open)
        {
            return [];
        }

        return text[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Never throws: reading a raw response can fail on a non-buffered or already-disposed response, and
    // a diagnostics helper that takes down the cleanup path would be worse than the missing detail.
    private static string? ReadResponseBody(Exception ex)
    {
        if (ex is not ClientResultException client)
        {
            return null;
        }

        try
        {
            return client.GetRawResponse()?.Content?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsConnectivityFailure(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The endpoint's own explanation, flattened to one short line so it fits a status pill and a log
    /// entry. Trimmed hard because a client exception message can carry a whole response body.
    /// </summary>
    private static string DescribeServerMessage(Exception ex)
    {
        // Prefer the endpoint's own error envelope: it is authoritative and states the fault directly
        // ("Model '...' is not loaded. Please load the model before getting a ChatClient."). The client's
        // exception message is the fallback, since its shape is a library detail that can change.
        var line = ExtractErrorMessage(ReadResponseBody(ex));

        if (string.IsNullOrWhiteSpace(line))
        {
            // The OpenAI client formats its message as "HTTP 400 (type: code)\n\n<server message>", so
            // the final non-header line is the part worth showing; the status is reported separately.
            line = ex.Message
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => !l.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        // One line: this text lands in a status pill and in a single shared-log entry.
        line = WhitespaceRun.Replace(line, " ").Trim();
        return line.Length <= MaxServerMessageChars ? line : line[..MaxServerMessageChars].TrimEnd() + "…";
    }

    // Pulls error.message out of the standard OpenAI error envelope, falling back to the raw body.
    // Non-throwing: this only runs while already reporting a failure.
    private static string? ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (Exception)
        {
            // Not JSON, or not the envelope we know; the raw body below is still better than nothing.
        }

        return body;
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return false;
        }

        try
        {
            // Creates the manager only. Execution providers are registered with the first catalog
            // read, which this deliberately does not do, so a probe never downloads anything.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return await EnsureFoundryRuntimeAsync(linked.Token).ConfigureAwait(false) is not null;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Foundry Local availability probe failed ({Failure}).", DescribeFailureShape(ex));
            return false;
        }
    }

    public async Task<IReadOnlyList<FoundryModelOption>> ListFoundryModelsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return Array.Empty<FoundryModelOption>();
        }

        // Listing is the user setting Foundry Local up, and it downloads the hardware runtime.
        NoteExplicitFoundryUse();

        try
        {
            // Listing only reads the catalog, so it deliberately does not take the init lock; that
            // way the picker stays responsive even while a model is downloading under InitializeAsync.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var catalog = await EnsureFoundryCatalogAsync(linked.Token).ConfigureAwait(false);
            return catalog is null
                ? Array.Empty<FoundryModelOption>()
                : await ReadFoundryModelOptionsAsync(catalog, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<FoundryModelOption>();
        }
        catch (Exception ex)
        {
            _log.LogDebug("Listing Foundry Local models failed ({Failure}).", DescribeFailureShape(ex));
            return Array.Empty<FoundryModelOption>();
        }
    }

    public async Task<IReadOnlyList<FoundryModelOption>> ListFoundryModelsIfInitializedAsync(
        CancellationToken cancellationToken = default)
    {
        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return Array.Empty<FoundryModelOption>();
        }

        // Only a catalog something else already initialized. Showing the settings page must never be
        // the thing that downloads several gigabytes of execution providers.
        if (Volatile.Read(ref _catalog) is not { } catalog)
        {
            return Array.Empty<FoundryModelOption>();
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return await ReadFoundryModelOptionsAsync(catalog, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<FoundryModelOption>();
        }
        catch (Exception ex)
        {
            _log.LogDebug("Listing Foundry Local models failed ({Failure}).", DescribeFailureShape(ex));
            return Array.Empty<FoundryModelOption>();
        }
    }

    private static async Task<IReadOnlyList<FoundryModelOption>> ReadFoundryModelOptionsAsync(
        ICatalog catalog, CancellationToken cancellationToken)
    {
        var all = await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        var cached = await catalog.GetCachedModelsAsync(cancellationToken).ConfigureAwait(false);
        var loaded = await catalog.GetLoadedModelsAsync(cancellationToken).ConfigureAwait(false);

        var cachedAliases = new HashSet<string>(cached.Select(m => m.Alias), StringComparer.OrdinalIgnoreCase);
        var loadedAliases = new HashSet<string>(loaded.Select(m => m.Alias), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<FoundryModelOption>();
        foreach (var model in all)
        {
            if (string.IsNullOrWhiteSpace(model.Alias) || !seen.Add(model.Alias))
            {
                continue;
            }

            // Both the device type and the provider name come from the SDK. Deriving the device
            // from the provider string would be a hand-maintained mirror of SDK state, and under
            // WinML the provider set is extended by Windows Update, so a name we have never seen
            // is an ordinary runtime condition rather than a theoretical one.
            var runtime = model.Info?.Runtime;
            options.Add(new FoundryModelOption(
                model.Alias,
                cachedAliases.Contains(model.Alias),
                loadedAliases.Contains(model.Alias),
                runtime?.ExecutionProvider,
                runtime?.DeviceType.ToString()));
        }

        // Loaded first, then downloaded, then the rest; alphabetical within each tier.
        return options
            .OrderByDescending(o => o.Loaded)
            .ThenByDescending(o => o.Cached)
            .ThenBy(o => o.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetLoadedFoundryModelAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return null;
        }

        // The runtime is in-process, so if this process never initialized it, nothing is loaded.
        // Initializing it just to answer would download execution providers to report "none".
        if (Volatile.Read(ref _catalog) is not { } catalog)
        {
            return null;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var loaded = await catalog.GetLoadedModelsAsync(linked.Token).ConfigureAwait(false);
            return loaded.Count > 0 ? loaded[0].Alias : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Reading the loaded Foundry Local model failed ({Failure}).", DescribeFailureShape(ex));
            return null;
        }
    }

    public Task<bool> LoadFoundryModelAsync(
        string alias, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return Task.FromResult(false);
        }

        // Before anything else, so storage work already queued can no longer undo this load.
        NoteExplicitFoundryUse();
        return LoadFoundryModelCoreAsync(alias, progress, cancellationToken);
    }

    // An explicit Load or List. See _explicitUseEpoch.
    private void NoteExplicitFoundryUse()
    {
        lock (_gate)
        {
            _explicitUseEpoch++;
        }
    }

    // The load itself. The eviction reload calls this directly: reloading the configured model is not
    // a user asking for one, so it must not cancel storage work the user's settings call for.
    private async Task<bool> LoadFoundryModelCoreAsync(
        string alias, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;

        alias = alias.Trim();
        var acquired = false;
        var reconcile = false;
        long? cancelledGeneration = null;
        long? interruptedGeneration = null;
        string[]? loadedIdentifiers = null;
        try
        {
            // An explicit Load must not queue behind a background readiness probe, which can hold
            // the lock for minutes on a large on-device model. Cancelling the in-flight
            // initialization releases it promptly; the resident-change decision at the end of this
            // method rebuilds the agent once the requested model is resident, and otherwise the
            // interrupted initialization is restarted, so nothing is lost.
            (cancelledGeneration, interruptedGeneration) = CancelPendingConfigure();

            // Serialize with InitializeAsync and other load/unload calls so the runtime is never asked
            // to hold two models at once.
            progress?.Report("Starting Foundry Local…");
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            await EnsureManagerAsync(ct).ConfigureAwait(false);
            if (Volatile.Read(ref _catalog) is null)
            {
                progress?.Report("Foundry Local could not be initialized.");
                return false;
            }

            var model = await ResolveFoundryModelAsync(alias, ct).ConfigureAwait(false);
            if (model is null)
            {
                progress?.Report($"Model '{alias}' was not found in the Foundry catalog.");
                return false;
            }

            await UnloadOtherFoundryModelsAsync(model.Id, model.Alias, ct).ConfigureAwait(false);

            if (!await model.IsCachedAsync(ct).ConfigureAwait(false))
            {
                _lastReportedPct = -1;
                await model.DownloadAsync(p =>
                {
                    var pct = Math.Clamp((int)Math.Round(p), 0, 100);
                    if (pct != _lastReportedPct)
                    {
                        _lastReportedPct = pct;
                        progress?.Report($"Downloading {alias}… {pct}%");
                    }
                }, ct).ConfigureAwait(false);
            }

            progress?.Report($"Loading {alias}…");
            await model.LoadAsync(ct).ConfigureAwait(false);
            progress?.Report($"{alias} is loaded and ready.");
            loadedIdentifiers = [model.Id, model.Alias];
            reconcile = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Reported so the caller has a terminal message: without one the UI would be left
            // showing "Loading x…" forever after a cancelled or timed-out load.
            progress?.Report($"Loading {alias} was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Loading Foundry Local model {Alias} failed ({Failure}).", alias, DescribeFailureShape(ex));

            // "Make sure Foundry Local is installed" was reported for every failure, including the
            // common one where Foundry Local is plainly installed and running but selected a variant
            // needing an execution provider this PC does not have. Naming the provider turns a dead
            // end into an action the user can take.
            progress?.Report(TryParseRequiredExecutionProvider(ex) is { } required
                ? $"Couldn't load {alias}: it needs {required}, which this PC does not have. Pick a different model."
                : $"Couldn't load {alias}. Make sure Foundry Local is installed.");
            return false;
        }
        finally
        {
            // Decided before the init lock is released: until then no initialization, load or unload
            // can change which model is resident, so the decision is made on the picture this load
            // produced. Decided after, an initialization for a configuration saved meanwhile could
            // load its own model and publish Ready first, and this load's stale "a different model
            // was loaded" would then take down a configuration that was working.
            ResidentChange change = default;
            try
            {
                if (reconcile)
                {
                    change = DecideResidentChange(
                        loadedAlias: alias,
                        unloadedAlias: null,
                        unloadedAll: false,
                        loadedIdentifiers: loadedIdentifiers,
                        cancelledGeneration: cancelledGeneration);
                }
            }
            finally
            {
                if (acquired)
                {
                    _initLock.Release();
                }
            }

            if (reconcile)
            {
                ResidentChangeDecidedForTesting?.Invoke();
            }

            var recovered = ApplyResidentChange(change);

            // Recovery keys off whether anything published a terminal status, not off whether the
            // load succeeded. A load while cleanup points at a cloud provider decides nothing, and a
            // failed load never reconciles at all; both would otherwise leave cleanup stuck on
            // "Applying new settings…" and silently emitting raw text. Only an initialization this
            // load interrupted is restarted, and only while nothing newer owns the status.
            if (interruptedGeneration is { } generation && !recovered)
            {
                RestartCancelledConfigure(generation);
            }
        }
    }

    public async Task<bool> UnloadFoundryModelAsync(string? alias, CancellationToken cancellationToken = default)
    {
        using var lease = _operations.TryEnter();
        if (lease is null)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;

        var acquired = false;
        var reconcile = false;
        string? trimmed = null;
        try
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            // Never initializes: a runtime this process never started has nothing loaded in it.
            if (Volatile.Read(ref _catalog) is not { } catalog)
            {
                return false;
            }

            trimmed = alias?.Trim();
            var loaded = await catalog.GetLoadedModelsAsync(ct).ConfigureAwait(false);
            var unloadedAny = false;
            foreach (var model in loaded)
            {
                if (!string.IsNullOrWhiteSpace(trimmed) &&
                    !string.Equals(model.Alias, trimmed, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(model.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await model.UnloadAsync(ct).ConfigureAwait(false);
                unloadedAny = true;
            }

            reconcile = unloadedAny;
            return unloadedAny;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Unloading Foundry Local model {Alias} failed ({Failure}).", alias, DescribeFailureShape(ex));
            return false;
        }
        finally
        {
            // Decided under the init lock for the same reason as a load's: see LoadFoundryModelCoreAsync.
            ResidentChange change = default;
            try
            {
                if (reconcile)
                {
                    change = DecideResidentChange(
                        loadedAlias: null, unloadedAlias: trimmed, unloadedAll: string.IsNullOrWhiteSpace(trimmed));
                }
            }
            finally
            {
                if (acquired)
                {
                    _initLock.Release();
                }
            }

            if (reconcile)
            {
                ResidentChangeDecidedForTesting?.Invoke();
            }

            ApplyResidentChange(change);
        }
    }

    // Unloads every loaded Foundry model except the target so only one stays resident at a time.
    // Best-effort: a failure to unload one model never blocks loading the requested one.
    private async Task UnloadOtherFoundryModelsAsync(string keepId, string keepAlias, CancellationToken ct)
    {
        if (Volatile.Read(ref _catalog) is not { } catalog)
        {
            return;
        }

        try
        {
            var loaded = await catalog.GetLoadedModelsAsync(ct).ConfigureAwait(false);
            foreach (var other in loaded)
            {
                if (string.Equals(other.Id, keepId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(other.Alias, keepAlias, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    _log.LogInformation("Unloading Foundry model {Alias} to keep a single model resident.", other.Alias);
                    await other.UnloadAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug("Could not unload Foundry model {Alias} ({Failure}).", other.Alias, DescribeFailureShape(ex));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not enumerate loaded Foundry models ({Failure}).", DescribeFailureShape(ex));
        }
    }

    /// <summary>
    /// Cancels an initialization already in flight. Used when an explicit user action must take the
    /// init lock without waiting out a probe.
    /// <para>
    /// <c>Cancelled</c> is the generation that was current when this call cancelled a configuration
    /// token, which may belong to a run that had already finished; the resident-change decision
    /// treats the status as stale only while that generation is still the current one.
    /// <c>Interrupted</c> is the generation whose live initialization this call stopped before it
    /// could publish anything. <see cref="InitializeAsync"/> treats cancellation as someone else
    /// taking over the status, which is true when Configure or disposal cancels but not here, so
    /// the caller must pass that generation to <see cref="RestartCancelledConfigure"/> unless its
    /// own resident-change decision published a status instead.
    /// </para>
    /// </summary>
    private (long? Cancelled, long? Interrupted) CancelPendingConfigure()
    {
        CancellationTokenSource? pending;
        long? cancelled = null;
        long? interrupted = null;
        lock (_gate)
        {
            if (_configureCts is { IsCancellationRequested: false } cts)
            {
                pending = cts;
                cancelled = _initGeneration;
            }
            else
            {
                pending = null;
            }

            // Marked under the lock, before the token is cancelled, so an identical save that lands
            // from here on starts again rather than coalescing onto a run that is about to stop.
            if (_initPhase == InitPhase.Live)
            {
                _initPhase = InitPhase.Interrupted;
                interrupted = _initGeneration;
            }
        }

        // Outside _gate: the cancelled initialization's continuations can run inline on this thread.
        TryCancel(pending);
        return (cancelled, interrupted);
    }

    /// <summary>
    /// Restarts the initialization that <see cref="CancelPendingConfigure"/> interrupted, from the
    /// live options, unless something newer owns the status by now.
    /// <para>
    /// Decided in one step under the lock, so it can never supersede a newer configuration: a save,
    /// a reconcile or another restart has moved the generation on, disposal has closed admission,
    /// or the interrupted run published a terminal status of its own before it stopped. Anything
    /// read outside the lock could be overtaken before it was acted on, which is how a recovery
    /// would put back a configuration the user had just replaced or turned off.
    /// </para>
    /// </summary>
    private void RestartCancelledConfigure(long interruptedGeneration)
    {
        CleanupOptions options;
        InitReservation reservation;

        lock (_gate)
        {
            if (_operations.IsClosed ||
                _initGeneration != interruptedGeneration ||
                _initPhase != InitPhase.Interrupted)
            {
                return;
            }

            // Reliable because every reservation writes its first status in the same step: an
            // interrupted generation reads "in progress" unless its initialization published an
            // outcome of its own before it stopped.
            options = _options;
            if (_status is not (CleanupStatus.Initializing or CleanupStatus.Downloading) ||
                !options.Enabled || !options.IsActionable)
            {
                // Terminal already: nothing is left in progress for a restart to finish.
                _initPhase = InitPhase.Idle;
                return;
            }

            // Same admission rule as Configure: tracked by disposal, or never started.
            if (ReserveInitializationLocked("Resuming AI cleanup setup…") is not { } reserved)
            {
                return;
            }

            reservation = reserved;
            DropAgents();
        }

        TryCancel(reservation.Superseded);
        if (reservation.StatusChanged)
        {
            RaiseStatusChanged();
        }

        _log.LogInformation(
            "Restarting the {Provider} cleanup initialization that a manual model load interrupted.", options.Provider);
        StartInitialization(options, reservation);
    }

    // What a manual load or unload means for the cleanup agent: drop it (with a clear status) when its
    // configured model was just evicted, so CleanAsync cannot call an unloaded model, or rebuild it when
    // the configured model is loaded back in, all without a settings save. Decided by
    // DecideResidentChange and carried out by ApplyResidentChange.
    private enum ResidentChangeKind
    {
        None,
        Invalidate,
        Rebuild,
    }

    // A decided resident change. Its status was written when it was decided, in the same step; what is
    // left is the notification and, for a rebuild, starting the initialization it reserved.
    private readonly record struct ResidentChange(
        ResidentChangeKind Kind,
        bool StatusChanged,
        CleanupOptions? Options = null,
        InitReservation? Reservation = null);

    /// <summary>
    /// Decides what a manual load or unload means for the cleanup agent. No-op for other providers
    /// and for disabled cleanup.
    /// <para>
    /// Must be called holding <see cref="_initLock"/>, before the caller releases it: every change of
    /// which model is resident happens under that lock, so only then does the picture the caller
    /// produced still describe the runtime. The decision takes ownership of the status and writes it
    /// in the same step (see the invariant on <c>_initGeneration</c>). Cancelling a superseded token,
    /// raising StatusChanged and starting a rebuild's initialization wait for
    /// <see cref="ApplyResidentChange"/>, once <c>_gate</c> and the init lock are released.
    /// </para>
    /// </summary>
    private ResidentChange DecideResidentChange(
        string? loadedAlias,
        string? unloadedAlias,
        bool unloadedAll,
        IReadOnlyCollection<string>? loadedIdentifiers = null,
        long? cancelledGeneration = null)
    {
        lock (_gate)
        {
            if (_operations.IsClosed)
            {
                return default;
            }

            var options = _options;
            if (options.Provider != CleanupProvider.FoundryLocal || !options.Enabled || !options.IsActionable)
            {
                return default;
            }

            var active = options.FoundryModelAlias;
            bool Matches(string? candidate) =>
                !string.IsNullOrWhiteSpace(candidate) &&
                string.Equals(candidate, active, StringComparison.OrdinalIgnoreCase);

            // One model answers to several names: the family alias the user typed, the concrete
            // variant id, and the alias a persisted demotion pinned. Comparing only the requested
            // name would read a successful load of the configured model as "a different model was
            // loaded" and tear down a perfectly good agent.
            bool MatchesLoaded() =>
                Matches(loadedAlias) || (loadedIdentifiers?.Any(Matches) ?? false);

            // The configured model is gone if everything was unloaded, if it was the unload target, or
            // if a *different* model was just loaded (loading one model evicts all others).
            var evicted = unloadedAll || Matches(unloadedAlias) || (loadedAlias is not null && !MatchesLoaded());
            var nowResident = MatchesLoaded();

            // Normally an agent-less state means some other path already owns the status. That is not
            // true after this caller cancelled the current configuration's token: the agent was dropped
            // and the status left on Initializing by a run that no longer exists, so the eviction has to
            // treat that as stale rather than as somebody else's business. Only while that generation is
            // still the current one, though: a configuration saved since owns the status, and its own
            // initialization, queued behind this caller, decides what ends up resident. A rebuild asks
            // the direct question instead: is a live initialization going to publish an agent? Anything
            // else (an interrupted run, a terminal status, a status nothing is finishing) is stale.
            var afterCancelledInit = cancelledGeneration == _initGeneration;
            if (evicted && (_agent is not null || afterCancelledInit))
            {
                // Ends the current generation with a terminal status, so a restart still pending on it
                // (another load that interrupted it) finds it taken over.
                var owner = NextGenerationLocked(InitPhase.Idle);
                DropAgents();
                return new ResidentChange(
                    ResidentChangeKind.Invalidate,
                    WriteStatusLocked(owner, CleanupStatus.Unavailable, CleanupReason.Same(
                        "The on-device cleanup model was unloaded. Reload it to turn cleanup back on.")));
            }

            if (nowResident && _agent is null && _initPhase != InitPhase.Live &&
                ReserveInitializationLocked("Re-enabling cleanup with the reloaded model…") is { } reserved)
            {
                return new ResidentChange(ResidentChangeKind.Rebuild, reserved.StatusChanged, options, reserved);
            }

            return default;
        }
    }

    /// <summary>
    /// Carries out a decided resident change, after the caller released <c>_gate</c> and the init lock
    /// it decided under: the notification for the status the decision wrote and, for a rebuild, the
    /// initialization it reserved. Returns whether a change was decided at all: the caller then has no
    /// interrupted initialization left to restart.
    /// </summary>
    private bool ApplyResidentChange(ResidentChange change)
    {
        switch (change.Kind)
        {
            case ResidentChangeKind.Invalidate:
                if (change.StatusChanged)
                {
                    RaiseStatusChanged();
                }

                _log.LogInformation("Cleanup paused: its Foundry Local model is no longer resident.");
                return true;

            case ResidentChangeKind.Rebuild when change.Reservation is { } reservation:
                TryCancel(reservation.Superseded);
                if (change.StatusChanged)
                {
                    RaiseStatusChanged();
                }

                // Started even when a load has cancelled it since the decision: it observes its token and
                // returns its lease, and the load that cancelled it owns what happens next.
                _log.LogInformation("Rebuilding cleanup agent after its Foundry Local model was reloaded.");
                StartInitialization(change.Options!, reservation);
                return true;

            default:
                return false;
        }
    }

    private async Task InitializeAsync(
        CleanupOptions options, long generation, CancellationToken configureToken, CleanupOperationTracker.Lease lease)
    {
        using var ownership = lease;
        var acquired = false;
        FoundryModelIdentity? inUse = null;
        try
        {
            // Superseded runs are tracked too: the lease keeps disposal waiting for this one even
            // after a newer Configure cancelled it, until it has actually stopped using anything.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(configureToken, _lifetime.Token);
            var ct = linked.Token;

            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            lock (_gate)
            {
                _initWriter = generation;
            }

            ct.ThrowIfCancellationRequested();

            /*
             * A Copilot session belongs to the Copilot provider and nothing else.
             *
             * Without this, a user who tried Copilot and then switched to Foundry Local kept the
             * `copilot` child process alive for the rest of the session: the only other disposal site
             * is inside InitGitHubCopilotAsync, which runs when Copilot is selected AGAIN, and
             * ownsClient is false so the agent never claims it either.
             */
            if (options.Provider != CleanupProvider.GitHubCopilot)
            {
                await ReleaseCopilotSessionAsync().ConfigureAwait(false);
            }

            _pendingFoundryInUse = null;
            AIAgent? agent;
            try
            {
                agent = ProviderFactoryForTesting is { } testFactory
                    ? await InitFromTestFactoryAsync(testFactory, options, ct).ConfigureAwait(false)
                    : options.Provider switch
                    {
                        CleanupProvider.AzureFoundry => await InitAzureAsync(options, ct).ConfigureAwait(false),
                        CleanupProvider.OpenAiCompatible => await InitOpenAiCompatibleAsync(options, ct).ConfigureAwait(false),
                        CleanupProvider.GitHubCopilot => await InitGitHubCopilotAsync(options, ct).ConfigureAwait(false),
                        _ => await InitFoundryAsync(options, ct).ConfigureAwait(false),
                    };
            }
            catch (Exception ex) when (options.Provider == CleanupProvider.FoundryLocal &&
                                       IsExecutionProviderUnavailable(ex))
            {
                if (await TryDemoteFoundryLoadFailureAsync(options, ex, ct).ConfigureAwait(false) is { } demotion)
                {
                    agent = demotion.Agent;
                    options = demotion.Options;
                }
                else
                {
                    var message = await DescribeFoundryExecutionProviderFailureAsync(options.FoundryModelAlias, ex, ct)
                        .ConfigureAwait(false);
                    _log.LogWarning(
                        "Foundry Local model {Alias} could not load because an execution provider is unavailable ({Failure}).",
                        options.FoundryModelAlias,
                        DescribeFailureShape(ex));
                    SetInitStatus(CleanupStatus.Unavailable, message);
                    return;
                }
            }

            if (agent is null)
            {
                // A sub-initializer already published an Unavailable status with a useful reason.
                return;
            }

            if (await ProbeAgentAsync(agent, options, ct).ConfigureAwait(false) is { } probeFailure)
            {
                if (await TryDemoteFoundryGpuAsync(options, probeFailure, ct).ConfigureAwait(false) is { } demotion)
                {
                    agent = demotion.Agent;
                    options = demotion.Options;
                }
                else if (await TryAzureChatCompletionsAsync(options, probeFailure, ct).ConfigureAwait(false)
                    is { } viaChat)
                {
                    agent = viaChat;
                }
                else
                {
                    lock (_gate)
                    {
                        if (_operations.IsClosed || !_options.Enabled || _options != options)
                        {
                            return;
                        }

                        DropAgents();
                        _pendingFactory = null;
                    }

                    if (probeFailure.Exception is { } ex)
                    {
                        LogProviderFailure(LogLevel.Warning, options.Provider, ex, "AI cleanup initialization probe failed.");
                    }
                    else
                    {
                        _log.LogWarning("AI cleanup initialization probe failed ({Provider}): {Reason}",
                            options.Provider, probeFailure.Reason.Diagnostic);
                    }

                    SetInitStatus(CleanupStatus.Unavailable, probeFailure.Reason);
                    return;
                }
            }

            lock (_gate)
            {
                // A newer Configure (different provider/model, or disabled) may have superseded this
                // run, and disposal may have started: neither may be handed a freshly built agent.
                if (_operations.IsClosed || !_options.Enabled || _options != options)
                {
                    return;
                }

                _options = options;
                _agent = agent;
                _agentFactory = _pendingFactory;
                _styleAgents.Clear();
                _foundryInUse = options.Provider == CleanupProvider.FoundryLocal ? _pendingFoundryInUse : null;
                inUse = _foundryInUse;
            }

            // The selected model is in use, so a pending model switch can now delete the model it
            // replaced. Scheduled before Ready is published, so anything that observes Ready also
            // sees the pass; the pass itself waits for the init lock this run still holds.
            if (inUse is { } model)
            {
                ScheduleKeepOnlySelected(options, model);
            }

            SetInitStatus(CleanupStatus.Ready, ReadyReason(options));
            _log.LogInformation("AI cleanup ready ({Provider}).", options.Provider);
        }
        catch (Exception ex) when (configureToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            // Cancelled, so whoever cancelled owns the status from here: a newer Configure (or the
            // feature being switched off), disposal, or a manual model load whose restart of this
            // generation is pending. Nothing is published, whatever shape the cancellation surfaced
            // as: Foundry Local runs a native load to completion once it has started, whatever the
            // token says, and reports that load's own error as a FoundryLocalException.
            if (ex is not OperationCanceledException)
            {
                LogProviderFailure(LogLevel.Debug, options.Provider, ex, "AI cleanup initialization stopped after it was cancelled.");
            }
        }
        catch (Exception ex)
        {
            // Includes an OperationCanceledException neither token caused, such as a client's own
            // timeout. That used to be read as "superseded" and left the status on Initializing or
            // Downloading with nothing left to finish it, so every dictation skipped cleanup without
            // saying why. Nothing newer owns the status here, so this is the only way it ends. Written
            // for this run's own generation rather than through SetInitStatus, because it may have
            // failed before it ever held the lock; a save that took over just before cancelling this
            // run refuses it.
            LogProviderFailure(LogLevel.Warning, options.Provider, ex, "AI cleanup initialization failed.");
            PublishStatus(
                generation,
                CleanupStatus.Unavailable,
                CleanupReason.Same("AI cleanup could not start. Dictation continues with raw text."));
        }
        finally
        {
            lock (_gate)
            {
                // Finished, however it finished. An interrupted run stays marked for the restart that
                // is pending on it, and a superseded one no longer owns anything to clear. Cleared
                // before the init lock is released, so an explicit load that takes the lock next
                // never mistakes this run for one still going to publish.
                if (_initGeneration == generation && _initPhase == InitPhase.Live)
                {
                    _initPhase = InitPhase.Idle;
                }

                // Only a run that took the lock was the writer; one that never got it must not clear
                // the run that did.
                if (acquired)
                {
                    _initWriter = 0;
                }
            }

            if (acquired)
            {
                _initLock.Release();
            }
        }
    }

    private sealed record AgentProbeFailure(CleanupReason Reason, Exception? Exception);

    // See ProviderFactoryForTesting. Hands over a factory exactly the way the real initializers do.
    private async Task<AIAgent?> InitFromTestFactoryAsync(
        Func<CleanupOptions, CancellationToken, Task<Func<string, AIAgent>>> build, CleanupOptions options, CancellationToken ct)
    {
        _pendingFactory = await build(options, ct).ConfigureAwait(false);
        return _pendingFactory(BuildSystemPrompt(options));
    }

    /*
     * Second surface: Chat Completions, for a deployment the Responses API will not serve.
     *
     * Not every Foundry model speaks Responses. MAI-Thinking-1 is documented as "API type: Chat
     * completions" and answers a Responses call with HTTP 400 "The requested operation is
     * unsupported", which arrives as a validation failure and leaves cleanup switched off with no
     * route to the model the owner deliberately deployed.
     *
     * Measured against all three deployments before this was written, because the fix had to be a
     * surface the whole set shares rather than a special case for one model:
     *
     *   POST /openai/v1/chat/completions   MAI-Thinking-1  OK    gpt-5.6-sol  OK   grok-4.6  OK
     *   POST /openai/v1/responses          MAI-Thinking-1  400   gpt-5.6-sol  OK   grok-4.6  OK
     *
     * So Chat Completions on the unified v1 path is the common denominator. It is the fallback and
     * not the default because Responses is the forward-looking surface and is the only one that
     * serves the newest reasoning models: the comment on the Responses client records that Chat
     * Completions answers gpt-5.x "pro" and the o-series with the same "operation unsupported" 400
     * in the other direction. Trying Responses first and stepping down keeps both halves working.
     *
     * Detected rather than configured. The deployment capability map does carry the answer
     * (MAI-Thinking-1 reports chatCompletion=true and no responses key), but reading it here would
     * mean persisting a second copy of it into settings and trusting it to still be true; the model
     * itself is never out of date about what it serves.
     */
    private async Task<AIAgent?> TryAzureChatCompletionsAsync(
        CleanupOptions options, AgentProbeFailure failure, CancellationToken ct)
    {
        if (options.Provider != CleanupProvider.AzureFoundry ||
            string.IsNullOrWhiteSpace(options.AzureEndpoint) ||
            string.IsNullOrWhiteSpace(options.AzureDeployment))
        {
            return null;
        }

        // Only for the refusal that means "wrong surface". A timeout, a 500 or an auth failure says
        // nothing about which API the deployment speaks, and retrying those here would turn one slow
        // failure into two.
        if (!IsSurfaceRejection(failure.Exception))
        {
            return null;
        }

        try
        {
            var endpointUri = new Uri(options.AzureEndpoint!);
            var accountHost = new Uri($"{endpointUri.Scheme}://{endpointUri.Authority}/");
            var useKey = !string.IsNullOrWhiteSpace(options.AzureApiKey);

            var openAiClient = useKey
                ? AzureOpenAIResponsesClientFactory.CreateClientWithApiKey(accountHost, options.AzureApiKey!)
                : AzureOpenAIResponsesClientFactory.CreateClientWithTokenCredential(
                    accountHost,
                    AzureCredentialFactory.Create(new AzureCredentialRequest(
                        options.AzureAuthMode,
                        options.AzureTenantId,
                        options.AzureSubscriptionId,
                        options.AzureClientId,
                        options.AzureClientSecret)));

            var deployment = openAiClient.GetChatClient(options.AzureDeployment!);
            // Carries the same wrapper as the Responses path; on this surface it keeps "store" unset,
            // never true (WithStoredOutputDisabled explains why the field is not sent here).
            _pendingFactory = i => CreateAzureChatCompletionsAgent(deployment, i);
            var agent = _pendingFactory(BuildSystemPrompt(options));

            if (await ProbeAgentAsync(agent, options, ct).ConfigureAwait(false) is { } stillFailing)
            {
                // Deployment names are the user's resource names; the log gets the failure's shape.
                _log.LogDebug(
                    "Chat Completions fallback also failed: {Failure}",
                    stillFailing.Exception is { } fallbackEx
                        ? DescribeFailureShape(fallbackEx)
                        : stillFailing.Reason.Diagnostic);
                return null;
            }

            _log.LogInformation("The Azure deployment does not serve the Responses API; using Chat Completions.");
            return agent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug("Could not build a Chat Completions agent ({Failure}).", DescribeFailureShape(ex));
            return null;
        }
    }

    /// <summary>
    /// True for a 400 from the Responses surface, which is the signal to try Chat Completions.
    /// </summary>
    /// <remarks>
    /// Deliberately any 400 rather than only the ones whose text says "unsupported". Azure phrases
    /// this refusal at least two ways for the same deployment: MAI-Thinking-1 answered "The
    /// requested operation is unsupported" through one path and "There was an issue with your
    /// request. Please check your inputs and try again" through another, and a matcher keyed to the
    /// first wording silently stopped firing when the second one arrived. Matching on the status
    /// alone cannot be broken by rewording.
    /// <para>
    /// Widening it costs nothing, because this is not the decision. A 400 only buys ONE attempt on
    /// the other surface, that attempt is validated by the same probe, and a failure there returns
    /// null and leaves the original diagnostic standing. Statuses that say nothing about the surface
    /// (a timeout, a 401, a 429, a 500) are still excluded, because retrying those here would turn
    /// one slow failure into two.
    /// </para>
    /// </remarks>
    private static bool IsSurfaceRejection(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is ClientResultException { Status: 400 })
            {
                return true;
            }
        }

        return false;
    }

    private sealed record FoundryDemotionResult(CleanupOptions Options, AIAgent Agent);

    private sealed record FoundryExecutionProviderFailure(
        string? RequiredProvider,
        IReadOnlyList<string> AvailableProviders);

    private static IChatClient DisableStoredOutput(IChatClient client) =>
        new StoredOutputDisabledChatClient(client);

    /// <summary>
    /// The Azure Chat Completions fallback's agent, exactly as <see cref="TryAzureChatCompletionsAsync"/>
    /// builds it: every call goes through the stored-output wrapper, which on this surface keeps
    /// "store" unset and never true.
    /// </summary>
    internal static AIAgent CreateAzureChatCompletionsAgent(OpenAI.Chat.ChatClient deployment, string instructions) =>
        deployment.AsAIAgent(instructions: instructions, name: AgentName, clientFactory: DisableStoredOutput);

#pragma warning disable OPENAI001
    /// <summary>
    /// The Azure Responses agent, exactly as <see cref="InitAzureAsync"/> builds it: every call carries
    /// the stored-output control.
    /// </summary>
    internal static AIAgent CreateAzureResponsesAgent(ResponsesClient responses, string deployment, string instructions) =>
        responses.AsAIAgent(model: deployment, instructions: instructions, name: AgentName, clientFactory: DisableStoredOutput);
#pragma warning restore OPENAI001

    internal static ChatOptions WithStoredOutputDisabled(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        var innerFactory = clone.RawRepresentationFactory;
        clone.RawRepresentationFactory = client =>
        {
            var raw = innerFactory?.Invoke(client);
#pragma warning disable OPENAI001
            /*
             * Chosen by the client that asks, not by what the factory happened to return.
             *
             * Each surface honours exactly one options type and silently replaces anything else with
             * a fresh instance (Microsoft.Extensions.AI.OpenAI 10.9.0: OpenAIChatClient keeps only a
             * ChatCompletionOptions, OpenAIResponsesChatClient only a CreateResponseOptions).
             *
             * Chat Completions never sets "store", and never lets it be true. Azure stores a chat
             * completion only when store is true ("set the store parameter to True",
             * learn.microsoft.com/azure/foundry-classic/openai/how-to/stored-completions), so omitting it
             * is already the non-storing default. That fallback exists for deployments such as
             * MAI-Thinking-1, measured working without the field, and some non-OpenAI deployments
             * reject parameters they do not know, so sending store=false there would risk breaking
             * cleanup for no privacy gain. This keeps the wire shape Scribe has always sent, but on
             * purpose rather than because the wrong options type happened to be discarded.
             */
            if (Exposes(client, typeof(OpenAI.Chat.ChatClient)))
            {
                var chatOptions = raw as OpenAI.Chat.ChatCompletionOptions ?? new OpenAI.Chat.ChatCompletionOptions();
                chatOptions.StoredOutputEnabled = null;
                return chatOptions;
            }

            // Responses defaults to store=true on Azure, so this surface is always told false.
            if (Exposes(client, typeof(ResponsesClient)))
            {
                var responses = raw as CreateResponseOptions ?? new CreateResponseOptions();
                responses.StoredOutputEnabled = false;
                return responses;
            }

            // A client that names neither surface: keep the flag on whichever options type it was
            // given.
            if (raw is CreateResponseOptions responseOptions)
            {
                responseOptions.StoredOutputEnabled = false;
                return responseOptions;
            }

            if (raw is OpenAI.Chat.ChatCompletionOptions otherChatOptions)
            {
                otherChatOptions.StoredOutputEnabled = false;
                return otherChatOptions;
            }

            // Fail CLOSED. Passing an unrecognised object through would let Azure fall back to its
            // store=true default and silently retain dictated text, which is the exact outcome this
            // exists to prevent. Nothing in Scribe sets a factory today, so this only triggers if a
            // future package version starts supplying one, and a privacy control must not lapse on a
            // dependency bump. The same holds above: an unrecognised object on a known surface is
            // replaced by that surface's own options with the flag off.
            return new CreateResponseOptions { StoredOutputEnabled = false };
#pragma warning restore OPENAI001
        };
        return clone;
    }

    // Whether the calling client is backed by the given OpenAI client type, which is how the
    // Microsoft.Extensions.AI adapters expose their surface (IChatClient.GetService). Never throws.
    private static bool Exposes(IChatClient client, Type serviceType)
    {
        try
        {
            return client.GetService(serviceType) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class StoredOutputDisabledChatClient(IChatClient inner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResponseAsync(messages, WithStoredOutputDisabled(options), cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, WithStoredOutputDisabled(options), cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            if (inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

    }

    private async Task<AgentProbeFailure?> ProbeAgentAsync(AIAgent agent, CleanupOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CleanupTimeoutOverride ?? TimeSpan.FromSeconds(ProbeTimeoutSecondsFor(options)));

        try
        {
            var chatOptions = new ChatOptions
            {
                /*
                 * Keyed on the provider that needs it, not on "anything but local".
                 *
                 * The predicate was `!= FoundryLocal`, which swept in OpenAiCompatible as well. That
                 * provider's users are running Ollama and LM Studio against small local models, and
                 * reserving 4096 output tokens on a short context can get the probe rejected outright,
                 * marking cleanup Unavailable on a model that would have worked. The headroom is for
                 * Azure reasoning deployments and for the Copilot backend, which are the ones measured
                 * to need it.
                 */
                MaxOutputTokens = options.Provider is CleanupProvider.AzureFoundry
                    or CleanupProvider.GitHubCopilot
                    ? CloudInitProbeMaxOutputTokens
                    : InitProbeMaxOutputTokens,
            };
            if (options.Provider == CleanupProvider.FoundryLocal)
            {
                chatOptions.Temperature = CleanupTemperature;
            }

            /*
             * The probe reasons the way a real cleanup call will.
             *
             * Deliberately NOT pinned to a low effort to make the probe cheap.
             * The probe exists to answer "will this deployment serve cleanup",
             * and a probe that succeeds at an effort the cleanup path never uses
             * answers a different question: a model that passes validation and
             * then times out on the owner's first dictation is worse than one
             * that refuses at setup, because the failure has moved to where he
             * is not looking. Whatever the model's own default is, it is what
             * both calls get.
             */
            if (ReasoningEffortOverride is { } probeEffort)
            {
                chatOptions.Reasoning = new ReasoningOptions
                {
                    Effort = probeEffort,
                    Output = ReasoningOutput.None,
                };
            }

            var runOptions = new ChatClientAgentRunOptions(chatOptions);
            _ = await agent.RunAsync(BuildUserMessage("ok"), options: runOptions, cancellationToken: cts.Token)
                .ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return new AgentProbeFailure(CleanupReason.Same("AI cleanup validation timed out."), ex);
        }
        catch (Exception ex)
        {
            return new AgentProbeFailure(DescribeFailureReason(ex, options.Provider), ex);
        }
    }

    /// <summary>
    /// How long the readiness probe waits for a first response.
    /// <para>
    /// An on-device model pays its warm-up on this very first call, and that cost scales with the
    /// model. A 14B model on the CPU took longer than the cloud budget and was declared unavailable
    /// even though it went on to work, so a large local model gets the longer allowance rather than
    /// having a cloud-shaped timeout applied to it.
    /// </para>
    /// </summary>
    private static int ProbeTimeoutSecondsFor(CleanupOptions options) =>
        options.Provider == CleanupProvider.FoundryLocal
            ? LocalInitProbeTimeoutSeconds
            : InitProbeTimeoutSeconds;

    /// <summary>
    /// The GPU alias to demote from, or null when nothing GPU-shaped is in play. The configured
    /// alias is usually a family name like "qwen3-1.7b" that Foundry resolves to a hardware variant
    /// such as "qwen3-1.7b-generic-gpu:2", so the resolved id has to be consulted first. Checking
    /// only the configured alias silently disables demotion for every curated model, which is all of
    /// them.
    /// </summary>
    private async Task<string?> ResolveGpuSourceAliasAsync(CleanupOptions options, CancellationToken ct)
    {
        if (_catalog is null)
        {
            return null;
        }

        string? resolvedId = null;
        try
        {
            var selectedModel = await _catalog.GetModelAsync(options.FoundryModelAlias, ct).ConfigureAwait(false);
            resolvedId = selectedModel?.Id;
        }
        catch (Exception ex)
        {
            // The catalog read is a lookup for a better alias, never a reason to abandon a demotion
            // that could still succeed from the configured name.
            _log.LogDebug(
                "Could not resolve the Foundry Local variant id for {Alias} ({Failure}).",
                options.FoundryModelAlias,
                DescribeFailureShape(ex));
        }

        if (FoundryModelVariant.IsGpuAlias(resolvedId))
        {
            return resolvedId;
        }

        return FoundryModelVariant.IsGpuAlias(options.FoundryModelAlias) ? options.FoundryModelAlias : null;
    }

    private async Task<FoundryDemotionResult?> TryDemoteFoundryLoadFailureAsync(
        CleanupOptions options,
        Exception loadFailure,
        CancellationToken ct)
    {
        if (_catalog is null)
        {
            return null;
        }

        var sourceAlias = await ResolveGpuSourceAliasAsync(options, ct).ConfigureAwait(false);
        if (sourceAlias is null)
        {
            return null;
        }

        var catalogModels = await _catalog.ListModelsAsync(ct).ConfigureAwait(false);
        var cpuAlias = FoundryModelVariant.ResolveCpuCounterpartAlias(
            sourceAlias,
            BuildVariantCandidates(catalogModels));
        if (cpuAlias is null)
        {
            _log.LogWarning(
                "Foundry Local GPU model {GpuAlias} could not load, but no CPU counterpart was found in the catalog ({Failure}).",
                sourceAlias,
                DescribeFailureShape(loadFailure));
            return null;
        }

        var demotedOptions = options with { FoundryModelAlias = cpuAlias };
        _log.LogWarning(
            "Foundry Local GPU model {GpuAlias} could not load because an execution provider is unavailable ({Failure}). Retrying once with CPU model {CpuAlias}.",
            sourceAlias,
            DescribeFailureShape(loadFailure),
            cpuAlias);

        AIAgent? agent;
        try
        {
            agent = await InitFoundryAsync(demotedOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Foundry Local CPU demotion load failed for {CpuAlias} ({Failure}).", cpuAlias, DescribeFailureShape(ex));
            return null;
        }

        if (agent is null)
        {
            return null;
        }

        RememberFoundryDemotion(options.FoundryModelAlias, cpuAlias);
        lock (_gate)
        {
            if (_options != options)
            {
                return null;
            }

            _options = demotedOptions;
        }

        return new FoundryDemotionResult(demotedOptions, agent);
    }

    private async Task<FoundryDemotionResult?> TryDemoteFoundryGpuAsync(
        CleanupOptions options,
        AgentProbeFailure probeFailure,
        CancellationToken ct)
    {
        if (options.Provider != CleanupProvider.FoundryLocal ||
            probeFailure.Exception is null ||
            !IsGpuShaderIncompatibility(probeFailure.Exception) ||
            _catalog is null)
        {
            return null;
        }

        var sourceAlias = await ResolveGpuSourceAliasAsync(options, ct).ConfigureAwait(false);
        if (sourceAlias is null)
        {
            return null;
        }

        var catalogModels = await _catalog.ListModelsAsync(ct).ConfigureAwait(false);
        var cpuAlias = FoundryModelVariant.ResolveCpuCounterpartAlias(
            sourceAlias,
            BuildVariantCandidates(catalogModels));
        if (cpuAlias is null)
        {
            _log.LogWarning(
                "Foundry Local GPU model {GpuAlias} failed the shader probe, but no CPU counterpart was found in the catalog.",
                sourceAlias);
            return null;
        }

        var demotedOptions = options with { FoundryModelAlias = cpuAlias };
        _log.LogWarning(
            "Foundry Local GPU model {GpuAlias} failed the shader probe ({Failure}). Retrying once with CPU model {CpuAlias}.",
            sourceAlias,
            DescribeFailureShape(probeFailure.Exception),
            cpuAlias);

        var agent = await InitFoundryAsync(demotedOptions, ct).ConfigureAwait(false);
        if (agent is null)
        {
            return null;
        }

        if (await ProbeAgentAsync(agent, demotedOptions, ct).ConfigureAwait(false) is { } cpuFailure)
        {
            // Report the CPU failure, not the original GPU one. Falling back through here and then
            // telling the user to "pick a CPU variant" would be advising the exact action that just
            // failed in front of them.
            if (cpuFailure.Exception is { } ex)
            {
                _log.LogWarning(
                    "Foundry Local CPU demotion probe failed for {CpuAlias} after {GpuAlias} failed the shader probe ({Failure}).",
                    cpuAlias, sourceAlias, DescribeFailureShape(ex));
            }
            else
            {
                _log.LogWarning(
                    "Foundry Local CPU demotion probe failed for {CpuAlias} after {GpuAlias} failed the shader probe: {Reason}",
                    cpuAlias, sourceAlias, cpuFailure.Reason.Diagnostic);
            }

            const string neither = "Neither the GPU nor the CPU build of this model would run. Pick a different model in Settings.";
            SetInitStatus(CleanupStatus.Unavailable, new CleanupReason(
                $"{neither} {cpuFailure.Reason.Diagnostic}".TrimEnd(),
                $"{neither} {cpuFailure.Reason.Display}".TrimEnd()));
            return null;
        }

        RememberFoundryDemotion(options.FoundryModelAlias, cpuAlias);
        lock (_gate)
        {
            if (_options != options)
            {
                return null;
            }

            _options = demotedOptions;
        }

        return new FoundryDemotionResult(demotedOptions, agent);
    }

    private static IEnumerable<FoundryModelVariantCandidate> BuildVariantCandidates(IEnumerable<IModel> models)
    {
        foreach (var model in models)
        {
            yield return new FoundryModelVariantCandidate(model.Id, model.Info?.Runtime?.ExecutionProvider);
            yield return new FoundryModelVariantCandidate(model.Alias, model.Info?.Runtime?.ExecutionProvider);

            foreach (var variant in model.Variants)
            {
                yield return new FoundryModelVariantCandidate(variant.Id, variant.Info?.Runtime?.ExecutionProvider);
                yield return new FoundryModelVariantCandidate(variant.Alias, variant.Info?.Runtime?.ExecutionProvider);
            }
        }
    }

    private async Task<string> DescribeFoundryExecutionProviderFailureAsync(
        string alias,
        Exception ex,
        CancellationToken ct)
    {
        var failure = await GetFoundryExecutionProviderFailureAsync(alias, ex, ct).ConfigureAwait(false);
        var required = string.IsNullOrWhiteSpace(failure.RequiredProvider)
            ? "an execution provider that is not available"
            : failure.RequiredProvider;
        var available = failure.AvailableProviders.Count == 0
            ? "none reported"
            : string.Join(", ", failure.AvailableProviders);

        return $"Model '{alias}' requires {required}, but this PC has {available}. Pick a different model in Settings.";
    }

    private async Task<FoundryExecutionProviderFailure> GetFoundryExecutionProviderFailureAsync(
        string alias,
        Exception ex,
        CancellationToken ct)
    {
        string? required = null;
        if (_catalog is not null)
        {
            try
            {
                var model = await _catalog.GetModelAsync(alias, ct).ConfigureAwait(false);
                required = model?.Info?.Runtime?.ExecutionProvider;
            }
            catch (Exception lookupEx)
            {
                _log.LogDebug("Could not read Foundry model runtime metadata for {Alias} ({Failure}).", alias, DescribeFailureShape(lookupEx));
            }
        }

        // Identifiers only: this becomes the status reason, which the dictation pipeline logs, so a
        // name parsed out of an error message must not carry any of the message with it.
        required = CleanupFailureShape.SanitizeCode(required) ?? CleanupFailureShape.SanitizeCode(TryParseRequiredExecutionProvider(ex));
        var available = _availableExecutionProviders.Length > 0
            ? _availableExecutionProviders
            : TryParseAvailableExecutionProviders(ex)
                .Select(CleanupFailureShape.SanitizeCode)
                .OfType<string>()
                .ToArray();

        return new FoundryExecutionProviderFailure(required, available);
    }

    private async Task<AIAgent?> InitFoundryAsync(CleanupOptions options, CancellationToken ct)
    {
        var alias = options.FoundryModelAlias;
        SetInitStatus(CleanupStatus.Initializing, "Starting Foundry Local…");
        await EnsureManagerAsync(ct).ConfigureAwait(false);

        if (_catalog is null || _openAiClient is null)
        {
            SetInitStatus(CleanupStatus.Unavailable, "Foundry Local could not be initialized.");
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var model = await ResolveFoundryModelAsync(alias, ct).ConfigureAwait(false);
        if (model is null)
        {
            // A persisted demotion points at an exact variant that a Foundry Local update can
            // retire. Without this the marker would pin cleanup to a model that no longer exists
            // and the user would have no way back short of deleting a JSON file they never knew
            // about, so a missing demotion target forgets itself and retries the original.
            if (ForgetFoundryDemotionTarget(alias) is { } restoredAlias)
            {
                _log.LogWarning(
                    "The demoted Foundry Local model {MissingAlias} is no longer in the catalog. Clearing the demotion and retrying {OriginalAlias}.",
                    alias,
                    restoredAlias);
                return await InitFoundryAsync(options with { FoundryModelAlias = restoredAlias }, ct)
                    .ConfigureAwait(false);
            }

            SetInitStatus(CleanupStatus.Unavailable, $"Model '{alias}' was not found in the Foundry catalog.");
            return null;
        }
        _log.LogInformation("Foundry Local cleanup model resolved to {ModelId}.", model.Id);

        var cached = await model.IsCachedAsync(ct).ConfigureAwait(false);
        if (!cached)
        {
            _lastReportedPct = -1;
            SetInitStatus(CleanupStatus.Downloading, $"Downloading {alias}…");
            await model.DownloadAsync(progress => OnDownloadProgress(alias, progress), ct).ConfigureAwait(false);
        }

        // Keep only one model resident: unload any previously-loaded model before loading this one.
        await UnloadOtherFoundryModelsAsync(model.Id, model.Alias, ct).ConfigureAwait(false);

        SetInitStatus(CleanupStatus.Downloading, $"Loading {alias}…");
        await model.LoadAsync(ct).ConfigureAwait(false);

        // Present the on-device OpenAI-compatible chat client as an Agent Framework agent so the
        // cleanup call site is identical to the Azure path.
        var chatClient = _openAiClient.GetChatClient(model.Id);
        _pendingFactory = instructions => chatClient.AsAIAgent(instructions: instructions, name: AgentName);
        _pendingFoundryInUse = new FoundryModelIdentity(model.Id, model.Alias);
        return _pendingFactory(BuildSystemPrompt(options));
    }

    private async Task<IModel?> ResolveFoundryModelAsync(string alias, CancellationToken ct)
    {
        if (_catalog is null)
        {
            return null;
        }

        var model = await _catalog.GetModelAsync(alias, ct).ConfigureAwait(false);
        if (model is not null && !string.IsNullOrWhiteSpace(model.Id))
        {
            SelectRunnableVariant(model, alias);
            return model;
        }

        var catalogModels = await _catalog.ListModelsAsync(ct).ConfigureAwait(false);
        foreach (var parent in catalogModels)
        {
            foreach (var variant in parent.Variants)
            {
                if (string.Equals(variant.Id, alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(variant.Alias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    // An exact variant id is an explicit user choice, so it is honoured as-is even
                    // if its execution provider looks unavailable. Overriding a name the user typed
                    // or picked from the list would be the same silent substitution this method
                    // exists to correct.
                    parent.SelectVariant(variant);
                    return parent;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Repoints a curated family alias at a variant this PC can actually run.
    /// <para>
    /// Foundry Local normally selects the variant itself, but that choice can name an execution
    /// provider the machine does not have: an RTX box reporting
    /// [CPU, WebGpu, NvTensorRTRTX] was handed <c>qwen3-1.7b-cuda-gpu</c>, which fails to load every
    /// time. Because Scribe's default is a family alias, that left first-run cleanup dead while the
    /// same model loaded fine once the user picked a concrete variant by hand.
    /// </para>
    /// <para>
    /// This filters, it never reorders: the SDK's own variant order is preserved and only entries
    /// whose provider is positively known to be missing are skipped, so the SDK keeps making the
    /// choice wherever it can. A variant that does not report a provider is left alone, since
    /// absence of evidence is not evidence the variant is broken.
    /// </para>
    /// </summary>
    private void SelectRunnableVariant(IModel model, string requestedAlias)
    {
        try
        {
            var variants = model.Variants;
            if (variants is null || variants.Count == 0)
            {
                return;
            }

            var selected = model.Info?.Runtime?.ExecutionProvider;
            if (string.IsNullOrWhiteSpace(selected) || IsExecutionProviderAvailable(selected))
            {
                return;
            }

            var runnable = variants.FirstOrDefault(variant =>
                IsExecutionProviderAvailable(variant?.Info?.Runtime?.ExecutionProvider));
            if (runnable is null)
            {
                _log.LogWarning(
                    "Foundry Local selected {SelectedProvider} for {Alias}, which this PC does not have ({Available}), and no variant reported an available provider.",
                    selected,
                    requestedAlias,
                    string.Join(", ", _availableExecutionProviders));
                return;
            }

            model.SelectVariant(runnable);
            _log.LogInformation(
                "Foundry Local selected {SelectedProvider} for {Alias}, which this PC does not have ({Available}). Using variant {VariantId} on {VariantProvider} instead.",
                selected,
                requestedAlias,
                string.Join(", ", _availableExecutionProviders),
                runnable.Id,
                runnable.Info?.Runtime?.ExecutionProvider);
        }
        catch (Exception ex)
        {
            // Variant inspection is an optimization over the SDK's own choice. If it throws, the
            // original selection is still there to attempt, and a real load failure reports a far
            // better diagnostic than an exception thrown while trying to avoid one.
            _log.LogDebug(
                "Could not check Foundry Local variant compatibility for {Alias} ({Failure}).",
                requestedAlias,
                DescribeFailureShape(ex));
        }
    }

    private bool IsExecutionProviderAvailable(string? executionProvider)
    {
        var provider = executionProvider?.Trim();
        return !string.IsNullOrEmpty(provider) &&
            _availableExecutionProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
    }

    /*
     * Cleanup through the user's own GitHub Copilot licence.
     *
     * ## Why the types only appear inside this method
     *
     * Every reference to CopilotClient is local to this body, or to GitHubCopilotAgentFactory, which
     * only this body calls, and that is load-bearing rather than
     * tidiness. The CLR resolves an assembly the first time a method that references it is JIT
     * compiled, so a user who never selects this provider never loads
     * Microsoft.Agents.AI.GitHub.Copilot at all: no startup cost, no memory cost, and no Copilot
     * runtime touched. Hoisting the client into a field, or naming the type in a signature on a
     * class built during startup, would pull the assembly in on every launch and quietly undo that.
     *
     * ## Why the CLI is checked here as well as in Settings
     *
     * Settings checks it to decide what to offer. This checks it because the answer can change
     * between the two: the CLI can be uninstalled, or a settings file can arrive from a machine that
     * had it. Failing here with the real reason is what turns "cleanup silently did nothing" into a
     * status line naming the missing dependency.
     *
     * ## What this deliberately does not enable
     *
     * The Copilot backend is a coding agent: shell execution, file access and URL fetching are all
     * in its runtime. They are off unless a SessionConfig supplies an OnPermissionRequest handler,
     * and none is supplied here and none should be. Scribe is asking it to punctuate a sentence.
     *
     * ## How the model is chosen
     *
     * Through SessionConfig.Model, the typed SDK surface, which the pinned SDK forwards into every
     * create-session request (GitHubCopilotAgentFactory). The previous approach published the choice
     * in a process-wide environment variable around client startup and restored it afterwards; a
     * cancelled startup skipped the restore, and anything else in the process could read it in the
     * meantime. The runtime's child environment is now built explicitly instead (see
     * GitHubCopilotCli.BuildRuntimeEnvironment), so the CLI sees exactly what it saw before, a blank
     * model still means "not set" rather than an inherited ambient value, and this process's own
     * environment is never touched.
     */
    private async Task<AIAgent?> InitGitHubCopilotAsync(CleanupOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Cancellation reaches the version probe, which kills the child it started rather than
        // leaving it for the probe's own deadline.
        var cli = await GitHubCopilotCli.DetectAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!cli.Found)
        {
            SetInitStatus(
                CleanupStatus.Unavailable,
                "The GitHub Copilot CLI is not installed. Install it from Settings, then turn AI cleanup back on.");
            return null;
        }

        var model = string.IsNullOrWhiteSpace(options.CopilotModel) ? null : options.CopilotModel.Trim();

        /*
         * Initializing, not Downloading. Nothing is being downloaded: the CLI is already installed
         * and this is the handshake with it.
         *
         * The status text is not internal. It is interpolated into the line a skipped dictation shows
         * the user, so this read as "AI cleanup is enabled but Downloading (Starting the GitHub
         * Copilot session…)" on any dictation taken during startup, which is both wrong and alarming.
         * The startup is around 20 seconds, so the window this is visible in is not small.
         */
        SetInitStatus(CleanupStatus.Initializing, "Connecting to the GitHub Copilot CLI…");

        /*
         * Point the SDK at the CLI we found, rather than the one it expects to have bundled.
         *
         * With no Connection set, CopilotClient spawns
         * `<output>/runtimes/win-x64/native/copilot.exe`, the copy the build-time npm download would
         * have placed there. Directory.Build.props switches that download off, so the default throws
         * "Copilot runtime not found".
         *
         * ForStdio with the detected path is the better arrangement in any case: it runs the CLI the
         * owner actually installed and is signed in to, which is what makes this "bring your own
         * model" rather than a second, unauthenticated Copilot inside Scribe. UseLoggedInUser is left
         * at its default of true so the runtime picks up the stored OAuth token or `gh` auth.
         */
        var client = new GitHub.Copilot.CopilotClient(new GitHub.Copilot.CopilotClientOptions
        {
            Connection = GitHub.Copilot.RuntimeConnection.ForStdio(cli.Path!),
            Environment = GitHubCopilotCli.BuildRuntimeEnvironment(model),
        });
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeEx)
            {
                LogProviderFailure(LogLevel.Debug, CleanupProvider.GitHubCopilot, disposeEx,
                    "Could not dispose a GitHub Copilot client that failed to start.");
            }

            SetInitStatus(
                CleanupStatus.Unavailable,
                "Could not start GitHub Copilot. Check that you are signed in: run `copilot` once in a terminal.");
            LogProviderFailure(LogLevel.Warning, CleanupProvider.GitHubCopilot, ex, "GitHub Copilot session could not be started.");
            return null;
        }

        // Held so the session is torn down with the service (DisposeAsync) rather than leaked per
        // reconfiguration, and replaced here so switching model does not strand the old child process.
        // Published under _gate so a client started after disposal began is never adopted.
        object? previous = null;
        var adopted = false;
        lock (_gate)
        {
            if (!_operations.IsClosed)
            {
                previous = _copilotClientHandle;
                _copilotClientHandle = client;
                adopted = true;
            }
        }

        if (!adopted)
        {
            await DisposeCopilotSessionAsync(client).ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }

        await DisposeCopilotSessionAsync(previous).ConfigureAwait(false);

        // ownsClient stays false: the session is disposed through _copilotClientHandle, and letting
        // the agent own it too would double-dispose it every time the user changes a setting. Each
        // call builds a fresh SessionConfig, so no two per-style agents share a mutable instance.
        _pendingFactory = instructions => GitHubCopilotAgentFactory.Create(client, instructions, model, AgentName);
        return _pendingFactory(BuildSystemPrompt(options));
    }

    /// <summary>
    /// Bring-your-own-endpoint: any server speaking the OpenAI chat protocol (Ollama, LM Studio,
    /// vLLM, OpenRouter, or api.openai.com itself). The API key is optional because local servers
    /// don't check it; a placeholder is sent when blank, mirroring the Foundry Local client.
    /// </summary>
    private Task<AIAgent?> InitOpenAiCompatibleAsync(CleanupOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.CustomEndpoint) || string.IsNullOrWhiteSpace(options.CustomModel))
        {
            SetInitStatus(CleanupStatus.Unavailable, "Enter the endpoint URL and model name to enable cleanup.");
            return Task.FromResult<AIAgent?>(null);
        }

        if (!TryValidateCustomEndpoint(options.CustomEndpoint, out var endpointUri, out var endpointError))
        {
            SetInitStatus(CleanupStatus.Unavailable, endpointError);
            return Task.FromResult<AIAgent?>(null);
        }

        // The host is what makes this line useful in Settings, and it is exactly what must not reach
        // the log: a dictation skipped while connecting reports this status as its skip reason.
        SetInitStatus(CleanupStatus.Initializing, new CleanupReason(
            "Connecting to the custom endpoint…", $"Connecting to {endpointUri.Host}…"));

        var key = string.IsNullOrWhiteSpace(options.CustomApiKey) ? "not-needed" : options.CustomApiKey!;
        var clientOptions = new OpenAIClientOptions { Endpoint = endpointUri };
        OpenAIClientOptionsOverride?.Invoke(clientOptions);
        var client = new OpenAIClient(new ApiKeyCredential(key), clientOptions);
        var chatClient = client.GetChatClient(options.CustomModel!.Trim());
        _pendingFactory = instructions => chatClient.AsAIAgent(instructions: instructions, name: AgentName);
        return Task.FromResult<AIAgent?>(_pendingFactory(BuildSystemPrompt(options)));
    }

    private Task<AIAgent?> InitAzureAsync(CleanupOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.AzureEndpoint) || string.IsNullOrWhiteSpace(options.AzureDeployment))
        {
            SetInitStatus(CleanupStatus.Unavailable, "Choose an Azure deployment to enable cleanup.");
            return Task.FromResult<AIAgent?>(null);
        }

        if (!Uri.TryCreate(options.AzureEndpoint, UriKind.Absolute, out var endpointUri))
        {
            SetInitStatus(CleanupStatus.Unavailable, "The Azure endpoint is not a valid URL.");
            return Task.FromResult<AIAgent?>(null);
        }

        SetInitStatus(CleanupStatus.Initializing, new CleanupReason(
            "Connecting to the Azure deployment…", $"Connecting to Azure deployment '{options.AzureDeployment}'…"));

        var instructions = BuildSystemPrompt(options);
        var useKey = !string.IsNullOrWhiteSpace(options.AzureApiKey);
        var networkTimeout = CleanupTimeoutOverride is { } timeout
            ? timeout + TimeSpan.FromSeconds(5)
            : (TimeSpan?)null;

        // Cleanup needs model inference, not the project data plane. That route returned HTTP 500
        // for gpt-6-astra while the same model, credentials and request worked on /openai/v1/.
        // The factory normalizes either saved endpoint shape to the same resource's inference URL.
        _log.LogDebug("Azure cleanup uses account-level Responses inference.");
#pragma warning disable OPENAI001
        var responses = useKey
            ? AzureOpenAIResponsesClientFactory.CreateWithApiKey(
                endpointUri,
                options.AzureApiKey!,
                networkTimeout,
                DisableRetries)
            : AzureOpenAIResponsesClientFactory.CreateWithTokenCredential(
                endpointUri,
                AzureCredentialFactory.Create(new AzureCredentialRequest(
                    options.AzureAuthMode,
                    options.AzureTenantId,
                    options.AzureSubscriptionId,
                    options.AzureClientId,
                    options.AzureClientSecret)),
                networkTimeout,
                DisableRetries);
        _pendingFactory = i => CreateAzureResponsesAgent(responses, options.AzureDeployment!, i);
#pragma warning restore OPENAI001
        var agent = _pendingFactory(instructions);

        return Task.FromResult<AIAgent?>(agent);
    }

    /// <summary>
    /// Turns an Azure validation failure into a message that names the actual problem. This exists
    /// because a single generic string sent a user chasing <c>az login</c> for two days while the
    /// real fault was a 403: the right endpoint and deployment, but no data-plane role assignment.
    /// The HTTP status is the highest-signal thing we have, so it drives the message.
    /// </summary>
    internal static string DescribeAzureFailure(Exception ex, bool useKey, Settings.AzureAuthMode mode, string? deployment)
    {
        var status = ExtractHttpStatus(ex);
        var identity = useKey ? "The API key" : mode == Settings.AzureAuthMode.ServicePrincipal
            ? "The service principal"
            : "Your Azure CLI sign-in";

        return status switch
        {
            401 => $"Azure rejected the credentials (401). {identity} is not valid for this resource." +
                   (useKey ? " Check the key." : " Check the tenant, then re-authenticate."),

            // The distinction that matters most: reachable and authenticated, but not authorized.
            // Propagation is called out first because a freshly assigned role reads as a wrong role
            // for roughly ten minutes, which is longer than Azure's own documentation suggests.
            403 => $"Azure accepted the sign-in but denied access (403). {identity} can reach the resource " +
                   "yet is not authorized to call it. If you just assigned a role, wait about ten minutes: " +
                   "role assignments take longer to take effect than Azure documents. Otherwise assign " +
                   "'Foundry User' (Foundry resource, kind=AIServices) or 'Cognitive Services OpenAI User' " +
                   "(Azure OpenAI account, kind=OpenAI) on the resource that hosts the deployment. Do not " +
                   "use the 'Cognitive Services' roles on a Foundry resource; Microsoft does not support " +
                   "them there even when they appear to work.",

            404 => $"Azure could not find the deployment '{deployment}' (404). The endpoint is reachable, so " +
                   "check that the deployment name matches exactly, including any suffix, and that it lives " +
                   "on this resource.",

            429 => "Azure is throttling requests (429). The deployment is correct but over its quota. " +
                   "Wait and retry, or raise the deployment's capacity.",

            >= 500 => $"Azure returned a server error ({status}). This is usually transient; try again shortly.",

            _ when ex is OperationCanceledException or TimeoutException =>
                "The Azure request timed out before the deployment answered. Check the endpoint host and " +
                "your network, then try again.",

            _ when useKey =>
                "Couldn't reach the Azure deployment. Check the endpoint, deployment name, and API key.",

            _ when mode == Settings.AzureAuthMode.ServicePrincipal =>
                "Couldn't reach the Azure deployment. Check the endpoint, deployment name, tenant, client ID, " +
                "and client secret.",

            _ => "Couldn't reach the Azure deployment. Check that you're signed in (az login), the tenant is " +
                 "correct, and you have access.",
        };
    }

    /// <summary>
    /// Digs the HTTP status out of the two exception shapes the Azure and OpenAI clients throw, including
    /// when either is wrapped by the Agent Framework. Returns 0 when the failure was not an HTTP response.
    /// Depth-bounded (see <see cref="CleanupFailureShape.ExtractHttpStatus"/>), because a
    /// StackOverflowException on the failure path cannot be caught.
    /// </summary>
    internal static int ExtractHttpStatus(Exception? ex) => CleanupFailureShape.ExtractHttpStatus(ex);

    // Builds the process-wide Foundry Local configuration. The SDK requires an explicit web-service
    // configuration; when it is omitted, StartWebServiceAsync throws "Web service configuration was
    // not provided" and never populates manager.Urls. We bind the local OpenAI-compatible service to
    // a loopback address on an OS-assigned port (":0") so it never collides with a foundry CLI service
    // or a second Scribe process; manager.Urls then reports the port it actually bound.
    //
    // AppDataDir is the same resolved directory storage reclaim is confined to (see
    // FoundryLocalStorage.ResolveAppDataDir): the SDK default for the normal profile, and a folder
    // inside the data root for an isolated one. It has to be in this, the first configuration, because
    // the manager is a process-wide singleton the SDK never lets be created again. Null (no resolvable
    // profile folder) leaves the SDK's own default in place.
    internal FoundryConfiguration CreateFoundryConfiguration() => new()
    {
        AppName = FoundryLocalStorage.AppName,
        AppDataDir = _foundryAppDataDir,
        LogLevel = FoundryLogLevel.Warning,
        Web = new FoundryConfiguration.WebService { Urls = "http://127.0.0.1:0" },
    };

    // The manager only: no execution providers, no catalog, so nothing is downloaded. Serialized on
    // the runtime gate with the full creation below, so there is only ever one creator.
    private async Task<IFoundryLocalRuntime?> EnsureFoundryRuntimeAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _foundryRuntime) is { } ready)
        {
            return ready;
        }

        await _foundryRuntimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnsureFoundryRuntimeCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _foundryRuntimeGate.Release();
        }
    }

    // Must be called holding _foundryRuntimeGate.
    private async Task<IFoundryLocalRuntime?> EnsureFoundryRuntimeCoreAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _foundryRuntime) is { } existing)
        {
            return existing;
        }

        var runtime = await _foundryHost.CreateOrAttachAsync(CreateFoundryConfiguration(), _foundrySdkLog, ct)
            .ConfigureAwait(false);

        var adopted = false;
        lock (_gate)
        {
            // Never published once disposal started: disposal releases what it can see, so a runtime
            // published behind its back would be one nothing ever stops.
            if (!_operations.IsClosed)
            {
                Volatile.Write(ref _foundryRuntime, runtime);
                adopted = true;
            }
        }

        if (!adopted)
        {
            try
            {
                runtime.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug("Could not dispose a Foundry Local manager created during shutdown ({Failure}).", DescribeFailureShape(ex));
            }

            return null;
        }

        return runtime;
    }

    // The manager, its execution providers and its catalog, created once and published together.
    // Execution providers are registered BEFORE the first catalog read, because the SDK populates the
    // catalog from the currently-registered EPs and caches it on first use; fetching it earlier would
    // lock every consumer (the model picker and inference) into a CPU-only catalog even on a CUDA or
    // TensorRT-RTX machine. This is also the one call that can download several gigabytes of
    // execution providers, which is why only explicit requests and a saved Foundry Local
    // configuration reach it.
    private async Task<ICatalog?> EnsureFoundryCatalogAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _catalog) is { } ready)
        {
            return ready;
        }

        await _foundryRuntimeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A caller that queued behind the first one finds the published catalog here, and so
            // never registers execution providers a second time.
            if (Volatile.Read(ref _catalog) is { } raced)
            {
                return raced;
            }

            var runtime = await EnsureFoundryRuntimeCoreAsync(ct).ConfigureAwait(false);
            if (runtime is null)
            {
                return null;
            }

            var providers = await RegisterExecutionProvidersAsync(runtime, ct).ConfigureAwait(false);
            var catalog = await runtime.GetCatalogAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                if (_operations.IsClosed)
                {
                    return null;
                }

                _availableExecutionProviders = providers;
                Volatile.Write(ref _catalog, catalog);
            }

            return catalog;
        }
        finally
        {
            _foundryRuntimeGate.Release();
        }
    }

    // Registers the best available hardware execution providers (e.g. CUDA / TensorRT-RTX) for the
    // catalog about to be read. Best-effort: if EP setup fails the model still runs on CPU, so we log
    // and continue. A cancelled registration publishes nothing, so the next caller tries again.
    private async Task<string[]> RegisterExecutionProvidersAsync(IFoundryLocalRuntime runtime, CancellationToken ct)
    {
        try
        {
            var discovered = runtime.DiscoverEps();
            var result = await runtime.DownloadAndRegisterEpsAsync(ct).ConfigureAwait(false);
            return MergeAvailableExecutionProviders(
                discovered,
                result.RegisteredEps,
                runtime.DiscoverEps());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer Configure superseded this run, or the service is shutting down. Callers
            // already handle cancellation, and the next initialization must be free to try again.
            throw;
        }
        catch (Exception ex)
        {
            // By shape: an execution-provider download failure quotes the download location.
            _log.LogInformation(
                "Foundry execution-provider setup was skipped; continuing on available providers ({Failure}).",
                DescribeFailureShape(ex));
            try
            {
                return MergeAvailableExecutionProviders(runtime.DiscoverEps());
            }
            catch (Exception discoverEx)
            {
                _log.LogDebug("Could not enumerate Foundry execution providers ({Failure}).", DescribeFailureShape(discoverEx));
                return MergeAvailableExecutionProviders(discovered: null);
            }
        }
    }

    private static string[] MergeAvailableExecutionProviders(
        EpInfo[]? discovered,
        string[]? registered = null,
        EpInfo[]? afterRegistration = null)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CPUExecutionProvider",
        };

        void Add(string? provider)
        {
            if (!string.IsNullOrWhiteSpace(provider))
            {
                providers.Add(provider.Trim());
            }
        }

        if (discovered is not null)
        {
            foreach (var ep in discovered)
            {
                if (ep.IsRegistered)
                {
                    Add(ep.Name);
                }
            }
        }

        if (afterRegistration is not null)
        {
            foreach (var ep in afterRegistration)
            {
                if (ep.IsRegistered)
                {
                    Add(ep.Name);
                }
            }
        }

        if (registered is not null)
        {
            foreach (var ep in registered)
            {
                Add(ep);
            }
        }

        return providers.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Must be called holding _initLock: the web-service fields are only touched under it.
    private async Task EnsureManagerAsync(CancellationToken ct)
    {
        if (_managerReady && Volatile.Read(ref _catalog) is not null && _openAiClient is not null)
        {
            return;
        }

        // A null catalog means disposal began while it was being built; there is then nothing to
        // start a web service for.
        if (await EnsureFoundryCatalogAsync(ct).ConfigureAwait(false) is null ||
            Volatile.Read(ref _foundryRuntime) is not { } runtime)
        {
            return;
        }

        // Execution providers were registered inside EnsureFoundryCatalogAsync, before the catalog
        // read. Start (or attach to) the local OpenAI-compatible web service, then read the endpoint
        // it actually bound to rather than assuming a port.
        try
        {
            await runtime.StartWebServiceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // By shape: a web-service failure quotes the address it tried to bind.
            _log.LogInformation(
                "StartWebServiceAsync reported an issue; using the existing endpoint if available ({Failure}).",
                DescribeFailureShape(ex));
        }

        var urls = runtime.Urls;
        var baseUrl = urls is { Length: > 0 } ? urls[0] : null;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Foundry Local did not expose a web-service endpoint.");
        }

        // Only a manager this process created can have a web service running in it, so a bound URL
        // here means Scribe started it and switching away from Foundry Local may stop it.
        _webServiceStarted = true;

        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        // Foundry Local does not require a real API key; the credential is a placeholder.
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        OpenAIClientOptionsOverride?.Invoke(clientOptions);
        _openAiClient = new OpenAIClient(new ApiKeyCredential("foundry-local"), clientOptions);
        _managerReady = true;
    }

    private void OnDownloadProgress(string alias, float progress)
    {
        var pct = (int)Math.Round(progress);
        if (pct == _lastReportedPct)
        {
            return;
        }

        _lastReportedPct = pct;
        SetInitStatus(CleanupStatus.Downloading, $"Downloading {alias}… {Math.Clamp(pct, 0, 100)}%");
    }

    internal static string BuildSystemPrompt(CleanupOptions options)
    {
        var style = CleanupPrompt.ResolveWritingStyle(options.WritingStyle);

        // The guardrail preamble is the fixed part of the prompt; it varies by prompt style (frontier
        // vs local) and can be overridden per style by the user (with a restore-to-default in settings).
        var isLocalPrompt = CleanupPrompt.ResolvePromptStyle(options.PromptStyle, options.Provider) == CleanupPromptStyle.Local;
        var guardrail = isLocalPrompt
            ? CleanupPrompt.ResolveLocalPrompt(options.LocalPrompt)
            : CleanupPrompt.ResolveFrontierPrompt(options.FrontierPrompt);
        var prompt = guardrail + "\n\nWriting style:\n" + style;

        // The user dictionary is folded in as its own block after the writing style, so the vocabulary
        // feature is preserved independently of whatever tone the user asked for.
        if (!string.IsNullOrWhiteSpace(options.Glossary))
        {
            prompt += "\n\n" + options.Glossary.Trim();
        }

        // Qwen3-family models support a "/no_think" directive that suppresses chain-of-thought, so
        // they return the corrected text directly with no reasoning preamble. Applies to Foundry
        // Local aliases and to BYO endpoints (Ollama etc.) serving a qwen3 model. (Measured: on the
        // small default qwen3-1.7b, letting it reason did not improve cleanup quality, so we keep the
        // directive on both prompt paths for the lower, more predictable dictation latency.)
        if (IsQwen3Family(options))
        {
            prompt += " /no_think";
        }

        return prompt;
    }

    internal static string BuildAuxiliarySystemPrompt(CleanupOptions options, string systemPrompt)
    {
        var prompt = systemPrompt;
        if (IsQwen3Family(options) && !prompt.TrimEnd().EndsWith("/no_think", StringComparison.OrdinalIgnoreCase))
        {
            prompt += " /no_think";
        }

        return prompt;
    }

    internal static string? SanitizeAuxiliaryCompletion(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var cleaned = ThinkBlock.Replace(candidate, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : DashNormalizer.Normalize(cleaned);
    }

    private static bool IsQwen3Family(CleanupOptions options) => options.Provider switch
    {
        CleanupProvider.FoundryLocal =>
            options.FoundryModelAlias.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase),
        CleanupProvider.OpenAiCompatible =>
            options.CustomModel?.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) == true,
        _ => false,
    };

    // The per-call user message: just the delimited transcript, nothing else. ASR output never
    // contains angle-bracket tags, so the delimiters cannot be spoofed by speech.
    internal static string BuildUserMessage(string chunk) =>
        $"{TranscriptOpenTag}\n{chunk}\n{TranscriptCloseTag}";

    private static CleanupReason ReadyReason(CleanupOptions options) => options.Provider switch
    {
        CleanupProvider.AzureFoundry => new CleanupReason(
            "Azure deployment ready.",
            $"Azure deployment '{options.AzureDeployment}' ready."),
        CleanupProvider.OpenAiCompatible => new CleanupReason(
            $"'{options.CustomModel}' at the custom endpoint ready.",
            $"'{options.CustomModel}' at {(Uri.TryCreate(options.CustomEndpoint, UriKind.Absolute, out var u) ? u.Host : "custom endpoint")} ready."),
        _ => CleanupReason.Same($"{CleanupModelCatalog.Resolve(options.FoundryModelAlias).DisplayName} ready."),
    };

    // Per-call generation options. The system prompt lives on the agent, so this only carries the
    // sampling/limit knobs that vary by provider.
    private ChatOptions BuildChatOptions(CleanupOptions options, string text)
    {
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokensOverride ?? EstimateMaxTokens(text, options.Provider),
        };

        if (ReasoningEffortOverride is { } effort)
        {
            chatOptions.Reasoning = new ReasoningOptions
            {
                Effort = effort,
                Output = ReasoningOutput.None,
            };
        }

        // A low temperature keeps Foundry Local instruct models deterministic for a faithful edit.
        // Azure cleanup commonly targets gpt-5-class reasoning models, which run at a fixed internal
        // temperature and can reject or ignore an override; so we leave it unset and trust the model.
        if (options.Provider == CleanupProvider.FoundryLocal)
        {
            chatOptions.Temperature = CleanupTemperature;
        }

        return chatOptions;
    }

    private static int EstimateMaxTokens(string text, CleanupProvider provider)
    {
        // English averages a little over one token per word; cleanup output tracks input length.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        /*
         * Azure cleanup often runs on reasoning models whose hidden thinking counts against this
         * same budget, so a tight cap truncates the visible answer. The floor was 512 and that was
         * measured to be far too low.
         *
         * Grok 4.6, asked to punctuate a 51-token sentence, spent 532 reasoning tokens before it
         * wrote a single visible one. Scribe's real prompt carries the rulebook and the glossary, so
         * its inputs run about a thousand tokens and its thinking runs longer still. Against a 512
         * floor the model regularly used the whole budget on reasoning, returned an empty message,
         * failed TrySanitize, and cleanup fell back to the raw transcript. That reads to the user as
         * "the AI did nothing" on a model that was working correctly and simply had nowhere to put
         * the answer.
         *
         * The ceiling is not an allocation. Azure bills the tokens a model actually generates, so a
         * generous cap costs nothing on gpt-5.6-sol, which answers the same prompt with zero
         * reasoning tokens, and is the difference between working and silently doing nothing on a
         * model that thinks before it speaks.
         */
        if (provider == CleanupProvider.AzureFoundry)
        {
            var azureEstimate = (words * 4) + AzureReasoningHeadroomTokens;
            return Math.Clamp(azureEstimate, AzureReasoningHeadroomTokens, 16384);
        }

        // Foundry Local output also has to cover translation/format expansion and any hidden reasoning
        // tokens (e.g. qwen3). Long dictation is chunked before it reaches here, so each call is bounded
        // and this ceiling is only a safety net; keep it roomy so a chunk is never truncated. The
        // per-call timeout still bounds runaway generation.
        var estimate = (int)(words * 2.5) + 128;
        return Math.Clamp(estimate, 64, 4096);
    }

    // Cleans up a model's raw answer and reports whether it is usable. Returns false (and yields the
    // original text) when the output is empty after stripping think-blocks/fences/quotes, or is an
    // over-long ramble; so a caller cleaning a single chunk can treat a rejected answer as a failure
    // and surface it, rather than silently logging it as an unchanged success.
    internal static bool TrySanitize(string? candidate, string original, out string text)
    {
        text = original;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var cleaned = ThinkBlock.Replace(candidate, string.Empty).Trim();

        // Strip an enclosing markdown code fence the model may have added.
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
            {
                cleaned = cleaned[(firstNewline + 1)..];
            }

            if (cleaned.EndsWith("```", StringComparison.Ordinal))
            {
                cleaned = cleaned[..^3];
            }

            cleaned = cleaned.Trim();
        }

        // Strip echoed transcript delimiters: a literal-minded model sometimes mirrors the tags it
        // was shown around the user message back into its answer.
        if (cleaned.StartsWith(TranscriptOpenTag, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[TranscriptOpenTag.Length..].TrimStart();
        }

        if (cleaned.EndsWith(TranscriptCloseTag, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^TranscriptCloseTag.Length].TrimEnd();
        }

        // Strip a single pair of enclosing quotes if the model wrapped the whole answer in them.
        if (cleaned.Length >= 2 &&
            ((cleaned[0] == '"' && cleaned[^1] == '"') || (cleaned[0] == '\'' && cleaned[^1] == '\'')) &&
            !HasMatchingOuterQuotes(original))
        {
            cleaned = cleaned[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        // If the model ignored the instruction and rambled (e.g. answered the text), reject it.
        if (cleaned.Length > (original.Length * 2.5) + 80)
        {
            return false;
        }

        // Some models decline the rewrite and return a canned safety refusal ("I'm sorry, but I cannot
        // assist with that request.") in place of the cleaned text. It is short and non-empty, so it
        // slips past the empty/ramble guards and would be injected over the user's words. Reject it so
        // the chunk falls back to the raw transcription (and the pipeline flashes "intelligence
        // failed"). Only reject when the raw input isn't itself phrased that way, so a user who
        // literally dictates such a sentence keeps their words.
        if (LooksLikeRefusal(cleaned) && !LooksLikeRefusal(original))
        {
            return false;
        }

        // A weaker model sometimes answers or acknowledges the transcript instead of cleaning it (a
        // dictated "Can you hear me now?" comes back as "Yeah."). A terse reply is short and non-empty,
        // so it slips past every guard above and would be injected over the user's words. Reject it so
        // the chunk falls back to raw text and the pipeline flashes "intelligence failed"; the very next
        // dictation tries again. A false positive costs a missed clean-up, never wrong words.
        if (LooksLikeInventedReply(cleaned, original))
        {
            return false;
        }

        // Last, because the guards above compare against the raw answer: strip the em/en dashes the
        // writing style forbids but models still emit. Applied here rather than downstream so it only
        // ever touches the model's prose, never the user's dictionary replacements or snippets.
        text = DashNormalizer.Normalize(cleaned);
        return true;
    }

    internal static bool TryValidateCustomEndpoint(string? value, out Uri endpoint, out string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint!) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            error = "The endpoint is not a valid http(s) URL.";
            return false;
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            error = "Remote custom endpoints must use HTTPS. HTTP is allowed only for this PC.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool HasMatchingOuterQuotes(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''));
    }

    // True when the text reads like a model refusing the cleanup task rather than performing it: an
    // apology / AI-identity preamble at the start, or an inability verb ("can't/cannot/unable") next to
    // a help object ("assist/help/comply/…") anywhere. Deliberately narrow so ordinary speech that
    // merely opens with "Sorry" or "Unfortunately" is not misread as a refusal.
    internal static bool LooksLikeRefusal(string text) =>
        !string.IsNullOrWhiteSpace(text) && (RefusalPreamble.IsMatch(text) || RefusalInability.IsMatch(text));

    // True when the model's answer reads like a REPLY to the transcript rather than a cleaned copy of
    // it, and the raw input is not itself phrased that way. Three independent, gated signals:
    //  (1) an offer to help / assistant self-reference the speaker never said;
    //  (2) an affirmation/acknowledgement opener ("Yes,"/"Sure,"/"Will do.") the speaker never said
    //      (a clean-up never invents one, so an opener absent from the input is the model replying);
    //  (3) a terse reply (<= 3 words, not a question) that either answers a dictated question, or
    //      replaces a longer non-question utterance with words absent from it. Numbers are exempted so
    //      spoken-number reformatting ("nine hundred fifty" -> "$950") is never mistaken for an answer.
    // Rejecting falls back to the raw transcription, so the safe failure direction is preserved:
    // dropping a clean-up is recoverable; injecting the model's answer over the user's words is not.
    internal static bool LooksLikeInventedReply(string? candidate, string original)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        // (1) Offer to help the speaker did not dictate.
        if (ReplyOffer.IsMatch(candidate) && !ReplyOffer.IsMatch(original))
        {
            return true;
        }

        // (2) An affirmation/acknowledgement opener ("Yes,"/"Sure,"/"No,"/"Will do.") the speaker never
        // said. A clean-up never invents these, so an opener present in the output but absent from the
        // input is the model replying (to a question or a request). Preserved when the raw input itself
        // contains an affirmation, so a genuinely dictated "yes"/"will do" keeps the user's words.
        if (ReplyOpener.IsMatch(candidate) && !AffirmationAnywhere.IsMatch(original))
        {
            return true;
        }

        // (3) Terse reply. A cleaned question ends with "?"; a short, non-question result is a candidate
        // answer that replaced (rather than edited) the input.
        var candidateWords = WordSet(candidate);
        if (candidateWords.Count is > 0 and <= 3 && !candidate.TrimEnd().EndsWith('?'))
        {
            // A short, non-question reply to a dictated question is the model answering it.
            if (LooksLikeQuestion(original))
            {
                return true;
            }

            // For a non-question input, only reject when the few output words are absent from a longer
            // utterance (a replacement, not an edit) and the output isn't a numeric reformat.
            var originalWords = WordSet(original);
            if (originalWords.Count >= 4 && !candidate.Any(char.IsDigit))
            {
                var shared = 0;
                foreach (var word in candidateWords)
                {
                    if (originalWords.Contains(word))
                    {
                        shared++;
                    }
                }

                if (shared * 2 < candidateWords.Count)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Loose interrogative test for the reply guard: a trailing "?" or a leading question word/auxiliary.
    internal static bool LooksLikeQuestion(string text) =>
        !string.IsNullOrWhiteSpace(text) && (text.TrimEnd().EndsWith('?') || QuestionOpener.IsMatch(text));

    // Distinct lowercased word tokens, used by the terse-answer signal to measure input overlap.
    private static HashSet<string> WordSet(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordToken.Matches(text))
        {
            set.Add(match.Value.ToLowerInvariant());
        }

        return set;
    }

    private static CleanupOptions Normalize(CleanupOptions options)
    {
        var alias = string.IsNullOrWhiteSpace(options.FoundryModelAlias)
            ? CleanupModelCatalog.DefaultAlias
            : options.FoundryModelAlias.Trim();
        var endpoint = string.IsNullOrWhiteSpace(options.AzureEndpoint) ? null : options.AzureEndpoint.Trim();
        var deployment = string.IsNullOrWhiteSpace(options.AzureDeployment) ? null : options.AzureDeployment.Trim();
        var customEndpoint = string.IsNullOrWhiteSpace(options.CustomEndpoint) ? null : options.CustomEndpoint.Trim();
        var customModel = string.IsNullOrWhiteSpace(options.CustomModel) ? null : options.CustomModel.Trim();

        return options with
        {
            FoundryModelAlias = alias,
            AzureEndpoint = endpoint,
            AzureDeployment = deployment,
            CustomEndpoint = customEndpoint,
            CustomModel = customModel,
        };
    }

    private CleanupOptions ApplyPersistedFoundryDemotion(CleanupOptions options)
    {
        if (!options.Enabled ||
            options.Provider != CleanupProvider.FoundryLocal)
        {
            return options;
        }

        if (ReadFoundryDemotions().TryGetValue(options.FoundryModelAlias, out var cpuAlias) &&
            !string.IsNullOrWhiteSpace(cpuAlias))
        {
            _log.LogInformation(
                "Foundry Local model {GpuAlias} was previously demoted after a GPU compatibility failure. Using {CpuAlias}.",
                options.FoundryModelAlias,
                cpuAlias);
            return options with { FoundryModelAlias = cpuAlias.Trim() };
        }

        return options;
    }

    /// <summary>
    /// Drops any demotion whose CPU target is <paramref name="missingAlias"/> and returns the
    /// original alias to retry, or null when no demotion pointed there. Self-healing matters
    /// because the marker is invisible to the user: a stale target would otherwise leave cleanup
    /// permanently unavailable with no in-app way to recover.
    /// </summary>
    private string? ForgetFoundryDemotionTarget(string missingAlias)
    {
        if (string.IsNullOrWhiteSpace(missingAlias))
        {
            return null;
        }

        try
        {
            var demotions = ReadFoundryDemotions();
            var match = demotions.FirstOrDefault(pair =>
                string.Equals(pair.Value?.Trim(), missingAlias.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                return null;
            }

            demotions.Remove(match.Key);
            WriteFoundryDemotions(demotions);
            return match.Key;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not clear the stale Foundry Local demotion marker ({Failure}).", CleanupFailureShape.Describe(ex));
            return null;
        }
    }

    private void RememberFoundryDemotion(string gpuAlias, string cpuAlias)
    {
        if (string.IsNullOrWhiteSpace(gpuAlias) || string.IsNullOrWhiteSpace(cpuAlias))
        {
            return;
        }

        try
        {
            var demotions = ReadFoundryDemotions();
            demotions[gpuAlias.Trim()] = cpuAlias.Trim();
            WriteFoundryDemotions(demotions);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not persist the Foundry Local model demotion marker ({Failure}).", CleanupFailureShape.Describe(ex));
        }
    }

    // Written through a temp file and moved into place: a second Scribe process, or a crash
    // mid-write, would otherwise leave a truncated file. Reads already recover from that, but a
    // torn write silently discards every marker rather than the one being added.
    private void WriteFoundryDemotions(Dictionary<string, string> demotions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_foundryDemotionsPath)!);
        var staging = _foundryDemotionsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(demotions));
            File.Move(staging, _foundryDemotionsPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
            catch
            {
                // The unique staging name cannot block a later write, so cleanup is best effort.
            }

            throw;
        }
    }

    private Dictionary<string, string> ReadFoundryDemotions()
    {
        try
        {
            if (!File.Exists(_foundryDemotionsPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_foundryDemotionsPath));
            return values is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not read the Foundry Local model demotion marker ({Failure}).", CleanupFailureShape.Describe(ex));
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SetInitStatus(CleanupStatus status, string? detail) =>
        SetInitStatus(status, detail is null ? null : CleanupReason.Same(detail));

    // An initialization's own progress and outcome, from anywhere in its run: written on behalf of the
    // generation whose initialization holds _initLock, so a run that has been superseded writes nothing.
    private void SetInitStatus(CleanupStatus status, CleanupReason? reason) => PublishStatus(owner: null, status, reason);

    // Writes the status on behalf of a generation (null: the initialization holding _initLock), then
    // raises StatusChanged after releasing _gate. An initialization calls this while it still holds
    // _initLock, so its progress, outcome and Ready notifications are raised under that lock.
    private void PublishStatus(long? owner, CleanupStatus status, CleanupReason? reason)
    {
        bool changed;
        lock (_gate)
        {
            changed = WriteStatusLocked(owner ?? _initWriter, status, reason);
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    // Must be called under _gate. The only place the status is written, which is what makes the
    // invariant on _initGeneration hold for every writer rather than only for the ones that remember
    // it: a write lands only on behalf of the generation that owns the status, and never once disposal
    // has begun (the settings window may itself be closing). Returns whether the status changed; the
    // caller raises StatusChanged after releasing _gate.
    private bool WriteStatusLocked(long owner, CleanupStatus status, CleanupReason? reason)
    {
        if (_operations.IsClosed || owner == 0 || owner != _initGeneration)
        {
            return false;
        }

        var detail = reason?.Display;
        var diagnostic = reason?.Diagnostic;
        var changed = _status != status ||
            !string.Equals(_statusDetail, detail, StringComparison.Ordinal) ||
            !string.Equals(_statusReason, diagnostic, StringComparison.Ordinal);
        _status = status;
        _statusDetail = detail;
        _statusReason = diagnostic;
        return changed;
    }

    // Never called holding _gate. An initialization raises its progress, outcome and Ready while it still
    // holds _initLock, so a subscriber reached from those notifications runs under that lock too; the
    // other paths raise it after releasing the locks they decided under. The event carries nothing, so a
    // notification that arrives after a newer write is harmless: every subscriber reads the current status.
    private void RaiseStatusChanged()
    {
        if (StatusChanged is not { } handlers)
        {
            return;
        }

        // Each subscriber on its own: .NET stops walking an invocation list at the first throw, and a
        // closing settings window must not stop the dictation controller hearing about Ready. This is
        // ResilientEvent.InvokeAll's shape; that helper only takes Action<T>, and this event has no
        // argument.
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                try
                {
                    _log.LogDebug("A cleanup StatusChanged handler threw ({Failure}).", CleanupFailureShape.Describe(ex));
                }
                catch (Exception)
                {
                    // A logger that throws must not stop the fan-out it was only meant to describe.
                }
            }
        }
    }

    /// <summary>
    /// Releases the Copilot CLI session, if one is held. Returns the handle to null so a second call
    /// is a no-op.
    /// </summary>
    /// <remarks>
    /// Typed against <see cref="IAsyncDisposable"/> rather than the SDK's client so this method does
    /// not name a Copilot type: a signature here is class metadata and would load the assembly on
    /// every launch, which is the whole thing <c>InitGitHubCopilotAsync</c>'s lazy-loading discipline
    /// exists to avoid.
    /// </remarks>
    private async Task ReleaseCopilotSessionAsync()
    {
        object? session;
        lock (_gate)
        {
            session = _copilotClientHandle;
            _copilotClientHandle = null;
        }

        await DisposeCopilotSessionAsync(session).ConfigureAwait(false);
    }

    private async Task DisposeCopilotSessionAsync(object? session)
    {
        if (session is not IAsyncDisposable disposable)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogProviderFailure(LogLevel.Debug, CleanupProvider.GitHubCopilot, ex, "Could not dispose the GitHub Copilot session.");
        }
    }

    // --- Foundry Local storage reclaim ----------------------------------------------------------

    // Runs deferred storage work for a settings change. Admitted by the caller under _gate, and
    // re-checked against the configuration applied by the time it runs: a reclaim scheduled for a
    // switch away must not run after the user has switched back, and nothing runs once the user has
    // explicitly loaded or listed models since it was scheduled (epoch).
    private async Task RunStorageIntentAsync(FoundryStorageIntent intent, long epoch, CleanupOperationTracker.Lease lease)
    {
        using var ownership = lease;
        try
        {
            if (StorageWorkGateForTesting is { } gate)
            {
                await gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }

            switch (intent)
            {
                case FoundryStorageIntent.ReclaimEverything:
                    // Raised here, after the reclaim released its gates, so a notice handler can
                    // never be what holds up the next initialization.
                    RaiseStorageReclaimed(await ReclaimEverythingAsync(epoch, _lifetime.Token).ConfigureAwait(false));
                    break;

                case FoundryStorageIntent.UnloadOnly:
                    await UnloadForDisabledCleanupAsync(epoch, _lifetime.Token).ConfigureAwait(false);
                    break;

                case FoundryStorageIntent.ReviewUnusedRuntime:
                    RaiseStorageReclaimed(await ReviewUnusedRuntimeAsync(epoch, _lifetime.Token).ConfigureAwait(false));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Anything not reclaimed now is reclaimed at the next startup.
        }
        catch (Exception ex)
        {
            // By shape: file-system and SDK failures here quote paths under the user profile.
            TryLogFailureShape(ex, "Reclaiming Foundry Local storage failed; it is retried at the next start.");
        }
    }

    private bool StorageIntentStillApplies(FoundryStorageIntent intent, long epoch)
    {
        lock (_gate)
        {
            return !_operations.IsClosed &&
                FoundryStoragePolicy.StillApplies(intent, _appliedSelection, explicitUseSinceScheduled: _explicitUseEpoch != epoch);
        }
    }

    // Called holding the runtime gate, so this service cannot create a runtime underneath it.
    private FoundryRuntimePresence ReadRuntimePresence() => CurrentRuntimePresence(_foundryHost.IsManagerCreated);

    // The saved provider is not Foundry Local: unload, stop the web service Scribe started, remove
    // the models, and delete the execution-provider downloads, each by the only means that is safe
    // for how much of the runtime this process holds. Returns what was given back, if anything.
    private async Task<FoundryStorageReclaim?> ReclaimEverythingAsync(long epoch, CancellationToken ct)
    {
        var storage = _foundryStorage!;
        bool StillApplies() => StorageIntentStillApplies(FoundryStorageIntent.ReclaimEverything, epoch);

        // _initLock first, then the runtime gate, the same order initialization takes them, so this
        // can neither deadlock with it nor race a first-time runtime creation while it deletes files.
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _foundryRuntimeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!StillApplies())
                {
                    return null;
                }

                var plan = FoundryStoragePolicy.ForIntent(FoundryStorageIntent.ReclaimEverything, ReadRuntimePresence());
                var unloaded = 0;
                var removed = 0;
                var removalFailures = 0;
                long removedBytes = 0;

                if (Volatile.Read(ref _catalog) is { } catalog && (plan.UnloadAllModels || plan.RemoveAllCachedModels))
                {
                    if (plan.UnloadAllModels)
                    {
                        unloaded = await UnloadAllFoundryModelsAsync(catalog, ct).ConfigureAwait(false);
                    }

                    if (plan.RemoveAllCachedModels)
                    {
                        var cached = await catalog.GetCachedModelsAsync(ct).ConfigureAwait(false);
                        (removed, removalFailures, removedBytes) = await RemoveCachedModelsAsync(
                                cached,
                                storage,
                                StillApplies,
                                ct)
                            .ConfigureAwait(false);
                    }
                }

                // The user may have switched back, or asked for a model, while the SDK calls above
                // ran; the rest would only be undone again by the work waiting behind this.
                if (!StillApplies())
                {
                    return ReportReclaim(
                        storage, plan, FoundryStorageReclaimReason.ProviderIsNotFoundryLocal, unloaded, removed, removalFailures, removedBytes, default);
                }

                if (plan.StopWebService)
                {
                    await StopFoundryWebServiceAsync(ct).ConfigureAwait(false);
                }

                if (plan.ReleaseRuntimeReferences)
                {
                    // The manager itself stays: the SDK never lets it be created again in this
                    // process, so disposing it would break a later switch back to Foundry Local.
                    _openAiClient = null;
                    _managerReady = false;
                    lock (_gate)
                    {
                        _foundryInUse = null;
                    }
                }

                // Each directory goes as a unit: a file in use leaves all of it for a later pass rather
                // than half a runtime or half a model on disk.
                var files = default(FoundryStorageReclaimResult);
                if (plan.DeleteModelFiles)
                {
                    files = files.Add(storage.Janitor.ReclaimDirectory(storage.AppDataDir, storage.ModelCacheDir, ct));
                }

                if (plan.RuntimeFiles == FoundryRuntimeFiles.DeleteNow)
                {
                    files = files.Add(storage.Janitor.ReclaimDirectory(storage.AppDataDir, storage.ExecutionProviderDir, ct));
                }

                // Every model is gone, so a pending "keep only the selected model" has nothing left to
                // narrow down. Kept when a removal failed so it can be retried, and never cleared once
                // the user has moved on: the marker may by then belong to a newer model switch.
                if (plan.KeepOnlySelected == FoundryKeepOnlySelected.Clear && removalFailures == 0 &&
                    files.FilesDeferred == 0 && !files.Refused)
                {
                    lock (_markerSync)
                    {
                        if (StillApplies())
                        {
                            storage.WriteKeepOnlySelected(false);
                        }
                    }
                }

                return ReportReclaim(
                    storage, plan, FoundryStorageReclaimReason.ProviderIsNotFoundryLocal, unloaded, removed, removalFailures, removedBytes, files);
            }
            finally
            {
                _foundryRuntimeGate.Release();
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    // AI cleanup was switched off while Foundry Local stays selected: free the memory, keep the files
    // so switching it back on is quick.
    private async Task UnloadForDisabledCleanupAsync(long epoch, CancellationToken ct)
    {
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!StorageIntentStillApplies(FoundryStorageIntent.UnloadOnly, epoch) ||
                Volatile.Read(ref _catalog) is not { } catalog)
            {
                return;
            }

            var unloaded = await UnloadAllFoundryModelsAsync(catalog, ct).ConfigureAwait(false);
            if (unloaded > 0)
            {
                _log.LogInformation(
                    "AI cleanup is off: unloaded {Count} Foundry Local model(s) to free memory; the downloaded files stay.",
                    unloaded);
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Startup with Foundry Local saved and cleanup off. When no model is cached, the hardware runtime
    // downloads came from browsing the model list rather than from choosing to load a model, so they
    // go; setting Foundry Local up again downloads them. Returns what was given back, if anything.
    private async Task<FoundryStorageReclaim?> ReviewUnusedRuntimeAsync(long epoch, CancellationToken ct)
    {
        var storage = _foundryStorage!;

        // Same order as initialization, and held throughout, so no runtime can be created while the
        // cache is read and the runtime deleted.
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _foundryRuntimeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!StorageIntentStillApplies(FoundryStorageIntent.ReviewUnusedRuntime, epoch))
                {
                    return null;
                }

                var modelCache = storage.Janitor.InspectModelCache(storage.AppDataDir, storage.ModelCacheDir);
                var plan = FoundryStoragePolicy.ForIntent(FoundryStorageIntent.ReviewUnusedRuntime, ReadRuntimePresence(), modelCache);
                if (plan.RuntimeFiles != FoundryRuntimeFiles.DeleteNow)
                {
                    return null;
                }

                var files = storage.Janitor.ReclaimDirectory(storage.AppDataDir, storage.ExecutionProviderDir, ct);
                if (plan.KeepOnlySelected == FoundryKeepOnlySelected.Clear && files.FilesDeferred == 0 && !files.Refused)
                {
                    lock (_markerSync)
                    {
                        if (StorageIntentStillApplies(FoundryStorageIntent.ReviewUnusedRuntime, epoch))
                        {
                            storage.WriteKeepOnlySelected(false);
                        }
                    }
                }

                if (files.FilesDeleted > 0)
                {
                    _log.LogInformation(
                        "Foundry Local is selected but no model is downloaded and AI cleanup is off: deleted the hardware runtime downloads; setting up Foundry Local fetches them again.");
                }

                return ReportReclaim(storage, plan, FoundryStorageReclaimReason.RuntimeWithoutModel, 0, 0, 0, 0, files);
            }
            finally
            {
                _foundryRuntimeGate.Release();
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Once the selected Foundry Local model is in use, removes every other cached model, keeping only
    // the selected one: after a model switch (the persisted marker), and once per session when
    // Foundry Local was saved at startup (leftovers, including switches made before this rule).
    private void ScheduleKeepOnlySelected(CleanupOptions options, FoundryModelIdentity inUse)
    {
        if (_foundryStorage is not { } storage)
        {
            return;
        }

        bool pending;
        lock (_markerSync)
        {
            pending = _keepOnlySelectedArmed || storage.ReadKeepOnlySelected();
        }

        if (!pending)
        {
            return;
        }

        FoundrySelection? scheduledFor;
        CleanupOperationTracker.Lease? lease;
        long epoch;
        lock (_gate)
        {
            scheduledFor = _appliedSelection;
            epoch = _explicitUseEpoch;
            lease = _operations.TryEnter();
        }

        if (lease is null || scheduledFor is not { IsFoundry: true } selection)
        {
            lease?.Dispose();
            return;
        }

        LastStorageWork = Task.Run(() => KeepOnlySelectedAsync(selection, options.FoundryModelAlias, inUse, epoch, lease));
    }

    // Whether the model a keep-only-selected pass was scheduled for is still the selected one in use,
    // and the user has not explicitly loaded or listed models since.
    private bool KeepOnlySelectedStillCurrent(FoundrySelection scheduledFor, FoundryModelIdentity inUse, long epoch)
    {
        lock (_gate)
        {
            return !_operations.IsClosed && _appliedSelection == scheduledFor && _explicitUseEpoch == epoch &&
                _status == CleanupStatus.Ready && _foundryInUse == inUse;
        }
    }

    private async Task KeepOnlySelectedAsync(
        FoundrySelection scheduledFor, string effectiveAlias, FoundryModelIdentity inUse, long epoch, CleanupOperationTracker.Lease lease)
    {
        using var ownership = lease;
        var storage = _foundryStorage!;
        var ct = _lifetime.Token;
        bool StillCurrent() => KeepOnlySelectedStillCurrent(scheduledFor, inUse, epoch);
        FoundryStorageReclaim? reclaimed = null;
        try
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!StillCurrent() || Volatile.Read(ref _catalog) is not { } catalog)
                {
                    return;
                }

                var cached = await catalog.GetCachedModelsAsync(ct).ConfigureAwait(false);
                var loaded = await catalog.GetLoadedModelsAsync(ct).ConfigureAwait(false);
                var removeIds = FoundryStoragePolicy.SelectModelsToRemove(
                    [scheduledFor.Alias, effectiveAlias, inUse.Id, inUse.Alias],
                    cached.Select(ToIdentity),
                    loaded.Select(ToIdentity));

                // Re-checked before every removal: a newer switch made while this runs may select one
                // of the models this pass would otherwise delete.
                var remove = new HashSet<string>(removeIds, StringComparer.OrdinalIgnoreCase);
                var (removed, failures, bytes) = await RemoveCachedModelsAsync(
                        cached.Where(model => remove.Contains(model.Id)).ToList(),
                        storage,
                        StillCurrent,
                        ct)
                    .ConfigureAwait(false);

                if (failures == 0)
                {
                    lock (_markerSync)
                    {
                        if (StillCurrent())
                        {
                            storage.WriteKeepOnlySelected(false);
                            _keepOnlySelectedArmed = false;
                        }
                    }
                }

                if (removed > 0 || failures > 0)
                {
                    _log.LogInformation(
                        "Kept only the selected Foundry Local model: removed {Removed} other cached model(s), {Megabytes:F0} MB, {Failed} left for a later attempt.",
                        removed, bytes / (1024.0 * 1024.0), failures);
                }

                if (removed > 0)
                {
                    reclaimed = new FoundryStorageReclaim(
                        FoundryStorageReclaimReason.ModelSwitched, bytes, removed, FilesDeleted: 0, RuntimeDeletedAtNextStart: false);
                }
            }
            finally
            {
                _initLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the marker stays set, so the next start finishes the job.
        }
        catch (Exception ex)
        {
            TryLogFailureShape(ex, "Removing the previously selected Foundry Local model failed; it is retried later.");
        }

        // After the init lock is released, so a notice handler cannot hold up the next initialization.
        RaiseStorageReclaimed(reclaimed);
    }

    private static FoundryModelIdentity ToIdentity(IModel model) => new(model.Id, model.Alias);

    private async Task<int> UnloadAllFoundryModelsAsync(ICatalog catalog, CancellationToken ct)
    {
        var unloaded = 0;
        foreach (var model in await catalog.GetLoadedModelsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await model.UnloadAsync(ct).ConfigureAwait(false);
                unloaded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                TryLogFailureShape(ex, "Could not unload a Foundry Local model.", LogLevel.Debug);
            }
        }

        return unloaded;
    }

    // Removal goes through the SDK's own RemoveFromCacheAsync, which knows its cache layout; the
    // size is measured first, read-only, purely for the log line. stillWanted is asked before each
    // model, so work that the user's latest settings no longer call for stops at once.
    private async Task<(int Removed, int Failures, long Bytes)> RemoveCachedModelsAsync(
        IReadOnlyList<IModel> models, FoundryLocalStorage storage, Func<bool> stillWanted, CancellationToken ct)
    {
        var removed = 0;
        var failures = 0;
        long bytes = 0;
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            if (!stillWanted())
            {
                // Not a failure, but not finished either: the marker must not be cleared as if it were.
                failures++;
                break;
            }

            long size = 0;
            try
            {
                size = storage.Janitor.MeasureBytes(storage.AppDataDir, await model.GetPathAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Sizing only feeds the log; removal proceeds regardless.
            }

            try
            {
                await model.RemoveFromCacheAsync(ct).ConfigureAwait(false);
                removed++;
                bytes += size;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                TryLogFailureShape(ex, "Could not remove a Foundry Local model from the cache.", LogLevel.Debug);
            }
        }

        return (removed, failures, bytes);
    }

    // Must be called holding _initLock.
    private async Task StopFoundryWebServiceAsync(CancellationToken ct)
    {
        if (!_webServiceStarted || Volatile.Read(ref _foundryRuntime) is not { } runtime)
        {
            return;
        }

        try
        {
            await runtime.StopWebServiceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryLogFailureShape(ex, "Stopping the Foundry Local web service failed.", LogLevel.Debug);
        }

        // Cleared either way, so a switch back to Foundry Local starts the service and rebuilds the
        // client instead of trusting an endpoint that may be gone.
        _webServiceStarted = false;
        _managerReady = false;
        _openAiClient = null;
    }

    // Logs a failure by its shape only. Used where a file-system or SDK message would name a path under
    // the user profile (the reclaim log lines promise no user name) or a remote endpoint.
    private void TryLogFailureShape(Exception exception, string message, LogLevel level = LogLevel.Warning)
    {
        try
        {
            _log.Log(level, "{Message} ({Failure})", message, CleanupFailureShape.Describe(exception));
        }
        catch (Exception)
        {
            // Logging must never turn a best-effort reclaim into a failure.
        }
    }

    // Logs one reclaim pass and returns what it gave back, or null when nothing was freed.
    private FoundryStorageReclaim? ReportReclaim(
        FoundryLocalStorage storage,
        FoundryStoragePlan plan,
        FoundryStorageReclaimReason reason,
        int unloaded,
        int removed,
        int removalFailures,
        long removedBytes,
        FoundryStorageReclaimResult files)
    {
        var freedBytes = removedBytes + files.BytesDeleted;
        var megabytes = freedBytes / (1024.0 * 1024.0);
        if (unloaded > 0 || removed > 0 || removalFailures > 0 || files.FilesDeleted > 0 ||
            files.FilesDeferred > 0 || files.ReparsePointsSkipped > 0 || files.Refused)
        {
            _log.LogInformation(
                "Reclaimed Foundry Local storage in {Directory}: {Megabytes:F0} MB, {Models} cached model(s) removed, {Files} file(s) deleted, {Unloaded} model(s) unloaded; {Deferred} left for a later attempt, {Skipped} link(s) skipped, refused={Refused}.",
                FoundryLocalStorage.DisplayPath(storage.AppDataDir),
                megabytes,
                removed,
                files.FilesDeleted,
                unloaded,
                removalFailures + files.FilesDeferred,
                files.ReparsePointsSkipped,
                files.Refused);
        }

        var runtimeDeferred = plan.RuntimeFiles == FoundryRuntimeFiles.DeferToNextStartup;
        if (runtimeDeferred)
        {
            _log.LogInformation(
                "Foundry Local execution-provider downloads in {Directory} are in use by this process; they are deleted at the next start if another provider is still selected.",
                FoundryLocalStorage.DisplayPath(storage.ExecutionProviderDir));
        }

        return freedBytes > 0 || removed > 0 || files.FilesDeleted > 0
            ? new FoundryStorageReclaim(reason, freedBytes, removed, files.FilesDeleted, runtimeDeferred)
            : null;
    }

    // Tells subscribers what a reclaim gave back. Never under a Scribe lock, never after disposal
    // began, and one throwing subscriber cannot stop the others (P-3).
    private void RaiseStorageReclaimed(FoundryStorageReclaim? reclaimed)
    {
        if (reclaimed is null || _operations.IsClosed)
        {
            return;
        }

        ResilientEvent.InvokeAll(
            FoundryStorageReclaimed,
            reclaimed,
            ex => TryLogFailureShape(ex, "A Foundry Local reclaim notice handler threw.", LogLevel.Debug));
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? configure;
        List<AIAgent> agents;
        Task drained;
        lock (_gate)
        {
            if (_operations.IsClosed)
            {
                return;
            }

            // Closing admission under _gate is what makes every publication check (status, agents,
            // Copilot client, Foundry runtime) refuse from this point on.
            drained = _operations.Close();
            configure = _configureCts;
            _configureCts = null;
            agents = DetachAgents();
        }

        // Outside _gate: cancellation runs registered callbacks, and so the cancelled operations'
        // continuations, synchronously on this thread.
        TryCancel(_lifetime);
        TryCancel(configure);

        // Every admitted operation, superseded initializations included, has to stop using the
        // shared resources before any of them is released. One that will not stop in time keeps them:
        // releasing a client or runtime in use turns a slow shutdown into a crash.
        try
        {
            await drained.WaitAsync(DisposalDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            DisposalOutcome = CleanupDisposalOutcome.LeftToProcessExit;
            _log.LogWarning(
                "AI cleanup shut down with {Count} operation(s) still running after {Seconds:F0}s; their shared resources are left to process exit rather than released while in use.",
                _operations.ActiveCount,
                DisposalDrainTimeout.TotalSeconds);
            return;
        }

        /*
         * Released on the thread pool, never on the caller's context.
         *
         * The app disposes this from the WPF dispatcher through Host.Dispose, which blocks on
         * DisposeAsync. With nothing in flight the drain above completes synchronously, so everything
         * below would otherwise run on that blocked dispatcher. The Copilot SDK's own cleanup awaits
         * the CLI's exit and its stderr pump without ConfigureAwait(false) (GitHub.Copilot.SDK 1.0.5),
         * so those continuations would be posted to the dispatcher and never run: the app would never
         * exit, and would keep holding the single-instance mutex. Task.Yield would not help, because
         * it posts back to the captured context too.
         */
        await Task.Run(() => ReleaseSharedResourcesAsync(agents)).ConfigureAwait(false);

        _initLock.Dispose();
        _foundryRuntimeGate.Dispose();
        configure?.Dispose();
        _lifetime.Dispose();
        DisposalOutcome = CleanupDisposalOutcome.Released;
    }

    // Runs after the drain, so no cleanup call is still using any of these.
    private async Task ReleaseSharedResourcesAsync(List<AIAgent> agents)
    {
        foreach (var agent in agents)
        {
            await DisposeQuietlyAsync(agent).ConfigureAwait(false);
        }

        /*
         * The Copilot session is a child process, so it has to be asked to close.
         *
         * DropAgents only clears the agent references; nothing in it reaches the CLI. Without this
         * the `copilot` process outlived the app on every exit.
         */
        await ReleaseCopilotSessionAsync().ConfigureAwait(false);

        // Stops the Foundry Local web service too, when Scribe started it.
        var runtime = Interlocked.Exchange(ref _foundryRuntime, null);
        Volatile.Write(ref _catalog, null);
        _openAiClient = null;
        try
        {
            runtime?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not dispose the Foundry Local manager ({Failure}).", DescribeFailureShape(ex));
        }
    }

    // Must be called under _gate. The distinct agents the service holds, detached so nothing new can
    // pick them up; they are disposed only after every operation that captured one has finished.
    private List<AIAgent> DetachAgents()
    {
        var agents = new List<AIAgent>(_styleAgents.Count + 1);
        if (_agent is not null)
        {
            agents.Add(_agent);
        }

        foreach (var styled in _styleAgents.Values)
        {
            if (!agents.Contains(styled))
            {
                agents.Add(styled);
            }
        }

        DropAgents();
        return agents;
    }

    private async Task DisposeQuietlyAsync(object resource)
    {
        try
        {
            switch (resource)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            // By shape: an agent over a remote endpoint can surface that endpoint in its failure.
            TryLogFailureShape(ex, "Could not dispose a cleanup agent.", LogLevel.Debug);
        }
    }
}
