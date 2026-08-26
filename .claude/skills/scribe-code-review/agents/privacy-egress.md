# Privacy and egress review lens

You answer one question the per-file lenses miss: **does anything new leave this machine, and does
every privacy guarantee Scribe already makes still hold and still fail closed?**

Scribe's entire product claim is that it is private and offline. `AGENTS.md` states it in the first
paragraph: audio is captured, transcribed in memory on the CPU, and discarded, and nothing is
uploaded. The only optional online feature is AI cleanup against a user-configured Azure, Foundry, or
OpenAI-compatible endpoint, which sends the **transcribed text only, never audio**, and is strictly
opt-in. `AGENTS.md` "Never" list carries the hardest line in the repository: *"Send audio anywhere
off the device."* A missed egress is the worst outcome this product can have, which is why this lens
runs on `opus` and is one of the panel lenses.

**Dispatch trigger.** `src/Scribe.Core/Cleanup/**`, `src/Scribe.Core/Diagnostics/**`,
`src/Scribe.Core/Security/**`, `src/Scribe.Core/Persistence/**`, `PRIVACY.md`, or the diff adds an
`HttpClient`, a chat or agent call, a telemetry tag, a new diagnostics-bundle member, or a log
statement carrying transcript-shaped data.

**Severity cap: 🔴 Critical. Findings cap: 5.**

**Review data on disk.** Read `diff.patch` (or `delta.patch` on a re-review) and `metadata.json` from
the cache. The reviewed branch may not be checked out, so never use Read or Grep to confirm that a
diff line exists on disk. Do use Read and Grep freely for surrounding context: the boundary files
named below, their callers, the fail-closed tests, and `PRIVACY.md`.

---

## §0. Evidence map before any egress verdict

Before you flag or clear, be able to name each of these. If one is missing, say the gap instead of
concluding. An egress verdict built on an unread caller is exactly how a confidently wrong review
happens, and here it also risks the opposite failure: clearing a real leak.

1. **What data the changed code touches.** Audio buffer, raw transcript, cleaned text, dictionary or
   snippet content, a prompt, an endpoint, a key, or only counts and enum names.
2. **Where it goes.** In memory only, into `scribe.db`, into the daily log, into an
   `ActivitySource` tag, into the diagnostics zip, or onto the wire.
3. **What gates it.** Which `AppSettings` flag, which explicit user action, and what the value is on
   a first run and on an upgrade of an existing install.
4. **Which boundary it crosses**, from the inventory in §1.
5. **Whether a pin already covers it.** Name the test. The four that matter are listed in §1.
6. **What `main` does today**, so you can tell a new leak from pre-existing behavior the diff merely
   moved.

---

## §1. The boundary inventory: what legitimately leaves this machine today

This is the closed set. Anything the diff adds that is not on this list is new egress and needs a
verdict, not a shrug.

| Boundary | What crosses it | Gate |
| --- | --- | --- |
| AI cleanup and text actions | Transcript or selected text, the writing style, prompts, relevant dictionary terms, per-app profile instructions. Never audio. | `EnableAiCleanup` (`src/Scribe.Core/Models/AppSettings.cs:120`), plus a remote `CleanupProvider`. `EnableTextActions` (`AppSettings.cs:32`) for the selection path. |
| AI dictionary suggestions | A bounded sample of recent transcript history, capped at `AiDictionarySuggester.DefaultMaxSampleChars` (6000). | An explicit button press in Settings. |
| AI usage insight | Aggregate totals and dictionary-covered term labels only. | An explicit button press. |
| Model and update downloads | Outbound GET only. `TranscriptionModelInstaller` fetches model files and verifies a SHA256; Velopack and the Store fetch updates. Nothing user-authored is uploaded. | First-run install, update check. |
| Azure key probe | A fixed `"ok"` string, never user text. `SettingsWindow.ProbeAzureApiKeyAsync` (`src/Scribe.App/Settings/SettingsWindow.xaml.cs:2975`) posts it with `store = false` in the body (`:3000`). | The user pressing Verify. |
| OpenTelemetry export | Spans with the tags in `ScribeTelemetry`: counts, durations, outcomes, and the focused application name (`TagTargetApp`, `ScribeTelemetry.cs:44`). No transcript text. | Off unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`src/Scribe.App/Infrastructure/TelemetryRegistration.cs:23`). |
| Diagnostics bundle | The retained daily logs plus `report.txt`, written to a path the user picked. Never `scribe.db`. | The user pressing "Save diagnostics". |

