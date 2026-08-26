using System.ClientModel;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.Projects;
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
/// Foundry <i>project</i> endpoint (<c>…/api/projects/…</c>) is turned into an agent directly with
/// the framework's native <c>AIProjectClient.AsAIAgent</c>; a classic Azure OpenAI account endpoint
/// uses the unified OpenAI v1 endpoint and is wrapped with <see cref="ChatClientAgent"/>.
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
internal sealed class TextCleanupService : ITextCleanupService
{
    // Cleanup is a quick rewrite of short text; cap latency and input size so a long paragraph or a
    // slow model can never stall the inject path. On any timeout we return the raw text. Azure gets a
    // longer budget than Foundry Local: a cloud round-trip plus a reasoning model's hidden thinking
    // step is slower than a warm on-device model. The Azure ceiling is generous enough for a reasoning
    // ("pro"/o-series) model to finish a real rewrite; fast chat models (e.g. gpt-5.x-mini) return in
    // a couple of seconds regardless, so the cap only ever bites a genuinely slow model.
    private const int CleanupTimeoutSeconds = 12;
    private const int AzureCleanupTimeoutSeconds = 45;
    private const int InitProbeTimeoutSeconds = 30;
    // A local model's first inference includes warm-up, and that grows with parameter count: a 14B
    // model on the CPU cleared 30s, so cleanup was marked Unavailable for a model that then worked
    // fine. The probe runs once per configuration change, so waiting longer here costs nothing on
    // the dictation path and is far better than a false negative the user cannot explain.
    private const int LocalInitProbeTimeoutSeconds = 180;
    // Azure rejects anything below 16 with "integer_below_min_value", so the probe would have failed
    // on every Azure endpoint and marked cleanup Unavailable. The probe only needs the call to
    // succeed, not to produce useful text, so the minimum accepted value is the right choice.
    private const int InitProbeMaxOutputTokens = 16;

    // How much of the cloud budget one attempt may spend. Measured cleanups on a real deployment
    // run around 2s and peak near 12s, so 25s is far beyond any healthy call and leaves 20s to
    // recover from a stalled connection.
    private const int CloudFirstAttemptTimeoutSeconds = 25;
    private const int TotalCleanupTimeoutSeconds = 90;
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

