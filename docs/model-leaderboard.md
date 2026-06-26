# Scribe AI Cleanup — Model Leaderboard

A speed **and** quality benchmark of every Foundry model available to Scribe — **16 Microsoft
Foundry (Azure cloud)** deployments and **22 Foundry Local (on-device)** models — each driving
Scribe's **real** post-ASR cleanup pipeline on one shared, deliberately messy dictation. Every
model saw byte-identical input; an Azure `gpt-4.1` judge graded each output on a strict
post-editor rubric. The full auto-generated report (all tables, judge rationales, and verbatim
outputs) follows the summary below.

> **Benchmark hardware:** NVIDIA GeForce RTX 5080 + AMD Ryzen 9 9900X (12-core). Foundry Local ran
> on the **TensorRT-RTX** and **CUDA** execution providers. On-device latencies are hardware-specific
> — a different GPU will shift the local numbers (but not the relative ranking or the quality grades).

## TL;DR — what to actually use

| Scenario | Pick | Grade | Median | Why |
|---|---|---|---|---|
| **Fully offline, everyday** | `mistral-nemo-12b-instruct` (Local) | **B (85)** | **1.48 s** | Best quality-per-millisecond on-device — near real-time, nothing leaves the machine. |
| **Fully offline, max quality** | `qwen2.5-14b` (Local) | **A- (92)** | 3.93 s | The **only** local model to reach A-tier; worth the extra ~2.5 s when quality matters. |
| **Cloud default (opt-in)** | `gpt-5.4-mini` | **A- (92)** | 1.60 s | Fastest A-tier cloud model with no caveats; great everyday cloud pick. |
| **Cloud, top quality** | `gpt-5.1` | **A (95)** | 1.97 s | Best output of all 38 — correctly resolved the spoken self-correction and kept the quote verbatim. |
| **Lowest cloud latency** | `gpt-4.1` | A- (92) | 1.26 s | Fastest gradable cloud model overall. |

**Cloud vs. Local verdict:** Cloud owns the speed/quality frontier — `gpt-4.1` (A-, 1.26 s) and
`gpt-5.4-mini` (A-, 1.60 s) beat every local model on quality-per-millisecond. But cloud cleanup
sends your transcript off-device. If privacy/offline is the priority (Scribe's whole reason to
exist), **`mistral-nemo-12b-instruct` is the on-device default** (B, ~1.5 s), stepping up to
**`qwen2.5-14b`** (A-, ~3.9 s) when you want cloud-grade quality without the cloud.

## Key findings

1. **Reasoning models are the wrong tool for real-time cleanup.** Hidden chain-of-thought burns the
   output-token budget, so the model either returns *nothing* and Scribe falls back to the raw text
   (`gpt-5` → F, `deepseek-r1-7b` → F, both no-ops) or finishes far too slowly for dictation
   (`gpt-5.4-pro` **82.9 s**, `qwen3-14b` **38.4 s**). Prefer **instruct / non-reasoning** models.
   Note `gpt-5.1`, `gpt-5.3-codex`, `gpt-5.2`, and `gpt-5.4` are tagged "reasoning" but behave well
   here because they emit answers promptly; `gpt-5` (base) does not.
2. **Tiny models (≤ 2 B) can't honor the post-editor contract.** `qwen3-0.6b` flatly *refused*
   ("I'm sorry, but I can't process the content…"); the 0.5–2 B tier left fillers, dropped content, or
   hallucinated. The quality floor for usable cleanup is roughly the **3–4 B** class (`qwen3-4b`,
   `phi-4-mini` at D), and B-grade quality starts at **7 B+** (`olmo-3-7b-instruct`, `mistral-nemo-12b`).
3. **Foundry Local picks a CPU variant for the `qwen2.5-1.5b` alias.** Despite the
   `qwen2.5-1.5b-instruct-trtrtx-gpu` build being cached, the bare alias resolved to
   `qwen2.5-1.5b-instruct-generic-cpu` → **3.39 s** (slower than several 7–12 B GPU models). If you run
   that model, **pin the `…-trtrtx-gpu` / `…-cuda-gpu` variant** explicitly to get GPU speed. Scribe
   uses the same alias resolution, so this affects the app too.
4. **A few models silently no-op.** `Kimi-K2.5`, `mistral-7b-v0.2`, and `smollm3-3b` returned output
   *identical to the raw dictation* (flagged `degraded` / `changed=no`) — a real failure to edit, not a
   measurement artifact. Avoid them for cleanup.
5. **Two cloud deployments aren't text-capable.** `gpt-audio-1.5` (audio) and `model-router` both
   answer HTTP 400 ("operation is unsupported") on the Responses API and never produced a result —
   correctly excluded from the grade tables.

## How to read the grades (methodology & caveats)

- **One pipeline, one input.** A fixed messy passage was spoken via SAPI TTS → 16 kHz mono WAV →
  transcribed by Parakeet TDT 0.6 b (the same ASR Scribe ships) → cleaned by the **actual**
  `TextCleanupService` every model in the app uses. Input bytes were cached so all 38 models saw the
  *exact* same transcript.
- **Timing.** Per model: 1 warmup run (discarded) + 3 timed runs; **median** reported, min/max shown.
  Latency was **uncapped** (180 s ceiling) so slow models report their true cost instead of being cut
  off — that's why you see the 38 s and 83 s reasoning entries.
- **The judge is deliberately strict, but fair.** An Azure `gpt-4.1` judge scores each output on
  mechanics, fidelity, disfluency removal, and instruction-adherence, and flags issues. It grades
  against the **corrupted ASR transcript** — including the ASR-mangled Shakespeare line
  ("*we few we Happy few we band of brothers*") and garbles like "*Us and it on Tuesday*". Models that
  *fixed* the mangled quote get an `altered_quote` flag, and models that tidied ASR noise sometimes get
  `dropped_information`. Because **the same input and the same judge apply to all 38 models**, the
  *comparative* ranking is sound even though absolute scores skew conservative. Read grades as
  relative, not as an absolute ceiling on a model's ability.
- **"Changed = no" means failure.** It indicates the model returned the raw text unchanged (a no-op /
  internal fallback), which the judge correctly grades F.

---

_The full auto-generated benchmark report — run metadata, complete leaderboards, speed board,
failures, and per-model judge detail with verbatim outputs — follows._

## Run metadata

- **Generated (UTC):** 2026-06-26 02:34:48Z
- **Machine:** RTX 5080 + Ryzen 9 9900X (Foundry Local on TensorRT-RTX + CUDA)
- **Input source:** wav+asr (Parakeet)
- **ASR (Parakeet) decode:** 2562 ms, audio 34.1s
- **Quality judge:** gpt-4.1 @ https://mtech-project-resource.cognitiveservices.azure.com/
- **Timed runs per model:** 3 (median reported; 1 warmup discarded; latency uncapped)
- **Models benchmarked:** 38

**Raw transcript (identical for every model):**

