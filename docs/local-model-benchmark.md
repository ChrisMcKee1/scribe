# Local AI cleanup benchmark

Updated October 21, 2026, for Scribe 0.5.11. How well and how fast open models clean up dictation
on this PC across the four local runtimes Scribe supports: **Ollama**, **TensorSharp**, **LM Studio**
and **Foundry Local**.

> **Hardware:** NVIDIA GeForce RTX 5080 (16 GB), AMD Ryzen 9 9900X (12 cores), 95 GB of memory,
> Windows 11 build 26200. Times are this PC's; the ranking and the quality scores are what carries
> over.

## Current Local Leaderboard: Gemma 4 26B-A4B and TensorSharp vs. Ollama vs. LM Studio

Measured across the frozen 25 dictations (2 to 3 runs per case, 50 to 75 timed rewrites per model)
through Scribe's production cleanup service, graded blind against the retained `gpt-5.6-terra` cloud
anchor. **An open model now matches the cloud anchor on a 16 GB card:** `gemma4:26b-a4b-iq3s` in
Ollama scored 94.1 against `gpt-5.6-terra`'s 93.8, in 462 ms against 2,088 ms.

| Model | Runtime and configuration on 16 GB RTX 5080 | Quality | 95% interval | Vocab accuracy | Typical (P50) | P95 | Fallbacks |
|---|---|---:|---|---:|---:|---:|---:|
| **`gemma4:26b-a4b-iq3s`** | **Ollama 0.40.7 (`llama.cpp` CUDA + MTP draft head, 32K context, mentioned vocab)** | **94.1** | **91.3 to 96.5** | **100.0%** | **462 ms** (363 ms idle desktop) | **1,041 ms** | **0/50** |
| `Gemma 4 26B-A4B` (`UD-IQ3_S`) | LM Studio 0.4.25 (`llama.cpp` CUDA, 16K context) | 94.0 | 91.1 to 96.4 | 100.0% | 3,758 ms | 9,946 ms | 0/50 |
| `gpt-5.6-terra` (Cloud anchor) | Microsoft Foundry (South Central US, DataZoneStandard) | 93.8 | 91.1 to 96.0 | 100.0% | 2,088 ms | 3,703 ms | 0/25 |
| **`Gemma 4 26B-A4B` (`UD-IQ3_S`)** | **TensorSharp v2026.10.11 (`ggml_cuda`, 16K context, user-turn glossary)** | **93.7** | **91.1 to 96.2** | **100.0%** | **1,094 ms** | **1,560 ms** | **0/50** |
| `gemma4:26b-a4b-iq3s` | Ollama 0.40.7 (no MTP draft head, 4K context) | 93.5 | 90.5 to 96.1 | 100.0% | 502 ms | 886 ms | 0/50 |
| **`gemma4:26b-a4b-iq3s`** | **Ollama 0.40.7 (32K context, whole vocabulary: all 11 word packs, 1,332 terms)** | **92.7** | **89.5 to 95.7** | **100.0%** | **519 ms** | **760 ms** | **0/50** |
| **`gemma4:12b`** | **Ollama (`Q4_K_M` + MTP draft head)** | **91.9** | **88.5 to 94.9** | **96.1%** | **431 ms** | **857 ms** | **0/75** |
| **`Qwen3.8-27B` (`UD-IQ3_S`)** | **TensorSharp v2026.10.11 (`ggml_cuda`, 8K context, 100% GPU-resident)** | **90.0** | **87.9 to 94.0** | **94.1%** | **2,764 ms** | **4,307 ms** | **0/50** |
| `Qwen3.8-27B` (`UD-IQ3_S`) | LM Studio 0.4.25 (tuned `--gpu 0.78`, 16K context) | 90.8 | 87.5 to 93.8 | 92.2% | 5,319 ms | 7,450 ms | 0/50 |
| **`gemma4:e4b`** | **Ollama (`Q4_K_M`, mentioned vocab)** | **89.2** | **85.2 to 92.7** | **97.0%** | **499 ms** | **790 ms** | **0/75** |
| **`Bonsai 2 27B` (`PQ2_0` ternary)** | **TensorSharp v2026.10.11 (`ggml_cuda`, 16K context)** | **87.9** | **82.8 to 92.4** | **94.1%** | **2,180 ms** | **3,632 ms** | **0/50** |
| **`gemma4:e2b`** | **Ollama (`Q4_K_M`, mentioned vocab)** | **83.5** | **77.7 to 88.7** | **83.0%** | **327 ms** | **532 ms** | **0/75** |
| `nemotron-3-nano:4b` | Ollama 0.40.7 (`Q4_K_M`, 32K context) | 71.8 | 64.5 to 78.8 | 67.0% | 321 ms | 544 ms | 0/50 |

### How TensorSharp compares against Ollama and LM Studio

1. **Where TensorSharp (`ggml_cuda`) wins on a 16 GB card:**
   - **3.4x faster than LM Studio on `Gemma 4 26B-A4B`** (1,094 ms vs. 3,758 ms) and **1.9x faster on `Qwen3.8-27B`** (2,764 ms vs. 5,319 ms).
   - **Avoids the 16 GB WDDM VRAM cliff on dense 27B models (`Qwen3.8-27B`):** Ollama pre-allocates the full KV context buffer upfront in VRAM, pushing an 11.21 GiB 27B model over Windows 11's ~12.3 GiB free VRAM budget. TensorSharp memory-maps the GGUF and allocates only 2,048 initial KV tokens on demand (`TS_KV_INITIAL_TOKENS=2048`), keeping `Qwen3.8-27B` on the GPU.
   - **Persists Radix prefix checkpoints to disk (`prefix-cache/*.ckpt`):** across server restarts, TensorSharp reloads the prefilled system-prompt KV state from disk in milliseconds.
   - **Runs new quantizations (`Bonsai 2 27B PQ2_0` ternary)** that Ollama 0.40.x rejects with HTTP 412.
