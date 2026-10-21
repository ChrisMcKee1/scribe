# Scribe AI Cleanup: Model Leaderboard

Updated October 21, 2026. Speed and quality benchmark of cloud frontier models (Microsoft Foundry
in South Central US and GitHub Copilot) against the top local models on this PC. For the full
40+ model local comparison across Ollama, TensorSharp, LM Studio and Foundry Local, see the
[Local AI Cleanup Benchmark](local-model-benchmark.md).

## TL;DR: What to Use

| Scenario | Recommended Model | Where It Runs | Quality (/100) | Median (P50) | P95 | Why |
|---|---|---|---:|---:|---:|---|
| **Cloud default (best balance)** | **`gpt-5.6-terra`** | Microsoft Foundry (`DataZoneStandard`, South Central US) | **94.6** | **1.30 s** | **1.78 s** | Tied at the top on quality, fastest frontier p95, zero reasoning overhead. |
| **Cloud, highest fidelity** | **`gpt-6.1-sol`** | Microsoft Foundry (`DataZoneStandard`, South Central US) | **94.2** | 1.98 s | 6.10 s | Highest raw fidelity (93.8) with low reasoning effort (`13` reasoning tokens). |
| **Cloud, sub-second frontier** | **`gpt-6-luna`** | Microsoft Foundry (`DataZoneStandard`, South Central US) | **92.3** | **1.00 s** | 2.69 s | Sub-second median latency with frontier self-correction and formatting. |
| **Cloud, Claude alternative** | **`claude-sonnet-5.5`** | GitHub Copilot / Anthropic | **94.0** | 10.60 s | 16.05 s | Matches `gpt-6.1-sol` on fidelity (93.6), but Copilot session setup adds latency. |
| **Cloud, budget / minimum latency** | **`gpt-5.4-mini`** | Microsoft Foundry (`DataZoneStandard`, South Central US) | **87.9** | **0.98 s** | **1.30 s** | Tightest latency tail in the cloud; ideal for quick everyday dictation. |
| **Local, 16 GB GPU (#1 overall)** | **`gemma4:26b-a4b-iq3s`** | Ollama (`32K` context, MTP draft head) | **93.4 to 94.1** | **0.46 s** | **1.04 s** | Matches cloud frontier quality in under half a second with zero token cost. |
| **Local, 16 GB GPU (TensorSharp)** | **`Gemma 4 26B-A4B (`UD-IQ3_S`)`** | TensorSharp (`ggml_cuda`, `16K` context) | **93.7** | **1.09 s** | **1.56 s** | Persistent disk-backed Radix prefix cache; 3.4x faster than LM Studio. |
| **Local, zero external install** | **`qwen2.5-7b` / `qwen2.5-1.5b`** | Foundry Local (built in) | **85.4 / 74.0** | **0.74 s / 0.37 s** | 1.12 s / 0.64 s | Automatic TensorRT-RTX / CUDA hardware selection with nothing else to install. |

---

## Current 25-Case Leaderboard (October 2026)

Every model below ran Scribe's production `TextCleanupService` across the frozen 25-case suite
([evidence](benchmarks/gpt6-astra-2026-09-04.json)) with Scribe's shipped writing style,
mentioned-vocabulary selection (`VocabularyMentions`) and production response guards. All outputs
were graded together in identity-blind packets by Claude Sonnet 5.5 across four dimensions:
**45% fidelity, 25% instruction adherence, 20% disfluency removal and 10% mechanics**.

| Rank | Model | Provider & SKU | Quality | 95% CI | Fid / Inst / Disf / Mech | Fallbacks | Median (P50) | P95 |
|---:|---|---|---:|---|---:|---:|---:|---:|
| 1 | **`gpt-5.6-terra`** | Foundry (`DataZoneStandard`, South Central US) | **94.6** | 91.9 to 96.7 | 92.8 / 95.0 / 96.7 / 96.8 | 0/50 | **1,296 ms** | **1,784 ms** |
| 2 | **`gpt-6.1-sol`** | Foundry (`DataZoneStandard`, South Central US, `low` reasoning) | **94.2** | 91.9 to 96.1 | **93.8** / 93.0 / 96.1 / 95.4 | 0/50 | 1,982 ms | 6,098 ms |
| 3 | **`claude-sonnet-5.5`** | GitHub Copilot | **94.0** | 91.2 to 96.6 | 93.6 / 93.2 / 95.2 / 95.4 | 0/50 | 10,604 ms | 16,050 ms |
| 4 | **`gemma4:26b-a4b-iq3s`** | Local Ollama (`RTX 5080`, `32K` context, MTP) | **93.4** (94.1 local pool) | 90.2 to 95.9 | 91.4 / 94.1 / 95.9 / 95.7 | 0/50 | **462 ms** | **1,041 ms** |
| 5 | **`gpt-6-sol`** | Foundry (`DataZoneStandard`, South Central US, `none` reasoning) | **93.1** | 90.8 to 95.2 | 90.6 / 93.4 / 96.8 / 96.2 | 0/50 | 1,494 ms | 2,059 ms |
| 6 | **`gpt-6-luna`** | Foundry (`DataZoneStandard`, South Central US) | **92.3** | 88.2 to 95.9 | 89.8 / 92.9 / 96.0 / 95.2 | 0/50 | **995 ms** | 2,685 ms |
| 7 | `MAI-Thinking-1` | Foundry (`GlobalStandard`, South Central US) | 90.7 | 87.4 to 93.6 | 89.4 / 90.7 / 92.2 / 93.2 | 0/50 | 11,944 ms | 21,065 ms |
| 8 | `Mistral-Large-3` | Foundry (`DataZoneStandard`, South Central US) | 90.6 | 85.7 to 94.3 | 88.5 / 91.5 / 92.3 / 94.1 | 0/50 | 1,387 ms | 6,683 ms |
| 9 | `grok-4-1-fast-non-reasoning` | Foundry (`DataZoneStandard`, South Central US) | 89.3 | 84.0 to 93.7 | 89.6 / 88.7 / 87.7 / 92.6 | 0/50 | 1,381 ms | 2,539 ms |
| 10 | `grok-4.7` | Foundry (`DataZoneStandard`, South Central US) | 89.2 | 81.3 to 95.6 | 91.7 / 85.2 / 89.0 / 87.9 | 6/50 | 12,819 ms | 45,024 ms |
| 11 | `gpt-5.4-mini` | Foundry (`DataZoneStandard`, South Central US) | 87.9 | 82.0 to 92.7 | 89.7 / 86.6 / 84.2 / 90.3 | 0/50 | **980 ms** | **1,303 ms** |
| 12 | `Phi-4` | Foundry (`GlobalStandard`, South Central US) | 76.3 | 70.5 to 81.7 | 69.3 / 77.6 / 83.8 / 89.5 | 0/50 | 1,874 ms | 3,488 ms |
| 13 | `nemotron-3-nano:4b` | Local Ollama (`RTX 5080`, `32K` context) | 71.4 | 64.3 to 78.3 | 67.7 / 72.1 / 75.2 / 78.6 | 0/50 | **321 ms** | **544 ms** |
| 14 | `Phi-4-mini-instruct` | Foundry (`GlobalStandard`, South Central US) | 37.8 | 32.4 to 43.1 | 49.2 / 13.8 / 39.9 / 42.1 | 25/50 | 4,204 ms | 35,197 ms |

*Earlier South Central US comparison note:* `gpt-6-astra` (`DataZoneStandard`, South Central US)
scored within 0.3 points of `gpt-5.6-terra` and `gpt-5.6-sol` across 150 samples, at a 5.53 s
median (8.52 s p95). `gpt-5.6-luna` scored within 1.4 points at a 2.59 s median.

---

## Key Findings

1. **Frontier OpenAI vs. Claude Sonnet 5.5 vs. Local Gemma 4 26B-A4B:**
   `gpt-5.6-terra` (94.6), `gpt-6.1-sol` (94.2), `claude-sonnet-5.5` (94.0) and local
   `gemma4:26b-a4b-iq3s` (93.4 in the 14-model pool, 94.1 in the local pool) are statistically
   tied at the top of the board. On a 16 GB desktop GPU, `gemma4:26b-a4b-iq3s` delivers the same
   tier of rewrite quality in **462 ms** (nearly 3x faster than `gpt-5.6-terra` and 23x faster than
   `claude-sonnet-5.5` over the Copilot SDK).
2. **Reasoning models pay a steep latency tax without improving dictation cleanup:**
   - Scribe automatically negotiates the lowest reasoning effort a Microsoft Foundry deployment
     accepts (`none` for `gpt-5.6-terra`, `gpt-6-sol` and `gpt-6-luna`; `low` for `gpt-6.1-sol`).
     Turning reasoning off cut `gpt-6-sol` latency from 2.04 s to 1.34 s at identical quality.
   - Models that force long reasoning traces (`MAI-Thinking-1` at 11.9 s median with 600 to 1,200
     reasoning tokens per call, `grok-4.7` at 12.8 s median with 6/50 timeouts, and earlier runs
     of `gpt-5.4-pro` at 57.6 s) are too slow for interactive push-to-talk dictation and score no
     higher than non-reasoning frontier models.
3. **Strong non-OpenAI cloud options in South Central US:**
   `Mistral-Large-3` (**90.6** at **1.39 s** median) and `grok-4-1-fast-non-reasoning` (**89.3** at
   **1.38 s** median) both run cleanly on `DataZoneStandard` in South Central US with zero fallbacks
   and outperform `gpt-5.4-mini` (87.9) on quality.
4. **Microsoft MAI and Phi models on Foundry:**
   - `MAI-Thinking-1` (90.7) is accurate and had zero fallbacks, but its mandatory thinking tokens
     make it an 11.9 s model.
   - `Microsoft-Decision-1` is a decision-only model (`chatCompletion=false`) and does not serve
     conversational rewrites.
   - `Phi-4` (76.3 at 1.87 s) handles basic punctuation and filler removal well but struggles on
     multi-step self-corrections; `Phi-4-mini-instruct` (37.8) failed Scribe's response guards on
     half of the cases.
5. **Mentioned vocabulary cuts cloud token usage by 80% while improving accuracy:**
   Sending only the dictionary and word pack terms a dictation appears to mention (`VocabularyMentions`)
   reduced average cloud input from ~7,500 tokens to ~1,400 tokens per dictation while preserving
   100% vocabulary accuracy on frontier models.

---

## Broader Golden-Suite Reference (Earlier 6-Case Cloud Survey)

For reference on older or secondary Azure deployments tested on the 6-case Golden Suite (`gpt-4.1`
golden judge):

| Model | Quality (/100) | Grade | Median | Notes |
|---|---:|---|---:|---|
| `gpt-5.2` | 90 | A- | 2.86 s | High quality, superseded by `gpt-5.6-terra` and `gpt-6.1-sol`. |
| `gpt-5.4` | 87 | B+ | 1.82 s | Superseded by `gpt-6-luna` (995 ms) and `gpt-5.6-terra` (1.30 s). |
| `gpt-chat-latest` | 86 | B | 1.70 s | Fast non-reasoning chat deployment. |
| `gpt-5.1` | 86 | B | 2.30 s | Older reasoning generation. |
| `gpt-4.1` | 85 | B | 1.95 s | Reliable non-reasoning baseline. |
| `DeepSeek-V4-Flash` | 85 | B | 2.46 s | Fast when warm; South Central `DataZoneStandard` cold-start can exceed 30 s. |
| `gpt-5.3-codex` | 82 | B- | 1.41 s | Code-tuned; occasionally alters prose quotes. |
| `gpt-4o` | 82 | B- | 1.52 s | Legacy baseline. |
| `DeepSeek-V3.2` | 81 | B- | 2.57 s | Superseded by V4. |
| `cohere-command-a` | 80 | B- | 2.61 s | Solid mechanics, weaker on self-corrections. |
| `gpt-4.1-mini` / `gpt-5.4-nano` | 79 / 77 | C+ | 1.88 s / 1.69 s | Slower and no better than `gpt-5.4-mini` (980 ms). |
| `Llama-3.3-70B-Instruct` | 71 | C- | 2.61 s | Outperformed by local `gemma4:12b` and `gemma4:26b-a4b-iq3s`. |
| `Kimi-K2.5` / `Kimi-K2.6` / `Llama-4-Maverick-17B` | 8 to 14 | F | 3.5 to 6.5 s | Unchanged or unusable output; Scribe falls back to raw transcript. |

---

## Reproduce

```powershell
# Benchmark cloud deployments in South Central US against the 25 frozen cases
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-local --runs 2 --no-judge --no-wav `
  --endpoint https://<resource>.cognitiveservices.azure.com/ `
  --subscription <subscription-id> --tenant <tenant-id> `
  --cloud-models "gpt-5.6-terra,gpt-6.1-sol,gpt-6-sol,gpt-6-luna,gpt-5.4-mini,Mistral-Large-3,grok-4-1-fast-non-reasoning" `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json `
  --glossary-libraries default --vocabulary mentioned --admitted --clean-timeout 0 `
  --out runs/cloud-sc

# Grade all outputs together with the identity-blind judge
dotnet run -c Release --project tools/Scribe.Evals -- --blind-judge `
  --judge-model claude-sonnet-5.5 `
  --judge-results runs/cloud-sc/results.json `
  --out runs/cloud-judge
```
