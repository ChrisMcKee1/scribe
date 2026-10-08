using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

internal sealed record BenchmarkConfig
{
    public required string OutDir { get; init; }
    public int Runs { get; init; } = 3;
    public bool IncludeCloud { get; init; } = true;
    public bool IncludeLocal { get; init; } = true;
    public IReadOnlyList<string>? CloudOnly { get; init; }
    public IReadOnlyList<string>? LocalOnly { get; init; }
    public IReadOnlyList<string>? CaseOnly { get; init; }
    public int MaxCloud { get; init; }
    public int MaxLocal { get; init; }
    public string? CloudEndpoint { get; init; }
    public string? TenantId { get; init; }
    public string? SubscriptionId { get; init; }
    public string JudgeEndpoint { get; init; } = "https://mtech-project-resource.cognitiveservices.azure.com/";
    public string JudgeModel { get; init; } = "gpt-4.1";
    public string? JudgeTenantId { get; init; }
    public string? JudgeSubscriptionId { get; init; }
    public bool UseJudge { get; init; } = true;
    public bool Synthesize { get; init; } = true;
    public string? ModelsDir { get; init; }
    public bool Force { get; init; }
    public CleanupPromptStyle PromptStyle { get; init; } = CleanupPromptStyle.Auto;
    public string? WritingStyle { get; init; }
    public string? FrontierPrompt { get; init; }
    public string? LocalPrompt { get; init; }

    /// <summary>
    /// Pre-rendered glossary block appended to the system prompt, mirroring what the app builds from
    /// the user's enabled dictionary libraries. Null runs the arm with no glossary, which is how the
    /// libraries' contribution is isolated. Ignored when <see cref="GlossaryEntries"/> is set.
    /// </summary>
    public string? Glossary { get; init; }

    /// <summary>
    /// The dictionary library entries to render as a glossary for each model. Rendered per model at
    /// <see cref="GlossaryMaxTerms"/>, or when that is null at the budget the app itself applies to the
    /// model's provider and prompt style, so a Foundry Local arm carries the same 80 terms a user's
    /// on-device cleanup does and an Ollama arm carries what that provider is sent.
    /// </summary>
    public IReadOnlyList<Scribe.Core.Models.DictionaryEntry>? GlossaryEntries { get; init; }

    /// <summary>An explicit glossary term budget for every model; null follows the app's budget per provider.</summary>
    public int? GlossaryMaxTerms { get; init; }

    /// <summary>
    /// How much of <see cref="GlossaryEntries"/> each dictation carries: all of it (every release before 0.5.2), only the
    /// entries the dictation appears to mention (selected per case by the app's own <see cref="VocabularyMentions"/>), or
    /// none.
    /// </summary>
    public CleanupVocabularyMode VocabularyMode { get; init; } = CleanupVocabularyMode.All;

    /// <summary>
    /// The context size a model on this PC is asked to load at (see <see cref="CleanupOptions.LocalContextTokens"/>): for
    /// Ollama, through its own chat API, as Settings does with a size chosen.
    /// </summary>
    public int? LocalContextTokens { get; init; }

    /// <summary>The whole vocabulary goes to a model on this PC when it fits (<see cref="CleanupOptions.SendWholeVocabulary"/>).</summary>
    public bool SendWholeVocabulary { get; init; }
    public bool AdmitRequests { get; init; }

    /// <summary>
    /// Requests go through the production admission path (<see cref="TextCleanupService.Admit"/>), which fits each request's
    /// vocabulary into the model's context, rather than a glossary this harness renders: for a context size or the whole
    /// vocabulary, which only that path applies.
    /// </summary>
    internal bool Admitted => AdmitRequests || SendWholeVocabulary || LocalContextTokens is not null;

    public LocalEndpoints LocalEndpoints { get; init; } = LocalEndpoints.Default;

    /// <summary>Leaves local servers' models loaded between roster entries, for instances loaded by hand (a CPU-only load).</summary>
    public bool KeepServerModelsLoaded { get; init; }