2. **Where Ollama (`363 to 462 ms`) beats TensorSharp (`1,094 ms`) on `Gemma 4 26B-A4B` (`gemma4:26b-a4b-iq3s`):**
   - In Ollama, `gemma4:26b-a4b-iq3s` pairs the 25.2B MoE model (3.8B active parameters per token) with its 425 MB 3-token MTP speculative draft head (`gemma4-assistant`) and reuses token-level LCP prefixes in place.
   - On a 16 GB desktop GPU where Windows apps hold 2 to 3.7 GB of VRAM, keep `gemma4:26b-a4b-iq3s` at **32K context (`32,768` tokens)** and set `LLAMA_ARG_N_CPU_MOE=4` so the KV cache, compute buffers and draft model stay in dedicated VRAM (at 128K context, the 3.4 GB KV cache spills into Windows WDDM shared memory over PCIe and drops generation to 11 tokens/s).
3. **Scribe's built-in TensorSharp optimizations:**
   - Sends `tool_choice: "none"`, `skills_discovery: false` and `multi_agent: false` to suppress ~1,800 hidden tool and coordination tokens per request (cutting first-run latency by >50%).
   - Keeps the `system` message static (`UsesUserTurnGlossary`) and places per-dictation mentioned vocabulary in the `user` turn before `<transcript>`, achieving 100% Radix prefix-cache hits on every dictation, including recurrent GatedDeltaNet models (`Qwen 3.8`, `Bonsai 2`).
   - Sends `think: false` rather than `reasoning_effort: "none"` (which TensorSharp rejects with HTTP 400).

## What to use

| If you want | Use | Quality | Typical, GPU | Typical, CPU only | Download | GPU memory |
|---|---|---:|---:|---:|---:|---:|
| **Cloud-level quality on a 16 GB GPU** | **Gemma 4 26B-A4B**: `gemma4:26b-a4b-iq3s` in Ollama (32K context) | **94.1** | **0.46 s** | n/a | 11.8 GB | 11.9 GB |
| **The same model without Ollama** | **Gemma 4 26B-A4B** (`UD-IQ3_S`) in TensorSharp (16K context) | 93.7 | 1.1 s | n/a | 11.3 GB | n/a |
| **Best dense model on a 16 GB GPU** | **Gemma 4 12B**: `gemma4:12b` in Ollama (32K context) | **91.9** | **0.43 s** | n/a | 8.0 GB | 8.5 GB |
| **The best balance** | **Gemma 4 E2B**: `gemma4:e2b` in Ollama, `google/gemma-4-e2b` in LM Studio | 83.5 | 0.33 s | 2.4 s (LM Studio), 3.3 s (Ollama) | 7.2 GB (Ollama), 4.1 GB (LM Studio) | 1.6 GB |
| The best quality under 4 GB VRAM | Gemma 4 E4B: `gemma4:e4b`, or `gemma4:e4b-it-qat` for a 6.1 GB download | 89.2 | 0.50 s | 4.6 s | 9.6 GB | 3.0 GB |
| The smallest download that does the job | IBM Granite 4 3B: `granite4:3b` | 80.7 | 0.33 s | 3.9 s | 2.1 GB | 2.3 GB |
| The best writing on the CPU alone | Qwen3 4B Instruct 2507: `qwen3:4b-instruct` | 81.7 | 0.41 s | 5.0 s (83.1) | 2.5 GB | 3.0 GB |
| Nothing else to install | Foundry Local: **Qwen2.5 1.5B** (the default), or **Qwen2.5 7B** with an NVIDIA RTX card of 8 GB or more | 74.0 (GPU), 66.1 (CPU); 7B: 85.4 | 0.37 s, NVIDIA RTX only; 7B: 0.74 s | 6.7 s | 1.3 GB (GPU), 1.8 GB (CPU); 7B: 4.7 GB | n/a |

- In Settings, choose **On this PC**, then **Ollama**, **LM Studio** or **TensorSharp**: Scribe lists the
  models you downloaded in it and uses the app's own address. Leave **Instructions** on Automatic, which
  gives a model on this PC the short instructions it can follow.
- **Defaults follow the hardware, and Settings recommends no model.** With nothing chosen, Scribe preselects
  the installed model that ranks highest here: on a graphics card with 12 GB or more of its own memory,
  Gemma 4 26B-A4B, then 12B, E4B and E2B; with 6 GB or more, E4B, then E2B, 26B-A4B and 12B; otherwise E2B
  first. A model you choose or save stays chosen.
- Quality is a blind judge's score out of 100 on 25 deliberately hard dictations, not a percentage
  of correct dictations. Differences under about 5 points are within the judge's noise.
- Typical is the median time from sending the words to getting the cleaned text back, with the model
  loaded. GPU memory is what `ollama ps` reported with the model loaded.
- **A chain of self-corrections is the weak spot of the small picks.** On the self-correction dictation, a
  chain such as "Friday, no, Thursday, actually make it Tuesday", Gemma 4 E2B scored 38.8, against 77.0 for
  E4B and 84.8 for Granite 4 3B. If you correct yourself mid-sentence a lot, take E4B or Granite 4 3B.
- **Avoid** `qwen3:4b` in Ollama (the thinking model: its reasoning arrives as text and Scribe rejected
  all 75 answers); LFM2.5 1.2B Instruct, which copied the worked example in its instructions into the
  text; `deepseek-r1`, which keeps thinking when asked not to (75.3 in 5.6 s, and 10 of 75 rewrites ran
  out of their output allowance); and `VicRodger27/Writex:4b`, about as fast as Gemma 4 12B (418 ms) but
  77.7, because fluent rewriting is not the same as keeping the speaker's meaning (71.0 for fidelity
  against 90.9 for Gemma 4 12B). Models under 1B parameters are the fastest (about 0.2 s on the GPU)
  but scored 44 to 66.

