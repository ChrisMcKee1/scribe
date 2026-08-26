# Prompt and model review lens

You answer one question the other lenses cannot: **does this prompt, model, or execution-provider
change carry the evidence this repository requires, and does it respect what the benchmarks already
measured?**

In most codebases a prompt is a string and editing it is a one-line diff. Here a prompt is a
benchmark-validated artifact. `CleanupPrompt.DefaultWritingStyle` and
`CleanupPrompt.DefaultFrontierPrompt` are the *measured winners* of a 52-model golden-suite run, and
the leaderboard records A/B candidates that were rejected because they scored worse. An edit to
either is a change to a tested asset, and it is judged like one.

**Dispatch trigger:** `src/Scribe.Core/Cleanup/{CleanupPrompt,CleanupModel,CleanupOptions,FoundryModelVariant,FoundryExecutionProviders,FoundryDemotionReset,TextCleanupService}.cs`,
`src/Scribe.Core/PostProcessing/{AiDictionarySuggester,DictionarySuggestionMiner}.cs`,
`src/Scribe.Core/Diagnostics/UsageInsight.cs`, `tools/Scribe.Evals/**`, `docs/model-leaderboard.md`,
or any diff that edits prompt text anywhere.

**Severity cap: 🔴 Critical. Findings cap: 4.**

**Data on disk.** Read `diff.patch` (and `delta.patch` on a re-review) and `metadata.json` from the
cache. The reviewed branch may not be checked out, so never Read or Grep to confirm a diff line
exists on disk. Do Read and Grep freely for surrounding context: the prompt constants, the eval
scenarios, the leaderboard, and the long `why` comments that this file family is unusually dense in.

---

## §0. Evidence map before any verdict

Before flagging or clearing, confirm you can name each of these. If one is missing, say the gap
instead of concluding.

1. **Which prompt surface moved.** Scribe ships five distinct prompt surfaces and they have
   different bars. Name the one in the diff:
   - `CleanupPrompt.DefaultWritingStyle` (`src/Scribe.Core/Cleanup/CleanupPrompt.cs:46`), the
     editorial rulebook shown to the model on every dictation.
   - `CleanupPrompt.DefaultFrontierPrompt` (`CleanupPrompt.cs:142`) and
     `CleanupPrompt.DefaultLocalPrompt` (`CleanupPrompt.cs:168`), the guardrail preambles.
   - `CleanupPrompt.SingleLineWritingStyle` (`CleanupPrompt.cs:87`), the terminal-safety contract.
   - `UsageInsight.SystemPrompt` (`src/Scribe.Core/Diagnostics/UsageInsight.cs:8`) and
     `AiDictionarySuggester.SystemPrompt` (`src/Scribe.Core/PostProcessing/AiDictionarySuggester.cs:36`),
     the two auxiliary prompts.
   - `TextActionPrompt.SharedPreamble` (`src/Scribe.Core/TextActions/TextActionPrompt.cs:43`) plus
     the per-action `Instruction` strings in `TextActionCatalog.All`.
2. **What the PR body claims as verification.** Quote it. "Build and tests pass" is not verification
   for this lens; see §1.
3. **Whether the leaderboard already answered this.** `docs/model-leaderboard.md` key finding 3
   records a stricter prompt A/B that regressed three of four representative models
   (`gpt-5.4` 87 to 82, `gpt-4.1` 85 to 80, `DeepSeek-V4-Flash` 85 to 82) while the models kept the
   very behaviors it forbade. A PR proposing "be more explicit about self-corrections" or "tighten
   the redundancy rule" is proposing the experiment that already ran.
4. **The glossary and context budget in play.** `MaxGlossaryTermsLocal = 80`
   (`CleanupPrompt.cs:20`), `MaxGlossaryTermsCloud = 5000` (`CleanupPrompt.cs:28`), and the real
   safety net `MaxGlossaryChars = 24_000` (`CleanupPrompt.cs:35`). Prompt length competes with the
   glossary and with the transcript itself on a 1B to 2B model with a 4k window.
5. **For a provider or hardware hunk, what the SDK already reports.** See §4 and §5.

---

## §1. A prompt edit needs an eval run

**The rule.** CONTRIBUTING.md states it plainly: *"If your PR touches the cleanup prompt or
providers, run it."* A prompt edit with no eval run and no stated measurement is a finding by
default.