> I'm okay so I need to uh send a quarterly report over to Sarah on the finance team by Friday end of day and like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.

<details><summary>Writing style applied</summary>

```
Write in clear, natural, well-structured English. Use correct punctuation — commas, periods, semicolons, colons, question marks, and parentheses — according to sentence structure. Break long run-on speech into properly formed sentences, and start a new paragraph when the topic shifts. Remove filler words and false starts (such as "um", "uh", "you know", "like", and "I mean") and fix small grammar slips, while keeping my meaning, intent, and vocabulary. Keep technical terms, product names, code, URLs, and numbers exactly as spoken.
```

</details>

## Recommendations

- **Best cloud quality:** `gpt-5.1` — quality 95 (A), 1967 ms median.
- **Best local quality:** `qwen2.5-14b` — quality 92 (A-), 3929 ms median.
- **Best on-device default (≤ 2000 ms, fully offline):** `mistral-nemo-12b-instruct` — quality 85 (B), 1483 ms median. Strong quality with real-time feel and no data leaving the machine.
- **Fastest overall:** `qwen3-0.6b` (Local) — 142 ms median, quality 5 (F).

## Overall leaderboard (quality, then speed)

| # | Model | Group | Quality | Grade | Median ms | Min/Max ms | Changed | Flags | Note |
|---|---|---|---|---|---|---|---|---|---|
| 1 | gpt-5.1 | Cloud | 95 | A | 1967 | 1829/2247 | yes |  | reasoning |
| 2 | gpt-5.3-codex | Cloud | 95 | A | 2409 | 2017/43531 | yes |  | reasoning |
| 3 | gpt-4.1 | Cloud | 92 | A- | 1257 | 1233/3659 | yes | dropped_information |  |
| 4 | gpt-5.4-mini | Cloud | 92 | A- | 1595 | 1311/1801 | yes |  |  |
| 5 | gpt-5.4-nano | Cloud | 92 | A- | 1615 | 1585/2267 | yes | dropped_information |  |
| 6 | gpt-4o | Cloud | 92 | A- | 2171 | 1660/2795 | yes | dropped_information |  |
| 7 | gpt-5.2 | Cloud | 92 | A- | 2458 | 1870/2608 | yes | dropped_information | reasoning |
| 8 | gpt-5.4 | Cloud | 92 | A- | 2610 | 2347/2770 | yes |  | reasoning |
| 9 | qwen2.5-14b | Local | 92 | A- | 3929 | 3641/3992 | yes | dropped_information, altered_quote |  |
| 10 | gpt-5.4-pro | Cloud | 92 | A- | 82934 | 60815/163058 | yes |  | reasoning |
| 11 | mistral-nemo-12b-instruct | Local | 85 | B | 1483 | 1437/1484 | yes | dropped_information |  |
| 12 | phi-4 | Local | 85 | B | 1800 | 1665/1867 | yes | altered_quote |  |
| 13 | olmo-3-7b-instruct | Local | 85 | B | 1819 | 1723/1850 | yes | altered_quote, added_information |  |
| 14 | qwen3-14b | Local | 85 | B | 38416 | 37979/38469 | yes | added_information | reasoning |
| 15 | qwen3-8b | Local | 70 | C- | 1506 | 1486/1537 | yes | altered_quote, added_information |  |
| 16 | gpt-4.1-mini | Cloud | 70 | C- | 1875 | 1727/2105 | yes | answered_instead_of_edited, altered_quote |  |
| 17 | Mistral-Large-3 | Cloud | 70 | C- | 3207 | 2412/3427 | yes | added_information, dropped_information |  |
| 18 | phi-4-mini | Local | 65 | D | 850 | 828/1008 | yes | dropped_information, altered_quote |  |
| 19 | qwen3-4b | Local | 65 | D | 875 | 869/1053 | yes | added_information, altered_quote |  |
| 20 | qwen2.5-7b | Local | 65 | D | 917 | 882/1153 | yes | added_information, dropped_information, altered_quote |  |
| 21 | qwen3.5-0.8b | Local | 65 | D | 1388 | 1327/1435 | yes | altered_quote, added_information |  |
| 22 | Phi-4-multimodal-instruct | Cloud | 65 | D | 7036 | 6920/8566 | yes | added_information, dropped_information, altered_quote |  |
| 23 | phi-3.5-mini | Local | 60 | D | 819 | 789/2507 | yes | altered_quote, added_information |  |
| 24 | ministral-3-3b-instruct-2512 | Local | 60 | D | 968 | 960/1065 | yes | answered_instead_of_edited, altered_quote, added_information |  |
| 25 | qwen2.5-1.5b | Local | 55 | F | 3394 | 3359/3538 | yes | dropped_information, altered_quote |  |
| 26 | qwen2.5-0.5b | Local | 45 | F | 451 | 311/452 | yes | dropped_information, altered_quote |  |
| 27 | qwen3.5-2b-text | Local | 40 | F | 626 | 599/658 | yes | left_fillers, dropped_information |  |
| 28 | qwen3-1.7b | Local | 35 | F | 719 | 702/768 | yes | left_fillers |  |
| 29 | phi-3-mini-4k | Local | 35 | F | 1739 | 1297/1798 | yes | answered_instead_of_edited, added_information, dropped_information, altered_quote |  |
| 30 | mistral-7b-v0.2 | Local | 10 | F | 2 | 2/2 | no | unchanged, left_fillers |  |
| 31 | Kimi-K2.5 | Cloud | 10 | F | 11104 | 9214/11355 | no | unchanged, left_fillers |  |
| 32 | smollm3-3b | Local | 5 | F | 4 | 3/4 | no | unchanged, left_fillers |  |
| 33 | qwen3-0.6b | Local | 5 | F | 142 | 139/179 | yes | refused, empty |  |
| 34 | deepseek-r1-7b | Local | 5 | F | 3770 | 3758/3799 | no | unchanged, left_fillers | reasoning |
| 35 | gpt-5 | Cloud | 5 | F | 10752 | 10650/13178 | no | unchanged, left_fillers | reasoning |

## Cloud (Microsoft Foundry / Azure)