## Tuning and setup guide

### Context size: 32K is the sweet spot on a 16 GB card

- Scribe fits every request to the context the model actually loaded, the dictation first, so a larger
  context only adds room for vocabulary and costs memory. Ollama's own default is 4,096 tokens below 24 GB
  of graphics memory, 32,768 from 24 GB and 262,144 from 48 GB. Choose a **Context size** under
  **Ollama settings** in AI cleanup, or set **Context length** in Ollama's own settings
  (`OLLAMA_CONTEXT_LENGTH`). Scribe never asks for more than the model's own limit, and a
  `PARAMETER num_ctx` in the model's Modelfile counts as that limit.
- **`gemma4:26b-a4b-iq3s`: use 32K (`32,768` tokens) and `LLAMA_ARG_N_CPU_MOE=4`.** The main weights
  (10.75 GiB) and the MTP draft head (425 MiB) take 11.2 GiB before any KV cache or desktop apps, which
  commonly hold 2 to 3.7 GB of VRAM on Windows 11. A 128K request (`131,072`) allocates 3.4 GiB of KV cache,
  so the compute buffers and draft head spill into Windows WDDM shared memory over PCIe and generation
  falls from about 200 tokens a second (462 ms) to 11 tokens a second (5.7 s). Set
  `LLAMA_ARG_N_CPU_MOE=4` as an environment variable for Ollama and restart it: 4 mixture-of-experts layers
  stay in system memory while all 30 attention layers, the 32K KV cache, the compute buffers and the draft
  model stay in dedicated VRAM, and over 28,000 tokens of vocabulary fit with no WDDM paging.
- `gemma4:12b` was measured at 32K too. The TensorSharp rows ran at 16K, except `Qwen3.8-27B` at 8K, and
  the LM Studio rows at 16K. Under **TensorSharp settings**, **Context size** takes `TensorSharp's setting`
  or 4K to 128K tokens, fitted to the smaller of your choice and the window TensorSharp loaded the model with.

### Vocabulary: mentioned terms or the whole list

| Gemma 4 26B-A4B in Ollama (32K) | Quality | Vocab accuracy | Typical (P50) | P95 |
|---|---:|---:|---:|---:|
| The terms each dictation mentions (default) | **94.1** | 100.0% | **462 ms** | 1,041 ms |
| **Send your whole vocabulary when it fits** (all 11 word packs, 1,332 terms) | 92.7 | 100.0% | 519 ms | 760 ms |

- **Leave Send your whole vocabulary when it fits off,** as it is by default. It cost the 26B-A4B 1.4
  points (inside the judge's noise) and 57 ms, and it hurts smaller models: a request carries about 13,700
  input tokens with the whole list against about 1,500 with the terms a dictation mentions, and a small
  model loses the few terms it needs among 1,300 lines. Gemma 4 E2B got 53% of word pack terms right with
  the whole list against 80% with the mentioned ones, and Gemma 4 E4B scored the same either way (87.0 with
  the mentioned terms, 87.7 with the whole list; evidence `benchmarks/context-window-2026-09-30.json`).
- **Mentioned terms** are the ones whose spoken or written words the dictation appears to use, heard
  exactly or slightly differently. The request stays small, and vocabulary accuracy stayed at 100% on the
  26B-A4B.
- On TensorSharp the mentioned terms go in the user turn, so the system message is identical from one
  dictation to the next and its Radix prefix cache hits every time. With the whole list on, it stays in the
  system message.

### Memory retention: Never

- **Free memory when Scribe isn't used** (AI cleanup, **On this PC**, and **Advanced**) is one shared
  choice for speech and AI models: 5 to 60 minutes in 5-minute steps (10 by default), or **Never**.
- With **Never**, each request asks Ollama to keep its model loaded indefinitely, LM Studio keeps following
  its own idle policy, and TensorSharp keeps its hosted model in memory while its server runs. **Free
  memory**, pausing dictation and switching AI cleanup off still free it, and another app or an Ollama
  restart can too.
- When Ollama confirms the model is already loaded at the context size Scribe asks for, with its full
  prompt cached, a recording sends no readying request even after 30 seconds of idle, so the first dictation
  after a break needs no extra wait. With a time set instead, the first recording after the model was freed
  waits up to 30 seconds for it to start, and the recording indicator says **Starting local model**.
- The cost is graphics memory: the 26B-A4B holds 11.9 GB until it is freed.

### Runtime tips

- **Ollama** (0.34.4 to 0.40.7 were measured). Keep **Automatic** instructions, use a 32K **Context size**
  for `gemma4:26b-a4b-iq3s` and `gemma4:12b`, and set `LLAMA_ARG_N_CPU_MOE=4` for the 26B-A4B on a 16 GB card
  (see above). After a model loads, `ollama ps` shows whether it runs on the GPU, the CPU or both; Scribe
  doesn't force either.
- **TensorSharp** (v2026.10.11 at `http://localhost:5000/v1`, measured on `ggml_cuda`). Choose
  **TensorSharp** under **On this PC**. Scribe turns off TensorSharp's built-in tool, skill and multi-agent
  blocks, sends `think: false` (unless you choose a thinking level under **TensorSharp settings**) and puts
  the mentioned vocabulary in the user turn, so the persistent Radix prefix cache (`prefix-cache/*.ckpt`)
  hits on every dictation. **How TensorSharp runs the model** offers GGML CUDA, Direct CUDA, GGML Vulkan,
  GGML CPU and Pure C# CPU. For a dense 27B model on a 16 GB card, keep its initial KV allocation small
  (`TS_KV_INITIAL_TOKENS=2048`) and the context at 8K to 16K, as measured.
- **LM Studio** (0.4.25 with the CUDA 12 llama.cpp runtime 2.47.0). The rows ran at 16K, and
  `Qwen3.8-27B` with a tuned `--gpu 0.78`. Its lmstudio-community `phi-4-mini-instruct` build scored 49.6
  against 78.7 for Ollama's `phi4-mini`; use Ollama for Phi-4 Mini. Speculative decoding does not help
  dictation: LM Studio takes a draft model only when the model loads (a request that names one is
  refused), and loaded with Qwen3 0.6B as its draft, Qwen3 4B 2507 took 0.74 s typically against 0.41 s
  without, because a cleanup writes only about 60 tokens.
- **Foundry Local 2.1.0** is the only runtime that needs nothing else installed, and its SDK chooses the
  hardware itself.
  - **Defaults:** Qwen2.5 1.5B on any PC (0.37 s on an NVIDIA RTX card through its TensorRT-RTX build, 6.7 s
    on the processor), and Qwen2.5 7B for a first setup on an NVIDIA RTX card of 8 GB or more (85.4 in
    0.74 s), still the best model Foundry Local offers for this job. TensorRT-RTX is the better build of
    both (Qwen2.5 7B 85.4 against 84.0 on CUDA, Qwen2.5 1.5B 74.0 against 67.0). The gap to Ollama and
    LM Studio is the catalog: it has no Gemma 4 E4B and no Qwen3 4B Instruct 2507.
  - **CUDA builds:** Phi-4 Mini (0.79 s) and Qwen3 4B (0.72 s) run on an NVIDIA graphics card, where their
    processor builds took about 16 s.
  - **Thinking off:** Scribe sends `reasoning_effort: "none"`. Without it Qwen3.5 4B spent all 2,048 of its
    output tokens thinking (33 s) on a one-sentence edit; with it, 0.58 s.
  - **RTX 50 series cards need cuDNN attention off.** ONNX Runtime prefers cuDNN's attention kernel on
    them, and with it Qwen3 4B's CUDA build failed every request and Gemma 4 E2B's first request took 16 s
    (1.3 s without). Scribe sets `ORT_ENABLE_CUDNN_FLASH_ATTENTION=0` before Foundry Local starts, as ONNX
    Runtime documents, unless you set it yourself.
  - **A graphics card build that fails its first request** moves to its CPU build for the rest of the
    session and the failed build is unloaded; the next start tries the card again. The first request after
    a load is slow (4.4 s for Qwen2.5 1.5B on TensorRT-RTX), and setup absorbs it.
  - **Still broken upstream:** every build of Qwen3.5 2B ("Invalid rank for input: position_ids"), SmolLM3
    3B's chat template ("Unknown method: replace") and Gemma 4 E2B on the processor (no answer within
    180 s). Gemma 4 E2B on CUDA is erratic: typically 1.4 s, but 6 to 7 s on about one request in five.