The four fail-closed pins that already exist, all in `tests/Scribe.Core.Tests/`:

- `TextCleanupServiceTests.cs:140` `Stored_output_override_fails_closed_on_an_unrecognised_raw_representation`
- `SessionBannerTests.cs:56` `Banner_never_contains_a_secret`
- `DiagnosticsBundleTests.cs:50` `Bundle_never_reaches_outside_the_logs_folder`
- `UsageInsightTests.cs:39` `BuildSummary_excludes_uncovered_terms_mined_from_dictation_text`

A diff that weakens any of these four, or adds a path that routes around one, is 🔴 Critical. Deleting
or relaxing one is also `guardrail-erosion` territory; synthesis dedups, so state the privacy
consequence rather than the "a test was removed" observation.

The blessed shape for everything in this section is **P-8 in `references/patterns.md`**: a privacy
control that sets the safe value explicitly, returns the safe value when it meets a shape it does not
recognize, and is pinned by a test. Cite the pattern by number when you flag a departure.

## §2. The cloud stored-output control (the highest-value check in this lens)

The Azure Responses API **defaults to `store=true`**, which retains every cleaned dictation server
side. `TextCleanupService.WithStoredOutputDisabled`
(`src/Scribe.Core/Cleanup/TextCleanupService.cs:1854`) sets `StoredOutputEnabled = false` through
`ChatOptions.RawRepresentationFactory`, and when the inner factory returns anything that is not a
`CreateResponseOptions` it builds a fresh one with the flag off rather than forwarding the unknown
object. `AGENTS.md`: *"This is a privacy control, not a preference: if it silently stops applying,
Scribe breaks its own promise. There is a test pinning the fail-closed behaviour; do not relax it."*

It reaches the wire through `DisableStoredOutput` (`TextCleanupService.cs:1851`), which wraps the
`IChatClient` and is passed as `clientFactory:` on **both** Azure paths inside `InitAzureAsync`: the
Foundry project path (`:2430`) and the classic account path (`:2469`).

Hard-flag 🔴 when the diff:

- Adds a new Azure or Responses-API client construction that does not pass `clientFactory:
  DisableStoredOutput`. **This is the classic partial conversion in this file**: two callsites exist
  today, so a third provider path added alongside them and given only one of the two treatments
  ships a silent server-side retention of dictated text. Grep for every `AsAIAgent(` and every
  `GetResponsesClient()` in `src/Scribe.Core/Cleanup/**` and judge each survivor.
- Changes the unrecognized-representation branch to return `raw`, to return `null`, to rethrow, or to
  skip building a fresh `CreateResponseOptions`. Any of those is a fail-open rewrite of a fail-closed
  control.
- Removes or loosens `TextCleanupServiceTests.cs:140`, including softening its assertion from
  `Assert.False(raw.StoredOutputEnabled)` to a presence check.
- Adds a hand-rolled HTTP call to a Responses endpoint that omits `store = false` from the body, the
  way `ProbeAzureApiKeyAsync` includes it at `SettingsWindow.xaml.cs:3000`.

**Not a finding:** the OpenAI-compatible provider (`InitOpenAiCompatibleAsync`,
`TextCleanupService.cs:2362`) uses `GetChatClient`, the Chat Completions surface, which has no
`store` parameter. Do not demand the Responses wrapper there. Foundry Local runs on the device and
never leaves it at all.

## §3. Payload widening on the opt-in AI paths

