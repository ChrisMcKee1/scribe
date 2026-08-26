using Microsoft.Extensions.AI;

namespace Scribe.StyleEval.Runner;

/// <summary>Parsed command line for the style suite.</summary>
internal sealed class StyleEvalOptions
{
    /// <summary>
    /// Default generating deployment. South Central rather than East US 2 on purpose: both host
    /// gpt-5.6-terra, and docs/gpt56-phonetic-benchmark.md measured the DataZoneStandard deployments
    /// at roughly nine to eleven times lower latency than the GlobalStandard ones. Over ten thousand
    /// cells that is the difference between an afternoon and a weekend.
    /// </summary>
    public const string DefaultEndpoint = "https://mtech-sc-resource.cognitiveservices.azure.com/";

    /// <summary>The generating model under test.</summary>
    public const string DefaultDeployment = "gpt-5.6-terra";

    /// <summary>
    /// The subscription whose cached Azure CLI account owns the deployments. Passed as
    /// <c>--subscription</c> rather than <c>--tenant</c>, which is what
    /// <c>AzureCredentialFactory</c> in Scribe.Core does and the only form that mints an
    /// ai.azure.com token on a machine signed in to several tenants.
    /// </summary>
    public const string DefaultSubscription = "d7652db6-8548-4b7d-81e9-16638a7287c4";

    public string Endpoint { get; private set; } = DefaultEndpoint;

    public string Deployment { get; private set; } = DefaultDeployment;

    public string? Subscription { get; private set; } = DefaultSubscription;

    public string? TenantId { get; private set; }

    public string CorpusDirectory { get; private set; } = string.Empty;

    public string ResultsPath { get; private set; } = string.Empty;

    public int Concurrency { get; private set; } = 8;

    /// <summary>
    /// Ceiling on model calls per minute across the whole run, enforced by
    /// <see cref="AdaptiveRateLimiter"/>.
    /// </summary>
    /// <remarks>
    /// Defaults low enough to leave the deployment usable by whatever else is on it. A previous run
    /// at concurrency 8 with no rate ceiling saturated the shared deployment so thoroughly that
    /// Scribe itself got HTTP 429 on every interactive action for the duration. Concurrency alone
    /// cannot express this: it caps calls in flight, not calls per minute, so eight fast cells
    /// offer far more load than eight slow ones. Raise it when the deployment is not shared.
    /// </remarks>
    public int RequestsPerMinute { get; private set; } = 60;

    /// <summary>First N scenarios per category, or 0 for all of them.</summary>
    public int Sample { get; private set; }

    public IReadOnlyList<string> Actions { get; private set; } = [];

    public IReadOnlyList<string> Categories { get; private set; } = [];

    public ReasoningEffort? Reasoning { get; private set; }

    public int MaxOutputTokens { get; private set; } = 4000;

    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(180);

    /// <summary>USD per million input tokens, for the cost estimate.</summary>
    public double PriceInputPerMillion { get; private set; } = 1.25;

    /// <summary>USD per million output tokens, for the cost estimate.</summary>
    public double PriceOutputPerMillion { get; private set; } = 10.00;

    /// <summary>Load the corpus, print the plan and the cost estimate, then stop.</summary>
    public bool DryRun { get; private set; }

    /// <summary>Re-run every cell instead of skipping the ones already in the results file.</summary>
    public bool NoResume { get; private set; }

    /// <summary>Re-score an existing results file with the current checkers and stop.</summary>
    public bool ScoreOnly { get; private set; }

    /// <summary>
    /// Baseline results file for a comparison run. Null when not comparing.
    /// </summary>
    /// <remarks>
    /// This is how a prompt edit is judged. A single run gives a score; only a comparison against the
    /// run the edit was meant to improve says whether it helped, and what it broke on the way.
    /// </remarks>
    public string? ComparePath { get; private set; }

    /// <summary>Variant results file for the comparison. Defaults to the normal results path.</summary>
    public string? CompareAgainstPath { get; private set; }

    /// <summary>Path stem for a before-and-after export. Null when not exporting.</summary>
    public string? ExportPairsStem { get; private set; }

    /// <summary>Which cells the export selects.</summary>
    public ExportPairs.Selection ExportSelection { get; private set; } = ExportPairs.Selection.FailuresAndControl;

    /// <summary>
    /// Take one clean cell in this many as a control. Zero disables the control sample, which is the
    /// only thing in the suite that can find an answer that passed every check and is still poor.
    /// </summary>
    public int ExportControlEvery { get; private set; } = 8;

    /// <summary>Enumerate the Azure deployments the current sign-in can reach, then stop.</summary>
    public bool ListDeployments { get; private set; }

    /// <summary>Run the LLM judge over the stored answers rather than generating anything.</summary>
    public bool Judge { get; private set; }

    /// <summary>
    /// The deployment that produces verdicts. Required for <see cref="Judge"/> and deliberately
    /// without a default.
    /// </summary>
    /// <remarks>
    /// A model judging its own output is a known validity problem: it prefers its own phrasing and
    /// it is blind to its own habits. Defaulting this to the generating deployment would make that
    /// the quiet, normal case, so there is no default at all and the runner refuses to start when
    /// the name matches the deployment that wrote the answers.
    /// </remarks>
    public string? JudgeDeployment { get; private set; }