    private static readonly Regex ThinkBlock =
        new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A model sometimes declines the rewrite and answers with a canned safety refusal ("I'm sorry, but
    // I cannot assist with that request.") instead of the cleaned text. Two intent families detect it:
    // an apology / AI-identity preamble at the very start, or an inability verb paired with a help
    // object anywhere. See LooksLikeRefusal / TrySanitize; a match is only acted on when the raw input
    // isn't phrased the same way, so genuine dictation of these words is preserved.
    private static readonly Regex RefusalPreamble =
        new(@"^\s*(?:i(?:'m| am)\s+(?:sorry|afraid)\b|i apologi[sz]e\b|my apologies\b|as an ai\b|as a language model\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RefusalInability =
        new(@"\b(?:can'?t|cannot|could\s*n'?t|unable to|not able to|won'?t|will not)\s+(?:assist|help|comply|fulfil|fulfill|provide|process|complete|continue)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Reply/answer guards (siblings of the refusal guards). A weaker model, a small Foundry Local
    // model especially, sometimes REPLIES to the transcript (answers a dictated question, acknowledges
    // a request, or offers help) instead of editing it. Such replies are short and non-empty, so they
    // slip past the empty/ramble/refusal guards; injected, they overwrite the user's words with the
    // model's answer. This is the defect behind "Can you hear me now?" producing "Yeah." A strong model
    // obeys the prompt and never trips these; the guard is the deterministic backstop for weaker ones.
    // See LooksLikeInventedReply. Each pattern is only acted on when the raw input isn't itself phrased
    // that way, so genuinely dictated affirmations, offers and questions are preserved.
    private static readonly Regex ReplyOpener =
        new(@"^\s*[""']?\s*(?:yes|yeah|yep|yup|sure\s+thing|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|affirmative|you\s+bet|my\s+pleasure)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The same affirmation/acknowledgement words anywhere in a message. Used as the valve for signal (2):
    // an opener in the model's output that appears nowhere in the raw input was invented by the model.
    private static readonly Regex AffirmationAnywhere =
        new(@"\b(?:yes|yeah|yep|yup|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|affirmative|you\s+bet)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReplyOffer =
        new(@"\b(?:i\s+can\s+(?:help|assist)|i(?:'d|\s+would)\s+be\s+(?:happy|glad)\s+to|(?:happy|glad)\s+to\s+(?:help|assist)|how\s+(?:can|may)\s+i\s+(?:help|assist)|let\s+me\s+(?:help|assist)|i(?:'m|\s+am)\s+here\s+to\s+(?:help|assist)|is\s+there\s+anything\s+else\s+i)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Loose interrogative test: a trailing "?" or a leading question word/auxiliary. Only gates the
    // affirmation/terse signals below, which also require the output not to be a question, so occasional
    // imprecision here can never reject an ordinary cleaned sentence.
    private static readonly Regex QuestionOpener =
        new(@"^\s*(?:who|what|what'?s|when|where|why|how|how'?s|which|whose|whom|do|does|did|is|are|am|was|were|can|could|will|would|should|shall|may|might|have|has|had|must)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Word tokens (letters/digits, inner apostrophes kept) for the terse-answer overlap check.
    private static readonly Regex WordToken =
        new(@"[\p{L}\p{Nd}]+(?:'[\p{L}\p{Nd}]+)*", RegexOptions.Compiled);

    // Collapses an endpoint's multi-line error text into the single line a status pill can show.
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private readonly ILogger<TextCleanupService> _log;
    private readonly string _foundryDemotionsPath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private CleanupOptions _options = CleanupOptions.Disabled;

    private CleanupStatus _status = CleanupStatus.Disabled;
    private string? _statusDetail;

    // Foundry Local runtime (shared across model switches once initialized).
    private FoundryLocalManager? _manager;
    private ICatalog? _catalog;
    private OpenAIClient? _openAiClient;
    private bool _managerReady;
    private bool _epsRegistered;
    private string[] _availableExecutionProviders = ["CPUExecutionProvider"];

    // The active cleanup agent (Agent Framework). Rebuilt whenever the provider/model/endpoint
    // changes; null until initialization completes or after the feature is disabled.
    private AIAgent? _agent;

    // Per-app profiles swap the writing style per call. The factory builds an agent for a given
    // system prompt against the already-initialized client (pure object construction, no I/O), and
    // built agents are cached per style so an app switch costs nothing after its first dictation.
    // Both are reset together with _agent whenever the provider/model/endpoint changes.
    private Func<string, AIAgent>? _agentFactory;
    private Func<string, AIAgent>? _pendingFactory; // handoff from InitXxx (serialized by _initLock)
    private readonly Dictionary<string, AIAgent> _styleAgents = new(StringComparer.Ordinal);

    private CancellationTokenSource? _configureCts;
    private int _lastReportedPct = -1;
    private bool _disposed;

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

    public TextCleanupService(ILogger<TextCleanupService> log, AppPaths? paths = null)
    {
        _log = log;
        _foundryDemotionsPath = Path.Combine((paths ?? new AppPaths()).RootDir, "foundry-local-demotions.json");
    }

    public CleanupStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    public string? StatusDetail
    {
        get { lock (_gate) { return _statusDetail; } }
    }

    public event Action? StatusChanged;

    public void Configure(CleanupOptions options)
    {
        if (_disposed)
        {
            return;
        }

        var effective = ApplyPersistedFoundryDemotion(Normalize(options ?? CleanupOptions.Disabled));

        bool startInit = false;
        bool nowDisabled = false;
        bool notActionable = false;
        CancellationToken initToken = default;

        lock (_gate)
        {
            var sameConfig = _options == effective;
            _options = effective;

            if (!effective.Enabled)
            {
                _configureCts?.Cancel();
                DropAgents();
                nowDisabled = true;
            }
            else if (!effective.IsActionable)
            {
                _configureCts?.Cancel();
                DropAgents();
                notActionable = true;
            }
            else if (!(sameConfig && _status == CleanupStatus.Ready))
            {
                _configureCts?.Cancel();
                _configureCts = new CancellationTokenSource();
                initToken = _configureCts.Token;
                // Drop the stale agents immediately so a dictation fired right after a save can never
                // run against the previous provider/model/prompt; CleanAsync passes through raw text
                // until the rebuilt agent is published, then the next call reflects the new settings.
                DropAgents();
                startInit = true;
            }
        }

        if (nowDisabled)
        {
            SetStatus(CleanupStatus.Disabled, null);
            _log.LogInformation("AI cleanup disabled.");
            return;
        }

        if (notActionable)
        {
            var detail = effective.Provider switch
            {
                CleanupProvider.AzureFoundry => "Choose an Azure deployment to enable cleanup.",
                CleanupProvider.OpenAiCompatible => "Enter the endpoint URL and model name to enable cleanup.",
                _ => "Select a model to enable cleanup.",
            };
            SetStatus(CleanupStatus.Unavailable, detail);
            return;
        }

        if (startInit)
        {
            // Reflect the reboot in the status pill and stop CleanAsync from serving the old agent
            // (it gates on Ready) while the new provider/model spins up in the background.
            SetStatus(CleanupStatus.Initializing, "Applying new settings…");
            _log.LogInformation("AI cleanup enabled; preparing {Provider} in the background.", effective.Provider);
            _ = Task.Run(() => InitializeAsync(effective, initToken));
        }
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

        AIAgent? agent;
        CleanupOptions options;
        lock (_gate)
        {
            if (!_options.Enabled || _status != CleanupStatus.Ready || _agent is null)
            {
                // Distinguish "the user turned it off" from "the user turned it on and it is
                // broken". The second case previously looked identical in the logs, so a bad
                // endpoint or deployment name disabled cleanup for a whole session with no signal
                // beyond one startup warning the user had long scrolled past.
                var reason = !_options.Enabled
                    ? null
                    : $"AI cleanup is enabled but {_status} ({StatusDetail}).";
                return CleanupResult.Skip(text, reason);
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
        string? firstFailure = null;
        var reloadBudget = new ReloadBudget();

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var totalTimeout = CleanupTotalTimeoutOverride ??
            (CleanupTimeoutOverride is null ? TimeSpan.FromSeconds(TotalCleanupTimeoutSeconds) : Timeout.InfiniteTimeSpan);
        if (totalTimeout != Timeout.InfiniteTimeSpan)
        {
            totalCts.CancelAfter(totalTimeout);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            string cleanedChunk;
            string? error;
            try
            {
                (cleanedChunk, error) = await CleanChunkAsync(agent, options, chunks[i], reloadBudget, totalCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                firstFailure ??= "AI cleanup exceeded the total time limit.";
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
            return new CleanupResult(text, CleanupOutcome.Failed, firstFailure ?? "AI cleanup failed.");
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
        string? partial = null;
        if (failures > 0 || overflowTail is not null)
        {
            var parts = new List<string>(2);
            if (failures > 0)
            {
                parts.Add($"{failures} of {chunks.Count} segments failed ({firstFailure})");
            }

            if (overflowTail is not null)
            {
                parts.Add($"the remainder beyond the first {chunks.Count} segments was left raw");
            }

            partial = "Partial cleanup: " + string.Join("; ", parts) + ".";
        }

        return new CleanupResult(combined, outcome, partial);
    }

    /// <summary>
    /// Largest selection accepted for a text action. Beyond this the request would be chunked, and
    /// chunking a transform is not equivalent to chunking a cleanup: a per-chunk rewrite loses the
    /// document-level structure that "rewrite as a task list" or "format for Teams" exist to produce,
    /// and rejoining the pieces produces something no single instruction ever asked for. Refusing is
    /// honest; silently degrading is not.
    /// </summary>
    internal const int MaxTextActionChars = 12_000;

    /// <inheritdoc />
    public async Task<TextActionResult> ApplyActionAsync(
        string selection,
        TextActions.TextAction action,
        IReadOnlyList<DictionaryEntry>? glossaryEntries = null,
        string? spokenInstruction = null,
        string? writingStyleOverride = null,
        bool requireSingleLine = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (string.IsNullOrWhiteSpace(selection))
        {
            return TextActionResult.Failed(
                TextActionFailure.EmptySelection, "Select some text first.");
        }

        if (selection.Length > MaxTextActionChars)
        {
            return TextActionResult.Failed(
                TextActionFailure.TooLarge,
                $"That selection is too long for one request. Try {MaxTextActionChars:N0} characters or fewer.");
        }

        AIAgent agent;
        CleanupOptions options;
        lock (_gate)
        {
            if (!_options.Enabled)
            {
                return TextActionResult.Failed(
                    TextActionFailure.NotEnabled,
                    "Turn on AI cleanup in Settings to use this action.");
            }

            if (_status != CleanupStatus.Ready || _agentFactory is not { } factory)
            {
                return TextActionResult.Failed(
                    TextActionFailure.NotReady,
                    string.IsNullOrWhiteSpace(_statusDetail)
                        ? "The AI model is not ready yet."
                        : "The AI model is not ready yet: " + _statusDetail);
            }

            options = _options;

            // Small on-device models get the tighter glossary budget for the same reason cleanup does:
            // a long vocabulary list crowds out the text being transformed.
            var maxTerms = options.Provider == CleanupProvider.FoundryLocal
                ? CleanupPrompt.MaxGlossaryTermsLocal
                : CleanupPrompt.MaxGlossaryTermsCloud;

            // The local tier gets the compressed rulebook. Resolved by prompt style rather than by
            // provider alone, so a user pointing the bring-your-own endpoint at a small local server
            // can opt into it explicitly, exactly as cleanup already allows.
            var local = CleanupPrompt.ResolvePromptStyle(options.PromptStyle, options.Provider)
                == CleanupPromptStyle.Local;

            var systemPrompt = TextActions.TextActionPrompt.BuildSystemPrompt(
                action, glossaryEntries, maxTerms, writingStyleOverride, requireSingleLine, local);

            agent = factory(BuildAuxiliarySystemPrompt(options, systemPrompt));
        }

        var userMessage = TextActions.TextActionPrompt.BuildUserMessage(selection, spokenInstruction);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(AuxiliaryCompletionTimeoutSeconds));

            // Headroom over the selection so a legitimate expansion is not truncated mid-sentence,
            // which would otherwise reach the sanitizer as a plausible but silently cut answer.
            var chatOptions = new ChatOptions
            {
                MaxOutputTokens = Math.Clamp((selection.Length / 2) + 512, 512, 8192),
            };

            if (options.Provider == CleanupProvider.FoundryLocal)
            {
                chatOptions.Temperature = CleanupTemperature;
            }

            var runOptions = new ChatClientAgentRunOptions(chatOptions);
            var response = await agent.RunAsync(userMessage, options: runOptions, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            var verdict = TextActions.TextActionSanitizer.Sanitize(response.Text, selection, action);
            if (!verdict.Accepted)
            {
                _log.LogInformation(
                    "Text action {ActionId} rejected: {Reason}.", action.Id, verdict.Reason);
                return new TextActionResult(
                    null,
                    TextActionFailure.Rejected,
                    TextActions.TextActionSanitizer.Describe(verdict.Reason),
                    verdict.Reason);
            }

            return TextActionResult.Success(verdict.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TextActionResult.Failed(TextActionFailure.Cancelled, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return TextActionResult.Failed(
                TextActionFailure.CallFailed,
                $"The model did not answer within {AuxiliaryCompletionTimeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            // Never log the selection or the model's answer: both are the user's document content.
            _log.LogWarning(ex, "Text action {ActionId} failed at runtime.", action.Id);
            return TextActionResult.Failed(TextActionFailure.CallFailed, DescribeFailure(ex, options.Provider));
        }
    }

    public async Task<string?> CompleteAsync(
        string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt) || string.IsNullOrWhiteSpace(userMessage))
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            _log.LogWarning(ex, "Auxiliary AI completion failed; returning null.");
            return null;
        }
    }

    // Cleans a single chunk. Returns the cleaned text and a null error on success, or the raw chunk and
    // a human-readable error when the model call throws, times out, or returns nothing usable. Never
    // throws; a failed segment falls back to its raw text so dictation is never lost.
    private async Task<(string Text, string? Error)> CleanChunkAsync(
        AIAgent agent, CleanupOptions options, string chunk, ReloadBudget reload, CancellationToken cancellationToken)
    {
        // Azure and BYO endpoints share the longer budget: both may be a cloud round-trip to a
        // reasoning model whose hidden thinking precedes the visible rewrite.
        var isCloud = options.Provider is CleanupProvider.AzureFoundry or CleanupProvider.OpenAiCompatible;
        var budget = CleanupTimeoutOverride ?? TimeSpan.FromSeconds(
            isCloud ? AzureCleanupTimeoutSeconds : CleanupTimeoutSeconds);

        // A cloud connection that has sat idle can be silently dead: the request goes out and
        // nothing ever comes back, so the entire budget drains and the dictation falls back to raw
        // text, while an attempt moments later succeeds in about a second. Spending the whole
        // budget on one attempt makes that stall unrecoverable, so the first attempt gets a slice
        // large enough for any healthy call and a stall still leaves room to try again.
        // Benchmarks pin the timeout explicitly and want exactly one attempt.
        var retryOnStall = isCloud && CleanupTimeoutOverride is null;
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
                        Error = "The on-device cleanup model had been unloaded and is loading again. " +
                                "This dictation used raw text; give it a moment and try again.",
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
        _log.LogWarning(
            "Foundry Local evicted the cleanup model {Alias}; reloading it before falling back to raw text.",
            options.FoundryModelAlias);

        var reloaded = await LoadFoundryModelAsync(options.FoundryModelAlias, progress: null, ct)
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

        // A genuine failure: drop the stale Ready status so Settings stops claiming cleanup works.
        SetStatus(
            CleanupStatus.Unavailable,
            $"The on-device model '{options.FoundryModelAlias}' was unloaded and could not be reloaded.");
        return ReloadOutcome.Failed;
    }

    private enum ReloadOutcome
    {
        Reloaded,

        /// <summary>The load outlived this dictation's deadline; the model is likely ready shortly.</summary>
        StillLoading,

        Failed,
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
                return new ChunkAttempt(chunk, "AI cleanup returned no text.", false, false);
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
                return new ChunkAttempt(chunk, reason, false, false);
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
            _log.LogDebug(ex, "AI cleanup timed out for a segment.");
            return new ChunkAttempt(chunk, DescribeFailure(ex, options.Provider), true, false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "AI cleanup failed for a segment; using raw text.");
            return new ChunkAttempt(chunk, DescribeFailure(ex, options.Provider), false, IsModelNotLoaded(ex));
        }
    }

    private readonly record struct ChunkAttempt(string Text, string? Error, bool Stalled, bool Evicted);

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
    /// </summary>
    internal static string DescribeFailure(Exception ex, CleanupProvider provider)
    {
        if (ex is OperationCanceledException or TimeoutException)
        {
            return "AI cleanup timed out.";
        }

        var status = ExtractHttpStatus(ex);
        var detail = DescribeServerMessage(ex);

        if (IsModelNotLoaded(ex))
        {
            return provider == CleanupProvider.FoundryLocal
                ? "The on-device cleanup model is no longer loaded in Foundry Local, and reloading it " +
                  "failed. Something else likely evicted it (another app, or a model loaded from the " +
                  "foundry CLI). Reopen Settings and load the model again."
                : $"The endpoint reports that model is not loaded ({status}). {detail}".TrimEnd();
        }

        if (IsGpuShaderIncompatibility(ex))
        {
            return provider == CleanupProvider.FoundryLocal
                ? $"This model variant cannot run on this GPU. In Foundry Local, pick a CPU variant " +
                  $"of the model and try again. {detail}".TrimEnd()
                : $"This model variant cannot run on the endpoint GPU. Pick a CPU variant or use " +
                  $"a different model variant. {detail}".TrimEnd();
        }

        if (IsExecutionProviderUnavailable(ex))
        {
            return provider == CleanupProvider.FoundryLocal
                ? $"This model variant requires an execution provider that is not available on this PC. " +
                  $"Pick a different model in Settings. {detail}".TrimEnd()
                : $"This model variant requires an execution provider that is not available on the endpoint. " +
                  $"Pick a different model variant. {detail}".TrimEnd();
        }

        // The endpoint's own message is appended where it adds something; it is empty often enough
        // (a transport failure has no response body) that every branch has to survive without it.
        var described = status switch
        {
            400 => $"The AI endpoint rejected the request (400). {detail}",

            401 or 403 => provider switch
            {
                CleanupProvider.FoundryLocal => $"Foundry Local refused the request ({status}). {detail}",
                CleanupProvider.AzureFoundry =>
                    $"The AI endpoint rejected the Azure access ({status}). Check the sign-in and role " +
                    $"assignment, then try again. {detail}",
                _ => $"The AI endpoint rejected the credentials ({status}). Check the API key, then try again.",
            },

            404 => provider == CleanupProvider.FoundryLocal
                ? "Foundry Local no longer recognises the cleanup model (404). Reopen Settings and " +
                  "pick the model again."
                : $"The AI endpoint could not find that model (404). Check the model name. {detail}",

            429 => "The AI endpoint is throttling requests (429). Wait a moment and try again.",

            >= 500 => $"The AI endpoint returned a server error ({status}). This is usually transient. {detail}",

            _ when IsConnectivityFailure(ex) => provider == CleanupProvider.FoundryLocal
                ? "Couldn't reach Foundry Local. Make sure it is installed and running."
                : "Couldn't reach the AI endpoint. Check the endpoint URL and your network.",

            _ when status > 0 => $"The AI endpoint returned {status}. {detail}",

            // Last resort. Still better than the bare type name: the message usually names the fault.
            _ => $"AI cleanup error: {ex.GetType().Name}. {detail}",
        };

        return described.TrimEnd();
    }

    /// <summary>
    /// True when the failure is Foundry Local's "Model '&lt;id&gt;' is not loaded" 400, which it returns
    /// after evicting a resident model. Matched on the text because the endpoint reports it with a
    /// null error code, so the status alone cannot distinguish it from a malformed request. The raw
    /// response body is checked as well as the message: the message shape is a client-library detail,
    /// and losing this signal costs the reload that makes cleanup self-heal.
    /// </summary>
    internal static bool IsModelNotLoaded(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (MentionsUnloadedModel(current.Message) || MentionsUnloadedModel(ReadResponseBody(current)))
            {
                return true;
            }

            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(inner => IsModelNotLoaded(inner)))
            {
                return true;
            }
        }

        return false;
    }

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

    private static bool IsGpuShaderIncompatibility(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (MentionsGpuShaderIncompatibility(current.Message) ||
                MentionsGpuShaderIncompatibility(ReadResponseBody(current)))
            {
                return true;
            }

            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(inner => IsGpuShaderIncompatibility(inner)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExecutionProviderUnavailable(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (MentionsExecutionProviderUnavailable(current.Message) ||
                MentionsExecutionProviderUnavailable(ReadResponseBody(current)))
            {
                return true;
            }

            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(inner => IsExecutionProviderUnavailable(inner)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryParseRequiredExecutionProvider(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (TryParseRequiredExecutionProvider(current.Message) is { } parsed)
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
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (TryParseAvailableExecutionProviders(current.Message) is { Length: > 0 } parsed)
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
        try
        {
            if (!FoundryLocalManager.IsInitialized)
            {
                try
                {
                    await FoundryLocalManager.CreateAsync(CreateFoundryConfiguration(), _log, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Raced with another initializer; the singleton is already created.
                }
            }

            _manager ??= FoundryLocalManager.Instance;
            return _manager is not null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Foundry Local availability probe failed.");
            return false;
        }
    }

    public async Task<IReadOnlyList<FoundryModelOption>> ListFoundryModelsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return Array.Empty<FoundryModelOption>();
        }

        try
        {
            // Listing only reads the catalog, so it deliberately does not take the init lock; that
            // way the picker stays responsive even while a model is downloading under InitializeAsync.
            await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (_catalog is null)
            {
                return Array.Empty<FoundryModelOption>();
            }

            var all = await _catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var cached = await _catalog.GetCachedModelsAsync(cancellationToken).ConfigureAwait(false);
            var loaded = await _catalog.GetLoadedModelsAsync(cancellationToken).ConfigureAwait(false);

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
        catch (OperationCanceledException)
        {
            return Array.Empty<FoundryModelOption>();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Listing Foundry Local models failed.");
            return Array.Empty<FoundryModelOption>();
        }
    }

    public async Task<string?> GetLoadedFoundryModelAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (_catalog is null)
            {
                return null;
            }

            var loaded = await _catalog.GetLoadedModelsAsync(cancellationToken).ConfigureAwait(false);
            return loaded.Count > 0 ? loaded[0].Alias : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Reading the loaded Foundry Local model failed.");
            return null;
        }
    }

    public async Task<bool> LoadFoundryModelAsync(
        string alias, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        alias = alias.Trim();
        var acquired = false;
        var reconcile = false;
        var cancelledConfigure = false;
        string[]? loadedIdentifiers = null;
        try
        {
            // An explicit Load must not queue behind a background readiness probe, which can hold
            // the lock for minutes on a large on-device model. Cancelling the in-flight
            // initialization releases it promptly; the reconcile at the end of this method rebuilds
            // the agent once the requested model is resident, so nothing is lost.
            cancelledConfigure = CancelPendingConfigure();

            // Serialize with InitializeAsync and other load/unload calls so the runtime is never asked
            // to hold two models at once.
            progress?.Report("Starting Foundry Local…");
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            await EnsureManagerAsync(cancellationToken).ConfigureAwait(false);
            if (_catalog is null)
            {
                progress?.Report("Foundry Local could not be initialized.");
                return false;
            }

            var model = await ResolveFoundryModelAsync(alias, cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                progress?.Report($"Model '{alias}' was not found in the Foundry catalog.");
                return false;
            }

            await UnloadOtherFoundryModelsAsync(model.Id, model.Alias, cancellationToken).ConfigureAwait(false);

            if (!await model.IsCachedAsync(cancellationToken).ConfigureAwait(false))
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
                }, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report($"Loading {alias}…");
            await model.LoadAsync(cancellationToken).ConfigureAwait(false);
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
            _log.LogWarning(ex, "Loading Foundry Local model {Alias} failed.", alias);

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
            if (acquired)
            {
                _initLock.Release();
            }

            // Reconcile outside the init lock: loading a different model evicts the one cleanup was
            // using, and reloading the configured model should turn cleanup back on.
            var recovered = false;
            if (reconcile)
            {
                recovered = ReconcileCleanupAfterResidentChange(
                    loadedAlias: alias,
                    unloadedAlias: null,
                    unloadedAll: false,
                    loadedIdentifiers: loadedIdentifiers,
                    afterCancelledInit: cancelledConfigure);
            }

            // Recovery keys off whether anything published a terminal status, not off whether the
            // load succeeded. A load while cleanup points at a cloud provider returns from reconcile
            // immediately, and a failed load never reconciles at all; both would otherwise leave
            // cleanup stuck on "Applying new settings…" and silently emitting raw text.
            if (cancelledConfigure && !recovered)
            {
                RestartCancelledConfigure();
            }
        }
    }

    public async Task<bool> UnloadFoundryModelAsync(string? alias, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        var acquired = false;
        var reconcile = false;
        string? trimmed = null;
        try
        {
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (_catalog is null)
            {
                return false;
            }

            trimmed = alias?.Trim();
            var loaded = await _catalog.GetLoadedModelsAsync(cancellationToken).ConfigureAwait(false);
            var unloadedAny = false;
            foreach (var model in loaded)
            {
                if (!string.IsNullOrWhiteSpace(trimmed) &&
                    !string.Equals(model.Alias, trimmed, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(model.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await model.UnloadAsync(cancellationToken).ConfigureAwait(false);
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
            _log.LogWarning(ex, "Unloading Foundry Local model {Alias} failed.", alias);
            return false;
        }
        finally
        {
            if (acquired)
            {
                _initLock.Release();
            }

            if (reconcile)
            {
                ReconcileCleanupAfterResidentChange(
                    loadedAlias: null, unloadedAlias: trimmed, unloadedAll: string.IsNullOrWhiteSpace(trimmed));
            }
        }
    }

    // Unloads every loaded Foundry model except the target so only one stays resident at a time.
    // Best-effort: a failure to unload one model never blocks loading the requested one.
    private async Task UnloadOtherFoundryModelsAsync(string keepId, string keepAlias, CancellationToken ct)
    {
        if (_catalog is null)
        {
            return;
        }

        try
        {
            var loaded = await _catalog.GetLoadedModelsAsync(ct).ConfigureAwait(false);
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
                    _log.LogDebug(ex, "Could not unload Foundry model {Alias}.", other.Alias);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not enumerate loaded Foundry models.");
        }
    }

    // After a manual load/unload changes which Foundry Local model is resident, keep the cleanup agent
    // honest: drop it (and surface a clear status) when its configured model was just evicted so
    // CleanAsync can't call an unloaded model, and rebuild it when the configured model is loaded back
    // in, all without forcing a settings save. No-op for Azure or disabled cleanup. Must be called
    // WITHOUT holding _initLock, because a rebuild starts a background InitializeAsync that takes it.
    /// <summary>
    /// Cancels an initialization already in flight and reports whether it did. Used when an
    /// explicit user action must take the init lock without waiting out a probe.
    /// <para>
    /// The caller must pass the result to <see cref="ReconcileCleanupAfterResidentChange"/> as
    /// <c>forceRebuild</c>. <see cref="InitializeAsync"/> swallows cancellation on the assumption
    /// that a newer Configure owns the status from then on, which is true when Configure cancels
    /// but not here: nothing else would move the status off Initializing, and the reconcile refuses
    /// to rebuild while it reads Initializing, so cleanup would sit there forever.
    /// </para>
    /// </summary>
    private bool CancelPendingConfigure()
    {
        lock (_gate)
        {
            if (_configureCts is { IsCancellationRequested: false } cts)
            {
                cts.Cancel();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Re-runs the configuration that <see cref="CancelPendingConfigure"/> interrupted. Configure
    /// restarts initialization whenever the current status is anything but Ready, so passing the
    /// live options back through it recovers an interrupted run; when the status is already Ready
    /// (the cancelled token belonged to a run that had finished) it correctly does nothing.
    /// </summary>
    private void RestartCancelledConfigure()
    {
        CleanupOptions options;
        lock (_gate)
        {
            options = _options;
        }

        if (options.Enabled && options.IsActionable)
        {
            Configure(options);
        }
    }

    private bool ReconcileCleanupAfterResidentChange(
        string? loadedAlias,
        string? unloadedAlias,
        bool unloadedAll,
        IReadOnlyCollection<string>? loadedIdentifiers = null,
        bool afterCancelledInit = false)
    {
        if (_disposed)
        {
            return false;
        }

        var invalidate = false;
        var rebuild = false;
        var options = CleanupOptions.Disabled;
        CancellationToken initToken = default;

        lock (_gate)
        {
            options = _options;
            if (options.Provider != CleanupProvider.FoundryLocal || !options.Enabled || !options.IsActionable)
            {
                return false;
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

            // Normally an agent-less state means some other path already owns the status. That is
            // not true after this caller cancelled an initialization: the agent was dropped and the
            // status left on Initializing by a run that no longer exists, so both branches have to
            // treat that as stale rather than as somebody else's business.
            if (evicted && (_agent is not null || afterCancelledInit))
            {
                DropAgents();
                invalidate = true;
            }
            else if (nowResident && _agent is null &&
                     (afterCancelledInit ||
                      _status is not (CleanupStatus.Ready or CleanupStatus.Initializing or CleanupStatus.Downloading)))
            {
                _configureCts?.Cancel();
                _configureCts = new CancellationTokenSource();
                initToken = _configureCts.Token;
                rebuild = true;
            }
        }

        if (invalidate)
        {
            SetStatus(CleanupStatus.Unavailable,
                "The on-device cleanup model was unloaded. Reload it to turn cleanup back on.");
            _log.LogInformation("Cleanup paused: its Foundry Local model is no longer resident.");
        }
        else if (rebuild)
        {
            SetStatus(CleanupStatus.Initializing, "Re-enabling cleanup with the reloaded model…");
            _log.LogInformation("Rebuilding cleanup agent after its Foundry Local model was reloaded.");
            _ = Task.Run(() => InitializeAsync(options, initToken));
        }

        // Whether this call published a terminal status. The caller uses it to decide if cleanup
        // still needs recovering after an initialization it cancelled.
        return invalidate || rebuild;
    }

    private async Task InitializeAsync(CleanupOptions options, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            ct.ThrowIfCancellationRequested();

            AIAgent? agent;
            try
            {
                agent = options.Provider switch
                {
                    CleanupProvider.AzureFoundry => await InitAzureAsync(options, ct).ConfigureAwait(false),
                    CleanupProvider.OpenAiCompatible => await InitOpenAiCompatibleAsync(options, ct).ConfigureAwait(false),
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
                    _log.LogWarning(ex, "Foundry Local model {Alias} could not load because an execution provider is unavailable.",
                        options.FoundryModelAlias);
                    SetStatus(CleanupStatus.Unavailable, message);
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
                else
                {
                    lock (_gate)
                    {
                        if (!_options.Enabled || _options != options)
                        {
                            return;
                        }

                        DropAgents();
                        _pendingFactory = null;
                    }

                    if (probeFailure.Exception is { } ex)
                    {
                        _log.LogWarning(ex, "AI cleanup initialization probe failed ({Provider}).", options.Provider);
                    }
                    else
                    {
                        _log.LogWarning("AI cleanup initialization probe failed ({Provider}): {Message}",
                            options.Provider, probeFailure.Message);
                    }

                    SetStatus(CleanupStatus.Unavailable, probeFailure.Message);
                    return;
                }
            }

            lock (_gate)
            {
                // A newer Configure (different provider/model, or disabled) may have superseded this run.
                if (!_options.Enabled || _options != options)
                {
                    return;
                }

                _options = options;
                _agent = agent;
                _agentFactory = _pendingFactory;
                _styleAgents.Clear();
            }

            SetStatus(CleanupStatus.Ready, ReadyDetail(options));
            _log.LogInformation("AI cleanup ready ({Provider}).", options.Provider);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Configure or the feature was disabled; the newer call owns status.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI cleanup initialization failed ({Provider}).", options.Provider);
            SetStatus(CleanupStatus.Unavailable, "AI cleanup could not start. Dictation continues with raw text.");
        }
        finally
        {
            if (acquired)
            {
                _initLock.Release();
            }
        }
    }

    private sealed record AgentProbeFailure(string Message, Exception? Exception);

    private sealed record FoundryDemotionResult(CleanupOptions Options, AIAgent Agent);

    private sealed record FoundryExecutionProviderFailure(
        string? RequiredProvider,
        IReadOnlyList<string> AvailableProviders);

    private static IChatClient DisableStoredOutput(IChatClient client) =>
        new StoredOutputDisabledChatClient(client);

    internal static ChatOptions WithStoredOutputDisabled(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();
        var innerFactory = clone.RawRepresentationFactory;
        clone.RawRepresentationFactory = client =>
        {
            var raw = innerFactory?.Invoke(client);
#pragma warning disable OPENAI001
            if (raw is CreateResponseOptions responseOptions)
            {
                responseOptions.StoredOutputEnabled = false;
                return responseOptions;
            }

            // Fail CLOSED. Passing an unrecognised object through would let Azure fall back to its
            // store=true default and silently retain dictated text, which is the exact outcome this
            // exists to prevent. Nothing in Scribe sets a factory today, so this only triggers if a
            // future package version starts supplying one, and a privacy control must not lapse on a
            // dependency bump.
            return new CreateResponseOptions { StoredOutputEnabled = false };
#pragma warning restore OPENAI001
        };
        return clone;
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
                MaxOutputTokens = InitProbeMaxOutputTokens,
            };
            if (options.Provider == CleanupProvider.FoundryLocal)
            {
                chatOptions.Temperature = CleanupTemperature;
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
            return new AgentProbeFailure("AI cleanup validation timed out.", ex);
        }
        catch (Exception ex)
        {
            return new AgentProbeFailure(DescribeFailure(ex, options.Provider), ex);
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
            _log.LogDebug(ex, "Could not resolve the Foundry Local variant id for {Alias}.", options.FoundryModelAlias);
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
                loadFailure,
                "Foundry Local GPU model {GpuAlias} could not load, but no CPU counterpart was found in the catalog.",
                sourceAlias);
            return null;
        }

        var demotedOptions = options with { FoundryModelAlias = cpuAlias };
        _log.LogWarning(
            loadFailure,
            "Foundry Local GPU model {GpuAlias} could not load because an execution provider is unavailable. Retrying once with CPU model {CpuAlias}.",
            sourceAlias,
            cpuAlias);

        AIAgent? agent;
        try
        {
            agent = await InitFoundryAsync(demotedOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Foundry Local CPU demotion load failed for {CpuAlias}.", cpuAlias);
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
            probeFailure.Exception,
            "Foundry Local GPU model {GpuAlias} failed the shader probe. Retrying once with CPU model {CpuAlias}.",
            sourceAlias,
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
                _log.LogWarning(ex,
                    "Foundry Local CPU demotion probe failed for {CpuAlias} after {GpuAlias} failed the shader probe.",
                    cpuAlias, sourceAlias);
            }
            else
            {
                _log.LogWarning(
                    "Foundry Local CPU demotion probe failed for {CpuAlias} after {GpuAlias} failed the shader probe: {Message}",
                    cpuAlias, sourceAlias, cpuFailure.Message);
            }

            SetStatus(CleanupStatus.Unavailable,
                $"Neither the GPU nor the CPU build of this model would run. Pick a different model in Settings. {cpuFailure.Message}".TrimEnd());
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
                _log.LogDebug(lookupEx, "Could not read Foundry model runtime metadata for {Alias}.", alias);
            }
        }

        required ??= TryParseRequiredExecutionProvider(ex);
        var available = _availableExecutionProviders.Length > 0
            ? _availableExecutionProviders
            : TryParseAvailableExecutionProviders(ex);

        return new FoundryExecutionProviderFailure(required, available);
    }

    private async Task<AIAgent?> InitFoundryAsync(CleanupOptions options, CancellationToken ct)
    {
        var alias = options.FoundryModelAlias;
        SetStatus(CleanupStatus.Initializing, "Starting Foundry Local…");
        await EnsureManagerAsync(ct).ConfigureAwait(false);

        if (_catalog is null || _openAiClient is null)
        {
            SetStatus(CleanupStatus.Unavailable, "Foundry Local could not be initialized.");
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

            SetStatus(CleanupStatus.Unavailable, $"Model '{alias}' was not found in the Foundry catalog.");
            return null;
        }
        _log.LogInformation("Foundry Local cleanup model resolved to {ModelId}.", model.Id);

        var cached = await model.IsCachedAsync(ct).ConfigureAwait(false);
        if (!cached)
        {
            _lastReportedPct = -1;
            SetStatus(CleanupStatus.Downloading, $"Downloading {alias}…");
            await model.DownloadAsync(progress => OnDownloadProgress(alias, progress), ct).ConfigureAwait(false);
        }

        // Keep only one model resident: unload any previously-loaded model before loading this one.
        await UnloadOtherFoundryModelsAsync(model.Id, model.Alias, ct).ConfigureAwait(false);

        SetStatus(CleanupStatus.Downloading, $"Loading {alias}…");
        await model.LoadAsync(ct).ConfigureAwait(false);

        // Present the on-device OpenAI-compatible chat client as an Agent Framework agent so the
        // cleanup call site is identical to the Azure path.
        var chatClient = _openAiClient.GetChatClient(model.Id);
        _pendingFactory = instructions => chatClient.AsAIAgent(instructions: instructions, name: AgentName);
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
            _log.LogDebug(ex, "Could not check Foundry Local variant compatibility for {Alias}.", requestedAlias);
        }
    }

    private bool IsExecutionProviderAvailable(string? executionProvider)
    {
        var provider = executionProvider?.Trim();
        return !string.IsNullOrEmpty(provider) &&
            _availableExecutionProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
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
            SetStatus(CleanupStatus.Unavailable, "Enter the endpoint URL and model name to enable cleanup.");
            return Task.FromResult<AIAgent?>(null);
        }

        if (!TryValidateCustomEndpoint(options.CustomEndpoint, out var endpointUri, out var endpointError))
        {
            SetStatus(CleanupStatus.Unavailable, endpointError);
            return Task.FromResult<AIAgent?>(null);
        }

        SetStatus(CleanupStatus.Initializing, $"Connecting to {endpointUri.Host}…");

        var key = string.IsNullOrWhiteSpace(options.CustomApiKey) ? "not-needed" : options.CustomApiKey!;
        var client = new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = endpointUri });
        var chatClient = client.GetChatClient(options.CustomModel!.Trim());
        _pendingFactory = instructions => chatClient.AsAIAgent(instructions: instructions, name: AgentName);
        return Task.FromResult<AIAgent?>(_pendingFactory(BuildSystemPrompt(options)));
    }

    private Task<AIAgent?> InitAzureAsync(CleanupOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.AzureEndpoint) || string.IsNullOrWhiteSpace(options.AzureDeployment))
        {
            SetStatus(CleanupStatus.Unavailable, "Choose an Azure deployment to enable cleanup.");
            return Task.FromResult<AIAgent?>(null);
        }

        if (!Uri.TryCreate(options.AzureEndpoint, UriKind.Absolute, out var endpointUri))
        {
            SetStatus(CleanupStatus.Unavailable, "The Azure endpoint is not a valid URL.");
            return Task.FromResult<AIAgent?>(null);
        }

        SetStatus(CleanupStatus.Initializing, $"Connecting to Azure deployment '{options.AzureDeployment}'…");

        var instructions = BuildSystemPrompt(options);
        var useKey = !string.IsNullOrWhiteSpace(options.AzureApiKey);
        var networkTimeout = CleanupTimeoutOverride is { } timeout
            ? timeout + TimeSpan.FromSeconds(5)
            : (TimeSpan?)null;

        // A Microsoft Foundry *project* endpoint (…/api/projects/…) has a different shape from a
        // classic Azure OpenAI account endpoint and needs the project Responses client.
        var isProject = endpointUri.AbsolutePath.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase);

        AIAgent agent;
        if (isProject && !useKey)
        {
            // Native Foundry path: the project client uses the project data plane directly without
            // creating a server-side agent resource.
            // The project data-plane requires an AAD token, so this path is AAD-only.
            var credential = AzureCredentialFactory.Create(new AzureCredentialRequest(
                options.AzureAuthMode,
                options.AzureTenantId,
                options.AzureSubscriptionId,
                options.AzureClientId,
                options.AzureClientSecret));
            var project = new AIProjectClient(endpointUri, credential);
            _pendingFactory = i => project.AsAIAgent(
                model: options.AzureDeployment!,
                instructions: i,
                name: AgentName,
                clientFactory: DisableStoredOutput);
            agent = _pendingFactory(instructions);
        }
        else
        {
            // Classic Azure OpenAI account endpoint, or a project endpoint paired with an API key
            // (the project data-plane can't use keys, so fall back to the account host for key auth).
            var accountHost = isProject
                ? new Uri($"{endpointUri.Scheme}://{endpointUri.Authority}/")
                : endpointUri;

            // When the user supplies an API key, authenticate with it directly; otherwise reuse the
            // existing Azure CLI sign-in, pinned to the selected subscription when one is saved.
            // Route cleanup through the Azure OpenAI **Responses API** rather than Chat Completions.
            // Responses is the forward-looking surface and is the only one that serves the newest
            // reasoning models (e.g. gpt-5.x "pro"/o-series); Chat Completions returns HTTP 400
            // "operation unsupported" for those. The unified v1 endpoint lets the current OpenAI
            // client handle Azure directly while preserving API-key and Microsoft Entra auth.
#pragma warning disable OPENAI001
            var responses = useKey
                ? AzureOpenAIResponsesClientFactory.CreateWithApiKey(
                    accountHost,
                    options.AzureApiKey!,
                    networkTimeout,
                    DisableRetries)
                : AzureOpenAIResponsesClientFactory.CreateWithTokenCredential(
                    accountHost,
                    AzureCredentialFactory.Create(new AzureCredentialRequest(
                        options.AzureAuthMode,
                        options.AzureTenantId,
                        options.AzureSubscriptionId,
                        options.AzureClientId,
                        options.AzureClientSecret)),
                    networkTimeout,
                    DisableRetries);
            _pendingFactory = i => responses.AsAIAgent(
                model: options.AzureDeployment!,
                instructions: i,
                name: AgentName,
                clientFactory: DisableStoredOutput);
#pragma warning restore OPENAI001
            agent = _pendingFactory(instructions);
        }

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
    /// </summary>
    internal static int ExtractHttpStatus(Exception? ex) => ExtractHttpStatus(ex, depth: 0);

    // Depth-bounded because this runs on the failure path: an AggregateException whose inner list
    // reaches back to an ancestor would recurse until the stack overflows, and a StackOverflowException
    // cannot be caught, so a diagnostics helper would take the process down while reporting an error
    // the user could otherwise have acted on. Real Azure exception chains are a handful deep.
    private const int MaxStatusSearchDepth = 16;

    private static int ExtractHttpStatus(Exception? ex, int depth)
    {
        if (depth >= MaxStatusSearchDepth)
        {
            return 0;
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.ClientModel.ClientResultException client:
                    return client.Status;
                case Azure.RequestFailedException request:
                    return request.Status;
                case AggregateException aggregate:
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        var nested = ExtractHttpStatus(inner, depth + 1);
                        if (nested != 0)
                        {
                            return nested;
                        }
                    }

                    break;
                }
            }
        }

        return 0;
    }

    // Ensures the Foundry Local manager + catalog exist, without starting the web service or
    // downloading execution providers. This is enough to list, load and unload models, and is the
    // Builds the process-wide Foundry Local configuration. The SDK requires an explicit web-service
    // configuration; when it is omitted, StartWebServiceAsync throws "Web service configuration was
    // not provided" and never populates manager.Urls. We bind the local OpenAI-compatible service to
    // a loopback address on an OS-assigned port (":0") so it never collides with a foundry CLI service
    // or a second Scribe process; manager.Urls then reports the port it actually bound.
    private static FoundryConfiguration CreateFoundryConfiguration() => new()
    {
        AppName = "Scribe",
        LogLevel = FoundryLogLevel.Warning,
        Web = new FoundryConfiguration.WebService { Urls = "http://127.0.0.1:0" },
    };

    // Shared first step of the heavier EnsureManagerAsync. Safe to call concurrently: manager
    // creation is idempotent (the SDK exposes a process-wide singleton) and the catalog read is
    // cached. Execution providers are registered here, BEFORE the first catalog read, because the
    // SDK populates the catalog from the currently-registered EPs and caches it on first use --
    // fetching it earlier would silently lock every consumer (the model picker and inference) into
    // a CPU-only catalog even on a CUDA / TensorRT-RTX machine.
    private async Task EnsureCatalogAsync(CancellationToken ct)
    {
        if (_manager is not null && _catalog is not null)
        {
            return;
        }

        if (_manager is null)
        {
            if (!FoundryLocalManager.IsInitialized)
            {
                try
                {
                    await FoundryLocalManager.CreateAsync(CreateFoundryConfiguration(), _log, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Already created in this process; reuse the singleton below.
                }
            }

            _manager = FoundryLocalManager.Instance;
        }

        await EnsureExecutionProvidersAsync(ct).ConfigureAwait(false);

        _catalog ??= await _manager.GetCatalogAsync(ct).ConfigureAwait(false);
    }

    // Registers the best available hardware execution providers (e.g. CUDA / TensorRT-RTX) once per
    // manager instance. Best-effort: if EP setup fails the model still runs on CPU, so we log and
    // continue. Must run before GetCatalogAsync so hardware-accelerated model variants are listed.
    private async Task EnsureExecutionProvidersAsync(CancellationToken ct)
    {
        if (_epsRegistered)
        {
            return;
        }

        try
        {
            var discovered = _manager!.DiscoverEps();
            var result = await _manager.DownloadAndRegisterEpsAsync(ct).ConfigureAwait(false);
            _availableExecutionProviders = MergeAvailableExecutionProviders(
                discovered,
                result.RegisteredEps,
                _manager.DiscoverEps());

            // Latch only on success. Under the WinML package these plugins come from the OS and
            // Windows Update, so a first-run network hiccup is an ordinary transient failure. Setting
            // this unconditionally would turn that into a permanent CPU-only session, because the
            // catalog is populated from the registered providers and cached on first read.
            _epsRegistered = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer Configure superseded this run. Callers already handle cancellation, and the
            // next initialization must be free to try registration again.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Foundry execution-provider setup was skipped; continuing on available providers.");
            try
            {
                _availableExecutionProviders = MergeAvailableExecutionProviders(_manager!.DiscoverEps());
            }
            catch (Exception discoverEx)
            {
                _log.LogDebug(discoverEx, "Could not enumerate Foundry execution providers.");
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

    private async Task EnsureManagerAsync(CancellationToken ct)
    {
        if (_managerReady && _manager is not null && _catalog is not null && _openAiClient is not null)
        {
            return;
        }

        await EnsureCatalogAsync(ct).ConfigureAwait(false);
        var manager = _manager!;

        // Execution providers were registered inside EnsureCatalogAsync, before the catalog read.
        // Start (or attach to) the local OpenAI-compatible web service, then read the endpoint it
        // actually bound to rather than assuming a port.
        try
        {
            await manager.StartWebServiceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "StartWebServiceAsync reported an issue; using the existing endpoint if available.");
        }

        var urls = manager.Urls;
        var baseUrl = urls is { Length: > 0 } ? urls[0] : null;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Foundry Local did not expose a web-service endpoint.");
        }

        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        // Foundry Local does not require a real API key; the credential is a placeholder.
        _openAiClient = new OpenAIClient(
            new ApiKeyCredential("foundry-local"),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });
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
        SetStatus(CleanupStatus.Downloading, $"Downloading {alias}… {Math.Clamp(pct, 0, 100)}%");
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

    private static string ReadyDetail(CleanupOptions options) => options.Provider switch
    {
        CleanupProvider.AzureFoundry => $"Azure deployment '{options.AzureDeployment}' ready.",
        CleanupProvider.OpenAiCompatible =>
            $"'{options.CustomModel}' at {(Uri.TryCreate(options.CustomEndpoint, UriKind.Absolute, out var u) ? u.Host : "custom endpoint")} ready.",
        _ => $"{CleanupModelCatalog.Resolve(options.FoundryModelAlias).DisplayName} ready.",
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

        // Azure cleanup often runs on reasoning models whose hidden thinking tokens count against this
        // same budget, so a tight cap would truncate the visible answer. Give a generous ceiling.
        if (provider == CleanupProvider.AzureFoundry)
        {
            var azureEstimate = (words * 4) + 512;
            return Math.Clamp(azureEstimate, 512, 16384);
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
            _log.LogDebug(ex, "Could not clear the stale Foundry Local demotion marker.");
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
            _log.LogDebug(ex, "Could not persist the Foundry Local model demotion marker.");
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
            _log.LogDebug(ex, "Could not read the Foundry Local model demotion marker.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SetStatus(CleanupStatus status, string? detail)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != status || !string.Equals(_statusDetail, detail, StringComparison.Ordinal);
            _status = status;
            _statusDetail = detail;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "A cleanup StatusChanged handler threw.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _configureCts;
            _configureCts = null;
            DropAgents();
        }

        try { cts?.Cancel(); } catch { /* best effort */ }
        cts?.Dispose();
        try { _manager?.Dispose(); } catch { /* best effort */ }
        _initLock.Dispose();

        return ValueTask.CompletedTask;
    }
}