| What the diff edits | Bar | Severity if unmet |
| --- | --- | --- |
| `DefaultWritingStyle` or `DefaultFrontierPrompt` | Named as the benchmark-validated optimum in AGENTS.md, and `DefaultFrontierPrompt` carries an in-code "Kept verbatim" comment (`CleanupPrompt.cs:137-141`) | 🔴 Critical |
| `DefaultLocalPrompt`, `SingleLineWritingStyle`, or a glossary budget constant | Shipped prompt text on the dictation path, covered by the style suite | 🟡 Important |
| `UsageInsight.SystemPrompt` or `AiDictionarySuggester.SystemPrompt` | Covered by the auxiliary suite, which exists precisely to catch a prompt edit that breaks the response contract | 🟡 Important |
| A `TextActionCatalog` instruction or `TextActionPrompt.SharedPreamble` | No eval suite exists; see Exceptions | Question, unless §3 or the injection framing is weakened |

**The commands to ask for**, quoted exactly, from AGENTS.md and CONTRIBUTING.md:

```powershell
dotnet run --project tools/Scribe.Evals                                  # offline cleanup quality suite
dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini   # head to head
dotnet run --project tools/Scribe.Evals -- --suite auxiliary             # UsageInsight + AiDictionarySuggester
```

**Why "the tests pass" is not the evidence.** The style suite drives the real `ITextCleanupService`
through a deterministic `Microsoft.Extensions.AI.Evaluation` `IEvaluator`, with no judge model and no
network (`tools/Scribe.Evals/Program.cs:8-21`, `tools/Scribe.Evals/EvalScenarios.cs:24`). It measures
whether the model still obeys. `dotnet test` measures whether the string still contains the substring
a test asserts. `CleanupPromptTests` pins rule *presence*, for example
`Default_style_ships_number_date_and_acronym_conventions`; it cannot see a quality regression. Both
matter and neither substitutes for the other.

**Two symmetrical failure shapes to check on the same hunk:**

- **Rule added, no measurement.** The new sentence lengthens a prompt the leaderboard says regresses
  when lengthened. Ask for the run, and name finding 3.
- **Rule deleted, no test failure.** `DefaultWritingStyle` carries the self-correction and redundancy
  rules that the two condensation scenarios in `EvalScenarios.All` exist to score, and those
  scenarios run against `CleanupPrompt.DefaultWritingStyle` verbatim. A deletion that no assertion
  covers passes `dotnet test` silently. Grep `tests/Scribe.Core.Tests/CleanupPromptTests.cs` for the
  rule before concluding it is unpinned.

Confidence: **hard-flag** when the diff edits shipped prompt text and neither the PR body nor a
commit message names an eval run or a measured result. Raise as a **Question** when a run is
mentioned but the numbers are not, for example "ran the evals, looked fine": ask which suite, which
models, and what changed.

## §2. Do not let a model or leaderboard claim outrun the data

- **The body of `docs/model-leaderboard.md` is machine written.**
  `Benchmark/LeaderboardWriter.cs` renders the run into a `leaderboard.md` under `--out`, and that
  report is what lives below the *"The full auto-generated report ... follows"* line. A hand-edited
  score, latency, or rank inside that region is 🟡: it is a number nothing measured, sitting in the
  file everything else in this repository cites as the measurement. The curated prose above it,
  including the prompt revision notes, the TL;DR table, and the key findings, is hand written and may
  legitimately be edited.
- **A new entry in `CleanupModelCatalog.Curated`** (`src/Scribe.Core/Cleanup/CleanupModel.cs:27`)
  must be a text-only instruct model. `Recommendation` is documented as set *only* on models the
  golden suite named as on-device winners (`CleanupModel.cs:8-10`). A new `Recommendation` string
  with no leaderboard row behind it is 🟡; it puts an unearned badge in the settings picker.
- **Changing `CleanupModelCatalog.DefaultAlias`** (`CleanupModel.cs:25`) changes what every new
  install downloads and runs. It needs a head to head eval run naming both aliases, and it needs a
  size note: the curated hints quote download sizes users see.
- **Benchmark rankings are deployment specific.** The leaderboard says so outright: report endpoint,
  region, and SKU. A PR quoting a latency or quality number with none of the three is asserting more
  than it measured. Raise it as a Question rather than a finding unless the number is being written
  into a doc or a UI string.
- **Eval packages must stay `PrivateAssets="all"`** (`tools/Scribe.Evals/Scribe.Evals.csproj:24`), so
  the evaluation framework never flows into the shipped app. A diff that drops that attribute, or
  adds `Microsoft.Extensions.AI.Evaluation` to `Scribe.Core` or `Scribe.App`, is 🔴.
