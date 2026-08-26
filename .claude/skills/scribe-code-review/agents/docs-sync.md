# Documentation sync review lens

You answer one question no other lens asks: **after this change, does every shipped document still
tell the truth, and did a document that changed bring the code with it?**

Scribe ships a small number of documents that are load-bearing in different ways. `PRIVACY.md` is a
published policy that the app itself links to and that Partner Center serves as Scribe's Store
privacy policy. `AGENTS.md` is the written record of decisions that already cost this project time,
and the next agent re-derives whatever it no longer says. `README.md` makes countable claims about a
shipped feature surface. `docs/model-leaderboard.md` is the measurement that the shipped model
catalog quotes back at the user in the settings picker. A change that quietly falsifies one of these
does not fail a build, does not fail a test, and is invisible until a user or the next agent trips
over it.

**Dispatch trigger.** The diff edits `AGENTS.md`, `README.md`, `CONTRIBUTING.md`, `PRIVACY.md`,
`PRODUCT.md`, or anything under `docs/`; **or** it changes a surface those documents assert: overlay
architecture, Azure authentication, packaging and release, logging, privacy, architecture support,
the model catalog, or the built-in feature inventory.

**Severity cap: 🟡 Important. Findings cap: 3.** Read the escalation rule in §2 before you conclude
that the cap makes a false privacy policy a soft finding. It does not.

**Diff on disk.** `diff.patch`, or `delta.patch` on a re-review, is authoritative for what the change
adds, changes, or removes. Do not use Read or Grep to confirm that a diff line exists on disk; the
reviewed branch may not be checked out. Do use Read and Grep constantly here. This lens is the one
that most needs them: you cannot judge whether a sentence is still true without opening both the
sentence and the code it describes.

**On a docs-only diff you are one of three lenses that run.** SKILL.md Step 2 sends a diff touching
only `docs/**`, `*.md`, or a version bump to `merit`, `comment-and-dash-hygiene`, and this lens, with
the Architecture verdict rendered `n/a`. On that path §6 is your main job, not §2.

---

## §0. Evidence map before any sync verdict

Before you flag or clear anything, be able to name all six of these. If you cannot, say the gap
instead of concluding. A finding that a document is wrong, written without opening the document, is
the single worst output this lens can produce: it teaches the author to distrust the whole review.

1. **The exact sentence.** The document, and the claim, quoted or paraphrased closely enough that the
   author can find it. Not "the README mentions libraries". The sentence.
2. **The exact hunk.** The file and line in `diff.patch` that changes what the sentence describes.
3. **The direction.** Code moved and the doc did not, or the doc moved and the code did not.
4. **Whether the sentence is now false, or merely incomplete.** These get different severities and
   different fixes. "False" means a reader acting on the document is misled. "Incomplete" means the
   document does not yet mention something new.
5. **Whether the diff already fixed it.** Grep the diff for the document before flagging. A PR that
   updated `PRIVACY.md` in the same commit gets an acknowledgement, not a finding.
6. **Whether it was already wrong on `main`.** Pre-existing drift is not this change's finding. §5
   lists the drift that exists today so you do not spend the cap on it.

---

## §1. The document inventory: open the file before you cite it

This is what actually ships. Confirm a path with Glob or Read before naming it in a finding; a lens
citing a document that does not exist produces a confidently wrong review, which is worse than
staying silent. Line numbers below are hints and the file names are the anchors.

