using System.Text.Json.Serialization;
using Scribe.StyleEval.Checks;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// One scenario-plus-action cell: what was sent, what came back, what the shipping sanitizer said,
/// and every deterministic verdict.
/// </summary>
/// <remarks>
/// Written to the results file as one JSON object per line, appended the moment the cell completes.
/// The raw response is kept alongside the sanitized text because the two differ in ways that matter:
/// the sanitizer strips a wrapper fence, and a Markdown answer that was wrapped in one is a contract
/// violation the sanitized text no longer shows.
/// </remarks>
internal sealed record CellResult
{
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("actionId")]
    public required string ActionId { get; init; }

    [JsonPropertyName("deployment")]
    public required string Deployment { get; init; }

    /// <summary>Null on success; the transport or provider error otherwise.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("rawResponse")]
    public string RawResponse { get; init; } = string.Empty;

    [JsonPropertyName("sanitizedText")]
    public string SanitizedText { get; init; } = string.Empty;

    [JsonPropertyName("sanitizerAccepted")]
    public bool SanitizerAccepted { get; init; }

    /// <summary>The shipping <c>TextActionSanitizer.RejectionReason</c> name.</summary>
    [JsonPropertyName("sanitizerReason")]
    public string SanitizerReason { get; init; } = "None";

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("inputTokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("reasoningTokens")]
    public long? ReasoningTokens { get; init; }

    [JsonPropertyName("checks")]
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];

    [JsonPropertyName("completedUtc")]
    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The cell key used for resume. Scenario and action together, nothing else.</summary>
    [JsonIgnore]
    public string Key => CellKey(ScenarioId, ActionId);

    public static string CellKey(string scenarioId, string actionId) => scenarioId + "::" + actionId;

    [JsonIgnore]
    public int Failures => Checks.Count(c => c.Status == CheckStatus.Fail);

    [JsonIgnore]
    public int NegativeFailures =>
        Checks.Count(c => c.Status == CheckStatus.Fail && c.Polarity == CheckPolarity.Negative);

    [JsonIgnore]
    public int PositiveFailures =>
        Checks.Count(c => c.Status == CheckStatus.Fail && c.Polarity == CheckPolarity.Positive);
}
