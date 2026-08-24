# Backend — History

## Project Context
Scribe macOS port. Owns audio capture + VAD + ASR on macOS, evaluating sherpa-onnx macOS/arm64
native builds vs. Apple Speech framework (must stay fully offline). See Lead's history.md for full
project context.

## 2026-08-24
- Built the first real macOS dictation foundation shell: `AudioCaptureEngine` now uses
  `AVAudioEngine` plus `AVAudioConverter` to tap the default microphone and emit 16 kHz mono
  Float32 buffers with live peak and RMS dBFS readings.
- Wired the menu bar app's test action into start and stop capture, logging live levels plus final
  duration and sample count, instead of showing the placeholder alert.
- Added `PersistenceStore`, which creates `~/Library/Application Support/Scribe/scribe.db` on app
  launch and records one `dictation_history` row after each completed test dictation.
- Added a temporary local ASR bridge for the macOS port: `TranscriptionEngine` now shells out to
  `whisper-cli` with a local `ggml-tiny.en` model, can transcribe a WAV fixture from the command
  line, and the menu bar test dictation flow now logs and injects the real transcript instead of a
  hard-coded string.
- Attempted the preferred sherpa-onnx plus Parakeet route first by installing the Python package
  and starting the exact Windows-model download, but the 652 MB encoder transfer exceeded the
  agreed five-minute time-box on this machine, so I switched to the documented whisper stopgap to
  land a working end-to-end milestone without stalling.