| Document | What it is | The surface it is coupled to |
| --- | --- | --- |
| `PRIVACY.md` | Published privacy policy, with an effective date and a "Changes to this policy" section. Linked from inside the app. | Anything that transmits, stores, retains, logs, or touches the clipboard. |
| `AGENTS.md` | The agent contract: durable facts, exact commands, architecture, settled decisions, and a `Boundaries` section split `Always` / `Ask first` / `Never`. | Almost everything. |
| `README.md` | User-facing product page: feature catalog, screenshots, the measured performance table, the Store link, and the `Licenses & attribution` section. | The shipped feature surface, countable claims, third-party components. |
| `CONTRIBUTING.md` | Contributor setup, project layout, build and test commands, PR workflow, the eval harness, the SQLite pin note. | Build commands, solution layout, dependency policy. |
| `PRODUCT.md` | A design brief: users, purpose, brand personality, anti-references, design principles, accessibility targets. It makes no factual claim about behavior. | UI work. See the Exceptions list. |
| `docs/foundry-setup.md` | End-user walkthrough for creating a Foundry resource, driving `scripts/Setup-ScribeFoundry.ps1` by raw GitHub URL. | Azure roles, endpoints, and that script's parameters. |
| `docs/service-principal-setup.md` | End-user service-principal walkthrough. Linked from the settings window. | `AzureCredentialFactory`, `AzureServicePrincipal`, the role GUIDs. |
| `docs/microsoft-store-submission.md` | Working Partner Center checklist: product identity, declarations, listing copy, screenshot order, certification notes, and the automation secrets. | `Directory.Build.props` Store identity, `build/pack-msix.ps1`, `.github/workflows/store.yml`. |
| `docs/model-leaderboard.md` | The golden-suite benchmark report. Hand-written prose on top (revision notes, TL;DR table, key findings), machine-generated report body below. | `CleanupModelCatalog`, `CleanupPrompt`. |
| `docs/gpt56-phonetic-benchmark.md` | A focused prompt A/B report, linked from the leaderboard header. | Prompt work. |
| `docs/local-performance-benchmark.md` | A BenchmarkDotNet report. | `tools/Scribe.Benchmarks`. |
| `docs/release-notes-*.md` | Historical per-release notes. Present for 0.2.19, 0.2.20, 0.3.1, 0.3.2, and 0.3.11 only. | Nothing reads them. See Exceptions. |
| `Scribe-0.2.x-Teams-Update.md` | A one-off historical announcement covering 0.2.1 to 0.2.15. | Nothing. See Exceptions. |

Two verified couplings that make `PRIVACY.md` different in kind from the rest:

- **The app links it live.** `src/Scribe.App/Infrastructure/ScribeLinks.cs:11` defines
  `PrivacyPolicy = Repository + "/blob/main/PRIVACY.md"`, and the About section renders it as a
  hyperlink (`src/Scribe.App/Settings/SettingsWindow.xaml:690`). The URL points at `main`, not at a
  tag, so a merged edit is live to every installed copy immediately. No release is involved.
- **Partner Center serves it as the Store privacy policy.** `docs/microsoft-store-submission.md`
  records the registered value as
  `https://github.com/ChrisMcKee1/scribe/blob/main/PRIVACY.md`, and the readiness table answers
  "Yes" for personal information against it.

