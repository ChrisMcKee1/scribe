# GPT-5.6 Phonetic Cleanup Benchmark

Updated July 10, 2026.

This report compares `gpt-5.6-sol`, `gpt-5.6-luna`, `gpt-5.6-terra`, `gpt-5.4`, and
`gpt-5.4-mini` on Scribe's real post-ASR cleanup path. It also records two prompt-tuning
experiments, why neither candidate replaced the shipped defaults, and the regional deployment
investigation that made the 5.6 models practical for interactive cleanup.

## Result

| Quality rank | Model and best tested deployment | Quality | Grade | Median | Phonetic quality | Practical verdict |
|---|---|---:|---:|---:|---:|---|
| 1 | `gpt-5.6-luna` · US Data Zone | **92** | A- | 4.12 s | 98 | Highest quality; about 1.4 times slower than Terra. |
| 2 | `gpt-5.6-terra` · US Data Zone | 91 | A- | 2.88 s | 98 | **Best overall balance:** one quality point behind Luna and substantially faster. |
| 3 | `gpt-5.6-sol` · US Data Zone | 90 | A- | 5.70 s | 98 | Slower and lower quality than both Luna and Terra. |
| 4 | `gpt-5.4-mini` · Global Standard | 88 | B+ | **0.96 s** | 98 | Fastest option; best when minimum interaction delay matters most. |
| 5 | `gpt-5.4` · Global Standard | 88 | B+ | 2.00 s | 98 | Same measured quality as Mini at about 2.1 times the latency. |

Quality ties are ordered by speed. The recommended quality/speed default is now
`gpt-5.6-terra` on the US Data Zone deployment. `gpt-5.6-luna` is the maximum-quality option,
and `gpt-5.4-mini` remains the minimum-latency option. Quality scores can move a point or two with
model and judge variance; Terra's recommendation rests on the combination of its near-Luna quality,
lower median, tighter tail, and zero reasoning tokens across the canonical run.

## Strengths And Weaknesses

| Model | Strongest evidence | Main weakness |
|---|---|---|
| `gpt-5.6-luna` | Highest mechanics, fidelity, disfluency, and instruction scores. Scored 90 or better on 10 of 11 cases. | 42.44 s median and a 230.42 s maximum. Its weakest case was the mixed kitchen-sink transcript at 85. |
| `gpt-5.6-sol` | Strong general mechanics and 100 on the story phonetic case. | Slowest median at 51.84 s. Redundancy scored 70, and phonetic dialogue scored 85. |
| `gpt-5.4-mini` | Fastest by a large margin. Scored 98 across the phonetic subset and 95 or better on five cases. | Self-correction scored 60. It sometimes retains rejected alternatives and correction cues. |
| `gpt-5.4` | Stable general editor with 98 phonetic quality and 95 or better on four cases. | Redundancy scored 65. It often leaves semantically duplicate statements. |
| `gpt-5.6-terra` | Self-correction scored 85 and phonetic quality scored 98. | Redundancy and grammar-run-on scored 70. Its 31.38 s median is about 32.7 times Mini's latency. |

## Method

- Eleven authored cases, including dialogue, narrative prose, colloquial speech, numbers,
  corrections, repetition, quoted text, long pauses, and model/version identifiers.
- Eleven generated PCM WAV files. Every WAV passed through Scribe's shipped Parakeet ASR.
- Three cases then use deliberately phonetic, sound-alike transcript spellings while retaining the
  corresponding natural-language WAV and a conventional golden rewrite.
- Canonical case cache SHA-256:
  `21F45D85BB1114384810876EE4375086918878D25EE782FD0D762E5EC54B7A91`.
- Three timed calls per model and case, after one discarded warmup.
- Quality graded per case by `gpt-4.1` against authored golden rewrites.
- Every model used the same case bytes, shipped frontier prompt, shipped writing style, tenant,
  endpoint family, timeout settings, and judge.
- Models were executed in isolated output directories. Runs could proceed concurrently across
  deployments, but each model's own warmup and timed calls remained sequential.

