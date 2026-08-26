using System.Text;
using System.Text.Json;
using Scribe.StyleEval.Checks;
using Scribe.StyleEval.Corpus;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Writes every cell as a readable before-and-after pair, for human or model review.
/// </summary>
/// <remarks>
/// <para>
/// The results file is built for machines: one dense JSON object per line, the input reachable only
/// by joining back to the corpus on scenario id. That is the wrong shape for the question "is this
/// rewrite actually any good", which needs the original and the result side by side with the verdicts
/// that were reached about them.
/// </para>
/// <para>
/// Two outputs, because two different readers need it. The JSONL carries everything and is what a
/// grading agent consumes. The Markdown is for a person to scroll, grouped by action so the character
/// of one style can be read in one pass rather than reconstructed from scattered cells.
/// </para>
/// </remarks>
internal static class ExportPairs
{
    /// <summary>
    /// Which cells to export. Grading effort is finite, and a cell that passed every check teaches
    /// far less than one that failed.
    /// </summary>
    public enum Selection
    {
        /// <summary>Every cell.</summary>
        All,

        /// <summary>Only cells with at least one failing check.</summary>
        FailuresOnly,

        /// <summary>
        /// Every failure, plus a deterministic sample of clean cells. The clean sample is the only
        /// way to find a FALSE PASS: an answer that satisfied every mechanical check and is still a
        /// poor rewrite. Nothing else in the suite looks for those.
        /// </summary>
        FailuresAndControl,
    }

    public static int Run(
        string resultsPath,
        string corpusDirectory,
        string outputStem,
        Selection selection,
        int controlEvery,
        TextWriter log)
    {
        var scenarios = CorpusLoader.Load(corpusDirectory, out _).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var cells = ResultStore.ReadAll(resultsPath)
            .Where(c => string.IsNullOrEmpty(c.Error))
            .OrderBy(c => c.ActionId, StringComparer.Ordinal)
            .ThenBy(c => c.ScenarioId, StringComparer.Ordinal)
            .ToList();

        var selected = new List<CellResult>();
        var cleanSeen = 0;

        foreach (var cell in cells)
        {
            var failed = cell.Failures > 0;

            switch (selection)
            {
                case Selection.All:
                    selected.Add(cell);
                    break;
                case Selection.FailuresOnly when failed:
                    selected.Add(cell);
                    break;
                case Selection.FailuresAndControl:
                    if (failed)
                    {
                        selected.Add(cell);
                    }
                    else if (controlEvery > 0 && cleanSeen++ % controlEvery == 0)
                    {
                        // Deterministic rather than random so a rerun grades the same control cells
                        // and two grading passes stay comparable.
                        selected.Add(cell);
                    }

                    break;
            }
        }

        var jsonlPath = outputStem + ".jsonl";
        var markdownPath = outputStem + ".md";

        WriteJsonl(selected, scenarios, jsonlPath);
        WriteMarkdown(selected, scenarios, markdownPath);

        log.WriteLine(
            $"Exported {selected.Count} of {cells.Count} cell(s) ({selection}).");
        log.WriteLine($"  {jsonlPath}");
        log.WriteLine($"  {markdownPath}");
        return 0;
    }

    private static void WriteJsonl(
        List<CellResult> cells, Dictionary<string, Scenario> scenarios, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        foreach (var cell in cells)
        {
            scenarios.TryGetValue(cell.ScenarioId, out var scenario);

            var row = new
            {
                scenarioId = cell.ScenarioId,
                category = cell.Category,
                actionId = cell.ActionId,
                before = scenario?.Text ?? string.Empty,
                after = cell.SanitizedText,
                expectations = new
                {
                    shouldBold = scenario?.ShouldBold ?? [],
                    shouldList = scenario?.ShouldList ?? false,
                    shouldTable = scenario?.ShouldTable ?? false,
                    shouldCode = scenario?.ShouldCode ?? [],
                    expectNoBold = scenario?.ExpectNoBold ?? false,
                    expectNoList = scenario?.ExpectNoList ?? false,
                    protectedTokens = scenario?.ProtectedTokens ?? [],
                },
                verdicts = cell.Checks
                    .Where(c => c.Status != CheckStatus.NotApplicable)
                    .Select(c => new { check = c.Check, status = c.Status.ToString(), reason = c.Reason }),
                failureCount = cell.Failures,
            };

            writer.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    private static void WriteMarkdown(
        List<CellResult> cells, Dictionary<string, Scenario> scenarios, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        writer.WriteLine("# Rewrite pairs");
        writer.WriteLine();
        writer.WriteLine($"{cells.Count} cell(s), grouped by action.");
        writer.WriteLine();

        string? currentAction = null;

        foreach (var cell in cells)
        {
            if (cell.ActionId != currentAction)
            {
                currentAction = cell.ActionId;
                writer.WriteLine();
                writer.WriteLine($"## {currentAction}");
            }

            scenarios.TryGetValue(cell.ScenarioId, out var scenario);
            var failing = cell.Checks.Where(c => c.Status == CheckStatus.Fail).ToList();

            writer.WriteLine();
            writer.WriteLine($"### {cell.ScenarioId}  ({cell.Category})");
            if (failing.Count == 0)
            {
                writer.WriteLine();
                writer.WriteLine("_All checks passed. Included as a control: read it for quality, not compliance._");
            }
            else
            {
                writer.WriteLine();
                foreach (var f in failing)
                {
                    writer.WriteLine($"- **{f.Check}**: {f.Reason}");
                }
            }

            writer.WriteLine();
            writer.WriteLine("**Before**");
            writer.WriteLine();
            writer.WriteLine(Fence(scenario?.Text ?? string.Empty));
            writer.WriteLine();
            writer.WriteLine("**After**");
            writer.WriteLine();
            writer.WriteLine(Fence(cell.SanitizedText));
        }
    }

    /// <summary>
    /// Fences a block with enough backticks to survive content that contains its own fences, which
    /// Markdown and code answers routinely do.
    /// </summary>
    private static string Fence(string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + "text\n" + content + "\n" + fence;
    }
}
