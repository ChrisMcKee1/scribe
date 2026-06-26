using Scribe.Core.Cleanup;

namespace Scribe.Evals;

/// <summary>Parsed command-line options for the eval runner.</summary>
internal sealed class CliOptions
{
    public CleanupProvider Provider { get; private set; } = CleanupProvider.FoundryLocal;
    public IReadOnlyList<string> Models { get; private set; } = [CleanupModelCatalog.DefaultAlias];
    public string? AzureEndpoint { get; private set; }
    public string? AzureTenantId { get; private set; }
    public TimeSpan ReadyTimeout { get; private set; } = TimeSpan.FromSeconds(240);
    public bool ListScenarios { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool Verbose { get; private set; }

    // Benchmark mode (leaderboard across every available model).
    public bool Benchmark { get; private set; }
    public string? BenchOut { get; private set; }
    public int BenchRuns { get; private set; } = 3;
    public bool IncludeCloud { get; private set; } = true;
    public bool IncludeLocal { get; private set; } = true;
    public IReadOnlyList<string>? CloudOnly { get; private set; }
    public IReadOnlyList<string>? LocalOnly { get; private set; }
    public int MaxCloud { get; private set; }
    public int MaxLocal { get; private set; }
    public string? JudgeEndpoint { get; private set; }
    public string? JudgeModel { get; private set; }
    public string? JudgeTenantId { get; private set; }
    public bool NoJudge { get; private set; }
    public bool NoWav { get; private set; }
    public bool Force { get; private set; }
    public int LocalLoadTimeout { get; private set; } = 1800;
    public int CloudReadyTimeout { get; private set; } = 120;
    public int CleanTimeout { get; private set; } = 180;

    /// <summary>Builds the benchmark configuration from the parsed flags.</summary>
    public Benchmark.BenchmarkConfig ToBenchmarkConfig()
    {
        var outDir = BenchOut ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScribeData", "bench");

        return new Benchmark.BenchmarkConfig
        {
            OutDir = outDir,
            Runs = BenchRuns,
            IncludeCloud = IncludeCloud,
            IncludeLocal = IncludeLocal,
            CloudOnly = CloudOnly,
            LocalOnly = LocalOnly,
            MaxCloud = MaxCloud,
            MaxLocal = MaxLocal,
            TenantId = AzureTenantId,
            JudgeEndpoint = string.IsNullOrWhiteSpace(JudgeEndpoint)
                ? "https://mtech-project-resource.cognitiveservices.azure.com/"
                : JudgeEndpoint!,
            JudgeModel = string.IsNullOrWhiteSpace(JudgeModel) ? "gpt-4.1" : JudgeModel!,
            JudgeTenantId = JudgeTenantId ?? AzureTenantId,
            UseJudge = !NoJudge,
            Synthesize = !NoWav,
            Force = Force,
            CloudReadyTimeoutSeconds = CloudReadyTimeout,
            LocalReadyTimeoutSeconds = LocalLoadTimeout,
            CleanTimeoutSeconds = CleanTimeout,
        };
    }

    /// <summary>
    /// Builds the cleanup configuration for a single eval run: a model and a writing-style prompt on
    /// top of the selected provider. For Azure, the model name is the deployment; the Foundry alias
    /// slot keeps its default placeholder (unused by the Azure path).
    /// </summary>
    public CleanupOptions BuildOptions(string model, string writingStyle) => Provider switch
    {
        CleanupProvider.AzureFoundry => new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.AzureFoundry,
            FoundryModelAlias: CleanupModelCatalog.DefaultAlias,
            AzureEndpoint: AzureEndpoint,
            AzureDeployment: model,
            AzureTenantId: AzureTenantId,
            WritingStyle: writingStyle),
        _ => new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.FoundryLocal,
            FoundryModelAlias: model,
            AzureEndpoint: null,
            AzureDeployment: null,
            WritingStyle: writingStyle),
    };

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var models = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : string.Empty;

            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help":
                    o.ShowHelp = true;
                    break;
                case "--list":
                    o.ListScenarios = true;
                    break;
                case "-v" or "--verbose":
                    o.Verbose = true;
                    break;
                case "--provider":
                    o.Provider = Next().ToLowerInvariant() is "azure" or "azurefoundry" or "cloud"
                        ? CleanupProvider.AzureFoundry
                        : CleanupProvider.FoundryLocal;
                    break;
                case "--model":
                    var m = Next();
                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m.Trim());
                    break;
                case "--models":
                    models.AddRange(Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--endpoint":
                    o.AzureEndpoint = Next();
                    break;
                case "--tenant":
                    o.AzureTenantId = Next();
                    break;
                case "--ready-timeout":
                    if (int.TryParse(Next(), out var secs) && secs > 0)
                    {
                        o.ReadyTimeout = TimeSpan.FromSeconds(secs);
                    }
                    break;
                case "--benchmark" or "--bench":
                    o.Benchmark = true;
                    break;
                case "--out":
                    o.BenchOut = Next();
                    break;
                case "--runs":
                    if (int.TryParse(Next(), out var runs) && runs > 0)
                    {
                        o.BenchRuns = runs;
                    }
                    break;
                case "--no-cloud":
                    o.IncludeCloud = false;
                    break;
                case "--no-local":
                    o.IncludeLocal = false;
                    break;
                case "--cloud-models":
                    o.CloudOnly = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--local-models":
                    o.LocalOnly = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--max-cloud":
                    if (int.TryParse(Next(), out var mc) && mc > 0)
                    {
                        o.MaxCloud = mc;
                    }
                    break;
                case "--max-local":
                    if (int.TryParse(Next(), out var ml) && ml > 0)
                    {
                        o.MaxLocal = ml;
                    }
                    break;
                case "--judge-endpoint":
                    o.JudgeEndpoint = Next();
                    break;
                case "--judge-model":
                    o.JudgeModel = Next();
                    break;
                case "--judge-tenant":
                    o.JudgeTenantId = Next();
                    break;
                case "--no-judge":
                    o.NoJudge = true;
                    break;
                case "--no-wav":
                    o.NoWav = true;
                    break;
                case "--force":
                    o.Force = true;
                    break;
                case "--local-load-timeout":
                    if (int.TryParse(Next(), out var llt) && llt > 0)
                    {
                        o.LocalLoadTimeout = llt;
                    }
                    break;
                case "--clean-timeout":
                    if (int.TryParse(Next(), out var clt) && clt > 0)
                    {
                        o.CleanTimeout = clt;
                    }
                    break;
            }
        }

        if (models.Count > 0)
        {
            o.Models = models;
        }

        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            Scribe.Evals — offline style/format eval harness for Scribe AI cleanup.

            Usage:
              dotnet run --project tools/Scribe.Evals -- [options]

            Options:
              --provider <foundrylocal|azure>   Cleanup backend (default: foundrylocal).
              --model <name>                    A model to test (Foundry alias, or Azure deployment).
                                                Repeatable.
              --models <a,b,c>                  Comma-separated models to compare head-to-head.
              --endpoint <url>                  Azure/Microsoft Foundry endpoint (azure provider).
              --tenant <id>                     Optional Azure tenant id override (azure provider).
              --ready-timeout <seconds>         Max wait for a model to load (default: 240).
              --list                            List the eval scenarios and exit.
              -v, --verbose                     Print service init/cleanup diagnostics to stderr.
              -h, --help                        Show this help.

            Benchmark mode (speed + quality leaderboard across every available model):
              --benchmark                       Run the full model leaderboard instead of the eval suite.
              --out <dir>                        Output dir for results.json + leaderboard.md
                                                (default: %LOCALAPPDATA%\ScribeData\bench).
              --runs <n>                        Timed runs per model (median reported, default: 3).
              --no-cloud / --no-local            Skip a whole group.
              --cloud-models <a,b> / --local-models <a,b>
                                                Restrict to a subset (substring match for cloud).
              --max-cloud <n> / --max-local <n>  Cap the number of models per group.
              --judge-endpoint <url>            Azure endpoint for the quality judge.
              --judge-model <name>              Judge deployment (default: gpt-4.1).
              --judge-tenant <id>               Tenant override for the judge.
              --no-judge                        Latency only (skip quality grading).
              --no-wav                          Use the authored transcript (skip TTS+ASR).
              --force                           Re-run models already present in results.json.
              --local-load-timeout <seconds>    Max wait for a local model to download+load (default: 1800).
              --clean-timeout <seconds>         Per-call cleanup timeout override (default: 180).

            Examples:
              dotnet run --project tools/Scribe.Evals
              dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini
              dotnet run --project tools/Scribe.Evals -- --provider azure --endpoint https://x.openai.azure.com/ --model gpt-5.4-mini

            Exit code is 0 when every scenario follows its prompt, otherwise the number of failures.
            """);
    }
}
