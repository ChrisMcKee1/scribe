using System.Text.Json.Serialization;

namespace Scribe.StyleEval.Judge;

/// <summary>How badly a finding matters. Ordered, so a report can sort by it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
internal enum Severity
{
    /// <summary>The judge named a severity this suite does not know. Counted, never scored.</summary>
    Unknown = 0,

    /// <summary>A careful editor would change it; the text survives without the change.</summary>
    Minor = 1,

    /// <summary>Clearly worse as it stands.</summary>
    Moderate = 2,

    /// <summary>A reader loses the point, or the output says something the author did not say.</summary>
    Major = 3,
}

/// <summary>
/// Whether the scenario's own expectations back a missed-opportunity finding.
/// </summary>
/// <remarks>
/// Self-reported by the judge and then checked in the report against what the corpus actually says.
/// It is the cheapest calibration signal in the suite: a judge that reports "confirms" on a scenario
/// whose expectations say the opposite is not reading its ground truth, and a wall of "silent"
/// findings is a judge free-associating rather than checking.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<GroundTruthStance>))]
internal enum GroundTruthStance
{
    /// <summary>Unparseable or absent.</summary>
    Unknown = 0,

    /// <summary>The ground truth block explicitly names this structure.</summary>
    Confirms,

    /// <summary>The ground truth block says nothing either way.</summary>
    Silent,

    /// <summary>The ground truth block argues against it.</summary>
    Contradicts,
}

/// <summary>Structure the content warranted that the output does not have.</summary>
/// <param name="Kind">Which markup was warranted.</param>
/// <param name="InputSpan">The words from the input that warranted it, quoted by the judge.</param>
/// <param name="Severity">How much the reader loses.</param>
/// <param name="DetectionSignal">Which Detection bullet justifies it.</param>
/// <param name="GroundTruth">Whether the scenario's expectations back the finding.</param>
/// <param name="Explanation">One sentence.</param>
/// <param name="Grounded">
/// True when <paramref name="InputSpan"/> was actually found in the input. Written by the runner,
/// not by the judge: an ungrounded finding is a quote the judge could not produce, and it is
/// excluded from every aggregate rather than argued with.
/// </param>
internal sealed record MissedOpportunity(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("inputSpan")] string InputSpan,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("detectionSignal")] string DetectionSignal,
    [property: JsonPropertyName("groundTruth")] GroundTruthStance GroundTruth,
    [property: JsonPropertyName("explanation")] string Explanation)
{
    [JsonPropertyName("grounded")]
    public bool Grounded { get; init; }
}

/// <summary>Structure in the output that the content did not warrant.</summary>
internal sealed record UnwarrantedStructure(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("outputSpan")] string OutputSpan,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("restraintRule")] string RestraintRule,
    [property: JsonPropertyName("explanation")] string Explanation)
{
    /// <summary>True when the quoted span was actually found in the output.</summary>
    [JsonPropertyName("grounded")]
    public bool Grounded { get; init; }
}

/// <summary>A fact, number, name, commitment, caveat or question that did not survive intact.</summary>
internal sealed record FidelityIssue(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("inputSpan")] string InputSpan,
    [property: JsonPropertyName("outputSpan")] string OutputSpan,
    [property: JsonPropertyName("severity")] Severity Severity,
    [property: JsonPropertyName("explanation")] string Explanation)
{
    /// <summary>
    /// True when the span this issue leans on was found where the judge said it was: the input span
    /// for a drop or a change, the output span for an addition.
    /// </summary>
    [JsonPropertyName("grounded")]
    public bool Grounded { get; init; }
}

/// <summary>The subjective scores, all 0 to 100.</summary>
internal sealed record QualityScores(
    [property: JsonPropertyName("goal")] int Goal,
    [property: JsonPropertyName("register")] int Register,
    [property: JsonPropertyName("clarity")] int Clarity,
    [property: JsonPropertyName("fidelity")] int Fidelity,
    [property: JsonPropertyName("overall")] int Overall,
    [property: JsonPropertyName("wouldShipAsIs")] bool WouldShipAsIs,
    [property: JsonPropertyName("verdict")] string Verdict);

