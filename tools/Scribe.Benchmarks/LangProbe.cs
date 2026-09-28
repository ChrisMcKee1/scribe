using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Scribe.Benchmarks;

/// <summary>
/// Facts about the language benchmarks that BenchmarkDotNet's tables do not show.
/// <list type="bullet">
/// <item><c>--lang-probe matcher-dimensions</c>: what every case of the matcher benchmarks processes, from their own setup:
/// the rows it was given, the rules they compiled to and the two texts' lengths (review finding LANG-IR-03).</item>
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
        switch (command)
        {
            case "matcher-dimensions":
                MatcherDimensions();
                return 0;
            default:
                Console.Error.WriteLine("Usage: --lang-probe matcher-dimensions");
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
}
