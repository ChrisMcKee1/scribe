# Scribe for macOS

A native Swift menu bar port of [Scribe](../README.md), Windows' offline push-to-talk dictation
app. Built with Swift Package Manager and bundled into a minimal, ad-hoc-signed `.app` by a shell
script. Feature parity with the Windows app is close (see `PORTING-PLAN.md` for the parity table, the
row-by-row checklist and known gaps). The current code passes CI's builds, unit and scenario tests and
sanitizer runs on macOS 15 and 26, but it has not yet been run on a real Mac: permissions, the
microphone, the push-to-talk key, insertion into real apps and the overlay still need that check (see
Tests below).

## Requirements

- macOS 13 or later
- Apple Silicon (`arm64`)
- Xcode Command Line Tools or Xcode with `swift` available on PATH
- [Foundry Local](https://github.com/microsoft/homebrew-foundrylocal) for on-device ASR and the
  default AI cleanup provider: `brew install microsoft/foundrylocal/foundrylocal`. Use the fully
  qualified name: since Homebrew 6.0, a short name from a third-party tap is refused until the tap is
  trusted (`brew trust`), while installing by the full name trusts that one formula
- Optional: [Ollama](https://ollama.com) as an alternative local AI cleanup provider

## Build

```bash
swift build --package-path macos/Scribe -c release
./macos/Scribe/scripts/build-app.sh release
```

The app bundle is written to:

```text
macos/Scribe/dist/Scribe.app
```

The first time you build locally, run `./macos/Scribe/scripts/setup-dev-signing.sh` once so
rebuilt bundles keep a stable code signature; otherwise macOS re-prompts for Accessibility
permission on every rebuild (see the script's header comment for why).

## Run

From Finder, double-click `macos/Scribe/dist/Scribe.app`, or from Terminal:

```bash
open macos/Scribe/dist/Scribe.app
```

On first launch you'll be asked to grant Microphone, Accessibility and Input Monitoring access (System
Settings > Privacy & Security), and a one-time Welcome window explains the push-to-talk gesture and the
privacy/offline promise.

## What works today

- Menu bar app shell (`NSStatusItem`, background-only via `LSUIElement`) with tray items for test
  dictation, Settings, AI Cleanup/Pause toggles, Recent Dictations, Quick Add to Dictionary,
  Welcome, and Quit
- Global push-to-talk hotkey, real audio capture, and text injection into the app that had focus when the
  recording started. Scribe inserts through Accessibility where it can; otherwise it borrows the clipboard
  only when it is empty or holds plain text, keeps its own copy off Universal Clipboard and marks it so
  clipboard history tools skip it, and puts your text back only if nothing replaced it in the meantime.
  With anything else on the clipboard it types the text instead. If focus moves to another app before or
  while the text is going in, Scribe stops and keeps the dictation for recovery
- The dictation pipeline: raw speech recognition; with AI cleanup on, your dictionary and library
  spellings applied to that transcript, and the result sent for cleanup (with the app profile's writing
  style, and a one-line request for a terminal), the reply checked against what was sent and its dashes
  rewritten, then your snippets and any dictionary entry whose replacement is more than one line,
  longer than 100 characters or holds an em or en dash; with cleanup off, snippets and then your dictionary, as on Windows; then
  line breaks for the target app. A cleanup request contains the dictation with your vocabulary
  corrections applied, never a snippet body or one of those longer replacements, and no rule runs
  twice. If cleanup fails, the text is exactly what it would be with cleanup off. You can start the
  next dictation while the last one is still being processed; the text goes in in the order you spoke
  it
- On-device ASR via Foundry Local's `parakeet-tdt-0.6b-v2`, an English model (`TranscriptionEngine.swift`).
  The recognizer runs off the main thread with a deadline and can be cancelled, and the recording it
  reads is a private temporary file that is deleted as soon as it returns
- Capture that belongs to one recording at a time: every input channel is mixed in, so a microphone on
  any input of an interface is heard; a device change ends the recording and keeps what it captured;
  Caps Lock (the default key) is tapped on and off and stops only when you tap it again, unless you turn
  on "Also stop after a pause" in Settings > Input (off by default, as on Windows, because a pause to
  think would end the dictation); the tray's test dictation always stops after a pause; a held key never
  does; and every recording stops at ten minutes, even if the microphone stops delivering. Scribe only
  listens to Caps Lock and never changes its lock state, so after a dictation that ended some other way
  than your tap (a pause with the setting on, the ten minute limit, a microphone fault, Pause Dictation,
  a change of key or a press Scribe turned away) the Caps Lock light can be out of step with dictation;
  the next tap still starts a new one
- Overlay pill with a 9-anchor position picker and live recording/processing state, and a short notice
  that names what went wrong (for example "Cleanup failed, raw text used" or "Not inserted, text kept").
  A notice never covers a recording and never replaces a newer failure; one that cannot be shown waits
  for the pill, and a cleanup fallback or failed transcription that cannot be shown at once is posted as
  a notification instead. No modal alerts while you dictate
- Releasing the key never waits for the recording to be finished off: that happens in the background,
  and dictations are still processed in the order you spoke them
- Quitting hides the pill at once, then waits for a paste in progress to put your clipboard back, and for a
  running recognizer or a Settings or Usage Insights check that started `az` or `foundry` to be stopped,
  before Scribe exits
- Settings window with Overlay, Input, Dictionary, Libraries, Snippets, App Profiles, AI Cleanup,
  Playground, Diagnostics, Usage Insights, History, and About sections; a change made from the tray
  shows in an open window, Open at Login shows what macOS reports, and no tab waits on the database
  on the main thread
- User dictionary (CSV import/export, history-mined suggestions, unused-entry cleanup), voice
  snippets, and per-app profiles (writing style + newline mode by focused app)
- AI cleanup across four providers: Foundry Local (default), managed Ollama, any
  OpenAI-compatible endpoint, and Microsoft Foundry cloud (Azure CLI or service-principal auth,
  secrets in Keychain, an https resource or pasted Foundry project URL works). Each provider is
  built once per configuration and reused across dictations, Test Connection sends a real
  cleanup request for a test word and passes only if the model answers with text, and em and en
  dashes are rewritten out of the model's answer
- Diagnostics (P50/P95 decode latency, real-time factor) and Usage Insights (totals, trend chart,
  top apps, recurring terms with one-click dictionary add, opt-in AI summary)
- Dictation recovery: last 5 transcripts survive both the current run and an app restart (seeded
  from persisted history), in a Recent Dictations submenu that fills itself as it opens, plus a
  notification with Copy Transcript for a dictation that did not go in. After Clear history neither
  an entry already on show nor an earlier notification copies the deleted text
- Startup problems (a database that could not be read, missing Input Monitoring or Accessibility) are
  reported once, in a notification that opens the right System Settings pane. Scribe tries the
  push-to-talk key again whenever it becomes active or its menu opens, so granting Input Monitoring may
  take effect without a relaunch; that has not been checked on a real Mac yet, so if the key still does
  nothing, quit and reopen Scribe
- Dictation history written in the background after the text is delivered, in dictation order. It is
  best-effort until committed: a crash in that moment loses the entry. A new install keeps 90 days of
  text, a history from an earlier build keeps everything until a limit is chosen, and a missing or
  unreadable setting never deletes anything. Retention is swept at launch and daily, deleted text is
  overwritten in the database rather than left in its freed space, and freed space is reclaimed only while
  no dictation is running. Settings > History chooses the limit (7, 30, 90 days,
  1 year or Forever) and clears all history after a confirmation, which also empties Recent Dictations and
  the Playground's last dictation and closes an open Quick Add window
- Scribe removes only what it made: the private recording it hands the recognizer, as soon as the recognizer
  returns, and any a crash left behind, at the next launch. It never deletes Foundry Local's or Ollama's model
  caches, which you installed and which other apps share

## Tests

```bash
swift test --package-path macos/Scribe --parallel
```

runs both test targets, each test in a worker process of its own; CI runs them the same way on macOS 15 and 26
and under the thread and address sanitizers, fails on any compiler warning, and builds the app bundle, lints its
Info.plist, verifies its signature and runs its library listing from inside it.

- `Tests/ScribeTests`: the unit tests. Every suite uses a defaults suite, Keychain service, temporary directory
  and pasteboard of its own, so no test reads or changes your settings or credentials.
- `Tests/ScribeScenarioTests`: headless scenarios on the committed speech fixtures in `tests/fixtures/speech`
  (the phrases of `fixtures.json` and `scenario-fixtures.json`, which the Windows scenario suite also uses). They
  play the fixtures through the real capture engine on scripted devices at 16, 44.1 and 48 kHz, mono and stereo,
  with the voice on either channel; run silence auto-stop on room tone, steady noise, a quiet microphone and speech
  followed by silence; drive the whole dictation pipeline with a stand-in recognizer that answers with each
  fixture's text; and read and sweep 50,000 rows of history. They need no microphone, recognizer, permission or
  network. They find the fixtures from their own source path; `SCRIBE_FIXTURES_DIR` points them at another copy,
  and they are skipped when there are none. Their timings and levels are printed, and written to the folder
  `SCRIBE_SCENARIO_REPORT_DIR` names when it is set.
- With Foundry Local and its model installed,
  `SCRIBE_REAL_ASR=1 swift test --package-path macos/Scribe --filter RealRecognizerScenarioTests` transcribes the
  short fixtures with the real recognizer and holds each asserted phrase to Windows' word-overlap bar. The
  workflow's optional real speech recognition job does the same on a hosted runner, only when it is dispatched by
  hand; it fits one, with the model taking about 700 MB of disk and Foundry Local about 1.1 GB of memory.

The runners cannot grant Microphone, Accessibility or Input Monitoring access and have no screen to look at, so the
event tap, a real microphone, insertion into real apps and the menu bar and overlay UI still need a real Mac.

## Formatting

`macos/Scribe/.swift-format` is the style: four-space indentation and 120 columns. Format with the Swift 6.1
toolchain's formatter (Xcode 16.4), which CI's style job lints with, strictly:

```bash
swift format format --in-place --recursive --configuration macos/Scribe/.swift-format \
    macos/Scribe/Sources macos/Scribe/Tests
swift format lint --strict --recursive --configuration macos/Scribe/.swift-format \
    macos/Scribe/Sources macos/Scribe/Tests
```

## Known gaps vs. Windows

See `PORTING-PLAN.md` for the parity table and the authoritative, row-by-row feature checklist. As of this writing
the main outstanding gaps are: the default speech model is English-only; long recordings are transcribed in one
call rather than split on pauses as Windows does; there is no voice activity detection trimming the capture before
recognition; AI cleanup is sent no glossary of your dictionary terms (Windows sends up to 5,000 with every cloud
request; whether macOS should is an open decision, because it changes what leaves the Mac); and there is no release
packaging/notarization or auto-update story yet (dev builds are ad-hoc signed for local Accessibility persistence
only).
