using Scribe.StyleEval.Checks;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Compares two result files cell for cell, so a prompt change can be judged against the run it was
/// meant to improve.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the suite that turns a score into a decision. A single run says
/// "should-bold fails 36 percent of the time"; only a comparison says whether the edit you just made
/// to <c>EnrichmentRules</c> actually helped, and whether it broke something else while doing it.
/// </para>
/// <para>
/// Cells are matched on scenario plus action, so the two files must have been produced from the same
/// corpus. Cells present in only one file are reported and excluded from the rates rather than
/// silently counted, because a partial run compared against a full one would otherwise look like a
/// large regression.
/// </para>
/// <para>
/// The headline number is deliberately NOT the overall pass rate. An edit that fixes eight cells and
/// breaks seven barely moves the aggregate while churning the output, so regressions are counted and
/// listed separately: a change that fixes more than it breaks is progress, and a change that breaks
/// anything at all needs a look at what it broke.
/// </para>
/// </remarks>
internal static class Compare
{
    public static void Run(string baselinePath, string variantPath, TextWriter output)
    {
        var baseline = LoadLatest(baselinePath);
        var variant = LoadLatest(variantPath);

        var shared = baseline.Keys.Intersect(variant.Keys).ToList();
        var onlyBaseline = baseline.Keys.Except(variant.Keys).Count();
        var onlyVariant = variant.Keys.Except(baseline.Keys).Count();

        output.WriteLine();
        output.WriteLine($"=== {Path.GetFileName(baselinePath)}  ->  {Path.GetFileName(variantPath)} ===");
        output.WriteLine($"{shared.Count} cell(s) in both.");
        if (onlyBaseline > 0 || onlyVariant > 0)
        {
            output.WriteLine(
                $"Excluded from rates: {onlyBaseline} only in baseline, {onlyVariant} only in variant.");
        }

        if (shared.Count == 0)
        {
            output.WriteLine("Nothing to compare. Were both files produced from the same corpus?");
            return;
        }

        WriteCheckDeltas(shared, baseline, variant, output);
        WriteActionDeltas(shared, baseline, variant, output);
        WriteMovedCells(shared, baseline, variant, output);
    }

    private static void WriteCheckDeltas(
        List<string> shared,
        Dictionary<string, CellResult> baseline,
        Dictionary<string, CellResult> variant,
        TextWriter output)
    {
        var names = shared
            .SelectMany(k => baseline[k].Checks.Concat(variant[k].Checks))
            .Select(c => c.Check)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        output.WriteLine();
        output.WriteLine("Per check (graded cells only; n/a excluded from the rate)");
        output.WriteLine("  check                  before    after   delta   fixed  broke");

        foreach (var name in names)
        {
            var (beforePass, beforeGraded) = Rate(shared, baseline, name);
            var (afterPass, afterGraded) = Rate(shared, variant, name);

            if (beforeGraded == 0 && afterGraded == 0)
            {
                continue;
            }

            var before = beforeGraded == 0 ? 0 : (double)beforePass / beforeGraded;
            var after = afterGraded == 0 ? 0 : (double)afterPass / afterGraded;

            var fixedCount = shared.Count(k =>
                Status(baseline[k], name) == CheckStatus.Fail && Status(variant[k], name) == CheckStatus.Pass);
            var brokeCount = shared.Count(k =>
                Status(baseline[k], name) == CheckStatus.Pass && Status(variant[k], name) == CheckStatus.Fail);

            var delta = (after - before) * 100;
            var arrow = brokeCount > 0 && fixedCount == 0 ? " <-- regressed" : string.Empty;

            output.WriteLine(
                $"  {name,-20} {before,7:P0} {after,8:P0} {delta,7:+0.0;-0.0;0.0} {fixedCount,7} {brokeCount,6}{arrow}");
        }
    }

