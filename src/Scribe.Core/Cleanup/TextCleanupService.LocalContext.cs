using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Cleanup;

/*
 * A model on this PC reads a limited context, and every request has to fit in it.
 *
 * Ollama keeps only the end of a prompt that runs past its context, which once dropped every instruction (see
 * LocalAiServer), and LM Studio and Foundry Local refuse or cut such a prompt too. So for the apps whose context Scribe can
 * learn, Foundry Local and Ollama and LM Studio at their own addresses (FitsLocalContext), each dictation is planned before
 * it is sent (PlanLocalRequestLocked): the dictated text and the longest answer each request allows take their room first,
 * in chunks small enough to leave some, and the vocabulary gets what is left (ContextBudget, CleanupPrompt.FitGlossary).
 * With "Send your whole vocabulary when it fits" on (CleanupOptions.SendWholeVocabulary), the whole vocabulary goes when it
 * fits, and the readying request a recording starts with carries as much of it as fits, so Ollama and LM Studio have read
 * it by the time the dictation arrives. Any other server on this PC gets what every release since 0.5.2 sent it: Scribe
 * cannot learn its context.
 *
 * The context a request is fitted into (EffectiveContextLocked):
 * - Ollama with a size: the size asked, capped at the largest the model was made for (/api/show; or what /api/ps says after
 *   one of Scribe's own requests, when that is smaller, which is Ollama's own cap). Each of Scribe's requests asks for it
 *   (num_ctx, through Ollama's own chat API, the only one that takes a size), and Ollama loads the model at it, whatever
 *   another app's requests loaded it at in between, so what /api/ps says before Scribe's own request is never used for it.
 * - Ollama with its own setting: what /api/ps says after one of Scribe's own requests reached the model, which is what
 *   Ollama's own setting gives Scribe's requests. A reading before one may be another app's copy at its own size, which
 *   Scribe's request then replaces, so it can only lower what is known (NoteObservedContext).
 * - LM Studio: the size of the copy that requests by the model's name reach (any reading, and the size Scribe loaded a copy
 *   at), which a request never reloads. Not the size asked before a copy at it is confirmed: a load at it may fail, and LM
 *   Studio then loads the model at its own size.
 * - Foundry Local: what its model's folder says (FoundryContextTokensAsync).
 * - Before any of that is known, ContextBudget.AssumedContextTokens, Ollama's and LM Studio's smallest default.
 *
 * LM Studio takes a size only as it loads a model, so with a size chosen Scribe loads the model at it through LM Studio's
 * own chat API before a request would load it at LM Studio's own (ReconcileLmStudioAsync). LM Studio keeps a model loaded
 * that way for its own idle time, an hour by default, whatever Scribe's requests ask, so Scribe frees it itself after the
 * idle time and as it closes.
 */
internal sealed partial class TextCleanupService
{
    // What the model on this PC was found to read, for the configuration it was learned under (compared apart from what the
    // prompt says). Guarded by _gate.
    private int _localContextTokens;
    private CleanupOptions? _localContextFor;

    // Ollama with a size: the largest context the model was made for, for the configuration it was read under. Guarded by
    // _gate.
    private int _ollamaModelMaxTokens;
    private CleanupOptions? _ollamaModelMaxFor;

    // LM Studio: the instance Scribe loaded at a size, and the configuration it loaded it for. LM Studio keeps such an
    // instance for its own idle time, not Scribe's, so Scribe frees it itself. Guarded by _gate.
    private string? _scribeLoadedInstance;
    private CleanupOptions? _scribeLoadedFor;

    // LM Studio: the size it refused to load the model at, and the configuration it refused it for. Without it, every
    // recording would find the copy LM Studio loaded at its own size to be "another size", unload it and ask for the refused
    // size again. Ends when a copy at that size is held, or when a different configuration is served. Guarded by _gate.
    private CleanupOptions? _lmStudioRefusedFor;
    private int _lmStudioRefusedTarget;

    /*
     * One LM Studio load at a time, with what follows it: the read it decides by, unloading a copy at another size, the
     * load, and settling the copy it made (SettleLoadedCopyAsync). A load Scribe stopped waiting for (its configuration was
     * replaced, or its time ran out) keeps the lane until it is settled (SettleLoadInBackground), so the next configuration
     * reads LM Studio only once that copy is Scribe's or gone, and never loads a second copy beside it or probes a copy
     * about to be unloaded. Test connection holds it across its own read and load, not its readiness check. Never held while
     * waiting for the model uses in flight, so a release (which waits for them) and the lane cannot wait on each other.
     */
    private readonly SemaphoreSlim _lmStudioLane = new(1, 1);

    /*
     * LM Studio copies Scribe loaded that no configuration owns: one Test connection loaded, one whose settings were replaced
     * while it loaded, one an earlier configuration owned, and one LM Studio would not unload when asked. LM Studio would
     * keep each for its own hour, so Scribe unloads them itself, and keeps asking: at the next recording's check
     * (ReconcileLmStudioAsync, which takes one at the size the settings in use ask for instead of loading another), at the
     * idle and pause releases (StartUnownedRetirement), and as Scribe closes. One is forgotten only once LM Studio has
     * unloaded it or no longer lists it, never to make room: each is a copy LM Studio holds. Guarded by _gate.
     */
    private readonly List<UnownedCopy> _unownedCopies = [];

    private sealed record UnownedCopy(string Endpoint, string Model, string Instance, int ContextTokens, string? ApiKey);

