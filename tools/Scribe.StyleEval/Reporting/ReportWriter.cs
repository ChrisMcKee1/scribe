using System.Globalization;
using System.Text;
using Scribe.Core.TextActions;
using Scribe.StyleEval.Checks;
using Scribe.StyleEval.Corpus;
using Scribe.StyleEval.Judge;
using Scribe.StyleEval.Runner;

namespace Scribe.StyleEval.Reporting;

/// <summary>
/// Writes the Markdown report: both halves of the deterministic suite and the judge, in one file.
/// </summary>
/// <remarks>
/// <para>
/// The console summaries are for watching a run. This is the artefact somebody reads afterwards to
/// decide whether an instruction set needs work, so it is organised around decisions rather than
/// around the order the checkers happen to run in: which style is weakest, on which kind of text,
/// and what exactly did it produce.
/// </para>
/// <para>
/// The judge columns sit beside the deterministic ones rather than in a section of their own on
/// purpose. The two halves answer different questions about the same cell, and reading them apart is
/// how a suite ends up celebrating a spotless rule sheet produced by a model that formatted nothing.
/// </para>
/// </remarks>
internal static class ReportWriter
{
    /// <summary>Cells shown verbatim in the worst-cells section.</summary>
    private const int WorstCellCount = 20;

    /// <summary>Longest input or output quoted in the worst-cells section, in characters.</summary>
    private const int QuoteLimit = 1400;

    /// <summary>A check pass rate below this is called out as worth acting on.</summary>
    private const double RegressionThreshold = 0.90;

    /// <summary>A missed-opportunity rate above this is called out as worth acting on.</summary>
    private const double MissedOpportunityThreshold = 0.20;

    public static int Write(StyleEvalOptions options, IReadOnlyList<Scenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scenarios);

        var results = ResultStore.ReadAll(options.ResultsPath).ToList();
        if (results.Count == 0)
        {
            Console.Error.WriteLine($"No generation results in {options.ResultsPath}; nothing to report.");
            return 1;
        }

        var judged = File.Exists(options.JudgePath)
            ? JudgeStore.ReadAll(options.JudgePath).ToList()
            : [];

        var scenarioById = scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var actionById = TextActionCatalog.All.ToDictionary(a => a.Id, StringComparer.Ordinal);

        // Last verdict wins if a cell was judged twice, which is what --judge-no-resume produces.
        var judgeByKey = judged
            .Where(j => j.Verdict is not null && j.Error is null)
            .GroupBy(j => j.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var graded = results.Where(r => r.Error is null).ToList();
        var actions = graded
            .Select(r => r.ActionId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(CatalogOrder)
            .ToList();

        var sb = new StringBuilder(1 << 18);

        WriteHeader(sb, options, results, graded, judged, judgeByKey);
        WriteHeadlineMatrix(sb, graded, actions, judgeByKey);
        WriteMissedOpportunities(sb, graded, actions, judgeByKey);
        WriteQuality(sb, graded, actions, judgeByKey);
        WriteCategories(sb, graded, actions, judgeByKey);
        WriteWorstCells(sb, graded, judgeByKey, scenarioById, actionById);
        WriteRegressions(sb, graded, actions, judgeByKey);
        WriteCalibration(sb, graded, judgeByKey, scenarioById);

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.ReportPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(options.ReportPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"Report written to {options.ReportPath} ({sb.Length:N0} characters).");
        return 0;
    }

    private static void WriteHeader(
        StringBuilder sb,
        StyleEvalOptions options,
        List<CellResult> results,
        List<CellResult> graded,
        List<JudgeCell> judged,
        Dictionary<string, JudgeCell> judgeByKey)
    {
        var generators = graded.Select(r => r.Deployment).Distinct(StringComparer.Ordinal).ToList();
        var judges = judged.Select(r => r.JudgeDeployment).Distinct(StringComparer.Ordinal).ToList();
        var rejected = graded.Count(r => !r.SanitizerAccepted);

        sb.AppendLine("# Scribe text actions: style evaluation");
        sb.AppendLine();
        sb.AppendLine(
            "Side testing only. This report grades the shipping instruction sets in " +
            "`src/Scribe.Core/TextActions`; nothing under `tools/` is published.");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} local time.");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Generation results | `{options.ResultsPath}` |");
        sb.AppendLine($"| Cells | {results.Count:N0} ({results.Count - graded.Count} transport error(s)) |");
        sb.AppendLine($"| Generating deployment | {(generators.Count == 0 ? "none" : string.Join(", ", generators))} |");
        sb.AppendLine($"| Sanitizer rejections | {rejected} ({Rate(rejected, graded.Count)}) |");
        sb.AppendLine($"| Judge verdicts | `{options.JudgePath}` |");
        sb.AppendLine($"| Judged cells | {judgeByKey.Count:N0} of {graded.Count:N0} |");
        sb.AppendLine($"| Judge deployment | {(judges.Count == 0 ? "not run" : string.Join(", ", judges))} |");
        sb.AppendLine($"| Judge schema | `{JudgeSchema.Name}` ({JudgeSchema.Version}), strict |");
        sb.AppendLine();

