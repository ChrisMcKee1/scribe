# Scribe 0.5.11

Scribe 0.5.11 improves local AI cleanup behavior with Ollama on this PC, fixes memory and context-limit reporting for Mixture-of-Experts models with speculative draft heads, and updates the cloud and local AI cleanup benchmarks. Existing settings, dictionary, word packs and history carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## Local AI cleanup and Ollama improvements

- **No redundant readying calls when idle release is set to Never:** When **Free memory when Scribe isn't used** is set to **Never** and Ollama or LM Studio already holds the model at the configured context size with its prompt cached, starting a dictation after more than 30 seconds of idle time no longer sends a 1-token readying request.
- **Accurate memory display for speculative-decoding models in Ollama:** For models that bundle an MTP speculative draft head (such as `gemma4:26b-a4b-iq3s` and `gemma4:12b`), Ollama's `/api/ps` reports only the draft head's ~1 GB buffer. Scribe now includes the listed main model weight size when `/api/ps` reports less than one fifth of the model's disk size, so **Settings > AI cleanup > On this PC** reports the real memory footprint (for example, `11.9 GB` instead of `998 MB`).
- **Modelfile `PARAMETER num_ctx` respected:** When reading an Ollama model's maximum context window from `/api/show`, Scribe now caps the architectural context limit by any explicit `PARAMETER num_ctx` configured in the Modelfile, preventing a higher context setting from spilling large MoE KV caches into slow shared system memory.

## Privacy and compatibility

All local-app requests stay on this PC. Audio never leaves the device. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for local-app setup and tuning remain separate.
