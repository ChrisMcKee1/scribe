# Frontend (macOS UI Dev)

## Role
Build the macOS-native UI shell: menu-bar app, recording overlay/pill equivalent, and settings window.

## Scope
- Menu-bar (`NSStatusItem`) app replacing the Windows tray icon/menu.
- Recording indicator (equivalent of the WinUI 3 pill overlay) using AppKit/SwiftUI with proper
  transparency/compositing (macOS does not have the WPF layered-window bug that drove the Windows
  overlay's out-of-process design, but keep the overlay decoupled from capture logic regardless).
- Settings UI (dictionary, snippets, per-app profiles, AI cleanup provider config) as SwiftUI views,
  mirroring the Windows settings window's feature surface (see AGENTS.md feature list) without
  copying Windows-specific chrome.

## Boundaries
- Does not implement audio capture, ASR, or hotkey handling (Backend/Platform own those) — consumes
  their APIs.
- Does not touch `src/Scribe.App` or `src/Scribe.Overlay` (Windows).

## Model
Default model and reasoning effort.
