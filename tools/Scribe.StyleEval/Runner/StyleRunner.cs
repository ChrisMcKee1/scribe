using Scribe.Core.TextActions;
using Scribe.StyleEval.Checks;
using Scribe.StyleEval.Corpus;

namespace Scribe.StyleEval.Runner;

/// <summary>One unit of work: one selection through one action.</summary>
internal sealed record Cell(Scenario Scenario, TextAction Action)
{
    public string Key => CellResult.CellKey(Scenario.Id, Action.Id);
}

/// <summary>
/// Drives the corpus across the model-backed actions, grades each answer, and streams results.
/// </summary>
/// <remarks>
/// Everything here exists because the full matrix is roughly ten thousand paid model calls: bounded
/// concurrency so the deployment is not stampeded, an append-per-cell results file so a crash costs
/// one cell rather than the run, a resume index so restarting is free, and a cost projection that
/// stops guessing as soon as real token counts arrive.
/// </remarks>
internal sealed class StyleRunner(
    StyleEvalOptions options,
    IReadOnlyList<Scenario> scenarios,
    IReadOnlyList<TextAction> actions)
{
    /// <summary>The model-backed actions, in catalog order. The vocabulary pass runs no model.</summary>
    public static IReadOnlyList<TextAction> ModelBackedActions { get; } =
        [.. TextActionCatalog.All.Where(a => a.Kind == TextActionKind.Ai)];

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var cells = BuildCells();
        var completed = options.NoResume
            ? new HashSet<string>(StringComparer.Ordinal)
            : ResultStore.LoadCompletedKeys(options.ResultsPath, out var malformed);

        if (!options.NoResume && completed.Count > 0)
        {
            Console.WriteLine($"Resuming: {completed.Count} cell(s) already in {options.ResultsPath}.");
        }

        var pending = cells.Where(c => !completed.Contains(c.Key)).ToList();

        var cost = new CostEstimator(options.PriceInputPerMillion, options.PriceOutputPerMillion);

        // Cells already on disk carry real token counts, so a resumed run starts with a projection
        // grounded in what this corpus and this deployment actually cost rather than in a character
        // count. That matters most on the run where the number is worth checking: the full one.
        if (completed.Count > 0)
        {
            foreach (var previous in ResultStore.ReadAll(options.ResultsPath))
            {
                cost.Observe(previous.InputTokens, previous.OutputTokens);
            }
        }

        PrintPlan(cells.Count, pending.Count, cost);

        if (options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("--dry-run: nothing was sent.");
            return 0;
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("Nothing to do. Use --no-resume to re-run.");
            return Summarize();
        }

        // One governor for the whole run, shared by every worker. Per-worker pacing would multiply
        // the ceiling by the concurrency and defeat the point.
        var limiter = new AdaptiveRateLimiter(options.RequestsPerMinute);

        var client = new StyleActionClient(
            options.Endpoint,
            options.Deployment,
            options.Subscription,
            options.TenantId,
            actions,
            options.Reasoning,
            options.MaxOutputTokens,
            options.Timeout,
            limiter);

        var progress = new ProgressReporter(pending.Count, !Console.IsOutputRedirected);
        using var gate = new SemaphoreSlim(options.Concurrency, options.Concurrency);

        var estimatedInput = EstimateInputTokens(client);
        var refined = false;

        // The store owns the results file handle for the whole run, so it has to be closed before
        // the summary reopens the file to read it back.
        await using (var store = new ResultStore(options.ResultsPath))
        {
        var work = pending.Select(async cell =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await ExecuteAsync(client, cell, ct).ConfigureAwait(false);
                store.Append(result);
                cost.Observe(result.InputTokens, result.OutputTokens);
                progress.Record(result, cost);

                if (result.Error is not null)
                {
                    progress.Note($"  ERROR {cell.Scenario.Id} / {cell.Action.Id}: {TextTools.Clip(result.Error, 200)}");
                }

                if (!refined && cost.IsRefined)
                {
                    refined = true;
                    progress.Note(
                        $"  Cost projection refined from {cost.Basis}: " +
                        $"${cost.Project(pending.Count, estimatedInput, estimatedInput):F2} for {pending.Count} cell(s).");
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
            $"Completed {progress.Done} cell(s) in {progress.Elapsed:hh\\:mm\\:ss}. " +
            $"Observed spend ${cost.SpentUsd:F2}.");

        return Summarize();
    }

    private async Task<CellResult> ExecuteAsync(StyleActionClient client, Cell cell, CancellationToken ct)
    {
        var response = await client.RunAsync(cell.Action, cell.Scenario.Text, ct).ConfigureAwait(false);

        if (response.Error is not null)
        {
            return new CellResult
            {
                ScenarioId = cell.Scenario.Id,
                Category = cell.Scenario.Category,
                ActionId = cell.Action.Id,
                Deployment = options.Deployment,
                Error = response.Error,
                LatencyMs = response.LatencyMs,
            };
        }

        // The REAL sanitizer, not a copy. Its verdict is itself a result worth recording: an answer
        // it rejects never reaches the user's document, however good the prose was.
        var sanitized = TextActionSanitizer.Sanitize(response.Text, cell.Scenario.Text, cell.Action);

        // On rejection the sanitizer hands back the user's own selection, so grading its output would
        // score the INPUT and report a spotless sheet for a failed cell. Grade the raw answer instead.
        var graded = sanitized.Accepted ? sanitized.Text : response.Text;

        var checks = CheckSuite.Run(cell.Scenario, cell.Action, response.Text, graded, sanitized.Accepted);

        return new CellResult
        {
            ScenarioId = cell.Scenario.Id,
            Category = cell.Scenario.Category,
            ActionId = cell.Action.Id,
            Deployment = options.Deployment,
            RawResponse = response.Text,
            SanitizedText = sanitized.Accepted ? sanitized.Text : string.Empty,
            SanitizerAccepted = sanitized.Accepted,
            SanitizerReason = sanitized.Reason.ToString(),
            LatencyMs = response.LatencyMs,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            ReasoningTokens = response.ReasoningTokens,
            Checks = checks,
        };
    }

    private List<Cell> BuildCells()
    {
        var selected = scenarios.AsEnumerable();

        if (options.Categories.Count > 0)
        {
            selected = selected.Where(s => options.Categories.Contains(s.Category, StringComparer.OrdinalIgnoreCase));
        }

        if (options.Sample > 0)
        {
            selected = selected
                .GroupBy(s => s.Category, StringComparer.Ordinal)
                .SelectMany(g => g.Take(options.Sample));
        }

        return [.. selected
            .OrderBy(s => s.Category, StringComparer.Ordinal)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .SelectMany(s => actions.Select(a => new Cell(s, a)))];
    }

    private long EstimateInputTokens(StyleActionClient client) =>
        actions.Count == 0
            ? 0
            : (long)actions.Average(a => CostEstimator.EstimateTokens(client.SystemPromptFor(a.Id)));

    private void PrintPlan(int totalCells, int pendingCells, CostEstimator cost)
    {
        var scenarioCount = scenarios.Count == 0 ? 0 : totalCells / Math.Max(1, actions.Count);

        Console.WriteLine();
        Console.WriteLine($"Endpoint    {options.Endpoint}");
        Console.WriteLine($"Deployment  {options.Deployment}");
        Console.WriteLine($"Identity    {(options.TenantId is not null ? "tenant " + options.TenantId : "subscription " + options.Subscription)}");
        Console.WriteLine($"Corpus      {options.CorpusDirectory}");
        Console.WriteLine($"Results     {options.ResultsPath}");
        Console.WriteLine($"Actions     {string.Join(", ", actions.Select(a => a.Id))}");
        Console.WriteLine($"Matrix      {scenarioCount} scenario(s) x {actions.Count} action(s) = {totalCells} cell(s)");
        Console.WriteLine($"To run      {pendingCells} cell(s) at concurrency {options.Concurrency}");

        // Estimate from the composed prompts and the corpus, not from a guessed average: the system
        // prompt is by far the largest part of a text-action request and it is known exactly.
        var averageSystem = actions.Count == 0
            ? 0
            : (long)actions.Average(a => CostEstimator.EstimateTokens(TextActionPrompt.BuildSystemPrompt(a)));
        var averageSelection = scenarios.Count == 0
            ? 0
            : (long)scenarios.Average(s => CostEstimator.EstimateTokens(s.Text));
        var inputPerCell = averageSystem + averageSelection;
        var outputPerCell = Math.Max(200, averageSelection * 2);

        Console.WriteLine(
            $"Cost est.   ${cost.Project(pendingCells, inputPerCell, outputPerCell):F2} " +
            $"({inputPerCell} in + {outputPerCell} out tokens per cell, {cost.Basis})");
        Console.WriteLine(
            $"            prices ${options.PriceInputPerMillion:F2}/M in, ${options.PriceOutputPerMillion:F2}/M out; " +
            "override with --price-in and --price-out.");
    }

    /// <summary>Re-reads the results file and prints the two failure sheets, negative and positive.</summary>
    public int Summarize() => Summary.Print(options.ResultsPath);
}
