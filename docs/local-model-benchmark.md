# Local AI cleanup benchmark

Measured September 29, 2026, for Scribe 0.5.2. How well and how fast small open models clean up
dictation on this PC through the three local runtimes Scribe supports, Ollama, LM Studio and
Foundry Local, what Scribe did wrong with them, what 0.5.2 changes, and which model to use.

> **Hardware:** NVIDIA GeForce RTX 5080 (16 GB, driver 617.14), AMD Ryzen 9 9900X (12 cores),
> 95 GB of memory, Windows 11 build 26200. Times are this PC's; the ranking and the quality scores
> are what carries over.

## October 8 prefix-cache check

Measured October 8, 2026, with Scribe 0.5.6's shipped short cleanup instructions and default writing
style, Ollama 0.40.0 and `gemma4:12b`. This is a new backend microbenchmark, not a rerun of the
September model leaderboard. The [raw evidence](benchmarks/prompt-cache-2026-10-08.json) contains
63 requests: 55 rewrites of eight public synthetic dictations and eight one-token readying requests.
There is no user history, vocabulary, cloud call or change to saved settings.

The same model, 4,096-token context (Ollama's own setting), temperature 0.1, thinking off and seed
were used throughout. Native streaming exposes time to first token; production dictation still
uses its existing API and response handling. The system/style prefix was 4,686 characters.

| Condition | Requests | Median rewrite | P95 rewrite | Median first token | Median cached input tokens |
|---|---:|---:|---:|---:|---:|
| Model unloaded before each request | 8 | 6.656 s | 13.976 s | 6.556 s | 0 |
| Resident model, stable prefix, changing dictation | 16 | **0.317 s** | **0.350 s** | 0.245 s | **1,084** |
| Resident model, changing prefix | 16 | 0.708 s | 0.871 s | 0.636 s | 0 |
| After a cold one-token readying request | 8 | 0.286 s | 0.303 s | 0.193 s | 1,083 |
| Repeating the complete request | 6 | 0.181 s | 0.238 s | 0.135 s | 1,105 |

**Prefix reuse is observed, not inferred from cached C# objects.** Ollama reported
`prompt_eval_cached_count` on every request. The stable arm normally reused 1,084 of about 1,106
input tokens. A unique neutral marker at the start of the system prompt produced zero cache hits
without unloading the model; this is a cache-miss control, not an API setting that disables caching.
Median prompt evaluation was 202 ms with the stable prefix and 401 ms with the changing prefix.
The roughly 55% lower rewrite median in this small sample is separate from the much larger model
loading cost. Arms ran in blocks rather than randomized order, so timing effects are not fully
controlled; cache counters and matched outputs are stronger evidence of the mechanism than the
precise percentage.

**Readying moves work, rather than removing it.** Its median was 6.200 s before the 0.286 s rewrite.
That preparation can overlap the user speaking; it is not included in the rewrite-only row. The
first cold request took 13.976 s, so the eight-sample P95 is simply that maximum, not a reliable
estimate of a long-run tail.
All P95 values in this table are sample maxima at these small sample sizes. "Cold" means the
model was unloaded, not that operating-system file caches were empty. Preparation plus rewrite
was 6.476 s at the median and 7.283 s at observed P95.

**A bounded confirmation addressed order and length.** A
[second check](benchmarks/prompt-cache-confirmation-2026-10-08.json) used four existing cases,
equal-length constant/changing markers and both stable-then-changed and changed-then-stable block
orders. Per-case prompt token counts matched exactly. Stable-prefix medians were 327 ms and
313 ms in the two orders, against 689 ms and 724 ms for misses. Stable calls reused 1,100 to
1,101 tokens; changed markers reused zero. All 16 rewritten outputs matched across the arms and
passed the production sanitizer. The two priming requests are recorded separately, including
their 12.315 s and 0.719 s costs; they are not hidden in the rewrite timing.

**Quality is matched, not a new leaderboard score.** All 55 rewrite answers passed Scribe's
production sanitizer. Each of the eight stable-prefix outputs was identical to its changed-prefix
counterpart. Both arms matched five of eight fixed references exactly (62.5%); the other answers
differed in capitalization or punctuation. There was no blind judge, real speech, long dictation
or app-profile/vocabulary-switch corpus in this check.

**Memory was not independently profiled.** Ollama's `api/ps` reported `size` and `size_vram` of
1.01 GiB, retained verbatim in the evidence. Those fields do not establish total process memory
or complete physical GPU allocation. The model was unloaded at the end, restoring the initially
nonresident state.

This supports the existing stable instruction/style ordering, local readying and selectable idle
duration/Never behavior. It does not justify an extra cache knob that the backend does not expose,
or prove a Foundry Local/LM Studio/cloud caching gain. Microsoft Foundry's existing cache-on
setting is separate from `store=false`; cache reads should be measured directly on that deployment
before claiming an additional cloud performance improvement.

## Summary

1. **A small open model on this PC is good enough for dictation, fast, and costs no tokens.**
   Gemma 4 E4B in Ollama scored **89.2**, level with `gpt-5.4-mini` (**88.3**) graded in the same
   packets, and answered in **0.50 s** typically on the GPU (**4.6 s** on the CPU alone), where
   Mini took 2.1 s. Gemma 4 E2B scored **83.5** in **0.33 s**. The frontier cloud models are still
   ahead on the hardest dictation (`gpt-5.6-terra` **96.0**), mostly on long chains of
   self-corrections, badly misheard phrases and comparisons of technical names.
2. **Scribe 0.5.1 made local servers look far worse than they are.** It sent Ollama and LM Studio
   the cloud instructions with the whole vocabulary, which Ollama silently cut down to its last
   2,050 tokens, so the model never saw an instruction; it left thinking on; it sent no temperature;
   and Ollama ignored its output limit. With the 0.5.2 fixes, the same nine models on Ollama scored
   **26 points higher on average**, and Gemma 4 E2B went from **38.7 to 83.5** and from **2.3 s to
   0.33 s**.
3. **Foundry Local's recommended default was the wrong pick for this PC.** Every Qwen3 build Foundry
   Local offers for this GPU failed to start, so Qwen3 1.7B ran on the CPU: **9.2 s** typically,
   scoring **46.5**. Qwen2.5 1.5B has a TensorRT-RTX build that runs on the GPU in **0.39 s**
   (71.5), and on the CPU it is still faster (6.3 s). It is the new default.
4. **Round two (below) tuned what every request carries.** Each dictation now carries only the vocabulary
   it appears to mention, which put the right word pack term in the output **97%** of the time on
   Gemma 4 E4B instead of 67%, cut a cloud request from about **7,500 to 1,400 input tokens**, and cost no
   quality. Microsoft Foundry is asked not to spend time reasoning, which saved about a third of the time on
   gpt-6-sol at the same score. The writing style now asks for a list when you list things. With all of it,
   every model measured scored as well or better than with 0.5.1's requests.
5. **Round three (0.5.3, below) lets a model on this PC read more at once and get the whole vocabulary**, as a
   choice for each app. Every request is now kept to what the model's context holds, the dictation first. Sending
   the whole vocabulary scored no better on Gemma 4 E4B (**87.7** against **87.0**) and got the 2B models a fifth to
   a third fewer word pack terms right, so it stays off unless you turn it on.

## Round two: vocabulary, instructions, lists and reasoning

Measured the same day on the same PC, with the same blind judge. The standard rubric grades the frozen 25
dictations against the writing style each arm used; the lists rubric grades 33 (the 25 plus 8 new ones with
steps, action items, an email, pros and cons, a long update and three short messages that must stay
sentences) against 0.5.2's writing style. The judge's own spread between repeated items averaged 9 to 10
points on the standard rubric and 3 to 5 on the lists rubric, so single differences under about 3 points
are noise. Every number is in
[`benchmarks/cleanup-tuning-2026-09-29.json`](benchmarks/cleanup-tuning-2026-09-29.json).

### The vocabulary each dictation carries

0.5.1 sent every request the whole vocabulary, or its first 80 terms with the short instructions, whether or
not the dictation mentioned any of it. 0.5.2 sends the terms whose spoken or written words the dictation
appears to use, heard exactly or slightly differently (`VocabularyMentions`). Standard rubric, Ollama on the
GPU, two runs; "terms right" counts the 30 uses of word pack terms in the 7 dictations that need them:

| Model | Every term: score, terms right, typical | Mentioned: score, terms right, typical |
|---|---|---|
| gemma4:e4b | 89.4, 67%, 0.62 s | **90.2, 97%, 0.46 s** |
| gemma4:e2b | 84.0, 50%, 0.31 s | 84.4, 83%, 0.29 s |
| qwen3:4b-instruct | 81.8, 57%, 0.36 s | 81.9, 90%, 0.35 s |
| gemma3:4b | 82.1, 53%, 0.56 s | 79.1, 87%, 0.41 s |
| granite4:3b | 80.2, 57%, 0.29 s | 78.4, 73%, 0.31 s |
| phi4-mini:3.8b | 79.1, 57%, 0.32 s | 76.6, 67%, 0.29 s |
| qwen2.5:1.5b | 67.2, 53%, 0.16 s | 66.6, 63%, 0.15 s |
| gpt-6-sol, no reasoning | 93.9, 100%, 1.34 s, 7,527 input tokens | 94.1, 100%, 1.29 s, 1,412 input tokens |

The first 80 terms rarely held the one a dictation needed (Phi-4-mini and Llama 3.3-70B sit deep in the AI
model names pack), so the model on this PC wrote them wrong; carrying what the dictation mentions fixed that
without costing the judge's score. On Foundry Local's CPU build, which caches nothing, the shorter prompt made
Qwen2.5 1.5B answer in 3.1 s instead of 5.6 s. The cloud models spelled every term right either way and now read
a fifth of the input, which also means a small tokens-per-minute quota serves five times as many dictations.
**Adopted for every place AI cleanup runs**; PRIVACY.md and the AI cleanup page say what now goes.

### Reasoning on Microsoft Foundry

Standard rubric, two runs, every term; score, typical time, reasoning tokens per request:

| Deployment | Its default | None | Low | Medium |
|---|---|---|---|---|
| gpt-6-sol | 93.9, 2.04 s, 102 | **93.9, 1.34 s, 0** | 94.0, 1.91 s, 26 | 93.7, 1.93 s, 83 |
| gpt-6.1-sol | 96.1, 1.96 s, 33 | refused | **96.5, 1.60 s, 13** | 95.9, 1.85 s, 34 |
| gpt-5.6-terra | 95.0, 1.33 s, 0 | **94.7, 1.28 s, 0** | 95.0, 1.44 s, 3 | 94.8, 1.31 s, 0 |

Thinking bought cleanup nothing. Scribe now asks each deployment for the least effort it accepts: none, then
low when the model refuses none, as gpt-6.1-sol does ("'none' is not supported ... Supported values are: 'low',
'medium', 'high', 'xhigh', and 'max'"), then no effort field for a model that takes none. It happens in the
readiness probe, with no setting.

### Shorter instructions

A writing style cut from 646 to about 290 tokens, and short instructions cut from 316 to about 160, were tried
on the same models. The shorter style alone moved the local models by -2.0 to +1.7 (-0.6 on average) and made
the 4B models 15 to 25% faster; both together cost Gemma 4 E4B 3.9 points and Foundry Local's Qwen2.5 7B 5.5.
On the cloud models they moved the score by -0.3 to +1.0 and saved about 430 of the cached tokens. **Not
adopted**: the saving is small once the vocabulary is trimmed, and small models on this PC lose on average.

### Lists when you list things

The writing style now says: write several items, steps or options as a list, keep the sentence that introduces
it, keep a short message or a single request as sentences, and add no headings. Lists rubric:

| Model | 0.5.1 style: 25 standard, 5 list cases, 3 short | 0.5.2 style |
|---|---|---|
| gpt-6-sol, no reasoning | 94.4, 89.0, 98.4 | 94.4, **93.5**, **100.0** |
| gemma4:e4b | 87.4, 89.7, 97.9 | 86.5, 89.8, 97.9 |
| gemma4:e2b | 81.5, 86.8, 97.9 | 79.9, 87.1, 97.6 |
| qwen3:4b-instruct | 81.9, 84.4, 99.5 | 81.8, **88.2**, 99.5 |
| Foundry Local qwen2.5-7b | 81.0, 83.9, 97.6 | 80.5, 84.7, 97.6 |

The cloud model wrote steps and action items as numbered and bulleted lists and kept every short message as a
sentence. Most small models kept writing sentences whatever the style said (Gemma 4 never wrote a list), and
repeating the rule in the short instructions did not change that, so it was left out of them. **Adopted in the
default writing style.** A terminal still gets one line: the single-line rule for command windows comes after
the style.

### The shipped configuration, every runtime

0.5.2 as it ships (the style with lists, mentioned vocabulary, the lowest reasoning effort), lists rubric, 33
dictations, against 0.5.1's style and whole vocabulary where both were run:

| Where it runs | Model | 0.5.1's requests | **0.5.2** | Typical | Terms right |
|---|---|---:|---:|---:|---:|
| Microsoft Foundry | gpt-6.1-sol (low) | | **96.0** | 1.54 s | 100% |
| Microsoft Foundry | gpt-5.6-terra (none) | | **95.5** | 1.40 s | 100% |
| Microsoft Foundry | gpt-6-sol (none) | 93.9 | **94.9** | 1.27 s | 100% |
| Ollama, GPU | gemma4:e4b | 88.7 | **90.1** | 0.51 s | 97% |
| Foundry Local, RTX GPU | qwen2.5-7b | 82.9 | **85.7** | 0.73 s | 87% |
| Ollama, GPU | qwen3:4b-instruct | 83.9 | **85.5** | 0.45 s | 90% |
| LM Studio, GPU | qwen/qwen3-4b-2507 | | **85.1** | 0.40 s | 92% |
| Ollama, GPU | gemma4:e2b | 83.8 | **84.6** | 0.32 s | 83% |
| LM Studio, GPU | google/gemma-4-e2b | | **84.1** | 0.39 s | 83% |
| Ollama, GPU | granite4:3b | | **82.2** | 0.34 s | 73% |
| Foundry Local, RTX GPU | qwen2.5-1.5b | 72.4 | **73.5** | 0.37 s | 63% |
| Foundry Local, CPU | qwen2.5-1.5b | | **69.2** | 6.2 s | 63% |

Every model that ran both ways scored higher with 0.5.2's requests. Gemma 4 E4B on this PC's GPU is within 5
points of the cloud models in half a second, at no cost per dictation.

### Memory, starting and defaults

- **Memory comes back.** Every request asks Ollama and LM Studio to keep the model only as long as Scribe keeps
  its own speech model ("Free memory when Scribe isn't used", 10 minutes by default: `keep_alive` for Ollama,
  `ttl` for LM Studio), so they free it on their own clock after that time without a dictation, while Scribe
  frees its speech model and Foundry Local's. Pausing dictation, turning AI cleanup off, moving it elsewhere,
  shortening that time and **Free memory** in Settings free the model at once, for any other app using it
  too. Settings shows how much memory the model uses.
- **The first dictation after a release waits for the model** instead of being typed without cleanup, for up to
  30 seconds, and the recording indicator says **Starting local model** and **This can take time**; past that,
  Scribe types what it heard rather than waiting behind the load again. Measured loads: 2.7 to 5.2 s on the GPU,
  7 to 15 s on the CPU.
- **Defaults follow the hardware, and Settings recommends no model.** With an NVIDIA RTX graphics card with 8 GB
  or more, a first setup of Scribe's own model starts from Qwen2.5 7B (85.7 at 0.73 s) instead of Qwen2.5 1.5B
  (73.5 at 0.37 s); anywhere else from Qwen2.5 1.5B, which also runs on the processor. With Ollama or LM Studio,
  Scribe preselects the installed model that ranks highest here: Gemma 4 E4B first with 6 GB or more of graphics
  memory, Gemma 4 E2B first otherwise.
- **Azure CLI sign-in cost a token request per dictation.** Every cleanup request started `az` for a token:
  1.2 to 7 s here (6.95 of 8.4 s on one gpt-6-sol request). The Azure CLI access-token cache, which was on for every
  cloud number above, is now on by default: the token is reused until shortly before it expires, and an account
  change made outside Scribe is seen at the next token refresh rather than the next request.
  `PerfFlags.CliTokenEveryRequest` brings back the old path for one release.

## Round three: the context size and the whole vocabulary (0.5.3)

Measured September 30, 2026, on the same PC with Ollama 0.35.0, LM Studio 0.4.25 and Foundry Local 2.1.0, for the
setting that lets a model on this PC read more at once and receive the whole vocabulary rather than the terms a
dictation mentions. The frozen 25 dictations ran three times each with all 11 built-in word packs, 1,332 terms or
about 8,300 tokens, and the judge graded every arm in one pass (its spread between repeated items: 1.3 points on
average, 2.0 at most). "Terms right" is round two's count, the 30 uses of word pack terms in 7 dictations. Every
number is in [`benchmarks/context-window-2026-09-30.json`](benchmarks/context-window-2026-09-30.json).

| Model, context | Terms the dictation mentions: score, terms right, typical, first dictation | Whole vocabulary: score, terms right, typical, first dictation |
|---|---|---|
| gemma4:e4b, Ollama, 32K | **87.0**, 97%, 0.44 s, 0.9 s | **87.7**, 96%, 0.50 s, 2.4 s |
| gemma4:e2b, Ollama, 32K | **81.3**, 80%, 0.28 s, 0.8 s | 79.5, 53%, 0.29 s, 1.5 s |
| google/gemma-4-e2b, LM Studio, 32K | 78.5, 76%, 0.33 s, 0.7 s | 79.0, 53%, 0.36 s, 1.8 s |
| gemma4:e4b, Ollama, 8K | | 87.0, 97%, 0.47 s, 1.4 s |
| gemma4:e2b, Ollama, 8K | | 79.3, 60%, 0.29 s, 0.9 s |

A request carried about 1,500 input tokens with the terms its dictation mentions, 13,700 with the whole vocabulary,
and 6,400 at 8K, where the terms the dictation mentions went first and then as much of the rest as fit. The same
check with the maintainer's own ten word packs (762 terms) and the two default packs, in 60 synthetic dictations
that each mention three of those terms by what Scribe hears (43 of the 180 mentions slightly misheard), scored on
this PC only by the written forms kept:

| Model | Terms the dictation mentions | Whole vocabulary, 32K | Whole vocabulary, 8K |
|---|---:|---:|---:|
| gemma4:e4b, Ollama | 98% | 97% | 98% |
| gemma4:e2b, Ollama | 95% | 80% | 91% |
| google/gemma-4-e2b, LM Studio | 96% | 79% | 92% |

- **The whole vocabulary bought nothing measurable.** Gemma 4 E4B scored the same within the judge's noise and kept
  the same share of terms. The 2B models got a fifth to a third fewer terms right: a list of 1,300 lines hides the
  few a dictation needs from a small model. Every request also reads 9 to 12 times as many tokens. Ollama and LM
  Studio read them once a load, which made the first dictation after a load 1 to 1.5 s slower on the GPU, and cache
  them after that. Foundry Local caches nothing, so its CPU builds would read them for every dictation: Qwen2.5 0.5B
  took 2.2 s for a 1,700-token prompt and 11.5 s for 7,500.
- **Repeating the mentioned terms after the whole list**, to point them out, lifted the 2B models' terms right from
  53% to 70% but cost Gemma 4 E4B 3.6 points and once turned INT4 into INT8, a near match the repetition put next to
  the dictation. Dropped.
- **What helps is fitting every request to the context the model actually loaded**, the dictation and the longest
  answer it allows first. Nothing checked before 0.5.3, and Ollama's 4,096-token default silently cut the start of a
  long request (round one).

**Decided:** every request to Foundry Local, Ollama or LM Studio is fitted to its context, whatever the settings: the
dictated text and the longest answer the request allows take their room first, in chunks small enough to leave the
vocabulary some, and a dictation that cannot fit even in short chunks is typed as heard. **Send your whole vocabulary
when it fits** is off by default for every app, and its hint says a long list can confuse a small model. **Context
size** defaults to the app's own setting.

How each app takes a size:

- **Ollama** reads one only through its own API (`num_ctx`); its OpenAI-compatible address ignores it. So with a size
  chosen, Scribe sends every request through Ollama's own API asking for that size, capped at the most the model takes
  (`/api/show`), and Ollama loads the model at it once, though another app asking for the same model at another size
  makes Ollama reload it whenever the two take turns. With **Ollama's setting**, Scribe keeps the OpenAI-compatible
  requests every earlier release sent and fits to the size Ollama loaded for Scribe's own request (`/api/ps`). Ollama's
  own default is 4,096 tokens below 24 GB of graphics memory, 32,768 from 24 GB and 262,144 from 48 GB.
- **LM Studio** loads a model at its own size through its OpenAI-compatible address, and at a chosen size through its
  own chat API (`/api/v1/chat` with `context_length`). So with a size chosen, Scribe loads the model that way before
  the first request, and requests by name reach that copy. LM Studio keeps a copy loaded this way for its own hour
  whatever Scribe asks (its load refuses a `ttl`), so Scribe frees it itself after the idle time and when Scribe
  closes. LM Studio makes a second copy rather than resizing one, so a copy it loaded on demand at another size is
  unloaded first; a copy you loaded yourself keeps its size. Gemma 4 E2B's context took 545 MB at 32K against 214 MB
  at 8K, by LM Studio's own estimate.
- **Foundry Local** fixes each model's size in its files (`genai_config.json`: 32,768 for Qwen2.5 0.5B); its catalog
  reports none. Scribe reads it and fits to it.

## Round four: Gemma 4 12B system instructions and writing style

Measured October 7, 2026, on the RTX 5080 with Ollama 0.40.0 and `gemma4:12b`, at a 32,768-token
context, temperature 0.1 and thinking off. Each arm ran the frozen 25 dictations three times through
the production cleanup service with the mentioned terms from the default word packs. The same
identity-blind judge, Claude Opus 5.5, graded all 106 distinct outputs together in 25 packets, including
the retained GPT-5.6-Terra reference outputs from September 4. The reference was not called again:
its times below are the recorded September times. Evidence:
[`benchmarks/ollama-12b-prompts-2026-10-07.json`](benchmarks/ollama-12b-prompts-2026-10-07.json).

**Local and frontier instructions already share the same writing style.** They differ in the system
preamble: the local one is directive and gives a worked example; the frontier one describes the
post-editor contract and allows already-correct text to stay unchanged. The local preamble says
"always rewrite" and "do not shorten", while the shared style asks for repeated points to be merged.
An aligned local candidate resolved those tensions, made meaningful filler words explicit and
reinforced identifier fidelity. A second candidate appended explicit quotation preservation,
no invented AM/PM, units or currencies, and final corrected values to the shared style.

| Gemma 4 12B instructions and style | Score | 95% interval | Median | 95th percentile |
|---|---:|---|---:|---:|
| Shipped local instructions and style | **91.3** | 87.4 to 94.6 | **446 ms** | 939 ms |
| Shipped frontier instructions and style | 91.1 | 87.1 to 94.6 | 466 ms | 909 ms |
| Aligned local instructions, shipped style | 90.6 | 86.5 to 94.3 | 478 ms | 906 ms |
| Local instructions, more precise style | 91.2 | 87.6 to 94.5 | 458 ms | 1,033 ms |
| Frontier instructions, more precise style | 91.3 | 87.5 to 94.7 | 452 ms | 831 ms |
| Retained GPT-5.6-Terra reference | 94.9 | 93.1 to 96.5 | 2,088 ms | 3,703 ms |

Every local arm returned cleaned text in all 75 runs. There were no repeated calibration packets in
this pass, so it supplies no estimate of judge drift; the intervals describe variation over cases,
not every source of uncertainty. The changes bought no credible overall improvement. More precise
style clauses stopped invented AM/PM in the self-correction case and sometimes preserved the
quotation better, but lost elsewhere. The hard cases still drop a date correction, change a quoted
phrase, or swap a model version, such as Llama 3.3 for Llama 3.1.

**Decision: leave the shipped writing style and both system prompts unchanged.** Gemma 4 12B is
close to the frontier reference on this suite at much lower latency, not proven equivalent, and
selecting Detailed instructions does not close the gap. Keep Automatic for this model unless a
user's own samples establish a benefit from another choice.

## Round five: DeepSeek-R1, Writex and Gemma 4 12B (0.5.5)

Measured October 8, 2026, on the RTX 5080 with Ollama 0.40.0, using the final 0.5.5 completion
checks. All three models ran the frozen 25 September dictations three times at a 32,768-token
context, with the shipped Automatic instructions and writing style, mentioned vocabulary from the
default word packs, and production vocabulary admission. There was no output-token override,
reasoning override or enlarged deadline: `--clean-timeout 0` keeps the normal 25-second first
attempt, 20-second stall retry and 90-second operation bound. The ordinary benchmark default is a
180-second override, so omitting that flag would not test the shipping policy.

Claude Opus 5.5 graded all 162 distinct outputs together in 25 identity-blind packets, with the
retained GPT-5.6-Terra outputs as an anchor. Failed rewrites were graded as the raw text the user
would receive, not omitted from the score. The anchor was not called again; its latency is from
September 4. Every case was graded, but no repeated calibration packets were used, so the intervals
describe variation over cases, not all judge uncertainty.

The [sanitized evidence](benchmarks/ollama-model-compatibility-2026-10-08.json) records all 300
timed outputs across both API paths, their outcomes and partial-failure flags, the exact request
policy, source hashes, grades and frozen synthetic inputs. It contains no private dictation or
configuration.

| Model | Score | 95% interval | Median | 95th percentile | Raw fallback |
|---|---:|---|---:|---:|---:|
| **gemma4:12b** | **91.9** | 88.5 to 94.9 | **431 ms** | 857 ms | **0 of 75** |
| VicRodger27/Writex:4b | 77.7 | 72.1 to 82.9 | 418 ms | 692 ms | 0 of 75 |
| deepseek-r1 | 75.3 | 69.7 to 80.8 | 5,600 ms | 18,682 ms | 10 of 75 |
| Retained GPT-5.6-Terra reference | 95.5 | 93.9 to 97.0 | 2,088 ms, historical | 3,703 ms, historical | 0 of 25 |

**Gemma 4 12B is the measured choice for this 16 GB graphics card.** Writex was about as fast, but
its fidelity score was 71.0 against Gemma's 90.9: fast fluent rewriting is not the same as keeping
the speaker's meaning. DeepSeek kept reasoning despite the request to turn it off. Its successful
synthetic readiness check bought it a bounded 2,048-token reasoning allowance, not a promise that
every dictation would fit. It completed 65 of 75 rewrites; the other ten exhausted their output
allowance. Each of those ten outputs was checked against the frozen input, code unit for code unit,
and kept all of it. No run in this comparison had a partial-failure flag.

This fixes the earlier silent-cut defect rather than making DeepSeek a fast dictation model. A
reported length/content-filter end is rejected before answer cleanup; on a model that needs the
reasoning allowance, a missing completion reason is not trusted either. The original OpenAI SDK
field is checked, because its normalized value defaults a missing finish reason to Stop. A server
that falsely reports Stop can still produce a bad rewrite, which is why the quality grade matters.

Names and context limits are separate compatibility facts. Writex's catalog publishes `:0.8b`,
`:2b` and `:4b`, but no `:latest`; use `VicRodger27/Writex:4b`, or paste its `ollama run` command
into Download another model. The installed Q4_K_M builds report maximum contexts of 131,072 for
DeepSeek-R1 and 262,144 for Writex and Gemma 4 12B. Those are supported maxima, not measured fast
allocations. This comparison used 32K, not the maximum.

A separate completion-safety run kept **Ollama's setting**, with no chosen context size, through its
OpenAI-compatible API. The resident DeepSeek copy reported 4,096 tokens and 5,578,204,118 bytes,
all on the GPU. Of 75 runs, 57 returned a cleaned answer and 18 kept the exact frozen input; none
had a partial-failure flag. Median return time, including fallback, was 5,191 ms. That arm was not
blind-graded and is not part of the quality table. The two API paths therefore both preserve the
words after a reported cut, but neither makes this thinking model the recommended pick.

**Harness limitation:** a generated leaderboard can still rank a `degraded` model without displaying
its failure count. Also, `AdmittedRequests: true` proves vocabulary admission, not the absence of
benchmark-only generation overrides. Do not use either as a release-success claim. This table uses
the per-run outcomes and partial flags; the evidence explicitly records that no output, reasoning,
temperature or retry override was supplied.

## What to use

| If you want | Use | Quality | Typical, GPU | Typical, CPU only | Download | GPU memory |
|---|---|---:|---:|---:|---:|---:|
| **The best balance** | **Gemma 4 E2B**: `gemma4:e2b` in Ollama, `google/gemma-4-e2b` in LM Studio | 83.5 | 0.33 s | 2.4 s (LM Studio), 3.3 s (Ollama) | 7.2 GB (Ollama), 4.1 GB (LM Studio) | 1.6 GB |
| The best quality that still feels instant | Gemma 4 E4B: `gemma4:e4b`, or `gemma4:e4b-it-qat` for a 6.1 GB download | 89.2 | 0.50 s | 4.6 s | 9.6 GB | 3.0 GB |
| The smallest download that does the job | IBM Granite 4 3B: `granite4:3b` | 80.7 | 0.33 s | 3.9 s | 2.1 GB | 2.3 GB |
| The best writing on the CPU alone | Qwen3 4B Instruct 2507: `qwen3:4b-instruct` | 81.7 | 0.41 s | 5.0 s (83.1) | 2.5 GB | 3.0 GB |
| Nothing else to install | Foundry Local, **Qwen2.5 1.5B** (the default); **Qwen2.5 7B** with an NVIDIA RTX card and 8 GB | 74.0 (GPU), 66.1 (CPU); 7B: 85.4 | 0.37 s, NVIDIA RTX only; 7B: 0.74 s | 6.7 s | 1.3 GB (GPU), 1.8 GB (CPU); 7B: 4.7 GB | |

- In Settings, choose **On this PC**, then **Ollama** or **LM Studio**: Scribe lists the models you downloaded
  in it and uses the app's own address. Leave **Instructions** on Automatic: 0.5.2 gives a model on this PC the
  short instructions it can follow (see below).
- Quality is a blind judge's score out of 100 on 25 deliberately hard dictations, not a percentage
  of correct dictations. Differences under about 5 points are within the judge's noise.
- Typical is the median time from sending the words to getting the cleaned text back, with the model
  loaded. GPU memory is what `ollama ps` reported with the model loaded.
- **Avoid** `qwen3:4b` in Ollama (the thinking model; its reasoning arrives as text and Scribe
  rejected all 75 answers) and LFM2.5 1.2B Instruct, which copied the worked example in its
  instructions into the text. Models under 1B parameters are the fastest (about 0.2 s on the GPU)
  but scored 44 to 66.

## What 0.5.2 changes

| Problem in 0.5.1 | What it did to a dictation | Fix in 0.5.2 |
|---|---|---|
| Automatic chose the detailed instructions and up to 5,000 vocabulary terms for **any** OpenAI-compatible server, including Ollama and LM Studio on this PC | With a fresh install's two word packs the request was **7,745 tokens**. Ollama's 4,096-token context kept the last 2,050 ("truncating input prompt limit=2050" in its log), so the model saw vocabulary and no instructions, and wrote replies and emails instead of the dictation. Ollama refused `qwen3:4b-instruct` outright (HTTP 400, the request exceeds the available context size) and Scribe typed the raw text every time | A server at `localhost`, `127.x.x.x` or `[::1]` counts as a model on this PC (`LocalAiServer`): Automatic gives it the short instructions and the 80-term vocabulary, as for Foundry Local, and the Dictionary page counts that budget |
| Thinking stayed on | Ollama turns thinking on for every model that can think and ignores the `/no_think` Scribe appends for Qwen3: `qwen3.5:0.8b` spent 2,026 tokens and 12.2 s on a one-sentence edit, `qwen3.5:2b` took 10 s typically, and `gemma4:e2b` wrote 451 tokens on average | Scribe asks a server on this PC for `reasoning_effort: none`, which Ollama maps to thinking off; models that can't think accept it unchanged |
| Ollama ignored the output limit | The OpenAI package sends `max_completion_tokens`; Ollama reads only `max_tokens`. `qwen3:0.6b` fell into a loop and generated 19,711 tokens and counting | Both fields are sent to a server on this PC, with the same number |
| No temperature for a server on this PC | The server's default applied (1.0 for Gemma 4). At the server's default, `qwen3.5:2b` lost 14.7 points and `llama3.2:3b` 11.0 | 0.1, as Foundry Local always had |
| What small models wrap around the answer was typed | Foundry Local's Qwen3 1.7B opened 15 of 25 answers with a bare `<think>`, 19 with "Here's the rewritten transcript, following the rules and style guide:" and a `---`, wrapped text in `<rewritten_transcript>`, and ended some with "---" and "This version maintains the original meaning..."; Llama 3.2 3B and several small models opened answers with "Here is the rewritten text:" | Removed before typing: a leading bare think tag, a first line that announces or labels the rewrite ("Here is the rewritten text:", "**Transcript Rewritten:**") unless the dictation itself starts that way, the separator under it, wrapper tags the dictation didn't contain, and notes under a closing separator ("---" then "This version...", "**Note:**", "Let me know if..."). On the recorded answers this lifted Foundry Local's Qwen3 1.7B from 46.5 to 56.4 |
| A model the server unloaded while idle made the next dictation wait | Ollama unloads a model 5 minutes after its last request unless the request's `keep_alive` asks for longer, and Scribe never asked; LM Studio unloads a model it loaded on demand after an hour. The next dictation waited 2.7 to 5.2 s on the GPU and 7 to 15 s on the CPU | When a recording starts, Scribe sends a server on this PC the dictation's own instructions and vocabulary with no text and a one-token limit, unless it answered in the last 30 seconds. After an unload the GPU wait fell from 5.2 s to 0.45 s (see Cold start) |
| Foundry Local's default was Qwen3 1.7B | On this RTX 5080 its GPU build failed to start and it ran on the CPU: 9.2 s typically, 46.5 (56.4 with 0.5.2's answer cleanup) | The default is Qwen2.5 1.5B: 0.39 s on an NVIDIA RTX GPU through TensorRT-RTX, 6.3 s on the CPU |
| Mistral NeMo 12B and Phi-4 were labeled Foundry Local's "best balance" and "best quality" | Neither could run on this GPU through Foundry Local 1.2.4 (Mistral NeMo's GPU build failed to start; Phi-4's TensorRT-RTX engine failed to load), so each fell back to the CPU after a 7 to 10 GB download | Removed from the curated list; both stay available from the Foundry Local catalog |
| Settings suggested `qwen3:4b` as an example model name | In Ollama that tag is the thinking model, and every one of its 75 answers was rejected, so Scribe typed the raw text | The example is `gemma4:e2b` |

### 0.5.1 as shipped against 0.5.2, same models, Ollama on the GPU

| Model | 0.5.1 | 0.5.2 | Change | 0.5.1 typical | 0.5.2 typical |
|---|---:|---:|---:|---:|---:|
| gemma4:e2b | 38.7 | 83.5 | +44.8 | 2.3 s | 327 ms |
| granite4:1b | 38.1 | 73.4 | +35.3 | 778 ms | 388 ms |
| phi4-mini:3.8b | 48.6 | 78.7 | +30.1 | 758 ms | 346 ms |
| llama3.2:3b | 43.3 | 71.8 | +28.6 | 622 ms | 321 ms |
| qwen3:4b-instruct | 54.4 | 81.7 | +27.3 | 73 ms (refused, raw text) | 409 ms |
| qwen3:0.6b | 36.8 | 57.0 | +20.2 | 859 ms | 179 ms |
| qwen3.5:2b | 52.6 | 72.2 | +19.6 | 10.0 s | 390 ms |
| qwen3:1.7b | 49.5 | 67.1 | +17.6 | 2.7 s | 243 ms |
| gemma3:1b | 46.1 | 59.1 | +12.9 | 498 ms | 246 ms |

Mean change +26.3 points over 9 models, every one up.

### The 0.5.2 answer cleanup on the recorded answers

| Runtime and model | Answers changed | As recorded | With the 0.5.2 cleanup |
|---|---:|---:|---:|
| Foundry Local qwen3-1.7b, CPU | 23 of 25 | 46.5 | **56.4** |
| Foundry Local qwen3-0.6b, CPU | 11 of 25 | 41.4 | 46.8 |
| Foundry Local qwen3.5-2b-text, CPU | 1 of 25 | 73.2 | 74.0 |
| Ollama granite4:350m-h | 20 of 75 | 56.7 | 59.1 |
| Ollama llama3.2:1b | 13 of 75 | 44.8 | 45.5 |
| Ollama llama3.2:3b | 3 of 75 | 71.8 | 72.2 |
| LM Studio meta/llama-3.2-3b | 2 of 75 | 68.7 | 69.1 |

Six more small models changed by less than a point, and no other answer changed.

## Results

Each model cleaned the same 25 dictations. On the GPU each case ran three times (75 answers per
model); on the CPU once (25). **Fell back** counts answers Scribe rejected (a ramble, a refusal, an
empty answer) and replaced with the raw text, which is what the user would have received. The
complete tables, every model and runtime, are at the end.

### On the GPU (RTX 5080), the top of the board

| Runtime | Model | Quality | 95% interval | Typical | Slowest 1 in 20 | Download | License |
|---|---|---:|---|---:|---:|---:|---|
| Ollama | gemma4:12b | 92.3 | 89.5 to 94.8 | 888 ms | 1.4 s | 7.6 GB | Apache 2.0 |
| Ollama | **gemma4:e4b** | **89.2** | 85.2 to 92.7 | **499 ms** | 790 ms | 9.6 GB | Apache 2.0 |
| Ollama | gemma4:e4b-it-qat | 87.3 | 83.4 to 91.1 | 435 ms | 729 ms | 6.1 GB | Apache 2.0 |
| Ollama | qwen3.5:9b | 86.4 | 81.9 to 90.4 | 695 ms | 1.2 s | 6.6 GB | Apache 2.0 |
| Ollama | phi4:14b | 86.1 | 82.8 to 89.3 | 819 ms | 1.6 s | 9.1 GB | MIT |
| Ollama | **gemma4:e2b** | **83.5** | 77.7 to 88.7 | **327 ms** | 532 ms | 7.2 GB | Apache 2.0 |
| LM Studio | google/gemma-4-e2b | 83.0 | 77.5 to 87.9 | 327 ms | 597 ms | 4.1 GB | Apache 2.0 |
| Foundry Local | qwen2.5-7b | 83.0 | 77.6 to 88.1 | 737 ms | 1.2 s | 5.5 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-4b-2507 | 82.9 | 78.1 to 87.5 | 370 ms | 707 ms | 2.3 GB | Apache 2.0 |
| LM Studio | google/gemma-3-4b | 82.9 | 77.7 to 87.8 | 446 ms | 833 ms | 3.1 GB | Gemma terms |
| Ollama | gemma3:4b | 82.2 | 76.3 to 87.3 | 488 ms | 779 ms | 3.3 GB | Gemma terms |
| Ollama | qwen3.5:4b | 81.8 | 77.1 to 86.3 | 480 ms | 757 ms | 3.4 GB | Apache 2.0 |
| Ollama | qwen3:4b-instruct | 81.7 | 75.8 to 87.4 | 409 ms | 689 ms | 2.5 GB | Apache 2.0 |
| Ollama | **granite4:3b** | **80.7** | 75.1 to 85.8 | **326 ms** | 587 ms | **2.1 GB** | Apache 2.0 |
| LM Studio | ibm/granite-4-micro | 79.9 | 75.1 to 84.5 | 348 ms | 542 ms | 2.0 GB | Apache 2.0 |
| Ollama | gemma4:e2b-it-qat | 79.4 | 72.8 to 85.3 | 284 ms | 464 ms | 4.3 GB | Apache 2.0 |
| Ollama | phi4-mini:3.8b | 78.7 | 71.5 to 85.2 | 346 ms | 612 ms | 2.5 GB | MIT |
| Foundry Local | **qwen2.5-1.5b** | **71.5** | 64.1 to 78.6 | **393 ms** | 819 ms | 1.2 GB | Apache 2.0 |

No model in this part of the board fell back once in 75 answers.

### On the CPU only (Ryzen 9 9900X), the top of the board

| Runtime | Model | Quality | 95% interval | Typical | Slowest 1 in 20 | Download | License |
|---|---|---:|---|---:|---:|---:|---|
| Ollama | gemma4:e4b | 88.9 | 85.8 to 91.9 | 4.6 s | 6.8 s | 9.6 GB | Apache 2.0 |
| Ollama | gemma4:e4b-it-qat | 87.6 | 83.7 to 91.3 | 5.1 s | 8.8 s | 6.1 GB | Apache 2.0 |
| Ollama | qwen3:4b-instruct | 83.1 | 77.9 to 88.2 | 5.0 s | 7.9 s | 2.5 GB | Apache 2.0 |
| Ollama | gemma4:e2b | 83.0 | 77.4 to 88.1 | 3.3 s | 5.9 s | 7.2 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-4b-2507 | 82.3 | 76.8 to 87.4 | 4.5 s | 10.6 s | 2.3 GB | Apache 2.0 |
| LM Studio | google/gemma-3-4b | 82.0 | 76.1 to 87.3 | 3.8 s | 7.1 s | 3.1 GB | Gemma terms |
| LM Studio | **google/gemma-4-e2b** | **81.7** | 75.6 to 87.1 | **2.4 s** | 4.3 s | 4.1 GB | Apache 2.0 |
| Ollama | granite4:3b | 79.8 | 74.2 to 85.2 | 3.9 s | 6.1 s | 2.1 GB | Apache 2.0 |
| Ollama | phi4-mini:3.8b | 79.7 | 72.0 to 86.4 | 3.7 s | 6.6 s | 2.5 GB | MIT |
| Ollama | gemma3:4b | 79.5 | 72.8 to 85.7 | 3.3 s | 6.4 s | 3.3 GB | Gemma terms |
| Ollama | gemma4:e2b-it-qat | 79.1 | 72.2 to 85.7 | 2.2 s | 3.5 s | 4.3 GB | Apache 2.0 |
| Foundry Local | phi-4-mini | 78.9 (54.4 within the 12 s limit) | 71.6 to 85.7 | 16.7 s | 29.1 s | 4.8 GB | MIT |
| Foundry Local | qwen3.5-2b-text | 73.2 (68.4 within the limit) | 65.1 to 80.8 | 9.3 s | 13.2 s | 1.4 GB | Apache 2.0 |
| Foundry Local | **qwen2.5-1.5b** | **62.9** | 56.5 to 68.9 | **6.3 s** | 8.9 s | 1.8 GB | Apache 2.0 |
| Foundry Local | qwen3-1.7b (the 0.5.1 default) | 46.5 | 39.7 to 53.3 | 9.2 s | 14.2 s | 1.3 GB | Apache 2.0 |

Scribe gives Foundry Local 12 s per request, and a slower answer reaches the user as the raw text;
the score in parentheses is what that limit leaves. The same model is two to five times faster in
Ollama or LM Studio than in Foundry Local on the CPU (Qwen2.5 1.5B 2.5 s against 6.3 s, Phi-4 Mini
3.7 s against 16.7 s), largely because llama.cpp reuses the instructions it already processed while
Foundry Local's CPU path processes all of them again for every dictation.

### Cloud models graded in the same packets

| Model | Quality | 95% interval | Typical | Slowest 1 in 20 |
|---|---:|---|---:|---:|
| gpt-5.6-terra | 96.0 | 94.7 to 97.2 | 2.1 s | 3.7 s |
| gpt-5.6-sol | 95.6 | 93.7 to 97.2 | 3.0 s | 6.1 s |
| gpt-5.4-mini | 88.3 | 83.2 to 92.7 | 2.1 s | 3.6 s |

Their answers and times come from the [September 4 benchmark](gpt6-astra-benchmark.md) of the same
25 dictations; they were graded again here, blind, in the same packets as the local answers.

### Where local models still fall short

Per-case scores for the picks, against the cloud:

| Dictation | gpt-5.6-terra | gpt-5.4-mini | gemma4:e4b | gemma4:e2b | qwen3:4b-instruct | granite4:3b | Foundry qwen2.5-1.5b (GPU) |
|---|---:|---:|---:|---:|---:|---:|---:|
| self-correction | 95.4 | 77.2 | 77.0 | **38.8** | 74.2 | 84.8 | 52.9 |
| voice-with-values | 90.1 | 58.5 | 97.0 | **53.0** | 47.8 | 43.2 | 36.2 |
| ai-model-comparison | 92.0 | 68.2 | 58.5 | 66.2 | 70.6 | 61.5 | 71.1 |
| dialogue-phonetic | 95.7 | 97.7 | 68.8 | 71.8 | 55.2 | 77.5 | 30.0 |
| phonetic-wav-narrative | 92.7 | 56.4 | 84.9 | 75.0 | 63.3 | 65.9 | 52.5 |
| kitchen-sink | 94.5 | 79.8 | 82.9 | 83.7 | 63.7 | 61.9 | 54.0 |
| numbers-dates | 96.2 | 91.5 | 98.1 | 83.8 | 97.2 | 89.8 | 87.4 |
| instruction-immunity | 96.1 | 95.7 | 86.3 | 88.0 | 97.2 | 92.5 | 88.8 |
| blunt-opinion | 97.2 | 95.1 | 95.2 | 95.2 | 95.2 | 96.5 | 99.1 |
| corporate-bait | 98.8 | 100.0 | 94.3 | 91.9 | 98.9 | 94.3 | 92.3 |

Ordinary dictation (fillers, punctuation, numbers, keeping the speaker's voice, not obeying an
instruction inside the text) is handled at 85 to 99 by every pick. The gap is in resolving a chain
of corrections ("Friday, no, Thursday, actually make it Tuesday"), repairing badly misheard words,
and technical-name comparisons. Gemma 4 E2B is the weakest of the picks on self-corrections; if you
correct yourself mid-sentence a lot, E4B or Granite 4 3B does better.

## Speed

### Cold start and readying

Measured with the exact request Scribe sends, against Ollama, for a dictation of 6 seconds: a
loaded model; the first dictation after Ollama unloaded it; and the same, with Scribe 0.5.2's
readying request sent when the recording started.

| Model | Loaded | After an unload | After an unload, readied while speaking |
|---|---:|---:|---:|
| gemma4:e2b-it-qat, GPU | 396 ms | 5.2 s | **452 ms** |
| granite4:3b, GPU | 310 ms | 2.7 s | **334 ms** |
| qwen3:4b-instruct, GPU | 474 ms | 3.1 s | **486 ms** |
| qwen3:1.7b, GPU | 337 ms | 2.8 s | **402 ms** |
| gemma4:e2b-it-qat, CPU | 4.4 s | 14.5 s | 8.2 s |
| granite4:3b, CPU | 2.5 s | 12.1 s | 5.9 s |
| qwen3:1.7b, CPU | 2.7 s | 7.4 s | 2.7 s |

On the GPU the load finishes while you speak. On the CPU a load takes longer than 6 seconds of
speech for the larger models, so the readying request halves the wait instead of removing it. Since
round two (above), Scribe also asks Ollama and LM Studio to keep the model loaded as long as its own
"Free memory when Scribe isn't used" setting says, so they no longer unload it after their own five
minutes (Ollama) while you still use Scribe, and free it on their own clock after that time. Set that
setting to Never to leave their own policy in place (Ollama keeps the time it was last asked for).

### Tips for each runtime

- **Ollama:** 0.34.4 and 0.35.0 were measured. Keep Automatic instructions. Scribe fits every request to the
  context Ollama loaded the model with (its default is 4,096 tokens below 24 GB of graphics memory), the dictation
  first. To give the model more room, choose a **Context size** under Ollama settings in AI cleanup, or change
  **Context length** in Ollama's settings (`OLLAMA_CONTEXT_LENGTH`).
- **LM Studio:** 0.4.25 with the CUDA 12 llama.cpp runtime 2.47.0 was measured. Its lmstudio-community
  `phi-4-mini-instruct` build scored 49.6 against 78.7 for Ollama's `phi4-mini`; use Ollama for
  Phi-4 Mini. Speculative decoding does not help dictation: LM Studio takes a draft model only when the model
  loads (a request that names one is refused), and loaded with Qwen3 0.6B as its draft, Qwen3 4B 2507 took
  0.74 s typically against 0.41 s without, because a cleanup writes only about 60 tokens.
- **Foundry Local:** see below.

## What it costs

| Where cleanup runs | Input tokens per dictation | Billed | Notes |
|---|---:|---|---|
| Microsoft Foundry, 0.5.2: detailed instructions and the vocabulary the dictation mentions | about 1,450 | Yes | Round two: 1,412 to 1,464 over the frozen dictations, about half from the prompt cache |
| Microsoft Foundry, 0.5.1: detailed instructions and every word pack AI cleanup may use | 8,860 to 9,020 | Yes | From the maintainer's own log over two days: 8,834 to 8,843 of them from the prompt cache, 12 to 279 output tokens, 1.4 to 11 s |
| A model on this PC, short instructions with 80 terms | about 2,000 | No | Ollama and LM Studio cache all but the dictated words after the first request (2,074 of 2,075 cached) |
| A model on this PC, no vocabulary | about 1,050 | No | |

A model running on this PC costs no tokens, and nothing leaves the PC. Until 0.5.2 the cloud requests
were large because they carried the detailed instructions and the whole vocabulary (up to 5,000 terms),
every time, whether or not the dictation mentioned them; most of it came from the prompt cache, which
made it cheaper but not free. 0.5.2 sends the terms each dictation mentions. For comparison, the cleanup
instructions in [Handy](https://github.com/cjpais/Handy) are about 220 tokens and it never sends vocabulary.

## Experiments that kept the defaults

| Change | Result | Decision |
|---|---|---|
| No vocabulary at all (Ollama, GPU, 15 models) | +1.0 points on average (11 up, 4 down), input halved from about 2,000 to 1,050 tokens | Kept for now; see below |
| No vocabulary (Foundry Local, CPU, 3 models) | +0.8 to +8.6 points, and **30 to 37% faster** (qwen2.5-1.5b 6.3 s to 4.0 s, qwen3-1.7b 9.2 s to 6.6 s) | Kept for now; see below |
| The server's own temperature instead of 0.1 (6 models) | -5.0 points on average; qwen3.5:2b -14.7, llama3.2:3b -11.0 | 0.1 |
| Short instructions without their worked example (15 models) | +1.7 on average, but the top models lost 0.5 to 1.7; only LFM2.5 1.2B, which copied the example, gained much (+24) | Keep the example |
| Detailed instructions with 80 terms, where they fit (15 models) | +0.5 on average, 9 of 15 down; gemma4:e2b -4.6 | Short instructions for a model on this PC |

**The vocabulary** doubled every request and bought no measurable quality for these models, even on
the seven dictations full of AI model names and terms that the two default word packs cover. The first
round kept it; round two (above) found why it bought so little, the 80 terms a model on this PC got were
rarely the ones a dictation needed, and replaced it with the terms each dictation mentions.

## Foundry Local on this PC

Foundry Local is the only runtime that needs nothing else installed, and its SDK chooses the
hardware itself.

### Foundry Local 2.1.0 (what 0.5.2 ships)

Scribe 0.5.2 moves from SDK 1.2.4 to 2.1.0, one package with WinML built in. On this PC it registers three
execution providers where 1.2.4 had two working: TensorRT-RTX, **CUDA** (new) and WebGPU. Measured with the
shipped configuration, lists rubric, 33 dictations, two runs, graded in the same packets as the round-two table
(evidence: `docs/benchmarks/foundry-local-2.1.0-2026-09-30.json`):

| Model | Build the SDK picks | Quality | Typical | 95th percentile | Download |
|---|---|---:|---:|---:|---:|
| **Qwen2.5 7B** | TensorRT-RTX | **85.4** (1.2.4: 85.7) | 0.74 s | 1.12 s | 4.7 GB |
| Qwen2.5 7B | CUDA (chosen by id) | 84.0 | 0.73 s | 1.49 s | 4.7 GB |
| Qwen3 8B | CUDA | 82.0 | 1.00 s | 1.90 s | 5.7 GB |
| Gemma 4 E2B | CUDA | 81.9 | 1.37 s | **7.47 s** | 5.9 GB |
| Qwen3.5 4B | CUDA | 81.7 | 1.27 s | 2.24 s | 4.2 GB |
| Phi-4 Mini | CUDA | 80.3 (1.2.4 CPU: 16.7 s) | 0.79 s | 1.36 s | 3.7 GB |
| Qwen3 4B | CUDA | 79.3 (1.2.4 CPU: 15.8 s) | 0.72 s | 1.45 s | 2.7 GB |
| Qwen2.5 1.5B | TensorRT-RTX | 74.0 (1.2.4: 73.5) | 0.37 s | 0.64 s | 1.3 GB |
| Ministral 3 3B | CUDA | 67.8 | 0.59 s | 1.10 s | 3.7 GB |
| Qwen2.5 1.5B | CUDA (chosen by id) | 67.0 | 0.53 s | 0.92 s | 1.3 GB |
| OLMo 3 7B | CUDA | 60.7 | 1.07 s | 2.40 s | 5.2 GB |
| Qwen3 1.7B | CUDA | 59.1 | 0.70 s | 1.66 s | 1.3 GB |

On the processor alone (one run): Qwen3.5 0.8B 70.2 in 4.5 s, Qwen2.5 1.5B 66.1 in 6.7 s (1.2.4: 69.2 in 6.2 s, within
the judge's noise), Qwen3 1.7B 59.5 in 12.6 s. Qwen3.5 0.8B is the quickest there, but its CUDA build took 0.80 s on
the graphics card where Qwen2.5 1.5B's TensorRT-RTX build takes 0.37 s, so the default stays; it is a candidate for a
processor-only default once more runs confirm it. Note that the SDK prefers a build already downloaded: once Qwen3.5
0.8B's CPU build was on disk, its family name resolved to it even with CUDA available.

What changed, and what Scribe does about it:

- **Models that ran on the processor now run on the graphics card.** Phi-4 Mini and Qwen3 4B went from 16 s on
  the CPU (1.2.4, whose CUDA runtime failed to start here) to under 0.8 s on CUDA. Their Settings hints say so.
- **Thinking off.** 2.1.0 honors `reasoning_effort: "none"`, which Scribe now sends to Foundry Local as it does
  to Ollama: Qwen3.5 4B spent all 2,048 of its output tokens thinking (33 s) on a one-sentence edit without
  it, and answered in 0.58 s with it.
- **RTX 50 series cards need cuDNN attention off.** ONNX Runtime prefers cuDNN's attention kernel on these
  cards, and with it Qwen3 4B's CUDA build failed every request and Gemma 4 E2B's first request took 16 s
  (1.3 s without). Scribe sets `ORT_ENABLE_CUDNN_FLASH_ATTENTION=0` before Foundry Local starts, as ONNX
  Runtime documents, unless you set it yourself.
- **A graphics card build that fails its first request moves to its CPU build for the rest of the session**, and
  the failed build is unloaded. ONNX Runtime reports running out of graphics memory in the same words, so the next
  start tries the graphics card again. TensorRT-RTX builds get this fallback too.
- **Models 0.5.1 moved to the processor move back.** On an NVIDIA card, 0.5.1 moved every Qwen3 and Phi-4 Mini model
  to its CPU build after the WebGPU failure and remembered it. 0.5.2 sets that aside and starts the model on the build
  Foundry Local would pick on a new install (for Qwen3 1.7B here, CUDA: 0.70 s instead of 12.6 s), downloading it if
  needed; left alone, Foundry Local would keep the build already on disk. If that build can't be downloaded, loaded or
  answer, the CPU build serves until Scribe restarts, and the next start tries again.
- **The first request after a load is slow** (4.4 s for Qwen2.5 1.5B on TensorRT-RTX). Setup already absorbs it;
  when Scribe loads a model it freed, it now sends the readiness check first, while you are still speaking.
- **Still broken upstream in 2.1.0:** every build of Qwen3.5 2B ("Invalid rank for input: position_ids"; it left
  Scribe's list), SmolLM3 3B's chat template ("Unknown method: replace"), and Gemma 4 E2B on the CPU (no answer
  within 180 s). Gemma 4 E2B on CUDA is erratic: typically 1.4 s, but 6 to 7 s on about one request in five.
- **The speech engine moved with it.** Foundry Local brings ONNX Runtime 1.30.0, which replaces the 1.28.2 that
  sherpa-onnx ships for speech recognition too; the recognizer check and the scenario suite pass on it.

The defaults stay: **Qwen2.5 1.5B** runs on any PC (0.37 s on an NVIDIA RTX GPU, 6.7 s on the CPU), and a first
setup on an NVIDIA RTX card with 8 GB or more starts from **Qwen2.5 7B**, still the best model Foundry Local offers
for this job. The gap to Ollama and LM Studio is now the catalog, not the runtime: it has no Gemma 4 E4B and no
Qwen3 4B Instruct 2507, the models that lead the local board.

### Foundry Local 1.2.4 (what 0.5.1 shipped)

On this PC, with SDK 1.2.4 (WinML) and the ONNX Runtime 1.28.2 that Scribe loaded in process:

- **Every WebGPU build failed to start** with "Failed to create a WebGPU compute pipeline" (in
  `QuickGelu`): Qwen3 0.6B, 1.7B and 4B, Qwen3.5, Phi-4 Mini, Mistral NeMo. Scribe moves each to its
  CPU build and remembers, as designed. Scribe's notes already recorded this failure on Snapdragon
  Adreno and Intel Lunar Lake; it is not vendor specific.
- **The builds that ran on the GPU were TensorRT-RTX builds**: of those tested, the Qwen2.5 family
  (0.5B, 1.5B, 7B) and Phi-3.5 Mini. Phi-4's TensorRT-RTX build failed to load ("failed to
  deserialize engine"), and the CUDA runtime failed to initialize (Windows error 1114).
- SmolLM3 3B failed its chat template ("Unknown method: replace"), and Gemma 4 E2B failed at load
  ("GroupQueryAttention: query and key shall have same dim").
- The CPU builds showed no prompt caching: halving the prompt made them 30 to 37% faster, where
  Ollama and LM Studio reuse the instructions they already processed.

These were upstream issues to report to Foundry Local. With 1.2.4, **Qwen2.5 1.5B** became the
default because it was the one small model that ran on an NVIDIA RTX GPU here (0.39 s) and the
fastest curated model on the CPU (6.3 s), and `qwen2.5-7b` scored 83.0 at 0.74 s. 2.1.0 fixed the
CUDA runtime and the Gemma 4 E2B load failure, and Qwen3 1.7B's WebGPU build ran without the shader failure (three
dictations, about 30% slower per token than its CUDA build; the other WebGPU builds were not retested). SmolLM3's
template failure remains.

## Method

- **Dictations:** the 25 frozen transcripts of the September 4 benchmark
  ([evidence](benchmarks/gpt6-astra-2026-09-04.json)): speech synthesized from 25 hard scripts and
  recognized by Scribe's Parakeet model, so they carry real recognition errors.
- **Harness:** `tools/Scribe.Evals` drives Scribe's own `TextCleanupService`, with its instructions,
  chunking, guards and answer cleanup, through each runtime: Ollama and LM Studio through the
  OpenAI-compatible provider exactly as Settings configures them, Foundry Local through its SDK in
  process. Vocabulary: the two word packs a fresh install lets AI cleanup use, at the budget the app
  applies. Each model is unloaded before the next one loads; times exclude the first request after a
  load, which is reported separately.
- **CPU runs:** the same models kept off the GPU: Ollama variants created with `num_gpu 0`,
  LM Studio `lms load --gpu off`, Foundry Local's CPU builds.
- **Judge:** Claude Opus 5.5 through the GitHub Copilot command-line tool, blind to the model and
  runtime. Each request graded up to 12 distinct answers to one dictation against its golden
  reference, and every request also graded two calibration answers (the raw text and a cloud
  answer) to measure drift between requests. Score: 45% faithfulness, 25% following the
  instructions, 20% removing disfluencies, 10% mechanics, the weighting of the September 4 review.
  Intervals: 10,000-draw bootstrap over the 25 dictations.
- **Uncertainty:** the same calibration answer's grade varied by 8.9 points on average between
  requests (28 at most), so differences under about 5 points between models are not a ranking.
  Scores are comparable within this report only, not with the July letter grades or the September 4
  scores. One PC, one run of the judge.
- **The 0.5.2 answer cleanup** (removing announcements, labels, think tags, wrapper tags and trailing
  notes) was finished after most runs, so the tables show the answers as those runs typed them. It was
  then applied to the recorded answers with `--resanitize` and the changed answers graded blind: 181 of
  about 8,000 answers changed, each removal was reviewed, and none took the dictation's own words. It
  moved Foundry Local's Qwen3 1.7B on the CPU from 46.5 to 56.4 and Qwen3 0.6B from 41.4 to 46.8, and
  changed no answer of the picks (table above, under What 0.5.2 changes). An answer the old cleanup
  rejected was stored as the raw text and could not be recovered, so these gains are a lower bound.
- **A last check on the finished code:** the three picks, run once more with every 0.5.2 change in
  place, scored 83.8 (`gemma4:e2b`, Ollama, 355 ms typically), 83.3 (`google/gemma-4-e2b`, LM Studio,
  341 ms) and 69.9 (`qwen2.5-1.5b`, Foundry Local, 386 ms), with no answer falling back.

## Reproduce

```powershell
# Ollama on the GPU, the picks, three runs of the frozen dictations
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-cloud --runs 3 --no-judge `
  --local-models "ollama:gemma4:e2b,ollama:gemma4:e4b,ollama:granite4:3b,ollama:qwen3:4b-instruct" `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json --glossary-libraries default --out runs/ollama

# LM Studio and Foundry Local use the lmstudio: and foundry: prefixes
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-cloud --runs 3 --no-judge `
  --local-models "lmstudio:google/gemma-4-e2b,foundry:qwen2.5-1.5b" `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json --glossary-libraries default --out runs/other

# Blind grading through the GitHub Copilot command-line tool, with cloud answers as references
dotnet run -c Release --project tools/Scribe.Evals -- --blind-judge `
  --judge-results runs/ollama/results.json,runs/other/results.json `
  --judge-anchors "docs/benchmarks/gpt6-astra-2026-09-04.json:gpt-5.6-terra@1,docs/benchmarks/gpt6-astra-2026-09-04.json:gpt-5.4-mini@1" `
  --out runs/judge

# Round two: the vocabulary each dictation mentions (--vocabulary all|mentioned|none), a Microsoft Foundry
# reasoning effort (--reasoning-effort none|low|medium; omit it for Scribe's own negotiation), and a quota-safe
# pace between requests. The 8 structure cases are in BenchmarkCases; synthesize them once without
# --cases-from (set SCRIBE_MODELS_DIR to the speech models), then freeze the transcripts with the 25.
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-local --runs 2 --no-judge `
  --cloud-models gpt-6-sol --endpoint https://<resource>.cognitiveservices.azure.com/ `
  --reasoning-effort none --vocabulary mentioned --pace-ms 3000 `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json --glossary-libraries default --out runs/cloud

# Grade against a different writing style in a store of its own: grades are keyed by case and answer only
dotnet run -c Release --project tools/Scribe.Evals -- --blind-judge --writing-style-file style.txt `
  --judge-results runs/cloud/results.json --out runs/judge-style

# Round three: every built-in word pack ("all"), a context size (for Ollama, through its own API), and the whole
# vocabulary through the production admission path (--whole-vocabulary; leave it out for the terms each dictation
# mentions). --glossary-csv adds word pack files; keep a cloud judge off runs that carry private terms.
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-cloud --runs 3 --no-judge `
  --local-models "ollama:gemma4:e4b,ollama:gemma4:e2b,lmstudio:google/gemma-4-e2b" `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json --glossary-libraries all --vocabulary mentioned `
  --context-size 32768 --whole-vocabulary --out runs/whole32
```

The scores, times and token counts behind every table are in
[benchmarks/local-models-2026-09-29.json](benchmarks/local-models-2026-09-29.json) (round one),
[benchmarks/cleanup-tuning-2026-09-29.json](benchmarks/cleanup-tuning-2026-09-29.json) (round two) and
[benchmarks/context-window-2026-09-30.json](benchmarks/context-window-2026-09-30.json) (round three).

## Full results

As recorded, before the 0.5.2 answer cleanup (see above). **Within the 12 s limit** applies to Foundry Local only; Scribe gives a server on this PC 45 s.

### Every model on the GPU (RTX 5080, 16 GB)

| Runtime | Model | Quality | 95% interval | Typical | Slowest 1 in 20 | Fell back | Download | License |
|---|---|---:|---|---:|---:|---:|---|---|
| Ollama | gemma4:12b | 92.3 | 89.5 to 94.8 | 888 ms | 1.4 s | 0/75 | 7.6 GB | Apache 2.0 |
| Ollama | gemma4:e4b | 89.2 | 85.2 to 92.7 | 499 ms | 790 ms | 0/75 | 9.6 GB | Apache 2.0 |
| Ollama | gemma4:e4b-it-qat | 87.3 | 83.4 to 91.1 | 435 ms | 729 ms | 0/75 | 6.1 GB | Apache 2.0 |
| Ollama | qwen3.5:9b | 86.4 | 81.9 to 90.4 | 695 ms | 1.2 s | 0/75 | 6.6 GB | Apache 2.0 |
| Ollama | phi4:14b | 86.1 | 82.8 to 89.3 | 819 ms | 1.6 s | 0/75 | 9.1 GB | MIT |
| Ollama | gemma4:e2b | 83.5 | 77.7 to 88.7 | 327 ms | 532 ms | 0/75 | 7.2 GB | Apache 2.0 |
| LM Studio | google/gemma-4-e2b | 83.0 | 77.5 to 87.9 | 327 ms | 597 ms | 0/75 | 4.1 GB | Apache 2.0 |
| Foundry Local | qwen2.5-7b | 83.0 | 77.6 to 88.1 | 737 ms | 1.2 s | 0/75 | 5.5 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-4b-2507 | 82.9 | 78.1 to 87.5 | 370 ms | 707 ms | 0/75 | 2.3 GB | Apache 2.0 |
| LM Studio | google/gemma-3-4b | 82.9 | 77.7 to 87.8 | 446 ms | 833 ms | 0/75 | 3.1 GB | Gemma terms |
| Ollama | gemma3:4b | 82.2 | 76.3 to 87.3 | 488 ms | 779 ms | 0/75 | 3.3 GB | Gemma terms |
| Ollama | qwen3.5:4b | 81.8 | 77.1 to 86.3 | 480 ms | 757 ms | 0/75 | 3.4 GB | Apache 2.0 |
| Ollama | qwen3:4b-instruct | 81.7 | 75.8 to 87.4 | 409 ms | 689 ms | 0/75 | 2.5 GB | Apache 2.0 |
| Ollama | granite4:3b | 80.7 | 75.1 to 85.8 | 326 ms | 587 ms | 0/75 | 2.1 GB | Apache 2.0 |
| LM Studio | ibm/granite-4-micro | 79.9 | 75.1 to 84.5 | 348 ms | 542 ms | 0/75 | 2.0 GB | Apache 2.0 |
| Ollama | gemma4:e2b-it-qat | 79.4 | 72.8 to 85.3 | 284 ms | 464 ms | 0/75 | 4.3 GB | Apache 2.0 |
| Ollama | phi4-mini:3.8b | 78.7 | 71.5 to 85.2 | 346 ms | 612 ms | 0/75 | 2.5 GB | MIT |
| Ollama | granite4:micro-h | 78.4 | 72.5 to 83.9 | 396 ms | 589 ms | 0/75 | 1.9 GB | Apache 2.0 |
| Ollama | ministral-3:8b | 76.5 | 72.1 to 80.5 | 545 ms | 1.0 s | 0/75 | 6.0 GB | Apache 2.0 |
| Ollama | SmolLM3-3B | 75.5 | 69.8 to 81.1 | 331 ms | 535 ms | 0/75 | 1.9 GB | Apache 2.0 |
| Ollama | granite4:1b | 73.4 | 67.1 to 79.4 | 388 ms | 703 ms | 0/75 | 3.3 GB | Apache 2.0 |
| Ollama | qwen3.5:2b | 72.2 | 64.1 to 79.7 | 390 ms | 593 ms | 0/75 | 2.7 GB | Apache 2.0 |
| Ollama | llama3.2:3b | 71.8 | 65.9 to 78.0 | 321 ms | 481 ms | 0/75 | 2.0 GB | Llama 3.2 |
| Foundry Local | qwen2.5-1.5b | 71.5 | 64.1 to 78.6 | 393 ms | 819 ms | 0/75 | 1.2 GB | Apache 2.0 |
| Ollama | LFM2-2.6B | 71.1 | 63.5 to 78.4 | 260 ms | 417 ms | 0/75 | 1.6 GB | LFM Open |
| LM Studio | qwen/qwen3.5-2b | 69.0 | 60.4 to 77.3 | 274 ms | 422 ms | 0/75 | 1.8 GB | Apache 2.0 |
| LM Studio | meta/llama-3.2-3b | 68.7 | 64.1 to 73.6 | 334 ms | 542 ms | 2/75 | 1.9 GB | Llama 3.2 |
| LM Studio | qwen/qwen3-1.7b | 67.3 | 58.9 to 75.4 | 220 ms | 432 ms | 0/75 | 1.3 GB | Apache 2.0 |
| Ollama | qwen3:1.7b | 67.1 | 60.2 to 74.1 | 243 ms | 424 ms | 0/75 | 1.4 GB | Apache 2.0 |
| LM Studio | qwen/qwen3.5-0.8b | 65.5 | 57.9 to 72.9 | 231 ms | 399 ms | 0/75 | 0.95 GB | Apache 2.0 |
| Ollama | qwen2.5:1.5b | 65.3 | 58.7 to 71.8 | 185 ms | 344 ms | 1/75 | 0.99 GB | Apache 2.0 |
| Ollama | ministral-3:3b | 64.8 | 59.2 to 69.8 | 343 ms | 619 ms | 0/75 | 3.0 GB | Apache 2.0 |
| Foundry Local | qwen2.5-0.5b | 64.4 | 56.5 to 72.0 | 194 ms | 312 ms | 0/75 | 0.5 GB | Apache 2.0 |
| Foundry Local | phi-3.5-mini | 63.6 | 56.9 to 70.1 | 567 ms | 995 ms | 0/75 | 2.1 GB | MIT |
| Ollama | qwen3.5:0.8b | 63.5 | 56.1 to 70.6 | 253 ms | 407 ms | 0/75 | 1.0 GB | Apache 2.0 |
| Ollama | qwen2.5:0.5b | 63.2 | 55.9 to 70.5 | 162 ms | 241 ms | 0/75 | 0.40 GB | Apache 2.0 |
| Ollama | granite4:1b-h | 60.8 | 51.0 to 70.2 | 342 ms | 581 ms | 0/75 | 1.6 GB | Apache 2.0 |
| Ollama | gemma3:1b | 59.1 | 51.9 to 66.0 | 246 ms | 676 ms | 5/75 | 0.82 GB | Gemma terms |
| Ollama | LFM2-1.2B | 57.4 | 50.0 to 64.8 | 185 ms | 258 ms | 0/75 | 0.73 GB | LFM Open |
| Ollama | qwen3:0.6b | 57.0 | 48.2 to 65.4 | 179 ms | 291 ms | 1/75 | 0.52 GB | Apache 2.0 |
| Ollama | granite4:350m-h | 56.7 | 47.0 to 65.9 | 208 ms | 300 ms | 0/75 | 0.37 GB | Apache 2.0 |
| Ollama | LFM2-700M | 55.9 | 50.2 to 61.5 | 142 ms | 385 ms | 6/75 | 0.47 GB | LFM Open |
| Ollama | qwen3:4b | 54.4 | 50.5 to 58.3 | 1.4 s | 2.4 s | 75/75 | 2.5 GB | Apache 2.0 |
| Ollama | smollm2:360m | 54.4 | 50.1 to 58.9 | 196 ms | 613 ms | 8/75 | 0.73 GB | Apache 2.0 |
| LM Studio | lfm2-1.2b | 53.1 | 46.2 to 59.8 | 188 ms | 261 ms | 0/75 | 0.68 GB | LFM Open |
| Ollama | smollm2:1.7b | 53.0 | 43.0 to 63.1 | 271 ms | 476 ms | 1/75 | 1.8 GB | Apache 2.0 |
| LM Studio | gemma-3-1b-it | 51.7 | 45.1 to 58.4 | 288 ms | 1.0 s | 16/75 | 0.75 GB | Gemma terms |
| LM Studio | phi-4-mini-instruct | 49.6 | 44.2 to 55.1 | 393 ms | 654 ms | 3/75 | 2.3 GB | MIT |
| LM Studio | google/gemma-3-270m | 49.1 | 41.9 to 55.8 | 428 ms | 686 ms | 57/75 | 0.22 GB | Gemma terms |
| Ollama | granite4:350m | 48.9 | 41.5 to 56.1 | 159 ms | 269 ms | 0/75 | 0.71 GB | Apache 2.0 |
| Ollama | gemma3:270m | 47.3 | 40.1 to 54.0 | 270 ms | 474 ms | 56/75 | 0.29 GB | Gemma terms |
| LM Studio | llama-3.2-1b-instruct | 47.3 | 41.2 to 53.2 | 394 ms | 728 ms | 42/75 | 0.75 GB | Llama 3.2 |
| LM Studio | qwen3-0.6b | 47.0 | 36.5 to 57.9 | 178 ms | 409 ms | 0/75 | 0.45 GB | Apache 2.0 |
| Ollama | LFM2-350M | 46.7 | 40.7 to 52.6 | 298 ms | 412 ms | 45/75 | 0.23 GB | LFM Open |
| Ollama | llama3.2:1b | 44.8 | 40.5 to 48.7 | 339 ms | 840 ms | 32/75 | 1.3 GB | Llama 3.2 |
| Ollama | LFM2.5-350M | 43.8 | 36.1 to 50.9 | 57 ms | 105 ms | 55/75 | 0.23 GB | LFM Open |
| Ollama | LFM2.5-1.2B-Instruct | 24.9 | 18.0 to 32.7 | 167 ms | 234 ms | 0/75 | 0.73 GB | LFM Open |
| LM Studio | lfm2.5-1.2b-instruct | 22.8 | 17.4 to 28.7 | 155 ms | 229 ms | 0/75 | 0.68 GB | LFM Open |

### Every model on the CPU only (Ryzen 9 9900X, 12 cores)

| Runtime | Model | Quality | 95% interval | Within the 12 s limit | Typical | Slowest 1 in 20 | Fell back | Download | License |
|---|---|---:|---|---:|---:|---:|---:|---|---|
| Ollama | gemma4:e4b | 88.9 | 85.8 to 91.9 | same | 4.6 s | 6.8 s | 0/25 | 9.6 GB | Apache 2.0 |
| Ollama | gemma4:e4b-it-qat | 87.6 | 83.7 to 91.3 | same | 5.1 s | 8.8 s | 0/25 | 6.1 GB | Apache 2.0 |
| Ollama | qwen3:4b-instruct | 83.1 | 77.9 to 88.2 | same | 5.0 s | 7.9 s | 0/25 | 2.5 GB | Apache 2.0 |
| Ollama | gemma4:e2b | 83.0 | 77.4 to 88.1 | same | 3.3 s | 5.9 s | 0/25 | 7.2 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-4b-2507 | 82.3 | 76.8 to 87.4 | same | 4.5 s | 10.6 s | 0/25 | 2.3 GB | Apache 2.0 |
| LM Studio | google/gemma-3-4b | 82.0 | 76.1 to 87.3 | same | 3.8 s | 7.1 s | 0/25 | 3.1 GB | Gemma terms |
| LM Studio | google/gemma-4-e2b | 81.7 | 75.6 to 87.1 | same | 2.4 s | 4.3 s | 0/25 | 4.1 GB | Apache 2.0 |
| Ollama | granite4:3b | 79.8 | 74.2 to 85.2 | same | 3.9 s | 6.1 s | 0/25 | 2.1 GB | Apache 2.0 |
| Ollama | phi4-mini:3.8b | 79.7 | 72.0 to 86.4 | same | 3.7 s | 6.6 s | 0/25 | 2.5 GB | MIT |
| Ollama | gemma3:4b | 79.5 | 72.8 to 85.7 | same | 3.3 s | 6.4 s | 0/25 | 3.3 GB | Gemma terms |
| LM Studio | ibm/granite-4-micro | 79.4 | 74.2 to 84.3 | same | 3.2 s | 5.8 s | 0/25 | 2.0 GB | Apache 2.0 |
| Ollama | gemma4:e2b-it-qat | 79.1 | 72.2 to 85.7 | same | 2.2 s | 3.5 s | 0/25 | 4.3 GB | Apache 2.0 |
| Foundry Local | phi-4-mini | 78.9 | 71.6 to 85.7 | 54.4 | 16.7 s | 29.1 s | 0/25 | 4.8 GB | MIT |
| Ollama | granite4:micro-h | 74.4 | 65.9 to 81.6 | same | 4.3 s | 6.8 s | 0/25 | 1.9 GB | Apache 2.0 |
| Foundry Local | qwen3-4b | 73.6 | 65.0 to 81.4 | 54.4 | 15.8 s | 24.1 s | 0/25 | 2.7 GB | Apache 2.0 |
| Foundry Local | qwen3.5-2b-text | 73.2 | 65.1 to 80.8 | 68.4 | 9.3 s | 13.2 s | 0/25 | 1.4 GB | Apache 2.0 |
| Ollama | qwen3.5:2b | 72.7 | 65.0 to 79.9 | same | 4.5 s | 6.7 s | 0/25 | 2.7 GB | Apache 2.0 |
| Ollama | llama3.2:3b | 72.4 | 66.6 to 78.3 | same | 3.2 s | 6.5 s | 0/25 | 2.0 GB | Llama 3.2 |
| Ollama | granite4:1b | 71.7 | 64.9 to 78.3 | same | 5.4 s | 9.1 s | 0/25 | 3.3 GB | Apache 2.0 |
| Ollama | qwen3:1.7b | 68.4 | 61.4 to 75.4 | same | 2.8 s | 4.9 s | 0/25 | 1.4 GB | Apache 2.0 |
| LM Studio | qwen/qwen3.5-2b | 68.2 | 60.2 to 75.8 | same | 2.2 s | 3.3 s | 0/25 | 1.8 GB | Apache 2.0 |
| Foundry Local | qwen3.5-0.8b | 67.3 | 60.2 to 74.2 | 67.3 | 4.2 s | 5.9 s | 0/25 | 1.0 GB | Apache 2.0 |
| Ollama | qwen2.5:1.5b | 66.9 | 60.0 to 73.5 | same | 2.5 s | 5.8 s | 0/25 | 0.99 GB | Apache 2.0 |
| Ollama | qwen2.5:0.5b | 65.1 | 57.5 to 72.7 | same | 783 ms | 1.2 s | 0/25 | 0.40 GB | Apache 2.0 |
| Ollama | qwen3.5:0.8b | 63.1 | 54.9 to 71.0 | same | 2.5 s | 3.2 s | 0/25 | 1.0 GB | Apache 2.0 |
| Foundry Local | qwen2.5-1.5b | 62.9 | 56.5 to 68.9 | 62.9 | 6.3 s | 8.9 s | 0/25 | 1.8 GB | Apache 2.0 |
| Ollama | gemma3:1b | 61.3 | 55.0 to 67.9 | same | 1.8 s | 4.8 s | 1/25 | 0.82 GB | Gemma terms |
| Ollama | granite4:1b-h | 60.1 | 49.4 to 70.5 | same | 3.4 s | 4.9 s | 0/25 | 1.6 GB | Apache 2.0 |
| Ollama | qwen3:0.6b | 49.8 | 39.0 to 60.7 | same | 1.6 s | 2.9 s | 0/25 | 0.52 GB | Apache 2.0 |
| Ollama | llama3.2:1b | 46.8 | 40.9 to 52.8 | same | 6.8 s | 11.3 s | 14/25 | 1.3 GB | Llama 3.2 |
| Foundry Local | qwen3-1.7b | 46.5 | 39.7 to 53.3 | 46.5 | 9.2 s | 14.2 s | 2/25 | 1.3 GB | Apache 2.0 |
| Ollama | gemma3:270m | 46.4 | 38.4 to 53.8 | same | 1.1 s | 2.2 s | 19/25 | 0.29 GB | Gemma terms |
| LM Studio | qwen3-0.6b | 45.7 | 34.9 to 56.6 | same | 1.2 s | 1.7 s | 0/25 | 0.45 GB | Apache 2.0 |
| Foundry Local | qwen3-0.6b | 41.4 | 34.2 to 47.9 | 46.9 | 10.8 s | 14.3 s | 11/25 | 0.6 GB | Apache 2.0 |
| Foundry Local | qwen2.5-0.5b | 38.2 | 32.9 to 43.6 | 38.2 | 2.4 s | 3.2 s | 1/25 | 0.8 GB | Apache 2.0 |
| Ollama | LFM2.5-1.2B-Instruct | 20.8 | 14.9 to 27.8 | same | 1.6 s | 2.4 s | 0/25 | 0.73 GB | LFM Open |
