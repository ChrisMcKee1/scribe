using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Scribe.Core.PostProcessing;

namespace Scribe.Evals;

/// <summary>
/// A deterministic <see cref="IEvaluator"/> for <see cref="AiDictionarySuggester.SystemPrompt"/>
/// responses. It runs the response through the exact production parser
/// (<see cref="AiDictionarySuggester.ParseSuggestions"/>) and then checks the one property the
/// parser cannot enforce: grounding. Every suggestion must anchor to the dictation sample on at
/// least one side of the pair: the spoken form (the mishearing that appears in the sample) or the
/// written form (the correctly spelled term that appears in the sample). A model that invents
/// terms the user never dictated fails. Lowercase spoken forms are also asserted per the prompt.
/// </summary>
internal sealed class DictionarySuggestionEvaluator : IEvaluator
{
    internal const string MetricName = "Dictionary Suggestion Grounding";

    private readonly string _sampleNormalized;

    public DictionarySuggestionEvaluator(string sample) => _sampleNormalized = Normalize(sample);

    public IReadOnlyCollection<string> EvaluationMetricNames { get; } = [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var output = modelResponse.Text ?? string.Empty;
        var violations = new List<string>();

        // Empty existing dictionary keeps this focused on parseability and the response contract,
        // not duplicate filtering against user data.
        var suggestions = AiDictionarySuggester.ParseSuggestions(output, existing: []);
        if (suggestions.Count == 0)
        {
            violations.Add("response did not parse into any suggestions");
        }

        var grounded = 0;
        foreach (var suggestion in suggestions)
        {
            var spoken = Normalize(suggestion.Pattern);
            var written = Normalize(suggestion.Replacement);

            if (ContainsPhrase(_sampleNormalized, spoken) || ContainsPhrase(_sampleNormalized, written))
            {
                grounded++;
            }
            else
            {
                violations.Add($"'{suggestion.Pattern}' -> '{suggestion.Replacement}' not in the sample");
            }

            if (!string.Equals(suggestion.Pattern, suggestion.Pattern.ToLowerInvariant(), StringComparison.Ordinal))
            {
                violations.Add($"spoken form '{suggestion.Pattern}' is not lowercase");
            }
        }

        var passed = violations.Count == 0;
        var score = suggestions.Count == 0 ? 0.0 : grounded / (double)suggestions.Count;
        var rating = (passed, score) switch
        {
            (true, _) => EvaluationRating.Exceptional,
            (false, > 0.0) => EvaluationRating.Poor,
            _ => EvaluationRating.Unacceptable,
        };

        var reason = passed
            ? $"{suggestions.Count} suggestion(s), all grounded in the sample."
            : string.Join("; ", violations) + ".";

        var metric = new NumericMetric(MetricName, score, reason)
        {
            Interpretation = new EvaluationMetricInterpretation(rating, failed: !passed, reason: reason),
        };

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    // Lowercase + collapsed whitespace so "Cosmos DB" matches "cosmos db" and line breaks in the
    // sample never break a containment check.
    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    private static bool ContainsPhrase(string sample, string phrase)
    {
        if (phrase.Count(char.IsLetterOrDigit) < 2)
        {
            return false;
        }

        return Regex.IsMatch(
            sample,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant);
    }
}