    private static void WriteActionDeltas(
        List<string> shared,
        Dictionary<string, CellResult> baseline,
        Dictionary<string, CellResult> variant,
        TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("Per action (total check failures across all cells)");
        output.WriteLine("  action                before   after   delta");

        var actions = shared.Select(k => baseline[k].ActionId).Distinct().OrderBy(a => a, StringComparer.Ordinal);

        foreach (var action in actions)
        {
            var keys = shared.Where(k => baseline[k].ActionId == action).ToList();
            var before = keys.Sum(k => baseline[k].Failures);
            var after = keys.Sum(k => variant[k].Failures);

            output.WriteLine($"  {action,-20} {before,7} {after,7} {after - before,7:+0;-0;0}");
        }
    }

    /// <summary>
    /// Lists the cells that changed verdict. Regressions first and in full, because an edit that
    /// trades one failure for another needs its trade examined, not just its net counted.
    /// </summary>
    private static void WriteMovedCells(
        List<string> shared,
        Dictionary<string, CellResult> baseline,
        Dictionary<string, CellResult> variant,
        TextWriter output)
    {
        var regressed = new List<(string Key, string Check, string Reason)>();
        var improved = new List<(string Key, string Check)>();

        foreach (var key in shared)
        {
            foreach (var after in variant[key].Checks)
            {
                var before = Status(baseline[key], after.Check);
                if (before == CheckStatus.Pass && after.Status == CheckStatus.Fail)
                {
                    regressed.Add((key, after.Check, after.Reason));
                }
                else if (before == CheckStatus.Fail && after.Status == CheckStatus.Pass)
                {
                    improved.Add((key, after.Check));
                }
            }
        }

        output.WriteLine();
        output.WriteLine($"Fixed {improved.Count} check verdict(s); broke {regressed.Count}.");

        if (regressed.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"REGRESSIONS ({regressed.Count}), all listed:");
            foreach (var (key, check, reason) in regressed.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"  {key}");
                output.WriteLine($"      now fails {check}: {reason}");
            }
        }

        if (improved.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Improvements ({improved.Count}), first 25:");
            foreach (var (key, check) in improved.OrderBy(r => r.Key, StringComparer.Ordinal).Take(25))
            {
                output.WriteLine($"  {key} now passes {check}");
            }
        }

        output.WriteLine();
        output.WriteLine(regressed.Count == 0 && improved.Count > 0
            ? "Verdict: strictly better on the shared cells."
            : regressed.Count > improved.Count
                ? "Verdict: net worse. Read the regressions above before keeping this change."
                : improved.Count > regressed.Count
                    ? "Verdict: net better, but read the regressions to confirm the trade is one you want."
                    : "Verdict: no net movement.");
    }

    /// <summary>
    /// Reads a results file keeping the LAST row for each cell.
    /// </summary>
    /// <remarks>
    /// A results file can legitimately carry a cell twice. Resume retries any cell whose first
    /// attempt recorded a transport or authentication error, and the retry is appended rather than
    /// replacing the original, because the file is append-only so that a crash can never lose
    /// completed work. The later row is the real answer, so last wins.
    /// </remarks>
    private static Dictionary<string, CellResult> LoadLatest(string path)
    {
        var map = new Dictionary<string, CellResult>(StringComparer.Ordinal);
        foreach (var row in ResultStore.ReadAll(path))
        {
            map[row.Key] = row;
        }

        return map;
    }

    private static (int Pass, int Graded) Rate(
        List<string> shared, Dictionary<string, CellResult> set, string check)
    {
        var pass = 0;
        var graded = 0;

        foreach (var key in shared)
        {
            switch (Status(set[key], check))
            {
                case CheckStatus.Pass:
                    pass++;
                    graded++;
                    break;
                case CheckStatus.Fail:
                    graded++;
                    break;
            }
        }

        return (pass, graded);
    }

    private static CheckStatus Status(CellResult cell, string check) =>
        cell.Checks.FirstOrDefault(c => c.Check == check)?.Status ?? CheckStatus.NotApplicable;
}
