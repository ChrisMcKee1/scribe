# Scribe macOS Porting Plan

## Current baseline

- Status: In Progress
- Existing macOS code: `macos/Scribe` SwiftPM menu bar shell, microphone permission request, settings stub, app bundle script
- Current gap: none of the shipped Windows dictation features exist yet on macOS beyond the shell

## Feature parity checklist

| Feature | Status | Owner | macOS implementation approach |
|---|---|---|---|
| Overlay pill with 9-anchor position picker | Done | Frontend | `OverlayPanelController` (`OverlayPanel.swift`): borderless, non-activating `NSPanel` hosting a SwiftUI `OverlayPillView`; `OverlayAnchor.swift` computes the 9-position origin from `NSScreen.visibleFrame`. Position picked from a status-bar submenu and a Settings grid picker, persisted via `UserDefaults` (stopgap; a general Settings preferences store doesn't exist yet). |
| Overlay live recording state and meter | Done | Frontend | `DictationSessionModel` (ObservableObject) drives `OverlayPillView` through `hidden`/`listening(levelDbfs:)`/`processing`/`failed`, mirroring Windows' `OverlayState`. Wired into the existing audio chunk callback (meter), capture stop (processing), and injection result (success hides, failure flashes red then hides). In-process (no separate overlay process/IPC needed on macOS since there's no WPF transparent-window bug to work around). |
| Settings navigation replacing static stub | Done | Frontend | `SettingsView.swift`: a `TabView` with Overlay/Dictionary/Snippets/App Profiles tabs, each backed directly by `PersistenceStore`'s CRUD surface (add/enable-disable/delete). Replaces the earlier one-paragraph static text scaffold. `PersistenceStore` gained `fetchAll*`/`set*Enabled`/`delete*` methods and a `databaseURL` override initializer for testability; covered by 9 new `PersistenceStoreCRUDTests`. |
| User dictionary, core substitution | Done | Backend | `TextPostProcessor` applies whole-word, case-insensitive dictionary substitutions from a new SQLite `dictionary_entries` table, matching Windows' single-pass, longest-match-first semantics. Unit-tested; verified end to end via `Scribe --post-process-text`. CSV import/export and history-mined suggestions remain separate follow-ups below. |
| Dictionary CSV import/export | Done | Platform | `DictionaryCsv.swift` and `DictionaryImportMerger.swift` are line-for-line Swift ports of `Scribe.Core.PostProcessing.DictionaryCsv` / `Scribe.Core.Settings.DictionaryImportMerger`, including the exact error wording, RFC 4180 quoting rules, and the case-insensitive "first writer wins, update or add" merge semantics. `DictionarySettingsTab` gained Import CSV/Export CSV/Get Template buttons backed by `NSOpenPanel`/`NSSavePanel`. 17 new XCTests port the C# xUnit suite 1:1 (round-trip, quoting, comments/header skipping, bad-row line numbers, merge add/update/unchanged/dedup). Found and fixed a real porting bug this way: a naive `Character`-based CSV reader silently drops every record after the first because Swift's grapheme clustering merges `"\r\n"` into one `Character`, defeating a `case "\n":` switch arm that assumed C#'s per-UTF-16-char semantics; fixed by iterating `unicodeScalars` instead. Live-verified the Get-Template -> Import round trip via the real Settings window (`NSSavePanel` save confirmed byte-identical template content on disk); `NSOpenPanel` proved too flaky to drive end-to-end via AppleScript in this sandbox, so the import path is trusted on its unit tests plus the identical, already-verified `NSSavePanel` pattern. |
| Dictionary history-mined suggestions | Not Started | Backend | Mine recurring corrections and transcript terms from local history, then surface ranked suggestions in Settings. |
| Voice snippets | Done | Backend | `Snippet`/`snippets` SQLite table plus `TextPostProcessor` expand spoken trigger phrases into (possibly multi-line) templates before dictionary canonicalization runs, matching Windows' snippets-first ordering. Verified with a multi-line template via `Scribe --post-process-text`. |
| Per-app profiles by focused app | Done | Platform | `AppProfile`/`AppProfileMatcher` in `AppProfile.swift`; keys on bundle identifier first (via `NSWorkspace.shared.frontmostApplication.bundleIdentifier`), process name as fallback. SQLite-backed (`app_profiles` table). |
| Per-app writing style override | Done (stopgap) | Platform | `AppProfile.writingStylePrompt` resolved and logged at dictation time; not yet threaded into the live cleanup call since AI cleanup itself is only wired through the `--cleanup-text` CLI verb (see cleanup provider rows) rather than the live pipeline. Tracked as a follow-up alongside live cleanup wiring. |
| Per-app newline mode | Done | Platform | `NewlineInjectionMode` (smartFlatten/alwaysFlatten/keepNewlines) mirrors Windows; SmartFlatten checks bundle identifier against a known-terminal list (Terminal, iTerm2, Warp, WezTerm, kitty, Hyper, Ghostty). Applied to injected text in the live pipeline. |
| AI cleanup, OpenAI-compatible endpoint | Done | Platform | `OpenAICompatibleCleanupProvider` is a `URLSession`-based `/v1/chat/completions` client shared by every provider below; verified against a real Ollama endpoint via `SCRIBE_CLEANUP_PROVIDER=openai-compatible`. Unit-tested with a stubbed `URLProtocol`. |
| AI cleanup, Microsoft Foundry cloud | Not Started | Platform | Call the REST API directly, support Azure CLI and service principal auth, and store secrets in Keychain. |
| AI cleanup, on-device local runtime | Done | Platform | `FoundryLocalCleanupProvider` (default, `qwen2.5-1.5b`, dynamic port resolved via `foundry status -o json`) and `ManagedOllamaCleanupProvider` (`qwen2.5:3b`, fixed port 11434) both verified end to end via `Scribe --cleanup-text`, producing correctly punctuated output from a messy raw transcript. Provider selection is env-var driven (`SCRIBE_CLEANUP_PROVIDER`) pending the Settings UI. |
| Silence auto-stop for toggle mode | Done (stopgap) | Backend | `SilenceAutoStopDetector` implements an energy-threshold RMS detector (armed only for menu/toggle capture, never push-to-talk) firing after 2.0s below -45 dBFS once real speech was observed; unit-tested with XCTest. A trained Silero ONNX VAD (matching Windows exactly) is a follow-up, not yet done. |
| Playground, raw recognition view | Not Started | Frontend | Add a SwiftUI playground window that runs the normal dictation pipeline and shows raw transcript output. |
| Playground, replacement highlights | Not Started | Frontend | Show dictionary, snippet, and cleanup diffs inline by pipeline stage. |
| Playground, per-step timings | Not Started | Backend | Emit timing events for capture, VAD, ASR, replacements, cleanup, and injection, then bind them into the playground UI. |
| Diagnostics panel, P50/P95 decode latency | Done | Backend | `DictationStats.swift` is a direct port of Windows' `Scribe.Core.Diagnostics.DictationStats` (same R-7/Excel-method percentile interpolation, verified against the same numeric fixtures via `DictationStatsTests`). `dictation_history` gained `decode_ms`/`cleanup_ms` columns (nullable, added via a non-destructive `ALTER TABLE` migration for existing databases). Rendered in the Settings window's new Diagnostics tab (24h/7d/30d window picker) and reachable headlessly via `Scribe --diagnostics [days]`. |
| Diagnostics panel, real-time factor | Done | Backend | RTF (fastest/P50/P95) computed alongside decode latency in the same `DictationStats.compute`, from `decodeMilliseconds / audioMilliseconds` per dictation; audio duration was already recorded, decode time is now captured around the real `transcribe()` call in `main.swift` using `DispatchTime`. |
| Usage insights, local totals and trend chart | Not Started | Backend | Maintain local usage aggregates from dictation history and chart them in SwiftUI. |
| Usage insights, top apps | Not Started | Platform | Attribute each dictation to the frontmost app at injection time and aggregate by bundle id. |
| Usage insights, recurring terms with one-click dictionary add | Not Started | Backend | Reuse transcript mining to rank repeated terms and wire add-to-dictionary actions directly from the insights view. |
| Opt-in AI insight summary | Not Started | Platform | Send only aggregate totals and dictionary-covered labels to the configured cleanup provider, never raw novel terms or audio. |
| Dictation recovery, last 5 transcripts in tray | Done | Frontend | `LastTranscriptStore.swift` is a direct port of Windows' `LastTranscriptStore` (bounded ring, content-keyed `update`, `seed`, `formatPreview` truncation with surrogate-pair safety). Wired into a "Recent Dictations" submenu populated on `NSMenuDelegate.menuWillOpen`, matching Windows' `PopulateRecentDictations`. Clicking an entry copies it to the clipboard. Note: since `dictation_history` does not yet store transcript text, the ring only reflects the current run (no restart-survives-via-history fallback yet, unlike Windows); tracked as a follow-up alongside a text-retaining history schema change. 25 tests ported directly from the C# fixtures, all passing. |
| Injection failure recovery notification | Done | Platform | `AppDelegate.postInjectionFailureNotification()` mirrors Windows' `_controller.InjectionFailed` tray balloon: on `.accessibilityDenied`/`.noFocusedElement`, raises a local `UNUserNotificationCenter` alert (in addition to the existing modal `NSAlert`) with a "Copy Transcript" action wired to `LastTranscriptStore`. The transcript is already saved to the store before injection is attempted, so recovery works regardless of whether the user acts on the notification or opens the "Recent Dictations" tray submenu. Authorization/posting failures are logged and swallowed, never propagated back into the dictation pipeline (best-effort, same as Windows). Live-verified end to end with `SCRIBE_FORCE_ACCESSIBILITY_DENIED=1`: real capture -> ASR -> injection-denied path executes cleanly with no crash (notification authorization itself is denied for this unsigned dev binary in the current sandbox, which is expected and logged, not a code defect). |
| Tray quick add to dictionary | Not Started | Frontend | Present a transient popover from a recent transcript token list and commit the chosen correction back into history and dictionary tables. |
| Dictionary cleanup, disable unused entries | Not Started | Backend | Scan whether spoken and written forms ever appear in history and soft-disable stale entries instead of deleting them. |
| Tray quick toggles, AI cleanup on or off | Done | Frontend | A tray "AI Cleanup" checkbox item persists the user's intent via `UserDefaults` (`ScribeAiCleanupEnabled`), the same stopgap pattern as the overlay anchor. Note: AI cleanup itself is still only reachable through the `--cleanup-text` CLI verb and is not yet wired into the live dictation pipeline, so this toggle records intent for when that wiring lands rather than changing live output today; called out in a code comment. |
| Tray quick toggles, pause | Done | Frontend | A tray "Pause Dictation" checkbox item toggles `HotkeyManager.isPaused` (persisted via `ScribeIsPaused` in `UserDefaults`), mirroring Windows' `DictationController.SetPaused`: an in-flight capture stops immediately, new hotkey presses and the menu's "Start Test Dictation" are both ignored while paused, and the status bar icon swaps to `mic.slash.fill`. The event tap itself stays installed the whole time, so resuming never re-prompts for Input Monitoring. Manually verified end to end against the real binary: pausing during an active capture stops it, a paused hotkey press is ignored and logged, and the paused state survives a relaunch via `UserDefaults`. |
| Welcome and onboarding flow | Done | Frontend | `WelcomeView.swift` (SwiftUI) mirrors Windows' `WelcomeWindow`: push-to-talk gesture hint, privacy/offline promise, AI cleanup opt-in note, "Open Settings" and "Got It" actions. Shown non-modally on first launch (`ScribeHasCompletedFirstRun` in `UserDefaults`, consistent with the existing overlay-anchor stopgap persistence pattern) and reachable again anytime via a "Welcome..." tray menu item. Manually verified: appears automatically on a fresh launch, the flag persists across restarts, and the app never crashes across open/close cycles. |
| About page with privacy, support, source, and star links | Done | Frontend | `AboutView.swift` is a new 6th Settings tab mirroring Windows' `SectionAbout`: version/app header, "Private by design" privacy blurb linking `PRIVACY.md`, a GitHub-star card, a support/source card (report issue, view source), and a data-location card showing the live `scribe.db` path with Copy and Reveal-in-Finder actions (the macOS equivalent of Windows' Explorer-open button; there is no diagnostics-zip export yet since macOS logs go to stderr rather than a rotated log file). Manually verified end to end against the running app: all six tabs present (confirmed via `radio button` count), every card's text renders with real values (including the live database path), Copy correctly places the path on the clipboard, and Reveal in Finder opens Finder with no crash. |

