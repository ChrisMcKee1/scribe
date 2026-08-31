# Lead — History

## Project Context
Scribe: private, fully offline push-to-talk voice dictation. Existing shipped product is Windows 11
only (WPF tray app + WinUI 3 overlay process, .NET 10, sherpa-onnx Parakeet TDT ASR on CPU). User
(x3nc0n) wants a **new macOS port** to keep testing on macOS. No prior macOS work exists in this repo —
this is greenfield.

## 2026-08-24 — Team cast
Squad hired for the macOS port: Lead, Frontend (macOS UI Dev), Backend (Audio/ASR Dev), Platform
(Core Porting Dev), Tester, plus built-ins (Scribe, Ralph, Rai, Fact Checker). Descriptive naming
used (no themed universe requested).

## 2026-08-24 - macOS walking skeleton scaffolded
Built the first macOS shell under `macos/Scribe` as a standalone Swift Package Manager executable targeting macOS 13+ on Apple Silicon. Added a menu bar app with `NSStatusItem`, placeholder menu actions, a SwiftUI settings window stub, startup microphone permission request, and `scripts/build-app.sh` to bundle the executable into `macos/Scribe/dist/Scribe.app` with `LSUIElement` and `NSMicrophoneUsageDescription`.

Verified on this Mac: `swift build --package-path macos/Scribe -c release` succeeds, the bundle script produces an arm64 `.app`, and `open macos/Scribe/dist/Scribe.app` launches a background-only `Scribe` process that stays running until terminated. Could not directly confirm the rendered menu bar icon because this session does not have assistive access for UI scripting.

## 2026-08-24 - parity master plan and cleanup benchmark setup
Created `macos/PORTING-PLAN.md` as the master parity checklist for the macOS port, mapping every shipped Windows feature to a primary owner and a macOS implementation approach. Locked the architecture direction on two major points: keep Parakeet TDT via sherpa-onnx as the primary macOS ASR path for behavioral parity, and model cleanup through a Swift `CleanupProvider` layer with OpenAI-compatible, Microsoft Foundry, and managed Ollama implementations.

Prepared a live local-model benchmark environment on this Apple Silicon Mac. `brew install ollama` succeeded and the Ollama daemon started, but every candidate model pull timed out within the time box, so no cleanup requests or quality scoring completed. Captured the methodology, exact prompt, attempted models, and repro commands in `macos/CLEANUP-MODEL-BENCHMARK.md`, and recorded the resulting decisions in `.squad/decisions/inbox/lead-macos-parity-plan.md`.
