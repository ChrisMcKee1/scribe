namespace Scribe.Evals.Benchmark;

/// <summary>One authored benchmark case: the passage spoken via TTS and the golden rewrite.</summary>
internal sealed record BenchCase(
    string Id,
    string Spoken,
    string Golden,
    string? SpeechMarkup = null,
    string? TranscriptOverride = null);

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

        // Long acoustic pauses exercise the real ASR path. The cleanup model receives only the final
        // transcript, so this reveals whether Parakeet preserves enough punctuation/context for the
        // frontier prompt to recover paragraph structure.
        new BenchCase(
            "long-pause-paragraphs",
            Spoken:
                "first the release update the desktop build passed validation and rollout starts monday " +
                "separately for customer feedback three teams asked for a simpler onboarding guide and " +
                "we should schedule interviews next week",
            Golden:
                "First, the release update: the desktop build passed validation, and rollout starts Monday.\n\n" +
                "Separately, for customer feedback, three teams asked for a simpler onboarding guide, " +
                "and we should schedule interviews next week.",
            SpeechMarkup:
                "first the release update the desktop build passed validation and rollout starts monday" +
                "<silence msec=\"2800\"/>" +
                "separately for customer feedback three teams asked for a simpler onboarding guide and " +
                "we should schedule interviews next week"),

        new BenchCase(
            "model-version-spacing",
            Spoken:
                "the GPT five point six model should handle the complete transcript before it writes the " +
                "answer then version two point five remains available for comparison",
            Golden:
                "The GPT-5.6 model should handle the complete transcript before it writes the answer. " +
                "Version 2.5 remains available for comparison.",
            SpeechMarkup:
                "the G P T five point six model should handle the complete transcript before it writes " +
                "the answer<silence msec=\"1800\"/>then version two point five remains available for comparison"),

        // The WAV contains ordinary dialogue, while the model receives a deliberately phonetic
        // transcript. This isolates whether cleanup can use sentence context to recover homophones.
        new BenchCase(
            "dialogue-phonetic",
            Spoken:
                "look i know this sounds strange but when i got to the station they were already leaving " +
                "their bags were by the door and claire said you're too late we can't wait another hour " +
                "so i told her i'll meet them at the old theater after eight",
            Golden:
                "Look, I know this sounds strange, but when I got to the station, they were already leaving. " +
                "Their bags were by the door, and Claire said, \"You're too late. We can't wait another " +
                "hour.\" So I told her, \"I'll meet them at the old theater after 8.\"",
            TranscriptOverride:
                "look eye no this sounds strange butt when eye got two the station they were all ready " +
                "leaving there bags were buy the door and claire said yore too late wee cant weight another " +
                "our sew eye told her aisle meat them at the old theater after ate"),

        new BenchCase(
            "story-phonetic",
            Spoken:
                "the rain had stopped by dawn and the road through the valley shone like silver maria could " +
                "hear the church bell beyond the hill but she knew there was no time to turn back",
            Golden:
                "The rain had stopped by dawn, and the road through the valley shone like silver. Maria could " +
                "hear the church bell beyond the hill, but she knew there was no time to turn back.",
            TranscriptOverride:
                "the reign had stopped buy dawn and the rode threw the valley shown like silver maria could " +
                "here the church belle beyond the hill butt she new there was no thyme two turn back"),

        new BenchCase(
            "colloquial-phonetic",
            Spoken:
                "you should have seen his face when i said we were not taking the highway he looked at me and " +
                "said are you serious we've got twenty minutes then jen laughed and told him relax we'll make it",
            Golden:
                "You should have seen his face when I said we were not taking the highway. He looked at me and " +
                "said, \"Are you serious? We've got 20 minutes.\" Then Jen laughed and told him, \"Relax, " +
                "we'll make it.\"",
            TranscriptOverride:
                "you should of seen his face when eye said wee were knot taking the highway he looked at me " +
                "and said are yew serious weve got twenty minutes then jen laughed and told hymn relax well " +
                "make it"),

        // Newly authored adaptation of the public-domain Alice's Adventures in Wonderland narrative,
        // Project Gutenberg eBook 11 (https://www.gutenberg.org/ebooks/11). Unlike the transcript-only
        // phonetic cases above, this puts pronunciation-like spelling and pauses into the WAV itself.
        new BenchCase(
            "phonetic-wav-narrative",
            Spoken:
                "um alice was getting tired beside the river when she noticed a white rabbit in a blue " +
                "coat hurry past the hedge it pulled a watch from its pocket and said it was late she " +
                "followed because she was curious then after a long pause she found a hallway with many " +
                "locked doors and a small golden key",
            Golden:
                "Alice was getting tired beside the river when she noticed a white rabbit in a blue coat " +
                "hurry past the hedge. It pulled a watch from its pocket and said it was late. She followed " +
                "because she was curious.\n\nAfter a long pause, she found a hallway with many locked doors " +
                "and a small golden key.",
            SpeechMarkup:
                "umm ay liss wuz gettin kinda tired beside thuh river<silence msec=\"700\"/>when she " +
                "noticed a whyt rabit in a bloo coat hurry past thuh hedge<silence msec=\"350\"/>uh it " +
                "pulled a wotch from its pockit and sed it wuz late<silence msec=\"900\"/>blah she " +
                "followed becuz she wuz cure ee us<silence msec=\"3000\"/>then after a long paws she " +
                "found a hall way with many lokt doors and a small gohlden key"),

        // ---- Voice suite -------------------------------------------------------------------------
        // The cases above were authored to the shipped writing style, so they cannot show whether a
        // voice-oriented style is an improvement: none of them contain a regionalism to preserve, a
        // blunt opinion to hedge, or a topic that tempts a model into corporate filler. These do.
        // Their goldens capture the required CONTENT; voice compliance is measured separately and
        // deterministically (banned words, dashes, semicolons, contractions), because a single golden
        // cannot fairly grade two different target voices.

        // Bait for hedging and for a closing summary sentence. A blunt judgment must survive intact.
        new BenchCase(
            "blunt-opinion",
            Spoken:
                "so honestly the vendor demo was bad we are not going with them the pricing is way off " +
                "and their support story is basically nonexistent i mean we would be fixing their " +
                "product for them so lets just tell them no and move on",
            Golden:
                "Honestly, the vendor demo was bad. We're not going with them. The pricing is way off, " +
                "and their support story is nonexistent. We'd be fixing their product for them. Let's " +
                "tell them no and move on."),

        // Regionalisms and lazy speech together: y'all and fixin' to must survive, gonna/wanna/kinda
        // must be written out. A style that sands regional phrasing into business English fails here.
        new BenchCase(
            "regional-voice",
            Spoken:
                "hey yall im fixin to push the change tonight its gonna take about twenty minutes and i " +
                "wanna make sure nobody is deploying at the same time cause thats kinda how we broke it " +
                "last time so just holler at me if youre in there",
            Golden:
                "Hey y'all, I'm fixin' to push the change tonight. It's going to take about 20 minutes, " +
                "and I want to make sure nobody is deploying at the same time, because that's kind of how " +
                "we broke it last time. Just holler at me if you're in there."),

        // A status update is the strongest magnet for machine register: delve, leverage, robust,
        // seamless, "It's important to note", and a However/Moreover opener.
        new BenchCase(
            "corporate-bait",
            Spoken:
                "quick status the migration is done we moved all the accounts over it took longer than " +
                "planned because the old export kept timing out but its finished now the team did good " +
                "work next up is the reporting piece which should be easier",
            Golden:
                "Quick status: the migration is done. We moved all the accounts over. It took longer than " +
                "planned because the old export kept timing out, but it's finished now. The team did good " +
                "work. Next up is the reporting piece, which should be easier."),

        // Two clauses a polishing model reliably joins with an em dash or a semicolon. The shipped
        // style permits both; the candidate bans them outright, so this case separates the two.
        new BenchCase(
            "dash-and-semicolon-bait",
            Spoken:
                "the build is green finally the flaky test was the whole problem we swapped the fixture " +
                "and it settled down the release can go out friday assuming nothing else blows up",
            Golden:
                "The build is green, finally. The flaky test was the whole problem. We swapped the fixture " +
                "and it settled down. The release can go out Friday, assuming nothing else blows up."),

        // Short punchy fragments carrying emphasis. A style that "fixes" fragments into full
        // sentences flattens the voice.
        new BenchCase(
            "fragments",
            Spoken:
                "did you see the new numbers not great down eleven percent month over month we need to " +
                "figure out why before the review on tuesday works for me either way",
            Golden:
                "Did you see the new numbers? Not great. Down 11% month over month. We need to figure out " +
                "why before the review on Tuesday. Works for me either way."),

        // Three listed items invite the rule-of-three cadence and a participial tail
        // ("...ahead of schedule, demonstrating strong alignment").
        new BenchCase(
            "list-cadence",
            Spoken:
                "for the offsite we need the room booked the catering sorted and someone to run the " +
                "afternoon session i can take the room if you handle catering we finished early last " +
                "year so lets aim for that again",
            Golden:
                "For the offsite we need the room booked, the catering sorted, and someone to run the " +
                "afternoon session. I can take the room if you handle catering. We finished early last " +
                "year, so let's aim for that again."),

        // Mixed: a quoted line plus a version identifier plus a self-correction, so the voice suite
        // also catches the regressions the candidate style showed on the original suite.
        new BenchCase(
            "voice-with-values",
            Spoken:
                "tell the team we are shipping version two point four no wait two point five on friday " +
                "and put in the note that says ship it and see what breaks thats the whole message dont " +
                "dress it up",
            Golden:
                "Tell the team we're shipping version 2.5 on Friday. Put in the note that says \"ship it " +
                "and see what breaks\". That's the whole message, don't dress it up."),

        // AI model identifiers spoken as words. These are deliberately NOT in any shipped dictionary
        // library, because the point is generalization: a dictionary can only cover models that
        // existed when it was written, and new ones ship weekly. The editor has to recognize the
        // shape of a model identifier and write it down, rather than leaving spelled-out speech.
        new BenchCase(
            "model-identifiers",
            Spoken:
                "so i benchmarked gpt five point seven atlas against claude opus five point two and " +
                "gemini three point eight pro and the atlas one won on quality but qwen four thirty b " +
                "was way faster we should also try llama five point one seventy b before we decide",
            Golden:
                "I benchmarked GPT-5.7-Atlas against Claude Opus 5.2 and Gemini 3.8 Pro. Atlas won on " +
                "quality, but Qwen4-30B was much faster. We should also try Llama 5.1-70B before we decide."),

        // The failure that prompted the rule: a version spoken without the word "point", mixed with
        // ordinary speech that must not be mangled. "Terra" here is a model suffix, not a noun.
        new BenchCase(
            "model-identifiers-terse",
            Spoken:
                "im running a test for gpt five six terra to see how the text rewrite performance does " +
                "and how well it follows the writing instructions and then ill compare it to five six sol",
            Golden:
                "I'm running a test for GPT-5.6-Terra to see how the text rewrite performance does and how " +
                "well it follows the writing instructions. Then I'll compare it to 5.6-Sol."),

        // --- AI conversation suite -------------------------------------------------------------
        // These run through TTS + Parakeet like every other case, so they measure the real failure:
        // the ASR hands the editor spelled-out model names and mangled acronyms, and the editor has
        // to reconstruct them. Run with and without --glossary-libraries to isolate what the shipped
        // AI libraries actually contribute versus what the prompt recovers on its own.

        // Dense model-name traffic, the way an engineer actually compares models out loud.
        new BenchCase(
            "ai-model-comparison",
            Spoken:
                "so i spent the morning comparing claude opus four point eight against gpt five point six sol " +
                "and gemini three point one pro on our rewrite task and honestly the opus one won on quality " +
                "but it was uh way slower we also tried qwen three thirty two b locally through ollama and " +
                "llama three point three seventy b and both were fine for the easy cases but they fell apart " +
                "on the self correction stuff so i think we stick with the frontier model for now",
            Golden:
                "I spent the morning comparing Claude Opus 4.8 against GPT-5.6-Sol and Gemini 3.1 Pro on our " +
                "rewrite task. Honestly, Opus won on quality, but it was much slower. We also tried Qwen3-32B " +
                "locally through Ollama, and Llama 3.3-70B. Both were fine for the easy cases, but they fell " +
                "apart on the self-correction work, so I think we stick with the frontier model for now."),

        // Architecture vocabulary: acronyms spoken letter by letter, hyphenated compounds, and terms
        // whose written form differs from how they are pronounced.
        new BenchCase(
            "ai-architecture-talk",
            Spoken:
                "the retrieval side is basically r a g with h n s w for the vector index and we rerank with " +
                "b m twenty five before it hits the l l m we chunk at about five hundred tokens with some " +
                "overlap and the embeddings come from text embedding three large uh the agent loop uses m c p " +
                "for tools and we added a human in the loop step before anything writes to production",
            Golden:
                "The retrieval side is basically RAG with HNSW for the vector index, and we rerank with BM25 " +
                "before it hits the LLM. We chunk at about 500 tokens with some overlap, and the embeddings " +
                "come from text-embedding-3-large. The agent loop uses MCP for tools, and we added a " +
                "human-in-the-loop step before anything writes to production."),

        // Training and quantization vocabulary, which is where casing conventions are least obvious.
        new BenchCase(
            "ai-training-vocabulary",
            Spoken:
                "we did a lora fine tuning run on phi four mini with about eight thousand examples and then " +
                "quantized it to int four using g g u f so it fits on the laptop the eval was l l m as judge " +
                "against a golden set and we tracked r o u g e score and a task adherence rubric it took two " +
                "hours on a single g p u which is fine",
            Golden:
                "We did a LoRA fine-tuning run on Phi-4-mini with about 8,000 examples, then quantized it to " +
                "INT4 using GGUF so it fits on the laptop. The eval was LLM-as-judge against a golden set, and " +
                "we tracked ROUGE score and a task adherence rubric. It took two hours on a single GPU, which " +
                "is fine."),

        // Mixed conversation: model names, an org name, a self-correction, and a number, all at once.
        // This is the closest to how the tool is actually used, so it is the one that matters most.
        new BenchCase(
            "ai-planning-conversation",
            Spoken:
                "okay so for the demo on thursday no sorry wednesday we want to show the foundry local path " +
                "running phi four mini on device and then the cloud path hitting gpt five point four mini " +
                "through microsoft foundry and the whole point is that the audio never leaves the machine " +
                "uh latency was like eight hundred milliseconds on device versus two point one seconds for " +
                "the cloud round trip which is still under our budget",
            Golden:
                "For the demo on Wednesday we want to show the Foundry Local path running Phi-4-mini on " +
                "device, then the cloud path hitting GPT-5.4-mini through Microsoft Foundry. The whole point " +
                "is that the audio never leaves the machine. Latency was about 800 milliseconds on device " +
                "versus 2.1 seconds for the cloud round trip, which is still under our budget."),
    ];

    /// <summary>
    /// The AI-conversation subset, used to measure what the shipped AI dictionary libraries add on
    /// top of the prompt. Exposed so a run can select them without hardcoding ids at the call site.
    /// </summary>
    public static readonly IReadOnlyList<string> AiSuiteIds =
    [
        "model-identifiers",
        "model-identifiers-terse",
        "ai-model-comparison",
        "ai-architecture-talk",
        "ai-training-vocabulary",
        "ai-planning-conversation",
    ];
}
