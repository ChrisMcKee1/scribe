# Scribe 0.5.6

Scribe 0.5.6 fixes misleading Settings feedback and improves recovery when a local AI model or the
recording indicator cannot start. Existing settings, dictionary, word packs and history carry over.
Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## Settings and model memory

- **Copy directly from Add to dictionary.** Save and copy is in the fixed footer, not hidden inside
  the scrolling content. It saves a pending correction, copies the latest corrected dictation and
  keeps the window open. Copy dictation is available after saving. A failed save copies nothing;
  a clipboard failure can be retried without saving the word again.
- **Try dictation shows real unsaved changes.** It now uses the same change check as the Settings
  footer and Save. Word packs withheld from AI cleanup no longer make a freshly opened window look
  unsaved. Its shortcut instructions reflect the settings Scribe is running with.
- **Choose how long models stay loaded.** Free memory when Scribe isn't used offers Never and every
  five minutes from 5 to 60, on AI cleanup under On this PC and on Advanced. These are two views of
  one shared setting for speech and AI models. Save applies the choice; existing custom times and
  the 10-minute default are preserved.
- **Never keeps Ollama loaded.** Each new request asks Ollama to keep its model indefinitely.
  Another app or restarting Ollama can still free it. LM Studio keeps its own policy with Never.
  Free memory, pause, turning cleanup off and changing models still work.
- **Dictation waits for local preparation.** After Free memory, Scribe checks residency and starts
  the local model again. It also waits for the instructions to finish readying on an already
  resident model, and rechecks if Free memory landed after recording started.

## Bounded recovery and clearer failures

- **Failed preparation does not start another long retry.** A completed local-app load failure or
  preparation timeout keeps that dictation as heard, instead of starting a fresh cleanup call
  behind the failed load. A later recording checks again. Preparation waiting also counts toward
  the existing total cleanup deadline. The 30-second local-start wait is unchanged; a model that
  needs longer can still leave that dictation without AI cleanup.
- **Local server failures stop the remaining chunks.** If Ollama or LM Studio returns a server
  error during cleanup, the remaining parts stay as dictated. The reason names the local app and
  says to check it, rather than promising the failure is transient. Scribe does not change your
  model, context size or GPU settings to guess at a fix.
- **One automatic reconnect after a temporary connection-check failure.** Microsoft Foundry and
  another AI service can reconnect at the next recording after a 30-second backoff. There is no
  idle polling. A failed automatic attempt needs a manual retry in Settings. Cancellation,
  timeouts, sign-in/configuration errors and recognized model-load failures do not trigger it;
  Foundry Local downloads and GitHub Copilot are not automatically retried this way.
- **Recording-indicator failures have a tray fallback.** A failed helper opens one feedback episode.
  Problem routing follows actual availability, not just the enabled setting, and failed queued
  deliveries can fall back without covering a newer recording. Recovery settles silently only
  after a replacement survives the stability window. Startup stages and hexadecimal exit codes
  make native failures easier to investigate.

These changes improve Scribe's response to failures; they do not repair Windows compositor/driver
faults or Ollama's own model-runner failures. A connected helper that stays alive is not proof that
Windows rendered it correctly.

## Compatible dependency and privacy updates

The compatible AI package group moves to Agent Framework 1.24.0, Extensions.AI 10.10.0 with its
OpenAI adapter at 10.10.1, and OpenAI 2.14.0. NAudio, OllamaSharp, OpenTelemetry and Velopack also
receive stable updates. Speech-model, native speech-runtime, SQLite and Windows App SDK pins stay
unchanged.

Copilot SDK remains at the compatible version Agent Framework brings in. Its newer patch releases
force additional runtime file logging whose contents and retention are not yet established; that
privacy change is not enabled by this release.

Requests made through the OpenAI client also carry client language/version, operating system,
processor architecture and runtime name/version headers to the same AI service. Settings, README
and the privacy policy disclose this metadata.

Each Copilot cleanup uses a fresh session. Scribe attempts bounded detach and explicit deletion of
its local session state after success, failure or cancellation, with a separate bounded owner for
the shared client. Cleanup is best-effort: local state can remain when it fails, and it does not
erase anything GitHub retains under its own policy.

The official Agent Framework samples and released APIs were reviewed. Whole-agent decorators do
not expose the checkpoint between Copilot session creation and transcript sending, and the
experimental Responses-only storage helper does not cover Scribe's cache and multi-API privacy
controls. Those required boundaries remain explicit.

## Measured prefix caching

The fresh [Ollama prefix-cache check](local-model-benchmark.md#october-8-prefix-cache-check) used
Gemma 4 12B and public synthetic dictations. With the model already loaded, a stable instruction
prefix answered in 0.317 seconds at the median against 0.708 seconds when the prefix was changed.
Ollama explicitly reported about 1,084 cached input tokens in the stable-prefix arm, and zero
in the changed-prefix arm. The eight outputs were unchanged between the arms and all passed the
production response guard. These are backend measurements, not full dictation latency or a new
general quality score.

This confirms prefix reuse without making rewrites stateful. Keeping the model loaded avoids a
separate loading cost; readying can move that work into recording time, but does not make it free.
Microsoft Foundry's prompt cache remains a separate, already-on choice from response storage.
No new inference-cache switch or unsupported runtime setting is introduced.

## Platform scope

These Windows changes are not verified as parity fixes for the native macOS port. Live local/cloud
AI services, recording-indicator rendering and real microphone behavior remain separate runtime
checks; x64 validation does not prove Arm64 behavior.
The Store build's word-pack journal remains unverified on a live Store desktop.