The full generated baseline report, raw outputs, rationales, and timings are in
`artifacts/gpt56-phonetic/leaderboard.md` and `artifacts/gpt56-phonetic/results.json`.

## Phonetic Challenge

| Rank | Model | Mean quality | Median across phonetic cases |
|---|---|---:|---:|
| 1 | `gpt-5.4-mini` | 98 | 0.82 s |
| 2 | `gpt-5.4` | 98 | 1.75 s |
| 3 | `gpt-5.6-luna` | 98 | 26.74 s |
| 4 | `gpt-5.6-terra` | 98 | 31.38 s |
| 5 | `gpt-5.6-sol` | 94 | 50.01 s |

Contextual recovery of sound-alike spellings is largely solved by four models. The remaining
quality differences come primarily from self-correction, semantic deduplication, and fidelity to
quoted or explicitly dictated text.

## Prompt Tuning

Prompt candidates were supplied through benchmark-only files. The shipped constants were not
changed during the experiment. Each generated tuned leaderboard records its complete effective
frontier prompt and writing style.

Candidate v1 added general rules for:

- reading the complete transcript before editing;
- treating explicit correction cues as replacements;
- merging semantic restatements while retaining unique details and emphasis;
- preserving explicitly dictated literal wording;
- correcting likely ASR word choices only when context is unambiguous.

It contained no case names, people, expected outputs, or benchmark-specific vocabulary. An
automated leak scan confirmed that no exact eight-word sequence from any raw transcript or golden
answer appeared in either candidate prompt.

| Model | Shipped prompt | Candidate v1 | Delta |
|---|---:|---:|---:|
| `gpt-5.4` | 88 | 88 | 0 |
| `gpt-5.4-mini` | 88 | 87 | -1 |
| `gpt-5.6-luna` | 94 | 90 | -4 |
| `gpt-5.6-sol` | 89 | 90 | +1 |
| `gpt-5.6-terra` | 88 | 93 | +5 |
| **Mean** | **89.4** | **89.6** | **+0.2** |

The nominal 0.2-point gain is too small to distinguish from normal response and judge variance.
More importantly, it regressed the best model by four points and Mini by one. Across models, the
kitchen-sink case fell 9.4 points on average and redundancy fell 5 points on average.

A smaller v2 retained the shipped frontier prompt and added only generic correction and
restatement rules to the writing style. It failed the fast-model gate:

| Model | Shipped prompt | Candidate v2 |
|---|---:|---:|
| `gpt-5.4` | 88 | 86 |
| `gpt-5.4-mini` | 88 | 87 |

Separate style-only and frontier-only screens also regressed Mini, confirming that neither layer
was a universal improvement.

**Decision:** keep `DefaultWritingStyle` and `DefaultFrontierPrompt` unchanged. The experiments
improved individual models, especially Terra, but did not produce a robust global default. Future
candidates should keep the five-model regression gate and should not advance from fast-model
screening unless both 5.4 variants remain at or above baseline.

## GPT-5.6 Latency Root Cause

A controlled short-case diagnostic tested the remaining client-side explanations for the GPT-5.6
latency. Every timed request used the same 135-character Parakeet transcript and shipped prompt,
`reasoning=none`, a 256-token output cap, and zero Azure SDK retries. Each call consumed exactly 771
input tokens, 33 output tokens, and 0 reasoning tokens, except for one direct Sol response with 34
output tokens.

| Model | Agent Framework samples | Agent median | Direct Responses samples | Direct median |
|---|---:|---:|---:|---:|
| `gpt-5.6-sol` | 43.71 s, 18.42 s, 24.39 s | 24.39 s | 20.83 s, 55.44 s, 26.87 s | 26.87 s |
| `gpt-5.6-luna` | 66.09 s, 38.27 s, 69.58 s | 66.09 s | 57.12 s, 56.95 s, 24.41 s | 56.95 s |
| `gpt-5.6-terra` | 28.52 s, 16.86 s, 10.63 s | 16.86 s | 6.17 s, 47.72 s, 26.56 s | 26.56 s |

