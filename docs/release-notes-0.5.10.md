# Scribe 0.5.10

Scribe 0.5.10 adds first-class support for **TensorSharp** under **Settings > AI cleanup > On this PC**, automatic tray relaunch after Microsoft Store or Windows updates, and updated model preselection for high-VRAM graphics cards. Existing settings, dictionary, word packs and history carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## TensorSharp on this PC

- **Settings > AI cleanup > On this PC** now offers **TensorSharp** (`http://localhost:5000/v1`) alongside **Let Scribe manage it** (Foundry Local), **Ollama** and **LM Studio**.
- Scribe reads TensorSharp's hosted GGUF model list, loaded state, active hardware backend and context window from `GET /api/models` (verified by TensorSharp's capability fields so other servers on port 5000 stay under **Another AI service**) and model file sizes from `GET /api/tags`.
- **TensorSharp settings** (folded under **On this PC** when TensorSharp is selected) lets you configure:
  - **How TensorSharp runs the model:** `TensorSharp's setting`, `GGML CUDA (NVIDIA graphics card)`, `Direct CUDA (NVIDIA graphics card)`, `GGML Vulkan (graphics card)`, `GGML CPU (processor)` or `Pure C# CPU (processor)`. Scribe loads or switches TensorSharp's active backend through `POST /api/models/load`.
  - **Context size:** `TensorSharp's setting` or `4K` to `128K` tokens, fitted to the smaller of your choice and the window TensorSharp loaded the model with.
  - **Thinking before answering:** `Off (fastest)`, `Low`, `Medium` or `High`, with automatic token headroom when thinking is enabled.
  - **Send your whole vocabulary when it fits:** independent per-app toggle for TensorSharp.
- **Radix prefix-cache optimization:** When running TensorSharp with per-dictation mentioned vocabulary, Scribe keeps the `system` message static across prewarm and dictations and places the mentioned vocabulary in the `user` turn before `<transcript>`, while disabling TensorSharp's built-in agent skill and tool injection (`skills_discovery: false`, `multi_agent: false`, `tool_choice: "none"`). This enables 100% first-run Radix public prefix checkpoint reuse across dictations, including on recurrent GatedDeltaNet models.

## Automatic restart after updates and model preselection

- Scribe now registers with Windows Application Recovery and Restart (`RegisterApplicationRestart` with `RESTART_NO_CRASH | RESTART_NO_HANG`) so Windows Restart Manager relaunches Scribe quietly in the system tray after a Microsoft Store package update or an OS update reboot closes it.
- On PCs with a graphics card that has 12 GB or more of dedicated memory, Scribe's local-app model picker now preselects `Gemma 4 26B` (`gemma4:26b-a4b-iq3s`) or `Gemma 4 12B` when present before falling back to `Gemma 4 E4B` and `Gemma 4 E2B`.

## Privacy and compatibility

All TensorSharp requests stay on this PC (`localhost:5000`). Audio never leaves the device. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for local-app setup and tuning remain separate.
