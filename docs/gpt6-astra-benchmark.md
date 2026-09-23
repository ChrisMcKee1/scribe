# GPT-6-Astra cleanup benchmark

Measured September 4, 2026. This is a fresh Windows cleanup comparison, not an update to the
numerically incompatible July scores.

## Recommendation

**Keep `gpt-5.6-terra` as the recommended responsive cloud default. Do not switch the
recommendation to Astra yet.** Astra belongs in the high-quality group, but this run does not
establish a reliable quality advantage. It took **2.73 times as long as Terra at the median**,
adding about 3.5 seconds after each dictation. Sol is a faster alternative to Astra for someone
who prefers its editing, but the small quality differences should not be treated as a proven
ordering.

No application settings, production model defaults, or writing prompts were changed.

## Current 25-case leaderboard

Five models, two separate passes, **750 timed cleanup calls**, and **250 retained outputs**.
Every retained output received the existing GPT-4.1 grade and a second, identity-blind GPT-5.2
grade. Scores are model judgments, **not percentages of correct dictations**.

Sorted by the second judge's score. The top models are too close, and the judges too inconsistent,
to declare a confident quality champion.

| Model | Blind judge /100 | Existing judge /100 | Median | p95 | Maximum |
|---|---:|---:|---:|---:|---:|
| `gpt-5.6-sol` | 96.88 | 90.92 | 3.00 s | 5.70 s | 9.92 s |
| `gpt-6-astra` | 95.92 | 91.18 | 5.53 s | 8.52 s | 26.72 s |
| **`gpt-5.6-terra`** | 95.62 | 89.28 | **2.02 s** | 3.60 s | 11.65 s |
| `gpt-5.6-luna` | 94.23 | 88.20 | 2.59 s | 6.17 s | 8.06 s |
| `gpt-5.4-mini` * | 91.16 | 80.26 | 2.05 s | **3.47 s** | 13.49 s |

*Mini was measured on East US 2 GlobalStandard. The other four used South Central US
DataZoneStandard. Mini is a deployed-workflow reference, not a controlled same-region comparison.*

Each row pools 150 timing samples and 50 scored outputs. Only Astra had a timed call above
15 seconds: one of 150. There were no warning/error entries in the ten benchmark logs, no missing
token-usage records, and no unchanged retained outputs. The harness retains only the last output
of each three-call case, so this does **not** prove semantic correctness of all 750 responses.

### Repeatability

| Model | Existing judge, pass 1 / 2 | Blind judge, pass 1 / 2 |
|---|---:|---:|
| Astra | 91.60 / 90.76 | 96.19 / 95.65 |
| Sol | 91.20 / 90.64 | 96.75 / 97.01 |
| Terra | 89.00 / 89.56 | 96.07 / 95.16 |
| Luna | 88.76 / 87.64 | 95.85 / 92.61 |
| Mini | 78.84 / 81.68 | 92.25 / 90.06 |

Astra's blind-score lead over Terra is **0.30 points**. A paired, case-cluster bootstrap gives
an interval of **-1.49 to +1.93 points**. This resamples the 25 case-level differences after
averaging the two passes, with 10,000 draws and seed 6042026. It describes case sampling
conditional on these outputs and this judge, not production accuracy or all sources of judge
uncertainty. It does not establish an Astra quality win.

## Reading the actual outputs

The coordinating assistant, itself Astra, also inspected the inputs, the retained Astra repeat
outputs, and representative model/judge disagreements. This is a qualitative assessment, not
an additional independent score.

**What Astra does well:** it resolves the cascade of corrections to Tuesday, 5,000, Rachel and
9:30; merges the repeated onboarding request while keeping the room-booking request; repairs the
phonetic narrative and dialogue; and formats technical vocabulary such as
`text-embedding-3-large`, RAG, HNSW, BM25 and MCP. It edits the embedded security-incident request
rather than carrying it out.

**What still needs attention:**

- Technical-name recovery is not reliable. The second pass emitted **"Atlas 1.1 won on quality"**
  and **"Opus 11 won on quality"**, and retained **"rear eye task"**. These started as ASR garbles,
  not entirely new inventions by the editor. The clean references expose the intended speech,
  but the cleanup model never receives those references or the audio.
- The regional-voice output changes **"that's kinda how we broke it"** to
  **"that's how we broke it"**, losing a qualification. Both passes also omit **"I repeat"** from
  the emphatic security note. These are small fidelity costs that fluent prose can conceal.