## Foundation workstreams that unblock parity

| Workstream | Status | Owner | Why it comes first |
|---|---|---|---|
| Global hotkey capture | In Progress | Platform | Push-to-talk parity depends on a reliable event tap before any higher-level feature matters. |
| Audio capture and VAD | In Progress | Backend | Every dictation feature depends on a stable `AVAudioEngine` capture path plus silence detection. Fixed a real crash found while verifying the tray pause toggle: `AudioCaptureEngine.convert` used `AVAudioConverter.convert(to:from:)`, which enforces `outputBuffer.frameCapacity >= inputBuffer.frameLength` even when downsampling (48kHz -> 16kHz) makes that impossible, throwing an uncaught Objective-C exception that terminated the whole process on every real live dictation. Switched to the block-based `convert(to:error:withInputFrom:)` overload, which has no such constraint; verified live capture -> conversion -> ASR invocation completes with no crash across several real microphone captures. |
| ASR wrapper and transcript session model | Done (production path) | Backend | `TranscriptionEngine.swift` now defaults to Foundry Local's `parakeet-tdt-0.6b-v2` via `foundry transcribe -m <alias> -f <wav> -o json`, verified end to end through the real `Scribe` binary. `whisper-cli` remains as a documented fallback (`SCRIBE_ASR_BACKEND=whisper`), still verified working. |
| Text injection | In Progress | Platform | macOS accessibility-backed text insertion is a platform-specific core dependency. |
| Shared persistence layer | In Progress | Backend | Dictionary, snippets, profiles, recovery, diagnostics, and usage insights all need one local store. |
| Settings and navigation IA | In Progress | Frontend | The shell exists, but the full multi-section settings product still needs to be built. |

