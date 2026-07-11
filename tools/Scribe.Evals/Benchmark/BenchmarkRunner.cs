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
    public string JudgeEndpoint { get; init; } = "https://mtech-project-resource.cognitiveservices.azure.com/";
    public string JudgeModel { get; init; } = "gpt-4.1";
    public string? JudgeTenantId { get; init; }
    public bool UseJudge { get; init; } = true;
    public bool Synthesize { get; init; } = true;
    public string? ModelsDir { get; init; }
    public bool Force { get; init; }
    public CleanupPromptStyle PromptStyle { get; init; } = CleanupPromptStyle.Auto;
    public string? WritingStyle { get; init; }
    public string? FrontierPrompt { get; init; }
    public ReasoningEffort? ReasoningEffort { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool DisableRetries { get; init; }
    public bool DirectResponses { get; init; }
    public int CloudReadyTimeoutSeconds { get; init; } = 120;
    public int LocalReadyTimeoutSeconds { get; init; } = 1800;
    public int CleanTimeoutSeconds { get; init; } = 180;
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
        if (!_cfg.Force && File.Exists(inputCachePath))
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
                    _cfg.TenantId, _cfg.CloudEndpoint, _cfg.CloudOnly, _cfg.MaxCloud, _log, ct)
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
            var local = BenchmarkModels.BuildLocal(_cfg.LocalOnly, _cfg.MaxLocal);
            roster.AddRange(local);
            Console.WriteLine($"Local models ({local.Count}): {string.Join(", ", local.Select(m => m.Id))}");
        }

        Console.WriteLine();

        // Judge.
        QualityJudge? judge = null;
        if (_cfg.UseJudge)
        {
            try
            {
                judge = new QualityJudge(_cfg.JudgeEndpoint, _cfg.JudgeModel, _cfg.JudgeTenantId);
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

        await using var svc = new TextCleanupService(new VerboseConsoleLogger<TextCleanupService>())
        {
            CleanupTimeoutOverride = TimeSpan.FromSeconds(_cfg.CleanTimeoutSeconds),
            ReasoningEffortOverride = _cfg.ReasoningEffort,
            MaxOutputTokensOverride = _cfg.MaxOutputTokens,
            DisableRetries = _cfg.DisableRetries,
        };

        var index = 0;
        foreach (var model in roster)
        {
            index++;
            var key = $"{model.Group}/{model.Id}";
            if (!_cfg.Force && done.Contains(key))
            {
                Console.WriteLine($"[{index}/{roster.Count}] {key} — already done, skipping.");
                continue;
            }

            Console.WriteLine($"[{index}/{roster.Count}] {key} ({model.Target}){(model.Note is null ? "" : $" [{model.Note}]")}");
            var result = await BenchmarkOneAsync(svc, model, cases, style, judge, ct).ConfigureAwait(false);

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
        var options = model.Provider == CleanupProvider.AzureFoundry
            ? new CleanupOptions(true, CleanupProvider.AzureFoundry, CleanupModelCatalog.DefaultAlias,
                model.Endpoint, model.Target, AzureTenantId: _cfg.TenantId, WritingStyle: style,
                PromptStyle: _cfg.PromptStyle, FrontierPrompt: _cfg.FrontierPrompt)
            : new CleanupOptions(true, CleanupProvider.FoundryLocal, model.Target, null, null,
                WritingStyle: style, PromptStyle: _cfg.PromptStyle, FrontierPrompt: _cfg.FrontierPrompt);

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
            LoadedAtUtc = DateTime.UtcNow.ToString("u"),
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
                _cfg.DisableRetries);
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
            // One warmup call (discarded) so steady-state latency excludes any first-call cost.
            svc.UsageObserver = null;
            if (directClient is null)
            {
                _ = await svc.CleanAsync(cases[0].Transcript, ct).ConfigureAwait(false);
            }
            else
            {
                _ = await directClient.CleanAsync(cases[0].Transcript, ct).ConfigureAwait(false);
            }

            var caseResults = new List<BenchCaseResult>(cases.Count);
            var allTimes = new List<double>(cases.Count * _cfg.Runs);

            foreach (var c in cases)
            {
                var times = new List<double>(_cfg.Runs);
                var usages = new List<BenchTokenUsage?>(_cfg.Runs);
                var output = c.Transcript;
                for (var i = 0; i < _cfg.Runs; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    BenchTokenUsage? usage = null;
                    var sw = Stopwatch.StartNew();
                    if (directClient is null)
                    {
                        svc.UsageObserver = details => usage = AddUsage(usage, details);
                        output = (await svc.CleanAsync(c.Transcript, ct).ConfigureAwait(false)).Text;
                    }
                    else
                    {
                        (output, usage) = await directClient.CleanAsync(c.Transcript, ct).ConfigureAwait(false);
                    }
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    usages.Add(usage);
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
                    verdict?.Dims, verdict?.Flags ?? [], verdict?.Rationale, caseChanged, output, usages.ToArray()));

                var reasoning = usages
                    .Where(usage => usage?.ReasoningTokens is not null)
                    .Select(usage => (double)usage!.ReasoningTokens!.Value)
                    .OrderBy(tokens => tokens)
                    .ToList();

                Console.WriteLine(
                    $"        {c.CaseId,-22} {Median(sortedCase),6:F0} ms  " +
                    $"q={verdict?.Overall.ToString() ?? "-"}  changed={caseChanged}" +
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

            return baseline with
            {
                Status = anyChanged ? "ok" : "degraded",
                Error = anyChanged ? null : "output identical to raw on every case (no-op / internal fallback)",
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

    private static BenchTokenUsage AddUsage(BenchTokenUsage? current, UsageDetails usage) => new(
        Add(current?.InputTokens, usage.InputTokenCount),
        Add(current?.OutputTokens, usage.OutputTokenCount),
        Add(current?.ReasoningTokens, usage.ReasoningTokenCount),
        Add(current?.TotalTokens, usage.TotalTokenCount));

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

    private static string Snippet(string text)
    {
        var oneLine = text.ReplaceLineEndings(" / ").Trim();
        return oneLine.Length <= 200 ? oneLine : oneLine[..197] + "...";
    }
}
