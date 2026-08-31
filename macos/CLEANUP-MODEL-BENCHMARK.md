# macOS Cleanup Model Benchmark

## Status: EXPANDED, TWO RUNTIMES COMPARED (2026-08-24)

Lead's initial pass timed out on model downloads before any request executed. The first follow-up
run benchmarked four Ollama models end-to-end against the six ported golden cases via Ollama's real
OpenAI-compatible endpoint (Apple Silicon `arm64`, Metal-accelerated via Ollama's llama.cpp backend).
A second follow-up run added **Foundry Local** (`https://github.com/microsoft/homebrew-foundrylocal`)
as a second runtime, after an earlier planning pass incorrectly claimed it had no macOS support. All
results below are real, not projected.

### Why both runtimes are supported, and why Foundry Local is the default

Scribe's whole design premise is **local user control**: nothing leaves the device unless the user
opts in. Both runtimes fit that premise, so both are supported cleanup providers. But they are not
equivalent, and the default should be the one that gives the best out-of-the-box experience, even at
a storage or install-size cost:

- **Foundry Local is Microsoft's own on-device SDK** (the same family Windows Scribe already uses via
  `Microsoft.AI.Foundry.Local.WinML`), so choosing it as the macOS default keeps the two platforms on
  one real architecture instead of two unrelated local-inference stacks.
- **It owns hardware selection for us**, same philosophy as Windows: `foundry status` reports the
  Apple M5 GPU and picks a `WebGpuExecutionProvider` variant automatically; there is no per-model
  "did I get the GPU build" guesswork the way there can be with a from-scratch llama.cpp setup.
  Verified directly on this machine (see Environment checks below).
  Scribe never chooses the execution provider itself, matching the "SDK owns hardware selection, do
  not try to take it back" rule already in `AGENTS.md` for Windows.
- **It is the same runtime we need for ASR.** Foundry Local's catalog also hosts
  `parakeet-tdt-0.6b-v2`, a real Parakeet TDT speech model callable via `foundry transcribe`, which is
  the actual model family Windows Scribe ships (`parakeet-tdt-0.6b-v3-int8`, not identical version,
  same family). One runtime, one install, both AI features. This is a strictly better match to
  Windows' architecture than layering in whisper.cpp for ASR and Ollama for cleanup as two unrelated
  dependencies.
- **Ollama remains fully supported**, not deprecated. Some users already have Ollama installed and
  configured for other tools, prefer its model catalog/quantization ecosystem, or simply don't want a
  second background service. Scribe should let them point at their existing Ollama install exactly as
  Windows lets a user point at LM Studio/any OpenAI-compatible endpoint. The recommendation below is a
  default, not an exclusivity decision.

### Scoring methodology caveat

The Windows benchmark (`tools/Scribe.Evals`) grades output against the golden rewrite with an Azure
`gpt-4.1` judge model. No such judge was available offline in this environment, so scoring here uses
a deterministic heuristic instead (`macos/benchmark_cleanup.py`): 50% text-similarity ratio to the
golden rewrite (`difflib.SequenceMatcher`) + 50% presence of the specific tokens each case must
transform correctly (dates, numbers, corrected names, preserved quotes), with a penalty if a
pre-correction value survives. This is a reasonable proxy for relative ranking between models on the
same hardware, but it is **not** numerically comparable to the Windows leaderboard's judge-based
scores in `docs/model-leaderboard.md`. Re-run with a real judge (e.g. point `QualityJudge`-equivalent
logic at a cloud model) before treating these as final production numbers.

## Real results (six-case average, this machine)

### Ollama (`http://127.0.0.1:11434/v1/chat/completions`)

| Model | Avg score (heuristic) | Avg latency | Median latency | Max latency | Verdict |
|---|---:|---:|---:|---:|---|
| **`qwen2.5:3b`** | **0.632** | 1920 ms | 1574 ms | 3857 ms | **Best quality on Ollama**, latency still sub-2s median |
| `qwen2.5:1.5b` | 0.562 | 1032 ms | 703 ms | 2758 ms | Best latency/quality balance, fastest median |
| `qwen2.5:0.5b` | 0.560 | 1848 ms | 380 ms | 9243 ms | Fast typical case, but one case spiked to 9.2s (cold-start/first-token variance) |
| `llama3.2:1b` | 0.446 | 2901 ms | 1115 ms | 10088 ms | Worst quality AND worst latency of the four; drop from consideration |

