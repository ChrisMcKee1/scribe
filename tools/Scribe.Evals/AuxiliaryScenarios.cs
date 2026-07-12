using Microsoft.Extensions.AI.Evaluation;
using Scribe.Core.Diagnostics;
using Scribe.Core.PostProcessing;

namespace Scribe.Evals;

/// <summary>
/// A single auxiliary-prompt eval case: one of Scribe's non-cleanup system prompts run against a
/// realistic input through <c>ITextCleanupService.CompleteAsync</c> (the exact call path the app
/// uses), scored by a deterministic evaluator. Unlike the style suite, each scenario carries its own
/// full system prompt, the shipped prompt constant under test, so the suite catches a prompt edit
/// that breaks the response contract (parse shape, grounding, banned inference) before it ships.
/// </summary>
internal sealed record AuxiliaryScenario(
    string Name,
    string SystemPrompt,
    string UserMessage,
    string MetricName,
    IEvaluator Evaluator);

internal static class AuxiliaryScenarios
{
    // Build summaries through production code so format changes cannot leave the eval fixtures stale.
    private static readonly string DevOpsSummary = BuildUsageSummary(
        dictations: 148,
        words: 21430,
        activeDays: 19,
        [
            new UsageAnalyzer.TermUsage("Kubernetes", 32, 42, Covered: false),
            new UsageAnalyzer.TermUsage("PostgreSQL", 27, 35, Covered: false),
            new UsageAnalyzer.TermUsage("Terraform", 19, 24, Covered: false),
            new UsageAnalyzer.TermUsage("GitHub Actions", 14, 18, Covered: false),
            new UsageAnalyzer.TermUsage("Azure", 11, 15, Covered: false),
        ]);

    private static readonly string ClinicalSummary = BuildUsageSummary(
        dictations: 63,
        words: 9840,
        activeDays: 12,
        [
            new UsageAnalyzer.TermUsage("Radiology", 21, 26, Covered: false),
            new UsageAnalyzer.TermUsage("MRI", 18, 22, Covered: false),
            new UsageAnalyzer.TermUsage("Hypertension", 9, 11, Covered: false),
            new UsageAnalyzer.TermUsage("Cardiology", 7, 9, Covered: false),
        ]);

    private static readonly string SparseSummary = BuildUsageSummary(
        dictations: 12,
        words: 980,
        activeDays: 4,
        [
            new UsageAnalyzer.TermUsage("Unity", 5, 7, Covered: false),
            new UsageAnalyzer.TermUsage("Blender", 3, 4, Covered: false),
        ]);

    // Dictation samples in the shape BuildHistorySample emits (one dictation per line). Each
    // deliberately mixes correctly written terms with ASR-style spoken forms ("a p i m",
    // "cube control") so a grounded suggestion can anchor on either side of the pair.
    private const string DevOpsSample =
        "deploy the new container image to kubernetes and then run cube control get pods to verify the rollout\n" +
        "the PostgreSQL connection string in the app settings still points at the staging database\n" +
        "add the a p i m subscription key to the pipeline secrets before the Cosmos DB migration runs\n" +
        "remember to open a pull request on git hub once the terraform plan output looks clean";

    private const string ProductSample =
        "ask the sequel server team whether the read replica lag is back under a second\n" +
        "the figma mockups for the onboarding flow need sign off before we touch type script code\n" +
        "schedule a review of the o auth token refresh logic in the react native client\n" +
        "the v s code extension update broke intellisense for the graph q l schema files";

    internal static IReadOnlyList<AuxiliaryScenario> All { get; } =
    [
        new AuxiliaryScenario(
            Name: "Usage insight: dev tools",
            SystemPrompt: UsageInsight.SystemPrompt,
            UserMessage: DevOpsSummary,
            MetricName: UsageInsightEvaluator.MetricName,
            Evaluator: new UsageInsightEvaluator(DevOpsSummary)),

        new AuxiliaryScenario(
            Name: "Usage insight: clinical",
            SystemPrompt: UsageInsight.SystemPrompt,
            UserMessage: ClinicalSummary,
            MetricName: UsageInsightEvaluator.MetricName,
            Evaluator: new UsageInsightEvaluator(ClinicalSummary)),

        new AuxiliaryScenario(
            Name: "Usage insight: sparse",
            SystemPrompt: UsageInsight.SystemPrompt,
            UserMessage: SparseSummary,
            MetricName: UsageInsightEvaluator.MetricName,
            Evaluator: new UsageInsightEvaluator(SparseSummary)),

        new AuxiliaryScenario(
            Name: "Dictionary: dev ops",
            SystemPrompt: AiDictionarySuggester.SystemPrompt,
            UserMessage: DevOpsSample,
            MetricName: DictionarySuggestionEvaluator.MetricName,
            Evaluator: new DictionarySuggestionEvaluator(DevOpsSample)),

        new AuxiliaryScenario(
            Name: "Dictionary: product",
            SystemPrompt: AiDictionarySuggester.SystemPrompt,
            UserMessage: ProductSample,
            MetricName: DictionarySuggestionEvaluator.MetricName,
            Evaluator: new DictionarySuggestionEvaluator(ProductSample)),
    ];

    private static string BuildUsageSummary(
        int dictations,
        int words,
        int activeDays,
        IReadOnlyList<UsageAnalyzer.TermUsage> terms) =>
        UsageInsight.BuildSummary(new UsageAnalyzer.Snapshot(
            Dictations: dictations,
            Words: words,
            ActiveDays: activeDays,
            Speech: TimeSpan.Zero,
            AverageWords: dictations == 0 ? 0 : words / (double)dictations,
            TopApps: [],
            Trend: [],
            Terms: terms));
}
