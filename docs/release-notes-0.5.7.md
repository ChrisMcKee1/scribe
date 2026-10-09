# Scribe 0.5.7

## Smaller local-model preparation requests

When Send your whole vocabulary when it fits is selected for a model on this PC, preparing the model
now sends only a starting part of that vocabulary, at most 4,096 estimated vocabulary tokens. A large
context setting no longer makes preparation read the entire glossary before the dictation can proceed.

Actual dictations still receive their full selected vocabulary in the original format. Your model,
context size, Never memory-retention choice, dictionary and word packs are not silently changed.
Ollama and LM Studio continue to own their model allocation; this change cannot repair a native
model-loader crash.

## Vocabulary compaction stays experimental

CompactLocalGlossary is an opt-in performance experiment, not the default. It groups spoken variants
of the same exact spelling and reduces repeated glossary text. A local 25-case comparison found
repeatable rewriting and vocabulary regressions, so normal cleanup keeps the original format.

UnboundedLocalPreparation brings back the previous preparation budget for comparison. Neither flag
changes which vocabulary a normal dictation is allowed to send.

## Privacy and compatibility

Preparation still goes only to a server on this PC, never to a remote service. Audio never leaves
the device. Existing settings and the storage schema are unchanged. Personal dictionary reviews and
custom word-pack changes are local user choices, not data included in this release.

The Store library-journal desktop gate remains unverified. No macOS behavior is changed by this
Windows release.