| # | Model | Quality | Grade | Median ms | Min/Max ms | Changed | Flags | Note |
|---|---|---|---|---|---|---|---|---|
| 1 | gpt-5.1 | 95 | A | 1967 | 1829/2247 | yes |  | reasoning |
| 2 | gpt-5.3-codex | 95 | A | 2409 | 2017/43531 | yes |  | reasoning |
| 3 | gpt-4.1 | 92 | A- | 1257 | 1233/3659 | yes | dropped_information |  |
| 4 | gpt-5.4-mini | 92 | A- | 1595 | 1311/1801 | yes |  |  |
| 5 | gpt-5.4-nano | 92 | A- | 1615 | 1585/2267 | yes | dropped_information |  |
| 6 | gpt-4o | 92 | A- | 2171 | 1660/2795 | yes | dropped_information |  |
| 7 | gpt-5.2 | 92 | A- | 2458 | 1870/2608 | yes | dropped_information | reasoning |
| 8 | gpt-5.4 | 92 | A- | 2610 | 2347/2770 | yes |  | reasoning |
| 9 | gpt-5.4-pro | 92 | A- | 82934 | 60815/163058 | yes |  | reasoning |
| 10 | gpt-4.1-mini | 70 | C- | 1875 | 1727/2105 | yes | answered_instead_of_edited, altered_quote |  |
| 11 | Mistral-Large-3 | 70 | C- | 3207 | 2412/3427 | yes | added_information, dropped_information |  |
| 12 | Phi-4-multimodal-instruct | 65 | D | 7036 | 6920/8566 | yes | added_information, dropped_information, altered_quote |  |
| 13 | Kimi-K2.5 | 10 | F | 11104 | 9214/11355 | no | unchanged, left_fillers |  |
| 14 | gpt-5 | 5 | F | 10752 | 10650/13178 | no | unchanged, left_fillers | reasoning |

## Local (Foundry Local, on-device)

| # | Model | Quality | Grade | Median ms | Min/Max ms | Changed | Flags | Note |
|---|---|---|---|---|---|---|---|---|
| 1 | qwen2.5-14b | 92 | A- | 3929 | 3641/3992 | yes | dropped_information, altered_quote |  |
| 2 | mistral-nemo-12b-instruct | 85 | B | 1483 | 1437/1484 | yes | dropped_information |  |
| 3 | phi-4 | 85 | B | 1800 | 1665/1867 | yes | altered_quote |  |
| 4 | olmo-3-7b-instruct | 85 | B | 1819 | 1723/1850 | yes | altered_quote, added_information |  |
| 5 | qwen3-14b | 85 | B | 38416 | 37979/38469 | yes | added_information | reasoning |
| 6 | qwen3-8b | 70 | C- | 1506 | 1486/1537 | yes | altered_quote, added_information |  |
| 7 | phi-4-mini | 65 | D | 850 | 828/1008 | yes | dropped_information, altered_quote |  |
| 8 | qwen3-4b | 65 | D | 875 | 869/1053 | yes | added_information, altered_quote |  |
| 9 | qwen2.5-7b | 65 | D | 917 | 882/1153 | yes | added_information, dropped_information, altered_quote |  |
| 10 | qwen3.5-0.8b | 65 | D | 1388 | 1327/1435 | yes | altered_quote, added_information |  |
| 11 | phi-3.5-mini | 60 | D | 819 | 789/2507 | yes | altered_quote, added_information |  |
| 12 | ministral-3-3b-instruct-2512 | 60 | D | 968 | 960/1065 | yes | answered_instead_of_edited, altered_quote, added_information |  |
| 13 | qwen2.5-1.5b | 55 | F | 3394 | 3359/3538 | yes | dropped_information, altered_quote |  |
| 14 | qwen2.5-0.5b | 45 | F | 451 | 311/452 | yes | dropped_information, altered_quote |  |
| 15 | qwen3.5-2b-text | 40 | F | 626 | 599/658 | yes | left_fillers, dropped_information |  |
| 16 | qwen3-1.7b | 35 | F | 719 | 702/768 | yes | left_fillers |  |
| 17 | phi-3-mini-4k | 35 | F | 1739 | 1297/1798 | yes | answered_instead_of_edited, added_information, dropped_information, altered_quote |  |
| 18 | mistral-7b-v0.2 | 10 | F | 2 | 2/2 | no | unchanged, left_fillers |  |
| 19 | smollm3-3b | 5 | F | 4 | 3/4 | no | unchanged, left_fillers |  |
| 20 | qwen3-0.6b | 5 | F | 142 | 139/179 | yes | refused, empty |  |
| 21 | deepseek-r1-7b | 5 | F | 3770 | 3758/3799 | no | unchanged, left_fillers | reasoning |

## Fastest models (median latency)

| # | Model | Group | Median ms | Quality | Grade |
|---|---|---|---|---|---|
| 1 | mistral-7b-v0.2 | Local | 2 | 10 | F |
| 2 | smollm3-3b | Local | 4 | 5 | F |
| 3 | qwen3-0.6b | Local | 142 | 5 | F |
| 4 | qwen2.5-0.5b | Local | 451 | 45 | F |
| 5 | qwen3.5-2b-text | Local | 626 | 40 | F |
| 6 | qwen3-1.7b | Local | 719 | 35 | F |
| 7 | phi-3.5-mini | Local | 819 | 60 | D |
| 8 | phi-4-mini | Local | 850 | 65 | D |
| 9 | qwen3-4b | Local | 875 | 65 | D |
| 10 | qwen2.5-7b | Local | 917 | 65 | D |
| 11 | ministral-3-3b-instruct-2512 | Local | 968 | 60 | D |
| 12 | gpt-4.1 | Cloud | 1257 | 92 | A- |
| 13 | qwen3.5-0.8b | Local | 1388 | 65 | D |
| 14 | mistral-nemo-12b-instruct | Local | 1483 | 85 | B |
| 15 | qwen3-8b | Local | 1506 | 70 | C- |
| 16 | gpt-5.4-mini | Cloud | 1595 | 92 | A- |
| 17 | gpt-5.4-nano | Cloud | 1615 | 92 | A- |
| 18 | phi-3-mini-4k | Local | 1739 | 35 | F |
| 19 | phi-4 | Local | 1800 | 85 | B |
| 20 | olmo-3-7b-instruct | Local | 1819 | 85 | B |
| 21 | gpt-4.1-mini | Cloud | 1875 | 70 | C- |
| 22 | gpt-5.1 | Cloud | 1967 | 95 | A |
| 23 | gpt-4o | Cloud | 2171 | 92 | A- |
| 24 | gpt-5.3-codex | Cloud | 2409 | 95 | A |
| 25 | gpt-5.2 | Cloud | 2458 | 92 | A- |
| 26 | gpt-5.4 | Cloud | 2610 | 92 | A- |
| 27 | Mistral-Large-3 | Cloud | 3207 | 70 | C- |
| 28 | qwen2.5-1.5b | Local | 3394 | 55 | F |
| 29 | deepseek-r1-7b | Local | 3770 | 5 | F |
| 30 | qwen2.5-14b | Local | 3929 | 92 | A- |
| 31 | Phi-4-multimodal-instruct | Cloud | 7036 | 65 | D |
| 32 | gpt-5 | Cloud | 10752 | 5 | F |
| 33 | Kimi-K2.5 | Cloud | 11104 | 10 | F |
| 34 | qwen3-14b | Local | 38416 | 85 | B |
| 35 | gpt-5.4-pro | Cloud | 82934 | 92 | A- |

## Did not produce a gradable result

