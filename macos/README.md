# Scribe for macOS

A native Swift menu bar port of [Scribe](../README.md), Windows' offline push-to-talk dictation
app. Built with Swift Package Manager and bundled into a minimal, ad-hoc-signed `.app` by a shell
script. Feature parity with the Windows app is close (see `PORTING-PLAN.md` for the full,
row-by-row checklist and known gaps); this is a working daily-driver app, not a prototype.

## Requirements

- macOS 13 or later
- Apple Silicon (`arm64`)
- Xcode Command Line Tools or Xcode with `swift` available on PATH
- [Foundry Local](https://github.com/microsoft/homebrew-foundrylocal) for on-device ASR and the
  default AI cleanup provider: `brew tap microsoft/foundrylocal && brew install foundrylocal`
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
- The dictation pipeline in the Windows order: raw speech recognition, optional AI cleanup of that raw
  transcript (with the app profile's writing style, and a one-line request for a terminal), the reply
  checked and its dashes rewritten, then snippets and your dictionary, then line breaks for the target
  app. Your snippet templates are never sent to a cleanup provider, and your dictionary has the last word.
  You can start the next dictation while the last one is still being processed; the text goes in in the
  order you spoke it
- On-device ASR via Foundry Local's `parakeet-tdt-0.6b-v2`, an English model (`TranscriptionEngine.swift`).
  The recognizer runs off the main thread with a deadline and can be cancelled, and the recording it
  reads is a private temporary file that is deleted as soon as it returns
- Capture that belongs to one recording at a time: every input channel is mixed in, so a microphone on
  any input of an interface is heard; a device change ends the recording and keeps what it captured;
  Caps Lock (the default key) and the test dictation are toggles that stop on silence the way Windows does,
  a held key never does; and every recording stops at ten minutes, even if the microphone stops delivering
- Overlay pill with a 9-anchor position picker and live recording/processing state, and a short notice
  that names what went wrong (for example "Cleanup failed, raw text used" or "Not inserted, text kept").
  A notice never covers a recording and never replaces a newer failure; one that cannot be shown waits
  for the pill, and a cleanup fallback or failed transcription that cannot be shown at once is posted as
  a notification instead. No modal alerts while you dictate
- Releasing the key never waits for the recording to be finished off: that happens in the background,
  and dictations are still processed in the order you spoke them
- Quitting waits for a paste in progress to put your clipboard back, and for a running recognizer or a
  Settings or Usage Insights check that started `az` or `foundry` to be stopped, before Scribe exits
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
  reported once, in a notification that opens the right System Settings pane; granting Input Monitoring
  takes effect without a relaunch
- Dictation history written in the background after the text is delivered, in dictation order. It is
  best-effort until committed: a crash in that moment loses the entry. A new install keeps 90 days of
  text, a history from an earlier build keeps everything until a limit is chosen, and a missing or
  unreadable setting never deletes anything. Retention is swept at launch and daily, and freed space is
  reclaimed only while no dictation is running. Settings > History chooses the limit (7, 30, 90 days,
  1 year or Forever) and clears all history after a confirmation, which also empties Recent Dictations

## Known gaps vs. Windows

See `PORTING-PLAN.md` for the authoritative, row-by-row feature checklist. As of this writing the
main outstanding gaps are: the default speech model is English-only; long recordings are transcribed in
one call rather than split on pauses as Windows does; and there is no release packaging/notarization or
auto-update story yet (dev builds are ad-hoc signed for local Accessibility persistence only).
