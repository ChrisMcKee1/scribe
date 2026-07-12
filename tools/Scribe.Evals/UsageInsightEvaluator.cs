using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Scribe.Core.Diagnostics;

namespace Scribe.Evals;

/// <summary>
/// A deterministic <see cref="IEvaluator"/> for <see cref="UsageInsight.SystemPrompt"/> responses.
/// The prompt's contract is narrow: 2 to 4 factual plain-text sentences describing only the
/// supplied aggregate summary. The required properties are checkable without a judge model: the response
/// must survive <see cref="UsageInsight.Parse"/>, carry no markdown structure, stay inside the
/// sentence budget, use none of the inference vocabulary the prompt forbids (mood, productivity,
/// time saved, and similar claims), and name no capitalized technical token absent from the input.
/// </summary>
internal sealed class UsageInsightEvaluator : IEvaluator
{
    internal const string MetricName = "Usage Insight Adherence";

    // Any hit is invention by construction: the aggregate input contains counts and term labels
    // only, so mood/personality/sentiment/productivity/intent/time-saved language cannot be factual.
    private static readonly IReadOnlyList<Regex> BannedInference =
        new[]
        {
            @"\bmoods?\b", @"\bpersonalit(y|ies)\b", @"\bsentiments?\b", @"\bproductiv\w*\b",
            @"\befficien\w*\b", @"\bsav(e|es|ed|ing)\b", @"\bfeel(s|ing|ings)?\b", @"\bfelt\b",
            @"\bbusy\b", @"\bintent(ion)?s?\b", @"\bhabits?\b", @"\bimpressive\b", @"\bdiligent\b",
            @"\bhard-?working\b", @"\benjoy\w*\b", @"\bpassion\w*\b", @"\bmotivat\w*\b",
            @"\bdedicat\w*\b", @"\bexpert\w*\b", @"\bimpress\w*\b", @"\blikely\b",
            @"\bprobabl\w*\b", @"\bperhaps\b", @"\bseems?\b", @"\bsuggest\w*\b",
            @"\bindicat\w*\b", @"\bproficien\w*\b", @"\bskill(ed|ful)?\b", @"\bprefer\w*\b",
        }
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToList();

    // Fences, headings, lists, and block quotes all violate the prompt's plain-text contract.
    private static readonly Regex MarkdownStructure = new(
        @"```|~~~|(?m)^[ \t]*(?:#{1,6}\s|[-*+•]\s|\d+[.)]\s|>\s|(?:=+|-+)[ \t]*$)",
        RegexOptions.CultureInvariant);

    private static readonly Regex SentenceSplit = new(
        @"(?<=[.!?])\s+", RegexOptions.CultureInvariant);

    private static readonly Regex SummaryTerm = new(
        @"(?m)^- (?<term>.+): \d+ dictations\r?$", RegexOptions.CultureInvariant);

    // Hyphens and slashes split compounds into independently grounded tokens. Dots, plus signs, and
    // hashes stay attached so Node.js, C++, and C# remain single tokens.
    private static readonly Regex TokenPattern = new(
        @"[\p{L}][\p{L}\p{N}.+#]*", RegexOptions.CultureInvariant);

    // These ordinary words may be capitalized by sentence position without becoming claimed term
    // labels. Domain names and product names intentionally do not belong here.
    private static readonly HashSet<string> CommonEnglishWords = new(
        [
            "A", "According", "Across", "Active", "Additional", "All", "Also", "Although", "An",
            "And", "Another", "As", "At", "Based", "Both", "By", "Common", "Counts", "Data",
            "Days", "Dictation", "Dictations", "Each", "For", "Frequent", "From", "Given",
            "However", "In", "It", "Its", "Leading", "More", "Most", "Notably", "Of", "On",
            "One", "Only", "Other", "Others", "Overall", "Recurring", "Several", "Some", "Such",
            "Summary", "Technical", "Terms", "The", "These", "This", "Those", "Through", "To",
            "Together", "Two", "Usage", "While", "With", "Within", "Words",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _summary;
    private readonly IReadOnlyList<string> _terms;

    public UsageInsightEvaluator(string summary)
    {
        _summary = summary;
        _terms = SummaryTerm.Matches(summary)
            .Select(match => match.Groups["term"].Value)
            .ToList();
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
        var violations = new List<string>();

        // The response must round-trip through the exact parser the app feeds it to.
        var parsed = UsageInsight.Parse(output);
        if (parsed is null)
        {
            violations.Add("UsageInsight.Parse rejected the response");
        }

        if (MarkdownStructure.IsMatch(output))
        {
            violations.Add("markdown structure present");
        }

        var sentences = CountSentences(parsed ?? output);
        if (sentences is < 2 or > 4)
        {
            violations.Add($"sentence count {sentences} outside 2-4");
        }

        var banned = BannedInference
            .Select(r => r.Match(output))
            .Where(m => m.Success)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (banned.Count > 0)
        {
            violations.Add($"inference vocabulary: {string.Join(", ", banned)}");
        }

        var invented = FindInventedTerms(parsed ?? output);
        if (invented.Count > 0)
        {
            violations.Add($"terms not in the summary: {string.Join(", ", invented)}");
        }

        var groundedTerms = _terms.Where(term => ContainsTerm(parsed ?? output, term)).ToList();
        if (groundedTerms.Count == 0)
        {
            violations.Add("no recurring term from the summary was mentioned");
        }

        var passed = violations.Count == 0;
        var score = Math.Max(0.0, 1.0 - 0.25 * violations.Count);
        var rating = (passed, score) switch
        {
            (true, _) => EvaluationRating.Exceptional,
            (false, > 0.0) => EvaluationRating.Poor,
            _ => EvaluationRating.Unacceptable,
        };

        var reason = passed
            ? $"{sentences} sentence(s); parsed, plain text, no inference, {groundedTerms.Count} supplied term(s) mentioned."
            : string.Join("; ", violations) + ".";

        var metric = new NumericMetric(MetricName, score, reason)
        {
            Interpretation = new EvaluationMetricInterpretation(rating, failed: !passed, reason: reason),
        };

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    private static int CountSentences(string text) =>
        SentenceSplit.Split(text.Trim())
            .Count(s => !string.IsNullOrWhiteSpace(s));

    // Every capitalized non-common token is treated as a technical label. Checking sentence-initial
    // tokens too prevents an invented product name from escaping merely by starting a sentence.
    private List<string> FindInventedTerms(string text)
    {
        var invented = new List<string>();
        foreach (Match match in TokenPattern.Matches(text))
        {
            // Sentence-final periods ride along in the token ("PostgreSQL."); internal dots stay.
            var token = match.Value.TrimEnd('.');
            if (token.Length == 0 || !char.IsUpper(token[0]))
            {
                continue;
            }

            if (CommonEnglishWords.Contains(token))
            {
                continue;
            }

            var inSummary = Regex.IsMatch(
                _summary,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(token)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!inSummary && !invented.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                invented.Add(token);
            }
        }

        return invented;
    }

    private static bool ContainsTerm(string text, string term) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
