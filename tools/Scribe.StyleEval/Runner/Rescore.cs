using Scribe.Core.TextActions;
using Scribe.StyleEval.Checks;
using Scribe.StyleEval.Corpus;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Re-grades a results file with today's checkers, from the raw responses already stored in it.
/// </summary>
/// <remarks>
/// The checkers are where the iteration actually happens. A false positive found in one run
/// (a closing code fence counted as a block needing a blank line before it, four correct Teams row
/// labels counted as four emphasis violations) has to be fixable without paying for ten thousand
/// model calls again, so the raw response is kept in every row and re-scoring is a local operation.
/// </remarks>
internal static class Rescore
{
    public static int Run(string resultsPath, string corpusDirectory)
    {
        var results = ResultStore.ReadAll(resultsPath).ToList();
        if (results.Count == 0)
        {
            Console.Error.WriteLine($"No results in {resultsPath}.");
            return 1;
        }

        var scenarios = CorpusLoader.Load(corpusDirectory, out _)
            .ToDictionary(s => s.Id, StringComparer.Ordinal);
        var actions = TextActionCatalog.All.ToDictionary(a => a.Id, StringComparer.Ordinal);

        var rescored = new List<CellResult>(results.Count);
        var orphaned = 0;

        foreach (var result in results)
        {
            if (result.Error is not null ||
                !scenarios.TryGetValue(result.ScenarioId, out var scenario) ||
                !actions.TryGetValue(result.ActionId, out var action))
            {
                if (result.Error is null)
                {
                    orphaned++;
                }

                rescored.Add(result);
                continue;
            }

            // The sanitizer is re-run too. It ships, so a change to a length band or an output
            // contract has to move these numbers exactly as it would move the app's behaviour.
            var sanitized = TextActionSanitizer.Sanitize(result.RawResponse, scenario.Text, action);
            var graded = sanitized.Accepted ? sanitized.Text : result.RawResponse;

            rescored.Add(result with
            {
                SanitizedText = sanitized.Accepted ? sanitized.Text : string.Empty,
                SanitizerAccepted = sanitized.Accepted,
                SanitizerReason = sanitized.Reason.ToString(),
                Checks = CheckSuite.Run(scenario, action, result.RawResponse, graded, sanitized.Accepted),
            });
        }

        if (orphaned > 0)
        {
            Console.WriteLine(
                $"{orphaned} row(s) name a scenario or action the current corpus and catalog do not " +
                "have; their stored verdicts were kept as they were.");
        }

        var rewritten = resultsPath + ".rescored.jsonl";
        WriteAsync(rewritten, rescored).GetAwaiter().GetResult();
        Console.WriteLine($"Re-scored {rescored.Count} cell(s) into {rewritten}.");

        return Summary.Print(rewritten);
    }

    private static async Task WriteAsync(string path, IEnumerable<CellResult> results)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        await using var store = new ResultStore(path);
        foreach (var result in results)
        {
            store.Append(result);
        }
    }
}