The direct runs called `ResponsesClient.CreateResponseAsync` with the same system prompt, user
message, deployment, token cap, reasoning effort, credential, endpoint, and retry policy, bypassing
Agent Framework entirely. They ran sequentially by deployment and skipped judge traffic. Their
latency overlaps the Agent Framework range instead of forming a consistently faster band, so Agent
Framework is not the cause and cross-deployment benchmark concurrency is not required to reproduce
the issue.

Azure Monitor independently showed high service latency during the diagnostic window:

| Deployment | Mean of non-empty one-minute `TimeToResponse` averages | One-minute maximum |
|---|---:|---:|
| `gpt-5.6-sol` | 10.89 s | 30.01 s |
| `gpt-5.6-luna` | 14.46 s | 25.94 s |
| `gpt-5.6-terra` | 8.04 s | 14.82 s |

All 42 observed model requests returned HTTP 200 on the default service tier with
`IsSpillover=False`. There were no retries, throttles, failed status codes, or hidden reasoning
tokens to explain the variance. The remaining latency is therefore in the Azure GPT-5.6
deployment/model serving path, including a substantial long tail outside local application work.
Changing C# collections, prompt string assembly, or Agent Framework adapters cannot materially fix
it. For interactive dictation, the evidence still favors `gpt-5.4-mini`; the 5.6 deployments should
be treated as asynchronous/high-quality options until their serving latency changes.

The eval tool retains `--direct-responses` for repeating this diagnostic without Agent Framework.
Raw controlled results are under `artifacts/latency-diagnostic/none` and
`artifacts/latency-diagnostic/direct`.

## Region And Deployment-Type Investigation

The original 5.6 deployments lived on an East US 2 Foundry resource and used
`GlobalStandard`. Microsoft documents that Global deployments dynamically route inference across
Azure's global infrastructure; prompts and responses may be processed in any geography where the
model is deployed. Moving a second Global deployment to a geographically closer resource therefore
would not reliably move model execution. These 5.6 model versions do not currently offer the
region-pinned `Standard` SKU in the nearby US regions. They offer `GlobalStandard` and
`DataZoneStandard`; a US Data Zone deployment keeps processing in the United States but may still
route to any available US Azure region.

Azure's region metadata and a live AzureSpeed HTTPS test from the Austin-area development machine
identified South Central US as the best Foundry resource location:

| Candidate resource region | Physical location | Approx. distance from Austin | Live median RTT |
|---|---|---:|---:|
| **South Central US** | San Antonio, Texas | **74 mi** | **37 ms** |
| West US 3 | Phoenix, Arizona | 868 mi | 53 ms |
| Central US | Iowa | 815 mi | 57 ms |
| North Central US | Illinois | 980 mi | 70 ms |
| East US 2 | Virginia | 1,197 mi | 456 ms |

The RTT test is an indicative client-to-Azure storage measurement, not an inference SLA. The East
US 2 value in particular can include transient route conditions. Geography, the live ranking, and
the available model capacity all agree on South Central US, so it is the least-assumption endpoint
choice for this user.

The managed subscription had unused `DataZoneStandard` quota of 333K TPM for each 5.6 model in
South Central US. A separate Foundry resource, `MTech/mtech-southcentral-resource`, was created in
South Central US with three isolated deployments:

| Deployment | Model version | SKU | Capacity | RAI policy |
|---|---|---|---:|---|
| `gpt-5.6-luna-usdz` | `2026-07-09` | `DataZoneStandard` | 100K TPM | `Microsoft.DefaultV2` |
| `gpt-5.6-terra-usdz` | `2026-07-09` | `DataZoneStandard` | 100K TPM | `Microsoft.DefaultV2` |
| `gpt-5.6-sol-usdz` | `2026-07-09` | `DataZoneStandard` | 100K TPM | `Microsoft.DefaultV2` |

