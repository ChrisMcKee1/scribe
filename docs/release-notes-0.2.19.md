# Scribe 0.2.19

Dictation into chat apps no longer sends your message early, text arrives in whole words instead of
torn fragments, Scribe now writes AI model names the way you'd type them, and picking an Azure model
fills in Microsoft's recommended Foundry project endpoint.

## New: AI vocabulary libraries

Two dictionary libraries ship with this release, in **Dictionary > Libraries**, and both are **on by
default for a new install**:

- **AI Model Names**: frontier and open-weight model names in the spoken decimal forms dictation
  actually produces. Say "GPT five six Terra" or "GPT five point six Terra" and get `GPT-5.6-Terra`.
  Covers the GPT-5.x and GPT-4.x families, Claude (including the common "cloud" mishear), Gemini and
  Gemma, Qwen ("when"), Llama, Mistral, DeepSeek, Phi, and the speech models.
- **AI and Machine Learning Terminology**: RAG, retrieval, prompting, agents, architecture,
  fine-tuning, quantization, serving, evaluation, and speech vocabulary with the casing and
  hyphenation these terms are written with.

They are the only libraries on by default. The platform packs (Azure, GitHub, .NET, and the rest)
stay opt-in, because they are opinionated about a stack you may not use. Upgrading never changes
your selection: if you already had libraries turned on, or deliberately had them all off, that is
left exactly as it was. Every library can be switched on or off individually at any time.

Model version numbers were the gap worth closing. A dictionary can only cover models that existed
when it was written, so the default writing style was also taught the *shape* of a model identifier:
it now normalizes names it has never seen, such as an unreleased "GPT five point seven Atlas", by
following the pattern of the ones it knows. Measured on the benchmark suite, that took the
model-identifier cases from 78 to 99.

The dictionary page now reports how many entries are enabled, and says so plainly when you are over
the glossary limit that AI cleanup receives. That limit was previously invisible. It caps only the
glossary sent to the model; local replacement has never been capped and still covers every entry.

## Fixed: Azure cleanup failures now name the actual problem

Every Azure cleanup failure produced the same message: "check that you're signed in (az login)".
That sent at least one person chasing a sign-in problem for days when the real fault was a 403, with
the right endpoint and the right deployment but no data-plane role assignment, under an
authentication mode that never calls the Azure CLI at all.

The message now reflects the HTTP status and the configured authentication mode:

- **401** names the credential that was rejected.
- **403** says access was denied rather than unreachable, and names the roles to assign
  (`Cognitive Services User` or `Foundry User` for a Foundry resource,
  `Cognitive Services OpenAI User` for an Azure OpenAI account).
- **404** names the deployment that could not be found instead of blaming credentials.
- **429** identifies throttling.
- Service principal mode never suggests `az login`, because that mode does not use the CLI.

The warning log line now also records the endpoint and authentication mode alongside the deployment.

## Fixed: dictation no longer sends chat messages on its own

Typing a line break used to press plain Enter. Teams, Slack, and Discord all treat Enter as
"send", so a polished multi-paragraph dictation sent itself on the first paragraph break and typed
the rest into an empty composer. AI cleanup made this worse, because cleanup is what introduces
paragraph breaks in the first place: raw speech recognition produces none.

Line breaks are now typed as Shift+Enter, the soft-newline chord those apps use. In a plain text
box, a browser textarea, or a rich edit control it behaves exactly like Enter, so nothing else
changes. Word treats it as a line break rather than a paragraph mark, which is the one visible
difference.

A new setting, **Don't send chat messages early**, is on by default and can be turned off for an
app that binds Shift+Enter to something else.

## Improved: text arrives in whole words

Typed text is delivered in batches, and the target app repaints between them, so a fixed-size
batch could split a word across two repaints and show "consi" before "der". Batches now end on a
word boundary, backing off at most half a batch so a long URL or file path still makes steady
progress.

## Improved: Azure setup uses the Foundry project endpoint

Picking a discovered model now fills in the Microsoft Foundry **project** endpoint
(`https://<resource>.services.ai.azure.com/api/projects/<project>`), which is the shape Microsoft
recommends and which Scribe already routed natively. Previously the model picker could only offer
the older account endpoint, so anyone following Microsoft's guidance had to type the project URL by
hand with no indication it was supported.

Accounts without a project, and setups authenticating with an API key, keep the account endpoint:
the Foundry project data plane requires a Microsoft Entra token and cannot accept a key.

## Clearer wording in Text insertion settings

"Unicode typing" and "Clipboard paste" are now **Type it in** and **Paste it in**, with a
description of what each actually does, including that pasting replaces whatever you had copied.

## Under the hood

- The injection method setting only ever applied to apps that are not standard Windows text boxes.
  Standard boxes take an instant insertion path that both methods share. This is unchanged, but is
  now measured rather than assumed.
- New `tools/Scribe.InjectionLab` measures injection latency and fidelity against a real focused
  window, and `scripts/Run-DevBuild.ps1` runs a working-tree build in place of an installed copy
  for live testing.
- The benchmark harness now honors an explicit `--endpoint`. It previously preferred a hardcoded
  resource, which could silently measure a different region than the one requested.
- The benchmark harness gained `--glossary-libraries`, so a run can measure what the dictionary
  glossary contributes instead of assuming it. It previously had no glossary support at all, which
  means every earlier benchmark measured the prompt with an empty glossary.
- Six new benchmark cases cover AI conversation: model comparisons, retrieval architecture, training
  and quantization vocabulary, and mixed planning talk. They run through the full text-to-speech and
  Parakeet pipeline like every other case.
- Model leaderboard updated with a July 28 comparison of `gpt-5.6-sol`, `gpt-5.6-luna`, and
  `gpt-5.6-terra` across two deployments, including evidence that model rankings are
  deployment-specific.
- A dictation whose AI cleanup was skipped unexpectedly now logs a warning naming the reason, and
  records that reason on the trace line, so a silent cleanup outage is visible in one grep.
- Service principal setup documentation now follows Microsoft's current Foundry RBAC guidance: use
  **Foundry User**, not the `Cognitive Services` roles, and assign by role ID because those roles
  were renamed. It also documents that role assignments routinely take longer than the five minutes
  Microsoft documents, which is the most common cause of a 403 that looks like a wrong role.

The default writing style now teaches the shape of a model identifier rather than forbidding any
change to it. Measured across five models and 23 cases, no model regressed and the mean rose about
three points. A separate candidate rewrite of the whole style and system prompt was tested again and
still did not clear the gate, so it is not shipped.