    /// <summary>Account endpoint for the judge. Defaults to the generating endpoint.</summary>
    public string? JudgeEndpointOverride { get; private set; }

    /// <summary>The endpoint the judge actually runs against.</summary>
    public string JudgeEndpoint => JudgeEndpointOverride ?? Endpoint;

    /// <summary>Verdict file. Defaults to the results path with a <c>.judge.jsonl</c> suffix.</summary>
    public string? JudgePathOverride { get; private set; }

    /// <summary>The verdict file this run reads and writes.</summary>
    public string JudgePath =>
        JudgePathOverride ?? global::Scribe.StyleEval.Judge.JudgeStore.DefaultPathFor(ResultsPath);

    /// <summary>First N judged cells per action, or 0 for all of them.</summary>
    public int JudgeSample { get; private set; }

    private int? _judgeConcurrency;

    /// <summary>In-flight judge calls. Defaults to the generation concurrency.</summary>
    public int JudgeConcurrency => _judgeConcurrency ?? Concurrency;

    /// <summary>Re-judge cells already present in the verdict file.</summary>
    public bool JudgeNoResume { get; private set; }

    public ReasoningEffort? JudgeReasoning { get; private set; }

    public int JudgeMaxOutputTokens { get; private set; } = 3000;

    /// <summary>USD per million input tokens for the judge deployment.</summary>
    public double JudgePriceInputPerMillion { get; private set; } = 1.25;

    /// <summary>USD per million output tokens for the judge deployment.</summary>
    public double JudgePriceOutputPerMillion { get; private set; } = 10.00;

    /// <summary>Write the Markdown report and stop.</summary>
    public bool Report { get; private set; }

    /// <summary>Report path. Defaults to <c>results/report.md</c> beside the results file.</summary>
    public string? ReportPathOverride { get; private set; }