## ASR strategy decision

### Correction (2026-08-24)

An earlier pass of this plan claimed Foundry Local "does not have a direct macOS counterpart." That
was wrong: Foundry Local ships a real macOS build via
[`microsoft/homebrew-foundrylocal`](https://github.com/microsoft/homebrew-foundrylocal), verified on
this machine (`brew tap microsoft/foundrylocal && brew install foundrylocal`, v0.10.3, Apple M5 GPU
detected via `WebGpuExecutionProvider`). It also hosts `parakeet-tdt-0.6b-v2` in its model catalog as
a native `foundry transcribe` target, which changes the ASR decision below.

### Decision

**Primary path: Foundry Local's `parakeet-tdt-0.6b-v2` via `foundry transcribe`, not a hand-rolled
sherpa-onnx C bridge.** Keep the shipped Parakeet TDT model family as the ASR path (unchanged from
the original decision), but obtain it through Microsoft's own supported runtime instead of manually
wrapping the sherpa-onnx C API in a thin Swift module.

Verified directly on this machine:

```text
$ time foundry transcribe -m parakeet-tdt-0.6b-v2 -f /tmp/verify_test.wav -o json
{"model":"parakeet-tdt-0.6b-v2-generic-cpu:1","file":"/tmp/verify_test.wav",
 "text":" The quarterly report is due on Friday.","language":null,"durationSeconds":null}
real  1m6.425s   # includes first-run model download + load; steady-state calls are not this slow
```

The transcript exactly matches the same synthesized fixture ("The quarterly report is due on
Friday.") independently verified through `whisper-cli` earlier in this port, so this is a real,
working ASR path today, not a hypothetical.

### Why this beats both alternatives

1. **Parity beats novelty here.** Windows already ships Parakeet TDT, Silero VAD, and no language
   picker. Reusing the same model family preserves multilingual behavior, transcript shape, and the
   existing post-processing assumptions. This part of the original decision is unchanged.
2. **Foundry Local beats a hand-rolled sherpa-onnx bridge for delivery speed and hardware ownership.**
   A native sherpa-onnx C API wrapper means owning: model distribution, ORT session lifecycle, VAD
   segmentation glue, and hardware-provider selection ourselves in Swift. Foundry Local already does
   all of this (it is a real ORT-based runtime with its own model manager and execution-provider
   negotiation), so `foundry transcribe -f <path>` is functionally the sherpa-onnx wrapper we planned
   to build, already built, already shipping on macOS, and already exercising the same execution
   provider selection logic AGENTS.md documents for Windows ("the SDK owns hardware selection, do not
   try to take it back").
3. **Whisper.cpp still changes the product contract**, unchanged reasoning from the original decision:
   Whisper-based integrations push toward a language hint or auto-detect UX, and AGENTS.md is explicit
   that Scribe should not grow a language picker for the shipped model behavior. Parakeet via Foundry
   Local has no such parameter, matching Windows exactly.
4. **One runtime, two features.** Foundry Local is already the recommended default for AI cleanup (see
   below), so using it for ASR too means macOS Scribe depends on one Microsoft-supported local
   inference stack instead of three unrelated ones (sherpa-onnx, whisper.cpp, Ollama).
5. **Benchmark comparability still holds.** Keeping the ASR family aligned means Windows and macOS
   cleanup benchmarks stay comparable, because the cleanup model sees similar raw transcript failure
   modes either way.

### Implementation note

- Invoke Foundry Local's `transcribe` subprocess (or its `/v1` HTTP surface, if speech transcription
  is exposed there in a future CLI version; not yet confirmed) from `TranscriptionEngine.swift`
  instead of `whisper-cli`. Treat this as a provider swap behind the existing transcription interface,
  not a rewrite of the capture pipeline.
- `parakeet-tdt-0.6b-v2` is not byte-identical to Windows' `parakeet-tdt-0.6b-v3-int8` (different
  minor version, not yet confirmed int8-quantized on macOS), so re-run the accuracy/latency
  characterization Windows did in `tools/Scribe.AsrCheck` (long-audio, channel-mix, degraded-audio
  sweeps) before treating macOS ASR quality as proven equivalent to Windows.
- Keep `whisper.cpp` as a documented fallback path only, in case Foundry Local's transcription
  latency proves unacceptable for real-time push-to-talk once profiled with longer dictations; it is
  no longer the primary spike target.
- Preserve the no-language-picker product rule; Foundry Local's `--language` flag on `transcribe` is
  optional and should stay unset by default, matching Parakeet's actual behavior (auto-handles
  whatever is spoken, no runtime language parameter needed).