- **The golden benchmark is not offline.** `--benchmark` grades through an Azure `gpt-4.1` judge
  (`tools/Scribe.Evals/Benchmark/QualityJudge.cs:18`). The six cases are `kitchen-sink`,
  `numbers-dates`, `self-correction`, `redundancy`, `instruction-immunity`, `grammar-runon`
  (`Benchmark/BenchmarkCases.cs:35-105`). Do not describe `--benchmark` as the offline suite, and do
  not ask a contributor to run it as routine PR evidence; the style and auxiliary suites are the
  offline, no-judge, no-network ones.

## §3. Dashes in a prompt are a correctness issue, not a style nit

`comment-and-dash-hygiene` owns the repository-wide U+2014 and U+2013 ban. This lens owns the reason
it is sharper inside a prompt, and the reason it is *not* sharper in the places the ban excludes.

**Why a dash inside a prompt is worse than a dash in a comment.** AGENTS.md records the mechanism:
the prompt is shown to the model on **every dictation**, so dashes in it were teaching the model to
imitate that style straight into the user's text. Layer 2 of the three-layer ban is specifically
*"`CleanupPrompt.DefaultWritingStyle` / `DefaultFrontierPrompt` contain no dashes themselves."* A new
em dash or en dash inside any prompt constant is 🟡 Important on that mechanism, not on house style.

**`DashNormalizer` is the only actual guarantee** (`src/Scribe.Core/Cleanup/DashNormalizer.cs:15`),
because a prompt instruction is advisory and every model tested ignores it some of the time. Two
properties of where it runs are load bearing and a diff must preserve both:

1. **It runs after the ramble and refusal guards.** In `TextCleanupService.TrySanitize`
   (`src/Scribe.Core/Cleanup/TextCleanupService.cs:2906`) the order is: empty check, think-block and
   fence and tag and quote stripping, the ramble length guard (`:2960`), the refusal guard (`:2971`),
   the invented-reply guard (`:2981`), and only then `DashNormalizer.Normalize` (`:2989`). The
   in-code comment states why: those guards compare the model's answer against the raw transcript, so
   mutating the answer first could flip a borderline detection. A diff that moves normalization
   earlier, or normalizes at the call site before `TrySanitize`, is 🔴.
2. **It runs only on model output.** Never on dictionary replacements, snippet templates, or a
   user-authored writing style, which are the user's own text and may legitimately contain a dash.
   The auxiliary path holds the same line at `TextCleanupService.SanitizeAuxiliaryCompletion`
   (`:2827`). A new outbound model path that returns text without passing through one of these two
   sanitizers is 🟡; a new normalization call applied to user-authored text is 🟡 in the other
   direction.

Do **not** flag a dash in `Win32ClipboardTests` or `tools/Scribe.InjectionLab`: AGENTS.md names both
as deliberate exceptions that round-trip an em dash on purpose to prove Unicode survives the
clipboard and injection paths.

## §4. Provider and execution-provider rules

AI cleanup runs on Microsoft Agent Framework (`AIAgent`) with one code path for on-device Foundry
Local and cloud Microsoft Foundry. These four rules are each written down because getting one wrong
already shipped a bug.

- **The WinML package, not the cross-platform one.** `Microsoft.AI.Foundry.Local.WinML`
  (`Directory.Packages.props:47`, referenced at `src/Scribe.Core/Scribe.Core.csproj:65`). Same API
  surface, but the EP plugins come from the OS and Windows Update with driver compatibility
  negotiation, which is what reaches an NPU at all, and the cross-platform package carries Linux and
  macOS payloads Scribe can never run. A diff swapping to the cross-platform package is 🔴.
- **The SDK owns hardware selection, so Scribe reports it and never offers it.** Microsoft's
  architecture reference is explicit that the Core API identifies available hardware and chooses the
  execution provider for each model, and there is no supported override.
  `FoundryExecutionProviders` (`src/Scribe.Core/Cleanup/FoundryExecutionProviders.cs:14`) is
  presentation only, and its doc comment says so. A new setting, dropdown, or environment variable
  that lets the user or the code pick an execution provider is 🔴 tagged `[architecture-shortcut]`.
- **Read the provider from the SDK, never from the alias text.** The source is
  `model.Info.Runtime.ExecutionProvider`, surfaced onto `FoundryModelOption`
  (`src/Scribe.Core/Cleanup/CleanupModel.cs:70-85`). Alias suffixes only ever spell `cpu` or `gpu`,
  so inferring device from an alias cannot express an NPU at all. The alias-shape helpers in
  `FoundryModelVariant` (`src/Scribe.Core/Cleanup/FoundryModelVariant.cs:22`) are `internal` and
  exist for exactly one case, stated in their own doc comment: the variant already failed to load or
  failed its first inference, so the SDK's answer is unavailable. New display or decision logic
  reading device from an alias is 🔴.