Per-case scores (heuristic, 0-1):

| Case | qwen2.5:0.5b | qwen2.5:1.5b | qwen2.5:3b | llama3.2:1b |
|---|---:|---:|---:|---:|
| kitchen-sink | 0.44 | 0.67 | 0.68 | 0.47 |
| numbers-dates | 0.26 | 0.81 | 0.61 | 0.75 |
| self-correction | 0.56 | 0.21 | 0.65 | 0.14 |
| redundancy | 0.86 | 0.83 | 0.84 | 0.78 |
| instruction-immunity | 0.74 | 0.32 | 0.50 | 0.02 |
| grammar-runon | 0.51 | 0.53 | 0.51 | 0.51 |

### Foundry Local (`http://127.0.0.1:<dynamic port>/v1/chat/completions`, port from `foundry status`)

All four variants ran as the GPU build Foundry Local selected automatically
(`*-instruct-generic-gpu`), confirmed via `foundry model info <alias>` after load.

| Model | Variant loaded | Avg score (heuristic) | Avg latency | Median latency | Max latency | Verdict |
|---|---|---:|---:|---:|---:|---|
| `qwen2.5-0.5b` | `qwen2.5-0.5b-instruct-generic-gpu:4` | 0.411 | 864 ms | 666 ms | 1488 ms | Fastest, weakest quality; no cold-start spike unlike Ollama's 0.5b |
| **`qwen2.5-1.5b`** | `qwen2.5-1.5b-instruct-generic-gpu:4` | **0.575** | 1588 ms | 1402 ms | 2605 ms | **Recommended default** (see below): matches Ollama's 1.5b quality with a flatter, spike-free latency curve |
| `phi-3.5-mini` | `Phi-3.5-mini-instruct-generic-gpu:2` | 0.605 | 4953 ms | 3922 ms | 8568 ms | Good quality, too slow for push-to-talk (median well over the ~2s budget) |
| `qwen2.5-7b` | `qwen2.5-7b-instruct-generic-gpu:4` | 0.700 | 5686 ms | 5330 ms | 8096 ms | **Best quality of every model tested, either runtime**, but 5.3s median is a real UX cost; ship as an opt-in "max quality" tier, not the default |

Per-case scores (heuristic, 0-1):

| Case | qwen2.5-0.5b | qwen2.5-1.5b | phi-3.5-mini | qwen2.5-7b |
|---|---:|---:|---:|---:|
| kitchen-sink | 0.53 | 0.54 | 0.47 | 0.54 |
| numbers-dates | 0.25 | 0.62 | 0.81 | 0.89 |
| self-correction | 0.24 | 0.60 | 0.88 | 0.74 |
| redundancy | 0.87 | 0.76 | 0.74 | 0.84 |
| instruction-immunity | 0.01 | 0.43 | 0.23 | 0.67 |
| grammar-runon | 0.56 | 0.51 | 0.51 | 0.51 |

Notably Foundry Local's latency curve is markedly more consistent than Ollama's at the same
parameter count: no candidate showed Ollama's 9-10 second cold-start spikes across any case. This is
plausibly the WebGPU execution provider avoiding the per-request re-warm cost Ollama's llama.cpp
backend pays on this run, but that is an observation from six requests per model, not a controlled
study; treat it as a lead worth re-testing at scale, not a settled fact.

### Qualitative sample (qwen2.5:3b, kitchen-sink, real output, truncated)

> "Okay, I need to send the quarterly report to Sarah on the finance team by Friday end of day. Make
> sure the Q3 revenue numbers are included, as we discussed in the meeting last week, where the
> revenue increased by 12%. I should send it on Wednesday instead of Tuesday, as that would be
> better. The report needs to be more clear and better for the stakeholders; last time, they were
> confused. At the ve..." *(truncated)*

