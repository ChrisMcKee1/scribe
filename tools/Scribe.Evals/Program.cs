using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;

namespace Scribe.Evals;

/// <summary>
/// Offline style/format eval harness for Scribe's AI cleanup. It drives the real
/// <see cref="ITextCleanupService"/> (the exact code the app runs) across a suite of writing-style
/// prompts applied to one shared transcript, then scores each output with a deterministic
/// <see cref="StyleAdherenceEvaluator"/> (a Microsoft.Extensions.AI.Evaluation <c>IEvaluator</c>).
/// It proves three things the user asked for: (1) editing the prompt actually changes the output
/// ("pirate" vs "Old English"), (2) format/translation instructions take effect, and (3) different
/// models can be compared head-to-head. No judge model and no network are required.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var opts = CliOptions.Parse(args);
        if (opts.ShowHelp)
        {
            CliOptions.PrintUsage();
            return 0;
        }

        if (opts.ListScenarios)
        {
            Console.WriteLine("Eval scenarios:");
            foreach (var s in EvalScenarios.All)
            {
                Console.WriteLine($"  - {s.Name}: needs >= {s.MinMarkersToPass} markers; changed-from-raw={s.RequireChanged}");
            }
            return 0;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        if (opts.Benchmark)
        {
            var cfg = opts.ToBenchmarkConfig();
            var runner = new Benchmark.BenchmarkRunner(cfg, new VerboseConsoleLogger<Benchmark.BenchmarkRunner>());
            return await runner.RunAsync(cancel.Token);
        }

        Console.WriteLine($"Scribe evals — provider={opts.Provider}, models=[{string.Join(", ", opts.Models)}]");
        Console.WriteLine($"Raw transcript: \"{EvalScenarios.RawTranscript}\"");
        Console.WriteLine();

        await using var svc = new TextCleanupService(opts.Verbose
            ? new VerboseConsoleLogger<TextCleanupService>()
            : NullLogger<TextCleanupService>.Instance);
        svc.StatusChanged += () => Console.WriteLine($"   [status] {svc.Status}: {svc.StatusDetail}");

        var totalFailures = 0;
        foreach (var model in opts.Models)
        {
            totalFailures += await RunModelAsync(svc, opts, model, cancel.Token);
        }

        Console.WriteLine();
        Console.WriteLine(totalFailures == 0
            ? "RESULT: PASS — every scenario followed its prompt."
            : $"RESULT: FAIL — {totalFailures} scenario(s) did not meet the style threshold.");

        return totalFailures == 0 ? 0 : 1;
    }

    private static async Task<int> RunModelAsync(
        TextCleanupService svc, CliOptions opts, string model, CancellationToken ct)
    {
        Console.WriteLine($"=== Model: {model} ===");

        var failures = 0;
        var rows = new List<(string Scenario, string Score, string Result, string Reason)>();

        foreach (var scenario in EvalScenarios.All)
        {
            var options = opts.BuildOptions(model, scenario.WritingStyle);
            svc.Configure(options);

            var ready = await WaitForReadyAsync(svc, opts.ReadyTimeout, ct);
            if (!ready)
            {
                failures++;
                rows.Add((scenario.Name, "-", "ERROR", $"model not ready ({svc.Status}: {svc.StatusDetail})"));
                continue;
            }

            // Style scenarios share the suite transcript; condensation scenarios carry their own
            // (the disfluency under test has to exist in the input).
            var transcript = scenario.Transcript ?? EvalScenarios.RawTranscript;
            var cleaned = (await svc.CleanAsync(transcript, ct)).Text;

            var evaluator = new StyleAdherenceEvaluator(
                scenario.MarkerPatterns, scenario.MinMarkersToPass, scenario.RequireChanged,
                scenario.CountOccurrences, transcript, scenario.ForbiddenPatterns);

            var request = new ChatMessage(ChatRole.User, transcript);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, cleaned));
            var result = await evaluator.EvaluateAsync([request], response, cancellationToken: ct);

            var metric = result.Get<NumericMetric>(StyleAdherenceEvaluator.MetricName);
            var failed = metric.Interpretation?.Failed ?? true;
            if (failed)
            {
                failures++;
            }

            rows.Add((
                scenario.Name,
                (metric.Value ?? 0).ToString("0.00"),
                failed ? "FAIL" : "PASS",
                metric.Interpretation?.Reason ?? string.Empty));

            Console.WriteLine($"   · {scenario.Name} -> {(failed ? "FAIL" : "PASS")}");
            Console.WriteLine($"     output: {Snippet(cleaned)}");
        }

        Console.WriteLine();
        PrintTable(rows);
        Console.WriteLine($"   {model}: {rows.Count - failures}/{rows.Count} passed.");
        Console.WriteLine();
        return failures;
    }

    private static async Task<bool> WaitForReadyAsync(
        ITextCleanupService svc, TimeSpan timeout, CancellationToken ct)
    {
        if (svc.Status == CleanupStatus.Ready) return true;

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
            return await tcs.Task;
        }
        finally
        {
            svc.StatusChanged -= OnChanged;
        }
    }

    private static string Snippet(string text)
    {
        var oneLine = text.ReplaceLineEndings(" ⏎ ").Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..157] + "...";
    }

    private static void PrintTable(IReadOnlyList<(string Scenario, string Score, string Result, string Reason)> rows)
    {
        const int scen = 22, score = 6, res = 6;
        Console.WriteLine($"   {"Scenario".PadRight(scen)} {"Score".PadRight(score)} {"Result".PadRight(res)} Reason");
        Console.WriteLine($"   {new string('-', scen)} {new string('-', score)} {new string('-', res)} ------");
        foreach (var r in rows)
        {
            Console.WriteLine($"   {Trunc(r.Scenario, scen).PadRight(scen)} {r.Score.PadRight(score)} {r.Result.PadRight(res)} {r.Reason}");
        }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
