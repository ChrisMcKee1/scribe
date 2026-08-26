using Scribe.StyleEval.Checks;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Reads a results file back and prints the two sheets separately.
/// </summary>
/// <remarks>
/// Deliberately two tables rather than one pass rate. A single number hides the exact failure this
/// suite exists to find: a model can score 100% on the rule violations by producing flat, unmarked
/// prose for everything, and only the missed-structure sheet shows it.
/// </remarks>
internal static class Summary
{
    public static int Print(string resultsPath)
    {
        var results = ResultStore.ReadAll(resultsPath).ToList();
        if (results.Count == 0)
        {
            Console.WriteLine($"No results in {resultsPath}.");
            return 1;
        }

        var graded = results.Where(r => r.Error is null).ToList();

        Console.WriteLine();
        Console.WriteLine($"=== {resultsPath} ===");
        Console.WriteLine($"{results.Count} cell(s); {results.Count - graded.Count} transport error(s).");

        var rejected = graded.Where(r => !r.SanitizerAccepted).ToList();
        Console.WriteLine(
            $"Sanitizer rejected {rejected.Count} ({Percent(rejected.Count, graded.Count)}): " +
            (rejected.Count == 0
                ? "none"
                : string.Join(", ", rejected.GroupBy(r => r.SanitizerReason)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} {g.Count()}"))));

        PrintCheckTable("NEGATIVE half: rule violations", graded, CheckPolarity.Negative);
        PrintCheckTable("POSITIVE half: missed structure the content warranted", graded, CheckPolarity.Positive);
        PrintActionTable(graded);
        PrintWorstCells(graded);

        var anyFailure = graded.Any(r => r.Failures > 0) || results.Count != graded.Count;
        return anyFailure ? 2 : 0;
    }

    private static void PrintCheckTable(string title, List<CellResult> graded, CheckPolarity polarity)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine($"  {"check",-20} {"pass",6} {"fail",6} {"n/a",6}  {"fail rate of graded",20}");

        foreach (var name in CheckSuite.Names)
        {
            var verdicts = graded
                .SelectMany(r => r.Checks)
                .Where(c => c.Check == name && c.Polarity == polarity)
                .ToList();

            if (verdicts.Count == 0)
            {
                continue;
            }

            var pass = verdicts.Count(v => v.Status == CheckStatus.Pass);
            var fail = verdicts.Count(v => v.Status == CheckStatus.Fail);
            var skip = verdicts.Count(v => v.Status == CheckStatus.NotApplicable);
            Console.WriteLine($"  {name,-20} {pass,6} {fail,6} {skip,6}  {Percent(fail, pass + fail),20}");
        }
    }

    private static void PrintActionTable(List<CellResult> graded)
    {
        Console.WriteLine();
        Console.WriteLine("By action");
        Console.WriteLine($"  {"action",-20} {"cells",6} {"rule-fails",11} {"missed",7} {"rejected",9} {"p50 ms",8}");

        foreach (var group in graded.GroupBy(r => r.ActionId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cells = group.ToList();
            var latencies = cells.Select(c => c.LatencyMs).OrderBy(l => l).ToList();
            var p50 = latencies.Count == 0 ? 0 : latencies[latencies.Count / 2];

            Console.WriteLine(
                $"  {group.Key,-20} {cells.Count,6} {cells.Sum(c => c.NegativeFailures),11} " +
                $"{cells.Sum(c => c.PositiveFailures),7} {cells.Count(c => !c.SanitizerAccepted),9} {p50,8}");
        }
    }

    private static void PrintWorstCells(List<CellResult> graded)
    {
        var worst = graded.Where(r => r.Failures > 0)
            .OrderByDescending(r => r.Failures)
            .Take(15)
            .ToList();

        if (worst.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No cell failed a deterministic check.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Worst {worst.Count} cell(s)");
        foreach (var cell in worst)
        {
            Console.WriteLine($"  {cell.ScenarioId} / {cell.ActionId}  ({cell.Failures} failure(s))");
            foreach (var check in cell.Checks.Where(c => c.Status == CheckStatus.Fail))
            {
                var tag = check.Polarity == CheckPolarity.Negative ? "broke" : "missed";
                Console.WriteLine($"      {tag} {check.Check}: {check.Reason}");
            }
        }
    }

    private static string Percent(int part, int whole) =>
        whole == 0 ? "n/a" : $"{part * 100.0 / whole:F1}%";
}
