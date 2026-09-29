# Platform — History

## Project Context
Scribe macOS port. Owns porting Scribe.Core logic + building macOS hotkeys, text injection,
persistence paths, and permission flow (Accessibility + Microphone). See Lead's history.md for full
project context.

## 2026-08-24

- Added `HotkeyManager.swift` with a global `CGEventTap` push-to-talk path that defaults to Right
  Option, requests Input Monitoring access, and drives the shared `AudioCaptureEngine` start and
  stop flow directly.
- Added `TextInjector.swift` with Accessibility-based focused-element insertion plus a
  pasteboard-and-Command-V fallback, then wired the menu bar stop action to inject a fixed test
  string for headless verification.
- Updated the macOS app startup flow to prompt for Accessibility trust, keep the menu test path,
  persist capture summaries, and log distinct permission failures for microphone, input monitoring,
  and accessibility.


## 2026-09-25 - PR #81 macOS overhaul review
Reviewed upstream PR #81 from a platform-validation perspective. Local build, test, and format validation passed earlier, and the remaining issues were narrow follow-ups: CI sanitizer matrix and push-trigger coverage, the unbounded shutdown wait in `TextInjector.waitUntilIdle()`, the macOS `PRIVACY.md` section missing from the branch, Keychain accessibility flag choice, and the clipboard-restore semantic change. Outcome: no blocking platform defects, review only, no code changes from this session.