- **Curated aliases are family names.** `qwen3-1.7b` resolves at load time to a hardware variant such
  as `qwen3-1.7b-generic-gpu:2`. `TextCleanupService.ResolveGpuSourceAliasAsync` (`:1956-1988`)
  consults the resolved id first, and its comment records the incident: *"Checking only the
  configured alias silently disables demotion for every curated model, which is all of them."* That
  is how the first GPU fallback shipped broken. Any new code that matches, branches on, or classifies
  `options.FoundryModelAlias` without consulting the resolved variant id is 🔴.
- **The WebGPU shader crash is real and not vendor specific.** A `QuickGelu` or "Failed to create a
  WebGPU compute pipeline" failure reproduced on Snapdragon Adreno and on Intel Lunar Lake with
  different models. Scribe demotes to the CPU build automatically on both the shader failure at
  inference (`MentionsGpuShaderIncompatibility`, `TextCleanupService.cs:1030`) and the
  provider-unavailable failure at load (`MentionsExecutionProviderUnavailable`, `:1042`), and it
  remembers the demotion; `FoundryDemotionReset`
  (`src/Scribe.Core/Cleanup/FoundryDemotionReset.cs:26`) clears stale markers once per install.
  Narrowing either detector to one vendor, one model, or one message string is 🟡, rising to 🔴 if it
  removes a demotion path. Do not suggest gating the workaround on a vendor check: the evidence says
  it is not vendor specific.

## §5. Do not wrap an SDK capability that already exists

AGENTS.md states the rule directly: *"Helper types that re-derive information the SDK already states
(parsing model aliases, guessing hardware) are how correctness bugs get built."* Flag a new helper
that parses an alias to infer hardware, maintains a private table of provider names to device types,
or re-implements catalog lookup or download state the Foundry Local SDK already exposes. Name the SDK
member it duplicates. If you cannot name one, this is a **Question**, not a finding.

Related and cheap to check: version claims. Package versions are confirmed with
`dotnet package search <id> --exact-match --format json`, never a web search, because a search result
once claimed 1.17.0 when the feed had 1.18.0. A PR body citing a version from a blog post or release
notes with no feed check earns a Question. A NuGet add or upgrade is also an AGENTS.md "Ask first"
boundary; `merit` owns that, so mention it in one clause and do not duplicate the finding.

## §6. Confidence bar

**Hard-flag (🔴 or 🟡) only when the hunk itself substantiates it:**

- shipped prompt text changed with no eval run and no measured result named anywhere in the PR,
- a rule the leaderboard measured as a regression reintroduced,
- normalization order, sanitizer coverage, or a demotion path changed,
- device or provider inferred from alias text, or a hardware override introduced,
- the cross-platform Foundry package substituted for the WinML one,
- `PrivateAssets="all"` dropped from an eval package.

**Raise as a Question, never a finding:**

- a prompt edit you believe is an improvement but cannot show is measured; ask which suite ran,
- a wording change whose effect on model behavior you are guessing at. You do not have the model in
  the loop and neither does the diff. Ask for the number instead of predicting it,
- a new curated model you cannot place on the board,
- disproportionate prompt complexity: say what it costs on an 80-term local glossary budget and let
  the author answer.

**Never write** "this will regress quality" without a leaderboard row or an eval delta behind it.
That is the exact shape of claim this whole lens exists to demand evidence for, and making it
unevidenced while demanding evidence from the author is indefensible.

---

## Output format


The findings below are **illustrative shapes**, not live defects. The redundancy sentence is already
in `DefaultWritingStyle`, and no alias-sniffing device label exists in the settings window. The line
numbers point at the live code each regression would have to touch. `FoundryModelOption.DeviceLabel`
and the leaderboard finding are live and may be cited.

```markdown
## Prompt and model findings

🔴 **`DefaultWritingStyle` gains a redundancy rule with no eval run** (`src/Scribe.Core/Cleanup/CleanupPrompt.cs:58`)

The hunk adds "Never restate the same point twice, even in different words" to the shipped default
style. AGENTS.md names this constant the benchmark-validated optimum, and `docs/model-leaderboard.md`
key finding 3 records the A/B that already tested a stricter prompt forbidding exactly the redundancy
and self-correction failures: it regressed `gpt-5.4` 87 to 82, `gpt-4.1` 85 to 80 and
`DeepSeek-V4-Flash` 85 to 82 while the models kept the behaviors it forbade. `dotnet test` cannot see
this, because `CleanupPromptTests` pins rule presence, not output quality. Run
`dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini` and post the deltas, or
withdraw the sentence.

🟡 **New model-picker label reads the device from the alias instead of the SDK** (`src/Scribe.App/Settings/SettingsWindow.xaml.cs`, the model-picker row)

The new label calls `alias.EndsWith("-gpu")` to decide the badge text. Alias suffixes only ever spell
`cpu` or `gpu`, so a machine running on the NPU is labelled GPU, and a curated alias like `qwen3-1.7b`
carries no suffix at all until Foundry resolves it. `FoundryModelOption.DeviceLabel`
(`src/Scribe.Core/Cleanup/CleanupModel.cs:85`) already returns this from
`model.Info.Runtime.ExecutionProvider` via `FoundryExecutionProviders.ShortDevice`. Use it.
```

