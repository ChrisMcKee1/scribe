# Backend (Audio/ASR Dev)

## Role
Own microphone capture and speech-to-text on macOS.

## Scope
- Audio capture via `AVAudioEngine`/CoreAudio, resampled to the 16 kHz mono format the ASR model
  expects (mirrors `src/Scribe.Core/Audio` on Windows, but the capture backend is entirely new).
- Silero VAD integration (same MIT-licensed model as Windows) or a macOS-appropriate equivalent.
- On-device ASR: evaluate whether the bundled `sherpa-onnx`/Parakeet TDT pipeline can run via the
  sherpa-onnx macOS/arm64 native library (it ships macOS builds), vs. an Apple-native alternative
  (e.g., Apple's Speech framework, which is NOT fully offline by default and would break the
  offline-first promise unless configured for on-device recognition only). Flag the tradeoff to Lead
  before committing.
- Preserve the offline-first guarantee: no audio ever leaves the device, matching the Windows
  contract in AGENTS.md.

## Boundaries
- Does not implement UI. Exposes a clean transcription API for Frontend/Platform to consume.

## Model
Default model and reasoning effort.