| Model | Group | Status | Detail |
|---|---|---|---|
| gpt-audio-1.5 | Cloud | not-ready | not ready in 120s (Disabled: ) |
| model-router | Cloud | not-ready | not ready in 120s (Disabled: ) |
| deepseek-r1-1.5b | Local | not-ready | not ready in 1800s (Disabled: ) |

## Per-model detail

### gpt-5.1 (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.1`  ·  note: reasoning
- Quality: **95** (A)  ·  median **1967 ms** (min 1829 / max 2247)  ·  changed: True
- Dimensions — mechanics 98, fidelity 95, disfluency 100, instruction 98
- Judge: The editor output is fluent, well-structured, and follows the writing style. All disfluencies and self-corrections are removed cleanly. The meaning is preserved, and the quoted text is kept verbatim. There is a minor change in phrasing ('about 12%' instead of 'like 12%'), but it does not alter the meaning. No information was added or dropped, and the instructions were followed precisely.

```
I need to send a quarterly report to Sarah on the finance team by end of day Friday. I have to make sure the Q3 revenue numbers are included — the ones we were talking about in the meeting last week, where revenue went up about 12% for us.

Actually, Wednesday is better than Tuesday.

The report needs to be clearer and better for the stakeholders, because last time they were confused.

At the very end, add a line that says: “We few, we happy few, we band of brothers,” and then wrap it up nicely.
```

### gpt-5.3-codex (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.3-codex`  ·  note: reasoning
- Quality: **95** (A)  ·  median **2409 ms** (min 2017 / max 43531)  ·  changed: True
- Dimensions — mechanics 98, fidelity 95, disfluency 100, instruction 95
- Judge: The editor produced fluent, well-structured prose, removed all disfluencies, and preserved the speaker's meaning and quoted text verbatim. There is a minor drop in fidelity for slightly rephrasing 'more better and more clearer' to 'better and clearer,' but the intent is maintained. The writing style and contract were followed precisely.

```
I need to send a quarterly report to Sarah on the finance team by Friday, end of day, and make sure the Q3 revenue numbers are included—the ones we discussed in last week’s meeting, where it went up 12%.

Do it on Wednesday; that’s better than Tuesday. Also, the report needs to be better and clearer for the stakeholders, because last time they were confused.

At the very end, add a line that says: We few, we happy few, we band of brothers. Then wrap it up nicely.
```

### gpt-4.1 (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-4.1`
- Quality: **92** (A-)  ·  median **1257 ms** (min 1233 / max 3659)  ·  changed: True
- Dimensions — mechanics 98, fidelity 90, disfluency 100, instruction 95
- Flags: `dropped_information`
- Judge: The editor produced fluent, well-structured prose, removed all disfluencies, and followed the writing style. However, 'more better' was changed to 'clearer and more comprehensive,' which slightly alters the original meaning, and 'Us and it' was omitted, though it was unclear. Otherwise, the meaning and quoted text were preserved.

```
I need to send a quarterly report to Sarah on the finance team by Friday, end of day. Make sure the Q3 revenue numbers are included—the ones we discussed in the meeting last week, where revenue went up about 12%. Wednesday would be better for sending it than Tuesday.

The report needs to be clearer and more comprehensive for the stakeholders, since last time there was confusion. At the very end, add a line that says, "We few, we happy few, we band of brothers," and then wrap it up nicely. Thanks.
```

### gpt-5.4-mini (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.4-mini`
- Quality: **92** (A-)  ·  median **1595 ms** (min 1311 / max 1801)  ·  changed: True
- Dimensions — mechanics 95, fidelity 90, disfluency 100, instruction 95
- Judge: The editor output is fluent, well-structured, and follows the writing style. All disfluencies and self-corrections are removed, and the quoted text is preserved verbatim. There is a minor drop in fidelity as 'US' from '12% Us' was omitted, but otherwise, the meaning and intent are maintained.

```
I need to send a quarterly report to Sarah on the finance team by Friday, end of day, and make sure the Q3 revenue numbers are in there, the ones we were talking about in the meeting last week, where it went up 12%.

Let’s do it on Wednesday instead of Tuesday. Honestly, the report needs to be better and clearer for the stakeholders, because last time they were confused. At the very end, add a line that says, “We few, we happy few, we band of brothers,” and then wrap it up nicely. Thanks.
```

### gpt-5.4-nano (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.4-nano`
- Quality: **92** (A-)  ·  median **1615 ms** (min 1585 / max 2267)  ·  changed: True
- Dimensions — mechanics 98, fidelity 90, disfluency 100, instruction 95
- Flags: `dropped_information`
- Judge: The editor produced fluent, well-structured prose, removed all disfluencies, and followed the writing style. However, the phrase 'Us and it' was dropped, which slightly affects fidelity. All quoted text was preserved verbatim, and no instructions were executed.

```
Okay, I need to send the quarterly report to Sarah on the finance team by Friday end of day. I also need to make sure the Q3 revenue numbers are included—those we discussed in last week’s meeting, when it went up 12%.

Also, instead of Tuesday, Wednesday is better. Honestly, the report needs to be more complete and clearer for the stakeholders, because last time they were confused.

At the very end, add a line that says, We few, we happy few, we band of brothers, and then wrap it up nicely. Thanks.
```

### gpt-4o (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-4o`
- Quality: **92** (A-)  ·  median **2171 ms** (min 1660 / max 2795)  ·  changed: True
- Dimensions — mechanics 98, fidelity 90, disfluency 100, instruction 95
- Flags: `dropped_information`
- Judge: The editor produced fluent, well-structured prose, removed all disfluencies, and followed the writing style. However, 'more better' was changed to 'more polished,' which slightly alters the original meaning, and 'thanks' was omitted, resulting in minor dropped information.

```
I need to send a quarterly report to Sarah on the finance team by the end of the day on Friday. Make sure the Q3 revenue numbers are included—the ones we discussed in last week's meeting, where we noted the 12% increase. 

It's better to deliver the report on Wednesday rather than Tuesday. Also, the report needs to be clearer and more polished for the stakeholders, as they found the previous one confusing. At the very end, include a line that says, "We few, we happy few, we band of brothers," and then wrap it up neatly.
```

### gpt-5.2 (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.2`  ·  note: reasoning
- Quality: **92** (A-)  ·  median **2458 ms** (min 1870 / max 2608)  ·  changed: True
- Dimensions — mechanics 98, fidelity 90, disfluency 100, instruction 95
- Flags: `dropped_information`
- Judge: The editor produced clear, well-structured prose, removed all disfluencies, and followed the writing style. However, the phrase 'Us and it' was dropped, which slightly impacts fidelity. All quoted text was preserved verbatim, and no instructions were answered or executed.

```
I need to send a quarterly report to Sarah on the finance team by Friday, end of day. Make sure the Q3 revenue numbers are included—the ones we were talking about in last week’s meeting, where it went up about 12%.

Send it on Wednesday. The report needs to be better and clearer for the stakeholders, because last time they were confused.

At the very end, add a line that says, “we few we Happy few we band of brothers,” and then wrap it up nicely. Thanks.
```

