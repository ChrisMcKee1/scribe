using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;

namespace Scribe.Benchmarks;

internal static class Program
{
    private const string PriorityCaveat =
        "BenchmarkDotNet 0.15.8 raises every benchmark process to High priority " +
        "(DotNetCliExecutor calls ProcessExtensions.EnsureHighPriority right after starting it; the in-process " +
        "toolchains raise the process to High and the thread to Highest). These timings are NOT measurements " +
        "of Scribe running at Normal priority alongside other work; --soak runs at the priority it was started with. " +
        "BenchmarkDotNet would also switch the whole machine to the High performance power plan for the run " +
        "(PowerManagementApplier); this job opts out with DontEnforcePowerPlan, so it measures under the plan " +
        "shown as power= above and leaves that machine-wide setting alone.";

    private static int Main(string[] args)
    {
        var host = ExecutionEnvironment.Capture();
        Console.WriteLine($"Scribe benchmarks host: {host.Describe()}");

        if (SoakHarness.IsRequested(args))
        {
            return SoakHarness.Run(args, host);
        }

        if (!BenchmarkTarget.TryResolve(host.ProcessArchitecture, out var runtimeIdentifier, out var platform))
        {
            Console.Error.WriteLine(
                $"Scribe builds only for win-x64 and win-arm64; this host process is {host.ProcessArchitecture}.");
            return 2;
        }

        if (host.IsEmulated)
        {
            Console.WriteLine(
                $"WARNING: this host is {host.ProcessArchitecture} emulated on {host.OsArchitecture}. The benchmarks will " +
                "build and run emulated too, which is not what a native install executes.");
        }

        // DontEnforcePowerPlan: without it BenchmarkDotNet switches the whole machine to High
        // performance for the run, which neither represents a user's PC nor is this tool's to change.
        var job = Job.ShortRun
            .WithPlatform(platform)
            .WithMsBuildArguments($"/p:RuntimeIdentifier={runtimeIdentifier}")
            .DontEnforcePowerPlan();

        var dotnetHost = BenchmarkTarget.FindDotNetHost();
        var targetFramework = BenchmarkTarget.TargetFrameworkMoniker(typeof(Program).Assembly);
        if (dotnetHost is not null && targetFramework is not null)
        {
            job = job.WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings(
                targetFrameworkMoniker: targetFramework,
                runtimeFrameworkVersion: null,
                name: $".NET {Environment.Version.Major}.{Environment.Version.Minor}",
                customDotNetCliPath: dotnetHost)));
        }
        else
        {
            Console.WriteLine(
                "WARNING: could not locate the dotnet host of this runtime; BenchmarkDotNet will use the dotnet on PATH, " +
                "whose architecture may differ from this process.");
        }

        Console.WriteLine(
            $"Benchmark build: RuntimeIdentifier={runtimeIdentifier} Platform={platform} " +
            $"TargetFramework={targetFramework ?? "default"} host={(dotnetHost is null ? "PATH" : "this runtime's dotnet")}");
        Console.WriteLine(PriorityCaveat);
        Console.WriteLine();

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(job)
            .AddColumn(new ExecutionEnvironmentColumn());
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToList();

        PrintResultEnvironment(host, summaries);
        return 0;
    }

    private static void PrintResultEnvironment(ExecutionEnvironment host, IReadOnlyList<Summary> summaries)
    {
        Console.WriteLine();
        Console.WriteLine($"Scribe benchmarks host (with results): {ExecutionEnvironment.Capture().Describe()}");

        var reported = summaries
            .SelectMany(summary => summary.Reports)
            .Select(report => report.Success
                ? ExecutionEnvironmentColumn.FindMarker(report) ?? "not reported"
                : "did not run (build or execution failed; see the log above)")
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ToList();
        foreach (var group in reported)
        {
            Console.WriteLine($"  benchmark processes: {group.Key} ({group.Count()} case(s))");
        }

        if (reported.Count == 0)
        {
            Console.WriteLine("  benchmark processes: none ran");
        }

        if (host.IsEmulated)
        {
            Console.WriteLine("  WARNING: measured under emulation.");
        }

        Console.WriteLine(PriorityCaveat);
    }
}
