# Scribe for macOS

This directory contains the first walking skeleton for the macOS port. It is a native Swift menu bar app built with Swift Package Manager and bundled into a minimal `.app` by a shell script.

## Requirements

- macOS 13 or later
- Apple Silicon (`arm64`)
- Xcode Command Line Tools or Xcode with `swift` available on PATH

## Build

```bash
swift build --package-path macos/Scribe -c release
./macos/Scribe/scripts/build-app.sh release
```

The app bundle is written to:

```text
macos/Scribe/dist/Scribe.app
```

## Run

From Finder, double-click `macos/Scribe/dist/Scribe.app`, or from Terminal:

```bash
open macos/Scribe/dist/Scribe.app
```

## What works today

- Menu bar app shell (`NSStatusItem`)
- Background-only app bundle (`LSUIElement`)
- Menu items for test dictation, settings, and quit
- Microphone permission request on launch
- SwiftUI settings window stub

## Not implemented yet

- Real audio capture
- Global hotkey handling
- ASR, VAD, or text injection
- Dictionary, snippets, profiles, AI cleanup, diagnostics, or persistence

Those are follow-on tasks for Backend, Platform, and Frontend.