    /// <summary>
    /// A file of frozen case transcripts: a <c>cases.json</c> array, or benchmark evidence with a
    /// <c>fixtures</c> array. Seeds the output directory, so every model and runtime receives the exact
    /// bytes an earlier run graded.
    /// </summary>
    public string? CasesFrom { get; init; }
    public ReasoningEffort? ReasoningEffort { get; init; }
    public int? MaxOutputTokens { get; init; }

    /// <summary>Replaces the temperature a model on this PC is sent; a negative value sends none (the server's own default).</summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// The least time between the starts of two requests to a model, so a deployment with a small tokens-per-minute
    /// quota is measured rather than throttled (its retries would otherwise be timed as the model). Zero sends at once.
    /// </summary>
    public int PaceMs { get; init; }
    public bool DisableRetries { get; init; }
    public bool DirectResponses { get; init; }
    public int CloudReadyTimeoutSeconds { get; init; } = 120;
    public int LocalReadyTimeoutSeconds { get; init; } = 1800;
    public int CleanTimeoutSeconds { get; init; } = 180;
    internal TimeSpan? CleanupTimeout => CleanTimeoutSeconds == 0 ? null : TimeSpan.FromSeconds(CleanTimeoutSeconds);

    /// <summary>The glossary a model is sent: rendered from <see cref="GlossaryEntries"/> at its budget, or the fixed block.</summary>
    internal string? GlossaryFor(CleanupProvider provider, string? customEndpoint = null)
    {
        if (GlossaryEntries is not { Count: > 0 } entries)
        {
            return VocabularyMode == CleanupVocabularyMode.None ? null : Glossary;
        }

        if (VocabularyMode == CleanupVocabularyMode.None)
        {
            return null;
        }

        var glossary = CleanupPrompt.BuildGlossary(entries, GlossaryBudgetFor(provider, customEndpoint));
        return string.IsNullOrEmpty(glossary) ? null : glossary;
    }

    /// <summary>
    /// The glossary one dictation carries: under <see cref="CleanupVocabularyMode.Mentioned"/>, the entries it appears to
    /// mention; otherwise the model's glossary (<see cref="GlossaryFor"/>).
    /// </summary>
    internal string? GlossaryFor(CleanupProvider provider, string? customEndpoint, string dictation)
    {
        if (VocabularyMode != CleanupVocabularyMode.Mentioned || GlossaryEntries is not { Count: > 0 } entries)
        {
            return GlossaryFor(provider, customEndpoint);
        }

        var glossary = CleanupPrompt.BuildGlossary(
            VocabularyMentions.Select(entries, dictation), GlossaryBudgetFor(provider, customEndpoint));
        return string.IsNullOrEmpty(glossary) ? null : glossary;
    }

    /// <summary>The term budget a model's glossary is rendered at: the pinned one, or the app's own for its provider and address.</summary>
    internal int GlossaryBudgetFor(CleanupProvider provider, string? customEndpoint) =>
        GlossaryMaxTerms ?? CleanupPrompt.GlossaryTermBudget(PromptStyle, provider, customEndpoint);
}

