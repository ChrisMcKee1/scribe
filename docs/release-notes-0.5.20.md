# Scribe 0.5.20

Scribe 0.5.20 is a Windows reliability patch for dictation delivery, settings saves, word packs and optional Microsoft Foundry speech. Existing settings, dictionary, word packs, snippets and history carry over. The database schema remains at `user_version` 7.

## Dictation and typing

- Recheck the target window before retrying a partial typed insertion.
- Treat a timed-out edit-control insertion as uncertain delivery instead of resending the entire dictation and risking duplicate text. The recording indicator and recovery notice explain that uncertainty.
- Check that a clipboard owner responds before borrowing plain text, as well as when keeping formatted clipboard content.
- Keep offline recognition independent of optional cloud-preparation reads. A cloud preparation failure can fall back to the local speech model.
- Pin speech-model downloads to the original upstream revision so fresh builds and in-app downloads keep receiving the verified weights after upstream changes.

## Settings and word packs

- Apply committed settings even when a later word-pack repair cannot finish, including changes to history retention.
- Preserve newer tray choices and edits made while Save is waiting. Apply stored settings rather than an editing draft.
- Retry settings application under one process-owned owner, including after Settings closes. A vocabulary-only recovery no longer reports that all settings were applied.
- Track the exact word-pack Save through journal recovery and adoption, avoiding duplicate packs and false saved acknowledgements.
- Preserve actionable word-pack warnings and report an application failure once.

## Microsoft Foundry

- Make cloud-speech credentials accessible without turning AI cleanup on, changing where it runs or hiding its current setup.
- Correct subscription discovery progress, failures, stale results and explicit All-subscriptions choices. Changing the tenant resets that explicit choice.
- Refuse a selected blank API key instead of silently using Azure CLI sign-in.
- Keep connection-test negotiation separate from the active cleanup configuration. A speech-only test does not send unrelated authored cleanup instructions.
- Recheck word-pack AI permission at every cloud-speech send, including fallback requests. Do not follow redirects, and use one 25-second deadline for sign-in, requests and response reading.
- Fall back to local recognition for an unrecognized cloud-speech response instead of accepting an empty dictation.

## Diagnostics, privacy and migration

- Remove profile names from debug logging and improve settings-application failure reporting.
- Clarify that default speech recognition is offline, optional MAI-Transcribe-2 sends audio and permitted vocabulary to Microsoft Foundry, and AI cleanup itself sends no audio.
- Require a complete backup and verified data transfer before uninstalling a Store-only installation when moving to the direct installer.

## macOS source changes and known limitations

This tag also includes pending macOS source repairs for AI-excluded word-pack replacements, cleanup permission scope, truncated cleanup responses, unreadable built-in edits, credential drafts, Quick Add consistency and typed storage diagnostics. The Windows release does not provide a macOS installer.

Two known macOS issues are not fixed by this patch: overlapping file reads can restore a stale word-pack AI permission after an external edit, and permission-ownership tests do not assert every barrier timeout result. These are explicitly deferred and must not be read as a claim of macOS privacy-boundary correctness or native validation.

The Store build's word-pack journal desktop gate remains unverified. Cross-publishing an Arm64 package is not evidence that it ran on Arm64 hardware. Windows keeps its existing offline defaults and optional online choices; no new remote feature is enabled by this patch.