## AI cleanup provider architecture

### Provider protocol

```swift
protocol CleanupProvider {
    var id: String { get }
    var displayName: String { get }
    var capabilities: CleanupProviderCapabilities { get }

    func validateConfiguration() async throws
    func warmUp() async throws
    func clean(_ request: CleanupRequest) async throws -> CleanupResponse
    func healthSnapshot() async -> CleanupHealthSnapshot
}
```

Supporting types to plan now:

- `CleanupRequest`: transcript, effective writing style, prompt variant, glossary, timeout, and metadata such as single-line mode
- `CleanupResponse`: cleaned text, latency, token counts if available, and provider-specific diagnostics
- `CleanupProviderCapabilities`: local vs cloud, streaming support, auth modes, managed-runtime support

### Planned concrete implementations

1. `OpenAICompatibleCleanupProvider`
   - Covers LM Studio, OpenRouter, and any other compatible local or remote server.
   - Uses `URLSession` with configurable base URL, model, and API key.
   - This is the portability layer that keeps macOS aligned with Windows' bring-your-own endpoint story.

2. `MicrosoftFoundryCleanupProvider`
   - Reuses the REST API rather than any Windows-only SDK.
   - Supports two auth sources: serialized Azure CLI token acquisition and service principal credentials.
   - Secrets live in Keychain, not plist files or environment variables.

