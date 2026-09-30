using Scribe.Core.Cleanup;
using Scribe.Core.PostProcessing;
using Microsoft.Extensions.AI;
using BenchEndpoints = Scribe.Evals.Benchmark.LocalEndpoints;
using BenchJudge = Scribe.Evals.Benchmark.BlindJudge;

namespace Scribe.Evals;

/// <summary>
/// Which scenario suite the eval run covers: the cleanup writing-style suite, the auxiliary-prompt
/// suite (usage insight + AI dictionary suggestions), or both.
/// </summary>
internal enum EvalSuite
{
    Style,
    Auxiliary,
    All,
}

/// <summary>Parsed command-line options for the eval runner.</summary>
internal sealed class CliOptions
{
    public CleanupProvider Provider { get; private set; } = CleanupProvider.FoundryLocal;
    public IReadOnlyList<string> Models { get; private set; } = [CleanupModelCatalog.DefaultAlias];
    public EvalSuite Suite { get; private set; } = EvalSuite.Style;
    public string? AzureEndpoint { get; private set; }
    public string? AzureTenantId { get; private set; }
    public string? AzureSubscriptionId { get; private set; }
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
    public IReadOnlyList<string>? CaseOnly { get; private set; }
    public int MaxCloud { get; private set; }
    public int MaxLocal { get; private set; }
    public string? JudgeEndpoint { get; private set; }
    public string? JudgeModel { get; private set; }
    public string? JudgeTenantId { get; private set; }
    public string? JudgeSubscriptionId { get; private set; }
    public bool NoJudge { get; private set; }
    public bool NoWav { get; private set; }
    public CleanupPromptStyle PromptStyle { get; private set; } = CleanupPromptStyle.Auto;
    public string? BenchWritingStyleFile { get; private set; }
    public string? BenchFrontierPromptFile { get; private set; }
    public string? BenchLocalPromptFile { get; private set; }
    public string[]? BenchGlossaryLibraries { get; private set; }
    public int? BenchGlossaryMaxTerms { get; private set; }
    public CleanupVocabularyMode BenchVocabularyMode { get; private set; } = CleanupVocabularyMode.All;
    public int BenchPaceMs { get; private set; }
    public string? BenchCasesFrom { get; private set; }
    public string OllamaEndpoint { get; private set; } = BenchEndpoints.DefaultOllama;
    public string LmStudioEndpoint { get; private set; } = BenchEndpoints.DefaultLmStudio;
    public bool KeepServerModelsLoaded { get; private set; }

    // Blind judging of retained outputs (see Benchmark.BlindJudge).
    public bool BlindJudge { get; private set; }
    public IReadOnlyList<string>? PrefetchFoundry { get; private set; }
    public IReadOnlyList<string> ResanitizeResults { get; private set; } = [];
    public IReadOnlyList<string> BlindJudgeResults { get; private set; } = [];
    public IReadOnlyList<string> BlindJudgeAnchors { get; private set; } = [];
    public int BlindJudgePacketSize { get; private set; } = 12;
    public int BlindJudgeConcurrency { get; private set; } = 2;
    public string? BlindJudgeReasoningEffort { get; private set; }
    public ReasoningEffort? BenchReasoningEffort { get; private set; }
    public int? BenchMaxOutputTokens { get; private set; }
    public float? BenchTemperature { get; private set; }
    public bool BenchDisableRetries { get; private set; }
    public bool BenchDirectResponses { get; private set; }
    public bool Force { get; private set; }
    public int LocalLoadTimeout { get; private set; } = 1800;
    public int CloudReadyTimeout { get; private set; } = 120;
    public int CleanTimeout { get; private set; } = 180;