/// <summary>
/// Drives the speed + quality benchmark across the cloud and local model rosters using Scribe's real
/// cleanup pipeline, then writes a markdown leaderboard. Designed for very long runs: results are
/// persisted to <c>results.json</c> after every model and the markdown is regenerated each time, so an
/// interrupted run is resumable (already-graded models are skipped) and a partial board is always on disk.
/// </summary>
internal sealed class BenchmarkRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly BenchmarkConfig _cfg;
    private readonly ILogger _log;

    public BenchmarkRunner(BenchmarkConfig cfg, ILogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_cfg.OutDir);
        var resultsPath = Path.Combine(_cfg.OutDir, "results.json");
        var markdownPath = Path.Combine(_cfg.OutDir, "leaderboard.md");

        var style = CleanupPrompt.ResolveWritingStyle(_cfg.WritingStyle);
        var frontierPrompt = CleanupPrompt.ResolveFrontierPrompt(_cfg.FrontierPrompt);

        Console.WriteLine($"Preparing {BenchmarkCases.All.Count} benchmark cases (WAV synthesis + ASR)…");
        var inputCachePath = Path.Combine(_cfg.OutDir, "cases.json");
        List<BenchCaseInput>? cases = null;
        if (!string.IsNullOrWhiteSpace(_cfg.CasesFrom))
        {
            // Frozen transcripts win over --force, which is about re-running models, not re-recording
            // the speech every model is compared on.
            cases = ReadFrozenCases(_cfg.CasesFrom);
            File.WriteAllText(inputCachePath, JsonSerializer.Serialize(cases, Json));
            Console.WriteLine($"Using {cases.Count} frozen cases from {_cfg.CasesFrom}.");
        }
        else if (!_cfg.Force && File.Exists(inputCachePath))
        {
            cases = JsonSerializer.Deserialize<List<BenchCaseInput>>(File.ReadAllText(inputCachePath), Json);
            if (cases is { Count: > 0 })
            {
                Console.WriteLine($"Reusing cached cases from {inputCachePath} (keeps every model on identical bytes).");
            }
        }

        if (cases is not { Count: > 0 })
        {
            cases = await BenchmarkInput.PrepareCasesAsync(_cfg.OutDir, _cfg.ModelsDir, _cfg.Synthesize, _log, ct)
                .ConfigureAwait(false);
            File.WriteAllText(inputCachePath, JsonSerializer.Serialize(cases, Json));
        }

        if (_cfg.CaseOnly is { Count: > 0 })
        {
            var requested = _cfg.CaseOnly.ToHashSet(StringComparer.OrdinalIgnoreCase);
            cases = cases.Where(c => requested.Contains(c.CaseId)).ToList();
            var missing = requested.Where(id => !cases.Any(c =>
                string.Equals(c.CaseId, id, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (missing.Length > 0)
            {
                _log.LogWarning("Requested benchmark cases were not found: {Missing}.", string.Join(", ", missing));
            }

            if (cases.Count == 0)
            {
                Console.WriteLine("No requested benchmark cases were found.");
                return 2;
            }
        }

        foreach (var c in cases)
        {
            Console.WriteLine($"  {c.CaseId,-22} {c.Source,-24} {c.Transcript.Length} chars");
        }

        Console.WriteLine();

        // Build the roster.
        var roster = new List<BenchModel>();
        if (_cfg.IncludeCloud)
        {
            try
            {
                var cloud = await BenchmarkModels.BuildCloudAsync(
                    _cfg.TenantId, _cfg.CloudEndpoint, _cfg.CloudOnly, _cfg.MaxCloud, _log, ct,
                    _cfg.SubscriptionId)
                    .ConfigureAwait(false);
                roster.AddRange(cloud);
                Console.WriteLine($"Cloud models ({cloud.Count}): {string.Join(", ", cloud.Select(m => m.Id))}");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Cloud discovery failed; continuing with local only.");
                Console.WriteLine($"Cloud discovery failed: {ex.Message}");
            }
        }

        if (_cfg.IncludeLocal)
        {
            var local = BenchmarkModels.BuildLocal(_cfg.LocalOnly, _cfg.MaxLocal, _cfg.LocalEndpoints);
            roster.AddRange(local);
            Console.WriteLine($"Local models ({local.Count}): {string.Join(", ", local.Select(m => m.Id))}");
        }

        Console.WriteLine();

        if (roster.Count == 0)
        {
            Console.WriteLine("No models available; the benchmark did not run.");
            return 2;
        }

        // Judge.
        QualityJudge? judge = null;
        if (_cfg.UseJudge)
        {
            try
            {
                judge = new QualityJudge(_cfg.JudgeEndpoint, _cfg.JudgeModel, _cfg.JudgeTenantId,
                    _cfg.JudgeSubscriptionId);
                await judge.ValidateAsync(ct).ConfigureAwait(false);
                Console.WriteLine($"Judge ready: {_cfg.JudgeModel} @ {_cfg.JudgeEndpoint}");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Judge unavailable; quality scores will be omitted.");
                Console.WriteLine($"Judge unavailable ({ex.Message}); continuing without quality grades.");
                judge = null;
            }
        }

        Console.WriteLine();

        // Resume.
        var results = LoadExisting(resultsPath);
        var done = new HashSet<string>(results.Select(r => $"{r.Group}/{r.Id}"), StringComparer.OrdinalIgnoreCase);

        var meta = new LeaderboardMeta
        {
            GeneratedUtc = DateTime.UtcNow.ToString("u"),
            Machine = Environment.MachineName,
            InputSource = string.Join("; ", cases.Select(c => c.Source).Distinct()),
            WritingStyle = style,
            FrontierPrompt = frontierPrompt,
            JudgeModel = judge is null ? "(none)" : $"{_cfg.JudgeModel} @ {_cfg.JudgeEndpoint}",
            Runs = _cfg.Runs,
            Cases = cases.Select(c => new BenchCaseMeta(c.CaseId, c.Source, c.Transcript, c.Golden, c.AsrMs, c.AudioSeconds)).ToList(),
        };

        // SCRIBE_PERF_FLAGS applies to a benchmark as it does to the app, so a switched-off change (for example the Azure
        // CLI token cache) can be measured through the same service.
        await using var svc = new TextCleanupService(
            new VerboseConsoleLogger<TextCleanupService>(), perfFlags: Scribe.Core.Diagnostics.PerfFlags.FromEnvironment())
        {
            CleanupTimeoutOverride = _cfg.CleanupTimeout,
            ReasoningEffortOverride = _cfg.ReasoningEffort,
            MaxOutputTokensOverride = _cfg.MaxOutputTokens,
            TemperatureOverride = _cfg.Temperature,
            DisableRetries = _cfg.DisableRetries,
        };

        var index = 0;
        foreach (var model in roster)
        {
            index++;
            var key = $"{model.Group}/{model.Id}";
            if (!_cfg.Force && done.Contains(key))
            {
                Console.WriteLine($"[{index}/{roster.Count}] {key}: already done, skipping.");
                continue;
            }

            Console.WriteLine($"[{index}/{roster.Count}] {key} ({model.Target}){(model.Note is null ? "" : $" [{model.Note}]")}");
            // Unloaded before and after, so its load time is a real load and the next model has the GPU to itself.
            if (!_cfg.KeepServerModelsLoaded)
            {
                await LocalServerControl.UnloadAsync(model, ct).ConfigureAwait(false);
            }

            var result = await BenchmarkOneAsync(svc, model, cases, style, judge, ct).ConfigureAwait(false);
            svc.Configure(CleanupOptions.Disabled);
            if (!_cfg.KeepServerModelsLoaded)
            {
                // Ollama and LM Studio at their own address are freed through the service's own release, which turning
                // cleanup off has just queued: a second unloader beside it once left LM Studio loading the model again at
                // its own size (0.5.3). Other servers go through the harness's own request.
                if (model.Provider == CleanupProvider.OpenAiCompatible &&
                    LocalAiServer.AppAt(model.Endpoint) != LocalServerApp.None)
                {
                    await svc.FreeLocalAppModelAsync(model.Endpoint!, model.Target, ct).ConfigureAwait(false);
                }
                else
                {
                    await LocalServerControl.UnloadAsync(model, ct).ConfigureAwait(false);
                }
            }

            results.RemoveAll(r => string.Equals($"{r.Group}/{r.Id}", key, StringComparison.OrdinalIgnoreCase));
            results.Add(result);

            Persist(resultsPath, results);
            LeaderboardWriter.Write(markdownPath, meta, results);

            Console.WriteLine(
                $"      -> {result.Status} | {result.MedianMs:F0} ms median | " +
                $"quality {(result.Quality?.ToString() ?? "-")} ({result.Grade ?? "-"}) | changed={result.Changed}");
            if (!string.IsNullOrEmpty(result.Error))
            {
                Console.WriteLine($"      error: {result.Error}");
            }

            Console.WriteLine($"      out: {Snippet(result.Output ?? "")}");
            Console.WriteLine();

            if (ct.IsCancellationRequested)
            {
                break;
            }
        }

        // Release any resident local model.
        svc.Configure(CleanupOptions.Disabled);

        // Always regenerate the markdown at the end: a fully resumed run (everything already
        // done) still picks up template/format changes without re-benchmarking anything.
        LeaderboardWriter.Write(markdownPath, meta, results);

        Console.WriteLine($"Done. Results: {resultsPath}");
        Console.WriteLine($"Leaderboard: {markdownPath}");
        return 0;
    }

    private async Task<BenchResult> BenchmarkOneAsync(
        TextCleanupService svc, BenchModel model, IReadOnlyList<BenchCaseInput> cases, string style,
        QualityJudge? judge, CancellationToken ct)
    {
        var endpoint = model.Provider == CleanupProvider.OpenAiCompatible ? model.Endpoint : null;
        var admitted = _cfg.Admitted && model.Group != BenchGroup.Cloud;
        var glossary = admitted ? null : _cfg.GlossaryFor(model.Provider, endpoint);
        var vocabulary = admitted
            ? new CleanupVocabulary(_cfg.GlossaryEntries ?? [], Scribe.Core.Libraries.AiVocabularyScope.None)
            : null;
        var options = model.Provider switch
        {
            CleanupProvider.AzureFoundry => new CleanupOptions(true, CleanupProvider.AzureFoundry, CleanupModelCatalog.DefaultAlias,
                model.Endpoint, model.Target, AzureTenantId: _cfg.TenantId, WritingStyle: style,
                AzureSubscriptionId: _cfg.SubscriptionId,
                Glossary: glossary,
                PromptStyle: _cfg.PromptStyle, FrontierPrompt: _cfg.FrontierPrompt, LocalPrompt: _cfg.LocalPrompt),

            // Exactly the configuration Settings builds for "Another AI service": the server's /v1
            // address and the model name it lists, with no key.
            CleanupProvider.OpenAiCompatible => new CleanupOptions(true, CleanupProvider.OpenAiCompatible,
                CleanupModelCatalog.DefaultAlias, null, null,
                WritingStyle: style, Glossary: glossary,
                CustomEndpoint: model.Endpoint, CustomModel: model.Target,
                PromptStyle: _cfg.PromptStyle, FrontierPrompt: _cfg.FrontierPrompt, LocalPrompt: _cfg.LocalPrompt),

            _ => new CleanupOptions(true, CleanupProvider.FoundryLocal, model.Target, null, null,
                WritingStyle: style, Glossary: glossary,
                PromptStyle: _cfg.PromptStyle, FrontierPrompt: _cfg.FrontierPrompt, LocalPrompt: _cfg.LocalPrompt),
        };

        // The tuning Settings gives the app on this PC (LocalModelTuning), and through the admission path the vocabulary
        // mode the product sends with.
        if (admitted)
        {
            options = options with
            {
                VocabularyMode = _cfg.VocabularyMode,
                LocalContextTokens = _cfg.LocalContextTokens,
                SendWholeVocabulary = _cfg.SendWholeVocabulary,
            };
        }

        var loadTimeout = TimeSpan.FromSeconds(
            model.Group == BenchGroup.Cloud ? _cfg.CloudReadyTimeoutSeconds : _cfg.LocalReadyTimeoutSeconds);

        var baseline = new BenchResult
        {
            Group = model.Group.ToString(),
            Id = model.Id,
            Provider = _cfg.DirectResponses ? "AzureResponsesDirect" : model.Provider.ToString(),
            Endpoint = model.Endpoint,
            Target = model.Target,
            ModelName = model.ModelName,
            Note = _cfg.DirectResponses
                ? string.Join("; ", new[] { model.Note, "direct Responses API" }.Where(note => !string.IsNullOrWhiteSpace(note)))
                : model.Note,
            Status = "error",
            CleanTimeoutSeconds = _cfg.CleanTimeoutSeconds,
            AdmittedRequests = admitted,
            LoadedAtUtc = DateTime.UtcNow.ToString("u"),
            PromptStyle = CleanupPrompt.ResolvePromptStyle(options.PromptStyle, options.Provider, options.CustomEndpoint).ToString(),
            GlossaryTerms = _cfg.GlossaryEntries is { Count: > 0 } glossaryEntries && (glossary is not null || admitted)
                ? admitted
                    ? CleanupPrompt.CountGlossary(glossaryEntries, int.MaxValue).Eligible
                    : CleanupPrompt.CountGlossary(glossaryEntries, _cfg.GlossaryBudgetFor(model.Provider, endpoint)).Included
                : 0,
            SystemPromptChars = TextCleanupService.BuildSystemPrompt(options).Length,
        };

        var loadSw = Stopwatch.StartNew();
        DirectResponsesCleanupClient? directClient = null;
        var ready = true;
        if (_cfg.DirectResponses)
        {
            if (model.Provider != CleanupProvider.AzureFoundry || string.IsNullOrWhiteSpace(model.Endpoint))
            {
                loadSw.Stop();
                return baseline with { Error = "Direct Responses diagnostics require a cloud model endpoint." };
            }

            directClient = new DirectResponsesCleanupClient(
                model.Endpoint,
                model.Target,
                _cfg.TenantId,
                TextCleanupService.BuildSystemPrompt(options),
                _cfg.ReasoningEffort,
                _cfg.MaxOutputTokens,
                TimeSpan.FromSeconds(_cfg.CleanTimeoutSeconds),
                _cfg.DisableRetries,
                _cfg.SubscriptionId);
        }
        else
        {
            svc.Configure(options);
            ready = await WaitForReadyAsync(svc, loadTimeout, ct).ConfigureAwait(false);
        }
        loadSw.Stop();

        if (!ready)
        {
            // Cancel any in-flight load/download before moving on so the next model starts clean.
            var status = svc.Status;
            var statusDetail = svc.StatusDetail;
            svc.Configure(CleanupOptions.Disabled);
            return baseline with
            {
                Status = "not-ready",
                Error = $"not ready in {loadTimeout.TotalSeconds:F0}s ({status}: {statusDetail})",
                LoadSeconds = loadSw.Elapsed.TotalSeconds,
            };
        }

        try
        {
            // Under the Mentioned vocabulary each dictation carries its own glossary: a prompt-only change the service
            // applies in place, with no reconnect, exactly as it would for the next dictation in the app. Through the
            // admission path the service picks and fits it itself.
            void UseVocabularyFor(string dictation)
            {
                if (_cfg.VocabularyMode == CleanupVocabularyMode.Mentioned && directClient is null && !admitted)
                {
                    svc.Configure(options with { Glossary = _cfg.GlossaryFor(model.Provider, endpoint, dictation) });
                }
            }

            Task<CleanupResult> Clean(string text) =>
                vocabulary is null ? svc.CleanAsync(text, ct) : svc.Admit(vocabulary).CleanAsync(text, ct);

            // Keeps requests at least PaceMs apart, start to start, so the timing measures the model and not a quota.
            var lastStart = 0L;
            async Task PaceAsync()
            {
                if (_cfg.PaceMs > 0 && lastStart != 0)
                {
                    var wait = _cfg.PaceMs - Stopwatch.GetElapsedTime(lastStart).TotalMilliseconds;
                    if (wait > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
                    }
                }

                lastStart = Stopwatch.GetTimestamp();
            }

            // One warmup call (discarded) so steady-state latency excludes any first-call cost. Its time
            // is kept: it is what the first dictation after the model becomes ready would wait.
            svc.UsageObserver = null;
            UseVocabularyFor(cases[0].Transcript);
            await PaceAsync().ConfigureAwait(false);
            var warmup = Stopwatch.StartNew();
            if (directClient is null)
            {
                _ = await Clean(cases[0].Transcript).ConfigureAwait(false);
            }
            else
            {
                _ = await directClient.CleanAsync(cases[0].Transcript, ct).ConfigureAwait(false);
            }

            warmup.Stop();

            var caseResults = new List<BenchCaseResult>(cases.Count);
            var allTimes = new List<double>(cases.Count * _cfg.Runs);

            foreach (var c in cases)
            {
                var times = new List<double>(_cfg.Runs);
                var usages = new List<BenchTokenUsage?>(_cfg.Runs);
                var outputs = new List<string>(_cfg.Runs);
                var outcomes = new List<string>(_cfg.Runs);
                var partialFailures = new List<bool>(_cfg.Runs);
                var output = c.Transcript;
                UseVocabularyFor(c.Transcript);
                for (var i = 0; i < _cfg.Runs; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await PaceAsync().ConfigureAwait(false);
                    BenchTokenUsage? usage = null;
                    var outcome = "Direct";
                    var partialFailure = false;
                    var sw = Stopwatch.StartNew();
                    if (directClient is null)
                    {
                        svc.UsageObserver = details => usage = AddUsage(usage, details);
                        var cleaned = await Clean(c.Transcript).ConfigureAwait(false);
                        output = cleaned.Text;
                        outcome = cleaned.Outcome.ToString();
                        partialFailure = BenchResult.PartiallyFailed(cleaned);
                    }
                    else
                    {
                        (output, usage) = await directClient.CleanAsync(c.Transcript, ct).ConfigureAwait(false);
                    }
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    usages.Add(usage);
                    outputs.Add(output);
                    outcomes.Add(outcome);
                    partialFailures.Add(partialFailure);
                }

                allTimes.AddRange(times);
                var caseChanged = !string.Equals(output.Trim(), c.Transcript.Trim(), StringComparison.Ordinal);

                JudgeVerdict? verdict = null;
                if (judge is not null)
                {
                    try
                    {
                        verdict = await judge.JudgeAsync(c.Transcript, output, c.Golden, style, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Judge failed for {Model} case {Case}.", model.Id, c.CaseId);
                    }
                }

                var sortedCase = times.OrderBy(t => t).ToList();
                caseResults.Add(new BenchCaseResult(
                    c.CaseId, Median(sortedCase), times.ToArray(), verdict?.Overall,
                    verdict?.Dims, verdict?.Flags ?? [], verdict?.Rationale, caseChanged, output, usages.ToArray(),
                    outputs.ToArray(), outcomes.ToArray(), partialFailures.ToArray()));

                var reasoning = usages
                    .Where(usage => usage?.ReasoningTokens is not null)
                    .Select(usage => (double)usage!.ReasoningTokens!.Value)
                    .OrderBy(tokens => tokens)
                    .ToList();
                var failed = outcomes.Count(o => string.Equals(o, nameof(CleanupOutcome.Failed), StringComparison.Ordinal));
                var partial = partialFailures.Count(failedPart => failedPart);

                Console.WriteLine(
                    $"        {c.CaseId,-22} {Median(sortedCase),6:F0} ms  " +
                    $"q={verdict?.Overall.ToString() ?? "-"}  changed={caseChanged}" +
                    (failed == 0 ? "" : $"  failed={failed}/{outcomes.Count}") +
                    (partial == 0 ? "" : $"  partial={partial}/{outcomes.Count}") +
                    (reasoning.Count == 0 ? "" : $"  reasoning={Median(reasoning):F0} tok"));
            }

            svc.UsageObserver = null;

            // Aggregate: latency pools every timed sample (same case mix for every model, so the
            // comparison is apples-to-apples); quality is the mean of the per-case judge scores;
            // flags are the union; the leaderboard's verbatim output shows the worst-scoring case.
            var sorted = allTimes.OrderBy(t => t).ToList();
            var anyChanged = caseResults.Any(r => r.Changed);
            var qualities = caseResults.Where(r => r.Quality is not null).Select(r => r.Quality!.Value).ToList();
            int? quality = qualities.Count > 0 ? (int)Math.Round(qualities.Average()) : null;

            BenchDimensions? dims = null;
            var dimSets = caseResults.Select(r => r.Dims).Where(d => d is not null).Cast<BenchDimensions>().ToList();
            if (dimSets.Count > 0)
            {
                dims = new BenchDimensions(
                    (int)Math.Round(dimSets.Average(d => d.Mechanics)),
                    (int)Math.Round(dimSets.Average(d => d.Fidelity)),
                    (int)Math.Round(dimSets.Average(d => d.Disfluency)),
                    (int)Math.Round(dimSets.Average(d => d.Instruction)));
            }

            var flags = caseResults.SelectMany(r => r.Flags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var worst = caseResults.Where(r => r.Quality is not null).OrderBy(r => r.Quality).FirstOrDefault()
                ?? caseResults[0];
            var outcomeSummary = BenchResult.SummarizeOutcomes(caseResults, anyChanged);

            return baseline with
            {
                Status = outcomeSummary.Status,
                Error = outcomeSummary.Error,
                MedianMs = Median(sorted),
                MinMs = sorted[0],
                MaxMs = sorted[^1],
                Runs = _cfg.Runs,
                AllMs = allTimes.ToArray(),
                Quality = quality,
                Grade = quality is null ? null : BenchResult.GradeFor(quality.Value),
                Dims = dims,
                Flags = flags,
                Rationale = worst.Rationale is null ? null : $"[worst case: {worst.CaseId}] {worst.Rationale}",
                Changed = anyChanged,
                Output = worst.Output,
                Cases = caseResults.ToArray(),
                LoadSeconds = loadSw.Elapsed.TotalSeconds,
                WarmupMs = warmup.Elapsed.TotalMilliseconds,
                ContextTokens = admitted ? svc.LocalContextTokens : null,
                WholeVocabulary = admitted && _cfg.SendWholeVocabulary,
            };
        }
        catch (Exception ex)
        {
            return baseline with
            {
                Status = "error",
                Error = ex.Message,
                LoadSeconds = loadSw.Elapsed.TotalSeconds,
            };
        }
    }

    private static async Task<bool> WaitForReadyAsync(ITextCleanupService svc, TimeSpan timeout, CancellationToken ct)
    {
        if (svc.Status == CleanupStatus.Ready)
        {
            return true;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged()
        {
            switch (svc.Status)
            {
                case CleanupStatus.Ready: tcs.TrySetResult(true); break;
                case CleanupStatus.Unavailable: tcs.TrySetResult(false); break;
            }
        }

        svc.StatusChanged += OnChanged;
        try
        {
            OnChanged();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            await using var reg = linked.Token.Register(() => tcs.TrySetResult(svc.Status == CleanupStatus.Ready));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            svc.StatusChanged -= OnChanged;
        }
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    internal static BenchTokenUsage AddUsage(BenchTokenUsage? current, UsageDetails usage) => new(
        Add(current?.InputTokens, usage.InputTokenCount),
        Add(current?.OutputTokens, usage.OutputTokenCount),
        Add(current?.ReasoningTokens, usage.ReasoningTokenCount),
        Add(current?.TotalTokens, usage.TotalTokenCount),
        Add(current?.CachedInputTokens, usage.CachedInputTokenCount),
        Add(current?.CacheWriteTokens, CacheWriteCount(usage)));

    // Microsoft.Extensions.AI 10.9 has no typed cache-write count; take one only if the adapter passed it through among its
    // additional counts, under any spelling of "cache write". Absent stays null, never 0.
    internal static long? CacheWriteCount(UsageDetails usage)
    {
        if (usage.AdditionalCounts is not { } counts)
        {
            return null;
        }

        foreach (var (key, value) in counts)
        {
            if (key.Contains("cache_write", StringComparison.OrdinalIgnoreCase)
                || key.Contains("CacheWrite", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static long? Add(long? left, long? right) =>
        left is null && right is null ? null : (left ?? 0) + (right ?? 0);

    private static List<BenchResult> LoadExisting(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<BenchResult>>(json, Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void Persist(string path, List<BenchResult> results) =>
        File.WriteAllText(path, JsonSerializer.Serialize(results, Json));

    /// <summary>
    /// Reads frozen case transcripts from a <c>cases.json</c> array or from benchmark evidence carrying a
    /// <c>fixtures</c> array (docs/benchmarks/*.json). Fails loudly on an empty or unreadable file: a
    /// benchmark silently re-recording its speech would compare models on different bytes.
    /// </summary>
    internal static List<BenchCaseInput> ReadFrozenCases(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("fixtures", out var fixtures) && fixtures.ValueKind == JsonValueKind.Array
                ? fixtures
                : throw new InvalidDataException($"{fullPath} has neither a case array nor a 'fixtures' array.");

        var cases = array.Deserialize<List<BenchCaseInput>>(Json) ?? [];
        cases = cases.Where(c => !string.IsNullOrWhiteSpace(c.CaseId) && !string.IsNullOrWhiteSpace(c.Transcript)).ToList();
        return cases.Count > 0
            ? cases
            : throw new InvalidDataException($"{fullPath} holds no usable cases.");
    }

    private static string Snippet(string text)
    {
        var oneLine = text.ReplaceLineEndings(" / ").Trim();
        return oneLine.Length <= 200 ? oneLine : oneLine[..197] + "...";
    }
}
