using Scribe.Core.Cleanup;

namespace Scribe.Evals;

/// <summary>
/// A single style/format eval case: one writing-style instruction applied to a raw transcript, with
/// the markers that prove the model actually followed it. Style scenarios share
/// <see cref="EvalScenarios.RawTranscript"/> so a style swap is the only variable (the suite doubles
/// as a prompt hot-swap proof); condensation scenarios supply their own <see cref="Transcript"/>
/// because the disfluency under test must exist in the input. <see cref="ForbiddenPatterns"/> are
/// regexes that must NOT match the output — e.g. the discarded half of a spoken self-correction.
/// </summary>
internal sealed record EvalScenario(
    string Name,
    string WritingStyle,
    IReadOnlyList<string> MarkerPatterns,
    int MinMarkersToPass,
    bool RequireChanged = true,
    bool CountOccurrences = false,
    string? Transcript = null,
    IReadOnlyList<string>? ForbiddenPatterns = null);

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

        // The two scenarios below eval the SHIPPED default style's semantic-condensation rules
        // (spoken self-correction, redundancy merging), so they run against DefaultWritingStyle
        // verbatim and bring their own transcripts containing the disfluency under test.
        new EvalScenario(
            Name: "Self-correction",
            WritingStyle: CleanupPrompt.DefaultWritingStyle,
            Transcript:
                "um so i sent the quarterly report to the finance team on wednesday i mean thursday " +
                "and uh we should schedule the review meeting for next monday no wait next tuesday " +
                "afternoon so everyone has time to read it",
            // The corrected versions must survive...
            MarkerPatterns: [@"\bthursday\b", @"\btuesday\b"],
            MinMarkersToPass: 2,
            // ...and the retracted false starts (and the correction cues themselves) must not.
            ForbiddenPatterns: [@"\bwednesday\b", @"\bmonday\b", @"\bi mean\b", @"\bno wait\b"]),

        new EvalScenario(
            Name: "Redundancy collapse",
            WritingStyle: CleanupPrompt.DefaultWritingStyle,
            Transcript:
                "we need to update the documentation before the release um the docs really need " +
                "updating before we ship you know the documentation has to be updated prior to the " +
                "release and also please remember to tag the version in git",
            // Both distinct tasks must survive the merge...
            MarkerPatterns: [@"updat|documentation|docs", @"\btag\b"],
            MinMarkersToPass: 2,
            // ...but "update" may only be asked for once: a second "updat"/"docs" mention means the
            // model transcribed the repetition instead of merging it into a single statement.
            ForbiddenPatterns: [@"updat[\s\S]*updat", @"(documentation|docs)[\s\S]*(documentation|docs)"]),

        new EvalScenario(
            Name: "Number formatting",
            WritingStyle: CleanupPrompt.DefaultWritingStyle,
            Transcript:
                "the review moved from three p m to four thirty p m on july third and we will need " +
                "twenty three licenses plus eight gigabytes of ram per developer",
            // Times, dates and quantities must land in written form...
            MarkerPatterns: [@"4:30\s*PM", @"July 3", @"\b23\b", @"\b8\s*(GB|gigabytes)"],
            MinMarkersToPass: 3,
            // ...and their spoken spellings must not survive.
            ForbiddenPatterns: [@"four thirty", @"twenty three", @"\bjuly third\b"]),

        new EvalScenario(
            Name: "Paragraph and model spacing",
            WritingStyle: CleanupPrompt.DefaultWritingStyle,
            Transcript:
                "first the release update the desktop build passed validation and rollout starts monday " +
                "separately for customer feedback three teams asked about GPT five point six and we should " +
                "schedule interviews next week",
            MarkerPatterns: [@"GPT-5\.6", @"\r?\n\r?\n"],
            MinMarkersToPass: 2,
            ForbiddenPatterns: [@"GPT\s*-\s*5\.\s+6", @"[.!?][A-Z]"]),

        new EvalScenario(
            Name: "Phonetic narrative repair",
            WritingStyle: CleanupPrompt.DefaultWritingStyle,
            Transcript:
                "umm ay liss wuz gettin tired beside thuh river then she saw a whyt rabit pull a wotch " +
                "from its pockit blah she followed becuz she wuz cure ee us",
            MarkerPatterns: [@"\bAlice\b", @"\bwhite rabbit\b", @"\bwatch\b", @"\bpocket\b", @"\bcurious\b"],
            MinMarkersToPass: 4,
            ForbiddenPatterns: [@"\bumm?\b", @"\bblah\b", @"\bwuz\b", @"\bthuh\b", @"\bcure ee us\b"]),
    ];
}
