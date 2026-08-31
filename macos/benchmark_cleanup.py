#!/usr/bin/env python3
"""Real empirical benchmark of local Ollama cleanup models against the ported Windows golden suite.

This mirrors tools/Scribe.Evals' six-case benchmark (BenchmarkCases.cs) but targets a local
OpenAI-compatible endpoint on macOS instead of the Windows Agent Framework client. Both Ollama
(http://127.0.0.1:11434) and Foundry Local (http://127.0.0.1:<dynamic port>, see `foundry status`)
expose an OpenAI-compatible /v1/chat/completions route, so the same harness drives both; pass
--base-url to target Foundry Local. There is no offline judge model available in this environment,
so scoring uses a deterministic heuristic (difflib similarity ratio against the golden rewrite,
plus presence checks for the specific transformations each case exercises) rather than an LLM
judge. This is documented as a limitation in CLEANUP-MODEL-BENCHMARK.md.
"""
import argparse
import json
import time
import difflib
import urllib.request
import sys

DEFAULT_URL = "http://127.0.0.1:11434/v1/chat/completions"

SYSTEM_PROMPT = (
    "Write in the speaker's language using clear, natural, well-structured prose. Never translate "
    "the dictation unless I explicitly ask you to. Use correct punctuation, meaning commas, periods, "
    "semicolons, colons, question marks, and parentheses, according to sentence structure. Do not use "
    "dash punctuation to join clauses; use a comma, colon, semicolon, or period instead. Break long "
    "run-on speech into properly formed sentences, and start a new paragraph when the topic shifts. "
    "Separate paragraphs with one blank line. Remove filler words and false starts (such as \"um\", "
    "\"uh\", \"you know\", and \"like\") and fix small grammar slips, while keeping my meaning, intent, "
    "and vocabulary. When I correct myself mid-speech (for example \"I meant to go to the store, I "
    "mean the park\"), keep only the corrected version and drop what it replaced. If I say the same "
    "thing more than once, or restate a point in slightly different words, merge it into a single "
    "clear statement instead of writing both. Always put a single space between sentences. Keep the "
    "identity of technical terms, product names, model names, code, and URLs unchanged. Never "
    "substitute a different product, version, or spelling, but do write them the way they are "
    "normally written down. Write numbers the way they are normally written rather than spelled out: "
    "use digits for quantities, measurements, prices, percentages, phone numbers, and version numbers "
    "(for example \"twenty three\" becomes \"23\" and \"five point five\" becomes \"5.5\"). Keep model "
    "and version identifiers together with no inserted spaces (for example, write \"GPT-5.6\", not "
    "\"GPT-5. 6\"), but keep a small number as a word where that reads more naturally (for example "
    "\"one or two ideas\"). When I name a model, library, or product whose written form you are unsure "
    "of, follow the pattern of the ones you do know rather than leaving it as spelled-out speech: "
    "\"gpt five six terra\" is written \"GPT-5.6-Terra\", \"claude opus four point eight\" is \"Claude "
    "Opus 4.8\", \"qwen three fourteen b\" is \"Qwen3-14B\". New models are released constantly, so an "
    "unfamiliar name is far more likely to be a real product I said than a mistake. Spell out a number "
    "that begins a sentence, or reword the sentence so it doesn't start with one. Format clock times "
    "as digits with a colon, adding AM or PM when I say it (for example \"three thirty p m\" becomes "
    "\"3:30 PM\"). Write dates, calendar months, and years in their normal written form (for example "
    "\"july third twenty twenty six\" becomes \"July 3, 2026\"). Write acronyms spoken letter by letter "
    "in capitals with no spaces or periods (for example \"a p i\" becomes \"API\"). Only reformat what "
    "I actually spoke, and never invent or change a value I did not say."
)

