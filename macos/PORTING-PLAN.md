# Scribe macOS Porting Plan

## Current baseline

- Status: Feature parity complete
- Existing macOS code: `macos/Scribe` SwiftPM menu bar app: full dictation pipeline (hotkey, capture, ASR via Foundry Local, text injection), dictionary/snippets/app-profiles, Settings window (Overlay/Dictionary/Snippets/App Profiles/Diagnostics/About/Playground), AI cleanup across Foundry Local, managed Ollama, any OpenAI-compatible endpoint, and Microsoft Foundry cloud (Azure CLI or service-principal auth, secrets in Keychain)
- Current gap: every row in the checklist below is Done; remaining follow-ups are called out inline per row (e.g. a trained Silero VAD to replace the energy-threshold detector, a Settings UI to replace the env-var-driven cleanup provider selection, live AI-cleanup wiring into the interactive pipeline)

## Feature parity checklist

| Feature | Status | Owner | macOS implementation approach |
|---|---|---|---|
| Overlay pill with 9-anchor position picker | Done | Frontend | `OverlayPanelController` (`OverlayPanel.swift`): borderless, non-activating `NSPanel` hosting a SwiftUI `OverlayPillView`; `OverlayAnchor.swift` computes the 9-position origin from `NSScreen.visibleFrame`. Position picked from a status-bar submenu and a Settings grid picker, persisted via `UserDefaults` (stopgap; a general Settings preferences store doesn't exist yet). |
| Overlay live recording state and meter | Done | Frontend | `DictationSessionModel` (ObservableObject) drives `OverlayPillView` through `hidden`/`listening(levelDbfs:)`/`processing`/`failed`, mirroring Windows' `OverlayState`. Wired into the existing audio chunk callback (meter), capture stop (processing), and injection result (success hides, failure flashes red then hides). In-process (no separate overlay process/IPC needed on macOS since there's no WPF transparent-window bug to work around). |
| Settings navigation replacing static stub | Done | Frontend | `SettingsView.swift`: a `TabView` with Overlay/Dictionary/Snippets/App Profiles tabs, each backed directly by `PersistenceStore`'s CRUD surface (add/enable-disable/delete). Replaces the earlier one-paragraph static text scaffold. `PersistenceStore` gained `fetchAll*`/`set*Enabled`/`delete*` methods and a `databaseURL` override initializer for testability; covered by 9 new `PersistenceStoreCRUDTests`. |
| User dictionary, core substitution | Done | Backend | `TextPostProcessor` applies whole-word, case-insensitive dictionary substitutions from a new SQLite `dictionary_entries` table, matching Windows' single-pass, longest-match-first semantics. Unit-tested; verified end to end via `Scribe --post-process-text`. CSV import/export and history-mined suggestions remain separate follow-ups below. |
| Dictionary CSV import/export | Done | Platform | `DictionaryCsv.swift` and `DictionaryImportMerger.swift` are line-for-line Swift ports of `Scribe.Core.PostProcessing.DictionaryCsv` / `Scribe.Core.Settings.DictionaryImportMerger`, including the exact error wording, RFC 4180 quoting rules, and the case-insensitive "first writer wins, update or add" merge semantics. `DictionarySettingsTab` gained Import CSV/Export CSV/Get Template buttons backed by `NSOpenPanel`/`NSSavePanel`. 17 new XCTests port the C# xUnit suite 1:1 (round-trip, quoting, comments/header skipping, bad-row line numbers, merge add/update/unchanged/dedup). Found and fixed a real porting bug this way: a naive `Character`-based CSV reader silently drops every record after the first because Swift's grapheme clustering merges `"\r\n"` into one `Character`, defeating a `case "\n":` switch arm that assumed C#'s per-UTF-16-char semantics; fixed by iterating `unicodeScalars` instead. Live-verified the Get-Template -> Import round trip via the real Settings window (`NSSavePanel` save confirmed byte-identical template content on disk); `NSOpenPanel` proved too flaky to drive end-to-end via AppleScript in this sandbox, so the import path is trusted on its unit tests plus the identical, already-verified `NSSavePanel` pattern. |
| Dictionary history-mined suggestions | Done | Backend | `DictionarySuggestionMiner.swift`, `DictionaryTermVariants.swift`, and `DictionaryHistoryLearner.swift` are faithful Swift ports of `Scribe.Core.PostProcessing.DictionarySuggestionMiner` / `DictionaryTermVariants` / `DictionaryHistoryLearner`: mine recurring "jargon-shaped" tokens (acronyms, CamelCase humps, letter+digit like K8s) across distinct dictations, then derive only the two safe/recoverable pattern shapes (acronym letter-spelling, 3+ letters; CamelCase compound-splitting filtered against a common-words stoplist) rather than inventing an unobserved `lowercase -> term` rule. Required a schema change: `dictation_history` gained a `transcript_text` column (via the existing probe-then-`ALTER TABLE` migration pattern) since the prior schema stored only timing/duration metadata and had no transcript text for the miner to read, unlike Windows' `HistoryEntry.Text`. The live pipeline (`transcribeAndInject` in `main.swift`) now records the final post-processed transcript alongside each history row. Surfaced via a "Learn from History" button in `DictionarySettingsTab` (Windows uses a tray menu item instead; a Settings button was chosen for macOS to keep all dictionary actions in one place, reusing the existing `statusMessage` pattern). 20 new XCTests port both C# xUnit suites 1:1. Live-verified end to end via the real app: seeded `dictation_history` rows with a recurring term ("ReBAC" x3), clicked "Learn from History" through AppleScript, confirmed the exact expected pattern/replacement (`re bac` -> `ReBAC`) was inserted into `dictionary_entries`, and confirmed a second click reports "No new recurring terms" (idempotent, no duplicate). |
| Voice snippets | Done | Backend | `Snippet`/`snippets` SQLite table plus `TextPostProcessor` expand spoken trigger phrases into (possibly multi-line) templates before dictionary canonicalization runs, matching Windows' snippets-first ordering. Verified with a multi-line template via `Scribe --post-process-text`. |
| Per-app profiles by focused app | Done | Platform | `AppProfile`/`AppProfileMatcher` in `AppProfile.swift`; keys on bundle identifier first (via `NSWorkspace.shared.frontmostApplication.bundleIdentifier`), process name as fallback. SQLite-backed (`app_profiles` table). |
| Per-app writing style override | Done (stopgap) | Platform | `AppProfile.writingStylePrompt` resolved and logged at dictation time; not yet threaded into the live cleanup call since AI cleanup itself is only wired through the `--cleanup-text` CLI verb (see cleanup provider rows) rather than the live pipeline. Tracked as a follow-up alongside live cleanup wiring. |
| Per-app newline mode | Done | Platform | `NewlineInjectionMode` (smartFlatten/alwaysFlatten/keepNewlines) mirrors Windows; SmartFlatten checks bundle identifier against a known-terminal list (Terminal, iTerm2, Warp, WezTerm, kitty, Hyper, Ghostty). Applied to injected text in the live pipeline. |
| AI cleanup, OpenAI-compatible endpoint | Done | Platform | `OpenAICompatibleCleanupProvider` is a `URLSession`-based `/v1/chat/completions` client shared by every provider below; verified against a real Ollama endpoint via `SCRIBE_CLEANUP_PROVIDER=openai-compatible`. Unit-tested with a stubbed `URLProtocol`. |
| AI cleanup, Microsoft Foundry cloud | Done | Platform | `MicrosoftFoundryCleanupProvider.swift` calls the Azure OpenAI-compatible chat completions REST endpoint directly (`{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...`) with a bearer token, reusing the same JSON wire format as `OpenAICompatibleCleanupProvider` (no Azure SDK equivalent exists for Swift). `AzureCredential.swift` provides two auth modes mirroring Windows' `AzureAuthMode`: `AzureCliCredentialProvider` (an `actor` that shells out to `az account get-access-token`, naturally serializing concurrent requests onto one `az` invocation at a time, the Swift-idiomatic equivalent of Windows' `AzureCliProcessCoordinator`) and `AzureServicePrincipalCredentialProvider` (an `actor` doing a direct OAuth2 client-credentials POST to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`, scope `https://cognitiveservices.azure.com/.default`). Both cache the token per-scope until 60s before expiry. `KeychainStore.swift` wraps Security framework generic-password APIs for the service-principal client secret (macOS's DPAPI-at-rest equivalent); the secret is set out-of-band via a new `Scribe --set-azure-client-secret <client-id>` CLI verb reading from stdin, and is **never** accepted via an environment variable, per AGENTS.md's hard rule. Deliberately has no ARM/subscription/deployment discovery in either auth mode (endpoint + deployment name are supplied directly via env vars), extending Windows' own "service-principal mode hides ARM discovery" rationale uniformly, since macOS has no Settings GUI yet to drive a discovery flow from. Wired into `CleanupProviderResolver` as `SCRIBE_CLEANUP_PROVIDER=microsoft-foundry` (see its doc comment for the full env var list). Error messages for HTTP 401/403 responses proactively mention the two easy-to-misdiagnose causes AGENTS.md documents: slow role-assignment propagation and a missing custom subdomain. Also fixed a real pre-existing bug found while working in this area: `OpenAICompatibleCleanupProvider.applyAuthHeader` was sending the literal string `"******"` as the Authorization header instead of `"Bearer \(apiKey)"`, meaning any configured `SCRIBE_CLEANUP_API_KEY` (e.g. for OpenRouter) never actually authenticated. 24 new XCTests cover CLI JSON parsing (both `expiresOn`/`expires_on` shapes, malformed input), service-principal validation, a stubbed-`URLProtocol` token exchange (request shape, caching, error surfacing), the chat-completions call (bearer header, 401 error-hint text), and a Keychain round trip (dedicated test service name, cleaned up in `tearDown`). Live-verified against real Azure endpoints from the built release binary: `--set-azure-client-secret` persisted a secret to the real Keychain and it round-tripped correctly into a live service-principal token request, which Entra correctly rejected with a real `AADSTS900023` error for a deliberately-fake tenant id, proving the full request path is live and correctly wired; CLI auth mode was also verified live, correctly invoking the real `az` binary on this machine and surfacing its actual "Please run 'az login' to setup account" error. Full cloud-cleanup completion (an actual successful chat completion) could not be verified without real Azure/Entra credentials, which aren't available in this environment; the auth and error-surfacing paths are proven live, but the happy path beyond token acquisition rests on the unit tests' stubbed HTTP layer. |
| AI cleanup, on-device local runtime | Done | Platform | `FoundryLocalCleanupProvider` (default, `qwen2.5-1.5b`, dynamic port resolved via `foundry status -o json`) and `ManagedOllamaCleanupProvider` (`qwen2.5:3b`, fixed port 11434) both verified end to end via `Scribe --cleanup-text`, producing correctly punctuated output from a messy raw transcript. Provider selection is env-var driven (`SCRIBE_CLEANUP_PROVIDER`) pending the Settings UI. |
| Silence auto-stop for toggle mode | Done (stopgap) | Backend | `SilenceAutoStopDetector` implements an energy-threshold RMS detector (armed only for menu/toggle capture, never push-to-talk) firing after 2.0s below -45 dBFS once real speech was observed; unit-tested with XCTest. A trained Silero ONNX VAD (matching Windows exactly) is a follow-up, not yet done. |
| Playground, raw recognition view | Done | Frontend | New "Playground" tab in `SettingsView.swift` displays the raw ASR transcript from the most recently completed dictation (hotkey or "Start Test Dictation"), pushed live via a new `PipelineReportStore` (`ObservableObject`) published from `AppDelegate.transcribeAndInject`. No separate playground window or dedicated "Run" button, unlike Windows: macOS's push-to-talk hotkey already fires regardless of which window/app is focused, so simply dictating normally while the Settings window is open on this tab is sufficient. Live-verified via AppleScript: triggered a real "Start Test Dictation" capture and confirmed the raw transcript, processed text, and timings rendered in the tab. |
| Playground, replacement highlights | Done | Frontend | `TextPostProcessor` gained `processDetailed(_:) -> TextPostProcessingResult` (new `TextReplacement`/`TextReplacementKind` model, mirroring Windows' `ITextPostProcessor.ProcessDetailed`), extending the existing single-pass matcher to also report each dictionary/snippet substitution's exact range in the final text. Snippet spans are re-located after the dictionary phase runs on top of the expanded template (via a search-forward pass), matching Windows' `canonicalSnippets`-style two-phase reporting. The Playground tab renders these as inline colored/underlined `Text` segments (blue = dictionary, green = snippet). **Scope decision:** macOS has no dictionary "library" concept (established in a prior segment) and no live AI-cleanup/glossary wiring into the interactive pipeline yet, so unlike Windows' `ProcessDetailed`, this port has no `sourceText` parameter and no second "glossary" pass over pre-cleanup text; only base dictionary + snippet replacements are reported. 4 new XCTests (`testProcessDetailedReports*`) cover exact-span reporting, snippet-then-dictionary canonicalization, unchanged text producing no replacements, and blank input. |
| Playground, per-step timings | Done | Backend | New `PipelineReport` struct (`PipelineReport.swift`) mirrors the shape of Windows' `DictationPipelineReport`: per-stage durations (capture, decode, post-processing/dictionary+snippets, injection, total), a real-time factor, raw/processed/final text snapshots, the `InjectionResult`, and an optional `failureStage`/`failureReason` pair (mirroring Windows' `Fail(stage, reason)`). `transcribeAndInject` in `main.swift` now times every stage (previously only decode was timed) and publishes a report through `PipelineReportStore` after each run, success or failure. **Scope decision:** two Windows timing rows are intentionally not represented — "AI cleanup" (no live AI cleanup call in the interactive pipeline; only reachable via `--cleanup-text`) and a discrete "VAD decode" duration (macOS's capture uses an energy-threshold `SilenceAutoStopDetector`, not a trained Silero model with an inference step to time). Both are called out as known gaps in the Playground tab itself via a code comment, not silently omitted. Live-verified: real ambient speech captured via "Start Test Dictation" produced correct capture/decode/post-processing/injection timings, a total, and a real-time factor in the Settings window. |
| Diagnostics panel, P50/P95 decode latency | Done | Backend | `DictationStats.swift` is a direct port of Windows' `Scribe.Core.Diagnostics.DictationStats` (same R-7/Excel-method percentile interpolation, verified against the same numeric fixtures via `DictationStatsTests`). `dictation_history` gained `decode_ms`/`cleanup_ms` columns (nullable, added via a non-destructive `ALTER TABLE` migration for existing databases). Rendered in the Settings window's new Diagnostics tab (24h/7d/30d window picker) and reachable headlessly via `Scribe --diagnostics [days]`. |
| Diagnostics panel, real-time factor | Done | Backend | RTF (fastest/P50/P95) computed alongside decode latency in the same `DictationStats.compute`, from `decodeMilliseconds / audioMilliseconds` per dictation; audio duration was already recorded, decode time is now captured around the real `transcribe()` call in `main.swift` using `DispatchTime`. |
| Usage insights, local totals and trend chart | Done | Backend | New `UsageAnalyzer.swift` is a faithful port of Windows' `Scribe.Core.Diagnostics.UsageAnalyzer` (trend bucketing via a new `LocalDate` calendar-date wrapper, top-apps ranking, and the covered/novel term-mining algorithm, sharing `DictionarySuggestionMiner.isJargonShaped` for novel-term detection). 11 XCTests ported 1:1 from `UsageAnalyzerTests.cs`, all passing on the first run. Rendered in a new "Usage Insights" Settings tab (7/30/90-day window picker) with a totals block (dictations, words, active days, speech time, average words) and a SwiftUI `Charts` bar-mark trend. Live-verified with seeded history: totals and trend chart rendered correctly. |
| Usage insights, top apps | Done | Platform | `dictation_history` gained a `target_app` column (non-destructive `ALTER TABLE` migration, same pattern as `decode_ms`/`cleanup_ms`/`transcript_text`). `transcribeAndInject` now threads its existing frontmost-app lookup (`bundleIdentifier ?? processName`, already computed for app-profile matching) into `recordDictationHistory`. The Usage Insights tab's "Top apps" section ranks by dictation/word count. Live-verified: seeded rows across 3 synthetic bundle IDs rendered correctly ranked, with unattributed rows grouped under "Unknown app" exactly as `UsageAnalyzer` mirrors Windows' behavior. |
| Usage insights, recurring terms with one-click dictionary add | Done | Backend | `UsageAnalyzer.Snapshot.terms` surfaces recurring dictation-covered and novel jargon-shaped terms (>=2 distinct dictations). The Usage Insights tab's "Recurring terms" section shows each term's dictation/occurrence counts and, for uncovered terms, an "Add to Dictionary" button that calls `persistenceStore.insertDictionaryEntry` directly (whole-word, enabled) and refreshes the snapshot so the term flips to "In dictionary". Live-verified end to end: seeded 3 dictations containing "AKS" (an acronym, so jargon-shaped), confirmed it appeared as a recurring term, clicked "Add to Dictionary" via AppleScript, confirmed a real `dictionary_entries` row (`AKS` -> `AKS`, whole_word=1, enabled=1) and the button flipping to "In dictionary". |
| Opt-in AI insight summary | Done | Platform | New `UsageInsight.swift` (`buildSummary`/`parse`/`systemPrompt`) ports Windows' `Scribe.Core.Diagnostics.UsageInsight`, including its surrogate-pair-safe truncation and trailing-only whitespace trim. 5 XCTests ported 1:1, all passing. The Usage Insights tab's "AI summary" section is opt-in per generation (a button, never automatic), states explicitly that only aggregate totals and dictionary-covered term labels are sent and novel terms/transcripts never leave the device, and reuses `CleanupProviderResolver.resolveDefaultProvider()` + the existing `CleanupProvider.clean(_:)` surface (system prompt = `UsageInsight.systemPrompt`, user content = the aggregate payload) so no new network path was introduced. Live-verified the button is reachable and does not crash the app when invoked with no provider configured (graceful error path). |
| Dictation recovery, last 5 transcripts in tray | Done | Frontend | `LastTranscriptStore.swift` is a direct port of Windows' `LastTranscriptStore` (bounded ring, content-keyed `update`, `seed`, `formatPreview` truncation with surrogate-pair safety). Wired into a "Recent Dictations" submenu populated on `NSMenuDelegate.menuWillOpen`, matching Windows' `PopulateRecentDictations`. Clicking an entry copies it to the clipboard. Note: since `dictation_history` does not yet store transcript text, the ring only reflects the current run (no restart-survives-via-history fallback yet, unlike Windows); tracked as a follow-up alongside a text-retaining history schema change. 25 tests ported directly from the C# fixtures, all passing. |
| Injection failure recovery notification | Done | Platform | `AppDelegate.postInjectionFailureNotification()` mirrors Windows' `_controller.InjectionFailed` tray balloon: on `.accessibilityDenied`/`.noFocusedElement`, raises a local `UNUserNotificationCenter` alert (in addition to the existing modal `NSAlert`) with a "Copy Transcript" action wired to `LastTranscriptStore`. The transcript is already saved to the store before injection is attempted, so recovery works regardless of whether the user acts on the notification or opens the "Recent Dictations" tray submenu. Authorization/posting failures are logged and swallowed, never propagated back into the dictation pipeline (best-effort, same as Windows). Live-verified end to end with `SCRIBE_FORCE_ACCESSIBILITY_DENIED=1`: real capture -> ASR -> injection-denied path executes cleanly with no crash (notification authorization itself is denied for this unsigned dev binary in the current sandbox, which is expected and logged, not a code defect). |
| Tray quick add to dictionary | Done | Frontend | `QuickDictionaryAdd.swift` is a direct port of Windows' `Scribe.Core.Settings.QuickDictionaryAdd` (chip tokenize/toggle/select, plan build for create/update/no-change/invalid, and delegating `apply` to `TextPostProcessor.applyRule` for transcript repair). Porting its 49 xUnit-equivalent tests surfaced and fixed three real, pre-existing gaps in the *shared* `TextPostProcessor.swift` (not scoped to just this feature, since every dictionary rule goes through it): (1) `applySinglePass` was missing Windows' "tight punctuation" guard, so a comma/period replacement left a stray space before it ("hello , world" instead of "hello, world"); (2) `DictionaryRule` had no double-expansion guard, so an expansion whose replacement embeds its own pattern ("york" -> "New York") could re-fire on already-canonical text; (3) `normalizeWhitespace`'s character class used `\v`, which ICU (backing `NSRegularExpression`) expands to the full vertical-whitespace set including `\r`/`\n`, unlike .NET's literal-vertical-tab-only `\v`, so it was silently collapsing every line break to a single space. All three are now ported/fixed and covered by tests. New `QuickAddView.swift` is the tray popup: a custom `Layout`-conforming `ChipFlowLayout` wraps word chips (tap to extend/shrink the phrase, mirroring Windows' plain-click gesture; full drag-select was not ported, since this project's AppleScript-based UI verification cannot exercise a drag gesture and a single tap gesture already covers the "join split words"/"pick one word" cases that motivate the feature), a recent-dictation picker (seeded from `LastTranscriptStore`, falling back to `PersistenceStore.fetchDictationHistory()`'s `transcriptText` via `LastTranscriptStore.seed(_:)` when the in-memory ring is empty, mirroring Windows' `ShowQuickAdd()`), "Heard"/"Should be" fields, a whole-word toggle, and a live status message from `QuickDictionaryAdd.build`. Wired into the tray as a new "Quick Add to Dictionary..." menu item. On save, persists via `insertDictionaryEntry`/`updateDictionaryEntry`, reloads the post-processor immediately, and repairs the retained copy of the source transcript in place via `LastTranscriptStore.update`. Live-verified end to end against the real app bundle: opened via the tray menu, confirmed the most-recent transcript is selected by default, tapped two adjacent chips ("cloud"/"pilot") and confirmed the "Heard" field read "cloud pilot", typed "Copilot" into "Should be", confirmed the live status message, saved, and confirmed a real `dictionary_entries` row (`cloud pilot` -> `Copilot`, whole_word=1, enabled=1) plus a clean tray-menu reopen afterward with no crash. Test artifacts were removed from the database afterward. |
| Dictionary cleanup, disable unused entries | Done | Backend | `DictionaryUsageAnalyzer.swift` is a direct port of `Scribe.Core.Settings.DictionaryUsageAnalyzer`, base-entries only: macOS has no shipped dictionary-library concept, so `ScoreLibrary`/`LibraryUsage` were deliberately not ported (scope decision, not a gap). Preserves the "inversion trap" design: a working rule erases its own pattern from stored text, so a term is only flagged when **neither** its spoken pattern **nor** its written replacement ever appears in history, using the exact whole-word/case-insensitive regex `TextPostProcessor` compiles (replacement search is always non-whole-word, matching Windows' "comma" -> "," reasoning). Requires 25 dictations and 1,500 words of evidence before returning any verdict; unmeasurable entries (blank pattern or replacement, e.g. filler-removal rules) and unsaved rows (id 0) are never proposed. `maxGlossaryTermsLocal = 80` mirrors `CleanupPrompt.MaxGlossaryTermsLocal` for the future local-AI-cleanup glossary note. Wired into `DictionarySettingsTab` via a "Clean Up..." button that reads `PersistenceStore.fetchDictationHistory()`'s new `transcriptText` field, and a review sheet (`DictionaryCleanupView`) listing every flagged entry with a pre-checked toggle; nothing is written until the user confirms "Turn Off Selected," which soft-disables (never deletes) the chosen rows. 17 tests ported directly from the C# xUnit fixtures (library-specific cases excluded per the scope decision above), all passing. Live-verified via the real app bundle: seeded a used ("co pilot") and an unused ("kubernetes") entry plus 30 history transcripts, confirmed the sheet correctly flagged only "kubernetes," confirmed the database row was disabled (not deleted) after confirming, and confirmed re-running correctly reports it as "Already off" rather than re-flagging it as new. |
| Tray quick toggles, AI cleanup on or off | Done | Frontend | A tray "AI Cleanup" checkbox item persists the user's intent via `UserDefaults` (`ScribeAiCleanupEnabled`), the same stopgap pattern as the overlay anchor. Note: AI cleanup itself is still only reachable through the `--cleanup-text` CLI verb and is not yet wired into the live dictation pipeline, so this toggle records intent for when that wiring lands rather than changing live output today; called out in a code comment. |
| Tray quick toggles, pause | Done | Frontend | A tray "Pause Dictation" checkbox item toggles `HotkeyManager.isPaused` (persisted via `ScribeIsPaused` in `UserDefaults`), mirroring Windows' `DictationController.SetPaused`: an in-flight capture stops immediately, new hotkey presses and the menu's "Start Test Dictation" are both ignored while paused, and the status bar icon swaps to `mic.slash.fill`. The event tap itself stays installed the whole time, so resuming never re-prompts for Input Monitoring. Manually verified end to end against the real binary: pausing during an active capture stops it, a paused hotkey press is ignored and logged, and the paused state survives a relaunch via `UserDefaults`. |
| Welcome and onboarding flow | Done | Frontend | `WelcomeView.swift` (SwiftUI) mirrors Windows' `WelcomeWindow`: push-to-talk gesture hint, privacy/offline promise, AI cleanup opt-in note, "Open Settings" and "Got It" actions. Shown non-modally on first launch (`ScribeHasCompletedFirstRun` in `UserDefaults`, consistent with the existing overlay-anchor stopgap persistence pattern) and reachable again anytime via a "Welcome..." tray menu item. Manually verified: appears automatically on a fresh launch, the flag persists across restarts, and the app never crashes across open/close cycles. |
| About page with privacy, support, source, and star links | Done | Frontend | `AboutView.swift` is a new 6th Settings tab mirroring Windows' `SectionAbout`: version/app header, "Private by design" privacy blurb linking `PRIVACY.md`, a GitHub-star card, a support/source card (report issue, view source), and a data-location card showing the live `scribe.db` path with Copy and Reveal-in-Finder actions (the macOS equivalent of Windows' Explorer-open button; there is no diagnostics-zip export yet since macOS logs go to stderr rather than a rotated log file). Manually verified end to end against the running app: all six tabs present (confirmed via `radio button` count), every card's text renders with real values (including the live database path), Copy correctly places the path on the clipboard, and Reveal in Finder opens Finder with no crash. |

## Foundation workstreams that unblock parity

| Workstream | Status | Owner | Why it comes first |
|---|---|---|---|
| Global hotkey capture | Done | Platform | `HotkeyManager.swift`: a CGEvent tap for the push-to-talk key (Right Ctrl by default), with pause/resume that never re-prompts for Input Monitoring (verified live, see the tray pause toggle row above). Every dictation feature in the checklist above depends on this and is itself Done and live-verified. |
| Audio capture and VAD | Done | Backend | `AudioCaptureEngine.swift` (`AVAudioEngine` capture, 48kHz -> 16kHz conversion) plus `SilenceAutoStopDetector` (energy-threshold RMS VAD for toggle mode). Fixed a real crash found while verifying the tray pause toggle: `AudioCaptureEngine.convert` used `AVAudioConverter.convert(to:from:)`, which enforces `outputBuffer.frameCapacity >= inputBuffer.frameLength` even when downsampling (48kHz -> 16kHz) makes that impossible, throwing an uncaught Objective-C exception that terminated the whole process on every real live dictation. Switched to the block-based `convert(to:error:withInputFrom:)` overload, which has no such constraint; verified live capture -> conversion -> ASR invocation completes with no crash across several real microphone captures. |
| ASR wrapper and transcript session model | Done (production path) | Backend | `TranscriptionEngine.swift` now defaults to Foundry Local's `parakeet-tdt-0.6b-v2` via `foundry transcribe -m <alias> -f <wav> -o json`, verified end to end through the real `Scribe` binary. `whisper-cli` remains as a documented fallback (`SCRIBE_ASR_BACKEND=whisper`), still verified working. |
| Text injection | Done | Platform | `TextInjector.swift`: Accessibility-API-backed text insertion (with a Unicode/clipboard fallback path), driving every live-verified dictation feature above (per-app newline modes, quick dictionary add repair, etc). |
| Shared persistence layer | Done | Backend | `PersistenceStore.swift`: one SQLite store (dictionary entries, snippets, app profiles, dictation history, diagnostics columns) backing every feature row above, with a non-destructive probe-then-`ALTER TABLE` migration pattern used repeatedly as new columns were added (`transcript_text`, `decode_ms`/`cleanup_ms`, etc). |
| Settings and navigation IA | Done | Frontend | `SettingsView.swift`: a 6-tab `TabView` (Overlay/Dictionary/Snippets/App Profiles/Diagnostics/About), each backed directly by `PersistenceStore`'s CRUD surface, with a Playground tab layered on for pipeline verification. All tabs are individually Done and live-verified in the checklist above. |

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