3. `FoundryLocalCleanupProvider`
   - **Recommended default local provider** (see benchmark results in `CLEANUP-MODEL-BENCHMARK.md`).
   - Talks to Foundry Local's real macOS build (`microsoft/homebrew-foundrylocal`) over its
     OpenAI-compatible `/v1/chat/completions` endpoint; port is dynamic, read from `foundry status`
     or the CLI's own reported startup line, not hardcoded like Ollama's 11434.
   - Owns model load/download orchestration via `foundry model load <alias>` (or the equivalent SDK
     call if/when a native Swift or C SDK binding is adopted instead of shelling out to the CLI).
   - Uses the SDK's own hardware selection; Scribe never picks the execution provider, matching the
     "SDK owns hardware selection" rule already established for Windows in AGENTS.md.
   - Same runtime also serves ASR (`parakeet-tdt-0.6b-v2` via `foundry transcribe`), so this provider
     and the ASR wrapper share one install/health-check story instead of two.

4. `ManagedOllamaCleanupProvider`
   - Fully supported alternative local provider, not deprecated by the addition of Foundry Local.
   - Owns health checks, optional install guidance, daemon startup, and curated local-model management, then talks to the same OpenAI-compatible local endpoint (`http://127.0.0.1:11434`).
   - Appropriate for users who already run Ollama for other tools or prefer its model catalog; Settings should let users choose either managed local provider, with Foundry Local pre-selected by default.

### Native on-device choice

