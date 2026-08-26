using System.Text.Json;

namespace Scribe.StyleEval.Judge;

/// <summary>
/// Turns the judge's JSON answer into a <see cref="JudgeVerdict"/>, and marks every finding as
/// grounded or not.
/// </summary>
/// <remarks>
/// <para>
/// The service is asked for strict schema conformance, so this parser should never have to work
/// hard. It is written defensively anyway: a refusal, a truncated answer at the output token
/// ceiling, or a service that quietly relaxed strict mode all arrive here as text, and a judge pass
/// that throws on one malformed cell out of ten thousand is a judge pass nobody finishes.
/// </para>
/// <para>
/// Grounding is applied here rather than in the report because the report should not be able to
/// change what counted. A finding whose quoted span cannot be found in the text it was attributed to
/// is stored with <c>grounded: false</c> and is excluded from every rate the report computes.
/// </para>
/// </remarks>
internal static class JudgeVerdictParser
{
    /// <summary>What went wrong, or null and a verdict.</summary>
    public static (JudgeVerdict? Verdict, string? Error) Parse(string? answer, string input, string output)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return (null, "the judge returned nothing");
        }

        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (null, $"the judge returned no JSON object: {Checks.TextTools.Clip(answer, 200)}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(answer[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            return (null, $"the judge's JSON did not parse: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "the judge's answer was not a JSON object");
            }

            var verdict = new JudgeVerdict
            {
                StructureVerdict = String(root, "structureVerdict"),
                MissedOpportunities = [.. ReadArray(root, "missedOpportunities").Select(e => ReadMissed(e, input))],
                UnwarrantedStructure = [.. ReadArray(root, "unwarrantedStructure").Select(e => ReadUnwarranted(e, output))],
                FidelityIssues = [.. ReadArray(root, "fidelityIssues").Select(e => ReadFidelity(e, input, output))],
                Quality = ReadQuality(root),
            };

            return (verdict, null);
        }
    }

    private static MissedOpportunity ReadMissed(JsonElement element, string input)
    {
        var span = String(element, "inputSpan");
        return new MissedOpportunity(
            String(element, "kind"),
            span,
            ReadSeverity(element),
            String(element, "detectionSignal"),
            ReadStance(element),
            String(element, "explanation"))
        {
            Grounded = Grounding.IsGrounded(span, input),
        };
    }

    private static UnwarrantedStructure ReadUnwarranted(JsonElement element, string output)
    {
        var span = String(element, "outputSpan");
        return new UnwarrantedStructure(
            String(element, "kind"),
            span,
            ReadSeverity(element),
            String(element, "restraintRule"),
            String(element, "explanation"))
        {
            Grounded = Grounding.IsGrounded(span, output),
        };
    }

    private static FidelityIssue ReadFidelity(JsonElement element, string input, string output)
    {
        var type = String(element, "type");
        var inputSpan = String(element, "inputSpan");
        var outputSpan = String(element, "outputSpan");

        // Which side carries the evidence depends on the claim. Something ADDED can only be quoted
        // from the output, something DROPPED only from the input, and anything else should be
        // quotable from both, so requiring the input side is the strict reading.
        var grounded = type switch
        {
            "added" => Grounding.IsGrounded(outputSpan, output),
            "dropped" => Grounding.IsGrounded(inputSpan, input),
            _ => Grounding.IsGrounded(inputSpan, input) || Grounding.IsGrounded(outputSpan, output),
        };

        return new FidelityIssue(type, inputSpan, outputSpan, ReadSeverity(element), String(element, "explanation"))
        {
            Grounded = grounded,
        };
    }

    private static QualityScores ReadQuality(JsonElement root)
    {
        if (!root.TryGetProperty("quality", out var quality) || quality.ValueKind != JsonValueKind.Object)
        {
            return new QualityScores(0, 0, 0, 0, 0, false, string.Empty);
        }

        return new QualityScores(
            Score(quality, "goal"),
            Score(quality, "register"),
            Score(quality, "clarity"),
            Score(quality, "fidelity"),
            Score(quality, "overall"),
            quality.TryGetProperty("wouldShipAsIs", out var ship) && ship.ValueKind == JsonValueKind.True,
            String(quality, "verdict"));
    }

    private static Severity ReadSeverity(JsonElement element) => String(element, "severity") switch
    {
        "minor" => Severity.Minor,
        "moderate" => Severity.Moderate,
        "major" => Severity.Major,
        _ => Severity.Unknown,
    };

    private static GroundTruthStance ReadStance(JsonElement element) => String(element, "groundTruth") switch
    {
        "confirms" => GroundTruthStance.Confirms,
        "silent" => GroundTruthStance.Silent,
        "contradicts" => GroundTruthStance.Contradicts,
        _ => GroundTruthStance.Unknown,
    };

    private static IEnumerable<JsonElement> ReadArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// A 0 to 100 score. Clamped here because the schema cannot carry a range: strict structured
    /// output rejects <c>minimum</c> and <c>maximum</c>, so the bound has to be enforced on arrival.
    /// </summary>
    private static int Score(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, 0, 100)
            : 0;
}