    /// <summary>The report file this run writes.</summary>
    public string ReportPath => ReportPathOverride ??
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ResultsPath)) ?? ".", "report.md");

    public bool ShowHelp { get; private set; }

    public static StyleEvalOptions Parse(string[] args, string baseDirectory)
    {
        // Results default to the PROJECT folder, not the output folder. A full run is hours of paid
        // model calls, and `dotnet build` wipes bin, so defaulting results next to the binary would
        // mean an ordinary rebuild silently threw the run away.
        var o = new StyleEvalOptions
        {
            CorpusDirectory = Path.Combine(baseDirectory, "corpus"),
            ResultsPath = Path.Combine(ProjectDirectory(baseDirectory), "results", "style-eval.jsonl"),
        };

        string Next(int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"{args[i]} needs a value.");
            }

            return args[i + 1];
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-h":
                case "--help":
                    o.ShowHelp = true;
                    break;
                case "--endpoint":
                    o.Endpoint = Next(i++);
                    break;
                case "--deployment":
                case "--model":
                    o.Deployment = Next(i++);
                    break;
                case "--subscription":
                    o.Subscription = Next(i++);
                    break;
                case "--tenant":
                    o.TenantId = Next(i++);
                    o.Subscription = null;
                    break;
                case "--corpus":
                    o.CorpusDirectory = Path.GetFullPath(Next(i++));
                    break;
                case "--out":
                    o.ResultsPath = Path.GetFullPath(Next(i++));
                    break;
                case "--concurrency":
                    o.Concurrency = Math.Clamp(int.Parse(Next(i++)), 1, 64);
                    break;
                case "--requests-per-minute":
                    o.RequestsPerMinute = Math.Clamp(int.Parse(Next(i++)), 1, 6000);
                    break;
                case "--sample":
                    o.Sample = Math.Max(0, int.Parse(Next(i++)));
                    break;
                case "--actions":
                    o.Actions = Split(Next(i++));
                    break;
                case "--categories":
                    o.Categories = Split(Next(i++));
                    break;
                case "--reasoning":
                    o.Reasoning = ParseReasoning(Next(i++));
                    break;
                case "--max-output-tokens":
                    o.MaxOutputTokens = int.Parse(Next(i++));
                    break;
                case "--timeout-seconds":
                    o.Timeout = TimeSpan.FromSeconds(int.Parse(Next(i++)));
                    break;
                case "--price-in":
                    o.PriceInputPerMillion = double.Parse(Next(i++));
                    break;
                case "--price-out":
                    o.PriceOutputPerMillion = double.Parse(Next(i++));
                    break;
                case "--dry-run":
                    o.DryRun = true;
                    break;
                case "--no-resume":
                    o.NoResume = true;
                    break;
                case "--score-only":
                    o.ScoreOnly = true;
                    break;
                case "--list-deployments":
                    o.ListDeployments = true;
                    break;
                case "--judge":
                    o.Judge = true;
                    break;
                case "--judge-deployment":
                case "--judge-model":
                    o.JudgeDeployment = Next(i++);
                    break;
                case "--judge-endpoint":
                    o.JudgeEndpointOverride = Next(i++);
                    break;
                case "--judge-out":
                    o.JudgePathOverride = Path.GetFullPath(Next(i++));
                    break;
                case "--judge-sample":
                    o.JudgeSample = Math.Max(0, int.Parse(Next(i++)));
                    break;
                case "--judge-concurrency":
                    o._judgeConcurrency = Math.Clamp(int.Parse(Next(i++)), 1, 64);
                    break;
                case "--judge-no-resume":
                    o.JudgeNoResume = true;
                    break;
                case "--judge-reasoning":
                    o.JudgeReasoning = ParseReasoning(Next(i++));
                    break;
                case "--judge-max-output-tokens":
                    o.JudgeMaxOutputTokens = int.Parse(Next(i++));
                    break;
                case "--judge-price-in":
                    o.JudgePriceInputPerMillion = double.Parse(Next(i++));
                    break;
                case "--judge-price-out":
                    o.JudgePriceOutputPerMillion = double.Parse(Next(i++));
                    break;
                case "--export-pairs":
                    o.ExportPairsStem = Path.GetFullPath(Next(i++));
                    break;
                case "--export-all":
                    o.ExportSelection = ExportPairs.Selection.All;
                    break;
                case "--export-failures":
                    o.ExportSelection = ExportPairs.Selection.FailuresOnly;
                    break;
                case "--export-control-every":
                    o.ExportControlEvery = Math.Max(0, int.Parse(Next(i++)));
                    break;
                case "--compare":
                    o.ComparePath = Path.GetFullPath(Next(i++));
                    break;
                case "--against":
                    o.CompareAgainstPath = Path.GetFullPath(Next(i++));
                    break;
                case "--report":
                    o.Report = true;
                    break;
                case "--report-out":
                    o.ReportPathOverride = Path.GetFullPath(Next(i++));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Run with --help.");
            }
        }

        return o;
    }

    /// <summary>
    /// Walks up from the output folder to the folder holding <c>Scribe.StyleEval.csproj</c>, falling
    /// back to the output folder when the tool has been copied somewhere on its own.
    /// </summary>
    private static string ProjectDirectory(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Scribe.StyleEval.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return baseDirectory;
    }

    /// <summary>
    /// Parses a reasoning-effort level. Shared by the generation and judge flags so the two can never
    /// drift into accepting different spellings of the same level.
    /// </summary>
    private static ReasoningEffort ParseReasoning(string value) => value.ToLowerInvariant() switch
    {
        "none" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "xhigh" => ReasoningEffort.ExtraHigh,
        var other => throw new ArgumentException($"Unknown reasoning effort '{other}'."),
    };

    private static IReadOnlyList<string> Split(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public const string Help =
        """
        Scribe.StyleEval: side-testing evaluation of the shipping text-action instruction sets.
        Never published. Lives in tools/ and is not part of any build/pack.ps1 output.

        Usage:
          dotnet run --project tools/Scribe.StyleEval -- [options]

        Selection
          --sample N               First N scenarios per category (0 = all).
          --categories a,b         Only these corpus categories.
          --actions a,b            Only these action ids.
          --corpus <dir>           Corpus directory (default: <bin>/corpus).

        Model
          --endpoint <url>         Azure account or project endpoint.
                                   Default https://mtech-sc-resource.cognitiveservices.azure.com/
          --deployment <name>      Deployment name. Default gpt-5.6-terra.
          --subscription <id>      Azure subscription whose cached CLI account owns it.
          --tenant <id>            Use a tenant instead of a subscription for the CLI credential.
          --reasoning <level>      none|low|medium|high|xhigh. Default: the service default.
          --max-output-tokens N    Default 4000.
          --timeout-seconds N      Per-call budget. Default 180.

        Run
          --concurrency N          In-flight cells. Default 8.
          --requests-per-minute N  Ceiling on model calls per minute for the whole run. Default 60.
                                   Halves on HTTP 429 and climbs back after a clean stretch,
                                   so a shared deployment stays usable while it proceeds.
          --out <path>             Results JSONL. Default tools/Scribe.StyleEval/results/style-eval.jsonl,
                                   which survives a rebuild.
          --no-resume              Re-run cells already present in the results file.
          --dry-run                Plan and cost estimate only, no model calls.
          --score-only             Re-score an existing results file with today's checkers.
          --compare <baseline>     Diff a baseline results file against --against (or the default
                                   results file). Prints per-check and per-action deltas and lists
                                   every regression in full. This is how a prompt edit is judged.
          --against <variant>      The variant results file for --compare.
          --list-deployments       Enumerate reachable Azure deployments and stop.
          --price-in <usd>         USD per million input tokens for the estimate. Default 1.25.
          --price-out <usd>        USD per million output tokens. Default 10.00.

        Examples
          dotnet run --project tools/Scribe.StyleEval -- --sample 2
          dotnet run --project tools/Scribe.StyleEval -- --categories restraint,detection --concurrency 12
          dotnet run --project tools/Scribe.StyleEval -- --score-only --out results/style-eval.jsonl
        """;
}
