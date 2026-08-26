using Scribe.Core.TextActions;
using Scribe.StyleEval.Corpus;
using Scribe.StyleEval.Runner;

namespace Scribe.StyleEval;

/// <summary>
/// Side-testing evaluation of Scribe's text-action instruction sets. NEVER PUBLISHED.
/// </summary>
/// <remarks>
/// <para>
/// The suite answers one question: do the shipping instruction sets perform at the highest level,
/// for every style? It answers it in two halves, and building only one half is the usual mistake.
/// </para>
/// <para>
/// The NEGATIVE half is deterministic and lives in <c>Checks/</c>: did the answer break a stated
/// rule, over-bold, invent a blacklisted heading, drop a URL, emit unparseable JSON, exceed a length
/// band, add a dash. Cheap, reliable, and code.
/// </para>
/// <para>
/// The POSITIVE half asks whether the answer MISSED structure the content warranted. Its
/// mechanically decidable part also lives in <c>Checks/</c> (a named phrase came back bold, three
/// peer items became a list, repeated records became a table, an identifier got code formatting).
/// Its subjective part, whether a Teams message reads like a colleague wrote it and whether an agent
/// brief is genuinely actionable, is the LLM judge's job and consumes the same results file.
/// </para>
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        StyleEvalOptions options;
        try
        {
            options = StyleEvalOptions.Parse(args, AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(StyleEvalOptions.Help);
            return 64;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(StyleEvalOptions.Help);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("Stopping. Completed cells are already on disk; rerun to resume.");
            cancellation.Cancel();
        };

        try
        {
            if (options.ListDeployments)
            {
                return await DeploymentDiscovery.ListAsync(options, cancellation.Token).ConfigureAwait(false);
            }

            if (options.ExportPairsStem is { } stem)
            {
                return ExportPairs.Run(
                    options.ResultsPath,
                    options.CorpusDirectory,
                    stem,
                    options.ExportSelection,
                    options.ExportControlEvery,
                    Console.Out);
            }

            if (options.ComparePath is { } baseline)
            {
                // Purely a read of two files already on disk. No corpus, no credential, no model
                // call, so a comparison can be re-run as often as it takes to understand a trade.
                Compare.Run(baseline, options.CompareAgainstPath ?? options.ResultsPath, Console.Out);
                return 0;
            }

            if (options.ScoreOnly)
            {
                return Rescore.Run(options.ResultsPath, options.CorpusDirectory);
            }

            var scenarios = CorpusLoader.Load(options.CorpusDirectory, out var advisories);
            var actions = SelectActions(options);

            if (actions.Count == 0)
            {
                Console.Error.WriteLine(
                    $"No action matched --actions. Known: {string.Join(", ", StyleRunner.ModelBackedActions.Select(a => a.Id))}");
                return 64;
            }

            Console.WriteLine($"Loaded {scenarios.Count} scenario(s) from {scenarios.Select(s => s.Category).Distinct().Count()} category file(s).");

            if (advisories.Count > 0)
            {
                Console.WriteLine($"{advisories.Count} corpus advisory/advisories (not fatal):");
                foreach (var advisory in advisories.Take(10))
                {
                    Console.WriteLine($"  {advisory}");
                }

                if (advisories.Count > 10)
                {
                    Console.WriteLine($"  ... and {advisories.Count - 10} more.");
                }
            }

            var runner = new StyleRunner(options, scenarios, actions);
            return await runner.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (CorpusException ex)
        {
            Console.Error.WriteLine($"Corpus error: {ex.Message}");
            return 65;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static IReadOnlyList<TextAction> SelectActions(StyleEvalOptions options)
    {
        // Only the ten model-backed actions. apply-vocabulary runs no model and has nothing to grade
        // here; it is a deterministic dictionary pass covered by the unit tests.
        var all = StyleRunner.ModelBackedActions;

        return options.Actions.Count == 0
            ? all
            : [.. all.Where(a => options.Actions.Contains(a.Id, StringComparer.OrdinalIgnoreCase))];
    }
}