/// <summary>One judge answer, exactly as the schema shapes it.</summary>
internal sealed record JudgeVerdict
{
    /// <summary>under-structured, appropriate or over-structured.</summary>
    [JsonPropertyName("structureVerdict")]
    public string StructureVerdict { get; init; } = string.Empty;

    [JsonPropertyName("missedOpportunities")]
    public IReadOnlyList<MissedOpportunity> MissedOpportunities { get; init; } = [];

    [JsonPropertyName("unwarrantedStructure")]
    public IReadOnlyList<UnwarrantedStructure> UnwarrantedStructure { get; init; } = [];

    [JsonPropertyName("fidelityIssues")]
    public IReadOnlyList<FidelityIssue> FidelityIssues { get; init; } = [];

    [JsonPropertyName("quality")]
    public QualityScores Quality { get; init; } = new(0, 0, 0, 0, 0, false, string.Empty);

    /// <summary>Missed opportunities whose quoted span was actually found in the input.</summary>
    [JsonIgnore]
    public IReadOnlyList<MissedOpportunity> GroundedMisses =>
        [.. MissedOpportunities.Where(m => m.Grounded)];

    /// <summary>Unwarranted structure whose quoted span was actually found in the output.</summary>
    [JsonIgnore]
    public IReadOnlyList<UnwarrantedStructure> GroundedUnwarranted =>
        [.. UnwarrantedStructure.Where(u => u.Grounded)];

    /// <summary>Fidelity issues whose quoted span was actually found where it was claimed.</summary>
    [JsonIgnore]
    public IReadOnlyList<FidelityIssue> GroundedFidelity =>
        [.. FidelityIssues.Where(f => f.Grounded)];

    /// <summary>Every finding, grounded or not, for the hallucination rate.</summary>
    [JsonIgnore]
    public int FindingCount =>
        MissedOpportunities.Count + UnwarrantedStructure.Count + FidelityIssues.Count;

    /// <summary>Findings whose span could not be located. The judge's own error rate.</summary>
    [JsonIgnore]
    public int UngroundedCount =>
        FindingCount - GroundedMisses.Count - GroundedUnwarranted.Count - GroundedFidelity.Count;
}

/// <summary>
/// One judged cell, written as a line of the judge results file.
/// </summary>
/// <remarks>
/// A separate file from the generation results rather than a column added to them. The judge is an
/// opt-in second pass that costs as much as the run it grades, so it has to be re-runnable,
/// resumable and discardable on its own, and a generation results file has to stay readable by a
/// report that was produced before any judging happened.
/// </remarks>
internal sealed record JudgeCell
{
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("actionId")]
    public required string ActionId { get; init; }

    /// <summary>The deployment that produced the output being judged.</summary>
    [JsonPropertyName("generatorDeployment")]
    public required string GeneratorDeployment { get; init; }

    /// <summary>The deployment that produced this verdict. Never the same one.</summary>
    [JsonPropertyName("judgeDeployment")]
    public required string JudgeDeployment { get; init; }

    /// <summary>Schema and prompt generation, so two incomparable verdicts cannot be averaged.</summary>
    [JsonPropertyName("judgeVersion")]
    public string JudgeVersion { get; init; } = JudgeSchema.Version;

    /// <summary>
    /// SHA-256 over everything the judge was shown. Two cells with the same hash have the same
    /// answer, so the second one is served from the first rather than paid for again.
    /// </summary>
    [JsonPropertyName("contentHash")]
    public required string ContentHash { get; init; }

    /// <summary>True when this row was copied from another row with the same content hash.</summary>
    [JsonPropertyName("cached")]
    public bool Cached { get; init; }

    /// <summary>Null on success; the transport, schema or parse failure otherwise.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("verdict")]
    public JudgeVerdict? Verdict { get; init; }

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("inputTokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("reasoningTokens")]
    public long? ReasoningTokens { get; init; }

    [JsonPropertyName("completedUtc")]
    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The cell key used for resume. Identical to the generation cell's key.</summary>
    [JsonIgnore]
    public string Key => Runner.CellResult.CellKey(ScenarioId, ActionId);
}