    /// <summary>Builds the blind-judge configuration from the parsed flags.</summary>
    public Benchmark.BlindJudgeConfig ToBlindJudgeConfig() => new()
    {
        OutDir = BenchOut ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScribeData", "bench", "blind-judge"),
        ResultsFiles = BlindJudgeResults,
        Anchors = BlindJudgeAnchors,
        JudgeModel = string.IsNullOrWhiteSpace(JudgeModel) ? BenchJudge.DefaultModel : JudgeModel!,
        ReasoningEffort = BlindJudgeReasoningEffort,
        PacketSize = BlindJudgePacketSize,
        Concurrency = BlindJudgeConcurrency,
        CaseOnly = CaseOnly,
        WritingStyle = ReadPromptFile(BenchWritingStyleFile),
    };

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
            CaseOnly = CaseOnly,
            MaxCloud = MaxCloud,
            MaxLocal = MaxLocal,
            CloudEndpoint = AzureEndpoint,
            TenantId = AzureTenantId,
            SubscriptionId = AzureSubscriptionId,
            JudgeEndpoint = string.IsNullOrWhiteSpace(JudgeEndpoint)
                ? "https://mtech-project-resource.cognitiveservices.azure.com/"
                : JudgeEndpoint!,
            JudgeModel = string.IsNullOrWhiteSpace(JudgeModel) ? "gpt-4.1" : JudgeModel!,
            JudgeTenantId = JudgeTenantId ?? AzureTenantId,
            JudgeSubscriptionId = JudgeSubscriptionId ?? AzureSubscriptionId,
            UseJudge = !NoJudge,
            Synthesize = !NoWav,
            Force = Force,
            PromptStyle = PromptStyle,
            WritingStyle = ReadPromptFile(BenchWritingStyleFile),
            FrontierPrompt = ReadPromptFile(BenchFrontierPromptFile),
            LocalPrompt = ReadPromptFile(BenchLocalPromptFile),
            GlossaryEntries = GlossaryEntries(BenchGlossaryLibraries),
            GlossaryMaxTerms = BenchGlossaryMaxTerms,
            VocabularyMode = BenchVocabularyMode,
            PaceMs = BenchPaceMs,
            LocalEndpoints = new BenchEndpoints(OllamaEndpoint, LmStudioEndpoint),
            KeepServerModelsLoaded = KeepServerModelsLoaded,
            CasesFrom = BenchCasesFrom,
            ReasoningEffort = BenchReasoningEffort,
            MaxOutputTokens = BenchMaxOutputTokens,
            Temperature = BenchTemperature,
            DisableRetries = BenchDisableRetries,
            DirectResponses = BenchDirectResponses,
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
            AzureSubscriptionId: AzureSubscriptionId,
            WritingStyle: writingStyle),
        // The Copilot backend takes no endpoint and no key. The model slot is passed through, and a
        // blank one means "whatever this GitHub account defaults to", so `--model` stays optional.
        CleanupProvider.GitHubCopilot => new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.GitHubCopilot,
            FoundryModelAlias: CleanupModelCatalog.DefaultAlias,
            AzureEndpoint: null,
            AzureDeployment: null,
            WritingStyle: writingStyle,
            CopilotModel: string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) ? null : model),
        // A local server (Ollama, LM Studio) or any OpenAI-compatible endpoint, configured the way
        // Settings configures "Another AI service": --endpoint is the /v1 address, --model its model.
        CleanupProvider.OpenAiCompatible => new CleanupOptions(
            Enabled: true,
            Provider: CleanupProvider.OpenAiCompatible,
            FoundryModelAlias: CleanupModelCatalog.DefaultAlias,
            AzureEndpoint: null,
            AzureDeployment: null,
            WritingStyle: writingStyle,
            CustomEndpoint: string.IsNullOrWhiteSpace(AzureEndpoint) ? BenchEndpoints.DefaultOllama : AzureEndpoint,
            CustomModel: model,
            PromptStyle: PromptStyle),
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
                    o.Provider = Next().ToLowerInvariant() switch
                    {
                        "azure" or "azurefoundry" or "cloud" => CleanupProvider.AzureFoundry,
                        "copilot" or "githubcopilot" or "github" => CleanupProvider.GitHubCopilot,
                        "openai" or "openaicompatible" or "custom" or "ollama" or "lmstudio" => CleanupProvider.OpenAiCompatible,
                        _ => CleanupProvider.FoundryLocal,
                    };
                    break;
                case "--suite":
                    o.Suite = Next().ToLowerInvariant() switch
                    {
                        "auxiliary" or "aux" => EvalSuite.Auxiliary,
                        "all" or "both" => EvalSuite.All,
                        _ => EvalSuite.Style,
                    };
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
                case "--subscription":
                    o.AzureSubscriptionId = Next();
                    if (string.IsNullOrWhiteSpace(o.AzureSubscriptionId) || o.AzureSubscriptionId.StartsWith("--"))
                    {
                        throw new ArgumentException("--subscription requires a subscription ID.");
                    }
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
                case "--benchmark-cases":
                    o.CaseOnly = Next()
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
                case "--judge-subscription":
                    o.JudgeSubscriptionId = Next();
                    if (string.IsNullOrWhiteSpace(o.JudgeSubscriptionId) || o.JudgeSubscriptionId.StartsWith("--"))
                    {
                        throw new ArgumentException("--judge-subscription requires a subscription ID.");
                    }
                    break;
                case "--no-judge":
                    o.NoJudge = true;
                    break;
                case "--no-wav":
                    o.NoWav = true;
                    break;
                case "--prompt-style":
                    o.PromptStyle = Next().ToLowerInvariant() switch
                    {
                        "frontier" => CleanupPromptStyle.Frontier,
                        "local" => CleanupPromptStyle.Local,
                        _ => CleanupPromptStyle.Auto,
                    };
                    break;
                case "--writing-style-file":
                    o.BenchWritingStyleFile = Next();
                    break;
                case "--frontier-prompt-file":
                    o.BenchFrontierPromptFile = Next();
                    break;
                case "--glossary-libraries":
                    o.BenchGlossaryLibraries = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--glossary-budget":
                    var budget = Next().Trim().ToLowerInvariant();
                    o.BenchGlossaryMaxTerms = budget switch
                    {
                        "auto" or "" => null,
                        "cloud" or "frontier" => CleanupPrompt.MaxGlossaryTermsCloud,
                        "local" => CleanupPrompt.MaxGlossaryTermsLocal,
                        _ when int.TryParse(budget, out var terms) && terms > 0 => terms,
                        _ => throw new ArgumentException("--glossary-budget takes auto, cloud, local or a positive term count."),
                    };
                    break;
                case "--vocabulary":
                    o.BenchVocabularyMode = Next().Trim().ToLowerInvariant() switch
                    {
                        "all" => CleanupVocabularyMode.All,
                        "mentioned" => CleanupVocabularyMode.Mentioned,
                        "none" => CleanupVocabularyMode.None,
                        _ => throw new ArgumentException("--vocabulary takes all, mentioned or none."),
                    };
                    break;
                case "--pace-ms":
                    o.BenchPaceMs = int.TryParse(Next(), out var pace) && pace >= 0
                        ? pace
                        : throw new ArgumentException("--pace-ms takes a non-negative number of milliseconds.");
                    break;
                case "--local-prompt-file":
                    o.BenchLocalPromptFile = Next();
                    break;
                case "--cases-from":
                    o.BenchCasesFrom = Next();
                    break;
                case "--ollama-endpoint":
                    o.OllamaEndpoint = RequireValue(Next(), "--ollama-endpoint");
                    break;
                case "--lmstudio-endpoint":
                    o.LmStudioEndpoint = RequireValue(Next(), "--lmstudio-endpoint");
                    break;
                case "--keep-loaded":
                    o.KeepServerModelsLoaded = true;
                    break;
                case "--blind-judge":
                    o.BlindJudge = true;
                    break;
                case "--prefetch-foundry":
                    o.PrefetchFoundry = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--resanitize":
                    o.ResanitizeResults = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--judge-results":
                    o.BlindJudgeResults = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--judge-anchors":
                    o.BlindJudgeAnchors = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--judge-packet-size":
                    if (int.TryParse(Next(), out var packet) && packet > 0)
                    {
                        o.BlindJudgePacketSize = packet;
                    }
                    break;
                case "--judge-concurrency":
                    if (int.TryParse(Next(), out var concurrency) && concurrency > 0)
                    {
                        o.BlindJudgeConcurrency = concurrency;
                    }
                    break;
                case "--judge-reasoning-effort":
                    o.BlindJudgeReasoningEffort = RequireValue(Next(), "--judge-reasoning-effort");
                    break;
                case "--reasoning-effort":
                    o.BenchReasoningEffort = Next().ToLowerInvariant() switch
                    {
                        "none" => ReasoningEffort.None,
                        "low" => ReasoningEffort.Low,
                        "medium" => ReasoningEffort.Medium,
                        "high" => ReasoningEffort.High,
                        "xhigh" or "extra-high" => ReasoningEffort.ExtraHigh,
                        _ => null,
                    };
                    break;
                case "--max-output-tokens":
                    if (int.TryParse(Next(), out var maxOutputTokens) && maxOutputTokens > 0)
                    {
                        o.BenchMaxOutputTokens = maxOutputTokens;
                    }
                    break;
                case "--temperature":
                    var temperature = Next().Trim();
                    o.BenchTemperature = string.Equals(temperature, "default", StringComparison.OrdinalIgnoreCase)
                        ? -1f
                        : float.TryParse(temperature, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= 0
                            ? t
                            : throw new ArgumentException("--temperature takes a non-negative number or 'default'.");
                    break;
                case "--no-retries":
                    o.BenchDisableRetries = true;
                    break;
                case "--direct-responses":
                    o.BenchDirectResponses = true;
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
                case "--cloud-ready-timeout":
                    // Slow-validating deployments (ultra reasoning tiers, capacity-1 cold starts)
                    // can exceed the 120 s default probe and report a false "not-ready".
                    if (int.TryParse(Next(), out var crt) && crt > 0)
                    {
                        o.CloudReadyTimeout = crt;
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

    /// <summary>
    /// The shipped dictionary libraries' entries for a glossary arm, so a benchmark can measure the
    /// glossary's contribution rather than assuming it. <c>default</c> names the libraries a fresh
    /// install switches on. The block is rendered per model, at the budget the app applies to that
    /// model's provider and prompt style unless <c>--glossary-budget</c> pins one (see
    /// <see cref="Benchmark.BenchmarkConfig.GlossaryFor"/>). Unknown ids fail loudly: silently
    /// benchmarking an empty glossary would produce a plausible-looking "no effect" result.
    /// </summary>
    private static IReadOnlyList<Scribe.Core.Models.DictionaryEntry>? GlossaryEntries(string[]? libraryIds)
    {
        if (libraryIds is null || libraryIds.Length == 0)
        {
            return null;
        }

        var wanted = new HashSet<string>(
            libraryIds.SelectMany(id => string.Equals(id, "default", StringComparison.OrdinalIgnoreCase)
                ? Scribe.Core.Models.AppSettings.DefaultLibraryIds
                : [id]),
            StringComparer.OrdinalIgnoreCase);
        var known = BuiltInDictionaryLibraries.All.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        var missing = wanted.Where(id => !known.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown dictionary library id(s): {string.Join(", ", missing)}. " +
                $"Available: {string.Join(", ", known.Keys.Order())}");
        }

        return BuiltInDictionaryLibraries.All
            .Where(l => wanted.Contains(l.Id))
            .SelectMany(l => l.Entries)
            .ToList();
    }

    private static string RequireValue(string value, string flag) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)
            ? throw new ArgumentException($"{flag} requires a value.")
            : value.Trim();

    private static string? ReadPromptFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Prompt file not found: {fullPath}", fullPath);
        }

        var text = File.ReadAllText(fullPath).Trim();
        return text.Length == 0
            ? throw new InvalidDataException($"Prompt file is empty: {fullPath}")
            : text;
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            Scribe.Evals: offline prompt eval harness for Scribe AI features.

            Usage:
              dotnet run --project tools/Scribe.Evals -- [options]

            Options:
              --provider <foundrylocal|azure|copilot|openai>
                                                Cleanup backend (default: foundrylocal). openai runs
                                                --model against --endpoint as an OpenAI-compatible server.
              --suite <style|auxiliary|all>     Scenario suite (default: style). "auxiliary" evals
                                                the usage-insight and AI dictionary-suggestion
                                                prompts; "all" runs both suites.
              --model <name>                    A model to test (Foundry alias, or Azure deployment).
                                                Repeatable.
              --models <a,b,c>                  Comma-separated models to compare head-to-head.
              --endpoint <url>                  Azure/Microsoft Foundry endpoint (azure provider,
                                                or explicit benchmark deployment fallback).
              --tenant <id>                     Optional Azure tenant id override (azure provider).
              --subscription <id>               Pin discovery and inference to a cached Azure CLI account.
                                                Does not change the global Azure CLI subscription.
              --ready-timeout <seconds>         Max wait for a model to load (default: 240).
              --list                            List scenarios in the selected suite and exit.
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
              --benchmark-cases <a,b>           Restrict to selected case IDs.
              --max-cloud <n> / --max-local <n>  Cap the number of models per group.
              --judge-endpoint <url>            Azure endpoint for the quality judge.
              --judge-model <name>              Judge deployment (default: gpt-4.1).
              --judge-tenant <id>               Tenant override for the judge.
              --judge-subscription <id>         Judge account override (defaults to --subscription).
              --no-judge                        Latency only (skip quality grading).
              --no-wav                          Use the authored transcript (skip TTS+ASR).
              --writing-style-file <path>       Benchmark-only writing-style override.
              --frontier-prompt-file <path>     Benchmark-only frontier-prompt override.
              --glossary-libraries <a,b>        Append these built-in dictionary libraries to the
                                                prompt as a glossary (e.g. ai-model-names,ai-terminology;
                                                "default" names the libraries a fresh install turns on).
              --glossary-budget <auto|cloud|local|n>
                                                Glossary term budget. auto (default) applies the app's own
                                                budget for each model's provider and prompt style.
              --vocabulary <all|mentioned|none> How much of the glossary each dictation carries: all of it
                                                (default), only the entries it appears to mention, or none.
              --pace-ms <n>                     At least n ms between the starts of two requests to a model,
                                                so a small quota is measured instead of throttled.
              --local-prompt-file <path>        Benchmark-only local-prompt override.
              --cases-from <path>               Frozen transcripts: a cases.json array or benchmark
                                                evidence with a "fixtures" array (docs/benchmarks/*.json).
              --ollama-endpoint <url>           Ollama's /v1 address for ollama:<model> entries
                                                (default: http://127.0.0.1:11434/v1).
              --lmstudio-endpoint <url>         LM Studio's /v1 address for lmstudio:<model> entries
                                                (default: http://127.0.0.1:1234/v1).
              --keep-loaded                     Leave local servers' models loaded between entries (for
                                                instances loaded by hand, such as a CPU-only load).
              --reasoning-effort <level>        default, none, low, medium, high, or xhigh.
              --max-output-tokens <n>           Benchmark-only output/reasoning token cap.
              --temperature <value|default>     Temperature for a model on this PC (default: Scribe's 0.1);
                                                "default" sends none, leaving the server's own.
              --no-retries                      Disable Azure SDK retries for latency diagnosis.
              --direct-responses                Call Azure Responses directly, bypassing Agent Framework.
              --force                           Re-run models already present in results.json.
              --local-load-timeout <seconds>    Max wait for a local model to download+load (default: 1800).
              --clean-timeout <seconds>         Per-call cleanup timeout override (default: 180).

              Local roster entries (--local-models) take a runtime prefix: a bare name or foundry:<alias>
              is Foundry Local, ollama:<model> and lmstudio:<model> go through the OpenAI-compatible
              provider exactly as Settings configures those servers, and openai:<url>|<model> names any
              other server.

            Blind judging (grades retained outputs; no model is run):
              --blind-judge                     Grade every distinct output in the given results files
                                                with an identity-blind judge through the GitHub Copilot CLI.
              --judge-results <a.json,b.json>   results.json files to grade (cases come from the first
                                                file's cases.json).
              --judge-model <id>                Copilot model id (default: claude-opus-5.5).
              --judge-anchors <file:model[@n]>  Reference outputs from benchmark evidence, e.g.
                                                docs/benchmarks/gpt6-astra-2026-09-04.json:gpt-5.6-terra@1.
              --judge-packet-size <n>           Candidates graded together per request (default: 12).
              --judge-concurrency <n>           Parallel judge sessions (default: 2).
              --judge-reasoning-effort <level>  Reasoning effort for the judge model, when it takes one.
              --out <dir>                       Where grades and the summary are written.

            Foundry Local downloads:
              --prefetch-foundry <a,b>          Download these aliases or variant ids into the folder the
                                                cleanup service reads, without loading them.

            Answer cleanup:
              --resanitize <a.json,b.json>      Apply today's answer cleanup to recorded answers and write
                                                each arm again as <folder>-resanitized, listing every change.

            Examples:
              dotnet run --project tools/Scribe.Evals
              dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini
              dotnet run --project tools/Scribe.Evals -- --suite auxiliary --model qwen3-1.7b
              dotnet run --project tools/Scribe.Evals -- --provider azure --endpoint https://x.openai.azure.com/ --model gpt-5.4-mini

            Exit code is 0 when every scenario follows its prompt, otherwise the number of failures.
            """);
    }
}