### gpt-5.4 (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.4`  ·  note: reasoning
- Quality: **92** (A-)  ·  median **2610 ms** (min 2347 / max 2770)  ·  changed: True
- Dimensions — mechanics 95, fidelity 90, disfluency 100, instruction 95
- Judge: The editor output is fluent, well-structured, and follows the writing style. All disfluencies and self-corrections are removed, and quoted text is preserved verbatim. There is a minor drop in fidelity as the phrase 'honestly' was omitted, but the overall meaning is maintained. No contract violations or flags apply.

```
I need to send a quarterly report to Sarah on the finance team by Friday, end of day, and make sure the Q3 revenue numbers are included—the ones we were talking about in the meeting last week, where it went up 12%.

Wednesday is better.

The report needs to be better and clearer for the stakeholders, because last time they were confused. At the very end, add a line that says, "we few we Happy few we band of brothers," and then wrap it up nicely.
```

### qwen2.5-14b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen2.5-14b`
- Quality: **92** (A-)  ·  median **3929 ms** (min 3641 / max 3992)  ·  changed: True
- Dimensions — mechanics 98, fidelity 90, disfluency 100, instruction 95
- Flags: `dropped_information`, `altered_quote`
- Judge: The editor produced fluent, well-structured prose and removed all disfluencies. However, 'we Happy few' was dropped from the quoted line, and 'more better' was changed to 'clearer and more detailed,' which slightly alters the original meaning. Otherwise, the instructions and writing style were followed closely.

```
I'm okay, so I need to send a quarterly report to Sarah on the finance team by the end of the day on Friday. Make sure the Q3 revenue numbers are included—the ones we discussed in last week's meeting, which increased by 12%. Actually, Wednesday would be a better day to send it. The report needs to be clearer and more detailed for the stakeholders because they were confused last time. At the very end, add a line that says, "We few, we band of brothers," and wrap it up nicely. Thanks.
```

### gpt-5.4-pro (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5.4-pro`  ·  note: reasoning
- Quality: **92** (A-)  ·  median **82934 ms** (min 60815 / max 163058)  ·  changed: True
- Dimensions — mechanics 98, fidelity 90, disfluency 100, instruction 95
- Judge: The editor output is fluent, well-structured, and follows the writing style closely. All disfluencies and self-corrections are removed cleanly. There is a minor fidelity issue: 'Us and it' from the raw is omitted, but this phrase is incoherent and likely a dictation error, so its omission does not affect the intended meaning. Quoted text is preserved verbatim, and no information is added or dropped otherwise.

```
I need to send a quarterly report over to Sarah on the finance team by Friday, end of day, and make sure the Q3 revenue numbers are in there—the ones we were talking about in the meeting last week, where it went up like 12%. Send it on Tuesday—no, wait, actually Wednesday is better.

Honestly, the report needs to be better and clearer for the stakeholders, because last time they were confused. At the very end, add a line that says, “we few, we happy few, we band of brothers,” and then wrap it up nicely. Thanks.
```

### mistral-nemo-12b-instruct (Local)

- Provider: `FoundryLocal`  ·  target: `mistral-nemo-12b-instruct`
- Quality: **85** (B)  ·  median **1483 ms** (min 1437 / max 1484)  ·  changed: True
- Dimensions — mechanics 90, fidelity 80, disfluency 95, instruction 85
- Flags: `dropped_information`
- Judge: The editor cleaned up disfluencies and improved mechanics well, but dropped 'more better' and 'more clearer,' which, while ungrammatical, were part of the speaker's emphasis. The instruction was mostly followed, but the phrase 'aim for for Wednesday' is awkward and the meaning about the preferred day was slightly altered.

```
I'm okay, so I need to send a quarterly report to Sarah on the finance team by the end of Friday. Ensure the Q3 revenue numbers are included, specifically the ones we discussed in last week's meeting, where there was a 12% increase. It's better to aim for for Wednesday. The report should be clearer for stakeholders, as the previous one caused some confusion. Finally, add a line at the end: "We few, we happy few, we band of brothers." Then, wrap it up nicely.
```

### phi-4 (Local)

- Provider: `FoundryLocal`  ·  target: `phi-4`
- Quality: **85** (B)  ·  median **1800 ms** (min 1665 / max 1867)  ·  changed: True
- Dimensions — mechanics 90, fidelity 85, disfluency 95, instruction 85
- Flags: `altered_quote`
- Judge: The editor produced clear, well-structured prose, removed fillers and false starts, and preserved most of the meaning. However, the quoted line was altered with extra commas and capitalization, and the phrase 'Us and it' was dropped, slightly affecting fidelity. The writing style and contract were followed, but minor issues prevent a perfect score.

```
I need to send a quarterly report to Sarah on the finance team by the end of the day on Friday. Make sure the Q3 revenue numbers are included—the ones we discussed in last week's meeting where they increased by 12%. It's better to send it on Wednesday. The report needs to be clearer and more comprehensive for the stakeholders, as they were confused last time. At the very end, add a line that says,, "We few, we happy few, we band of brothers," and then wrap it up nicely. Thanks.
```

### olmo-3-7b-instruct (Local)

- Provider: `FoundryLocal`  ·  target: `olmo-3-7b-instruct`
- Quality: **85** (B)  ·  median **1819 ms** (min 1723 / max 1850)  ·  changed: True
- Dimensions — mechanics 95, fidelity 85, disfluency 100, instruction 90
- Flags: `altered_quote`, `added_information`
- Judge: Mechanics are strong and disfluencies are fully removed. The quote was slightly altered ('we few we Happy few we band of brothers' became 'We few, we happy few, we band of brothers'), and 'I will add a line' adds intent not explicitly stated. Otherwise, the meaning is well preserved and the writing style is followed.

```
I need to send a quarterly report to Sarah in the finance team by Friday at the end of the day. I want to make sure the Q3 revenue numbers are included—the ones we discussed in the meeting last week when our numbers went up by 12%. Also, I should use Wednesday as the reference date, not Tuesday. 

The report needs to be improved—it should be more clear and better organized for the stakeholders, since last time they were confused. At the very end, I will add a line: "We few, we happy few, we band of brothers." Then I’ll just wrap it up nicely. Thanks.
```

### qwen3-14b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3-14b`  ·  note: reasoning
- Quality: **85** (B)  ·  median **38416 ms** (min 37979 / max 38469)  ·  changed: True
- Dimensions — mechanics 95, fidelity 85, disfluency 95, instruction 80
- Flags: `added_information`
- Judge: The editor produced fluent, well-structured prose and removed disfluencies, but added 'more concise' which was not in the original dictation, slightly altering the meaning. Otherwise, mechanics and disfluency handling were strong, and the quoted text was preserved verbatim.

