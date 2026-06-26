using System.Diagnostics;
using System.Text.Json;
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
    public int MaxCloud { get; init; }
    public int MaxLocal { get; init; }
    public string? TenantId { get; init; }
    public string JudgeEndpoint { get; init; } = "https://mtech-project-resource.cognitiveservices.azure.com/";
    public string JudgeModel { get; init; } = "gpt-4.1";
    public string? JudgeTenantId { get; init; }
    public bool UseJudge { get; init; } = true;
    public bool Synthesize { get; init; } = true;
    public string? ModelsDir { get; init; }
    public bool Force { get; init; }
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

        var style = CleanupPrompt.DefaultWritingStyle;

        Console.WriteLine("Preparing benchmark input (WAV synthesis + ASR)…");
        var inputCachePath = Path.Combine(_cfg.OutDir, "input.json");
        BenchInput input;
        if (!_cfg.Force && File.Exists(inputCachePath))
        {
            input = JsonSerializer.Deserialize<BenchInput>(File.ReadAllText(inputCachePath), Json)
                ?? await BenchmarkInput.PrepareAsync(_cfg.OutDir, _cfg.ModelsDir, _cfg.Synthesize, _log, ct).ConfigureAwait(false);
            Console.WriteLine($"Reusing cached input from {inputCachePath} (keeps every model on identical bytes).");
        }
        else
        {
            input = await BenchmarkInput.PrepareAsync(_cfg.OutDir, _cfg.ModelsDir, _cfg.Synthesize, _log, ct)
                .ConfigureAwait(false);
            File.WriteAllText(inputCachePath, JsonSerializer.Serialize(input, Json));
        }

        Console.WriteLine($"Input source: {input.Source}");
        Console.WriteLine($"Transcript ({input.Transcript.Length} chars): {input.Transcript}");
        Console.WriteLine();

        // Build the roster.
        var roster = new List<BenchModel>();
        if (_cfg.IncludeCloud)
        {
            try
            {
                var cloud = await BenchmarkModels.BuildCloudAsync(_cfg.TenantId, _cfg.CloudOnly, _cfg.MaxCloud, _log, ct)
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
            InputSource = input.Source,
            InputTranscript = input.Transcript,
            WritingStyle = style,
            JudgeModel = judge is null ? "(none)" : $"{_cfg.JudgeModel} @ {_cfg.JudgeEndpoint}",
            Runs = _cfg.Runs,
            AsrMs = input.AsrMs,
            AudioSeconds = input.AudioSeconds,
        };

        await using var svc = new TextCleanupService(new VerboseConsoleLogger<TextCleanupService>())
        {
            CleanupTimeoutOverride = TimeSpan.FromSeconds(_cfg.CleanTimeoutSeconds),
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
            var result = await BenchmarkOneAsync(svc, model, input.Transcript, style, judge, ct).ConfigureAwait(false);

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

        Console.WriteLine($"Done. Results: {resultsPath}");
        Console.WriteLine($"Leaderboard: {markdownPath}");
        return 0;
    }

    private async Task<BenchResult> BenchmarkOneAsync(
        TextCleanupService svc, BenchModel model, string transcript, string style, QualityJudge? judge, CancellationToken ct)
    {
        var options = model.Provider == CleanupProvider.AzureFoundry
            ? new CleanupOptions(true, CleanupProvider.AzureFoundry, CleanupModelCatalog.DefaultAlias,
                model.Endpoint, model.Target, AzureTenantId: _cfg.TenantId, WritingStyle: style)
            : new CleanupOptions(true, CleanupProvider.FoundryLocal, model.Target, null, null, WritingStyle: style);

        var loadTimeout = TimeSpan.FromSeconds(
            model.Group == BenchGroup.Cloud ? _cfg.CloudReadyTimeoutSeconds : _cfg.LocalReadyTimeoutSeconds);

        var baseline = new BenchResult
        {
            Group = model.Group.ToString(),
            Id = model.Id,
            Provider = model.Provider.ToString(),
            Endpoint = model.Endpoint,
            Target = model.Target,
            ModelName = model.ModelName,
            Note = model.Note,
            Status = "error",
            LoadedAtUtc = DateTime.UtcNow.ToString("u"),
        };

        var loadSw = Stopwatch.StartNew();
        svc.Configure(options);
        var ready = await WaitForReadyAsync(svc, loadTimeout, ct).ConfigureAwait(false);
        loadSw.Stop();

        if (!ready)
        {
            // Cancel any in-flight load/download before moving on so the next model starts clean.
            svc.Configure(CleanupOptions.Disabled);
            return baseline with
            {
                Status = "not-ready",
                Error = $"not ready in {loadTimeout.TotalSeconds:F0}s ({svc.Status}: {svc.StatusDetail})",
                LoadSeconds = loadSw.Elapsed.TotalSeconds,
            };
        }

        try
        {
            // One warmup call (discarded) so steady-state latency excludes any first-call cost.
            _ = await svc.CleanAsync(transcript, ct).ConfigureAwait(false);

            var times = new List<double>(_cfg.Runs);
            string output = transcript;
            for (var i = 0; i < _cfg.Runs; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                output = await svc.CleanAsync(transcript, ct).ConfigureAwait(false);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            var changed = !string.Equals(output.Trim(), transcript.Trim(), StringComparison.Ordinal);

            JudgeVerdict? verdict = null;
            if (judge is not null)
            {
                try
                {
                    verdict = await judge.JudgeAsync(transcript, output, style, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Judge failed for {Model}.", model.Id);
                }
            }

            var sorted = times.OrderBy(t => t).ToList();
            return baseline with
            {
                Status = changed ? "ok" : "degraded",
                Error = changed ? null : "output identical to raw (no-op / internal fallback)",
                MedianMs = Median(sorted),
                MinMs = sorted[0],
                MaxMs = sorted[^1],
                Runs = _cfg.Runs,
                AllMs = times.ToArray(),
                Quality = verdict?.Overall,
                Grade = verdict is null ? null : BenchResult.GradeFor(verdict.Overall),
                Dims = verdict?.Dims,
                Flags = verdict?.Flags ?? [],
                Rationale = verdict?.Rationale,
                Changed = changed,
                Output = output,
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
