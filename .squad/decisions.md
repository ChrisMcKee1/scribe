# Squad Decisions

## Active Decisions

### 2026-08-24: macOS port walking skeleton kickoff
**By:** Lead
**What:** Start the macOS port as a separate Swift Package Manager executable under `macos/Scribe`, target macOS 13+ on Apple Silicon, and produce a runnable `.app` bundle with a small shell script instead of introducing an Xcode project.
**Why:** This keeps the Windows tree untouched, gives the team a fast CLI build path on this Mac, and provides a real menu bar app skeleton that Backend, Platform, and Frontend can extend in parallel.

### 2026-08-24: macOS parity plan and cleanup architecture
**By:** Lead
**What:**
- We will pursue feature-for-feature parity from the Windows product through the existing native Swift macOS shell under `macos/Scribe`, not by reshaping the Windows app.
- The primary macOS ASR path will stay on Parakeet TDT via a thin sherpa-onnx arm64 wrapper, with Silero VAD and no language picker, to preserve product behavior.
- The macOS cleanup layer will be a Swift `CleanupProvider` protocol with three planned implementations: `OpenAICompatibleCleanupProvider`, `MicrosoftFoundryCleanupProvider`, and `ManagedOllamaCleanupProvider`.
- macOS will not expose the Windows-only `Foundry Local` name. The user-facing local option will be `Local model (Ollama managed)`.

**Why:**
- Reusing Parakeet keeps multilingual behavior, transcript shape, and cleanup assumptions aligned with the Windows product, which is the shortest path to real parity.
- Using a protocol-based cleanup layer keeps the macOS port aligned with the Windows provider story while avoiding Windows-only SDK dependencies.
- A managed Ollama path is the most practical macOS equivalent to Windows local cleanup because it uses the same OpenAI-compatible transport as local servers like LM Studio while still enabling a guided on-device experience.

### 2026-08-24: Real macOS cleanup model benchmark completed
**By:** Squad (Coordinator), following up on Lead's timed-out attempt
**What:** Ran the actual six-case golden benchmark (ported from `tools/Scribe.Evals`) against four local Ollama models on this Apple Silicon Mac: `qwen2.5:0.5b`, `qwen2.5:1.5b`, `qwen2.5:3b`, `llama3.2:1b`. Full results in `macos/CLEANUP-MODEL-BENCHMARK.md`.
**Decision:** Default macOS on-device cleanup model is `qwen2.5:3b` (best heuristic quality score 0.632, 1.57s median latency). `qwen2.5:1.5b` is the recommended fast alternative (0.562 score, 0.7s median). `llama3.2:1b` is excluded from the curated default list (worst quality, worst latency).
**Why:** User explicitly asked for an empirical benchmark comparing macOS/Apple Silicon model choices for AI cleanup, since Windows' Foundry Local defaults don't carry over. Scoring used a deterministic heuristic (text similarity + required-token presence) instead of an LLM judge, since no offline judge was available — this should be re-validated with a real judge before shipping.

### 2026-08-24: Build the first macOS audio capture shell
**By:** Backend
**What:** Added a real `AVAudioEngine` microphone capture path for the menu bar app, with resampling through `AVAudioConverter` to 16 kHz mono Float32 and a minimal SQLite history shell at `~/Library/Application Support/Scribe/scribe.db`.
**Why:** The Windows dictation pipeline expects 16 kHz mono Float32 input, so the macOS port now normalizes captured microphone audio into that same target format before ASR work starts. Creating the local SQLite file and recording test dictation metadata proves the persistence path end to end before the full schema and transcript pipeline land.

### 2026-08-24: macOS hotkey and injection shell
**By:** Platform
**What:** The macOS port now defaults push-to-talk to Right Option, uses a `CGEventTap` instead of Carbon hotkeys, and surfaces microphone, Input Monitoring, and Accessibility as three separate permission states.
**Why:** Right Option is easier to reach on common Mac keyboards than Right Control and is less likely to be missing or awkwardly placed. `CGEventTap` can observe a hold and release gesture reliably, while Carbon `RegisterEventHotKey` is a worse fit for hold-driven push-to-talk. macOS splits the required system privileges across three different privacy domains, so the app needs to log and present those failures distinctly instead of collapsing them into one generic permissions error.

### 2026-08-24: Corrected Foundry Local macOS support; made it the default local provider for cleanup and ASR
**By:** Squad (Coordinator), correcting Lead's earlier claim after user provided
`https://github.com/microsoft/homebrew-foundrylocal`
**What:**
- Foundry Local has a real, officially-supported macOS build via Homebrew (`microsoft/homebrew-foundrylocal`), verified installed and running on this machine (v0.10.3, Apple M5 GPU detected, `WebGpuExecutionProvider` auto-selected). The earlier claim in `macos/PORTING-PLAN.md` that "Foundry Local... does not have a direct macOS counterpart" was false and has been corrected.
- Added `FoundryLocalCleanupProvider` as a fourth `CleanupProvider` implementation and made it the **recommended default** local cleanup provider, ahead of managed Ollama (which remains fully supported, not deprecated).
- Benchmarked four Foundry Local chat models (`qwen2.5-0.5b`, `qwen2.5-1.5b`, `phi-3.5-mini`, `qwen2.5-7b`) against the same six-case golden suite already used for Ollama. Full combined results in `macos/CLEANUP-MODEL-BENCHMARK.md`. Recommended default: Foundry Local `qwen2.5-1.5b` (0.575 avg score, 1.4s median latency, no observed cold-start spikes), with `qwen2.5-7b` (0.700, best quality of every model tested on either runtime) offered as an opt-in "max quality" tier (5.3s median, too slow to default).
- Verified Foundry Local's `parakeet-tdt-0.6b-v2` model produces correct real-world ASR transcripts via `foundry transcribe -m parakeet-tdt-0.6b-v2 -f <wav>`, reproducing the exact same test transcript ("The quarterly report is due on Friday.") already independently verified via whisper-cli. Revised the ASR strategy decision: the production ASR path is now Foundry Local's Parakeet TDT via `foundry transcribe`, not a hand-rolled sherpa-onnx C API wrapper (still Parakeet TDT family, still no language picker, just obtained through Microsoft's supported runtime instead of a manually-built bridge).
- Reversed the "do not surface Foundry Local in macOS UI" guidance; the Settings provider picker now lists `Foundry Local` as a real, named, default-selected option.
**Why:** User corrected the record with a source link; verifying it was straightforward (Homebrew tap + install + `foundry status`/`foundry model info`/`foundry transcribe`). Standardizing on Foundry Local for both AI features keeps macOS on one real Microsoft-supported local-inference stack instead of three unrelated ones (sherpa-onnx, whisper.cpp, Ollama), and matches the Windows product's own "SDK owns hardware selection" philosophy. User explicitly directed: recommend Foundry Local and explain why, but keep Ollama fully supported too; default should optimize for the best experience even at a storage or separate-install cost, which this recommendation reflects (Foundry Local + `qwen2.5-1.5b` costs roughly 1.7 GB beyond the ~200 MB CLI install).

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
