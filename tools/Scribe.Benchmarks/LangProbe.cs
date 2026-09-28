using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Scribe.Core.Libraries;

namespace Scribe.Benchmarks;

/// <summary>
/// Facts about the language benchmarks that BenchmarkDotNet's tables do not show.
/// <list type="bullet">
/// <item><c>--lang-probe matcher-dimensions</c>: what every case of the matcher benchmarks processes, from their own setup:
/// the rows it was given, the rules they compiled to and the two texts' lengths (review finding LANG-IR-03).</item>
/// <item><c>--lang-probe adoption-first-call eager|lazy</c>: in this process, which should be fresh, the first adoption plan
/// of <see cref="LangAdoptionPlanBenchmarks"/> and the three after it, each with its elapsed time and the bytes the calling
/// thread allocated inside it (review finding LANG-IR-02). BenchmarkDotNet cannot give that first call's allocation: its
/// memory diagnoser measures a separate run after the timed one, so even under ColdStart its Allocated column is a warm
/// call's. Run it in several fresh processes per arm, interleaved, inside the bench lane; it runs at the priority it was
/// started with.</item>
/// </list>
/// </summary>
internal static class LangProbe
{
    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--lang-probe", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var at = Array.FindIndex(args, arg => string.Equals(arg, "--lang-probe", StringComparison.OrdinalIgnoreCase));
        var command = at + 1 < args.Length ? args[at + 1] : string.Empty;
        var arm = at + 2 < args.Length ? args[at + 2] : string.Empty;
        switch (command)
        {
            case "matcher-dimensions":
                MatcherDimensions();
                return 0;
            case "adoption-first-call" when arm is "eager" or "lazy":
                AdoptionFirstCall(eager: arm == "eager");
                return 0;
            default:
                Console.Error.WriteLine("Usage: --lang-probe matcher-dimensions | --lang-probe adoption-first-call eager|lazy");
                return 2;
        }
    }

    // Each setup prints its own dimensions line; the Old arm is enough, since the arms share their inputs.
    private static void MatcherDimensions()
    {
        foreach (var rows in LangMatcherPassBenchmarks.RowCounts)
        {
            foreach (var limit in LangMatcherPassBenchmarks.CharacterLimits)
            {
                new LangMatcherPassBenchmarks { Arm = LangMatcherPassBenchmarks.Path.Old, InputRows = rows, MaxCharacters = limit }.Setup();
            }
        }

        var limits = typeof(LangMatcherFlagOffBenchmarks)
            .GetProperty(nameof(LangMatcherFlagOffBenchmarks.MaxCharacters))!
            .GetCustomAttribute<ParamsAttribute>()!
            .Values;
        foreach (var limit in limits)
        {
            new LangMatcherFlagOffBenchmarks { MaxCharacters = (int)limit! }.Setup();
        }
    }

    private static void AdoptionFirstCall(bool eager)
    {
        var benchmark = new LangAdoptionPlanBenchmarks();
        benchmark.Setup();
        Func<LibraryAdoption?> plan = eager ? benchmark.EagerPlan : benchmark.LazyPlan;

        // Both clocks are read once first, so neither's first call lands inside a measured one.
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = Stopwatch.GetTimestamp();
        var calls = new List<string>(4);
        for (var call = 1; call <= 4; call++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            _ = plan();
            var end = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            calls.Add($"call {call}: {(end - start) * 1_000.0 / Stopwatch.Frequency:F3} ms, {bytes} B");
        }

        Console.WriteLine($"adoption-first-call {(eager ? "eager" : "lazy")}: {string.Join("; ", calls)}");
    }
}