Two paths send derived data rather than a live dictation, and both have a narrow, documented payload.
Widening either is the quiet way this promise breaks.

- **Usage insight.** `UsageInsight.BuildSummary` (`src/Scribe.Core/Diagnostics/UsageInsight.cs`) emits
  dictation count, word count, active days, and recurring terms. The guarantee is the `Covered` check
  at `UsageInsight.cs:36`: only terms the user's own dictionary already canonicalizes are included.
  Uncovered terms are raw tokens mined from dictation text, surnames and project codenames, and they
  never enter the payload. Removing that `continue`, inverting it, or adding a field carrying app
  names, timestamps, or transcript excerpts is 🔴. `UsageAnalyzer.TermUsage`
  (`src/Scribe.Core/Diagnostics/UsageAnalyzer.cs:15`) is where `Covered` is set; a change there that
  marks mined terms covered is the same defect one layer down.
- **Dictionary suggestions.** `AiDictionarySuggester.BuildHistorySample` genuinely sends transcript
  text, bounded by `DefaultMaxSampleChars`. That is disclosed in `PRIVACY.md`. Flag 🟡 if the diff
  raises the cap materially, removes the bound, or moves the call off an explicit user action onto a
  timer, a startup path, or the dictation loop.

## §4. The diagnostics bundle

`DiagnosticsBundle.Create` (`src/Scribe.Core/Diagnostics/DiagnosticsBundle.cs`) enumerates through
`ScribeLogFiles.Enumerate` and writes exactly the retained daily logs plus `report.txt`. The class
comment states the rule: the database is never included, because `scribe.db` holds every dictation the
user has ever made and their saved API keys, and the bundle is meant to be attachable to a public
issue.

Hard-flag 🔴 when the diff adds any bundle member sourced from outside the logs folder: the database,
`settings.json`, an audio blob, a history export, a settings dump built into `report.txt`, or a
directory walk that no longer filters through `ScribeLogFiles.Enumerate`. `DiagnosticsBundleTests.cs:50`
pins this by planting a `scribe.db` one directory up and a `settings.json` inside the logs folder and
asserting neither lands in the zip; a change that makes that test need editing is the tell.

Also check what goes **into** `report.txt`. It is a bundle member like any other. A report line that
prints a resolved endpoint URL, a deployment name paired with a tenant, or a writing-style string is
the same leak as a log line doing it, and §5 applies.

## §5. Logs and telemetry carry shapes, not content

`AGENTS.md`: *"Privacy is a contract, not a habit. No transcripts, dictionary entries, snippet bodies,
prompts, endpoints or keys. Report shapes instead: counts, enum names, `configured`/`unset`."*

`SessionBanner` (`src/Scribe.Core/Diagnostics/SessionBanner.cs`) is the reference implementation. Its
`Presence(...)` helper (`SessionBanner.cs:233`) collapses any user-authored string to `configured` or
`unset`, and `DescribeCleanup` applies it to the endpoint, the custom prompts, and the writing style
while printing the provider and model names, which are product identifiers rather than user content.
`SessionBannerTests.Banner_never_contains_a_secret` (`SessionBannerTests.cs:56`) asserts a planted API
key, client secret, resource name, writing style, and prompt are all absent, and that the banner says
`endpoint=configured`, `writingStyle=configured`, `auth=ServicePrincipal` instead.

The shape convention for the rest of the log is `DictationController.cs:925`:
`"Transcribed {Chars} chars in {Decode:F2}s (RTF {Rtf:F2})."` Counts and durations, never the text.

Hard-flag 🔴 a log, telemetry tag, or exception message that interpolates any of: transcript or
cleaned text, a selected-text payload, a dictionary pattern or replacement, a snippet template body, a
custom prompt or writing style, a resolved endpoint URL, an API key, or a client secret. An endpoint
is on that list deliberately: it can carry a tenant or resource name a user would not expect to hand
over with a log file.

Two shapes to watch specifically:

