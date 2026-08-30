using System.Collections.Concurrent;
using Scribe.Core.TextActions;
using Scribe.StyleEval.Corpus;
using Scribe.StyleEval.Runner;

namespace Scribe.StyleEval.Judge;

/// <summary>
/// The judge pass: reads the generation results file, sends each stored answer to a DIFFERENT model,
/// and streams schema-conformant verdicts into their own file.
/// </summary>
/// <remarks>
/// <para>
/// It never regenerates. The deterministic half already paid for the answers and stored them, so the
/// judge reads them back; that is what makes the pass re-runnable after a prompt change and what
/// makes <c>--judge-sample</c> a genuinely cheap calibration rather than a second full run.
/// </para>
/// <para>
/// The judge deployment is required and explicit. A model grading its own output is a known validity
/// problem: it prefers its own phrasing and it is systematically blind to the mistakes it makes, so
/// the pass refuses to run when the judge deployment matches the deployment that wrote the answers
/// rather than warning and carrying on.
/// </para>
/// </remarks>
internal sealed class JudgeRunner(StyleEvalOptions options, IReadOnlyList<Scenario> scenarios)
{
    private readonly Dictionary<string, Scenario> _scenarios =
        scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);

    private readonly Dictionary<string, TextAction> _actions =
        TextActionCatalog.All.ToDictionary(a => a.Id, StringComparer.Ordinal);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.JudgeDeployment))
        {
            Console.Error.WriteLine(
                "--judge needs --judge-deployment. There is deliberately no default: the judge must " +
                "be a different model from the one under test, and picking one silently is how a " +
                "suite ends up grading a model with itself.");
            return 64;
        }

        var results = ResultStore.ReadAll(options.ResultsPath).ToList();
        if (results.Count == 0)
        {
            Console.Error.WriteLine(
                $"No generation results in {options.ResultsPath}. Run the generation pass first; the " +
                "judge reads stored answers rather than producing new ones.");
            return 1;
        }

        var generators = results
            .Select(r => r.Deployment)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (generators.Contains(options.JudgeDeployment, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"--judge-deployment {options.JudgeDeployment} is the deployment that produced these " +
                "answers. A model judging its own output inflates its own scores and is blind to its " +
                "own habits, so this is refused rather than warned about. Name a different deployment.");
            return 64;
        }

        var cells = BuildCells(results, out var skipped);
        if (cells.Count == 0)
        {
            Console.Error.WriteLine("Nothing to judge after filtering.");
            return 1;
        }

        var judgedKeys = options.JudgeNoResume
            ? new HashSet<string>(StringComparer.Ordinal)
            : JudgeStore.LoadJudgedKeys(options.JudgePath, out _);

        var cache = LoadCache();
        var pending = cells.Where(c => !judgedKeys.Contains(c.Key)).ToList();

        var cost = new CostEstimator(options.JudgePriceInputPerMillion, options.JudgePriceOutputPerMillion);
        foreach (var previous in JudgeStore.ReadAll(options.JudgePath).Where(r => !r.Cached))
        {
            cost.Observe(previous.InputTokens, previous.OutputTokens);
        }

        PrintPlan(cells, pending.Count, skipped, judgedKeys.Count, cache.Count, cost);

        if (options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("--dry-run: nothing was sent to the judge.");
            return 0;
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("Every selected cell already has a verdict. Use --judge-no-resume to re-judge.");
            return JudgeSummary.Print(options.JudgePath);
        }

        var client = new JudgeClient(
            options.JudgeEndpoint,
            options.JudgeDeployment,
            options.Subscription,
            options.TenantId,
            _actions.Values.Where(a => a.Kind == TextActionKind.Ai),
            options.JudgeReasoning,
            options.JudgeMaxOutputTokens,
            options.Timeout);

        var validationError = await client
            .ValidateAsync(_actions["improve-writing"], ct)
            .ConfigureAwait(false);
        if (validationError is not null)
        {
            Console.Error.WriteLine($"The judge deployment did not answer usably: {validationError}");
            return 1;
        }

        Console.WriteLine($"Judge deployment {options.JudgeDeployment} answered the schema check.");

        var progress = new JudgeProgress(pending.Count, !Console.IsOutputRedirected);
        using var gate = new SemaphoreSlim(options.JudgeConcurrency, options.JudgeConcurrency);

        await using (var store = new JudgeStore(options.JudgePath))
        {
            var work = pending.Select(async cell =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var row = await JudgeOneAsync(client, cell, cache, ct).ConfigureAwait(false);
                    store.Append(row);

                    if (!row.Cached)
                    {
                        cost.Observe(row.InputTokens, row.OutputTokens);
                    }

                    progress.Record(row, cost);

                    if (row.Error is not null)
                    {
                        progress.Note($"  ERROR {cell.ScenarioId} / {cell.ActionId}: {Checks.TextTools.Clip(row.Error, 200)}");
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(work).ConfigureAwait(false);
        }

        progress.Finish();

        Console.WriteLine();
        Console.WriteLine(
            $"Judged {progress.Done} cell(s) in {progress.Elapsed:hh\\:mm\\:ss}, " +
            $"{progress.Cached} served from cache. Observed spend ${cost.SpentUsd:F2}.");

        return JudgeSummary.Print(options.JudgePath);
    }

    /// <summary>One stored answer, resolved against its scenario and its action.</summary>
    private sealed record JudgeTask(CellResult Result, Scenario Scenario, TextAction Action, string Output)
    {
        public string ScenarioId => Result.ScenarioId;

        public string ActionId => Result.ActionId;

        public string Key => Result.Key;
    }

    private async Task<JudgeCell> JudgeOneAsync(
        JudgeClient client,
        JudgeTask task,
        ConcurrentDictionary<string, JudgeVerdict> cache,
        CancellationToken ct)
    {
        var hash = JudgePrompt.ContentHash(task.Scenario, task.Action, task.Output);

        // Identical content gets an identical verdict, so it is not paid for twice. This is not a
        // rare case: a proofread of a clean sentence returns the input unchanged, and several
        // actions on the same scenario can converge on the same answer.
        if (cache.TryGetValue(hash, out var cached))
        {
            return Row(task, hash, cached, null, cachedRow: true, 0, null, null, null);
        }

        var response = await client.JudgeAsync(task.Scenario, task.Action, task.Output, ct).ConfigureAwait(false);

        if (response.Verdict is not null)
        {
            cache.TryAdd(hash, response.Verdict);
        }

        return Row(
            task,
            hash,
            response.Verdict,
            response.Error,
            cachedRow: false,
            response.LatencyMs,
            response.InputTokens,
            response.OutputTokens,
            response.ReasoningTokens);
    }

    private JudgeCell Row(
        JudgeTask task,
        string hash,
        JudgeVerdict? verdict,
        string? error,
        bool cachedRow,
        long latencyMs,
        long? inputTokens,
        long? outputTokens,
        long? reasoningTokens) => new()
        {
            ScenarioId = task.ScenarioId,
            Category = task.Result.Category,
            ActionId = task.ActionId,
            GeneratorDeployment = task.Result.Deployment,
            JudgeDeployment = options.JudgeDeployment!,
            ContentHash = hash,
            Cached = cachedRow,
            Error = error,
            Verdict = verdict,
            LatencyMs = latencyMs,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ReasoningTokens = reasoningTokens,
        };

    /// <summary>
    /// The cells worth judging, with the same filters the generation pass understands.
    /// </summary>
    /// <remarks>
    /// <c>--judge-sample</c> takes the first N scenarios PER ACTION rather than the first N rows.
    /// The question the judge answers is per style, so a calibration pass that spent its whole
    /// budget on one action would answer nothing.
    /// </remarks>
    private List<JudgeTask> BuildCells(List<CellResult> results, out int skipped)
    {
        skipped = 0;
        var tasks = new List<JudgeTask>(results.Count);

        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                skipped++;
                continue;
            }

            if (options.Categories.Count > 0 &&
                !options.Categories.Contains(result.Category, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (options.Actions.Count > 0 &&
                !options.Actions.Contains(result.ActionId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_scenarios.TryGetValue(result.ScenarioId, out var scenario) ||
                !_actions.TryGetValue(result.ActionId, out var action))
            {
                skipped++;
                continue;
            }

            // The same text the deterministic checkers graded: the sanitized answer when the
            // shipping sanitizer accepted it, the raw answer when it did not. Judging the sanitizer's
            // fallback would judge the user's own selection and report it as a perfect result.
            var output = result.SanitizerAccepted ? result.SanitizedText : result.RawResponse;
            if (string.IsNullOrWhiteSpace(output))
            {
                skipped++;
                continue;
            }

            tasks.Add(new JudgeTask(result, scenario, action, output));
        }

        if (options.JudgeSample > 0)
        {
            tasks =
            [
                .. tasks
                    .GroupBy(t => t.ActionId, StringComparer.Ordinal)
                    .SelectMany(g => g.OrderBy(t => t.ScenarioId, StringComparer.Ordinal).Take(options.JudgeSample)),
            ];
        }

        return
        [
            .. tasks
                .OrderBy(t => t.Result.Category, StringComparer.Ordinal)
                .ThenBy(t => t.ScenarioId, StringComparer.Ordinal)
                .ThenBy(t => t.ActionId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Verdicts already on disk, keyed by content hash, for the cache.
    /// </summary>
    /// <remarks>
    /// Only rows written by THIS judge deployment at THIS schema version are eligible. Two judges
    /// disagree, and two prompt generations are not comparable, so mixing either into one report
    /// would produce a number that means nothing.
    /// </remarks>
    private ConcurrentDictionary<string, JudgeVerdict> LoadCache()
    {
        var cache = new ConcurrentDictionary<string, JudgeVerdict>(StringComparer.Ordinal);

        foreach (var row in JudgeStore.ReadAll(options.JudgePath))
        {
            if (row.Verdict is null ||
                row.Error is not null ||
                !string.Equals(row.JudgeVersion, JudgeSchema.Version, StringComparison.Ordinal) ||
                !string.Equals(row.JudgeDeployment, options.JudgeDeployment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cache.TryAdd(row.ContentHash, row.Verdict);
        }

        return cache;
    }

    private void PrintPlan(
        IReadOnlyList<JudgeTask> cells,
        int pending,
        int skipped,
        int judged,
        int cached,
        CostEstimator cost)
    {
        Console.WriteLine();
        Console.WriteLine($"Judge endpoint    {options.JudgeEndpoint}");
        Console.WriteLine($"Judge deployment  {options.JudgeDeployment}");
        Console.WriteLine($"Generation from   {options.ResultsPath}");
        Console.WriteLine($"Verdicts to       {options.JudgePath}");
        Console.WriteLine($"Schema            {JudgeSchema.Name} ({JudgeSchema.Version}), strict");
        Console.WriteLine($"Selected          {cells.Count} cell(s); {skipped} row(s) had no answer to judge");
        Console.WriteLine($"Already judged    {judged}; {cached} verdict(s) available to the cache");
        Console.WriteLine($"To judge          {pending} cell(s) at concurrency {options.JudgeConcurrency}");

        // Estimated from the composed prompts rather than from a guessed average. The judge's input
        // is dominated by the two rulebooks and the action instruction, all of which are known
        // exactly here, so the only estimated part is the pair of texts.
        var instructionTokens = cells.Count == 0
            ? 0
            : (long)cells
                .Select(c => c.Action)
                .DistinctBy(a => a.Id)
                .Average(a => CostEstimator.EstimateTokens(JudgePrompt.BuildInstructions(a)));
        var textTokens = cells.Count == 0
            ? 0
            : (long)cells.Average(c => CostEstimator.EstimateTokens(c.Scenario.Text) +
                                       CostEstimator.EstimateTokens(c.Output));
        var inputPerCell = instructionTokens + textTokens;

        // A verdict is a handful of short findings plus five scores. Reasoning tokens dominate it on
        // a reasoning deployment, and the projection is replaced by observed usage within eight cells.
        const long OutputPerCell = 700;

        Console.WriteLine(
            $"Cost est.         ${cost.Project(pending, inputPerCell, OutputPerCell):F2} " +
            $"({inputPerCell} in + {OutputPerCell} out tokens per cell, {cost.Basis})");
        Console.WriteLine(
            $"                  prices ${options.JudgePriceInputPerMillion:F2}/M in, " +
            $"${options.JudgePriceOutputPerMillion:F2}/M out; override with --judge-price-in and --judge-price-out.");
    }
}
