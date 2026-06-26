namespace Scribe.Evals;

/// <summary>
/// A single style/format eval case: one writing-style instruction applied to a shared raw transcript,
/// with the markers that prove the model actually followed it. Because every scenario starts from the
/// same <see cref="EvalScenarios.RawTranscript"/>, the suite doubles as a prompt hot-swap proof —
/// swapping the style must change the output and hit different markers.
/// </summary>
internal sealed record EvalScenario(
    string Name,
    string WritingStyle,
    IReadOnlyList<string> MarkerPatterns,
    int MinMarkersToPass,
    bool RequireChanged = true,
    bool CountOccurrences = false);

internal static class EvalScenarios
{
    /// <summary>
    /// Shared raw, ASR-style transcript (no punctuation, fillers, run-on) used by every scenario so a
    /// style swap is the only variable changing between runs.
    /// </summary>
    internal const string RawTranscript =
        "um so basically i need you to send the quarterly report to the finance team by friday and " +
        "uh make sure the revenue numbers from the third quarter are included you know the ones we " +
        "talked about in the meeting last week";

    internal static IReadOnlyList<EvalScenario> All { get; } =
    [
        new EvalScenario(
            Name: "Pirate",
            WritingStyle:
                "Rewrite the message in the voice of a swashbuckling pirate. Use pirate vocabulary " +
                "such as 'ahoy', 'matey', 'arr', 'aye', and 'ye' while preserving the original meaning.",
            MarkerPatterns: [@"\bahoy\b", @"\bmatey\b", @"\barr+\b", @"\baye\b", @"\bye\b", @"\bavast\b", @"\bhearties\b"],
            MinMarkersToPass: 2),

        new EvalScenario(
            Name: "Old English",
            WritingStyle:
                "Rewrite the message in formal Early Modern (Shakespearean) English. Use archaic " +
                "second-person forms such as 'thee', 'thou', 'thy', 'hath', and 'doth'.",
            MarkerPatterns: [@"\bthee\b", @"\bthou\b", @"\bthy\b", @"\bthine\b", @"\bhath\b", @"\bdoth\b", @"\bprithee\b"],
            MinMarkersToPass: 2),

        new EvalScenario(
            Name: "French translation",
            WritingStyle: "Translate the message into French. Respond only with the French translation.",
            MarkerPatterns:
            [
                @"\ble\b", @"\bla\b", @"\bles\b", @"\bje\b", @"\best\b", @"\bet\b",
                @"\bvous\b", @"\bnous\b", @"\bpour\b", @"\brapport\b", @"\béquipe\b",
            ],
            MinMarkersToPass: 3),

        new EvalScenario(
            Name: "Bulleted to-do list",
            WritingStyle:
                "Reformat the message as a concise bulleted to-do list. Put each distinct action item " +
                "on its own line starting with a dash.",
            MarkerPatterns: [@"(?m)^\s*[-*\u2022]\s+\S"],
            MinMarkersToPass: 2,
            CountOccurrences: true),
    ];
}