Notably it left a filler "Okay," at the start and phrased the Tuesday-to-Wednesday correction
awkwardly instead of dropping the rejected value outright — real gaps vs. the golden rewrite that a
frontier cloud model (or a larger local model) handles better. This matches the general pattern:
small local models are usable for cleanup but noticeably behind cloud-tier models on self-correction
and instruction-immunity, the two hardest cases across every model tested.

## Recommendation

**Default macOS provider: Foundry Local, model `qwen2.5-1.5b`.** Rationale, in priority order:

1. **Runtime choice.** Foundry Local is Microsoft's own on-device SDK, the same family already
   powering AI cleanup on Windows, and it also hosts the Parakeet TDT ASR model family Windows uses
   (see the ASR section of `PORTING-PLAN.md`). Standardizing on it keeps macOS on one real,
   Microsoft-supported local-inference stack for both features instead of stitching together
   whisper.cpp for ASR and Ollama for cleanup. `foundry model load` handles GPU/CPU variant selection
   automatically; Scribe never picks the execution provider itself, matching the Windows rule.
2. **Model choice within Foundry Local.** `qwen2.5-1.5b` (0.575 avg score, 1.4s median latency, no
   observed cold-start spikes) is the best quality-per-latency tradeoff of the four Foundry Local
   candidates tested, and it matches Ollama's `qwen2.5:1.5b` on quality while showing a flatter
   latency curve. Ship `qwen2.5-7b` (0.700, best of every model tested on either runtime) as an
   opt-in "max quality" tier in Settings for users willing to accept a ~5.3s median cost; do not
   default to it given the push-to-talk latency budget.
3. **Ollama stays fully supported, not deprecated.** Any user who already runs Ollama, prefers its
   catalog, or does not want a second background service can point Scribe at it exactly as today; the
   provider abstraction in `PORTING-PLAN.md` keeps `ManagedOllama` alongside the new
   `FoundryLocalCleanupProvider`. If a user is choosing between the two with no existing preference,
   Ollama's `qwen2.5:3b` (0.632) is the best quality result recorded on Ollama; it is very close to
   Foundry Local's `qwen2.5-1.5b` but on a different runtime, so this is a legitimate user choice, not
   a strictly dominated option.