- **NVIDIA Nemotron-3:** `nemotron-3-super:120b-a12b-bf16` (120B total, 12B active) is 247 GB (87 GB in
  Q4_K_M), and `nemotron-3-nano:30b-a3b` is 18 to 24 GB, so neither fits a 16 GB card without heavy CPU
  offloading. The closest variant that fits in VRAM is `nemotron-3-nano:4b` (2.8 GB), which answered in
  321 ms at the median with 0 fallbacks but scored 71.8, well behind the Gemma 4 family on fidelity.

## Full GPU results (RTX 5080, 16 GB)

Each model cleaned the same 25 dictations three times on the GPU (75 answers per model; 50 for the October
additions and 66 for Foundry Local, below). **Fell back** counts answers Scribe rejected (a ramble, a
refusal, an empty answer) and replaced with the raw text, which is what the user would have received. Rows
are sorted by quality, and **bold** rows are the picks in [What to use](#what-to-use).

| Runtime | Model | Quality | 95% interval | Typical | Slowest 1 in 20 | Fell back | Download | License |
|---|---|---:|---|---:|---:|---:|---:|---|
| Ollama | **gemma4:26b-a4b-iq3s (32K, MTP)** | **94.1** | 91.3 to 96.5 | **462 ms** (363 ms idle desktop) | 1.0 s | 0/50 | 11.8 GB | Apache 2.0 |
| LM Studio | Gemma 4 26B-A4B (`UD-IQ3_S`, 16K) | 94.0 | 91.1 to 96.4 | 3.8 s | 9.9 s | 0/50 | 11.3 GB | Apache 2.0 |
| TensorSharp | **Gemma 4 26B-A4B (`UD-IQ3_S`, 16K)** | **93.7** | 91.1 to 96.2 | **1.1 s** | 1.6 s | 0/50 | 11.3 GB | Apache 2.0 |
| Ollama | gemma4:26b-a4b-iq3s (4K, no MTP) | 93.5 | 90.5 to 96.1 | 502 ms | 886 ms | 0/50 | 11.8 GB | Apache 2.0 |
| Ollama | gemma4:26b-a4b-iq3s (32K, whole vocab) | 92.7 | 89.5 to 95.7 | 519 ms | 760 ms | 0/50 | 11.8 GB | Apache 2.0 |
| Ollama | **gemma4:12b (32K)** | **91.9** | 88.5 to 94.9 | **431 ms** | 857 ms | 0/75 | 8.0 GB | Apache 2.0 |
| LM Studio | Qwen3.8-27B (`UD-IQ3_S`, `--gpu 0.78`) | 90.8 | 87.5 to 93.8 | 5.3 s | 7.5 s | 0/50 | 11.2 GB | Apache 2.0 |
| TensorSharp | Gemma 4 12B (`Q4_K_M`, 16K) | 90.0 | 86.1 to 93.5 | 1.6 s | 2.4 s | 0/50 | 8.0 GB | Apache 2.0 |
| TensorSharp | Qwen3.8-27B (`UD-IQ3_S`, 8K) | 90.0 | 87.9 to 94.0 | 2.8 s | 4.3 s | 0/50 | 11.2 GB | Apache 2.0 |
| Ollama | **gemma4:e4b** | **89.2** | 85.2 to 92.7 | **499 ms** | 790 ms | 0/75 | 9.6 GB | Apache 2.0 |
| TensorSharp | Bonsai 2 27B (`PQ2_0`, 16K) | 87.9 | 82.8 to 92.4 | 2.2 s | 3.6 s | 0/50 | 7.7 GB | Apache 2.0 |
| Ollama | gemma4:e4b-it-qat | 87.3 | 83.4 to 91.1 | 435 ms | 729 ms | 0/75 | 6.1 GB | Apache 2.0 |
| Ollama | qwen3.5:9b | 86.4 | 81.9 to 90.4 | 695 ms | 1.2 s | 0/75 | 6.6 GB | Apache 2.0 |
| Ollama | phi4:14b | 86.1 | 82.8 to 89.3 | 819 ms | 1.6 s | 0/75 | 9.1 GB | MIT |
| Foundry Local | **qwen2.5-7b (TensorRT-RTX)** | **85.4** | 80.6 to 89.6 | **740 ms** | 1.1 s | 0/66 | 4.7 GB | Apache 2.0 |
| Foundry Local | qwen2.5-7b (CUDA, chosen by id) | 84.0 | 79.3 to 88.2 | 729 ms | 1.5 s | 0/66 | 4.7 GB | Apache 2.0 |
| Ollama | **gemma4:e2b** | **83.5** | 77.7 to 88.7 | **327 ms** | 532 ms | 0/75 | 7.2 GB | Apache 2.0 |
| LM Studio | **google/gemma-4-e2b** | **83.0** | 77.5 to 87.9 | **327 ms** | 597 ms | 0/75 | 4.1 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-4b-2507 | 82.9 | 78.1 to 87.5 | 370 ms | 707 ms | 0/75 | 2.3 GB | Apache 2.0 |
| LM Studio | google/gemma-3-4b | 82.9 | 77.7 to 87.8 | 446 ms | 833 ms | 0/75 | 3.1 GB | Gemma terms |
| Ollama | gemma3:4b | 82.2 | 76.3 to 87.3 | 488 ms | 779 ms | 0/75 | 3.3 GB | Gemma terms |
| Foundry Local | qwen3-8b (CUDA) | 82.0 | 76.1 to 87.4 | 1.0 s | 1.9 s | 2/66 | 5.7 GB | Apache 2.0 |
| Foundry Local | gemma-4-e2b (CUDA) | 81.9 | 76.5 to 87.0 | 1.4 s | 7.5 s | 0/66 | 5.9 GB | Apache 2.0 |
| Ollama | qwen3.5:4b | 81.8 | 77.1 to 86.3 | 480 ms | 757 ms | 0/75 | 3.4 GB | Apache 2.0 |
| Ollama | **qwen3:4b-instruct** | **81.7** | 75.8 to 87.4 | **409 ms** | 689 ms | 0/75 | 2.5 GB | Apache 2.0 |
| Foundry Local | qwen3.5-4b (CUDA) | 81.7 | 76.3 to 86.9 | 1.3 s | 2.2 s | 0/66 | 4.2 GB | Apache 2.0 |
| Ollama | **granite4:3b** | **80.7** | 75.1 to 85.8 | **326 ms** | 587 ms | 0/75 | 2.1 GB | Apache 2.0 |
| Foundry Local | phi-4-mini (CUDA) | 80.3 | 74.3 to 85.7 | 787 ms | 1.4 s | 0/66 | 3.7 GB | MIT |
| LM Studio | ibm/granite-4-micro | 79.9 | 75.1 to 84.5 | 348 ms | 542 ms | 0/75 | 2.0 GB | Apache 2.0 |
| Ollama | gemma4:e2b-it-qat | 79.4 | 72.8 to 85.3 | 284 ms | 464 ms | 0/75 | 4.3 GB | Apache 2.0 |
| Foundry Local | qwen3-4b (CUDA) | 79.3 | 72.8 to 85.2 | 721 ms | 1.4 s | 0/66 | 2.7 GB | Apache 2.0 |
| Ollama | phi4-mini:3.8b | 78.7 | 71.5 to 85.2 | 346 ms | 612 ms | 0/75 | 2.5 GB | MIT |
| Ollama | granite4:micro-h | 78.4 | 72.5 to 83.9 | 396 ms | 589 ms | 0/75 | 1.9 GB | Apache 2.0 |
| Ollama | ministral-3:8b | 76.5 | 72.1 to 80.5 | 545 ms | 1.0 s | 0/75 | 6.0 GB | Apache 2.0 |
| Ollama | SmolLM3-3B | 75.5 | 69.8 to 81.1 | 331 ms | 535 ms | 0/75 | 1.9 GB | Apache 2.0 |
| Foundry Local | **qwen2.5-1.5b (TensorRT-RTX)** | **74.0** | 67.6 to 80.2 | **374 ms** | 637 ms | 2/66 | 1.3 GB | Apache 2.0 |
| Ollama | granite4:1b | 73.4 | 67.1 to 79.4 | 388 ms | 703 ms | 0/75 | 3.3 GB | Apache 2.0 |
| Ollama | qwen3.5:2b | 72.2 | 64.1 to 79.7 | 390 ms | 593 ms | 0/75 | 2.7 GB | Apache 2.0 |
| Ollama | llama3.2:3b | 71.8 | 65.9 to 78.0 | 321 ms | 481 ms | 0/75 | 2.0 GB | Llama 3.2 |
| Ollama | nemotron-3-nano:4b | 71.8 | 64.5 to 78.8 | 321 ms | 544 ms | 0/50 | 2.8 GB | NVIDIA Open |
| Ollama | LFM2-2.6B | 71.1 | 63.5 to 78.4 | 260 ms | 417 ms | 0/75 | 1.6 GB | LFM Open |
| LM Studio | qwen/qwen3.5-2b | 69.0 | 60.4 to 77.3 | 274 ms | 422 ms | 0/75 | 1.8 GB | Apache 2.0 |
| LM Studio | meta/llama-3.2-3b | 68.7 | 64.1 to 73.6 | 334 ms | 542 ms | 2/75 | 1.9 GB | Llama 3.2 |
| Foundry Local | ministral-3-3b (CUDA) | 67.8 | 62.3 to 73.0 | 590 ms | 1.1 s | 0/66 | 3.7 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-1.7b | 67.3 | 58.9 to 75.4 | 220 ms | 432 ms | 0/75 | 1.3 GB | Apache 2.0 |
| Ollama | qwen3:1.7b | 67.1 | 60.2 to 74.1 | 243 ms | 424 ms | 0/75 | 1.4 GB | Apache 2.0 |
| Foundry Local | qwen2.5-1.5b (CUDA, chosen by id) | 67.0 | 60.6 to 73.2 | 531 ms | 915 ms | 0/66 | 1.3 GB | Apache 2.0 |
| LM Studio | qwen/qwen3.5-0.8b | 65.5 | 57.9 to 72.9 | 231 ms | 399 ms | 0/75 | 0.95 GB | Apache 2.0 |
| Ollama | qwen2.5:1.5b | 65.3 | 58.7 to 71.8 | 185 ms | 344 ms | 1/75 | 0.99 GB | Apache 2.0 |
| Ollama | ministral-3:3b | 64.8 | 59.2 to 69.8 | 343 ms | 619 ms | 0/75 | 3.0 GB | Apache 2.0 |
| Ollama | qwen3.5:0.8b | 63.5 | 56.1 to 70.6 | 253 ms | 407 ms | 0/75 | 1.0 GB | Apache 2.0 |
| Ollama | qwen2.5:0.5b | 63.2 | 55.9 to 70.5 | 162 ms | 241 ms | 0/75 | 0.40 GB | Apache 2.0 |
| Ollama | granite4:1b-h | 60.8 | 51.0 to 70.2 | 342 ms | 581 ms | 0/75 | 1.6 GB | Apache 2.0 |
| Foundry Local | olmo-3-7b (CUDA) | 60.7 | 52.6 to 68.1 | 1.1 s | 2.4 s | 1/66 | 5.2 GB | Apache 2.0 |
| Ollama | gemma3:1b | 59.1 | 51.9 to 66.0 | 246 ms | 676 ms | 5/75 | 0.82 GB | Gemma terms |
| Foundry Local | qwen3-1.7b (CUDA) | 59.1 | 53.1 to 65.5 | 704 ms | 1.7 s | 3/66 | 1.3 GB | Apache 2.0 |
| Ollama | LFM2-1.2B | 57.4 | 50.0 to 64.8 | 185 ms | 258 ms | 0/75 | 0.73 GB | LFM Open |
| Ollama | qwen3:0.6b | 57.0 | 48.2 to 65.4 | 179 ms | 291 ms | 1/75 | 0.52 GB | Apache 2.0 |
| Ollama | granite4:350m-h | 56.7 | 47.0 to 65.9 | 208 ms | 300 ms | 0/75 | 0.37 GB | Apache 2.0 |
| Ollama | LFM2-700M | 55.9 | 50.2 to 61.5 | 142 ms | 385 ms | 6/75 | 0.47 GB | LFM Open |
| Ollama | smollm2:360m | 54.4 | 50.1 to 58.9 | 196 ms | 613 ms | 8/75 | 0.73 GB | Apache 2.0 |
| Ollama | qwen3:4b | 54.4 | 50.5 to 58.3 | 1.4 s | 2.4 s | 75/75 | 2.5 GB | Apache 2.0 |
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

- Labels in parentheses give the context size, `MTP` for the draft head `gemma4:26b-a4b-iq3s` ships with
  (`no MTP` where it was left off) and `whole vocab` for the whole-vocabulary arm. For Foundry Local they
  name the build: the one its SDK picked, or the one chosen by its id.
- Foundry Local rows are Foundry Local 2.1.0, graded on 33 dictations (the 25 plus 8 structure cases, two
  runs) with the lists rubric, which scored Ollama's Gemma 4 E4B and E2B about a point higher than the
  standard rubric did (90.1 and 84.6 against 89.2 and 83.5). Foundry Local 2.1.0 can't run Qwen3.5 2B or
  SmolLM3 3B on this PC, so they have no Foundry Local row.

## Full CPU-only results (Ryzen 9 9900X, 12 cores)

One run of each dictation on the CPU (25 answers; 33 for Foundry Local 2.1.0). The Ollama variants ran with
`num_gpu 0`, LM Studio with `lms load --gpu off`, and Foundry Local on its CPU builds. Rows are sorted by
quality, and **bold** rows are the picks in [What to use](#what-to-use).

| Runtime | Model | Quality | 95% interval | Typical | Slowest 1 in 20 | Fell back | Download | License |
|---|---|---:|---|---:|---:|---:|---:|---|
| Ollama | **gemma4:e4b** | **88.9** | 85.8 to 91.9 | **4.6 s** | 6.8 s | 0/25 | 9.6 GB | Apache 2.0 |
| Ollama | gemma4:e4b-it-qat | 87.6 | 83.7 to 91.3 | 5.1 s | 8.8 s | 0/25 | 6.1 GB | Apache 2.0 |
| Ollama | **qwen3:4b-instruct** | **83.1** | 77.9 to 88.2 | **5.0 s** | 7.9 s | 0/25 | 2.5 GB | Apache 2.0 |
| Ollama | **gemma4:e2b** | **83.0** | 77.4 to 88.1 | **3.3 s** | 5.9 s | 0/25 | 7.2 GB | Apache 2.0 |
| LM Studio | qwen/qwen3-4b-2507 | 82.3 | 76.8 to 87.4 | 4.5 s | 10.6 s | 0/25 | 2.3 GB | Apache 2.0 |
| LM Studio | google/gemma-3-4b | 82.0 | 76.1 to 87.3 | 3.8 s | 7.1 s | 0/25 | 3.1 GB | Gemma terms |
| LM Studio | **google/gemma-4-e2b** | **81.7** | 75.6 to 87.1 | **2.4 s** | 4.3 s | 0/25 | 4.1 GB | Apache 2.0 |
| Ollama | **granite4:3b** | **79.8** | 74.2 to 85.2 | **3.9 s** | 6.1 s | 0/25 | 2.1 GB | Apache 2.0 |
| Ollama | phi4-mini:3.8b | 79.7 | 72.0 to 86.4 | 3.7 s | 6.6 s | 0/25 | 2.5 GB | MIT |
| Ollama | gemma3:4b | 79.5 | 72.8 to 85.7 | 3.3 s | 6.4 s | 0/25 | 3.3 GB | Gemma terms |
| LM Studio | ibm/granite-4-micro | 79.4 | 74.2 to 84.3 | 3.2 s | 5.8 s | 0/25 | 2.0 GB | Apache 2.0 |
| Ollama | gemma4:e2b-it-qat | 79.1 | 72.2 to 85.7 | 2.2 s | 3.5 s | 0/25 | 4.3 GB | Apache 2.0 |
| Ollama | granite4:micro-h | 74.4 | 65.9 to 81.6 | 4.3 s | 6.8 s | 0/25 | 1.9 GB | Apache 2.0 |
| Ollama | qwen3.5:2b | 72.7 | 65.0 to 79.9 | 4.5 s | 6.7 s | 0/25 | 2.7 GB | Apache 2.0 |
| Ollama | llama3.2:3b | 72.4 | 66.6 to 78.3 | 3.2 s | 6.5 s | 0/25 | 2.0 GB | Llama 3.2 |
| Ollama | granite4:1b | 71.7 | 64.9 to 78.3 | 5.4 s | 9.1 s | 0/25 | 3.3 GB | Apache 2.0 |
| Foundry Local | qwen3.5-0.8b | 70.2 | 64.0 to 76.1 | 4.5 s | 8.6 s | 0/33 | 1.0 GB | Apache 2.0 |
| Ollama | qwen3:1.7b | 68.4 | 61.4 to 75.4 | 2.8 s | 4.9 s | 0/25 | 1.4 GB | Apache 2.0 |
| LM Studio | qwen/qwen3.5-2b | 68.2 | 60.2 to 75.8 | 2.2 s | 3.3 s | 0/25 | 1.8 GB | Apache 2.0 |
| Ollama | qwen2.5:1.5b | 66.9 | 60.0 to 73.5 | 2.5 s | 5.8 s | 0/25 | 0.99 GB | Apache 2.0 |
| Foundry Local | **qwen2.5-1.5b** | **66.1** | 59.8 to 72.5 | **6.7 s** | 10.7 s | 0/33 | 1.8 GB | Apache 2.0 |
| Ollama | qwen2.5:0.5b | 65.1 | 57.5 to 72.7 | 783 ms | 1.2 s | 0/25 | 0.40 GB | Apache 2.0 |
| Ollama | qwen3.5:0.8b | 63.1 | 54.9 to 71.0 | 2.5 s | 3.2 s | 0/25 | 1.0 GB | Apache 2.0 |
| Ollama | gemma3:1b | 61.3 | 55.0 to 67.9 | 1.8 s | 4.8 s | 1/25 | 0.82 GB | Gemma terms |
| Ollama | granite4:1b-h | 60.1 | 49.4 to 70.5 | 3.4 s | 4.9 s | 0/25 | 1.6 GB | Apache 2.0 |
| Foundry Local | qwen3-1.7b | 59.5 | 52.3 to 67.0 | 12.6 s | 24.9 s | 0/33 | 1.3 GB | Apache 2.0 |
| Ollama | qwen3:0.6b | 49.8 | 39.0 to 60.7 | 1.6 s | 2.9 s | 0/25 | 0.52 GB | Apache 2.0 |
| Ollama | llama3.2:1b | 46.8 | 40.9 to 52.8 | 6.8 s | 11.3 s | 14/25 | 1.3 GB | Llama 3.2 |
| Ollama | gemma3:270m | 46.4 | 38.4 to 53.8 | 1.1 s | 2.2 s | 19/25 | 0.29 GB | Gemma terms |
| LM Studio | qwen3-0.6b | 45.7 | 34.9 to 56.6 | 1.2 s | 1.7 s | 0/25 | 0.45 GB | Apache 2.0 |
| Ollama | LFM2.5-1.2B-Instruct | 20.8 | 14.9 to 27.8 | 1.6 s | 2.4 s | 0/25 | 0.73 GB | LFM Open |

- Scribe gives Foundry Local 12 seconds a request, and a slower answer reaches the user as the raw text.
  Qwen3 1.7B's CPU build (12.6 s typically) is past that. So were Phi-4 Mini and Qwen3 4B, at about 16 s when
  last measured on the CPU; on an NVIDIA graphics card they run through CUDA in under 0.8 s (GPU table). A PC
  without an NVIDIA card should pick another model.
- The same model is two to five times faster in Ollama than in Foundry Local on the CPU (Qwen2.5 1.5B 2.5 s
  against 6.7 s, Qwen3 1.7B 2.8 s against 12.6 s), largely because llama.cpp reuses the instructions it
  already processed while Foundry Local's CPU path processes all of them again for every dictation.

## Method and reproduce

- **Dictations:** the 25 frozen transcripts of the September 4 benchmark
  ([evidence](benchmarks/gpt6-astra-2026-09-04.json)): speech synthesized from 25 hard scripts and recognized
  by Scribe's Parakeet model, so they carry real recognition errors. The Foundry Local 2.1.0 rows add 8
  structure cases.
- **Harness:** `tools/Scribe.Evals` drives Scribe's own `TextCleanupService`, with its instructions, chunking,
  guards, answer cleanup and vocabulary admission, through each runtime: Ollama, LM Studio and TensorSharp
  through the OpenAI-compatible API as Settings configures them (Ollama through its own API when a context
  size is chosen), and Foundry Local through its SDK in process. Vocabulary is the terms each dictation
  mentions from the two word packs a fresh install lets AI cleanup use; the whole-vocabulary arm sent all 11
  built-in word packs (1,332 terms). Each model is unloaded before the next one loads, and times exclude the
  first request after a load. **Vocab accuracy** is the share of the word pack written forms the reference
  rewrite uses that the output keeps exactly.
- **Runtimes:** Ollama 0.34.4 to 0.40.7, LM Studio 0.4.25 (CUDA 12 llama.cpp runtime 2.47.0), TensorSharp
  v2026.10.11 (`ggml_cuda`) and Foundry Local 2.1.0 (ONNX Runtime 1.30.0).
- **Judge:** Claude Opus 5.5 for the September and October 8 rows, and Claude Sonnet 5.5 with Claude Opus 5.5
  for the October 17 to 21 rows, through the GitHub Copilot command-line tool, blind to model and runtime.
  Each request graded up to 12 distinct answers to one dictation against its golden reference, and also
  graded two calibration answers (the raw text and a cloud answer) to measure drift between requests. Score:
  45% faithfulness, 25% following the instructions, 20% removing disfluencies, 10% mechanics. Intervals:
  10,000-draw bootstrap over the 25 dictations.
- **Uncertainty:** the same calibration answer's grade varied by 8.9 points on average between requests
  (28 at most), so differences under about 5 points between models are not a ranking. Scores are
  comparable within this report only, not with the grades in [model-leaderboard.md](model-leaderboard.md).
  One PC, one run of the judge.
- **Answers as recorded:** Scribe's later answer cleanup (removing announcements, labels, think tags,
  wrapper tags and trailing notes) lifts only the weakest small models and changed no answer of the picks.
- **Limits:** Scribe allows a Foundry Local request 12 seconds and a server on this PC 45 seconds; a slower
  answer is typed as heard.

```powershell
# Set SCRIBE_DATA_DIR to a scratch folder first, as for any dev instance. The model prefix names the
# runtime: ollama:, lmstudio:, tensorsharp: or foundry: (--ollama-endpoint, --lmstudio-endpoint and
# --tensorsharp-endpoint move a runtime off its default address). --admitted sends every request through
# the production vocabulary admission, and --clean-timeout 0 keeps Scribe's own deadlines, where the
# harness would otherwise override them with 180 seconds.

# Ollama at 32K. Give LM Studio ("lmstudio:google/gemma-4-e2b") and TensorSharp
# ("tensorsharp:gemma-4-26B-A4B-it-UD-IQ3_S.gguf") their own runs at 16384, or 8192 for Qwen3.8-27B.
# For the whole-vocabulary arm add --glossary-libraries all --whole-vocabulary (all 11 built-in word packs).
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-cloud --runs 3 --no-judge --no-wav `
  --local-models "ollama:gemma4:26b-a4b-iq3s,ollama:gemma4:12b" `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json --glossary-libraries default --vocabulary mentioned `
  --admitted --context-size 32768 --clean-timeout 0 --out runs/ollama

# Foundry Local takes no context size: its model files fix it
dotnet run -c Release --project tools/Scribe.Evals -- --benchmark --no-cloud --runs 3 --no-judge --no-wav `
  --local-models "foundry:qwen2.5-7b" `
  --cases-from docs/benchmarks/gpt6-astra-2026-09-04.json --glossary-libraries default --vocabulary mentioned `
  --admitted --clean-timeout 0 --out runs/foundry

# Blind grading through the GitHub Copilot command-line tool, with a cloud answer as the anchor
dotnet run -c Release --project tools/Scribe.Evals -- --blind-judge `
  --judge-results runs/ollama/results.json,runs/foundry/results.json `
  --judge-anchors "docs/benchmarks/gpt6-astra-2026-09-04.json:gpt-5.6-terra@1" --out runs/judge
```

The scores, times and token counts behind the tables are in `docs/benchmarks`:

- [local-models-2026-09-29.json](benchmarks/local-models-2026-09-29.json): the September 29 board, per
  answer (Ollama, LM Studio and the CPU rows).
- [foundry-local-2.1.0-2026-09-30.json](benchmarks/foundry-local-2.1.0-2026-09-30.json): the Foundry Local
  2.1.0 rows.
- [ollama-model-compatibility-2026-10-08.json](benchmarks/ollama-model-compatibility-2026-10-08.json):
  `gemma4:12b`, `deepseek-r1` and `VicRodger27/Writex:4b`.
- [cleanup-tuning-2026-09-29.json](benchmarks/cleanup-tuning-2026-09-29.json) and
  [context-window-2026-09-30.json](benchmarks/context-window-2026-09-30.json): the mentioned and whole
  vocabulary comparisons and the reasoning settings.
- [ollama-12b-prompts-2026-10-07.json](benchmarks/ollama-12b-prompts-2026-10-07.json),
  [prompt-cache-2026-10-08.json](benchmarks/prompt-cache-2026-10-08.json) and
  [prompt-cache-confirmation-2026-10-08.json](benchmarks/prompt-cache-confirmation-2026-10-08.json): Gemma 4
  12B instruction variants and Ollama prefix reuse, which add no rows to the tables.
