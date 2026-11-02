# Scribe 0.5.17

Scribe 0.5.17 adds a second speech model, for English, warns while you record when the microphone sends no sound, turns off Foundry Local's optional telemetry, and brings Scribe's components up to date. Existing settings, dictionary, word packs and history carry over (`user_version` 7).

## Parakeet Unified for English dictation

- **A second speech model comes with Scribe:** Parakeet Unified 0.6B, from NVIDIA, recognizes English only, and in our tests it got more English words right than the 25-language Parakeet, which stays the default. It adds less punctuation of its own; AI cleanup puts punctuation in if you use it. It recognizes about as fast as the default, on your PC like every other part of dictation.
- **To use it:** in **Settings > Advanced**, choose **Parakeet Unified, English only** as the speech model, save, and restart Scribe.
- **A bigger download, once:** the model takes about 663 MB, so this update and the installers are larger than usual.
- **License:** Parakeet Unified is licensed by NVIDIA Corporation under the NVIDIA Open Model License. The agreement and its Notice are in the model's folder, `models\parakeet-unified-en-0.6b-int8` in Scribe's install folder.

## A warning when the microphone sends no sound

- If the microphone opens but sends no sound in the first second of a recording, the recording indicator now says "No sound from the mic" right away (or a notice does, with the indicator off), instead of you finding out only after you let go. It happened with a USB microphone that had been unplugged and plugged back in. Scribe keeps recording, so sound that starts late is still kept; unplugging the microphone and plugging it back in, or choosing another from the tray's Microphone menu, fixes it.

## Privacy

- **Foundry Local's optional telemetry is off:** Scribe now asks Foundry Local, which runs AI cleanup on your PC, to send no usage, download, catalog, hardware or session events. Foundry Local still sends Microsoft one startup event the first time Scribe starts it in a session, which no setting turns off. Settings, the [privacy policy](../PRIVACY.md) and the README list what it carries: none of your text, vocabulary or audio.
- **One less detail in requests to Microsoft Foundry:** Agent Framework added a code describing which of its features Scribe uses to the User-Agent of each cleanup request. Scribe now removes it, and Settings and the privacy policy list the rest of the User-Agent beside the other headers a request carries.

## Under the hood

- **Updated:** .NET 10.0.14, a security release (0.5.16 shipped 10.0.13), Agent Framework 1.27.0, Microsoft.Extensions.AI 10.11.0, the Windows App SDK 2.6.4 components, NAudio 3.3.0 and OpenTelemetry 1.20.1. Each was measured against 0.5.16 (AI cleanup, speech recognition, the scenario suite, startup and the performance benchmarks) and none changed a result or slowed anything down.
- **Held back:** sherpa-onnx 2.0.0, which lost a dictation in our scenario suite, and Foundry Local 2.3.0, which changes the ONNX Runtime that speech recognition runs on and no longer bundles CUDA.
- **An experiment, off unless you turn it on:** `KeepIdleCleanupConnections` (through `SCRIBE_PERF_FLAGS`) keeps the connection to a remote AI service open between dictations, which saves a new connection when they are 1 to 2 minutes apart.

## Compatibility

Existing settings and the storage schema (`user_version` 7) are unchanged. An older version that finds Parakeet Unified chosen uses the default Parakeet instead.

The Store library-journal desktop gate remains unverified. The macOS port has neither Parakeet Unified nor the warning for a microphone that sends no sound; its speech model and recording rows in `macos/PORTING-PLAN.md` may now be stale.
