namespace Scribe.StyleEval.Judge;

/// <summary>
/// Reads a judge results file back and prints what it found, per action.
/// </summary>
/// <remarks>
/// The console summary leads with the missed-opportunity rate because that is the number the
/// deterministic half cannot produce. It prints the ungrounded finding rate immediately underneath,
/// because a missed-opportunity rate is only worth reading next to the judge's own error rate: a
/// judge quoting spans that are not in the text is telling you its findings are guesses.
/// </remarks>
internal static class JudgeSummary
{
    public static int Print(string judgePath)
    {
        var rows = JudgeStore.ReadAll(judgePath).ToList();
        if (rows.Count == 0)
        {
            Console.WriteLine($"No verdicts in {judgePath}.");
            return 1;
        }

        var graded = rows.Where(r => r.Verdict is not null && r.Error is null).ToList();

        Console.WriteLine();
        Console.WriteLine($"=== {judgePath} ===");
        Console.WriteLine($"{rows.Count} verdict(s); {rows.Count - graded.Count} error(s); {rows.Count(r => r.Cached)} cached.");

        var findings = graded.Sum(r => r.Verdict!.FindingCount);
        var ungrounded = graded.Sum(r => r.Verdict!.UngroundedCount);
        Console.WriteLine(
            $"{findings} finding(s), of which {ungrounded} " +
            $"({Percent(ungrounded, findings)}) quoted a span that is not in the text and were discarded.");

        Console.WriteLine();
        Console.WriteLine("MISSED OPPORTUNITY: structure the content warranted that the answer does not have");
        Console.WriteLine($"  {"action",-20} {"cells",6} {"with miss",10} {"rate",7} {"major",6} {"moderate",9} {"minor",6}");

        foreach (var group in graded.GroupBy(r => r.ActionId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cells = group.ToList();
            var misses = cells.SelectMany(c => c.Verdict!.GroundedMisses).ToList();
            var withMiss = cells.Count(c => c.Verdict!.GroundedMisses.Count > 0);

            Console.WriteLine(
                $"  {group.Key,-20} {cells.Count,6} {withMiss,10} {Percent(withMiss, cells.Count),7} " +
                $"{misses.Count(m => m.Severity == Severity.Major),6} " +
                $"{misses.Count(m => m.Severity == Severity.Moderate),9} " +
                $"{misses.Count(m => m.Severity == Severity.Minor),6}");
        }

        Console.WriteLine();
        Console.WriteLine("QUALITY, 0 to 100, judged against each action's own goal question");
        Console.WriteLine($"  {"action",-20} {"goal",6} {"register",9} {"clarity",8} {"fidelity",9} {"overall",8} {"ship as is",11}");

        foreach (var group in graded.GroupBy(r => r.ActionId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var q = group.Select(r => r.Verdict!.Quality).ToList();
            Console.WriteLine(
                $"  {group.Key,-20} {q.Average(x => x.Goal),6:F1} {q.Average(x => x.Register),9:F1} " +
                $"{q.Average(x => x.Clarity),8:F1} {q.Average(x => x.Fidelity),9:F1} " +
                $"{q.Average(x => x.Overall),8:F1} {Percent(q.Count(x => x.WouldShipAsIs), q.Count),11}");
        }

        Console.WriteLine();
        Console.WriteLine("STRUCTURE VERDICT");
        foreach (var group in graded.GroupBy(r => r.Verdict!.StructureVerdict).OrderByDescending(g => g.Count()))
        {
            var name = string.IsNullOrWhiteSpace(group.Key) ? "(none)" : group.Key;
            Console.WriteLine($"  {name,-20} {group.Count(),6}  {Percent(group.Count(), graded.Count)}");
        }

        var anyFinding = graded.Any(r =>
            r.Verdict!.GroundedMisses.Count > 0 ||
            r.Verdict!.GroundedFidelity.Count > 0 ||
            r.Verdict!.GroundedUnwarranted.Count > 0);

        return anyFinding || graded.Count != rows.Count ? 2 : 0;
    }

    private static string Percent(int part, int whole) =>
        whole == 0 ? "n/a" : $"{part * 100.0 / whole:F1}%";
}