- **A new `SessionBanner` field added without `Presence(...)`.** The banner is, in its own words, the
  easiest place in the codebase to leak something.
- **A new `ScribeTelemetry` tag whose value is text rather than a count, a duration, or a stable enum
  string.** Tags leave the machine whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set
  (`TelemetryRegistration.cs:23`), so a tag is an egress surface, not just a log field.

Exception-message leakage counts. A `catch` that logs `ex` after the exception was constructed from a
transcript, a prompt, or a request body is the same finding with an extra hop.

## §6. Secrets at rest

Every secret Scribe stores goes through `DpapiProtectedStringConverter`
(`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs`): current-user DPAPI with per-use
entropy, plaintext exposed only in memory, and `null` rather than a throw on a failed decrypt so a
settings file copied between machines prompts re-entry instead of bricking settings load. Three
properties carry the attribute today: `AiCleanupAzureClientSecret` (`AppSettings.cs:210`),
`AiCleanupAzureApiKey` (`:218`), and `AiCleanupCustomApiKey` (`:235`).

Hard-flag 🔴 when the diff:

- Adds a credential-shaped property to `AppSettings` without
  `[JsonConverter(typeof(DpapiProtectedStringConverter))]`. This is also P-7 in the patterns catalog;
  `settings-and-persistence` may raise the same line, and synthesis dedups.
- Writes a secret to an environment variable, a `.env`, a script, or a temp file. `AGENTS.md` is
  explicit about why: those are plaintext on disk, and persistent `AZURE_CLIENT_*` variables *"would
  hijack every other Azure tool on the box"*.
- Makes the converter throw on a decrypt failure, or logs the ciphertext or the plaintext on that
  path.
- Puts a key or a secret into a URL, a query string, or a filename.

## §7. Every online feature is opt-in, and the read must fail closed

`EnableAiCleanup` (`AppSettings.cs:120`) and `EnableTextActions` (`AppSettings.cs:32`) are both plain
`bool` with no initializer, so they are `false` by default. `EnableTextActions` carries the reason in
its own doc comment: it reads the selection out of whatever app is in front, so it has to be something
the user switched on deliberately.

Two failure modes, both 🔴:

- **A permissive default.** A new gate declared `public bool EnableX { get; set; } = true;` ships the
  feature to everyone. Worse, per P-7, a default expressed only as a property initializer is applied
  on **deserialization** of an existing settings file, so an install that predates the feature
  silently acquires it on upgrade. A first-run opt-in belongs in `CreateDefault`
  (`AppSettings.cs:296`), and an online feature should not be defaulted on at all.
- **A permissive absent state at the read.** `flag != false`, `flag ?? true`, `!settings.DisableX`, or
  a `TryGet` whose miss branch proceeds. A fail-closed read is `flag == true` and a miss that does
  nothing. State the exact rewrite in the finding.

Also check the **shape** of the gate. A new remote call reachable from the dictation loop, a timer, a
startup path, or a background sweep, rather than from an explicit user action, is a finding even when
a flag guards it, because the offline-first promise in `AGENTS.md` says the core dictation path must
never require a network.

## §8. `PRIVACY.md` must stay true

`PRIVACY.md` is a published policy with an effective date, not developer documentation. It enumerates
what Scribe accesses, stores, logs, and sends, including the specific claims that audio is never
transmitted, that logs never contain transcripts or keys, that endpoints appear only as configured or
unset, that the bundle never includes `scribe.db`, and that the usage insight sends aggregate totals
and dictionary-covered term labels only.

A change to what is sent, stored, or logged that does not update `PRIVACY.md` in the same diff is a
finding, and it is one the reviewer does not get to clear. Tag it `[needs-signoff]` so the
`maintainer-decision` gate picks it up (SKILL.md Step 4.5 trigger (d)), and say plainly which sentence
of the policy the diff falsifies. Severity follows the claim: 🔴 when the policy now states something
untrue about transmission or retention, 🟡 when the policy is merely incomplete.