The original East US 2 Global deployments remain untouched. The new resource exposes the Foundry
Models endpoint `https://mtech-southcentral-resource.services.ai.azure.com/`, plus the compatible
Azure OpenAI endpoint used by Scribe.

Microsoft Foundry documentation currently uses `az cognitiveservices account` for Foundry resource
and Foundry Model deployment control-plane operations. `Microsoft.CognitiveServices/accounts` is
the underlying ARM resource provider; the product-level abstraction is a Foundry resource. There
is no general `az ai` command for catalog model deployments in the installed Azure CLI. `azd ai`
extensions apply to azd-managed Foundry projects, agents, and fine-tuning. This repository is an
ad hoc model-consumer benchmark rather than an azd-managed Foundry project, so the documented
`az cognitiveservices account deployment` path is the correct Foundry CLI workflow here.

### Canonical US Data Zone Result

The three Data Zone deployments were rerun sequentially on the exact pinned 11-case corpus with the
same shipped prompts and `gpt-4.1` judge:

| Model | Global Standard quality | Global median | US Data Zone quality | US Data Zone median | Speedup |
|---|---:|---:|---:|---:|---:|
| `gpt-5.6-luna` | 94 | 42.44 s | 92 | 4.12 s | **10.3x** |
| `gpt-5.6-terra` | 88 | 31.38 s | 91 | 2.88 s | **10.9x** |
| `gpt-5.6-sol` | 89 | 51.84 s | 90 | 5.70 s | **9.1x** |

The small quality movements are within plausible model/judge variance; deployment type should not
change model capability. The approximately 9x to 11x latency improvement is too large and consistent
across all three models to attribute to local code or Agent Framework. Raw canonical results are in
`artifacts/gpt56-usdz-canonical`. A separate five-run short-case check produced tightly bounded
medians of 2.09 s for Luna, 3.00 s for Terra, and 4.85 s for Sol before the full corpus run.

## Issues Found And Fixed

1. The three 5.6 names are deployment identities, not interchangeable model-family aliases. Cloud
   roster selection now preserves explicitly requested deployment names instead of collapsing them
   by underlying model name.
2. The benchmark was initially authenticated against the wrong tenant. Runs now pin the MCAPS
   managed tenant `6e898202-3a97-48e6-9eb2-71fd5fe7de39`.
3. `gpt-5.6-terra` was genuinely missing. It was deployed to `MTech/mtech-project-resource` with
   model version `2026-07-09`, `GlobalStandard` capacity 500, and `Microsoft.DefaultV2`, matching Sol
   and Luna.
4. The Azure SDK's 100-second transport timeout overrode the benchmark's 300-second cleanup budget.
   Eval overrides now align the transport timeout with the requested benchmark budget.
5. Azure deployment validation had a separate 60-second ceiling. It now honors the eval timeout
   override, which allows Luna to initialize successfully.
6. Not-ready diagnostics were erased when cleanup was disabled. The runner now captures status and
   detail before teardown.
7. Deployment filtering diagnostics now show why non-text deployments are skipped.
8. Benchmark-only prompt files and exact prompt rendering make future A/B runs reproducible without
   modifying product defaults first.
9. A benchmark-only direct Responses path now isolates Azure model serving from Agent Framework
   without changing the production cleanup path.
10. Global Standard was the dominant 5.6 latency source. South Central US was selected from Azure
   region geography, live RTT, model SKU support, platform capacity, and subscription quota; US
   Data Zone deployments reduced canonical medians by approximately 9x to 11x.

## Validation And Logs

- `dotnet build Scribe.slnx -c Debug`: passed with zero warnings and zero errors.
- Core test suite: 311 passed, 0 failed.
- Final baseline logs for all five models: no warnings, errors, exceptions, or timeout markers.
- Candidate-v1 logs for all five models: no warnings, errors, exceptions, or timeout markers.
- Latest 500 lines of the combined Scribe app/overlay log: no error, critical, exception, failure,
  timeout, or unavailable markers.
- The earlier superseded run retains the timeout exception that led to the transport and validation
  fixes; it is not part of the final baseline.