    // One handler for every Ollama client this process builds, as the OpenAI clients share one transport: the hand-off in
    // front of a handler that follows no redirect and uses no proxy, since it only ever reaches Ollama's own address on this
    // PC (as LocalServerClient's requests do).
    private static readonly Lazy<HttpMessageHandler> SharedOllamaHandler = new(() =>
        new VocabularyHandOffHandler(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }));

    private HttpMessageHandler? _testOllamaHandler;

    /// <summary>The shortest chunk a dictation is split into to fit a small context, in characters.</summary>
    internal const int MinLocalChunkChars = 300;

    /// <summary>
    /// The context the model on this PC reads for the configuration AI cleanup serves, in tokens, as far as Scribe knows it
    /// (see the comment at the top of this file); 0 when cleanup does not run on an app whose context Scribe learns, or
    /// nothing is known yet.
    /// </summary>
    public int LocalContextTokens
    {
        get
        {
            lock (_gate)
            {
                return _options.Enabled && FitsLocalContext(_options) ? EffectiveContextLocked(_options) ?? 0 : 0;
            }
        }
    }

    // The apps whose context Scribe learns, and so fits every request into: Foundry Local, and Ollama and LM Studio at their
    // own addresses.
    private static bool FitsLocalContext(CleanupOptions options) =>
        options.Provider == CleanupProvider.FoundryLocal ||
        LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) != LocalServerApp.None;

    // The size Scribe asks Ollama or LM Studio for; null leaves the context to the app's own setting.
    private static int? AskedContextTokens(CleanupOptions options) =>
        options.LocalContextTokens is > 0 and var tokens &&
        LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) is LocalServerApp.Ollama or LocalServerApp.LmStudio
            ? ContextBudget.Sanitize(tokens)
            : null;

    // Ollama with a size: through Ollama's own chat API, the only one that takes a size.
    private static bool UsesOllamaApi(CleanupOptions options) =>
        LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) == LocalServerApp.Ollama &&
        AskedContextTokens(options) is not null;

    // Must be called under _gate. The context a request for this configuration is fitted into, when Scribe knows it.
    private int? EffectiveContextLocked(CleanupOptions options)
    {
        if (UsesOllamaApi(options))
        {
            var asked = AskedContextTokens(options)!.Value;
            return _ollamaModelMaxTokens > 0 && _ollamaModelMaxFor is { } maxFor && maxFor.MatchesIgnoringPrompt(options)
                ? Math.Min(asked, _ollamaModelMaxTokens)
                : asked;
        }

        return _localContextTokens > 0 && _localContextFor is { } learnedFor && learnedFor.MatchesIgnoringPrompt(options)
            ? _localContextTokens
            : null;
    }

    // Must be called under _gate. The context a request for this configuration fits into: what is known, or the assumed size.
    private int ContextTokensLocked(CleanupOptions options) =>
        EffectiveContextLocked(options) ?? ContextBudget.AssumedContextTokens;

    /*
     * What the app said it holds the model with: a reading of Ollama's /api/ps, or of LM Studio's instances. Ollama with a
     * size takes none, since Scribe's own requests load the model at the size asked. LM Studio's requests reach the copy
     * read, so any reading is what they find. With Ollama's own setting, a reading after one of Scribe's own requests reached
     * the model is what the next one finds, and one before may be another app's copy at another size, which Scribe's request
     * replaces at Ollama's own, so it can only lower what is known, or the assumed size, never raise it. Kept while cleanup
     * still serves this configuration.
     */
    private void NoteObservedContext(CleanupOptions options, int tokens, bool afterOwnRequest)
    {
        if (tokens <= 0 || UsesOllamaApi(options))
        {
            return;
        }

        lock (_gate)
        {
            if (!_options.MatchesIgnoringPrompt(options))
            {
                return;
            }

            var settled = afterOwnRequest ||
                LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) == LocalServerApp.LmStudio;
            _localContextTokens = settled ? tokens : Math.Min(tokens, ContextTokensLocked(options));
            _localContextFor = options;
        }
    }

    /*
     * What Ollama or LM Studio holds the model with is no longer known: a read failed, or found no copy of the model, or one
     * whose size the app does not say. The copy the next request reaches is whatever loads it then, so a size learned for
     * an earlier copy no longer vouches for it, and the next requests are fitted to what is known again
     * (ContextBudget.AssumedContextTokens until a request of Scribe's own has reached the model and been read). Ollama with
     * a size keeps it: its context never came from a reading.
     */
    private void ForgetObservedContext(CleanupOptions options)
    {
        lock (_gate)
        {
            if (options.Provider != CleanupProvider.FoundryLocal &&
                _localContextFor is { } learnedFor && learnedFor.MatchesIgnoringPrompt(options))
            {
                _localContextTokens = 0;
                _localContextFor = null;
            }
        }
    }

    // Asks Ollama or LM Studio what it holds the model with, after one of Scribe's own requests reached it: a bounded while,
    // so an app that does not answer holds up nothing (the fitting keeps what it knows). Never throws but for cancellation.
    private async Task LearnLocalContextAsync(CleanupOptions options, CancellationToken ct)
    {
        if (LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) == LocalServerApp.None ||
            options.CustomEndpoint is not { } endpoint || options.CustomModel is not { } model)
        {
            return;
        }

        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(LocalContextReadBound);
            var state = await LocalServers.ReadAsync(endpoint, options.CustomApiKey, bounded.Token).ConfigureAwait(false);
            var tokens = state.LoadedFor(model)?.ContextTokens ?? 0;
            if (UsesOllamaApi(options))
            {
                NoteOllamaCap(options, tokens);
            }
            else
            {
                NoteObservedContext(options, tokens, afterOwnRequest: true);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("AI cleanup could not read the context of the model on this PC ({Failure}).", DescribeFailureShape(ex));
        }
    }

    // Ollama with a size, after one of Scribe's own requests loaded the model: a context smaller than the one asked is the
    // most the model takes, which Ollama capped the request at, so later requests ask for, and are fitted into, that. What
    // /api/show says normally sets it first (LearnOllamaModelMaxAsync); this keeps a request from running past the context
    // when /api/show did not answer. Only ever lowers.
    private void NoteOllamaCap(CleanupOptions options, int tokens)
    {
        if (tokens <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_options.MatchesIgnoringPrompt(options) && tokens < ContextTokensLocked(options))
            {
                _ollamaModelMaxTokens = tokens;
                _ollamaModelMaxFor = options;
            }
        }
    }

    // Ollama with a size: the largest context the model was made for, which Ollama caps the size asked at, so each request
    // asks for, and is fitted into, what Ollama loads. A bounded while; without an answer the size asked stands. Never throws
    // but for cancellation.
    private async Task LearnOllamaModelMaxAsync(CleanupOptions options, CancellationToken ct)
    {
        if (!UsesOllamaApi(options) || options.CustomEndpoint is not { } endpoint || options.CustomModel is not { } model)
        {
            return;
        }

        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(LocalContextReadBound);
            var largest = await LocalServers.ReadMaxContextAsync(endpoint, model, options.CustomApiKey, bounded.Token)
                .ConfigureAwait(false);
            if (largest <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_options.MatchesIgnoringPrompt(options))
                {
                    _ollamaModelMaxTokens = largest;
                    _ollamaModelMaxFor = options;
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("AI cleanup could not read the largest context of the Ollama model ({Failure}).", DescribeFailureShape(ex));
        }
    }

    /// <summary>How long a read of what the app holds the model with may hold up the step that asks it.</summary>
    internal TimeSpan LocalContextReadBound { get; set; } = TimeSpan.FromSeconds(2);

    // Ollama with a size, holding the model at another (another app's request loaded it so): Scribe's next request loads it
    // again, which a readying request counts as starting the model.
    private bool OllamaWouldReload(CleanupOptions options, LocalServerLoadedModel held)
    {
        if (!UsesOllamaApi(options) || held.ContextTokens <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            return held.ContextTokens != ContextTokensLocked(options);
        }
    }

    // LM Studio: the size a copy of the model is loaded at for this request, the asked size capped at the largest LM Studio
    // says the model takes.
    private static int LmStudioTarget(LocalServerState state, string model, int asked)
    {
        var largest = state.Models.FirstOrDefault(m => LocalServerClient.SameModel(m.Id, model))?.MaxContextTokens ?? 0;
        return largest > 0 ? Math.Min(asked, largest) : asked;
    }

    // LM Studio: a copy it loaded on demand at another size, which ReconcileLmStudioAsync unloads and loads again at the
    // asked size. One loaded by hand (no idle time of its own), or one whose size LM Studio does not say, is kept.
    private static bool LmStudioCopyIsOtherSize(LocalServerLoadedModel held, int target) =>
        held.ContextTokens > 0 && held.ContextTokens != target && held.RemainingTtlSeconds is not null &&
        held.InstanceId is not null;

    // True when LM Studio refused to load the model at this size for these settings, so a copy at LM Studio's own size is used
    // as it is rather than replaced at every recording.
    private bool LmStudioRefusedSize(CleanupOptions options, int target)
    {
        lock (_gate)
        {
            return _lmStudioRefusedFor is { } refusedFor && _lmStudioRefusedTarget == target &&
                refusedFor.MatchesIgnoringPrompt(options);
        }
    }

    // Recorded only while cleanup still serves these settings, so a refusal for settings since replaced is not kept.
    private void NoteLmStudioRefusedSize(CleanupOptions options, int target)
    {
        lock (_gate)
        {
            if (!_operations.IsClosed && _options.MatchesIgnoringPrompt(options))
            {
                _lmStudioRefusedFor = options;
                _lmStudioRefusedTarget = target;
            }
        }
    }

    // Forgets a refusal that is not for these settings, or that a copy at its size has answered.
    private void ForgetLmStudioRefusal(CleanupOptions options, int? heldAtTarget = null)
    {
        lock (_gate)
        {
            if (_lmStudioRefusedFor is { } refusedFor &&
                (!refusedFor.MatchesIgnoringPrompt(options) || heldAtTarget == _lmStudioRefusedTarget))
            {
                _lmStudioRefusedFor = null;
                _lmStudioRefusedTarget = 0;
            }
        }
    }

    // The copy Scribe loaded at a size for an earlier configuration, which this one no longer asks for.
    private string? EarlierScribeLoadedInstance(CleanupOptions options)
    {
        lock (_gate)
        {
            return _scribeLoadedInstance is { } instance && _scribeLoadedFor is { } loadedFor &&
                !loadedFor.MatchesIgnoringPrompt(options)
                    ? instance
                    : null;
        }
    }

    // True when ReconcileLmStudioAsync will load or unload a copy of the model for this state: what a readying request
    // counts as starting the model.
    private bool LmStudioWouldReload(CleanupOptions options, LocalServerState state)
    {
        if (LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) != LocalServerApp.LmStudio ||
            options.CustomModel is not { } model || state.Reach != LocalServerReach.Reached ||
            state.LoadedFor(model) is not { } held)
        {
            return false;
        }

        var target = AskedContextTokens(options) is { } asked ? LmStudioTarget(state, model, asked) : 0;

        // A copy no configuration owns, or one an earlier configuration owned, is taken as it is when it is at the size asked
        // for, and unloaded otherwise.
        if (held.InstanceId is { } id &&
            (IsUnownedCopy(options.CustomEndpoint!, id) ||
             string.Equals(id, EarlierScribeLoadedInstance(options), StringComparison.Ordinal)))
        {
            return !(target > 0 && held.ContextTokens == target);
        }

        return target > 0 && LmStudioCopyIsOtherSize(held, target) && !LmStudioRefusedSize(options, target);
    }

    /*
     * LM Studio with a size chosen: loads the model at that size through LM Studio's own chat API, which its OpenAI-compatible
     * one cannot ask for, before a request would load it at LM Studio's own size. A copy LM Studio loaded on demand at
     * another size (its own, or one Scribe asked for before) is unloaded first: LM Studio makes a second copy rather than
     * resizing one (measured on 0.4.25), and the requests by the model's name would still reach the first, so a copy LM
     * Studio will not unload is used as it is. A copy loaded by hand, which LM Studio keeps until it is unloaded, is used as
     * it is. Capped at the largest size LM Studio says the model takes.
     *
     * A copy an earlier configuration owned is no longer owned (DisownEarlierCopy), and the copies no configuration owns are
     * settled first (SettleUnownedCopiesAsync): one that is the copy these requests reach, at the size asked for, becomes
     * this configuration's, and the rest are unloaded, so without a size the model loads at LM Studio's own size with
     * Scribe's idle time rather than staying at an old size for LM Studio's hour. Scribe owns a copy it loaded only once LM
     * Studio has said which instance it is and cleanup still serves the configuration it was loaded for (TakeOwnership); it
     * stops owning one only once LM Studio has unloaded it or no longer lists it (ForgetUnloadedCopy), and one LM Studio will
     * not unload stays a copy no configuration owns, so it is still asked for.
     *
     * The copy requests reach is changed (unloaded, or loaded) only while this request is the only one using the model
     * (TryBeginLocalChange), behind a barrier every request that begins meanwhile waits for, so no request in flight loses
     * its model and none loads a copy of its own meanwhile; otherwise the copy is used as it is and a later recording
     * changes it. In the LM Studio lane (_lmStudioLane), and called within a use of the model (BeginModelUse), so a release
     * decided before it cannot unload what it loads.
     *
     * True when a model was loaded or unloaded, which a recording waits for. Never throws but for cancellation: a failed load
     * leaves the request to load the model as before.
     */
    private async Task<bool> ReconcileLmStudioAsync(CleanupOptions options, LocalServerState? state, CancellationToken ct)
    {
        if (LocalAiServer.AppServing(options.Provider, options.CustomEndpoint) != LocalServerApp.LmStudio ||
            options.CustomEndpoint is not { } endpoint || options.CustomModel is not { } model)
        {
            return false;
        }

        // A load another configuration started settles first, and what was read before waiting for it may predate it.
        if (!_lmStudioLane.Wait(0))
        {
            await _lmStudioLane.WaitAsync(ct).ConfigureAwait(false);
            state = null;
        }

        var laneHandedOff = false;
        TaskCompletionSource? change = null;
        bool MayChange()
        {
            change ??= TryBeginLocalChange();
            return change is not null;
        }

        try
        {
            DisownEarlierCopy(options);
            var size = AskedContextTokens(options);
            var unowned = UnownedCopiesAt(endpoint);
            if (size is null && unowned.Count == 0)
            {
                return false;
            }

            state ??= await LocalServers.ReadAsync(endpoint, options.CustomApiKey, ct).ConfigureAwait(false);
            if (state.Reach != LocalServerReach.Reached)
            {
                return false;
            }

            var changed = false;
            if (unowned.Count > 0 && await SettleUnownedCopiesAsync(options, state, unowned, MayChange, ct).ConfigureAwait(false))
            {
                changed = true;
                state = await LocalServers.ReadAsync(endpoint, options.CustomApiKey, ct).ConfigureAwait(false);
                if (state.Reach != LocalServerReach.Reached)
                {
                    return true;
                }
            }

            if (size is not { } asked)
            {
                return changed;
            }

            var target = LmStudioTarget(state, model, asked);
            var held = state.LoadedFor(model);
            ForgetLmStudioRefusal(options, held?.ContextTokens);
            if (held is not null && !LmStudioCopyIsOtherSize(held, target))
            {
                // A copy of Scribe's that this configuration's requests reach is this configuration's, so its release frees it.
                if (held.InstanceId is { } heldId && held.ContextTokens == target)
                {
                    ClaimScribesCopy(options, heldId);
                }

                NoteObservedContext(options, held.ContextTokens, afterOwnRequest: true);
                return changed;
            }

            if (LmStudioRefusedSize(options, target))
            {
                if (held is not null)
                {
                    NoteObservedContext(options, held.ContextTokens, afterOwnRequest: true);
                }

                return changed;
            }

            if (!MayChange())
            {
                _log.LogDebug("AI cleanup left the model in LM Studio as it is while another request uses it.");
                if (held is not null)
                {
                    NoteObservedContext(options, held.ContextTokens, afterOwnRequest: true);
                }

                return changed;
            }

            if (held is not null)
            {
                if (!await LocalServers.UnloadInstanceAsync(endpoint, held.InstanceId!, options.CustomApiKey, ct).ConfigureAwait(false))
                {
                    _log.LogInformation("LM Studio did not unload its copy of the model at another size; AI cleanup uses that copy.");
                    NoteObservedContext(options, held.ContextTokens, afterOwnRequest: true);
                    return changed;
                }

                ForgetUnloadedCopy(endpoint, held.InstanceId, model);
            }

            // Not stopped by this configuration's own token: a load Scribe stops waiting for still loads in LM Studio, and the
            // copy it makes is settled once LM Studio says which it is (SettleLoadInBackground, which keeps the lane, a use of
            // the model and the barrier until then), even when the load ended at the moment the wait was cancelled. Disposal
            // still stops it.
            var load = LocalServers.LoadWithContextAsync(endpoint, model, target, options.CustomApiKey, _lifetime.Token);
            string? loaded;
            try
            {
                loaded = await load.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                laneHandedOff = SettleLoadInBackground(options, endpoint, model, target, load, change);
                if (laneHandedOff)
                {
                    change = null;
                }

                throw;
            }

            if (loaded is null)
            {
                NoteLmStudioRefusedSize(options, target);
                _log.LogInformation("LM Studio did not load the model at the context size Scribe asked for; it loads at its own.");
                return held is not null || changed;
            }

            await SettleLoadedCopyAsync(options, endpoint, model, target, loaded).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("AI cleanup could not load the model in LM Studio at its size ({Failure}).", DescribeFailureShape(ex));
            return false;
        }
        finally
        {
            change?.TrySetResult();
            if (!laneHandedOff)
            {
                _lmStudioLane.Release();
            }
        }
    }

    /*
     * The model's copy may change only while the request that asks is the only one using the model: in one step with every
     * use that begins (under _releaseSync, as a release commits), and only when no unload is on its way. Published as the
     * unload in flight, so every request that begins meanwhile waits for it before it sends (BeginModelUse), rather than
     * having LM Studio load a copy of its own; a dictation waits for it as long as for a model that is starting
     * (WaitForLocalModelStartAsync). Null when another request is using the model: its copy then stays as it is. A request
     * that is only waiting for the model to start counts too (it began before the change and did not capture it), so in that
     * rare order the load at a new size waits for the next recording, which finds the copy at another size and replaces it.
     */
    private TaskCompletionSource? TryBeginLocalChange()
    {
        lock (_releaseSync)
        {
            if (_activeModelUses > 1 || !_unloadInFlight.IsCompleted)
            {
                return null;
            }

            var change = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _unloadInFlight, change.Task);
            Volatile.Write(ref _localChangeInFlight, change.Task);
            return change;
        }
    }

    // The change to a copy of the model on its way (TryBeginLocalChange), which a dictation waits for as for a model starting.
    private Task _localChangeInFlight = Task.CompletedTask;

    // A use of the model handed on by one that is still in flight (a load it stopped waiting for): no new use begins, so a
    // release decided before stays as wanted as it was, and the count never reaches zero in between.
    private ModelUse ExtendModelUse()
    {
        lock (_releaseSync)
        {
            _activeModelUses++;
        }

        return new ModelUse(this);
    }

    // The copy an earlier configuration owned, which this configuration does not ask for: no longer owned, so the settling of
    // copies no configuration owns adopts it when it is the copy these requests reach at the size asked for, and unloads it
    // otherwise, and the idle and pause releases still free it.
    private void DisownEarlierCopy(CleanupOptions options)
    {
        lock (_gate)
        {
            if (_scribeLoadedInstance is { } instance && _scribeLoadedFor is { CustomEndpoint: { } endpoint } loadedFor &&
                !loadedFor.MatchesIgnoringPrompt(options))
            {
                AddUnownedCopyLocked(new UnownedCopy(endpoint, loadedFor.CustomModel ?? string.Empty, instance, 0, loadedFor.CustomApiKey));
                _scribeLoadedInstance = null;
                _scribeLoadedFor = null;
            }
        }
    }

    /*
     * In the LM Studio lane: the copies at this address that Scribe loaded and no configuration owns. One LM Studio no longer
     * lists is forgotten. The one this configuration's requests reach (the first copy of its model), when it is at the size
     * this configuration asks for, becomes this configuration's instead of being loaded again (a Test connection of these
     * settings just loaded it, or an earlier configuration did). The rest are unloaded only when mayChange allows it: a
     * request of earlier settings may still be using a copy these settings do not reach. Each is asked for with the key
     * that has just read this server (the settings' own, which may have replaced the key the copy was loaded with), and one
     * LM Studio refuses stays recorded with that key. True when one was unloaded.
     */
    private async Task<bool> SettleUnownedCopiesAsync(
        CleanupOptions options, LocalServerState state, IReadOnlyList<UnownedCopy> copies, Func<bool> mayChange, CancellationToken ct)
    {
        var model = options.CustomModel!;
        var held = state.LoadedFor(model);
        var target = AskedContextTokens(options) is { } asked ? LmStudioTarget(state, model, asked) : 0;
        var unloaded = false;
        foreach (var copy in copies)
        {
            var listed = state.Loaded.FirstOrDefault(loaded => string.Equals(loaded.InstanceId, copy.Instance, StringComparison.Ordinal));
            if (listed is null)
            {
                DropUnownedCopy(copy);
                continue;
            }

            var reached = string.Equals(held?.InstanceId, copy.Instance, StringComparison.Ordinal);
            if (target > 0 && listed.ContextTokens == target && LocalServerClient.SameModel(copy.Model, model) && reached &&
                TakeOwnership(options, copy.Instance, target))
            {
                _log.LogInformation("AI cleanup uses a copy of the model it loaded in LM Studio before, at {Tokens} tokens.", target);
                continue;
            }

            if (!mayChange())
            {
                continue;
            }

            if (await LocalServers.UnloadInstanceAsync(copy.Endpoint, copy.Instance, options.CustomApiKey, ct).ConfigureAwait(false))
            {
                ForgetUnloadedCopy(copy.Endpoint, copy.Instance, copy.Model);
                unloaded = true;
            }
            else
            {
                ReKeyUnownedCopy(copy, options.CustomApiKey);
            }
        }

        return unloaded;
    }

    // LM Studio loaded a copy at the size asked: this configuration's while cleanup still serves it (TakeOwnership); otherwise
    // nobody's, and freed now rather than left for LM Studio's hour, or, when LM Studio refuses, recorded as a copy no
    // configuration owns, so it is asked for again. Never throws.
    private async Task SettleLoadedCopyAsync(CleanupOptions options, string endpoint, string model, int target, string instance)
    {
        if (TakeOwnership(options, instance, target))
        {
            _log.LogInformation("AI cleanup loaded the model in LM Studio with a context of {Tokens} tokens.", target);
            return;
        }

        var copy = new UnownedCopy(endpoint, model, instance, target, options.CustomApiKey);
        try
        {
            using var bound = new CancellationTokenSource(ShutdownReleaseBound);
            var freed = await LocalServers.UnloadInstanceAsync(endpoint, instance, options.CustomApiKey, bound.Token)
                .ConfigureAwait(false);
            if (freed)
            {
                ForgetUnloadedCopy(endpoint, instance, model);
            }
            else
            {
                AddUnownedCopy(copy);
            }

            _log.LogInformation("AI cleanup freed a copy of the model LM Studio loaded for settings no longer in use: {Freed}.", freed);
        }
        catch (Exception ex)
        {
            AddUnownedCopy(copy);
            _log.LogDebug("AI cleanup could not free a copy of the model LM Studio loaded ({Failure}).", DescribeFailureShape(ex));
        }
    }

    /*
     * A load at a size Scribe stopped waiting for (its configuration was replaced, or its time ran out): settled once LM
     * Studio says which instance it loaded, tracked so disposal waits for it. It keeps what its caller held until the copy is
     * settled: the LM Studio lane, so no other configuration reads LM Studio, loads a copy beside it or probes it meanwhile; a
     * use of the model (ExtendModelUse, taken while the caller's is still in flight), so no release commits until the copy
     * it loads is Scribe's to free; and the change barrier, when the caller had one. False, leaving all of it to the caller,
     * when Scribe is closing and nothing can be tracked. Never throws.
     */
    private bool SettleLoadInBackground(
        CleanupOptions options, string endpoint, string model, int target, Task<string?> load, TaskCompletionSource? change)
    {
        if (_operations.TryEnter() is not { } lease)
        {
            return false;
        }

        var use = ExtendModelUse();
        _ = Task.Run(async () =>
        {
            try
            {
                if (await load.ConfigureAwait(false) is { } instance)
                {
                    await SettleLoadedCopyAsync(options, endpoint, model, target, instance).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("AI cleanup could not settle a model LM Studio loaded ({Failure}).", DescribeFailureShape(ex));
            }
            finally
            {
                change?.TrySetResult();
                use.Dispose();
                _lmStudioLane.Release();
                lease.Dispose();
            }
        });
        return true;
    }

    /*
     * The copy this configuration's requests reach, which Scribe loaded, becomes this configuration's while cleanup still
     * serves it: Scribe frees it at the idle time and as it closes, and fits requests to its size. A different copy Scribe
     * owned before becomes one no configuration owns, so it is still freed. False when cleanup no longer serves this
     * configuration (it changed, or Scribe is closing).
     */
    private bool TakeOwnership(CleanupOptions options, string instance, int contextTokens)
    {
        lock (_gate)
        {
            if (_operations.IsClosed || !_options.MatchesIgnoringPrompt(options))
            {
                return false;
            }

            if (_scribeLoadedInstance is { } previous && !string.Equals(previous, instance, StringComparison.Ordinal) &&
                _scribeLoadedFor is { CustomEndpoint: { } previousEndpoint } previousFor)
            {
                AddUnownedCopyLocked(new UnownedCopy(
                    previousEndpoint, previousFor.CustomModel ?? string.Empty, previous, 0, previousFor.CustomApiKey));
            }

            var endpoint = _options.CustomEndpoint ?? string.Empty;
            _unownedCopies.RemoveAll(copy =>
                string.Equals(copy.Instance, instance, StringComparison.Ordinal) && SameServer(copy.Endpoint, endpoint));

            // The configuration served, rather than the one passed (a Test connection candidate can differ from it in what
            // the prompt says, the idle time among it).
            _scribeLoadedInstance = instance;
            _scribeLoadedFor = _options;
            _localContextTokens = contextTokens;
            _localContextFor = _options;
            return true;
        }
    }

    // A copy of Scribe's, owned for an earlier configuration, that this configuration's requests reach: this configuration's.
    private void ClaimScribesCopy(CleanupOptions options, string instance)
    {
        lock (_gate)
        {
            if (string.Equals(_scribeLoadedInstance, instance, StringComparison.Ordinal) && !_operations.IsClosed &&
                _options.MatchesIgnoringPrompt(options))
            {
                _scribeLoadedFor = _options;
            }
        }
    }

    // Two addresses of one server: the same port, and the same host or both this PC (localhost, 127.0.0.1 and [::1] name
    // the same machine, and an app's address can be saved with any of them).
    private static bool SameServer(string first, string second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a) && Uri.TryCreate(second, UriKind.Absolute, out var b) &&
        a.Port == b.Port &&
        (string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) || (a.IsLoopback && b.IsLoopback));

    // The copies no configuration owns, at one server or at every one.
    private List<UnownedCopy> UnownedCopiesAt(string? endpoint)
    {
        lock (_gate)
        {
            return [.. _unownedCopies.Where(copy => endpoint is null || SameServer(copy.Endpoint, endpoint))];
        }
    }

    private bool IsUnownedCopy(string endpoint, string instance)
    {
        lock (_gate)
        {
            return _unownedCopies.Any(copy =>
                string.Equals(copy.Instance, instance, StringComparison.Ordinal) && SameServer(copy.Endpoint, endpoint));
        }
    }

    private void AddUnownedCopy(UnownedCopy copy)
    {
        lock (_gate)
        {
            AddUnownedCopyLocked(copy);
        }
    }

    // Must be called under _gate. Every one is kept until LM Studio has unloaded it or no longer lists it.
    private void AddUnownedCopyLocked(UnownedCopy copy)
    {
        _unownedCopies.RemoveAll(existing =>
            string.Equals(existing.Instance, copy.Instance, StringComparison.Ordinal) && SameServer(existing.Endpoint, copy.Endpoint));
        _unownedCopies.Add(copy);
    }

    private void DropUnownedCopy(UnownedCopy copy)
    {
        lock (_gate)
        {
            _unownedCopies.Remove(copy);
        }
    }

    private bool StillUnowned(UnownedCopy copy)
    {
        lock (_gate)
        {
            return _unownedCopies.Contains(copy);
        }
    }

    // A copy no configuration owns, recorded with the key that opens its server now, so later attempts ask with that one.
    private void ReKeyUnownedCopy(UnownedCopy copy, string? key)
    {
        lock (_gate)
        {
            var index = _unownedCopies.IndexOf(copy);
            if (index >= 0 && !string.Equals(copy.ApiKey, key, StringComparison.Ordinal))
            {
                _unownedCopies[index] = copy with { ApiKey = key };
            }
        }
    }

    /*
     * The keys to ask a server with for a copy Scribe loaded there, in order: the key the settings in use have for that
     * server (the one the user has now: a key LM Studio no longer takes may have been replaced since the copy was loaded),
     * then the one the copy was loaded with. Both were given for this server.
     */
    private IReadOnlyList<string?> KeysFor(string endpoint, string? loadedWith)
    {
        lock (_gate)
        {
            return _options.Provider == CleanupProvider.OpenAiCompatible && _options.CustomEndpoint is { } current &&
                SameServer(current, endpoint) && !string.Equals(_options.CustomApiKey, loadedWith, StringComparison.Ordinal)
                    ? [_options.CustomApiKey, loadedWith]
                    : [loadedWith];
        }
    }

    // Unloads one instance, asking with each key in turn until LM Studio takes it. Never throws but for cancellation.
    private async Task<bool> UnloadInstanceWithAnyAsync(
        string endpoint, string instance, IReadOnlyList<string?> keys, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            if (await LocalServers.UnloadInstanceAsync(endpoint, instance, key, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    // Frees a model, asking with each key in turn until the app takes it. Never throws but for cancellation.
    private async Task<bool> UnloadModelWithAnyAsync(string endpoint, string model, IReadOnlyList<string?> keys, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            if (await LocalServers.UnloadAsync(endpoint, model, key, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /*
     * At the idle and pause releases: the copies no configuration owns are unloaded, through the release lane like every
     * release, once the uses in flight have finished, and with every use that begins waiting for it (TryCommitRelease), so
     * none is unloaded under a request that reaches it by the model's name. Tracked, so disposal waits for it. Never throws.
     */
    private void StartUnownedRetirement()
    {
        try
        {
            if (UnownedCopiesAt(endpoint: null).Count == 0 || _operations.TryEnter() is not { } lease)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                var inLane = false;
                TaskCompletionSource? committed = null;
                try
                {
                    await _releaseLane.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    inLane = true;
                    await WaitForModelUsesToFinishAsync(static () => true, AutomaticReleaseDrainBound, _lifetime.Token).ConfigureAwait(false);
                    (committed, var held) = TryCommitRelease(static () => true);
                    if (committed is null)
                    {
                        _log.LogInformation("AI cleanup did not ask LM Studio to free copies no settings use: {Held}.", held);
                        return;
                    }

                    var freed = await RetireUnownedCopiesAsync(_lifetime.Token).ConfigureAwait(false);
                    _log.LogInformation("AI cleanup asked LM Studio to free copies of a model no settings use; it freed {Count}.", freed);
                }
                catch (Exception ex)
                {
                    _log.LogDebug("AI cleanup could not free copies of a model no settings use ({Failure}).", DescribeFailureShape(ex));
                }
                finally
                {
                    committed?.TrySetResult();
                    if (inLane)
                    {
                        _releaseLane.Release();
                    }

                    lease.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug("AI cleanup could not free copies of a model no settings use ({Failure}).", DescribeFailureShape(ex));
        }
    }

    /*
     * Forgets the copies no configuration owns that LM Studio no longer lists, and unloads the rest. Each server is read with
     * the keys KeysFor gives, until one opens it, and each copy is unloaded with that key first; a copy LM Studio refuses
     * stays recorded with the key that opened the server. The number unloaded. Never throws but for cancellation.
     */
    private async Task<int> RetireUnownedCopiesAsync(CancellationToken ct)
    {
        var freed = 0;
        var reads = new Dictionary<(string Endpoint, string? Key), LocalServerState>();
        foreach (var copy in UnownedCopiesAt(endpoint: null))
        {
            if (!StillUnowned(copy))
            {
                continue;
            }

            var keys = KeysFor(copy.Endpoint, copy.ApiKey);
            LocalServerState? state = null;
            string? opened = null;
            foreach (var key in keys)
            {
                if (!reads.TryGetValue((copy.Endpoint, key), out state))
                {
                    state = await LocalServers.ReadAsync(copy.Endpoint, key, ct).ConfigureAwait(false);
                    reads[(copy.Endpoint, key)] = state;
                }

                if (state.Reach == LocalServerReach.Reached)
                {
                    opened = key;
                    break;
                }

                if (state.Reach != LocalServerReach.NeedsKey)
                {
                    break;
                }
            }

            if (state is { Reach: LocalServerReach.Reached } &&
                !state.Loaded.Any(loaded => string.Equals(loaded.InstanceId, copy.Instance, StringComparison.Ordinal)))
            {
                DropUnownedCopy(copy);
                continue;
            }

            IReadOnlyList<string?> unloadKeys = state is { Reach: LocalServerReach.Reached } ? [opened] : keys;
            if (await UnloadInstanceWithAnyAsync(copy.Endpoint, copy.Instance, unloadKeys, ct).ConfigureAwait(false))
            {
                ForgetUnloadedCopy(copy.Endpoint, copy.Instance, copy.Model);
                freed++;
            }
            else if (state is { Reach: LocalServerReach.Reached })
            {
                ReKeyUnownedCopy(copy, opened);
            }
        }

        return freed;
    }

    // The LM Studio instance Scribe loaded for this configuration, which Scribe frees itself; null otherwise.
    private string? ScribeLoadedInstanceFor(CleanupOptions options)
    {
        lock (_gate)
        {
            return _scribeLoadedInstance is { } instance && _scribeLoadedFor is { } loadedFor &&
                loadedFor.MatchesIgnoringPrompt(options)
                    ? instance
                    : null;
        }
    }

    /// <summary>How long shutdown waits for LM Studio to free the copy Scribe loaded at a size.</summary>
    internal static TimeSpan ShutdownReleaseBound { get; } = TimeSpan.FromSeconds(2);

    /*
     * At shutdown, after every operation has drained: the LM Studio copy Scribe loaded at a size, whatever cleanup is set
     * to now, because LM Studio would keep it for its own hour (it takes no idle time for a copy loaded this way) after the
     * user is done with Scribe. Freed sooner than the idle time asked, never later, which 0.5.2's rules accept. With
     * "Never" (no idle time), the copy is left to LM Studio's own policy, as every other model is. The copies no
     * configuration owns are freed whatever the idle time: no settings use them, as for a model AI cleanup stops using.
     * Bounded together (ShutdownReleaseBound); LM Studio finishes an unload it has received even if Scribe stops waiting
     * (measured on 0.4.25). A load still on its way as Scribe closes is stopped, and a copy LM Studio makes of it anyway is
     * left to its own idle time. Never throws.
     */
    private async Task ReleaseScribeLoadedCopyAsync()
    {
        string? instance;
        CleanupOptions? loadedFor;
        int? keepAlive;
        lock (_gate)
        {
            instance = _scribeLoadedInstance;
            loadedFor = _scribeLoadedFor;

            // The idle time as it stands now: it is asked of each request, so a change to it keeps the copy's owner.
            keepAlive = loadedFor is not null && _options.MatchesIgnoringPrompt(loadedFor)
                ? _options.LocalModelKeepAliveMinutes
                : loadedFor?.LocalModelKeepAliveMinutes;
        }

        using var bound = new CancellationTokenSource(ShutdownReleaseBound);
        if (instance is not null && loadedFor?.CustomEndpoint is { } endpoint && keepAlive is > 0)
        {
            try
            {
                var freed = await UnloadInstanceWithAnyAsync(endpoint, instance, KeysFor(endpoint, loadedFor.CustomApiKey), bound.Token)
                    .ConfigureAwait(false);
                _log.LogInformation("AI cleanup freed the copy of the model it loaded in LM Studio as Scribe closed: {Freed}.", freed);
            }
            catch (Exception ex)
            {
                _log.LogDebug("AI cleanup could not free the model it loaded in LM Studio as Scribe closed ({Failure}).", DescribeFailureShape(ex));
            }
        }

        if (UnownedCopiesAt(endpoint: null).Count > 0)
        {
            try
            {
                var freed = await RetireUnownedCopiesAsync(bound.Token).ConfigureAwait(false);
                _log.LogInformation("AI cleanup freed copies of a model no settings use as Scribe closed: {Count}.", freed);
            }
            catch (Exception ex)
            {
                _log.LogDebug("AI cleanup could not free copies of a model no settings use as Scribe closed ({Failure}).", DescribeFailureShape(ex));
            }
        }
    }

    // A copy of the model was unloaded at this server, by its instance id, or every copy of it was (instance null): Scribe's
    // ownership of it goes, so does its record as a copy no configuration owns, and so does what was learned about the copy
    // LM Studio's requests reached. Only at that server: an Ollama model named as an LM Studio one is a different model, and
    // an instance Scribe loaded since for another model stays.
    private void ForgetUnloadedCopy(string endpoint, string? instance, string? model)
    {
        lock (_gate)
        {
            bool Matches(string copyEndpoint, string? copyInstance, string? copyModel) =>
                SameServer(copyEndpoint, endpoint) &&
                (instance is not null
                    ? string.Equals(copyInstance, instance, StringComparison.Ordinal)
                    : LocalServerClient.SameModel(copyModel, model));

            var scribes = _scribeLoadedInstance is { } owned && _scribeLoadedFor is { CustomEndpoint: { } ownedAt } loadedFor &&
                Matches(ownedAt, owned, loadedFor.CustomModel);
            if (scribes)
            {
                _scribeLoadedInstance = null;
                _scribeLoadedFor = null;
            }

            _unownedCopies.RemoveAll(copy => Matches(copy.Endpoint, copy.Instance, copy.Model));

            if (_localContextFor is { CustomEndpoint: { } learnedAt } learnedFor &&
                LocalAiServer.AppServing(learnedFor.Provider, learnedAt) == LocalServerApp.LmStudio &&
                SameServer(learnedAt, endpoint) &&
                (scribes || LocalServerClient.SameModel(learnedFor.CustomModel, model)))
            {
                _localContextTokens = 0;
                _localContextFor = null;
            }
        }
    }

    // A dictation's requests to a model whose context Scribe fits: the chunks it is cleaned in, and the tokens of vocabulary
    // each request may carry (every request carries the same vocabulary, so the chunk that costs the most decides).
    private readonly record struct LocalRequestPlan(List<string> Chunks, int VocabularyTokens, LocalRequestBudget Budget);

    // Kept with the plan: a later configuration or probe must not change the ceiling, the context or the plain-request mode a
    // fitted request sends, on any of its chunks or retries. A reasoning reserve also makes its answers count only when the
    // server says they finished (RunChunkAttemptAsync).
    private readonly record struct LocalRequestBudget(int ReasoningReserve, bool PlainRequests, int ContextTokens);

    // What a readiness check learned (ProbeOllamaAsync), published only by the initialization that ran it, for its options:
    // the room the model needs to think (LocalReasoningHeadroomTokens, or 0), its plain-request mode and the context it read.
    private sealed record LocalReasoningAllowance(CleanupOptions Options, int Tokens, bool PlainRequests, int ContextTokens);

    private LocalReasoningAllowance? _localReasoningAllowance;

    private int LocalReasoningReserve(CleanupOptions options)
    {
        var allowance = Volatile.Read(ref _localReasoningAllowance);
        return allowance is not null && allowance.Options.MatchesIgnoringPrompt(options) &&
            allowance.PlainRequests == SendsPlainRequests(options)
                ? allowance.Tokens
                : 0;
    }

    /*
     * Must be called under _gate. Plans a dictation's requests to a model on this PC so each fits the context with the
     * longest answer it allows (the output ceiling it declares), under the instructions this call runs with, its writing
     * style included. The detailed instructions try the dictation whole first, so a capable model makes its sentence and
     * paragraph decisions across all of it; the short instructions start from the chunks they always used. A plan that
     * leaves the vocabulary no room halves the chunks, down to MinLocalChunkChars. Null when not even those fit with the
     * instructions: the dictation is then typed as heard, rather than sent to a model that would cut the instructions off.
     */
    private LocalRequestPlan? PlanLocalRequestLocked(CleanupOptions options, string? style, string text)
    {
        var context = ContextTokensLocked(options);
        var budget = new LocalRequestBudget(LocalReasoningReserve(options), SendsPlainRequests(options), context);
        var instructions = PromptParts.Of(options, style ?? options.WritingStyle, glossary: null).Build();
        var whole = text.Trim();
        var frontier = CleanupPrompt.ResolvePromptStyle(options.PromptStyle, options.Provider, options.CustomEndpoint) ==
            CleanupPromptStyle.Frontier;
        var target = frontier ? Math.Max(whole.Length, 1) : ChunkTargetChars;
        while (true)
        {
            var chunks = ChunkForCleanup(whole, target);
            long worst = 0;
            foreach (var chunk in chunks)
            {
                worst = Math.Max(worst, ContextBudget.RequestTextCost(chunk, OutputCeiling(chunk, options, budget.ReasoningReserve)));
            }

            var room = ContextBudget.VocabularyTokensFor(context, instructions, worst);
            if (room >= 0)
            {
                return new LocalRequestPlan(chunks, room, budget);
            }

            // A target past the text's length splits nothing, so halving starts from the shorter of the two.
            var next = Math.Min(target, whole.Length) / 2;
            if (next < MinLocalChunkChars)
            {
                return null;
            }

            target = next;
        }
    }

    // The most a request carrying this text lets the model answer with: the ceiling it declares.
    private int OutputCeiling(string text, CleanupOptions options, int? reasoningReserve = null) =>
        MaxOutputTokensOverride ?? (EstimateMaxTokens(text, options.Provider) + (reasoningReserve ?? LocalReasoningReserve(options)));

    /*
     * Before a one-off request (CompleteCoreAsync) to Ollama or LM Studio at its own address: LM Studio with a size loads the
     * model at it first (ReconcileLmStudioAsync, which reads LM Studio itself once any load in flight has settled), rather
     * than letting the request load it at LM Studio's own; then what the app holds the model with is read, as a recording
     * reads it, so the request is fitted to the copy it reaches (a size learned for a copy evicted or replaced since no
     * longer vouches for it). Within the request's use of the model, and bounded by a one-off request's time limit
     * (LocalServerPrepareBound). Ollama with a size needs none of it: its requests ask for their size. False when the time
     * ran out while LM Studio work it waited behind still holds the lane, or a copy of the model is still being changed:
     * the request is not sent into that work. Never throws.
     */
    private async Task<bool> PrepareLocalServerAsync(CleanupRecipient recipient, CancellationToken ct)
    {
        CleanupOptions options;
        lock (_gate)
        {
            if (_status != CleanupStatus.Ready || !recipient.Matches(_options) ||
                LocalAiServer.AppServing(_options.Provider, _options.CustomEndpoint) == LocalServerApp.None ||
                UsesOllamaApi(_options))
            {
                return true;
            }

            options = _options;
        }

        if (options.CustomEndpoint is not { } endpoint || options.CustomModel is not { } model)
        {
            return true;
        }

        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            bounded.CancelAfter(LocalServerPrepareBound);
            await ReconcileLmStudioAsync(options, state: null, bounded.Token).ConfigureAwait(false);
            var state = await LocalServers.ReadAsync(endpoint, options.CustomApiKey, bounded.Token).ConfigureAwait(false);
            if (state.LoadedFor(model) is { ContextTokens: > 0 } held)
            {
                NoteObservedContext(options, held.ContextTokens, afterOwnRequest: false);
            }
            else
            {
                ForgetObservedContext(options);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // Out of time, cancelled or closing: the request is fitted to what is known, and its own send ends the same way
            // when it was cancelled. It is not sent while LM Studio work it waited behind still holds the lane, or a copy of
            // the model is still being changed (a load handed to SettleLoadInBackground holds both).
            return _lmStudioLane.CurrentCount > 0 && Volatile.Read(ref _localChangeInFlight).IsCompleted;
        }
    }

    /// <summary>How long a one-off request waits for its preparation (<see cref="PrepareLocalServerAsync"/>); tests shorten it.</summary>
    internal TimeSpan LocalServerPrepareBound { get; set; } = TimeSpan.FromSeconds(AuxiliaryCompletionTimeoutSeconds);

    // A chunk as short as a dictation is ever split into (MinLocalChunkChars), of ordinary words: what the instructions have
    // to leave room for, with its answer, or no dictation can be cleaned.
    private static readonly string ShortestDictation = string.Join(' ', Enumerable.Repeat("word", MinLocalChunkChars / 5));

    /*
     * True when the instructions, with this writing style, leave room in a context of contextTokens for the shortest chunk a
     * dictation is split into and the longest answer its request allows (PlanLocalRequestLocked's floor). Otherwise every
     * dictation would be typed as heard, so setting up AI cleanup and Test connection say so instead of reporting a model
     * that is ready, and a recording sends no readying request.
     */
    private bool LeavesRoomForDictation(CleanupOptions options, string? style, int contextTokens, int? reasoningReserve = null)
    {
        var instructions = PromptParts.Of(options, style ?? options.WritingStyle, glossary: null).Build();
        var shortest = ContextBudget.RequestTextCost(ShortestDictation, OutputCeiling(ShortestDictation, options, reasoningReserve));
        return ContextBudget.VocabularyTokensFor(contextTokens, instructions, shortest) >= 0;
    }

    // Must be called under _gate. LeavesRoomForDictation in the context this configuration's requests are fitted into.
    private bool LeavesRoomForDictationLocked(CleanupOptions options, string? style) =>
        LeavesRoomForDictation(options, style, ContextTokensLocked(options));

    // False only when the context is already known and the instructions leave no room in it: checked before a readiness
    // check that would load the model for nothing. Foundry Local's is known once its model is loaded, before the check.
    private bool KnownContextLeavesRoom(CleanupOptions options)
    {
        lock (_gate)
        {
            var known = EffectiveContextLocked(options) ??
                (options.Provider == CleanupProvider.FoundryLocal && _pendingFoundryContext > 0 ? _pendingFoundryContext : null);
            return known is not { } context || LeavesRoomForDictation(options, null, context);
        }
    }

    // Must be called under _gate. The instructions, with the configured writing style, leave a dictation room in what the
    // model reads at once; always true where Scribe does not fit requests to a context.
    private bool InstructionsFitLocked(CleanupOptions options) =>
        !FitsLocalContext(options) || LeavesRoomForDictationLocked(options, style: null);

    // What makes room, for the app that runs the model: Foundry Local's context comes with its model.
    private static string ContextAdvice(CleanupOptions options) =>
        options.Provider == CleanupProvider.FoundryLocal
            ? "Another model or shorter instructions make room."
            : "A larger context size or shorter instructions make room.";

    // AI cleanup cannot run on this model: its instructions leave no room for a dictation in what it reads at once.
    private static CleanupReason ContextTooSmallReason(CleanupOptions options) => CleanupReason.Same(
        "The instructions and writing style don't fit in what the AI model on this PC reads at once. " + ContextAdvice(options));

    // The model thinks before it answers (LocalReasoningHeadroomTokens), and that room is what does not fit.
    private static CleanupReason ThinkingRoomTooSmallReason(CleanupOptions options) => CleanupReason.Same(
        "This AI model thinks before it answers, and the room it needs to think, the instructions and a dictation don't " +
        "fit in what it reads at once. " + ContextAdvice(options));

    // Why a dictation has no room in contextTokens: the room to think, when the instructions and a dictation fit without it.
    private CleanupReason NoRoomReason(CleanupOptions options, int contextTokens, int reasoningReserve) =>
        reasoningReserve > 0 && LeavesRoomForDictation(options, style: null, contextTokens, reasoningReserve: 0)
            ? ThinkingRoomTooSmallReason(options)
            : ContextTooSmallReason(options);

    // Test connection: LM Studio at its own address with a size chosen, which TestLmStudioAtSizeAsync tests at that size.
    private static bool TestsLmStudioAtSize(CleanupOptions candidate) =>
        LocalAiServer.AppServing(candidate.Provider, candidate.CustomEndpoint) == LocalServerApp.LmStudio &&
        AskedContextTokens(candidate) is not null &&
        candidate.CustomEndpoint is not null && candidate.CustomModel is not null;

    /*
     * Test connection for LM Studio at its own address with a size chosen: what is tested is the size Save would load the
     * model at. With no copy of the model in LM Studio, it is loaded at that size, in the LM Studio lane, and the readiness
     * check reaches it; a load LM Studio refuses fails the test, as that size would fail after Save. The load changes what
     * requests to the model reach, so, as a recording's does, it happens only while the test is the only use of the model,
     * behind the change barrier every request that begins meanwhile waits for (TryBeginLocalChange), handed to the settling
     * of a load the test stopped waiting for; a test while AI cleanup is using the model loads nothing and says to test
     * again. Instructions that leave a dictation no room in the context the test would read fail it before anything is
     * loaded or sent. A copy LM Studio already holds is tested as it is, at the smaller of its size and the one chosen (or of
     * what is assumed, when LM Studio does not say its size): Scribe never unloads a copy for a test. A copy the test loaded
     * becomes the copy of the settings cleanup serves when those are the settings tested (TakeOwnership); otherwise it is a
     * copy no configuration owns, freed once the test is done (StartUnownedRetirement). The lane goes before the readiness
     * check; nothing unloads the copy under it, because the test is a use of the model. The caller holds that use. Returns
     * the readiness check's failure, or null, and the context the test found.
     */
    private async Task<(AgentProbeFailure? Failure, int ContextTokens)> TestLmStudioAtSizeAsync(
        CleanupOptions candidate, Func<string, AIAgent> factory, CancellationToken ct)
    {
        var endpoint = candidate.CustomEndpoint!;
        var model = candidate.CustomModel!;
        var asked = AskedContextTokens(candidate)!.Value;
        await _lmStudioLane.WaitAsync(ct).ConfigureAwait(false);
        var laneHandedOff = false;
        var laneReleased = false;
        UnownedCopy? loadedForTest = null;
        TaskCompletionSource? change = null;
        try
        {
            var state = await LocalServers.ReadAsync(endpoint, candidate.CustomApiKey, ct).ConfigureAwait(false);
            if (state.Reach != LocalServerReach.Reached)
            {
                // Without LM Studio's list, the size could not be loaded, and a readiness check would load the model at LM
                // Studio's own: what was tested would not be the size chosen.
                return (new AgentProbeFailure(CleanupReason.Same(state.Reach switch
                {
                    LocalServerReach.NotRunning => "Scribe couldn't reach LM Studio. Start LM Studio and test again.",
                    LocalServerReach.NeedsKey => "LM Studio asked for an API key. Enter the key you set in LM Studio.",
                    _ => "Scribe couldn't read LM Studio's models, so it couldn't load the model with the context size you chose.",
                }), null), 0);
            }

            var target = LmStudioTarget(state, model, asked);
            var held = state.LoadedFor(model);

            // A copy whose size LM Studio does not say is fitted to as requests are, to what is assumed, never to the size
            // asked for: nothing loaded it at that size.
            var context = held is null
                ? target
                : held.ContextTokens > 0 ? Math.Min(held.ContextTokens, target) : Math.Min(target, ContextBudget.AssumedContextTokens);
            if (!LeavesRoomForDictation(candidate, style: null, context))
            {
                return (new AgentProbeFailure(ContextTooSmallReason(candidate), null), context);
            }

            if (held is null)
            {
                change = TryBeginLocalChange();
                if (change is null)
                {
                    return (new AgentProbeFailure(
                        CleanupReason.Same("AI cleanup is using the model in LM Studio right now. Test again in a moment."),
                        null), 0);
                }

                var load = LocalServers.LoadWithContextAsync(endpoint, model, target, candidate.CustomApiKey, _lifetime.Token);
                string? instance;
                try
                {
                    instance = await load.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    laneHandedOff = SettleLoadInBackground(candidate, endpoint, model, target, load, change);
                    if (laneHandedOff)
                    {
                        change = null;
                    }

                    throw;
                }

                if (instance is null)
                {
                    return (new AgentProbeFailure(
                        CleanupReason.Same(
                            "LM Studio couldn't load the model with the context size you chose. Choose a smaller size, or " +
                            "LM Studio's setting."),
                        null), 0);
                }

                if (!TakeOwnership(candidate, instance, target))
                {
                    loadedForTest = new UnownedCopy(endpoint, model, instance, target, candidate.CustomApiKey);
                    AddUnownedCopy(loadedForTest);
                }

                // The copy is settled, Scribe's or recorded to be freed: a request that began meanwhile may go to it now.
                change.TrySetResult();
                change = null;
            }

            // The lane goes before the readiness check, so a recording that starts meanwhile reads LM Studio and fits its
            // dictation to the copy it would reach rather than waiting behind the check. Nothing unloads the copy under the
            // check: the test is a use of the model, and every unload waits for the uses in flight or needs the change
            // barrier, which a second use refuses.
            _lmStudioLane.Release();
            laneReleased = true;
            return (await ProbeAgentAsync(candidate, factory, ct, withinUse: true).ConfigureAwait(false), context);
        }
        finally
        {
            change?.TrySetResult();
            if (loadedForTest is not null)
            {
                StartUnownedRetirement();
            }

            if (!laneHandedOff && !laneReleased)
            {
                _lmStudioLane.Release();
            }
        }
    }

    /*
     * The context a Test connection candidate's requests would be fitted into, as the settings would serve it: for Ollama
     * with a size, that size capped at the largest the model takes; otherwise what Ollama or LM Studio holds the model with
     * after the readiness check reached it, or ContextBudget.AssumedContextTokens when it does not say. A bounded while.
     */
    private async Task<int> CandidateContextTokensAsync(CleanupOptions candidate, CancellationToken ct)
    {
        var asked = AskedContextTokens(candidate);
        if (candidate.CustomEndpoint is not { } endpoint || candidate.CustomModel is not { } model)
        {
            return asked ?? ContextBudget.AssumedContextTokens;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(LocalContextReadBound);
        try
        {
            if (UsesOllamaApi(candidate))
            {
                var largest = await LocalServers.ReadMaxContextAsync(endpoint, model, candidate.CustomApiKey, bounded.Token)
                    .ConfigureAwait(false);
                return largest > 0 ? Math.Min(asked!.Value, largest) : asked!.Value;
            }

            var state = await LocalServers.ReadAsync(endpoint, candidate.CustomApiKey, bounded.Token).ConfigureAwait(false);
            return state.LoadedFor(model)?.ContextTokens is > 0 and var tokens ? tokens : ContextBudget.AssumedContextTokens;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return asked ?? ContextBudget.AssumedContextTokens;
        }
    }

    // After the capped probe, what Ollama holds is its own request's copy. The reading belongs to this probe, never a
    // Test connection candidate changing the serving configuration's context. A native request may have been capped.
    private async Task<int> LocalProbeContextAfterAnswerAsync(CleanupOptions options, CancellationToken ct)
    {
        var context = await CandidateContextTokensAsync(options, ct).ConfigureAwait(false);
        if (!UsesOllamaApi(options))
        {
            return context;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(LocalContextReadBound);
        try
        {
            var state = await LocalServers.ReadAsync(options.CustomEndpoint!, options.CustomApiKey, bounded.Token)
                .ConfigureAwait(false);
            if (state.LoadedFor(options.CustomModel!) is { ContextTokens: > 0 } held)
            {
                context = Math.Min(context, held.ContextTokens);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The requested size, capped by the model's own maximum, still stands.
        }

        return context;
    }

    /*
     * Must be called under _gate. The glossary one request carries.
     *
     * A model whose context Scribe fits (FitsLocalContext) gets what fits in the room its request leaves for vocabulary: for
     * a dictation, the room its plan left (PlanLocalRequestLocked); for a readying request, the room beside a typical
     * dictation (ContextBudget.ReadyingVocabularyTokens); with the short instructions and without SendWholeVocabulary, also
     * no more than MaxGlossaryTermsLocal terms, as before. A readying request carries the leading run of the whole vocabulary
     * that fits when SendWholeVocabulary is on, and nothing otherwise. Any other model keeps the budgets every release had:
     * the terms a dictation appears to mention, up to MaxGlossaryTermsCloud (MaxGlossaryTermsLocal under the short
     * instructions) and MaxGlossaryChars, and nothing in a readying request.
     */
    private string? GlossaryForLocked(
        CleanupOptions options,
        string? style,
        CleanupVocabulary vocabulary,
        string? dictation,
        bool readying,
        int? vocabularyTokens)
    {
        if (!FitsLocalContext(options))
        {
            return readying
                ? null
                : vocabulary.GlossaryFor(
                    CleanupPrompt.GlossaryTermBudget(options.PromptStyle, options.Provider, options.CustomEndpoint),
                    options.VocabularyMode,
                    dictation);
        }

        if (readying && !options.SendWholeVocabulary)
        {
            return null;
        }

        var budget = !readying && vocabularyTokens is { } planned
            ? planned
            : ContextBudget.ReadyingVocabularyTokens(
                ContextTokensLocked(options), PromptParts.Of(options, style ?? options.WritingStyle, glossary: null).Build());
        var maxTerms = options.SendWholeVocabulary
            ? int.MaxValue
            : CleanupPrompt.GlossaryTermBudget(options.PromptStyle, options.Provider, options.CustomEndpoint);
        return vocabulary.GlossaryFor(
            options.VocabularyMode, options.SendWholeVocabulary, readying ? null : dictation, budget, maxTerms);
    }

    // Through Ollama's own chat API, one client per configuration over the process's shared handler.
    private IChatClient CreateOllamaChatClient(Uri endpoint, string model)
    {
        var root = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/");
        var http = new HttpClient(OllamaHandler(), disposeHandler: false) { BaseAddress = root, Timeout = Timeout.InfiniteTimeSpan };
        return new OllamaSharp.OllamaApiClient(http, model.Trim());
    }

    private HttpMessageHandler OllamaHandler()
    {
        if (InnerHttpHandlerForTesting is not { } network)
        {
            return SharedOllamaHandler.Value;
        }

        lock (_gate)
        {
            return _testOllamaHandler ??= new VocabularyHandOffHandler(network);
        }
    }

    private static AIAgent CreateOllamaAgent(IChatClient client, string instructions) =>
        new ChatClientAgent(client, instructions: instructions, name: AgentName);

    // Foundry Local: the context the model the initialization loaded reads, kept until that initialization publishes it with
    // its options (a demoted or fallen-back run publishes options other than the ones it started with). The initialization's
    // own state, under _initLock, as _pendingFoundryInUse is.
    private int _pendingFoundryContext;

    /*
     * Foundry Local: the context a loaded model reads. Its catalog gives none for its chat models (Foundry Local 2.1.0: no
     * context length in the model's information or properties), so it comes from the model's own ONNX Runtime GenAI
     * configuration, genai_config.json in the model's folder: search.max_length, the most a request and its answer may
     * hold together, and model.context_length, the most the model was made for; the smaller of the two that are set. 0
     * when neither is (ContextBudget.AssumedContextTokens then applies). Measured with Qwen2.5 0.5B: both 32,768, and the
     * SDK's own preflight reported the same limit. Never throws.
     */
    private async Task<int> FoundryContextTokensAsync(Microsoft.AI.Foundry.Local.IModel model, CancellationToken ct)
    {
        try
        {
            var path = await model.GetPathAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            var config = folder is null
                ? null
                : Directory.EnumerateFiles(folder, "genai_config.json", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 2,
                    IgnoreInaccessible = true,
                }).FirstOrDefault();
            if (config is null)
            {
                return 0;
            }

            await using var stream = File.OpenRead(config);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            var limits = new[] { Limit(root, "search", "max_length"), Limit(root, "model", "context_length") };
            return limits.Where(limit => limit > 0).DefaultIfEmpty(0).Min();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("AI cleanup could not read the context of the Foundry Local model ({Failure}).", DescribeFailureShape(ex));
            return 0;
        }

        static int Limit(System.Text.Json.JsonElement root, string section, string name) =>
            root.ValueKind == System.Text.Json.JsonValueKind.Object &&
            root.TryGetProperty(section, out var part) && part.ValueKind == System.Text.Json.JsonValueKind.Object &&
            part.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number &&
            value.TryGetInt64(out var tokens) && tokens > 0
                ? (int)Math.Min(tokens, int.MaxValue)
                : 0;
    }

    /*
     * Ollama's own chat API takes what the OpenAI-compatible request needed patched in as plain options: the context size
     * (num_ctx, asked on every request, so Ollama loads the model at it and never reloads it between Scribe's own
     * requests; the size asked, capped at the largest the model takes, which is what Ollama loads), and how long to keep the
     * model (keep_alive). OllamaSharp sends MaxOutputTokens as num_predict and ReasoningEffort.None as think: false, which
     * every model takes (measured on Ollama 0.35.0 with Gemma 4, Qwen3 and Granite 4, the last of which cannot think at all).
     */
    private void ApplyOllamaApiOptions(ChatOptions chatOptions, CleanupOptions options, int? contextTokens = null)
    {
        chatOptions.Reasoning ??= new ReasoningOptions { Effort = ReasoningEffort.None, Output = ReasoningOutput.None };
        var extra = chatOptions.AdditionalProperties ??= [];
        lock (_gate)
        {
            extra["num_ctx"] = contextTokens ?? ContextTokensLocked(options);
        }

        if (LocalServerKeepAliveMinutes(options) is { Minutes: var minutes })
        {
            extra["keep_alive"] = $"{minutes}m";
        }
    }
}
