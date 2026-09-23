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

On first launch you'll be asked to grant Microphone and Accessibility access (System Settings >
Privacy & Security), and a one-time Welcome window explains the push-to-talk gesture and the
privacy/offline promise.

## What works today

- Menu bar app shell (`NSStatusItem`, background-only via `LSUIElement`) with tray items for test
  dictation, Settings, AI Cleanup/Pause toggles, Recent Dictations, Quick Add to Dictionary,
  Welcome, and Quit
- Global push-to-talk hotkey, real audio capture, and text injection into the focused app. Scribe
  inserts through Accessibility where it can; otherwise it borrows the clipboard only when it is empty
  or holds plain text, keeps its own copy off Universal Clipboard and marks it so clipboard history
  tools skip it, and puts your text back only if nothing replaced it in the meantime. With anything
  else on the clipboard it types the text instead. If focus moves to another app while the text is
  going in, Scribe stops and keeps the dictation for recovery
- On-device ASR via Foundry Local's `parakeet-tdt-0.6b-v2` (`TranscriptionEngine.swift`)
- Overlay pill with a 9-anchor position picker and live recording/processing state
- Settings window with Overlay, Input, Dictionary, Libraries, Snippets, App Profiles, AI Cleanup,
  Playground, Diagnostics, Usage Insights, and About sections; a change made from the tray shows in an
  open window, and Open at Login shows what macOS reports
- User dictionary (CSV import/export, history-mined suggestions, unused-entry cleanup), voice
  snippets, and per-app profiles (writing style + newline mode by focused app)
- AI cleanup across four providers: Foundry Local (default), managed Ollama, any
  OpenAI-compatible endpoint, and Microsoft Foundry cloud (Azure CLI or service-principal auth,
  secrets in Keychain)
- Diagnostics (P50/P95 decode latency, real-time factor) and Usage Insights (totals, trend chart,
  top apps, recurring terms with one-click dictionary add, opt-in AI summary)
- Dictation recovery: last 5 transcripts survive both the current run and an app restart (seeded
  from persisted history), plus an injection-failure recovery notification

## Known gaps vs. Windows

See `PORTING-PLAN.md` for the authoritative, row-by-row feature checklist. As of this writing the
main outstanding gaps are: an energy-threshold silence detector instead of a trained VAD, and no
release packaging/notarization or auto-update story yet (dev builds are ad-hoc signed for local
Accessibility persistence only).
