using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Scribe.Evals;

/// <summary>
/// A deterministic, model-agnostic <see cref="IEvaluator"/> that scores how well a cleaned
/// transcript adheres to the writing-style instruction it was given. It looks for style "markers"
/// (regex patterns) in the model's output and also checks that the output actually changed from the
/// raw input — so a model that ignores the prompt and echoes the transcript fails. No judge model is
/// required, which makes it safe to run fully offline against Foundry Local.
/// </summary>
internal sealed class StyleAdherenceEvaluator : IEvaluator
{
    internal const string MetricName = "Style Adherence";

    private readonly IReadOnlyList<Regex> _markers;
    private readonly IReadOnlyList<Regex> _forbidden;
    private readonly int _minMarkersToPass;
    private readonly bool _requireChanged;
    private readonly bool _countOccurrences;
    private readonly string _rawNormalized;

    public StyleAdherenceEvaluator(
        IReadOnlyList<string> markerPatterns,
        int minMarkersToPass,
        bool requireChanged,
        bool countOccurrences,
        string rawTranscript,
        IReadOnlyList<string>? forbiddenPatterns = null)
    {
        _markers = markerPatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        _forbidden = (forbiddenPatterns ?? [])
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        _minMarkersToPass = Math.Max(1, minMarkersToPass);
        _requireChanged = requireChanged;
        _countOccurrences = countOccurrences;
        _rawNormalized = Normalize(rawTranscript);
    }

    public IReadOnlyCollection<string> EvaluationMetricNames { get; } = [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var output = modelResponse.Text ?? string.Empty;

        var matched = _markers
            .Where(m => m.IsMatch(output))
            .Select(m => m.ToString())
            .ToList();

        // Word-list styles (pirate, Old English, French) score on the *variety* of distinct markers
        // hit; the bullet-list style scores on the *number* of bullet lines, so it counts occurrences.
        var hits = _countOccurrences
            ? _markers.Sum(m => m.Matches(output).Count)
            : matched.Count;

        // Forbidden patterns catch content that should have been condensed away — the retracted
        // half of a self-correction, or a repeated statement the style says to merge. Any hit fails.
        var violations = _forbidden
            .Where(f => f.IsMatch(output))
            .Select(f => f.ToString())
            .ToList();

        var changed = !string.Equals(Normalize(output), _rawNormalized, StringComparison.Ordinal);
        var passed = hits >= _minMarkersToPass && violations.Count == 0 && (!_requireChanged || changed);

        var score = Math.Min(1.0, hits / (double)_minMarkersToPass);
        if (violations.Count > 0)
        {
            score = 0.0;
        }
        var rating = (passed, score) switch
        {
            (true, >= 1.0) => EvaluationRating.Exceptional,
            (true, _) => EvaluationRating.Good,
            (false, > 0.0) => EvaluationRating.Poor,
            _ => EvaluationRating.Unacceptable,
        };

        var detail = _countOccurrences
            ? $"{hits} matching line(s)"
            : matched.Count > 0 ? $"markers: {string.Join(", ", matched)}" : "no markers";
        var reason = $"{hits}/{_minMarkersToPass} ({detail}); changed-from-raw={changed}.";
        if (violations.Count > 0)
        {
            reason += $" Forbidden content present: {string.Join(", ", violations)}.";
        }

        var metric = new NumericMetric(MetricName, score, reason)
        {
            Interpretation = new EvaluationMetricInterpretation(rating, failed: !passed, reason: reason),
        };

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
}