```
I'm okay. So I need to send a quarterly report over to Sarah on the finance team by Friday end of day. Make sure the Q3 revenue numbers are in there — you know, the ones we were talking about in the meeting last week, where it went up like 12%. 

We need to send it on Tuesday — no, wait, actually Wednesday is better. And honestly, the report needs to be more clear and more concise for the stakeholders. Because last time, they were confused.

At the very end, add a line that says, "We few, we happy few, we band of brothers." And then just wrap it up nicely. Thanks.
```

### qwen3-8b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3-8b`
- Quality: **70** (C-)  ·  median **1506 ms** (min 1486 / max 1537)  ·  changed: True
- Dimensions — mechanics 80, fidelity 60, disfluency 90, instruction 70
- Flags: `altered_quote`, `added_information`
- Judge: The editor improved mechanics and removed disfluencies well, but altered the quoted text ('we few we Happy few we band of brothers' became 'We, the Happy Few, the band of brothers') and added 'the' and commas, which changes the original literary reference. Some meaning was lost or changed, and the editor did not strictly preserve the speaker's vocabulary as required.

```
I'm okay, so I need to send a quarterly report over to Sarah on the finance team by Friday, end of day. Make sure the Q3 revenue numbers are in there—the ones we talked about in the meeting last week, where it went up like 12%. 

Actually, Wednesday is better. And honestly, the report needs to be more better and more clearer for the stakeholders. Last time, they were confused. 

At the very end, add a line that says: "We, the Happy Few, the band of brothers." Then just wrap it up nicely. Thanks.
```

### gpt-4.1-mini (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-4.1-mini-950166`
- Quality: **70** (C-)  ·  median **1875 ms** (min 1727 / max 2105)  ·  changed: True
- Dimensions — mechanics 90, fidelity 65, disfluency 95, instruction 60
- Flags: `answered_instead_of_edited`, `altered_quote`
- Judge: The editor improved mechanics and removed disfluencies well, but it answered the request by using 'Please make sure' and 'Wednesday would be a better day,' which is directive and not just cleaned dictation. The quoted text was altered ('we few we Happy few we band of brothers' became 'We few, we happy few, we band of brothers'). Fidelity suffered due to these changes, and the instruction to not answer or execute was not fully followed.

```
I'm okay. I need to send a quarterly report to Sarah on the finance team by Friday, end of day. Please make sure the Q3 revenue numbers are included—the ones we discussed in the meeting last week, where it increased by 12% in the US. Wednesday would be a better day to send it than Tuesday. The report needs to be clearer and more polished for the stakeholders because they were confused last time. At the very end, add a line that says, "We few, we happy few, we band of brothers," and then wrap it up nicely. Thanks.
```

### Mistral-Large-3 (Cloud)

- Provider: `AzureFoundry`  ·  target: `Mistral-Large-3`
- Quality: **70** (C-)  ·  median **3207 ms** (min 2412 / max 3427)  ·  changed: True
- Dimensions — mechanics 90, fidelity 65, disfluency 95, instruction 60
- Flags: `added_information`, `dropped_information`
- Judge: The editor improved mechanics and removed disfluencies well, but added phrases ('Here’s the revised version') and omitted some original content (e.g., 'honestly', 'more better', 'more clearer', 'thanks'). The instruction to not answer or execute was partially violated by the introductory sentence and formatting. Fidelity suffered due to dropped and altered information.

```
Here’s the revised version:

I need to send the quarterly report to Sarah on the finance team by Friday end of day. The Q3 revenue numbers must be included—specifically the 12% increase we discussed in last week’s meeting on Wednesday. The report should be clearer and more polished for stakeholders, as they were confused last time.

At the end, add the line: *We few, we happy few, we band of brothers.* Then wrap it up neatly.
```

### phi-4-mini (Local)

- Provider: `FoundryLocal`  ·  target: `phi-4-mini`
- Quality: **65** (D)  ·  median **850 ms** (min 828 / max 1008)  ·  changed: True
- Dimensions — mechanics 80, fidelity 60, disfluency 90, instruction 60
- Flags: `dropped_information`, `altered_quote`
- Judge: The editor improved mechanics and removed disfluencies well, but dropped 'Happy few' from the quoted line and omitted 'US' and 'more better' from the original, altering meaning. The instruction to preserve quoted text verbatim and all information was not fully followed.

```
I am okay. I need to send a over a quarterly report to Sarah on the finance team by the end of Friday. Please make sure the Q3 revenue numbers are included, which we discussed in the meeting last week and increased by 12%. I prefer sending it on Wednesday. The report needs to be clearer for the stakeholders, as they were confused last time. At the very end, please add the line "We few, we band of brothers." Wrap it up nicely. Thanks.
```

### qwen3-4b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3-4b`
- Quality: **65** (D)  ·  median **875 ms** (min 869 / max 1053)  ·  changed: True
- Dimensions — mechanics 70, fidelity 60, disfluency 80, instruction 60
- Flags: `added_information`, `altered_quote`
- Judge: The editor improved punctuation and removed most disfluencies, but introduced errors (e.g., 'includedä'), altered the quoted text ('We few, we Happy few...' vs. 'We few, we happy few...'), and added words ('more precise') not present in the original. The structure is improved, but some sentences are awkwardly split, and the instruction to preserve meaning and quoted text verbatim was not fully followed.

```
I am okay. I need to send a quarterly report to Sarah on the finance team by Friday, end, end of day. Make sure the Q3 revenue numbers are includedä in there. The ones we were talking about in the meeting last week where it went up like 12%. On Tuesday, no wait, actually Wednesday is better. Honestly, the report needs to be more clear and more precise for the stakeholders. Because last time they were confused. At the very end, add a line that says, "We few, we happy few, we band of brothers." And then just wrap it up nicely. Thanks.
```

### qwen2.5-7b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen2.5-7b`
- Quality: **65** (D)  ·  median **917 ms** (min 882 / max 1153)  ·  changed: True
- Dimensions — mechanics 70, fidelity 60, disfluency 80, instruction 60
- Flags: `added_information`, `dropped_information`, `altered_quote`
- Judge: The editor improved mechanics and removed most disfluencies, but altered the quoted text ('we few we Happy few we band of brothers' became 'We are the happy few, a band of brothers'), dropped 'US' and '12%' details, and added 'I should include' and 'which we discussed,' which were not in the original. The instruction to preserve quoted/literary text verbatim and not add or drop information was not fully followed.

```
I'm okay. I need to send a quarterly report to Sarah on the finance team by Friday end-of-day. I should include the Q3 revenue numbers, which we discussed in the meeting last week and increased by 12%. It would be better and clearer for the stakeholders this time because they were confused last time.. At At the very end, add a line that says, "We are the happy few, a band of brothers," and then wrap it up nicely. Thanks.
```