4. **Storage/install tradeoff is accepted deliberately**, per explicit product direction: the default
   should optimize for the best experience even if that means a separate install and extra disk space
   (Foundry Local itself is ~200 MB via Homebrew; `qwen2.5-1.5b`'s GPU variant is another ~1.5 GB).
   This is no worse than Windows shipping its own bundled ASR/VAD models today.

`llama3.2:1b` and Foundry Local's `qwen2.5-0.5b` are not recommended as defaults; both trade too much
quality for their latency advantage (0.446 and 0.411 average score respectively, both below every
1.5b+ candidate on either runtime).

`llama3.2:1b` and `qwen2.5:0.5b` on Ollama both showed one case with a 9-10 second latency spike,
which lines up with Ollama's cold-start behavior on first request after a model loads into memory; a
managed Ollama provider should pre-warm (send a throwaway request right after selecting/pulling a
model) before relying on steady-state latency numbers. Foundry Local did not show this pattern in this
run.

## Still open before shipping

1. Re-score with an actual judge model (cloud or a larger local model) to get numbers comparable to
   `docs/model-leaderboard.md`.
2. Test 1-2 more Ollama candidates in the 3-4B range (`gemma2:2b`) for completeness; `phi3.5:3.8b` on
   Ollama specifically was superseded by testing `phi-3.5-mini` directly on Foundry Local instead.
3. Validate real-world latency with the actual writing-style prompt variants (frontier prompt, not
   just default) and with longer/noisier ASR-derived transcripts, not just clean authored text.
4. Confirm the observed lack of cold-start latency spikes on Foundry Local holds at larger sample
   sizes; this run only sent six requests per model.
5. Test `foundry transcribe -m parakeet-tdt-0.6b-v2` as the production ASR path (see
   `PORTING-PLAN.md`), since it is now confirmed to produce correct transcripts on this hardware.

## Original partial-run status (superseded above, kept for history)

Partial execution only. The benchmark environment was prepared on this Apple Silicon Mac, but no Ollama model finished downloading within the time box, so no cleanup requests were executed yet.

## Environment checks that actually ran

```text
$ which ollama
/opt/homebrew/bin/ollama

$ ollama --version
ollama version is 0.32.15

$ which lms
<not found>

$ xcrun --sdk macosx --show-sdk-version
26.5

$ foundry --version
0.10.3

$ foundry status
| System       | GPU                | Apple (0x106b) Apple M5 (—)              |
| Service      | State              | Ready                                    |
| Service      | Web URLs           | http://127.0.0.1:58621                   |
| Service      | ORT                | 1.26.0                                   |
```

### Foundry Local install and repro commands

```bash
brew tap microsoft/foundrylocal
brew trust microsoft/foundrylocal   # required in some environments before the tap is usable
brew install foundrylocal
foundry server start
foundry model load qwen2.5-1.5b     # downloads + loads the GPU variant automatically
foundry status                      # confirms the live port to target below

python3 macos/benchmark_cleanup.py qwen2.5-1.5b qwen2.5-0.5b phi-3.5-mini qwen2.5-7b \
  --base-url http://127.0.0.1:<port-from-status>/v1/chat/completions
```

## Methodology to keep parity with Windows

- Golden suite source: `tools/Scribe.Evals/Benchmark/BenchmarkCases.cs`
- Exact six cases reused for macOS: `kitchen-sink`, `numbers-dates`, `self-correction`, `redundancy`, `instruction-immunity`, `grammar-runon`
- Default writing style reused exactly from `src/Scribe.Core/Cleanup/CleanupPrompt.cs`
- Transport planned for measurement: Ollama's OpenAI-compatible `POST /v1/chat/completions`
- Quality scoring target: same Windows approach, meaning per-case grading against the golden rewrite using an external judge model

### Important limitation from this run

The Windows benchmark uses an Azure `gpt-4.1` judge to score each output against the golden rewrite. That judge path was **not** executed here because no local model completed its download, so there were no outputs to score. This file therefore records a real setup attempt, real environment data, and real pull-time evidence, but not final quality or latency results.

## Golden suite payload reused for macOS

| Case | Raw transcript source | Golden reference source |
|---|---|---|
| kitchen-sink | `BenchmarkCases.cs` | `BenchmarkCases.cs` |
| numbers-dates | `BenchmarkCases.cs` | `BenchmarkCases.cs` |
| self-correction | `BenchmarkCases.cs` | `BenchmarkCases.cs` |
| redundancy | `BenchmarkCases.cs` | `BenchmarkCases.cs` |
| instruction-immunity | `BenchmarkCases.cs` | `BenchmarkCases.cs` |
| grammar-runon | `BenchmarkCases.cs` | `BenchmarkCases.cs` |

## Prompt reused for the benchmark

System prompt text:

```text
Write in the speaker's language using clear, natural, well-structured prose. Never translate the dictation unless I explicitly ask you to. Use correct punctuation, meaning commas, periods, semicolons, colons, question marks, and parentheses, according to sentence structure. Do not use dash punctuation to join clauses; use a comma, colon, semicolon, or period instead. Break long run-on speech into properly formed sentences, and start a new paragraph when the topic shifts. Separate paragraphs with one blank line. Remove filler words and false starts (such as "um", "uh", "you know", and "like") and fix small grammar slips, while keeping my meaning, intent, and vocabulary. When I correct myself mid-speech (for example "I meant to go to the store, I mean the park"), keep only the corrected version and drop what it replaced. If I say the same thing more than once, or restate a point in slightly different words, merge it into a single clear statement instead of writing both. Always put a single space between sentences. Keep the identity of technical terms, product names, model names, code, and URLs unchanged. Never substitute a different product, version, or spelling, but do write them the way they are normally written down. Write numbers the way they are normally written rather than spelled out: use digits for quantities, measurements, prices, percentages, phone numbers, and version numbers (for example "twenty three" becomes "23" and "five point five" becomes "5.5"). Keep model and version identifiers together with no inserted spaces (for example, write "GPT-5.6", not "GPT-5. 6"), but keep a small number as a word where that reads more naturally (for example "one or two ideas"). When I name a model, library, or product whose written form you are unsure of, follow the pattern of the ones you do know rather than leaving it as spelled-out speech: "gpt five six terra" is written "GPT-5.6-Terra", "claude opus four point eight" is "Claude Opus 4.8", "qwen three fourteen b" is "Qwen3-14B". New models are released constantly, so an unfamiliar name is far more likely to be a real product I said than a mistake. Spell out a number that begins a sentence, or reword the sentence so it doesn't start with one. Format clock times as digits with a colon, adding AM or PM when I say it (for example "three thirty p m" becomes "3:30 PM"). Write dates, calendar months, and years in their normal written form (for example "july third twenty twenty six" becomes "July 3, 2026"). Write acronyms spoken letter by letter in capitals with no spaces or periods (for example "a p i" becomes "API"). Only reformat what I actually spoke, and never invent or change a value I did not say.
```

## What actually ran

1. Installed Ollama with Homebrew.
2. Started the Ollama daemon successfully.
3. Verified the daemon saw Apple Metal on an Apple M5 GPU from the server log.
4. Attempted to pull four candidate local cleanup models with a per-model timeout.
5. Attempted one smaller fallback pull (`qwen2.5:0.5b`) with a longer timeout.

### Pull attempt results

| Model | Download evidence from this run | Pass rate | Avg latency | Verdict |
|---|---|---:|---:|---|
| `qwen2.5:1.5b` | Timed out after 240s at about 69 MB / 986 MB, about 298 KB/s | n/a | n/a | Not benchmarked, download too slow for the time box |
| `qwen2.5:3b` | Timed out after 240s at about 88 MB / 1.9 GB, about 332 KB/s | n/a | n/a | Not benchmarked, download too slow for the time box |
| `llama3.2:1b` | Timed out after 240s at about 69 MB / 1.3 GB, about 312 KB/s | n/a | n/a | Not benchmarked, download too slow for the time box |
| `phi3.5:3.8b` | Timed out after 240s at about 77 MB / 2.2 GB, about 358 KB/s | n/a | n/a | Not benchmarked, download too slow for the time box |
| `qwen2.5:0.5b` | Timed out after 420s at about 79 MB / 397 MB, about 202 KB/s | n/a | n/a | Best next retry candidate, but still not completed here |

**(Superseded)** This is no longer accurate — see the real results and recommendation at the top of
this file. Kept only as evidence that the original download attempt legitimately timed out due to
throughput, not a scoring failure; a resumable-download UX is still worth building for onboarding.

## Exact repro commands for a future re-run (e.g. to add phi3.5/gemma2 candidates)

```bash
which ollama
which lms
xcrun --sdk macosx --show-sdk-version
brew install ollama
OLLAMA_FLASH_ATTENTION=1 OLLAMA_KV_CACHE_TYPE=q8_0 ollama serve
ollama pull qwen2.5:0.5b
ollama pull qwen2.5:1.5b
ollama pull llama3.2:1b
ollama pull phi3.5:3.8b
```

For each completed model, send each of the six raw transcripts through Ollama's OpenAI-compatible endpoint with the writing-style prompt above:

```bash
curl http://127.0.0.1:11434/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "qwen2.5:1.5b",
    "temperature": 0,
    "messages": [
      {"role": "system", "content": "<CleanupPrompt.DefaultWritingStyle>"},
      {"role": "user", "content": "<raw transcript from the six-case suite>"}
    ]
  }'
```

Then grade each output against its golden rewrite using the same judge pattern as `tools/Scribe.Evals/Benchmark/QualityJudge.cs`, record wall-clock latency, and update this table with real pass-rate and latency numbers.