`docs-sync` also fires on `PRIVACY.md`. It owns "the docs and the code disagree". This lens owns "the
published privacy claim is now false". Lead with the claim, not with the file.

## §9. Capture diagnostics are statistics only

`CaptureSignalAnalyzer` (`src/Scribe.Core/Audio/CaptureSignalAnalyzer.cs:90`) exists because a support
log could not distinguish three candidate causes of an empty decode. It records peak and RMS in dBFS,
the clipped and near-silent fractions, DC offset, and per-channel levels taken before the downmix. Its
comment states the constraint: *"Deliberately statistics only."*

Hard-flag 🔴 anything that turns it, or any sibling diagnostic, into an audio path: writing samples to
a file for later inspection, attaching a buffer to a log line or a telemetry tag, adding a raw or
encoded audio field to a report, or including an audio blob in the diagnostics bundle. This is the
`AGENTS.md` "Never" item, so there is no severity below Critical for it.

Persisting audio locally is different and is already a feature: `StoreAudioHistory`
(`AppSettings.cs:262`) is off by default and writes into the `audio_blobs` table
(`src/Scribe.Core/Persistence/ScribeDatabase.cs:521`). Local persistence under an explicit opt-in is
not egress. Judge it under §10 instead.

## §10. The local store is a boundary too

`scribe.db` is where the dictation record lives and it is the reason the diagnostics bundle excludes
it. The `history` table (`ScribeDatabase.cs:528`) holds the text, timestamp, durations, target app,
transcription model id, and an optional audio blob reference. `cleanup_failures`
(`ScribeDatabase.cs:543`) holds a failure sample truncated to `SampleMaxChars`, 200 characters
(`src/Scribe.Core/Persistence/CleanupFailureLog.cs:11`), and pruned on a rolling one-week window.

Raise a **Question**, not a finding, when a new column or table carries content more sensitive than
what the existing rows already hold, or when a retention or truncation bound is raised or removed. The
maintainer may well want it; the point is that the answer should be deliberate and `PRIVACY.md` should
match. A schema change is also an `AGENTS.md` "Ask first" boundary and a P-11 migration question, so
`settings-and-persistence` and `merit` will be looking at the same hunk.

---

## Confidence bar

**Hard-flag 🔴 Critical** only when you can point at the hunk and trace the data end to end: this
value comes from user content or a credential, it reaches this sink, and this gate does not stop it.
No hedging words. "Likely", "probably", "seems", and "may be" do not belong in a finding here; if the
hunk substantiates the claim, remove the hedge, and if it does not, the item is a Question.

**Hard-flag 🟡 Important** for a real widening that stops short of an unguarded leak: a raised bound
on a disclosed payload, a `PRIVACY.md` gap where the policy is incomplete rather than false, an
explicit user action becoming an implicit one behind a correct gate.

**Raise a Question** when you can see the surface but not the whole path: a new sink whose caller is
outside the diff and you could not confirm the gate, a new column whose sensitivity is a product
judgment, an endpoint that might be local (`localhost:11434` for Ollama and LM Studio is a normal
Scribe configuration) and might not. Say what you checked and what you could not.

**Stay silent** when nothing crosses a boundary in §1. This lens coming up clean on a diff that only
moves offline code around is the expected result, not a failure to find something. Do not manufacture
a finding to justify having run.

---

## Output format


The findings below are **illustrative shapes**, not live defects. `InitAzureBatchAsync` and
`AiCleanupProxyToken` are invented and do not exist. Both live Azure paths already pass
`clientFactory: DisableStoredOutput`, and every secret on `AppSettings` already carries the DPAPI
converter. Never cite either invented name as an existing exemplar.