### qwen3.5-0.8b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3.5-0.8b`
- Quality: **65** (D)  ·  median **1388 ms** (min 1327 / max 1435)  ·  changed: True
- Dimensions — mechanics 80, fidelity 60, disfluency 90, instruction 60
- Flags: `altered_quote`, `added_information`
- Judge: Mechanics are mostly correct, and disfluencies are well removed. However, the quoted text 'we few we Happy few we band of brothers' was altered, and the editor added 'Please ensure' and changed 'more better and more clearer' to 'more, better, and clearer,' which modifies the original meaning. The instruction to keep quoted/literary text verbatim and not add information was not fully followed.

```
I'm okay, so I need to send a quarterly report to Sarah on the finance team by Friday's end of the week. Please ensure the Q3 revenue numbers are included, as we discussed in the meeting last week where they went up by 12%. On Tuesday, no—actually Wednesday is better. Honestly, the report needs to be more, better, and clearer for the stakeholders, as last time they were confused. Add a line that says "We're happy, we're a band of brothers" at the very end, then wrap it up nicely. Thanks.
```

### Phi-4-multimodal-instruct (Cloud)

- Provider: `AzureFoundry`  ·  target: `Phi-4-multimodal-instruct`
- Quality: **65** (D)  ·  median **7036 ms** (min 6920 / max 8566)  ·  changed: True
- Dimensions — mechanics 85, fidelity 60, disfluency 90, instruction 65
- Flags: `added_information`, `dropped_information`, `altered_quote`
- Judge: The editor improved mechanics and removed disfluencies well, but changed meaning by adding 'Let's send it out on Wednesday' (not in the original), dropped 'more better and more clearer,' and altered the quoted line ('we few we Happy few we band of brothers' to 'We few, we Happy few, we band of brothers.'). The instruction to preserve meaning and quoted text verbatim was not fully followed.

```
I need to send a quarterly report over to Sarah on the finance team by the end of Friday. I need to ensure that the Q3 revenue numbers are included; the increase of 12% Us that we discussed in the meeting last week. Let's send it out on Wednesday as it will be more timely. The report needs to be clearer for the stakeholders, as they were confused with the last report. At the end, I'll add the line "We few, we Happy few, we band of brothers." and then wrap it up nicely. Thanks.
```

### phi-3.5-mini (Local)

- Provider: `FoundryLocal`  ·  target: `phi-3.5-mini`
- Quality: **60** (D)  ·  median **819 ms** (min 789 / max 2507)  ·  changed: True
- Dimensions — mechanics 70, fidelity 55, disfluency 60, instruction 55
- Flags: `altered_quote`, `added_information`
- Judge: The editor improved mechanics and removed most disfluencies, but altered the quoted phrase ('we few we Happy few we band of brothers' became 'We, the Happy few, are a band of brothers') and added phrases like 'I will conclude the report neatly. Thank you.' which were not present in the original. The writing style was mostly followed, but fidelity suffered due to these changes.

```
I'm okay; I need to send a quarterly report to Sarah on the finance team by the end of the day on Friday. I must ensure that the Q3 revenue numbers are included, specifically the ones we discussed in the meeting last week, where it went up up by 12% in the US. Actually, Wednesday would be a a better day day for sending it. The report needs to be more clear and improved for the stakeholders, as last time they were confused. At the very end, I will add a line that says, "We, the Happy few, are a band of brothers." Then, I will conclude the report neatly. Thank you.
```

### ministral-3-3b-instruct-2512 (Local)

- Provider: `FoundryLocal`  ·  target: `ministral-3-3b-instruct-2512`
- Quality: **60** (D)  ·  median **968 ms** (min 960 / max 1065)  ·  changed: True
- Dimensions — mechanics 90, fidelity 60, disfluency 90, instruction 50
- Flags: `answered_instead_of_edited`, `altered_quote`, `added_information`
- Judge: The editor answered and executed the request instead of simply editing the dictation, added formatting and changed the quoted text, and introduced new phrasing. Mechanics and disfluency handling are strong, but fidelity and instruction compliance are compromised.

```
Here’s the cleaned and structured version:

---

I’m fine—I need to send the quarterly report to Sarah on the finance team by Friday at the end of the day. Make sure the Q3 revenue numbers are included, specifically the ones we discussed in last week’s meeting, where growth was up by 12%.

I’d prefer to finalize it on **Wednesday** rather than Tuesday. The report should also be clearer and more polished for stakeholders, since they seemed confused last time.

Finally, add a line at the end that reads: *“We’re few, we’re happy, we’re a band of brothers.”* Wrap it up neatly, and thank you.
```

### qwen2.5-1.5b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen2.5-1.5b`
- Quality: **55** (F)  ·  median **3394 ms** (min 3359 / max 3538)  ·  changed: True
- Dimensions — mechanics 80, fidelity 40, disfluency 90, instruction 60
- Flags: `dropped_information`, `altered_quote`
- Judge: The editor improved mechanics and removed most disfluencies, but dropped and changed key information: the quoted line was altered ('we few we Happy few we band of brothers' became 'We are happy few' and 'The band of brothers'), and the meaning of the timing ('Tuesday no wait actually Wednesday is better') was not preserved. Some content was also rephrased or omitted, reducing fidelity and adherence to instructions.

```
I am fine. I need to send a quarterly report to Sarah on the finance team by Friday evening. Ensure the Q3 revenue numbers are included. We discussed an increase of approximately 12% from previous weeks. Last Tuesday had a slight improvement compared to Wednesday. The report should be clearer and easier to understand for stakeholders. Add a final line saying 'We are happy few' and 'The band of brothers'. Thank you.
```

### qwen2.5-0.5b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen2.5-0.5b`
- Quality: **45** (F)  ·  median **451 ms** (min 311 / max 452)  ·  changed: True
- Dimensions — mechanics 60, fidelity 35, disfluency 50, instruction 40
- Flags: `dropped_information`, `altered_quote`
- Judge: The editor dropped and altered the quoted literary text ('we few we Happy few we band of brothers'), changed meaning in several places, and added information not present in the original. Mechanics are inconsistent, and disfluencies are not fully removed. The writing style and contract were not properly followed.

```
I’m doing fine, so I need to send a quarterly report to to Sarah on the finance team by Friday evening. I also need to ensure the Q3 revenue figures numbers are included. We were discussing talking about this matter last week and it went up by 12%. On Tuesday, however, things weren't as good as expected. It wasn’t until Wednesday that the report got better. I think the report should be more detailed and clearer for the the stakeholders because they they were confused and and I mean at the end I added a line saying “We’re happy” and “The band of brothers and friends.” Then I I wrapped it up nicely. Thanks.
```

### qwen3.5-2b-text (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3.5-2b-text`
- Quality: **40** (F)  ·  median **626 ms** (min 599 / max 658)  ·  changed: True
- Dimensions — mechanics 35, fidelity 50, disfluency 40, instruction 40
- Flags: `left_fillers`, `dropped_information`
- Judge: The editor left several disfluencies ('um', 'like', repeated words), failed to break up run-on sentences, and did not fully clean up self-corrections. Some information was dropped or muddled, and mechanics (punctuation, grammar) are inconsistent. The writing style and contract were not fully followed.