**Recommendation: Foundry Local is the default managed local runtime for the first parity release.
Managed Ollama remains a fully supported alternative. MLX-swift in-process inference stays a future
spike.**

#### Foundry Local wins the default slot because

- It is Microsoft's own on-device SDK, the same family Windows Scribe already depends on
  (`Microsoft.AI.Foundry.Local.WinML`), so macOS and Windows share one real architecture for local AI
  instead of two unrelated stacks.
- It owns hardware selection for us (verified: Apple M5 GPU auto-selected via
  `WebGpuExecutionProvider` in this session), the same operating philosophy Windows already follows.
- It serves both AI features macOS needs: chat-style cleanup and Parakeet-family ASR, from one
  install and one health-check surface (see ASR strategy decision above).
- Benchmarked quality is competitive with Ollama at the same parameter count (`qwen2.5-1.5b`: 0.575
  Foundry Local vs. 0.562 Ollama) with a flatter, spike-free latency curve in this run. See
  `CLEANUP-MODEL-BENCHMARK.md` for full numbers.
- This is a deliberate storage/install tradeoff, made on purpose: Scribe's design goal is the best
  local-first experience, even if that costs a second background service and roughly 1.5-5 GB of
  model weights depending on the tier a user selects. Ollama users are not worse off; they simply
  point Scribe at their existing install instead.

#### Managed Ollama stays fully supported because

- Some users already have it installed and configured for other tools, or prefer its GGUF
  quantization ecosystem.
- It already matches the same OpenAI-compatible abstraction, so supporting both providers costs one
  extra `CleanupProvider` implementation, not a second protocol.
- `qwen2.5:3b` on Ollama scored the single best result on that runtime (0.632) and remains a
  legitimate user choice for those who prefer it over Foundry Local's `qwen2.5-1.5b`.

#### MLX-swift stays a future spike because

- It would be truly in-process and dependency-light once weights are installed.
- It would also force us to own model conversion, distribution, and a separate evaluation matrix for MLX-native checkpoints.
- It would add a third local ecosystem alongside Foundry Local and Ollama with no benchmarked quality
  advantage demonstrated yet.

## Provider naming in the macOS UI

Unlike an earlier draft of this plan, `Foundry Local` **is** a real, correctly-named macOS concept
and should be surfaced as such. The Settings provider picker should use:

- `Foundry Local` (default, recommended)
- `Local model (Ollama managed)`
- `Microsoft Foundry` (cloud)
- `OpenAI-compatible endpoint`

In docs, explain that Foundry Local and managed Ollama are both fully local, no audio or transcript
data leaves the device with either, and Foundry Local is recommended by default because it shares a
runtime with the macOS ASR path and matches the Windows architecture most closely.

## Delivery sequence

### Phase 0, platform foundations

- Platform: global hotkey, frontmost app detection, text injection, Keychain storage
- Backend: capture session, VAD, sherpa wrapper spike, SQLite store shell
- Frontend: real settings navigation, menu bar state model, overlay window shell

### Phase 1, core dictation parity

- Backend: transcript pipeline, replacements, recovery storage, metrics events
- Platform: per-app profile lookup and injection modes
- Frontend: overlay pill, live state, recovery menu, pause and cleanup toggles

### Phase 2, text intelligence parity

- Backend: dictionary, snippets, cleanup prompt composition, cleanup benchmark harness
- Platform: OpenAI-compatible provider, Foundry (cloud) provider, Foundry Local provider (default), managed Ollama provider
- Frontend: dictionary, snippets, and cleanup settings surfaces

### Phase 3, diagnostics and product polish

- Backend: diagnostics rollups, usage insights, dictionary cleanup analysis
- Platform: notification flows, diagnostics export, local AI insight transport guardrails
- Frontend: playground, diagnostics, usage insights, onboarding, About page

## Default local cleanup model recommendation

See `macos/CLEANUP-MODEL-BENCHMARK.md` for full results. **Real benchmark completed**: `qwen2.5:3b`
is the recommended default (best quality, 1.57s median latency), with `qwen2.5:1.5b` offered as a
faster low-latency alternative in Settings. `llama3.2:1b` and `qwen2.5:0.5b` were tested and ruled
out (worst quality, and both showed a 9-10s cold-start latency spike on one case). Note: scoring used
a deterministic heuristic (no offline judge model available), so absolute scores are not directly
comparable to the Windows `docs/model-leaderboard.md` numbers — re-run with a real judge before
finalizing for ship.