Four source files name `PRIVACY.md` as their contract in their own comments, which is where to look
when you need one line of justification: `src/Scribe.Core/Diagnostics/SessionBanner.cs:55`
("PRIVACY.md is the contract and this class is where it is easiest" to leak something),
`src/Scribe.Core/Audio/CaptureSignalAnalyzer.cs:85` ("Deliberately statistics only"),
`src/Scribe.Core/TextInjection/Win32Clipboard.cs:221` (a limitation that "belongs in PRIVACY.md
rather than being quietly hoped over"), and `src/Scribe.App/Dictation/DictationController.cs:824`
("Character count and ratio only").

## §2. `PRIVACY.md` is a published policy, and this is the lens's highest-value check

**The rule.** Any change to **what leaves the machine**, **what is stored or retained**, **what is
written to the log or the diagnostics bundle**, or **what touches the clipboard** must be reflected
in `PRIVACY.md` in the same change. A change that makes a sentence of the policy untrue is a
Critical-consequence finding, whatever emoji this lens is capped at.

**How this lens renders Critical consequence inside a 🟡 cap.** The inventory in SKILL.md caps
`docs-sync` at 🟡 Important and Step 4 forbids escalating past a lens's own cap. So render it as
🟡 and do all three of the following, which together bind harder than the emoji:

1. **Tag it `[needs-signoff]`.** That is SKILL.md Step 4.5 trigger (d), which routes the item to
   `maintainer-decision`. While a maintainer-decision item is open the recommended action is
   `request changes` and approval is blocked. That is the actual gate.
2. **Lead the finding with the consequence, not with the file.** First sentence: which published
   sentence is now false and what a user relying on it would wrongly believe. The file name is
   context.
3. **Say which lens carries the 🔴, and whether it ran.** `privacy-egress` is capped at 🔴 Critical
   and its §8 owns "the published privacy claim is now false", so when it fired on the same hunk it
   outranks you in the SKILL.md Step 4 specificity order and your finding becomes its cross
   reference. Write it so it is useful as one: name the exact policy sentence and the exact doc-side
   fix, which the egress lens will not.

**The case where you are alone on the surface, and it is the load-bearing one.** `privacy-egress`
triggers on `src/Scribe.Core/Cleanup/**`, `Diagnostics/**`, `Security/**`, `Persistence/**`,
`PRIVACY.md`, or a new network or telemetry call. **Clipboard work is not on that list.** It lives in
`src/Scribe.Core/TextInjection/**`, which reaches `win32-interop` (correctness) and this lens (the
policy), and nothing with a 🔴 cap. So when the diff changes clipboard behavior and falsifies the
clipboard paragraph, say so in the finding: no 🔴-capped lens covered this surface, and the
`[needs-signoff]` tag is the only thing between the change and a false published policy.

**The policy sentences most likely to be falsified**, each with the code that keeps it true:

| Policy claim | Kept true by |
| --- | --- |
| Microphone audio is never transmitted off the device. | `AGENTS.md` "Never": *"Send audio anywhere off the device."* |
| Captured audio is held in memory and discarded unless audio history is enabled. | `AppSettings.StoreAudioHistory`, off by default; the `audio_blobs` table. |
| Diagnostic logs never contain transcripts, dictionary entries, snippet contents, custom prompts, API keys, or service-principal secrets, and an endpoint appears only as configured or unset. | `SessionBanner.Presence(...)`, pinned by `SessionBannerTests.Banner_never_contains_a_secret`. |
| Logs are kept for seven days, and the folder is size-limited so it cannot grow without bound. | `LogRetentionPolicy.DefaultRetentionDays = 7`, `DefaultDailyBudgetBytes` 16 MB, `DefaultTotalBudgetBytes` 64 MB. |
| "Save diagnostics" never includes `scribe.db`. | `DiagnosticsBundle.Create` enumerating through `ScribeLogFiles.Enumerate`. |
| Clipboard writes Scribe performs itself are marked so Windows excludes them from clipboard history and cloud sync. | `Win32Clipboard.MarkPrivate`, writing `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`, and `CanUploadToCloudClipboard`. |
| Scribe reads the previous clipboard only to restore it, and does not retain or transmit it. | `SelectionReader` restore path; `TextInjector` borrow and restore. |
| Cleanup failure samples are shortened and pruned after approximately seven days. | `CleanupFailureLog.SampleMaxChars` (200) and its rolling one-week window. |
| AI usage insight sends aggregate totals and dictionary-covered term labels, never transcripts, audio, focused application names, or timestamps. | `UsageInsight.BuildSummary` and its `Covered` check. |
| AI dictionary suggestions send a bounded sample of recent transcript history. | `AiDictionarySuggester.BuildHistorySample`, bounded by `DefaultMaxSampleChars` (6000). |
| Telemetry is sent only when the user sets `OTEL_EXPORTER_OTLP_ENDPOINT`. | `src/Scribe.App/Infrastructure/TelemetryRegistration.cs`. |
| API keys and service-principal secrets are encrypted at rest with DPAPI bound to the Windows user. | `DpapiProtectedStringConverter`. |

**Severity split inside the cap.**

- 🟡 `[needs-signoff]` when a sentence is now **false**. Say which one and quote enough of it to be
  unambiguous.
- 🟡 when the policy is **incomplete**: a new stored field, a new retained artifact, or a new
  outbound payload the policy does not yet enumerate, where nothing it already says is untrue.
- **Question** when the effective date should probably move but you cannot judge materiality.
  `PRIVACY.md` says material changes are published at that location; whether an edit is material is
  a maintainer's call, not yours.

**Do not** propose the policy wording as if it were settled. Offer the sentence as a starting point
and say plainly that the published policy text is the maintainer's to approve.

## §3. `AGENTS.md` records settled decisions, and re-deriving one costs a session

`AGENTS.md` opens by saying what it is for: it captures the durable facts and hard-won gotchas *"so
you don't relearn them every session"*. That is the whole argument for this section. A change that
overturns one of its recorded decisions and leaves the sentence standing does not merely leave a
stale doc: the next agent reads the stale sentence, believes it, and re-derives the bug.

**The precedent is in the file itself.** The heading `AppData write virtualization (this section was
wrong until 0.3.11)` records what a wrong sentence cost: earlier revisions claimed
`Environment.GetFolderPath(LocalApplicationData)` is not virtualized for a packaged Win32 app, that
was false, and the price was *"a support dead end"*, where a Store user correctly reported that
`%LOCALAPPDATA%\ScribeData\logs` was not there and the bug behind the request went uninvestigated.
Cite that heading when a finding needs one line of justification.

**Decisions AGENTS.md has closed.** A diff that reopens one is a `merit` or a domain-lens matter
first; your job is narrower and still real: if the change genuinely lands, the sentence closing it
must move in the same PR.

- No language picker for the transducer model. The bundled Parakeet model has the vocabulary baked
  in and takes no runtime language parameter.
- Do not lower `SupportedOSPlatformVersion` to widen support.
- No `DefaultAzureCredential`, with or without `Exclude*` options.
- No `Cognitive Services *` roles on a Foundry resource, and not `Azure AI Developer`. Assign by
  GUID, because the Foundry role family was renamed with IDs unchanged.
- Service principal mode deliberately hides ARM discovery, so the smaller data-plane grant is enough.
- No in-process WPF transparent or layered-window pill, and no revert of the overlay to in-process.
- No MSI. MSIX is the Store path and free Microsoft signing is MSIX only.
- No NPU speech decoding. Cleanup uses the accelerator, speech stays on the CPU, and the measurement
  is in the file.
- Do not "fix" long-audio decoding or rewrite the channel downmix. Three hypotheses were tested
  against the real engine and all three were wrong.
- Do not remove the `SQLitePCLRaw.bundle_e_sqlite3` pin (CVE-2025-6965), and bump
  `ScribeDatabase.ExpectedSqliteVersion` deliberately when the package moves.
- Do not take hardware selection back from the Foundry Local SDK.
- Do not remove the `virtualization:ExcludedDirectory` in `build/pack-msix.ps1`, the
  `unvirtualizedResources` capability, or the `AppPaths` `VirtualizedRootDir` migration.
- `store.yml` is handed off by `gh workflow run` at the end of `release.yml`, deliberately not by an
  `on: release` trigger, because events raised by `GITHUB_TOKEN` do not start new workflow runs.

**Sections whose prose is a specification of live behavior**, so a code change in them is a doc
change too:

- **Overlay architecture.** The three-step `Scribe.Overlay.exe` resolution order (the
  `SCRIBE_OVERLAY_EXE` environment variable, then the installer layout at
  `AppContext.BaseDirectory\Overlay\Scribe.Overlay.exe`, then a dev fallback walking the repo),
  implemented at `src/Scribe.App/Overlay/OverlayProcessClient.ResolveOverlayExe`; the one-way named
  pipe; the by-name enum twins `Scribe.Core.Models.OverlayPosition` and `Scribe.Overlay.OverlayAnchor`;
  the position replay after every reconnect; the Job Object plus `--parent` watchdog.
- **Logging mandate.** The single shared daily file at
  `%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log`, `FileShare.ReadWrite` plus retry plus
  swallow, the `Debug` file sink with Microsoft, System, and Azure filtered to Warning, the session
  banner and matching `session end` line, per-dictation `#<n>` stamping with a stop reason, and the
  7 day / 16 MB per day / 64 MB total retention. Those numbers are also `PRIVACY.md` claims.
- **Azure authentication.** The two auth modes, the single `AzureCredentialFactory` owner, credential
  caching plus `AzureCredentialInvalidation.Invalidate()`, `AzureCliProcessCoordinator` serialization,
  the role GUIDs, the custom-subdomain requirement, and the role-propagation window.
- **Releases & Velopack.** One Velopack channel per architecture (`win-x64` and `win-arm64`) so an
  install only ever receives updates built for its own silicon; version derived from
  `Directory.Build.props`; branding read from the project file rather than hardcoded in the pack
  arguments.
- **Microsoft Store.** The identity values, which must match Partner Center exactly and live in
  `Directory.Build.props` (`StoreIdentityName`, `StoreProductDisplayName`, `StorePublisherDisplayName`);
  four-part MSIX versions with the revision reserved; the five repository secrets `store.yml` needs.
- **Architecture support.** Every project declaring `RuntimeIdentifiers=win-x64;win-arm64` and none
  pinning `PlatformTarget`; exactly one sherpa-onnx native package selected by `ScribeNativeRid`;
  the overlay built with a matching `-p:Platform=`; `scripts/Payload-Architecture.ps1` asserting
  payload purity at pack time.
- **Tech stack versions.** The prose names `Microsoft.WindowsAppSDK` 2.2.0, sherpa-onnx 1.13.4, and
  the deliberate `OpenAI` 2.12.0 hold, all of which are real values in `Directory.Packages.props`
  today. A version move in that file that leaves the prose behind is a finding at 🟡, and it is also
  an `Ask first` crossing that `merit` owns separately.

**The `Ask first` list has a documentation half that is yours.** `merit` owns whether the crossing
was flagged. You own whether the document that describes it moved:

- **A new third-party component.** `AGENTS.md` requires it to be license-compatible with MIT **and
  credited in the README attribution section**, and `CONTRIBUTING.md` repeats the requirement. A new
  runtime dependency with no entry under README `Licenses & attribution` is a 🟡 with a one-line fix.
- **A SQLite schema or migration change.** `PRIVACY.md` enumerates what a history entry may include;
  a new column carrying a new kind of user content makes that enumeration incomplete.
- **A signing-posture change.** `AGENTS.md`, `CONTRIBUTING.md`, and `README.md` all state that direct
  GitHub artifacts are intentionally unsigned and that packaging must not touch a certificate store.
  Three files, one claim.

## §4. The leaderboard and the shipped catalog must agree

`docs/model-leaderboard.md` is not decoration. The shipped catalog quotes it back at the user.

`CleanupModelCatalog.Curated` (`src/Scribe.Core/Cleanup/CleanupModel.cs:27`) carries a nullable
`Recommendation` whose doc comment states the rule outright: it *"is set only on the models the
golden-suite benchmark named as on-device winners (see docs/model-leaderboard.md); it is null for
everything else"*. Today two entries carry one, `mistral-nemo-12b-instruct` with `"Best on-device
balance"` and `phi-4` with `"Best on-device quality"`, and the leaderboard TL;DR table names exactly
those two: *"Fully offline, fastest usable"* is `mistral-nemo-12b-instruct` and *"Fully offline, best
quality"* is `phi-4`. `tests/Scribe.Core.Tests/CleanupModelCatalogTests.cs` pins the pair by alias
and by recommendation text (`Only_the_leaderboard_winners_carry_a_recommendation`,
`Winners_carry_the_expected_recommendation_text`).

Flag 🟡 when the diff creates a contradiction between the two, in either direction:

- A recommendation string moved, added, or removed in `CleanupModel.cs` with no matching row in the
  leaderboard TL;DR table, or vice versa. The settings picker then shows a badge nothing measured, or
  hides one the benchmark earned.
- `CleanupModelCatalog.DefaultAlias` changed while the leaderboard still names a different on-device
  pick, or a `Hint` quoting a download size or a latency the leaderboard contradicts. The hints are
  read by users choosing what to download.
- README's performance table and the leaderboard disagreeing. README currently states the cloud
  default as `gpt-5.4` at roughly 1.8 s median and grade B+, and the leaderboard TL;DR row says
  `gpt-5.4`, B+ (87), 1.82 s. A change to either that leaves the other behind is a finding.
- `AGENTS.md` calling `DefaultWritingStyle` the benchmark-validated optimum while the diff replaces
  the prompt without touching that sentence.

**Ownership split, so this does not become a duplicate.** `prompt-and-model` outranks you in the
SKILL.md Step 4 specificity order and owns two adjacent things: a hand-edited score, latency, or rank
inside the machine-generated report body, and a `Recommendation` string with no leaderboard row behind
it. **You own the contradiction between two documents, or between a document and shipped metadata,
where neither number was invented.** If `prompt-and-model` also fired, expect to be deduped, and write
your finding so the surviving one keeps your doc-side fix.

## §5. Countable claims, and the drift that already exists

`README.md` makes claims a reader can count, and each has a single referent in code. When the diff
moves the referent, the claim is a finding. When it does not, the claim is not your business.

| Claim | Referent, verified |
| --- | --- |
| "9 screen anchors", "9-anchor position picker" | `OverlayPosition` in `src/Scribe.Core/Models/Enums.cs` has exactly nine values. |
| "your last five dictations" | `LastTranscriptStore.Capacity = 5` (`src/Scribe.Core/PostProcessing/LastTranscriptStore.cs`). |
| "Diagnostic logs are kept for seven days" | `LogRetentionPolicy.DefaultRetentionDays = 7`. |
| Built-in dictionary libraries | One CSV per library under `src/Scribe.Core/PostProcessing/Libraries/`, embedded by the `*.csv` glob in `Scribe.Core.csproj` and loaded by resource name in `BuiltInDictionaryLibraries`. The file count **is** the library count. |
| Attribution list | README `Licenses & attribution` currently credits Parakeet, Moonshine, sherpa-onnx, and Silero VAD. |

**Known drift on `main`. Do not spend the cap re-reporting it.** Each of these is already wrong and
none of them is this change's finding unless the diff moves the same referent again:

- README says "Nine curated opt-in libraries" in two places; there are **eleven** CSV files under
  `src/Scribe.Core/PostProcessing/Libraries/` today.
- `CONTRIBUTING.md` says "all five projects, including the x64-only overlay"; `Scribe.slnx` declares
  **eight** projects and the overlay ships for both architectures.
- `CONTRIBUTING.md` lists the development requirement as "Windows 11 (x64)" and describes
  `build/pack.ps1` as publishing "a self-contained `win-x64` build"; both are stale against the
  documented x64 and ARM64 story.
- `CONTRIBUTING.md` names the SQLite pin as `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`;
  `Directory.Packages.props` pins 3.0.5. `AGENTS.md` states the same constraint as a floor ("at or
  above 3.0.3") and is therefore still true.
- `README.md` cites the leaderboard as "52 models" and as a "46-model golden suite" in adjacent
  paragraphs; the leaderboard header counts 24 cloud deployments plus 22 local models and its TL;DR
  says "46 graded models".

If a diff touches one of these referents, roll the correction into the finding you were already
writing rather than opening a second one. **A finding whose whole content is that a count is stale is
below this lens's bar** unless the diff is what made it stale.

## §6. The reverse direction: a document changed without the code

This is the docs-only path, and it is where a review is most likely to wave a change through.

- **A doc edit asserting behavior the code does not have.** Read the code. A setup guide that
  documents a script parameter, an environment variable, a role GUID, a settings field, or a menu
  item that does not exist sends a user down a dead end. `docs/foundry-setup.md` and
  `docs/service-principal-setup.md` both instruct users to run
  `scripts/Setup-ScribeFoundry.ps1` fetched by raw GitHub URL, so a documented flag that the script
  does not accept fails on the user's machine, not in CI.
- **A value duplicated across files, updated in one.** The `Foundry User` role GUID
  `53ca6127-db72-4b80-b1b0-d745d6d5456d` currently appears in `AGENTS.md`,
  `docs/foundry-setup.md`, `docs/service-principal-setup.md`, `scripts/Setup-ScribeFoundry.ps1`,
  `src/Scribe.Core/Cleanup/TextCleanupService.cs`, and
  `tests/Scribe.Core.Tests/AzureCleanupDiagnosticsTests.cs`. Six places. A diff that moves it in one
  or two of them is a partial conversion; grep for the literal and judge every survivor. Same shape
  for the Store identity values, which live in `Directory.Build.props` and in
  `docs/microsoft-store-submission.md` and must match Partner Center exactly.
- **A doc edit that contradicts another doc.** `AGENTS.md`, `README.md`, `CONTRIBUTING.md`, and
  `PRIVACY.md` overlap on the offline-first promise, the unsigned-artifact posture, and the retention
  numbers. An edit that changes one instance of a shared claim is incomplete by construction.
- **A doc edit that deletes an incident record.** These files carry paragraphs whose only job is to
  stop a decision being re-derived: the 3,775 false-positive hook probes, the `DefaultAzureCredential`
  IMDS probe, the three wrong long-audio hypotheses, the AppData virtualization correction. Removing
  one is not tidying. Flag it 🟡 and ask what replaced the knowledge.
- **A doc edit with no code change and no code referent at all.** Prose polish, a typo, a screenshot
  swap, a link fix. Stay silent. That is a clean pass, not an absence of effort.

## §7. Confidence bar

**Hard flag (a Finding)** only when all four hold:

1. You opened the document and can name the sentence.
2. You can point at the hunk in `diff.patch` that changes what the sentence describes.
3. The sentence is now false, or a required companion (README attribution, a shared constant, a
   twin value) is now missing.
4. The diff does not already fix it.

**Raise a Question** instead when any of these is true:

- The document is a benchmark report whose numbers depend on a run you cannot reproduce, and the
  leaderboard's own rule applies: rankings are deployment specific, so report endpoint, region, and
  SKU or do not assert.
- You believe an edit is material enough to move the `PRIVACY.md` effective date but cannot judge it.
- The doc-side wording is a product-voice decision rather than a factual correction.
- You can see that a claim and a hunk are related but cannot establish that the claim actually became
  false.

**Never** write a finding whose whole content is that a document is old, that a version number in
prose is behind, or that a section "could be clearer". `AGENTS.md` closes the version case itself:
*"Read `<VersionPrefix>` from that file rather than trusting a number quoted here; a version pinned
in prose is stale the next time anyone ships."* The same reasoning covers its own "878 as of 0.3.8"
test-count line, which is an as-of stamp and not a claim of currency.

**Never** assert that a build or a test will catch a doc-code disagreement. Nothing in this
repository checks prose against code; this lens is the check.

---

## Output format

The two findings below are **illustrative shapes**, not live defects. `ClipboardHandoff.cs` is an
invented path used only to show the format. Never cite either as an existing exemplar.

```markdown
## Documentation sync findings

🟡 **The new clipboard handoff path skips `MarkPrivate`, so the published policy is now false** `[needs-signoff]` (`src/Scribe.Core/TextInjection/ClipboardHandoff.cs:88`)

`PRIVACY.md` tells users, under "Clipboard and keyboard access", that "Clipboard writes that Scribe
performs itself are marked so Windows excludes them from clipboard history (Win+V) and from
cross-device cloud clipboard sync." The new handoff writes `CF_UNICODETEXT` directly and never calls
`Win32Clipboard.MarkPrivate`, so a dictation inserted through this path lands in Win+V history and
syncs to the user's other devices. A user who read the policy and left cloud clipboard on is misled
by a sentence that is live in the About page link (`ScribeLinks.PrivacyPolicy` points at
`blob/main/PRIVACY.md`) and registered in Partner Center as Scribe's Store privacy policy.

Note that no 🔴-capped lens covers this surface: `privacy-egress` does not trigger on
`src/Scribe.Core/TextInjection/**`, so this tag is the gate.

Two ways to resolve, and the choice is the maintainer's:

- Route the write through `Win32Clipboard.SetText` so `MarkPrivate` applies, and the policy stays
  true with no edit.
- Keep the direct write and amend the paragraph to scope the guarantee, then decide whether the
  effective date moves.

🟡 **`AGENTS.md` still documents the one-channel-per-architecture rule the pack script no longer follows** (`build/pack.ps1:214`)

The script now writes both architectures into a single Velopack channel. `AGENTS.md`, under
"Releases & Velopack", states: "One Velopack channel per architecture, `win-x64` and `win-arm64`, so
an install only ever receives updates built for its own silicon." That sentence is now false, and it
is the sentence the next agent will read before touching packaging: an ARM64 install offered an x64
delta does not crash, it silently runs emulated, which is the exact invisible failure the
"Architecture support" section says is enforced mechanically rather than by review.

Update both the "Releases & Velopack" bullet and the `vpk upload` example beneath it in the same PR.
`build-packaging` owns whether the channel change itself is correct; this finding is only about the
document that describes it.
```

**If clean:** "Documentation sync clean: every shipped document the change touches still describes
what the code does, `PRIVACY.md` still holds for what leaves the machine, what is logged, and what
touches the clipboard, and no settled decision in AGENTS.md was overturned without its record moving."

Trim that sentence to the documents the diff actually touched. Do not assert a document you did not
open.

---

## Exceptions

Do not raise any of the following.

- **`PRODUCT.md` as a factual claim.** It is a design brief: register, users, purpose, brand
  personality, anti-references, design principles, and accessibility targets. It says nothing about
  behavior that a code change can falsify. Whether the UI honors it is `ui-shell-quality`'s question.
  Fire on `PRODUCT.md` only when the diff edits it and the edit contradicts `AGENTS.md` or
  `PRIVACY.md`.
- **A missing release-notes file.** `docs/release-notes-*.md` exists for five versions only, nothing
  reads them, and no workflow or script references them. A version bump does not owe one.
- **`Scribe-0.2.x-Teams-Update.md`.** A one-off announcement covering 0.2.1 to 0.2.15. It is a
  historical artifact and is not maintained.
- **A version number quoted in prose.** `AGENTS.md` explicitly disclaims its own. `Directory.Build.props`
  `<VersionPrefix>` is the single source of truth.
- **The test count in `AGENTS.md`.** It is written as an as-of stamp with a note that the count only
  grows.
- **Numbers inside the machine-generated body of `docs/model-leaderboard.md`.** That region is
  rendered by `Benchmark/LeaderboardWriter.cs`. Hand edits there belong to `prompt-and-model`; the
  hand-written prose above it (revision notes, TL;DR, key findings) is meant to be edited.
- **The pre-existing drift listed in §5**, unless the diff moves the same referent again.
- **A screenshot that no longer matches the current UI**, unless the diff renames or removes the
  feature the caption describes. Screenshot refresh is its own task, and README embeds ten images
  from `docs/screenshots/`.
- **Prose style, tone, heading order, or table formatting.** `comment-and-dash-hygiene` owns the
  U+2014 and U+2013 ban across docs; you own truth, not craft.
- **A doc that documents a feature the diff is removing**, once the diff removes the doc section too.
  Verify the removal is complete, then acknowledge it.
- **Anything already fixed inside the same diff.** Grep the patch for the document before writing the
  finding. Acknowledge it in "What's good" instead.
- **A doc the diff merely reformatted, moved, or spell-checked** with no claim changed.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:docs-sync findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
