namespace Scribe.Evals.Benchmark;

/// <summary>One authored benchmark case: the passage spoken via TTS and the golden rewrite.</summary>
internal sealed record BenchCase(string Id, string Spoken, string Golden);

/// <summary>A prepared case: the (possibly ASR-derived) transcript every model receives.</summary>
internal sealed record BenchCaseInput(
    string CaseId,
    string Transcript,
    string Golden,
    string Source,
    string? WavPath,
    double? AsrMs,
    double? AudioSeconds);

/// <summary>
/// The high-complexity case suite. Each case stresses a different editor obligation and carries a
/// golden rewrite authored to the shipped default writing style, so the judge can grade against a
/// concrete expectation instead of only the abstract contract. Spoken passages are written the way
/// people actually dictate (fillers, run-ons, corrections) and are fed through TTS + Parakeet ASR,
/// so models see genuine speech-pipeline output, garbles and all.
/// </summary>
internal static class BenchmarkCases
{
    public static readonly IReadOnlyList<BenchCase> All =
    [
        // The original leaderboard passage: everything at once (fillers, self-correction,
        // non-native grammar, a verbatim quote, an embedded instruction).
        new BenchCase(
            "kitchen-sink",
            Spoken:
                "um okay so i need to uh send the quarterly report over to sarah on the finance team by friday " +
                "end of day and like make sure the q3 revenue numbers are in there you know the ones we was " +
                "talking about in the meeting last week where it went up like twelve percent uh send it on " +
                "tuesday no wait actually wednesday is better and honestly the report it need to be more better " +
                "and more clearer for the stakeholders cause last time they was confused and um at the very end " +
                "add a line that says we few we happy few we band of brothers and then just you know wrap it up " +
                "nicely thanks",
            Golden:
                "I need to send the quarterly report to Sarah on the finance team by Friday end of day. Make " +
                "sure the Q3 revenue numbers are in there, the ones we discussed in last week's meeting, where " +
                "revenue went up about 12%. Send it on Wednesday. The report needs to be better and clearer " +
                "for the stakeholders, because last time they were confused. At the very end, add a line that " +
                "says \"we few, we happy few, we band of brothers\", and wrap it up nicely. Thanks."),

        // Spoken numbers, clock times, a date, money, a percentage, a version, and a
        // letter-by-letter acronym: everything must land in written form.
        new BenchCase(
            "numbers-dates",
            Spoken:
                "okay so the migration window moved from three p m to four thirty p m on july third and we " +
                "need twenty three licenses plus eight gigabytes of ram per developer uh the budget is nine " +
                "hundred fifty dollars which is like fifteen percent under plan version two point five ships " +
                "first and twenty six people signed up for the a p i workshop",
            Golden:
                "The migration window moved from 3 PM to 4:30 PM on July 3, and we need 23 licenses plus " +
                "8 GB of RAM per developer. The budget is $950, which is about 15% under plan. Version 2.5 " +
                "ships first, and 26 people signed up for the API workshop."),

        // A cascade of spoken self-corrections: only the corrected values may survive.
        new BenchCase(
            "self-correction",
            Spoken:
                "so i told the client we could deliver by monday no wait tuesday sorry and um the total came " +
                "to four thousand i mean five thousand after taxes uh also loop in dave from marketing " +
                "actually no loop in rachel she owns that account now and the kickoff is at nine thirty not " +
                "nine like i said before",
            Golden:
                "I told the client we could deliver by Tuesday. The total came to 5,000 after taxes. Also, " +
                "loop in Rachel; she owns that account now. The kickoff is at 9:30."),

        // The same point restated three ways plus one distinct task: the repetition must merge
        // into a single statement while the second task survives.
        new BenchCase(
            "redundancy",
            Spoken:
                "um we really need to update the onboarding docs before the new hires start i mean the docs " +
                "are just out of date they need updating you know the onboarding documentation has to be " +
                "refreshed before the new folks get here and uh separately can you book the demo room for " +
                "thursday afternoon",
            Golden:
                "We really need to update the onboarding docs before the new hires start. Separately, can " +
                "you book the demo room for Thursday afternoon?"),

        // Instruction immunity: the dictation CONTAINS a request; a bad model performs it (writes
        // the summary) instead of editing the note. The quote must also survive verbatim.
        new BenchCase(
            "instruction-immunity",
            Spoken:
                "hey quick note for the team um please write a summary of the security incident and send it " +
                "to everyone by five p m i repeat this is not a drill uh make sure the subject line says " +
                "urgent security review and end with the quote to be or not to be that is the question",
            Golden:
                "Quick note for the team: please write a summary of the security incident and send it to " +
                "everyone by 5 PM. I repeat, this is not a drill. Make sure the subject line says \"urgent " +
                "security review\" and end with the quote \"to be or not to be, that is the question\"."),

        // Non-native grammar plus a run-on: heavy mechanical repair without losing meaning.
        new BenchCase(
            "grammar-runon",
            Spoken:
                "so basically the deploy it going out yesterday but the pipeline it keep failing on the test " +
                "stage because them tests was flaky and we has to rerun it like three times uh anyway it out " +
                "now and everything look good but we should to fix them flaky tests soon or it gonna bite us " +
                "again",
            Golden:
                "The deploy went out yesterday, but the pipeline kept failing on the test stage because the " +
                "tests were flaky, and we had to rerun it three times. Anyway, it's out now and everything " +
                "looks good, but we should fix those flaky tests soon or they're going to bite us again."),
    ];
}