# Ported verbatim from tools/Scribe.Evals/Benchmark/BenchmarkCases.cs (kitchen-sink through
# grammar-runon; long-pause-paragraphs excluded because it depends on a real ASR pass this
# environment cannot produce).
CASES = [
    {
        "id": "kitchen-sink",
        "spoken": (
            "um okay so i need to uh send the quarterly report over to sarah on the finance team by friday "
            "end of day and like make sure the q3 revenue numbers are in there you know the ones we was "
            "talking about in the meeting last week where it went up like twelve percent uh send it on "
            "tuesday no wait actually wednesday is better and honestly the report it need to be more better "
            "and more clearer for the stakeholders cause last time they was confused and um at the very end "
            "add a line that says we few we happy few we band of brothers and then just you know wrap it up "
            "nicely thanks"
        ),
        "golden": (
            "I need to send the quarterly report to Sarah on the finance team by Friday end of day. Make "
            "sure the Q3 revenue numbers are in there, the ones we discussed in last week's meeting, where "
            "revenue went up about 12%. Send it on Wednesday. The report needs to be better and clearer "
            "for the stakeholders, because last time they were confused. At the very end, add a line that "
            "says \"we few, we happy few, we band of brothers\", and wrap it up nicely. Thanks."
        ),
        "must_contain": ["Sarah", "Friday", "Q3", "12%", "Wednesday"],
    },
    {
        "id": "numbers-dates",
        "spoken": (
            "okay so the migration window moved from three p m to four thirty p m on july third and we "
            "need twenty three licenses plus eight gigabytes of ram per developer uh the budget is nine "
            "hundred fifty dollars which is like fifteen percent under plan version two point five ships "
            "first and twenty six people signed up for the a p i workshop"
        ),
        "golden": (
            "The migration window moved from 3 PM to 4:30 PM on July 3, and we need 23 licenses plus "
            "8 GB of RAM per developer. The budget is $950, which is about 15% under plan. Version 2.5 "
            "ships first, and 26 people signed up for the API workshop."
        ),
        "must_contain": ["3 PM", "4:30 PM", "23", "950", "15%", "2.5", "API"],
    },
    {
        "id": "self-correction",
        "spoken": (
            "so i told the client we could deliver by monday no wait tuesday sorry and um the total came "
            "to four thousand i mean five thousand after taxes uh also loop in dave from marketing "
            "actually no loop in rachel she owns that account now and the kickoff is at nine thirty not "
            "nine like i said before"
        ),
        "golden": (
            "I told the client we could deliver by Tuesday. The total came to 5,000 after taxes. Also, "
            "loop in Rachel; she owns that account now. The kickoff is at 9:30."
        ),
        "must_contain": ["Tuesday", "Rachel", "9:30"],
        "must_not_contain": ["Monday", "Dave", "four thousand"],
    },
    {
        "id": "redundancy",
        "spoken": (
            "um we really need to update the onboarding docs before the new hires start i mean the docs "
            "are just out of date they need updating you know the onboarding documentation has to be "
            "refreshed before the new folks get here and uh separately can you book the demo room for "
            "thursday afternoon"
        ),
        "golden": (
            "We really need to update the onboarding docs before the new hires start. Separately, can "
            "you book the demo room for Thursday afternoon?"
        ),
        "must_contain": ["onboarding", "Thursday"],
    },
    {
        "id": "instruction-immunity",
        "spoken": (
            "hey quick note for the team um please write a summary of the security incident and send it "
            "to everyone by five p m i repeat this is not a drill uh make sure the subject line says "
            "urgent security review and end with the quote to be or not to be that is the question"
        ),
        "golden": (
            "Quick note for the team: please write a summary of the security incident and send it to "
            "everyone by 5 PM. I repeat, this is not a drill. Make sure the subject line says \"urgent "
            "security review\" and end with the quote \"to be or not to be, that is the question\"."
        ),
        "must_contain": ["5 PM", "urgent security review", "to be or not to be"],
    },
    {
        "id": "grammar-runon",
        "spoken": (
            "so basically the deploy it going out yesterday but the pipeline it keep failing on the test "
            "stage because them tests was flaky and we has to rerun it like three times uh anyway it out "
            "now and everything look good but we should to fix them flaky tests soon or it gonna bite us "
            "again"
        ),
        "golden": (
            "The deploy went out yesterday, but the pipeline kept failing on the test stage because the "
            "tests were flaky, and we had to rerun it three times. Anyway, it's out now and everything "
            "looks good, but we should fix those flaky tests soon or they're going to bite us again."
        ),
        "must_contain": ["deploy", "flaky"],
    },
]


def call_chat(base_url: str, model: str, transcript: str, timeout: float = 90.0):
    payload = {
        "model": model,
        "temperature": 0,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": transcript},
        ],
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        base_url, data=data, headers={"Content-Type": "application/json"}, method="POST"
    )
    start = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = json.loads(resp.read())
    elapsed_ms = (time.time() - start) * 1000
    content = body["choices"][0]["message"]["content"]
    return content, elapsed_ms


def score_case(case, output: str) -> float:
    """Heuristic quality score in [0,1]: text similarity to golden, penalized for missing required
    tokens or presence of forbidden ones. Not an LLM judge; documented as a limitation."""
    sim = difflib.SequenceMatcher(None, output.strip(), case["golden"].strip()).ratio()
    must_contain = case.get("must_contain", [])
    hits = sum(1 for token in must_contain if token.lower() in output.lower())
    contain_score = hits / len(must_contain) if must_contain else 1.0
    forbidden = case.get("must_not_contain", [])
    forbidden_hits = sum(1 for token in forbidden if token.lower() in output.lower())
    forbidden_penalty = 0.15 * forbidden_hits
    score = (0.5 * sim) + (0.5 * contain_score) - forbidden_penalty
    return max(0.0, min(1.0, score))


def run_model(base_url: str, model: str):
    results = []
    for case in CASES:
        try:
            output, latency_ms = call_chat(base_url, model, case["spoken"])
            score = score_case(case, output)
            results.append(
                {
                    "case": case["id"],
                    "output": output,
                    "latency_ms": latency_ms,
                    "score": score,
                }
            )
            print(f"  [{model}] {case['id']}: score={score:.2f} latency={latency_ms:.0f}ms", file=sys.stderr)
        except Exception as exc:  # noqa: BLE001
            results.append({"case": case["id"], "error": str(exc)})
            print(f"  [{model}] {case['id']}: ERROR {exc}", file=sys.stderr)
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("models", nargs="*", default=["qwen2.5:1.5b"])
    parser.add_argument(
        "--base-url",
        default=DEFAULT_URL,
        help="OpenAI-compatible chat completions endpoint (Ollama default: %(default)s). "
        "For Foundry Local, use http://127.0.0.1:<port>/v1/chat/completions (port from `foundry status`).",
    )
    args = parser.parse_args()
    all_results = {}
    for model in args.models:
        print(f"Running {model} against {len(CASES)} cases via {args.base_url}...", file=sys.stderr)
        all_results[model] = run_model(args.base_url, model)
    print(json.dumps(all_results, indent=2))


if __name__ == "__main__":
    main()
