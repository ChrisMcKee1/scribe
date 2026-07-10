# GPT-5.6 Phonetic Cleanup Benchmark

Updated July 10, 2026.

This report compares `gpt-5.6-sol`, `gpt-5.6-luna`, `gpt-5.6-terra`, `gpt-5.4`, and
`gpt-5.4-mini` on Scribe's real post-ASR cleanup path. It also records two prompt-tuning
experiments and why neither candidate replaced the shipped defaults.

## Result

| Quality rank | Model | Quality | Grade | Median | Phonetic quality | Practical verdict |
|---|---|---:|---:|---:|---:|---|
| 1 | `gpt-5.6-luna` | **94** | A | 42.44 s | 98 | Best quality, but too slow for routine dictation. |
| 2 | `gpt-5.6-sol` | 89 | B+ | 51.84 s | 94 | Slowest model and weaker than Luna. |
| 3 | `gpt-5.4-mini` | 88 | B+ | **0.96 s** | 98 | Best speed and quality balance. |
| 4 | `gpt-5.4` | 88 | B+ | 2.00 s | 98 | Same quality as Mini at about 2.1 times the latency. |
| 5 | `gpt-5.6-terra` | 88 | B+ | 31.38 s | 98 | Better self-correction than 5.4, but not enough to justify the latency. |

Quality ties are ordered by speed. `gpt-5.4-mini` is the recommended default for interactive
cleanup. `gpt-5.6-luna` is the maximum-quality option when waiting tens of seconds is acceptable.

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

## Validation And Logs

- `dotnet build Scribe.slnx -c Debug`: passed with zero warnings and zero errors.
- Core test suite: 310 passed, 0 failed.
- Final baseline logs for all five models: no warnings, errors, exceptions, or timeout markers.
- Candidate-v1 logs for all five models: no warnings, errors, exceptions, or timeout markers.
- Latest 500 lines of the combined Scribe app/overlay log: no error, critical, exception, failure,
  timeout, or unavailable markers.
- The earlier superseded run retains the timeout exception that led to the transport and validation
  fixes; it is not part of the final baseline.
