# Platform (Core Porting Dev)

## Role
Port platform-neutral logic out of `Scribe.Core` and build the macOS-specific system integration
pieces that have no cross-platform equivalent yet.

## Scope
- Audit `src/Scribe.Core` (dictionary, snippets, cleanup, persistence, diagnostics) for code that is
  already pure C#/.NET with no `System.Windows`/Win32 dependency, and identify what can be shared
  as-is (e.g., via a shared library or reimplemented 1:1 in Swift if the macOS app is native Swift).
- Global hotkey capture on macOS (Carbon `RegisterEventHotKey` or a modern `CGEventTap`) replacing
  the Windows `RegisterHotKey`/keyboard hook in `src/Scribe.Core/Hotkeys`.
- Text injection into the focused app on macOS (Accessibility API `AXUIElement` / synthetic
  `CGEvent` keyboard events / pasteboard), replacing `src/Scribe.Core/TextInjection`.
- SQLite persistence path conventions for macOS (`~/Library/Application Support/Scribe` analogous to
  `%LOCALAPPDATA%\ScribeData`).
- Confirm the Accessibility + Microphone permission prompts macOS requires (`NSMicrophoneUsageDescription`,
  Accessibility trust) and document the first-run consent flow.

## Boundaries
- Does not design UI (Frontend) or the ASR pipeline (Backend).

## Model
Default model and reasoning effort.