- Astra's otherwise good editing does not make the additional delay disappear. The observed
  quality differences do not justify an extra 3.5 seconds over Terra for routine push-to-talk.

Keeping "gonna" or "wanna", using a semicolon, or choosing contractions is not automatically a
failure here. This run uses the current shipped style, **not** the older candidate-v4 voice rules.

## Why neither judge is ground truth

The second review was necessary, but it did not eliminate judging errors.

1. **GPT-4.1 punished correct deduplication.** Astra's first onboarding output preserved the update
   request and separate booking request, yet received 70 because the judge claimed that merging
   repetition dropped another request. That directly conflicts with the task and reference.
2. **GPT-4.1 described an error that was not present.** Its grammar-case rationale complained about
   a sentence starting with a digit, although the output said "we had to rerun it 3 times."
3. **GPT-5.2 also misgraded a correct correction.** The exact same Astra `voice-with-values`
   output scored **74.75 in one pass and 99.05 in the other**. The lower grade objected to replacing
   "2.4 no way 2.5" with 2.5, precisely the correction the case requires.
4. **Reference knowledge can leak into the assessment.** "Then it'll compare" already exists in
   the ASR input, but judges sometimes blame the editor for changing an original "I'll" that only
   the reference reveals. Irrecoverable ASR ambiguity must not be mistaken for an introduced error.

The raw grades and rationales remain intact in the evidence bundle. They were not selectively
adjusted to improve Astra's ranking. In particular, a shared prompt does not make reference bias
"cancel out" across models.

The blind judge saw all five anonymized outputs together for each case, separately for each
pass. Candidate positions rotated between cases. It received no model identities, timings, or
first-judge scores. Its four dimensions were weighted **45% fidelity, 25% instruction adherence,
20% disfluency removal and 10% mechanics**. It used GPT-5.2 with reasoning `none`, JSON output,
and no temperature override. Both successful judges are OpenAI-family models; this is not a
human or cross-vendor panel. An attempted Opus agent review produced no artifact and contributed
no scores.

## Original and focused suites

These are subsets of the **new** 25-case run, scored by the existing GPT-4.1 rubric. They must not
be spliced into the July board, whose prompts and ASR bytes differ.

| Model | Original six cases | Three phonetic-transcript cases | Six AI/model-name cases |
|---|---:|---:|---:|
| Astra | 88.67 | 96.33 | 84.75 |
| Sol | 88.58 | 97.50 | 89.92 |
| Terra | 84.17 | 97.83 | 85.33 |
| Luna | 87.67 | 97.67 | 85.42 |
| Mini | 70.67 | 98.00 | 73.83 |

The phonetic subset remains easy for all five. The garbled technical conversations and subjective
editing choices separate outputs more than homophone correction does. The judging limitations above
apply to every column.

## Style and auxiliary-prompt suites

One additional pass used the existing deterministic suites through the actual cleanup service.

| Model | Writing-style scenarios | Auxiliary scenarios | Unmet requirements |
|---|---:|---:|---|
| Astra | **9/9** | **3/5** | Usage-insight grounding: introduced CI/CD and Cardiovascular, absent from the supplied summaries. |
| Terra | 8/9 | 4/5 | One archaic-marker requirement; usage-insight grounding introduced CI/CD. |

Both passed the two dictionary-suggestion cases. These are narrow marker/grounding results, not a
complete semantic assessment, and were not averaged into the cleanup leaderboard. Astra does not
clear the entire auxiliary suite, so this is **not** a recommendation to replace every AI helper
with it. No prompts or evaluators were weakened to make the failures pass.

## Method and deployment details

- Windows `Scribe.Evals`, calling the real `TextCleanupService`, not a substitute HTTP cleanup
  script. The separate blind judge used direct Responses requests.
- Twenty-five synthetic passages passed through SAPI TTS and bundled Parakeet ASR. Three cases
  deliberately replace the recognized text with their authored phonetic transcript. All models
  then receive identical cached transcript strings. No personal dictation history was used.
- Two passes, three sequential timed calls per case, one discarded warmup per model per pass.
  Order was Astra, Terra, Luna, Sol, Mini, then the reverse. Cleanup timings exclude ASR,
  model readiness, warmup, grading and text injection.
