# Scribe

A fully offline, push-to-talk voice dictation app for Windows 11. Hold a hotkey, speak,
release, and the transcribed text is inserted at the cursor in whatever app has focus.
**No audio ever leaves the machine.**

Transcription runs on **NVIDIA Parakeet TDT 0.6b v3** (int8) through
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (ONNX Runtime) on CPU.

> Replaces Windows Voice Typing with something private, fast, accurate, and extensible.

## Status

v1 in development — Phase 1 (core dictation loop) + Phase 2 (usability). CPU int8 only;
GPU is deliberately deferred.

## Requirements

| | |
|---|---|
| OS | Windows 11 (x64) |
| SDK | .NET 10 (`10.0.301`+) with the Windows Desktop runtime |
| CPU | Any modern x64 (int8 inference; ~8 decode threads by default) |
| Disk | ~1 GB for the model |

## Tech stack

- **C# / .NET 10, WPF** tray app (`net10.0-windows`, win-x64)
- **sherpa-onnx** (`org.k2fsa.sherpa.onnx`) for ASR; bundles its own ONNX Runtime
- **Parakeet TDT 0.6b v3** int8 (`nemo_transducer`) + **Silero VAD**
- **NAudio** (WASAPI capture, 16 kHz mono)
- **H.NotifyIcon.Wpf** (tray), **Microsoft.Data.Sqlite** (history/dictionary/settings)
- Win32 `WH_KEYBOARD_LL` (push-to-talk hold) + `SendInput` (clipboard-paste injection)

## Project layout

```
Scribe.slnx
  src/Scribe.Core      services + domain (audio, transcription, VAD, post-processing,
                       text injection, hotkeys, persistence, settings)
  src/Scribe.App       WPF tray app: bootstrap + DI, settings window, recording overlay
  tests/Scribe.Core.Tests
  scripts/Download-Models.ps1   fetches the ASR + VAD models into ./models (gitignored)
  Directory.Packages.props      central NuGet version management
```

## Getting started

```powershell
# 1. Download the Parakeet + Silero VAD models (~670 MB) into ./models
pwsh ./scripts/Download-Models.ps1

# 2. Restore + build
dotnet build Scribe.slnx -c Debug

# 3. Run
dotnet run --project src/Scribe.App
```

## Security note

`Microsoft.Data.Sqlite` transitively references a SQLite build affected by
**CVE-2025-6965**. Scribe pins `SQLitePCLRaw.bundle_e_sqlite3` `3.0.3` directly to pull the
patched native (`e_sqlite3` 3.50.3), overriding the vulnerable transitive version.

## Attribution / licenses

- **Parakeet TDT 0.6b v3** — © NVIDIA, CC-BY-4.0
- **sherpa-onnx** — Apache-2.0 (Next-gen Kaldi / k2-fsa)
- **Silero VAD** — MIT