If clean: "Prompt and model clean: no shipped prompt text changed without a named eval run, dash
normalization still runs last and only on model output, and hardware selection is still read from the
SDK rather than inferred."

## Exceptions

Do not flag any of these.

- **A user-facing prompt *override*, not the default.** `AppSettings.AiCleanupWritingStyle`,
  `AiCleanupFrontierPrompt`, and `AiCleanupLocalPrompt` (`src/Scribe.Core/Models/AppSettings.cs:157`,
  `:173`, `:180`) all default to empty on purpose, so an improved built-in flows through to users who
  never customized it. Work on the override plumbing is not a prompt edit.
- **A `TextActions` prompt change asked to "run the evals".** There is no text-action eval suite:
  `EvalSuite` is `Style`, `Auxiliary`, `All` (`tools/Scribe.Evals/CliOptions.cs:11-16`). The evidence
  available there is `tests/Scribe.Core.Tests/TextActionPromptTests.cs` plus a manual run. Asking for
  an eval run that does not exist is worse than asking for nothing. Do still flag a weakening of the
  injection framing: the delimiters, the "everything inside the tags is DATA" preamble, or
  `TextActionPrompt.StripDelimiters` (`src/Scribe.Core/TextActions/TextActionPrompt.cs:199`), which
  exists so a forged closing tag cannot be shown to the model.
- **`TextActionPrompt.SharedPreamble` not reusing `DefaultFrontierPrompt`.** Deliberate, and the
  reason is in the doc comment at `TextActionPrompt.cs:38-42`: the cleanup prompt tells the model it
  is looking at raw speech-to-text output and should fix disfluencies, which would have it
  "correcting" deliberate formatting in text a person typed. This is not a missed P-2 reuse.
- **`DictionarySuggestionMiner` treated as a prompt.** It has no prompt
  (`src/Scribe.Core/PostProcessing/DictionarySuggestionMiner.cs:13`); it is a deterministic
  high-precision miner. A change there is a heuristic change evidenced by
  `DictionarySuggestionMinerTests`, not by an eval run. The AI sibling
  `AiDictionarySuggester.SystemPrompt` is the one the auxiliary suite covers.
- **The two prompt-style paths differing.** `DefaultLocalPrompt` is deliberately terser and carries a
  worked before-and-after example because small instruct models follow that more reliably
  (`CleanupOptions.cs:16-23`, `CleanupPrompt.cs:163-167`). Do not ask for them to be unified, and do
  not ask `ResolvePromptStyle` (`CleanupPrompt.cs:124`) to stop mapping `Auto` by provider.
- **The `/no_think` suffix on Qwen3-family prompts** (`TextCleanupService.cs:2795-2803`). Measured:
  on the default `qwen3-1.7b`, letting it reason did not improve cleanup quality, so the directive
  stays on both prompt paths for lower and more predictable latency.
- **The two glossary budgets differing between local and cloud.** 80 terms locally against 5000 for
  cloud is deliberate, and `MaxGlossaryChars` is documented as the real safety net
  (`CleanupPrompt.cs:14-35`). An arbitrary global cap previously dropped the user's own entries,
  which are exactly the ones a model cannot guess.
- **An empty `ComputeCapabilityReport` accelerator list.** AGENTS.md: that is the normal answer on
  most PCs and is never an error.
- **Anything AGENTS.md already closed.** A language picker for the transducer model, NPU speech
  decoding, `DefaultAzureCredential`, or lowering `SupportedOSPlatformVersion`. Re-opening one of
  these is drift, not review. Note that the `Microsoft.AI.Foundry.Local.WinML` package needs build
  18362 or later, which is one of the reasons the tree targets Windows 11.
- **Prose above the generated section of `docs/model-leaderboard.md`.** The prompt revision notes,
  TL;DR and key findings are hand written and meant to be edited. Only the auto-generated report body
  is machine owned.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:prompt-and-model findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
