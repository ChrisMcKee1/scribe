# Scribe 0.5.5

Scribe 0.5.5 makes choosing and downloading an Ollama model easier, fixes a reasoning-model failure,
and adds optional per-app delivery preferences. Existing settings, dictionary, word packs and history
carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## Ollama models, downloads and readiness

- **Download a model from Settings.** AI cleanup, On this PC, Ollama now has Download another model,
  a link to the public catalog, progress and cancellation. Enter a bare name, a publisher/name:tag
  such as `VicRodger27/Writex:4b`, or paste its `ollama run` or `ollama pull` command. Scribe takes
  only the model name, never executes the pasted command, and refuses extra options and cloud tags.
  The download uses the internet but sends no dictation, writing style or vocabulary.
- **Use a downloaded model without restarting Scribe.** After downloading, Scribe refreshes the list
  and asks whether to use it. Use model saves that choice immediately, preserving other unsaved
  Settings edits. The AI cleanup status then says when the model is ready; saving alone is not proof
  that it can answer.
- **Missing tags are explained.** An untagged name requests `latest`. Some publishers, including
  Writex at the time of this release, publish only explicit tags. Ollama's missing-manifest stream error
  now points to the catalog's exact model and tag instead of looking like a disk or device failure.
- **Reasoning that continues despite "off" gets a bounded allowance.** The tested DeepSeek-R1 build
  loaded on the GPU but consumed the old answer allowance before returning any visible rewrite.
  When the short check exhausts its allowance without usable text, Scribe validates one fixed,
  synthetic rewrite with more room. Only a successful check enables up to 2,048 extra answer tokens
  for that configuration. Every request is fitted to the context, and the context setting never grows
  silently. The larger check uses the normal dictation deadline. Fast models retain their old limits;
  unusable checks stay unavailable, with dictation working without AI cleanup. Thinking models can
  still be too slow or need more than this bounded allowance on a complex dictation.
- **A reported cut answer keeps your words.** If the AI service reports that it cut off or filtered
  its answer, Scribe keeps that part as dictated and reports why, rather than typing a partial rewrite.
  A model using the extra reasoning allowance must also explicitly report that its answer finished.
  This check covers dictation rewrites; dictionary suggestions and usage insights keep their existing
  answer checks.
- **Failure details name the model that was actually selected.** Ollama and GitHub Copilot failures
  no longer show the unused Foundry Local model.

## Starting Ollama

**Start Ollama** uses the installed Ollama app to start a local-only server, then becomes
**Stop Ollama** for that instance. Scribe stops only the process it started and its descendants,
including when Scribe closes, updates or is force-closed. An Ollama instance started outside Scribe
is left alone. Closing Settings cancels observation, not a Start or Stop already requested.

## Optional app-aware delivery

Dictation, Typing adds **Use app-aware formatting**, off by default. With it on, an app profile can
choose Plain text or Markdown source, plus its own typing/paste and line-break preferences.
Both formats preserve the supplied text literally. This release does not infer lists, create links,
generate code fences, send rich HTML or CSS, or add a second AI rewrite.

The tray's **Plain text once** action applies to the next accepted recording and expires after
60 seconds. It does not skip speech recognition, AI cleanup, the dictionary or snippets.
With app-aware formatting off, existing delivery behavior is unchanged.

## Prompts and platform scope

The shipped writing style and short/detailed cleanup instructions are unchanged. The Gemma 4 12B
prompt comparison did not establish a quality gain from changing them; its evidence is in the
[local model benchmark](local-model-benchmark.md).

These controls and compatibility fixes are Windows changes. The native macOS port does not yet
mirror the Ollama download/process controls, conditional reasoning allowance, cut-answer handling or
app-aware delivery.
The Store build's word-pack journal remains unverified on a live Store desktop.