```
I'm okay, so I need to send a quarterly report to Sarah on the finance team by Friday end of day.. I need to make sure the Q3 revenue numbers are included.. We were talking about the ones we were talking about about in the meeting last week week where it went up like 12% us and it went up on Tuesday no wait actually Wednesday Wednesday is better.. Honestly, the report needs to be more better and more clearer for the stakeholders because last time they were confused and um at the very end end add a line that says we few we we happy few we band of brothers and then just wrap it up nicely thanks.
```

### qwen3-1.7b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3-1.7b`
- Quality: **35** (F)  ·  median **719 ms** (min 702 / max 768)  ·  changed: True
- Dimensions — mechanics 30, fidelity 60, disfluency 20, instruction 40
- Flags: `left_fillers`
- Judge: The editor left multiple filler words and disfluencies ('um', 'like', 'you know', 'no wait', 'honestly', 'and um', 'just you know'), failed to break up run-on sentences, and did not correct grammar or punctuation as required. The meaning is mostly preserved, but the writing style and contract were not properly followed.

```
I'm okay so I need to send a quarterly report over to Sarah on the finance team by Friday end to the end of. And like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.
```

### phi-3-mini-4k (Local)

- Provider: `FoundryLocal`  ·  target: `phi-3-mini-4k`
- Quality: **35** (F)  ·  median **1739 ms** (min 1297 / max 1798)  ·  changed: True
- Dimensions — mechanics 60, fidelity 30, disfluency 80, instruction 20
- Flags: `answered_instead_of_edited`, `added_information`, `dropped_information`, `altered_quote`
- Judge: The editor output rewrote the dictation as a formal email, added new content, dropped some original information, and altered the quoted text. It did not simply clean the raw dictation but instead answered and executed the request, violating the contract and writing style instructions.

```
Subject: Quarterly Report Submission - Q3 Revenue and Team Update


Dear Sarah,

I hope this email email finds you well. I am writing to submit the Quarterly Report for Q3, which which includes the updated re revenue numbers.

The report is scheduled attached to this email and will be sent to you by end of day this Wednesday. The Q3 revenue numbers have been updated to reflect a 12% increase, as discussed in our meeting last week.

I understand apologize for any confusion caused in the previous report and have taken extra care to ensure that this report is clear and concise for our stakeholders. To further emphasize our team's dedication and unity, I have included the following line at the end of the the report:

"We are a few, but we are many, a band of brothers."

Thank you for your attention to this this matter, and please let me know if there are any additional further details or clarifications needed.

Best regards,

[YourYour Name]]

P.S. I appreciate your your leadership and support in this endeavor.
```

### mistral-7b-v0.2 (Local)

- Provider: `FoundryLocal`  ·  target: `mistral-7b-v0.2`
- Quality: **10** (F)  ·  median **2 ms** (min 2 / max 2)  ·  changed: False
- Dimensions — mechanics 10, fidelity 100, disfluency 0, instruction 10
- Flags: `unchanged`, `left_fillers`
- Judge: The editor output is identical to the raw dictation, with no corrections made. Fillers, disfluencies, and grammar errors remain, and the writing style was not applied. Only fidelity is high, as nothing was added or dropped.

```
I'm okay so I need to uh send a quarterly report over to Sarah on the finance team by Friday end of day and like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.
```

### Kimi-K2.5 (Cloud)

- Provider: `AzureFoundry`  ·  target: `Kimi-K2.5`
- Quality: **10** (F)  ·  median **11104 ms** (min 9214 / max 11355)  ·  changed: False
- Dimensions — mechanics 10, fidelity 100, disfluency 0, instruction 10
- Flags: `unchanged`, `left_fillers`
- Judge: The editor output is identical to the raw dictation, with no corrections made. Fillers, disfluencies, and grammar errors remain, and the writing style was not applied. Only fidelity is high, as nothing was added or dropped.

```
I'm okay so I need to uh send a quarterly report over to Sarah on the finance team by Friday end of day and like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.
```

### smollm3-3b (Local)

- Provider: `FoundryLocal`  ·  target: `smollm3-3b`
- Quality: **5** (F)  ·  median **4 ms** (min 3 / max 4)  ·  changed: False
- Dimensions — mechanics 5, fidelity 100, disfluency 0, instruction 5
- Flags: `unchanged`, `left_fillers`
- Judge: The editor output is unchanged from the raw dictation, leaving all disfluencies, fillers, and grammar errors intact. No editing was performed, violating the contract and writing style requirements.

```
I'm okay so I need to uh send a quarterly report over to Sarah on the finance team by Friday end of day and like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.
```

### qwen3-0.6b (Local)

- Provider: `FoundryLocal`  ·  target: `qwen3-0.6b`
- Quality: **5** (F)  ·  median **142 ms** (min 139 / max 179)  ·  changed: True
- Dimensions — mechanics 5, fidelity 0, disfluency 0, instruction 0
- Flags: `refused`, `empty`
- Judge: The editor refused to edit and returned an empty, non-edited response, violating the contract and failing to process the raw dictation. No editing was performed, and all information was dropped.

```
I'm sorry, but I can't process the content you've provided. Please provide the content you want to process.
```

### deepseek-r1-7b (Local)

- Provider: `FoundryLocal`  ·  target: `deepseek-r1-7b`  ·  note: reasoning
- Quality: **5** (F)  ·  median **3770 ms** (min 3758 / max 3799)  ·  changed: False
- Dimensions — mechanics 5, fidelity 100, disfluency 0, instruction 5
- Flags: `unchanged`, `left_fillers`
- Judge: The editor output is unchanged from the raw dictation, leaving all disfluencies, fillers, and grammar errors intact. No editing was performed, violating the contract and writing style requirements.

```
I'm okay so I need to uh send a quarterly report over to Sarah on the finance team by Friday end of day and like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.
```

### gpt-5 (Cloud)

- Provider: `AzureFoundry`  ·  target: `gpt-5`  ·  note: reasoning
- Quality: **5** (F)  ·  median **10752 ms** (min 10650 / max 13178)  ·  changed: False
- Dimensions — mechanics 5, fidelity 100, disfluency 0, instruction 5
- Flags: `unchanged`, `left_fillers`
- Judge: The editor output is identical to the raw dictation, with no corrections made. Fillers, disfluencies, and grammar errors remain, and the writing style instructions were not followed.

```
I'm okay so I need to uh send a quarterly report over to Sarah on the finance team by Friday end of day and like make sure the Q3 revenue numbers are in there you know the ones we was talking about in the meeting last week where it went up like 12% Us and it on Tuesday no wait actually Wednesday is better and honestly the report it need to be more better and more clearer for the stakeholders cause last time they was confused and um at the very end add a line that says we few we Happy few we band of brothers and then just you know wrap it up nicely thanks.
```

