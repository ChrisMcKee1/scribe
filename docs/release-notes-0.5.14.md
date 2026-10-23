# Scribe 0.5.14

Scribe 0.5.14 adds **Copilot+ PC NPU** optimizations for Foundry Local AI cleanup, optional **MAI-Transcribe-2** cloud speech recognition in direct installs (`Scribe-win-x64-Setup.exe` and `Scribe-win-arm64-Setup.exe`, or with `SCRIBE_PERF_FLAGS=MaiTranscribeSpeech`), installer-aware feature gating for Microsoft Store compliance, and the full-context vocabulary and **Learn from history** improvements from 0.5.13. Existing settings, dictionary, word packs and history carry over (`user_version` 7).

## Copilot+ PC NPU optimizations (Microsoft Store and direct download)

- **Windows ML NPU execution providers (`QNN`, `OpenVINO`, `Vitis AI`):** When setting up Foundry Local on a Copilot+ PC with a neural processing unit (Qualcomm Hexagon, Intel AI Boost or AMD XDNA) and without an 8 GB+ NVIDIA RTX graphics card, Scribe starts first-time setup on `Phi-4 Mini` (`phi-4-mini`).
- **Automatic `-npu` to CPU fallback:** `FoundryModelVariant` and `TextCleanupService` now recognize `-npu` model variants (`<model>-qnn-npu`, `<model>-openvino-npu`, `<model>-vitis-npu`) and automatically unload and fall back to the family's `-generic-cpu` / `-cpu` build for the session if an NPU build fails to load or fails its readiness check.
- **Native Copilot+ PC diagnostics:** `ComputeCapabilityReport` identifies native Copilot+ PC NPU configurations alongside emulation detection.

## MAI-Transcribe-2 cloud speech recognition (direct download and flag-gated)

- **60-language speech recognition with vocabulary biasing:** In direct installs (`Scribe-win-x64-Setup.exe` and `Scribe-win-arm64-Setup.exe`) or when `SCRIBE_PERF_FLAGS=MaiTranscribeSpeech` is set, **Settings > Advanced > Speech model** offers **MAI-Transcribe-2, 60 languages (Microsoft Foundry, preview)**.
- **Shared Microsoft Foundry sign-in (Azure CLI, app registration / service principal, or API key):** `MAI-Transcribe-2` reuses the Microsoft Foundry credentials and resource address from **Settings > AI cleanup** (with an optional separate **Speech resource address** override and **Clean** vs. **Verbatim** style picker), sends `"model": "MAI-Transcribe-2"` automatically in `enhancedMode`, biases recognition with up to 500 terms from your dictionary and AI-permitted word packs (`phraseList.phrases`), and automatically falls back to Azure Fast Transcription in regions where `enhancedMode` is not yet active or to offline **Parakeet** on this PC if offline.
- **Dictation and Advanced setup UX:** **Settings > Dictation** now includes a **Speech model** card showing the active speech model, live Microsoft Foundry authentication readiness, and one-click navigation to **Advanced** and **AI cleanup**, paired with the step-by-step screenshot guide in [`docs/mai-transcribe-setup.md`](mai-transcribe-setup.md).

## Frontier context sizing and Learn from history

- **Whole vocabulary works with the service's default context setting:** Checking **Send your whole vocabulary when it fits** on **Microsoft Foundry**, **GitHub Copilot**, or **Another AI service** activates full-context cleanup and **Learn from history** (up to 1,048,576 tokens) even when **Context size** is left on the service's default setting.
- **Local glossary compaction stays local-only:** `SCRIBE_PERF_FLAGS=CompactLocalGlossary` remains scoped strictly to recognized local runtimes (`FitsLocalContext`), keeping remote and unrecognized servers on the standard glossary format.

## Privacy and compatibility

In the Microsoft Store package, speech recognition stays 100% offline on this PC by default (`InstallerFeatureGate` keeps `MAI-Transcribe-2` and experimental Windows AI Speech NPU APIs off unless `SCRIBE_PERF_FLAGS=MaiTranscribeSpeech` is explicitly set). AI cleanup never sends audio. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for speech model selection, Copilot+ PC NPU variant handling, remote context sizing and full-context dictionary suggestions may now be stale.