- Current source frontier/writing prompts; no glossary, temperature, reasoning, or output-token
  overrides on the cleanup calls. Production retries remained enabled, with the harness's
  180-second per-cleanup timeout. The existing GPT-4.1 judge also had no temperature override.
- Astra and the 5.6 trio: `mtech-sc-resource`, South Central US, DataZoneStandard. Astra version
  `2026-09-03`, capacity 100; 5.6 versions `2026-07-09`, capacities 121 for Terra/Luna and 137
  for Sol.
- Mini: `mtech-project-resource`, East US 2, GlobalStandard, version `2026-03-17`, capacity 500.
  Judges also used that resource: GPT-4.1 version `2025-04-14`, GPT-5.2 version `2025-12-11`.
- Reported median pools all 150 samples per model. p95 is nearest rank, `ceil(0.95 * N)`.
  Endpoints, region, SKU, capacity and model revision are recorded; deployment latency is not
  a universal property of a model name.
- Only synthetic text went to Azure. WAV files stayed local. Response storage was disabled for
  cleanup and both judges. Local/offline models and the separate macOS harness were not rerun.

Astra averaged about 81 reasoning tokens per timed call, versus Terra's 11. This is not a complete
latency explanation: Luna averaged about 90 and was still faster than Astra. There was no
reasoning-effort tuning experiment, and no price comparison was made.

The measurement window was **21:40:40 to 22:36:51 UTC**. Source was base commit
`21cc5b4da77310f5c24be8e40f783c40d1f24f37` plus the existing working-tree account-inference fixes
and the benchmark changes below, using SDK 10.0.400. Assembly hashes remained constant throughout
the timed comparison. These are not measurements of an unmodified release commit.

## Evidence and reproduction

[The checked-in JSON evidence](benchmarks/gpt6-astra-2026-09-04.json) contains the portable
fixtures, effective prompts, all 750 timings and token records, all 250 retained outputs, both
sets of grades and rationales, deployment metadata, assembly hashes and aggregate calculations.
Local WAVs, run logs and analysis scripts remain in this session's `files\astra-eval` artifact
folder. Auxiliary console excerpts are truncated by the existing harness.

To replay one model against the frozen text and prompts, use a fresh output directory:

```powershell
$evidence = Get-Content .\docs\benchmarks\gpt6-astra-2026-09-04.json -Raw | ConvertFrom-Json
$out = Join-Path '.\artifacts' ("astra-replay-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$evidence.fixtures | ConvertTo-Json -Depth 8 | Set-Content "$out\cases.json" -Encoding utf8
$evidence.prompts.frontier | Set-Content "$out\frontier.txt" -Encoding utf8
$evidence.prompts.writing_style | Set-Content "$out\style.txt" -Encoding utf8

dotnet run --project .\tools\Scribe.Evals -- --benchmark --no-local `
  --cloud-models gpt-6-astra --runs 3 `
  --endpoint https://mtech-sc-resource.cognitiveservices.azure.com/ `
  --subscription "<benchmark-subscription-id>" `
  --judge-endpoint https://mtech-project-resource.cognitiveservices.azure.com/ `
  --judge-model gpt-4.1 `
  --frontier-prompt-file "$out\frontier.txt" --writing-style-file "$out\style.txt" `
  --out $out
```

Repeat with a separate directory for every model and pass, preserving the same cases and prompt
files. Use the East US 2 endpoint for Mini. This replay exercises cleanup with frozen ASR output;
it does not regenerate audio. The original complete `cases.json` SHA-256 was
`64FB7FB0688493B4E47B41B3A644EC1AFBA728473305A88A984F1144CB83E00B`; the portable export omits local
WAV paths, so its file hash intentionally differs.

For the supplementary scenarios:

```powershell
dotnet run --project .\tools\Scribe.Evals -- --provider azure --suite all `
  --models gpt-6-astra,gpt-5.6-terra `
  --endpoint https://mtech-sc-resource.cognitiveservices.azure.com/ `
  --subscription "<benchmark-subscription-id>" --verbose
```

### Harness repair required for this run

Tenant-only authentication selected the wrong cached Azure CLI account even though the benchmark
subscription already authenticated successfully. `--subscription` now pins discovery, cleanup and
direct diagnostics without changing the global CLI default. `--judge-subscription` can override
the judge independently and otherwise inherits that pin. The run also exposed an empty-roster
success exit; that now fails explicitly. Judge and direct diagnostic Responses requests now disable
server-side storage, matching the production cleanup privacy control.