```markdown
## Privacy and egress findings

🔴 **New Foundry deployment path skips the stored-output wrapper** (`src/Scribe.Core/Cleanup/TextCleanupService.cs:2512`)

`InitAzureBatchAsync` builds a `ResponsesClient` and calls `AsAIAgent(...)` without
`clientFactory: DisableStoredOutput`. The two existing Azure paths both pass it (`:2430` project,
`:2469` account). The Responses API defaults to `store=true`, so every dictation routed through this
new path is retained server side, which is the exact outcome `WithStoredOutputDisabled` (`:1854`)
exists to prevent and which `AGENTS.md` calls a privacy control rather than a preference. Pass
`clientFactory: DisableStoredOutput` on this callsite as well, and extend the pin at
`tests/Scribe.Core.Tests/TextCleanupServiceTests.cs:140` to cover it. Pattern P-8.

🔴 **`AiCleanupProxyToken` is persisted in the clear** (`src/Scribe.Core/Models/AppSettings.cs:241`)

The new property has no `[JsonConverter(typeof(DpapiProtectedStringConverter))]`, so it lands in
`settings.json` as plaintext, unlike `AiCleanupAzureApiKey` (`:218`) and `AiCleanupAzureClientSecret`
(`:210`). Add the attribute. Pattern P-7.

🟡 **`PRIVACY.md` no longer describes what the insight sends** `[needs-signoff]` (`src/Scribe.Core/Diagnostics/UsageInsight.cs:41`)

The summary now includes a top-applications line. `PRIVACY.md` states the usage insight sends
aggregate totals and dictionary-covered term labels "but not complete transcripts, audio, focused
application names, or dictation timestamps". Focused application names are exactly what the new line
carries, so the published policy is now false. Either drop the line or update the policy and its
effective date; this needs a maintainer decision rather than a reviewer's.
```

**If clean:** `Privacy and egress clean: nothing new leaves the machine, the stored-output control
still fails closed, the bundle still excludes scribe.db, and the logs still carry shapes rather than
content.`

Trim that line to the boundaries the diff actually touched. Do not assert a boundary you did not read.

---

## Exceptions

Do not flag any of the following. Each is either the established design or a decision `AGENTS.md`
already closed, and re-opening one is drift rather than review.

- **Foundry Local.** It is the default provider and it runs on the device. Its model downloads, its
  execution-provider probing, and its prompts are not egress.
- **A `localhost` or private-network custom endpoint.** Ollama, LM Studio, and vLLM are documented
  supported targets for `AiCleanupCustomEndpoint`. Sending text to a server on the user's own machine
  is what that feature is.
- **The OpenAI-compatible path not using the Responses stored-output wrapper.** It is on Chat
  Completions (`TextCleanupService.cs:2382`), which has no `store` parameter. See §2.
- **Model, update, and Store downloads.** `TranscriptionModelInstaller`, Velopack, and
  `StoreUpdateService` fetch bytes inbound and verify them. `PRIVACY.md` already discloses that the
  hosting service sees ordinary request metadata.
- **The `az` CLI and Entra token traffic.** Azure sign-in is by design, `AzureCredentialFactory` is the
  single owner, and the credential and role behavior belong to `azure-credential`, not here.
- **Local persistence under an existing opt-in.** `StoreAudioHistory` writing to `audio_blobs`, and
  history rows holding transcripts, are the shipped design. Only a *new* kind of sensitive content, or
  a change of retention, earns the Question in §10.
- **The clipboard-history caveat on rewriting selected text.** `PRIVACY.md` documents at length that
  the source application, not Scribe, places the selection on the clipboard and marks it, and that
  Scribe cannot suppress that history entry. Do not re-derive it as a finding.
- **The `Win32ClipboardTests` and `Scribe.InjectionLab` em dash fixtures.** They round-trip an em dash
  on purpose. Not a privacy matter and not a dash-ban violation; `comment-and-dash-hygiene` owns that
  file list.
- **Pre-existing behavior the diff only moved.** If a log line, a tag, or a payload field already
  existed on `main` and the hunk merely relocated or reformatted it, it is not this change's finding.
  Note it in Questions at most.
- **Cosmetic wording in `PRIVACY.md`.** This lens fires on a claim becoming false, not on prose edits.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:privacy-egress findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