        if (judgeByKey.Count == 0)
        {
            sb.AppendLine(
                "> The judge has not run over these results. Every judge column below is empty, which " +
                "means the only failures visible in this report are the ones a rule can count. Run " +
                "`--judge --judge-deployment <name>` for the other half.");
            sb.AppendLine();
        }
    }

    private static void WriteHeadlineMatrix(
        StringBuilder sb,
        List<CellResult> graded,
        List<string> actions,
        Dictionary<string, JudgeCell> judgeByKey)
    {
        sb.AppendLine("## Headline matrix");
        sb.AppendLine();
        sb.AppendLine(
            "Pass rate per action and check, over the cells where the check applied. A dash means the " +
            "check never applied to that action: the destination cannot express the construct, or the " +
            "action was never given the Detection rules, and neither is a silent pass.");
        sb.AppendLine();
        sb.AppendLine(
            "The last three columns are the judge, on the same cells. `miss` is the share of judged " +
            "cells where the judge found structure the content warranted and the answer does not " +
            "have, counting only findings whose quoted span was located in the text. `over` is the " +
            "same for structure that was not warranted. `qual` is the mean holistic score out of 100.");
        sb.AppendLine();

        var negative = CheckSuite.Names.Where(n => IsNegative(graded, n)).ToList();
        var positive = CheckSuite.Names.Where(n => !IsNegative(graded, n)).ToList();

        sb.Append("| action |");
        foreach (var name in negative.Concat(positive))
        {
            sb.Append(' ').Append(Abbreviate(name)).Append(" |");
        }

        sb.AppendLine(" miss | over | qual |");

        sb.Append("|---|");
        for (var i = 0; i < negative.Count + positive.Count + 3; i++)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        foreach (var action in actions)
        {
            var cells = graded.Where(r => r.ActionId == action).ToList();
            sb.Append("| `").Append(action).Append("` |");

            foreach (var name in negative.Concat(positive))
            {
                var verdicts = cells.SelectMany(c => c.Checks).Where(c => c.Check == name).ToList();
                var pass = verdicts.Count(v => v.Status == CheckStatus.Pass);
                var fail = verdicts.Count(v => v.Status == CheckStatus.Fail);
                sb.Append(' ').Append(pass + fail == 0 ? "-" : Rate(pass, pass + fail)).Append(" |");
            }

            var verdictRows = cells
                .Select(c => judgeByKey.TryGetValue(c.Key, out var j) ? j : null)
                .Where(j => j is not null)
                .Select(j => j!.Verdict!)
                .ToList();

            if (verdictRows.Count == 0)
            {
                sb.AppendLine(" - | - | - |");
                continue;
            }

            var withMiss = verdictRows.Count(v => v.GroundedMisses.Count > 0);
            var withOver = verdictRows.Count(v => v.GroundedUnwarranted.Count > 0);
            sb.Append(' ').Append(Rate(withMiss, verdictRows.Count)).Append(" |");
            sb.Append(' ').Append(Rate(withOver, verdictRows.Count)).Append(" |");
            sb.Append(' ').Append(verdictRows.Average(v => v.Quality.Overall).ToString("F0", CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("Column names, in order:");
        sb.AppendLine();
        foreach (var name in negative.Concat(positive))
        {
            sb.Append("- `").Append(Abbreviate(name)).Append("` ").Append(name).Append(" (")
              .Append(IsNegative(graded, name) ? "rule violation" : "missed structure").AppendLine(")");
        }

        sb.AppendLine();
    }

    private static void WriteMissedOpportunities(
        StringBuilder sb,
        List<CellResult> graded,
        List<string> actions,
        Dictionary<string, JudgeCell> judgeByKey)
    {
        sb.AppendLine("## Missed opportunity, by action");
        sb.AppendLine();
        sb.AppendLine(
            "How often each style failed to find structure the content actually warranted. This is " +
            "the half no rule can measure: every one of these cells passed the restraint ceilings by " +
            "doing nothing. Counts are findings, not cells, so one cell can contribute more than one.");
        sb.AppendLine();

        if (judgeByKey.Count == 0)
        {
            sb.AppendLine("The judge has not run. Nothing to report here.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| action | judged | cells with a miss | rate | major | moderate | minor | most common kind |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var action in actions)
        {
            var verdicts = VerdictsFor(graded, judgeByKey, r => r.ActionId == action);
            if (verdicts.Count == 0)
            {
                continue;
            }

            var misses = verdicts.SelectMany(v => v.GroundedMisses).ToList();
            var withMiss = verdicts.Count(v => v.GroundedMisses.Count > 0);
            var topKind = misses
                .GroupBy(m => m.Kind, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} ({g.Count()})")
                .FirstOrDefault() ?? "none";

            sb.AppendLine(
                $"| `{action}` | {verdicts.Count} | {withMiss} | {Rate(withMiss, verdicts.Count)} | " +
                $"{misses.Count(m => m.Severity == Severity.Major)} | " +
                $"{misses.Count(m => m.Severity == Severity.Moderate)} | " +
                $"{misses.Count(m => m.Severity == Severity.Minor)} | {topKind} |");
        }

        sb.AppendLine();

        var allMisses = judgeByKey.Values.SelectMany(j => j.Verdict!.GroundedMisses).ToList();
        if (allMisses.Count > 0)
        {
            sb.AppendLine("What was missed, across every action:");
            sb.AppendLine();
            sb.AppendLine("| kind | findings | major | moderate | minor |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var group in allMisses.GroupBy(m => m.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine(
                    $"| {group.Key} | {group.Count()} | {group.Count(m => m.Severity == Severity.Major)} | " +
                    $"{group.Count(m => m.Severity == Severity.Moderate)} | " +
                    $"{group.Count(m => m.Severity == Severity.Minor)} |");
            }

            sb.AppendLine();
        }
    }

    private static void WriteQuality(
        StringBuilder sb,
        List<CellResult> graded,
        List<string> actions,
        Dictionary<string, JudgeCell> judgeByKey)
    {
        sb.AppendLine("## Quality, by action");
        sb.AppendLine();

        if (judgeByKey.Count == 0)
        {
            sb.AppendLine("The judge has not run. Nothing to report here.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(
            "Scored against each action's own goal question, not a generic rubric: whether a Teams " +
            "message reads like a colleague wrote it, whether an agent brief is actionable without " +
            "a follow-up question, whether the proofread left everything else alone.");
        sb.AppendLine();
        sb.AppendLine("| action | n | goal | register | clarity | fidelity | overall | ship as is |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var action in actions)
        {
            var verdicts = VerdictsFor(graded, judgeByKey, r => r.ActionId == action);
            if (verdicts.Count == 0)
            {
                continue;
            }

            var q = verdicts.Select(v => v.Quality).ToList();
            sb.AppendLine(
                $"| `{action}` | {q.Count} | {q.Average(x => x.Goal):F1} | {q.Average(x => x.Register):F1} | " +
                $"{q.Average(x => x.Clarity):F1} | {q.Average(x => x.Fidelity):F1} | {q.Average(x => x.Overall):F1} | " +
                $"{Rate(q.Count(x => x.WouldShipAsIs), q.Count)} |");
        }

        sb.AppendLine();
        sb.AppendLine(
            "A mean hides the shape. The distribution below is what says whether an action is " +
            "reliably good or usually good and occasionally unusable, and only the second is worth " +
            "changing a prompt over.");
        sb.AppendLine();
        sb.AppendLine("| action | min | p25 | median | p75 | max | 0 to 59 | 60 to 79 | 80 to 89 | 90 to 100 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var action in actions)
        {
            var verdicts = VerdictsFor(graded, judgeByKey, r => r.ActionId == action);
            if (verdicts.Count == 0)
            {
                continue;
            }

            var scores = verdicts.Select(v => v.Quality.Overall).OrderBy(x => x).ToList();
            sb.AppendLine(
                $"| `{action}` | {scores[0]} | {Percentile(scores, 0.25)} | {Percentile(scores, 0.50)} | " +
                $"{Percentile(scores, 0.75)} | {scores[^1]} | " +
                $"{Bucket(scores, 0, 59)} | {Bucket(scores, 60, 79)} | {Bucket(scores, 80, 89)} | {Bucket(scores, 90, 100)} |");
        }

        sb.AppendLine();
    }

    private static void WriteCategories(
        StringBuilder sb,
        List<CellResult> graded,
        List<string> actions,
        Dictionary<string, JudgeCell> judgeByKey)
    {
        sb.AppendLine("## By corpus category");
        sb.AppendLine();
        sb.AppendLine(
            "Whether a style fails everywhere or only on one kind of text. A style that is fine on a " +
            "chat message and falls over on long-form prose needs a different fix from one that is " +
            "uniformly weak.");
        sb.AppendLine();

        var categories = graded
            .Select(r => r.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine("| category | cells | rule failures | missed structure (rules) | judged | miss rate (judge) | overall |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var category in categories)
        {
            var cells = graded.Where(r => r.Category == category).ToList();
            var negativeFails = cells.Sum(c => c.NegativeFailures);
            var positiveFails = cells.Sum(c => c.PositiveFailures);
            var verdicts = VerdictsFor(graded, judgeByKey, r => r.Category == category);
            var withMiss = verdicts.Count(v => v.GroundedMisses.Count > 0);

            sb.AppendLine(
                $"| {category} | {cells.Count} | {negativeFails} | {positiveFails} | {verdicts.Count} | " +
                $"{(verdicts.Count == 0 ? "-" : Rate(withMiss, verdicts.Count))} | " +
                $"{(verdicts.Count == 0 ? "-" : verdicts.Average(v => v.Quality.Overall).ToString("F0", CultureInfo.InvariantCulture))} |");
        }

        sb.AppendLine();

        if (judgeByKey.Count == 0)
        {
            return;
        }

        sb.AppendLine("Missed-opportunity rate, action by category:");
        sb.AppendLine();
        sb.Append("| action |");
        foreach (var category in categories)
        {
            sb.Append(' ').Append(category).Append(" |");
        }

        sb.AppendLine();
        sb.Append("|---|");
        foreach (var _ in categories)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        foreach (var action in actions)
        {
            sb.Append("| `").Append(action).Append("` |");
            foreach (var category in categories)
            {
                var verdicts = VerdictsFor(graded, judgeByKey, r => r.ActionId == action && r.Category == category);
                sb.Append(' ')
                  .Append(verdicts.Count == 0
                      ? "-"
                      : Rate(verdicts.Count(v => v.GroundedMisses.Count > 0), verdicts.Count))
                  .Append(" |");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
    }

    private static void WriteWorstCells(
        StringBuilder sb,
        List<CellResult> graded,
        Dictionary<string, JudgeCell> judgeByKey,
        Dictionary<string, Scenario> scenarioById,
        Dictionary<string, TextAction> actionById)
    {
        sb.AppendLine($"## The {WorstCellCount} worst cells, verbatim");
        sb.AppendLine();
        sb.AppendLine(
            "Ranked by a single severity score so both halves count: three points per rule violation, " +
            "two per missed structure a rule caught, three points per major judge finding, two per " +
            "moderate, one per minor, and one point for every twenty five points the holistic score " +
            "falls short of 100. A sanitizer rejection adds four, because that answer never reaches " +
            "the user's document at all.");
        sb.AppendLine();

        var ranked = graded
            .Select(cell => (Cell: cell, Judge: judgeByKey.GetValueOrDefault(cell.Key)))
            .Select(pair => (pair.Cell, pair.Judge, Score: SeverityScore(pair.Cell, pair.Judge?.Verdict)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cell.ScenarioId, StringComparer.Ordinal)
            .Take(WorstCellCount)
            .ToList();

        if (ranked.Count == 0)
        {
            sb.AppendLine("No cell failed anything. Check that the corpus and the checkers are actually running.");
            sb.AppendLine();
            return;
        }

        foreach (var (cell, judge, score) in ranked)
        {
            var scenario = scenarioById.GetValueOrDefault(cell.ScenarioId);
            var action = actionById.GetValueOrDefault(cell.ActionId);

            sb.AppendLine($"### `{cell.ScenarioId}` through `{cell.ActionId}` (severity {score})");
            sb.AppendLine();

            if (scenario is not null && !string.IsNullOrWhiteSpace(scenario.Note))
            {
                sb.Append("What the case tests: ").AppendLine(scenario.Note.Trim());
                sb.AppendLine();
            }

            if (action is not null)
            {
                sb.Append("Enrichment level: `").Append(action.Enrichment).Append("`. Length band: `")
                  .Append(action.Length).AppendLine("`.");
                sb.AppendLine();
            }

            sb.AppendLine("Input:");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(Clip(scenario?.Text ?? "(scenario not in the current corpus)"));
            sb.AppendLine("```");
            sb.AppendLine();

            sb.AppendLine(cell.SanitizerAccepted
                ? "Output, as the sanitizer accepted it:"
                : $"Output, REJECTED by the shipping sanitizer ({cell.SanitizerReason}), shown raw:");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(Clip(cell.SanitizerAccepted ? cell.SanitizedText : cell.RawResponse));
            sb.AppendLine("```");
            sb.AppendLine();

            var failures = cell.Checks.Where(c => c.Status == CheckStatus.Fail).ToList();
            if (failures.Count > 0)
            {
                sb.AppendLine("Deterministic verdicts:");
                sb.AppendLine();
                foreach (var failure in failures)
                {
                    var tag = failure.Polarity == CheckPolarity.Negative ? "broke" : "missed";
                    sb.Append("- ").Append(tag).Append(" `").Append(failure.Check).Append("`: ")
                      .AppendLine(failure.Reason);
                }

                sb.AppendLine();
            }

            if (judge?.Verdict is { } verdict)
            {
                sb.Append("Judge verdict (`").Append(judge.JudgeDeployment).Append("`), structure ")
                  .Append(verdict.StructureVerdict).Append(", overall ").Append(verdict.Quality.Overall)
                  .AppendLine(":");
                sb.AppendLine();
                sb.Append("> ").AppendLine(Single(verdict.Quality.Verdict));
                sb.AppendLine();

                foreach (var miss in verdict.GroundedMisses)
                {
                    sb.Append("- MISSED ").Append(miss.Kind).Append(" (").Append(miss.Severity.ToString().ToLowerInvariant())
                      .Append(") on \"").Append(Single(miss.InputSpan)).Append("\": ")
                      .AppendLine(Single(miss.Explanation));
                }

                foreach (var over in verdict.GroundedUnwarranted)
                {
                    sb.Append("- UNWARRANTED ").Append(over.Kind).Append(" (").Append(over.Severity.ToString().ToLowerInvariant())
                      .Append(") on \"").Append(Single(over.OutputSpan)).Append("\": ")
                      .AppendLine(Single(over.Explanation));
                }

                foreach (var issue in verdict.GroundedFidelity)
                {
                    sb.Append("- FIDELITY ").Append(issue.Type).Append(" (").Append(issue.Severity.ToString().ToLowerInvariant())
                      .Append("): \"").Append(Single(issue.InputSpan)).Append("\" became \"")
                      .Append(Single(issue.OutputSpan)).Append("\": ").AppendLine(Single(issue.Explanation));
                }

                sb.AppendLine();
            }
        }
    }

    private static void WriteRegressions(
        StringBuilder sb,
        List<CellResult> graded,
        List<string> actions,
        Dictionary<string, JudgeCell> judgeByKey)
    {
        sb.AppendLine("## Regressions worth acting on");
        sb.AppendLine();
        sb.AppendLine(
            $"Every action and check whose pass rate is below {RegressionThreshold * 100:F0} percent, " +
            $"and every action whose missed-opportunity rate is above {MissedOpportunityThreshold * 100:F0} " +
            "percent. Nothing else is listed here, so an empty section means the instruction sets are " +
            "holding.");
        sb.AppendLine();

        var rows = new List<(string Action, string Check, int Pass, int Fail, double Rate)>();

        foreach (var action in actions)
        {
            var cells = graded.Where(r => r.ActionId == action).ToList();
            foreach (var name in CheckSuite.Names)
            {
                var verdicts = cells.SelectMany(c => c.Checks).Where(c => c.Check == name).ToList();
                var pass = verdicts.Count(v => v.Status == CheckStatus.Pass);
                var fail = verdicts.Count(v => v.Status == CheckStatus.Fail);
                if (pass + fail == 0)
                {
                    continue;
                }

                var rate = pass / (double)(pass + fail);
                if (rate < RegressionThreshold)
                {
                    rows.Add((action, name, pass, fail, rate));
                }
            }
        }

        if (rows.Count == 0)
        {
            sb.AppendLine("No action and check pair is below the threshold.");
        }
        else
        {
            sb.AppendLine("| action | check | pass | fail | pass rate | most common reason |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var row in rows.OrderBy(r => r.Rate))
            {
                var reason = graded
                    .Where(c => c.ActionId == row.Action)
                    .SelectMany(c => c.Checks)
                    .Where(c => c.Check == row.Check && c.Status == CheckStatus.Fail)
                    .GroupBy(c => FirstClause(c.Reason), StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} ({g.Count()})")
                    .FirstOrDefault() ?? "-";

                sb.AppendLine(
                    $"| `{row.Action}` | `{row.Check}` | {row.Pass} | {row.Fail} | {row.Rate * 100:F1}% | {reason} |");
            }
        }

        sb.AppendLine();

        if (judgeByKey.Count == 0)
        {
            return;
        }

        var offenders = new List<(string Action, int WithMiss, int Judged, double Rate)>();
        foreach (var action in actions)
        {
            var verdicts = VerdictsFor(graded, judgeByKey, r => r.ActionId == action);
            if (verdicts.Count == 0)
            {
                continue;
            }

            var withMiss = verdicts.Count(v => v.GroundedMisses.Count > 0);
            var rate = withMiss / (double)verdicts.Count;
            if (rate > MissedOpportunityThreshold)
            {
                offenders.Add((action, withMiss, verdicts.Count, rate));
            }
        }

        if (offenders.Count == 0)
        {
            sb.AppendLine("No action is above the missed-opportunity threshold.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| action | cells with a miss | judged | rate | what it keeps missing |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var offender in offenders.OrderByDescending(o => o.Rate))
        {
            var kinds = VerdictsFor(graded, judgeByKey, r => r.ActionId == offender.Action)
                .SelectMany(v => v.GroundedMisses)
                .GroupBy(m => m.Kind, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key} ({g.Count()})");

            sb.AppendLine(
                $"| `{offender.Action}` | {offender.WithMiss} | {offender.Judged} | {offender.Rate * 100:F1}% | " +
                $"{string.Join(", ", kinds)} |");
        }

        sb.AppendLine();
    }

    private static void WriteCalibration(
        StringBuilder sb,
        List<CellResult> graded,
        Dictionary<string, JudgeCell> judgeByKey,
        Dictionary<string, Scenario> scenarioById)
    {
        sb.AppendLine("## Judge calibration");
        sb.AppendLine();

        if (judgeByKey.Count == 0)
        {
            sb.AppendLine("The judge has not run. Nothing to calibrate.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(
            "Whether the judge is finding real misses or producing plausible ones. Nothing in this " +
            "section is about the model under test; it is about whether the numbers above can be " +
            "believed.");
        sb.AppendLine();

        var verdicts = judgeByKey.Values.Select(j => j.Verdict!).ToList();
        var findings = verdicts.Sum(v => v.FindingCount);
        var ungrounded = verdicts.Sum(v => v.UngroundedCount);

        sb.AppendLine("### Are the quotes real");
        sb.AppendLine();
        sb.AppendLine(
            "Every finding has to quote the span it is about. A quote that cannot be found in the " +
            "text it was attributed to is discarded before it reaches any number in this report, so " +
            "this rate is the judge's own error rate rather than the model's.");
        sb.AppendLine();
        sb.AppendLine($"- {findings} finding(s) in total.");
        sb.AppendLine($"- {ungrounded} ({Rate(ungrounded, findings)}) quoted a span that is not in the text, and were discarded.");

        // Exact against loose is worth separating. A loose match is a quote that lost punctuation or
        // a markup character on its way out of the model, which is fine; a wall of loose matches
        // means the judge is reconstructing rather than copying.
        var exact = 0;
        var loose = 0;
        foreach (var (key, judge) in judgeByKey)
        {
            var cell = graded.FirstOrDefault(c => c.Key == key);
            var scenario = cell is null ? null : scenarioById.GetValueOrDefault(cell.ScenarioId);
            if (cell is null || scenario is null)
            {
                continue;
            }

            foreach (var miss in judge.Verdict!.GroundedMisses)
            {
                if (Grounding.Locate(miss.InputSpan, scenario.Text) == GroundingMode.Exact)
                {
                    exact++;
                }
                else
                {
                    loose++;
                }
            }
        }

        sb.AppendLine(
            $"- Of the missed-opportunity quotes that were found, {exact} matched the input exactly and " +
            $"{loose} matched only word for word after punctuation and markup were normalised.");
        sb.AppendLine();

        sb.AppendLine("### Does it agree with the rules");
        sb.AppendLine();
        sb.AppendLine(
            "The deterministic positive checks and the judge are looking for the same thing on the " +
            "cells where both apply. Agreement is evidence the judge is reading the content; the " +
            "judge-only column is where its added value lives, and it is also where a hallucination " +
            "would hide, so it is the column to sample by hand.");
        sb.AppendLine();
        sb.AppendLine("| deterministic check | judge kind | both | judge only | rules only | neither |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var (check, kinds) in CheckToKind)
        {
            var both = 0;
            var judgeOnly = 0;
            var rulesOnly = 0;
            var neither = 0;

            foreach (var cell in graded)
            {
                var verdict = judgeByKey.GetValueOrDefault(cell.Key)?.Verdict;
                if (verdict is null)
                {
                    continue;
                }

                var deterministic = cell.Checks.FirstOrDefault(c => c.Check == check);
                if (deterministic is null || deterministic.Status == CheckStatus.NotApplicable)
                {
                    continue;
                }

                var rulesSaidMissed = deterministic.Status == CheckStatus.Fail;
                var judgeSaidMissed = verdict.GroundedMisses.Any(m => kinds.Contains(m.Kind, StringComparer.Ordinal));

                if (rulesSaidMissed && judgeSaidMissed)
                {
                    both++;
                }
                else if (judgeSaidMissed)
                {
                    judgeOnly++;
                }
                else if (rulesSaidMissed)
                {
                    rulesOnly++;
                }
                else
                {
                    neither++;
                }
            }

            sb.AppendLine(
                $"| `{check}` | {string.Join(", ", kinds)} | {both} | {judgeOnly} | {rulesOnly} | {neither} |");
        }

        sb.AppendLine();

        sb.AppendLine("### Findings the corpus contradicts");
        sb.AppendLine();
        sb.AppendLine(
            "A missed-opportunity finding on a scenario whose own metadata says the correct answer is " +
            "no structure at all. These are prima facie judge errors: the corpus author asserted the " +
            "opposite when the case was written, and the judge was shown that assertion.");
        sb.AppendLine();

        var contradictions = new List<(string Key, string Kind, string Span, string Why)>();

        foreach (var (key, judge) in judgeByKey)
        {
            var cell = graded.FirstOrDefault(c => c.Key == key);
            var scenario = cell is null ? null : scenarioById.GetValueOrDefault(cell.ScenarioId);
            if (scenario is null)
            {
                continue;
            }

            foreach (var miss in judge.Verdict!.GroundedMisses)
            {
                var why = miss.Kind switch
                {
                    "bold" when scenario.ExpectNoBold => "the scenario states there is no emphasis trigger in the text",
                    "bulleted-list" or "numbered-list" when scenario.ExpectNoList =>
                        "the scenario states the text is one connected argument",
                    "table" when !scenario.ShouldTable => "the scenario states there are no repeated same-field records",
                    "heading" when !scenario.ShouldHeading => "the scenario states the text is too short for a heading",
                    _ => string.Empty,
                };

                if (why.Length > 0)
                {
                    contradictions.Add((key, miss.Kind, Single(miss.InputSpan), why));
                }
            }
        }

        var groundedMissTotal = verdicts.Sum(v => v.GroundedMisses.Count);
        sb.AppendLine(
            $"{contradictions.Count} of {groundedMissTotal} grounded missed-opportunity finding(s) " +
            $"({Rate(contradictions.Count, groundedMissTotal)}) contradict the scenario they were made on.");
        sb.AppendLine();

        if (contradictions.Count > 0)
        {
            sb.AppendLine("| cell | kind | quoted span | why it is doubtful |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (key, kind, span, why) in contradictions.Take(25))
            {
                sb.AppendLine($"| `{key}` | {kind} | {Clip(span, 80)} | {why} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("### What the judge said about its own grounding");
        sb.AppendLine();
        sb.AppendLine(
            "Self-reported, and worth reading against the table above. A judge reporting `confirms` " +
            "on findings the corpus contradicts is not reading the ground truth it was given; a wall " +
            "of `silent` is a judge working from its own taste rather than from the case.");
        sb.AppendLine();
        sb.AppendLine("| stance | findings | share |");
        sb.AppendLine("|---|---|---|");

        var allMisses = verdicts.SelectMany(v => v.GroundedMisses).ToList();
        foreach (var stance in (GroundTruthStance[])[GroundTruthStance.Confirms, GroundTruthStance.Silent, GroundTruthStance.Contradicts, GroundTruthStance.Unknown])
        {
            var count = allMisses.Count(m => m.GroundTruth == stance);
            sb.AppendLine($"| {stance.ToString().ToLowerInvariant()} | {count} | {Rate(count, allMisses.Count)} |");
        }

        sb.AppendLine();

        var errors = JudgeErrorCount(judgeByKey);
        if (errors > 0)
        {
            sb.AppendLine($"{errors} judged cell(s) carried a verdict that could not be used.");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Which judge finding kinds correspond to which deterministic positive check, for the agreement
    /// table. Deliberately not exhaustive: only the four constructs both halves can see.
    /// </summary>
    private static readonly (string Check, string[] Kinds)[] CheckToKind =
    [
        ("should-bold", ["bold"]),
        ("should-list", ["bulleted-list", "numbered-list"]),
        ("should-table", ["table", "definition-list"]),
        ("should-code", ["code"]),
    ];

    private static int JudgeErrorCount(Dictionary<string, JudgeCell> judgeByKey) =>
        judgeByKey.Values.Count(j => j.Verdict is null);

    private static List<JudgeVerdict> VerdictsFor(
        List<CellResult> graded,
        Dictionary<string, JudgeCell> judgeByKey,
        Func<CellResult, bool> predicate) =>
        [
            .. graded
                .Where(predicate)
                .Select(c => judgeByKey.GetValueOrDefault(c.Key)?.Verdict)
                .Where(v => v is not null)
                .Select(v => v!),
        ];

    /// <summary>
    /// One number per cell that both halves feed into, so the worst cells are the worst overall
    /// rather than the worst on whichever half happened to fire.
    /// </summary>
    private static int SeverityScore(CellResult cell, JudgeVerdict? verdict)
    {
        var score = (cell.NegativeFailures * 3) + (cell.PositiveFailures * 2);

        if (!cell.SanitizerAccepted)
        {
            score += 4;
        }

        if (verdict is null)
        {
            return score;
        }

        foreach (var finding in verdict.GroundedMisses.Select(m => m.Severity)
                     .Concat(verdict.GroundedUnwarranted.Select(u => u.Severity))
                     .Concat(verdict.GroundedFidelity.Select(f => f.Severity)))
        {
            score += finding switch
            {
                Severity.Major => 3,
                Severity.Moderate => 2,
                Severity.Minor => 1,
                _ => 0,
            };
        }

        score += (100 - verdict.Quality.Overall) / 25;
        return score;
    }

    private static bool IsNegative(List<CellResult> graded, string check) =>
        graded.SelectMany(r => r.Checks).FirstOrDefault(c => c.Check == check)?.Polarity != CheckPolarity.Positive;

    private static string Abbreviate(string check) => check switch
    {
        "preservation" => "presv",
        "house-style" => "house",
        "restraint-bold" => "r-bold",
        "restraint-list" => "r-list",
        "heading-blacklist" => "h-black",
        "markdown-contract" => "md",
        "html-contract" => "html",
        "json-contract" => "json",
        "teams-contract" => "teams",
        "length-band" => "len",
        "minimal-diff" => "diff",
        "should-bold" => "s-bold",
        "should-list" => "s-list",
        "should-table" => "s-tbl",
        "should-code" => "s-code",
        _ => check,
    };

    /// <summary>
    /// Catalog position, so the report reads in palette order rather than alphabetically. An id the
    /// catalog no longer carries sorts to the end rather than throwing: an old results file is still
    /// worth reporting on.
    /// </summary>
    private static int CatalogOrder(string actionId)
    {
        for (var i = 0; i < TextActionCatalog.All.Count; i++)
        {
            if (string.Equals(TextActionCatalog.All[i].Id, actionId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static int Percentile(List<int> sorted, double fraction) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Round((sorted.Count - 1) * fraction), 0, sorted.Count - 1)];

    private static string Bucket(List<int> scores, int low, int high)
    {
        var count = scores.Count(s => s >= low && s <= high);
        return $"{count} ({Rate(count, scores.Count)})";
    }

    private static string Rate(int part, int whole) =>
        whole == 0 ? "n/a" : $"{part * 100.0 / whole:F1}%";

    /// <summary>The first clause of a checker reason, for grouping failures by cause.</summary>
    private static string FirstClause(string reason)
    {
        var cut = reason.IndexOfAny([':', ';']);
        var clause = cut > 0 ? reason[..cut] : reason;
        return Clip(clause, 60);
    }

    /// <summary>Collapses to one line and escapes the pipe, so a quote cannot break a table row.</summary>
    private static string Single(string? value) =>
        TextTools.Clip(value ?? string.Empty, 300).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Clip(string value, int limit = QuoteLimit) =>
        value.Length <= limit ? value : value[..limit] + "\n... (clipped)";
}
