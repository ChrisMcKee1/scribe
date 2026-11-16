---
name: scribe-code-review
description: Multi-lens, multi-model code review for Scribe (private offline push-to-talk dictation for Windows 11 in C#/.NET 10, WPF tray shell, separate WinUI 3 overlay process, Scribe.Core + xUnit, plus the native Swift 6 macOS port in macos/Scribe). Built for GitHub Copilot App and GitHub Copilot CLI across Gemini 4 Carbon, GPT-6.1 Sol, Grok 4.7, and Claude Sonnet 5.5. Reviews a working tree diff, branch comparison, or GitHub PR; strictly read-only on tracked files.
---

# Scribe code review

This skill runs a structured, multi-lens, multi-model review over a change to **Scribe** in **GitHub
Copilot App** or **GitHub Copilot CLI**. It resolves the review target into a workspace-local cache under
`artifacts/scribe-code-review/<run-id>/`, fans out read-only custom specialist agents across up to four
model families in strict priority order, adjudicates findings adversarially, and renders one consolidated
review.

Scribe is not a web app. The things that break here are Win32 hooks under a hard OS deadline, a separate
WinUI 3 overlay process driven over a one-way named pipe, a shared daily log that must never throw, a
word-pack journal and vocabulary hand-off (`TryHandOff`) bound to exact file bytes, a fail-closed privacy
policy, two Windows CPU architectures (`win-x64` and `win-arm64`), and a native Swift 6 macOS port
(`macos/Scribe`). Read [`AGENTS.md`](../../../AGENTS.md) and
[`references/copilot-agent-dispatch.md`](references/copilot-agent-dispatch.md) before dispatching.

---

## Review philosophy

- **High signal, low noise.** Report only findings at **>= 80% confidence**. No style nits and no
  "consider using X" outside the Design assessment. Every finding must prevent a bug, fix an architecture
  violation, protect a stated guarantee (offline, privacy, non-throwing logging, warning-clean), or name a
  missing test that matters.
- **Understand before judging.** Read the surrounding code and its `why` comments with `view` and `grep`.
  Many Scribe shapes exist to prevent a shipped failure recorded three lines above the hunk; deleting one
  of those `why` comments while keeping the code is itself a finding.
- **Re-verify when challenged.** If the user pushes back on any verdict, re-read the exact code, callers,
  siblings, and patch hunks before answering.
- **Judge the design, not only the lines.** Every non-trivial change gets an **Architecture verdict** and
  a **Design assessment**, even when zero line findings survive.
- **A green build proves very little here.** Three defects in one Scribe release compiled warning-clean
  and failed only at runtime (`MissingMethodException`, an Azure probe token floor, and a throwing theme
  watcher). Never write "this will fail the build" or accept "tests pass" alone for a provider SDK,
  settings window, overlay, native speech, or startup change.
- **No em dashes or en dashes.** U+2014 and U+2013 are banned across the repository and in everything this
  skill or its lenses write. Use commas, colons, periods, or "to" for ranges. ASCII hyphens (`-`) are fine.

---

## Review surfaces

- **26 specialist and adjudicator lenses** in `agents/<lens>.md`, synced 1:1 to read-only Copilot custom
  agents in `.github/agents/<lens>.agent.md` (`tools: ["read", "search"]`, `user-invocable: false`) via
  `scripts/Sync-ReviewAgents.ps1`.
- **Canonical patterns rubric** in `references/patterns.md` (P-1 to P-12).
- **Copilot App & CLI dispatch contract** in `references/copilot-agent-dispatch.md`.
- **Empirical derived rules** in `docs/derived-rules/*.md` (`candidate` / `active` / `retired`), applied
  exclusively by `learned-patterns`.

---

## Step 1: Resolve the target and build the workspace review cache

Resolve one of three targets from the user's request without guessing:

| Target | Example request | Cache command (`pwsh`) |
| --- | --- | --- |
| Working tree | "review my changes", "review the diff" | `pwsh ./scripts/New-ReviewCache.ps1 -Target WorkingTree [-SinceSha <sha>]` |
| Branch | "review this branch", "review feat/x against main" | `pwsh ./scripts/New-ReviewCache.ps1 -Target Branch -BaseRef main -HeadRef <branch> [-SinceSha <sha>]` |
| GitHub PR | "review PR 61", or a PR URL | `pwsh ./scripts/New-ReviewCache.ps1 -Target PullRequest -PullRequest <n> [-SinceSha <sha>]` |

**Critical rules for Step 1:**

- **Cache inside `<repo-root>\artifacts\scribe-code-review\<run-id>\`, NEVER `$env:TEMP`.** GitHub Copilot
  App sub-agents scope `view`, `grep`, and `glob` to the repository workspace and reject `$env:TEMP` in
  autopilot/background runs. `artifacts/` is already gitignored in `.gitignore`, so `git status` stays
  clean while every sub-agent reads cache files directly via `view`.
- **If shell execution is unavailable in the host**, build the equivalent files under
  `artifacts/scribe-code-review/<run-id>/` using read-only `git` (`git --no-pager diff`, `git status --porcelain -uall`,
  `git --no-pager log --oneline -20`) and `create`. Never fall back from a missing `BaseRef` in Branch mode
  to `git diff HEAD`.
- **`gh` account preflight on PR targets.** `ChrisMcKee1/scribe` fails under the `chrismckee_microsoft` EMU
  account. Check `gh auth status` first; if `ChrisMcKee1` is not active, tell the user and ask them to run
  `gh auth switch --user ChrisMcKee1` before continuing. Never switch accounts silently.
- **Non-zero `gh pr checks` exit codes.** `gh pr checks` exits `8` while checks are pending and `1` when
  any check failed while still printing valid JSON. Preserve that JSON in `checks.json` rather than
  discarding it as `[]`, and always pass `--paginate` on `gh api` review/comment endpoints.
- **Untracked files in WorkingTree mode.** `git diff HEAD` omits `??` files; `New-ReviewCache.ps1` appends
  non-ignored untracked leaf files into `untracked.patch` and `diff.patch`. Either include untracked files
  or explicitly note excluded files in the Summary.
- **Patch vs working tree.** `diff.patch` (or `delta.patch` on round `N > 1`) is authoritative for what
  changed because the reviewed branch may not be checked out. Sub-agents must never use `view` or `grep`
  to verify a diff line exists on disk, and should use `view`, `grep`, and `glob` freely for surrounding
  code, callers, `AGENTS.md`, and `why` comments.

## Step 1.5: Detect the round and prepare the comment digest

- **PR target:** compare `reviews.json` and `issues-comments.json` authors against `viewer.json.login`.
  Round `N` is the count of prior formal reviews authored by us plus one; `since_sha` is the `commit_id`
  or `Reviewed at <sha>` of our latest review. Write substantive non-author, non-bot comments (>= 30 chars)
  to `prior-comments-digest.md`, or `"No prior review comments."` when empty.
- **Local or branch target:** Round 1 unless a prior review was rendered earlier in this session; if so,
  pass `-SinceSha <prior-sha>` to produce `delta.patch`. On round `N > 1`, lenses review `delta.patch`
  with `diff.patch` as context.

## Step 2: Select lenses

Match changed paths and diff content against the [Lens inventory](#lens-inventory). Every matched lens
runs in Pass A. Do not skip a matched lens expecting a clean pass. If the diff touches only `docs/**`,
`*.md`, or `<VersionPrefix>`, run `merit`, `comment-and-dash-hygiene`, and `docs-sync` only, and render
Architecture as `n/a`.

## Step 2.5: Orchestrator framing pass (skip for trivial diffs <20 lines, 1 file, no behavior/UI change)

Run a quick `grep` partial-conversion audit before dispatching and pass concrete hints to matching lenses:

- **Twin enums & pipe verbs:** `OverlayPosition` vs `OverlayAnchor`, and `OverlayPipeProtocol.Verbs` vs
  `OverlayIpcServer.Dispatch` (`OverlayPipeProtocolTests`).
- **`AppSettings` growth (P-7):** `CreateDefault` vs property initializers (`CreateForExistingInstall` for
  unreadable loads), deep copy in `Clone` (`Profiles`, `EnabledDictionaryLibraryIds`), `DraftSnapshot` /
  dirty tracking / `SettingsSavePreflight`, and `[JsonConverter(typeof(DpapiProtectedStringConverter))]` on
  secrets.
- **Word packs & vocabulary admission:** `LibraryPrecedence.BuiltInOrder` + `built-in-precedence.json`,
  `libraries.state` truth vs `EnabledDictionaryLibraryIds` projection, and every new cleanup client routed
  through `VocabularyHandOffHandler` / `TryHandOff`, `FeatureUsageTokenHandler`, and
  `WithStoredOutputDisabled`.
- **SQLite & architecture:** `SchemaVersion` held at `7` with new columns in `EnsureAdditiveSchema` and new
  tables in `SalvageTables`; `RuntimeIdentifiers=win-x64;win-arm64`, zero `PlatformTarget`, `-p:Platform=`
  on `Scribe.Overlay`; `PerfFlags` registered in `KnownNames` and read in `src/`; and `Scribe.Core` changes
  checked against `macos/Scribe` and `macos/PORTING-PLAN.md`.

---

## Step 3: Dispatch across the ordered 4-model roster

### Ordered 4-model roster (authoritative)

Every sub-agent dispatch in GitHub Copilot App and GitHub Copilot CLI **must** follow this exact priority
order and pass `model`, `reasoning_effort`, and `context_tier: "long_context"` explicitly on every `task`
call (see [`references/copilot-agent-dispatch.md`](references/copilot-agent-dispatch.md) for why omitting
them lets `/subagents` preferences silently override `.agent.md` defaults):

| Priority | Model | Exact `model` ID | `reasoning_effort` | `context_tier` | Assigned work |
| --- | --- | --- | --- | --- | --- |
| **1** | Gemini 4 Carbon | `gemini-4-carbon` | `"high"` *(max supported)* | `"long_context"` | **Majority of the work (Pass A):** 23 of 24 Pass A specialist lenses. |
| **2** | GPT-6.1 Sol | `gpt-6.1-sol` | `"max"` | `"long_context"` | **Adversarial reviews & Pass B panels:** post-synthesis adversarial `finding-verification` + Pass B cross-family confirmation (`architecture-fit`, `win32-interop`, `privacy-egress`, `word-packs-and-vocabulary`, `settings-and-persistence`, `tests-quality`) + optional built-in `rubber-duck`. |
| **3** | Grok 4.7 | `grok-4.7` | `"high"` | `"long_context"` | **Pass C 3rd-family confirmation & alternatives:** `solution-alternatives` in Pass A + Pass C confirmation (`overlay-process-contract`, `fragile-area`, `win32-interop`, `privacy-egress`, `macos-port`) + optional built-in `security-review` (or `finding-verification` if `gpt-6.1-sol` authored most findings). |
| **4** | Claude Sonnet 5.5 | `claude-sonnet-5.5` | `"max"` *(or `"xhigh"` if `"max"` is rejected)* | `"long_context"` | **4th-family adjudicator & ordered fallback:** post-synthesis `maintainer-decision` preflight gate + Pass D tie-breaker on critical changes + ordered fallback if Rank 3 (`grok-4.7`) is unavailable or returns empty after retry. |

Never pass `"auto"`, never invent model IDs, and report the exact model IDs and task counts in the Summary.

### Path A (default): In-session `task` tool (GitHub Copilot App & interactive Copilot CLI)

Invoke each matched custom agent in `.github/agents/<lens>.agent.md` with `mode: "background"`, launching
independent lenses in parallel batches and reading each completed agent once with
`read_agent(agent_id: "<id>", wait: true)` after completion notification:

```json
{
  "agent_type": "<lens>",
  "name": "pass-a-<lens>",
  "description": "Run <lens> review lens",
  "mode": "background",
  "model": "gemini-4-carbon",
  "reasoning_effort": "high",
  "context_tier": "long_context",
  "prompt": "Run the <lens> review lens.\n\n## Target\n- Kind: <WorkingTree|Branch|PullRequest>\n- Head SHA: <sha>\n- Round: <N>\n- Cache directory: <abs-path-to-artifacts/scribe-code-review/run-id>\n\n## Patch in scope\nRead <cache>/diff.patch (or delta.patch on round > 1) with view. Read AGENTS.md and surrounding files with view, grep, and glob.\n\n## Description & Prior Comments\nRead <cache>/metadata.json and <cache>/prior-comments-digest.md with view.\n\n## Framing hints\n<Step 2.5 hints or 'None.'>\n\n## Output rules\n- Zero U+2014 or U+2013 dashes.\n- Never claim the build or tests will catch an issue.\n- End with [[agent-done:<lens> findings=<n> coverage=complete|incomplete]]."
}
```

### Path B: Standalone GitHub Copilot CLI (`copilot`)

Use Path B when running from an external shell script or when an in-session `grok-4.7` `task` returns an
empty turn in Copilot App. Write the compact dispatch packet to
`artifacts\scribe-code-review\<run-id>\prompts\<lens>.packet.md` first so `-p` stays short (avoiding
Windows `CreateProcess`'s 32,767-character command-line limit; `--agent=<lens>` loads
`.github/agents/<lens>.agent.md` from disk automatically):

```powershell
$Packet = Resolve-Path "artifacts\scribe-code-review\<run-id>\prompts\<lens>.packet.md"
$Out = & copilot --agent=<lens> --model=grok-4.7 --reasoning-effort=high `
  -p "Read the dispatch packet at $Packet and execute every instruction in full." `
  -s --no-remote-export --deny-tool='write' --deny-tool='shell'
if ($LASTEXITCODE -ne 0) { throw "copilot CLI failed with exit code $LASTEXITCODE" }
```

### The completion marker

Every lens ends its output with exactly one line (strip it before rendering the user-facing review):

```
[[agent-done:<lens> findings=<n> coverage=complete|incomplete]]
```

- **Present with `coverage=complete`:** count as dispatched, succeeded, and results used.
- **Present with `coverage=incomplete`:** propagate `coverage=incomplete` to Summary; never recommend
  approval.
- **Absent or empty output:** retry once (or for `grok-4.7`, retry via Path B CLI or fall back to Rank 4
  `claude-sonnet-5.5` at `max` / `long_context`). If still absent, mark failed and set
  `coverage=incomplete`. Never silently substitute your own reading as the lens's output.
- **`findings=<n>` checksum:** if it disagrees with rendered findings in the body, trust the body and note
  the discrepancy.

### Multi-model panel policy (Pass B / C / D)

When the diff touches a fragile path (`fragile-area` trigger), alters a Win32 hook/injection or overlay
process contract, touches privacy/egress or word-pack admission/journal, touches `macos/Scribe`
process/logging/security, or exceeds ~400 changed lines, add cross-family panel passes:

| Lens | Panel models (in roster order) | Why |
| --- | --- | --- |
| `architecture-fit` | `gemini-4-carbon` + `gpt-6.1-sol` | Highest blast-radius verdict across Core, App, and Overlay. |
| `win32-interop` | `gemini-4-carbon` + `gpt-6.1-sol` (+ `grok-4.7` on hook/clipboard changes) | Hook deadlines, `SendInput` short counts, and `ClipboardBorrower` receipts benefit from independent reasoning. |
| `overlay-process-contract` | `gemini-4-carbon` + `grok-4.7` | Separate process, `OverlayPipeProtocol`, and `OverlayHelperLifetime` races. |
| `privacy-egress` | `gemini-4-carbon` + `gpt-6.1-sol` (+ `grok-4.7` / `claude-sonnet-5.5` on egress changes) | A missed egress or relaxed `store=false` / `TryHandOff` guard is the worst possible failure. |
| `word-packs-and-vocabulary` | `gemini-4-carbon` + `gpt-6.1-sol` | Journaled state, `LibraryPrecedence`, and content-bound AI permission. |
| `macos-port` | `gemini-4-carbon` + `grok-4.7` | Swift 6 concurrency, `ProcessRunner`, and `ScribeLog` shape-only logging without a local Windows Swift compiler. |
| `fragile-area` | `gemini-4-carbon` + `grok-4.7` (+ `claude-sonnet-5.5` on >400-line diffs) | Surfaces with documented historical regressions. |
| `tests-quality` | `gemini-4-carbon` + `gpt-6.1-sol` | Two model families catch tautological mocks and wrong-universe oracles differently. |

You may also invoke Copilot's built-in `rubber-duck` (`gpt-6.1-sol`, `max`, `long_context`) or
`security-review` (`grok-4.7`, `high`, `long_context`) as supplemental panel checks on high-risk diffs.

**Merging a panel:** union finding sets before dedup, match on **root cause** (not exact `file:line`),
count distinct model families in `corroborated (N/M)`, never down-weight a single-family finding, and
never drop a corroborated finding to meet the global cap.

### Coverage accounting

Track and render in the Summary: lenses matched, dispatched, succeeded, failed, and results used, keyed
strictly off returned `[[agent-done:...]]` markers. Any missing matched lens or `coverage=incomplete`
marker sets overall `coverage=incomplete` and blocks approval.

---

## Step 4: Synthesize and write `findings-for-verification.json`

1. **Dedup by (file, root cause).** Keep the more specific lens and append a one-line cross-reference.
   Specificity order (highest first across all 24 normal lenses):
   `fragile-area`, `overlay-process-contract`, `win32-interop`, `privacy-egress`,
   `word-packs-and-vocabulary`, `azure-credential`, `logging-discipline`, `settings-and-persistence`,
   `macos-port`, `asr-pipeline`, `prompt-and-model`, `core-app-layering`, `architecture-fit`,
   `build-packaging`, `ui-shell-quality`, `tests-regression-pin`, `tests-coverage`, `tests-quality`,
   `guardrail-erosion`, `docs-sync`, `learned-patterns`, `comment-and-dash-hygiene`, `merit`
   (`solution-alternatives` emits verdicts/questions only).
2. **Honor per-lens severity and findings caps** from the inventory table. Never escalate past a lens's cap.
3. **Global cap of 12 findings post-dedup** (lower on re-reviews). Drop 💡 first, then least-specific 🟡;
   never drop a 🔴 or a corroborated finding.
4. **Roll up shared-root-cause clusters:** 3+ findings from 2+ lenses sharing one root cause become one
   rollup at max child severity (for example, one `AppSettings` property missing `Clone`, `CreateDefault`,
   `DraftSnapshot`, and a test; or one pipe verb missing `OverlayPipeProtocol`, `Dispatch`, and test).
5. **Write `<cache>\findings-for-verification.json`** containing every deduped 🔴 and 🟡 candidate with a
   stable `finding_id` (`F-01`, `F-02`, ...), lens/model provenance, file/line, severity, tags, and the
   draft Architecture and Design verdicts.

## Step 4.5: Adjudicate concurrently before drafting

Dispatch both post-synthesis adjudicators in parallel via `task` (`mode: "background"`):

- **`finding-verification`** on Rank 2 **`gpt-6.1-sol`** (`reasoning_effort: "max"`,
  `context_tier: "long_context"`; or Rank 3 `grok-4.7` / Rank 4 `claude-sonnet-5.5` if `gpt-6.1-sol`
  authored most candidates). Tries to refute each `F-nn` against `diff.patch`, existing codebase guards,
  `why` comments, and settled `AGENTS.md` decisions. Returns `CONFIRMED` (`keep`), `PLAUSIBLE`
  (`downgrade` or move to Questions), or `REFUTED` (`drop`).
- **`maintainer-decision`** on Rank 4 **`claude-sonnet-5.5`** (`reasoning_effort: "max"`,
  `context_tier: "long_context"`). Flags items needing the maintainer rather than a branch-local fix:
  (a) `[architecture-shortcut]`, (b) `fragile-area` 🔴 or blast-radius + concrete risk, (c) unacknowledged
  `AGENTS.md` "Ask first" crossing (version/release/signing, `Directory.Packages.props`, new third-party
  component, SQLite schema), (d) `guardrail-erosion` 🔴 or `[needs-signoff]` privacy finding, or (e)
  verified 🔴 changing what leaves the machine.

Join both results: drop any maintainer candidate whose source finding was `REFUTED`, recalculate
severity-sensitive triggers on `PLAUSIBLE`, and carry `"Verified N findings; dropped M as unsubstantiated."`
into the Summary.

---

## Re-review convergence (Round N > 1)

1. **Delta scope:** fan out over `delta.patch`; do not raise findings outside the delta unless 🔴 Critical
   or privacy/security egress.
2. **Reconcile prior findings:** count fixed items as resolved without relisting, label surviving items on
   changed code as continuing, never restate already-posted PR comments, and surface reappearing issues as
   regressions.
3. **Drop 💡 Suggestions on Round N > 1.** Lead Summary with `Round N: X resolved, Y still open, W new`.
   *"Nothing new since round N-1"* is a valid, complete result.

---

## Lens inventory

Every file in `agents/` has a synced read-only Copilot custom agent in `.github/agents/<lens>.agent.md`.

| Lens (`agents/<lens>.md`) | Group | Trigger | Severity cap | Cap | Pass A model (`effort`, `long_context`) |
| --- | --- | --- | --- | --- | --- |
| `core-app-layering` | α | always (silent if no `src/**` change): logic in `.xaml.cs` / `SettingsWindow*.cs` that belongs in `Scribe.Core` | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `architecture-fit` | α | always (silent if no `src/**` or `tools/**` change): P-1 to P-12 fit and Core/App/Overlay boundaries | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `merit` | α | always: what/why rationale, runtime/log verification beyond green build, and `AGENTS.md` Ask-first crossings | 🟡 (🔴 `[ask-first]`) | 3 | `gemini-4-carbon` (`high`) |
| `guardrail-erosion` | α | always: deleted/skipped tests, new warning suppressions, weakened CI, relaxed privacy/source-contract pins | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `comment-and-dash-hygiene` | α | always: narration/restatement noise, deleted load-bearing `why` comments, U+2014/U+2013 ban | 🟡 Important | 5 | `gemini-4-carbon` (`high`) |
| `win32-interop` | β | `Hotkeys/**`, `TextInjection/**`, `Overlay/Interop/**`, `HotkeyCapture`, `StartupRegistration`, or Win32 P/Invokes | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `overlay-process-contract` | β | `Scribe.Overlay/**`, `App/Overlay/**`, `Core/Overlay/**`, `OverlayExecutableSelector`, `OverlayPosition`, pipe/Job APIs | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `asr-pipeline` | β | `Audio/**`, `Vad/**`, `Transcription/**`, `Scribe.AsrCheck/**`, `Download-Models.ps1`, `Model-Manifest.ps1`, speech fixtures | 🔴 Critical | 4 | `gemini-4-carbon` (`high`) |
| `macos-port` | β | `macos/**`, `.github/workflows/macos.yml`, shared fixtures/`GlossarySourceTests`, or mirrored `Scribe.Core` domain changes | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `logging-discipline` | γ | `FileLoggerProvider`, `TraceLog*`, `TraceTag*`, `Overlay/Logging/**`, `Core/Diagnostics/**`, `LogCallScanner`, or `catch` logging | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `privacy-egress` | γ | `Cleanup/**`, `Vocabulary/**`, `AiVocabularyPolicy`, `Diagnostics/**`, `Security/**`, `Persistence/**`, `PRIVACY.md`, outbound/OTLP calls | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `azure-credential` | γ | `Cleanup/Azure*.cs`, `CachingCliTokenCredential`, `Settings/Azure*.cs`, `Security/**`, Azure setup docs/scripts | 🔴 Critical | 4 | `gemini-4-carbon` (`high`) |
| `prompt-and-model` | γ | `CleanupPrompt`, `CleanupModel`, `ContextBudget`, `LocalServerClient`, `Foundry*`, `TextCleanupService*`, `Scribe.Evals/**`, benchmarks, prompts | 🔴 Critical | 4 | `gemini-4-carbon` (`high`) |
| `settings-and-persistence` | δ | `Models/**`, `Persistence/**`, `Security/**`, `AppPaths`, `StoredSettingsReapply`, `External*Sync`, `AppAwareFormattingRollout` | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `word-packs-and-vocabulary` | δ | `Core/Libraries/**`, `Core/Vocabulary/**`, `DictionaryLibrary*`, `TextPostProcessor`, `SettingsWindow.WordPacks.cs`, `scribe-word-pack`, library fixtures | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `tests-coverage` | ε | any `src/**`, `tools/**`, `macos/**`, or `tests/**` change | 🟡 Important | 3 | `gemini-4-carbon` (`high`) |
| `tests-quality` | ε | same trigger as `tests-coverage` | 🟡 Important | 5 | `gemini-4-carbon` (`high`) |
| `tests-regression-pin` | ε | bug fix (`fix:` / `hotfix:` title, or `fixes #` / `closes #` / `resolves #`) | 🔴 Critical | 3 | `gemini-4-carbon` (`high`) |
| `build-packaging` | ζ | `*.csproj`, `Directory.*.props`, `Scribe.slnx`, `build/**`, `scripts/**`, `.github/workflows/**`, `*.manifest`, `ModelLicenses/**`, `macos/Scribe/Package.swift` | 🔴 Critical | 5 | `gemini-4-carbon` (`high`) |
| `ui-shell-quality` | η | `*.xaml`, `*.xaml.cs`, `Core/Appearance/**`, `App/{Tray,Onboarding,QuickAdd,Settings,Infrastructure}/**` | 🟡 Important | 5 | `gemini-4-carbon` (`high`) |
| `fragile-area` | η | diff touches F-1..F-6 regression surfaces (hooks, injection, overlay, cleanup, ASR, word-pack journal/admission) | 🔴 Critical | 6 | `gemini-4-carbon` (`high`) |
| `solution-alternatives` | η | non-trivial change (>50 lines, linked issue, new service/abstraction/dependency, or root-cause question) | verdict/questions | 2 | `grok-4.7` (`high`) |
| `docs-sync` | η | edits `*.md` / `docs/**` or changes a surface those docs assert | 🟡 Important | 3 | `gemini-4-carbon` (`high`) |
| `learned-patterns` | η | changed paths overlap any `active` rule in `docs/derived-rules/` | 💡 (🟡 max) | 3 | `gemini-4-carbon` (`high`) |
| `finding-verification` | θ | post-synthesis adversarial refutation over `findings-for-verification.json` | adjudicates | n/a | `gpt-6.1-sol` (`max`) |
| `maintainer-decision` | θ | post-synthesis preflight gate concurrently with `finding-verification` | adjudicates | n/a | `claude-sonnet-5.5` (`max`) |

---

## Output format

Present two blocks in chat, in order:

**A. Walkthrough (chat only, never posted).** Plain-language step-by-step walkthrough of what changed and
how it fits Scribe's architecture. Skip for trivial diffs.

**B. The review (the block eligible for posting after user approval):**

0. **⛔ Maintainer decision required** (omit when none open; blocks approval and sets `Recommended action: request changes`).
1. **Summary:** one paragraph with assessment, confidence, exact model IDs + reasoning/context used,
   `Reviewed at <sha>`, `Verified N findings; dropped M as unsubstantiated`, and the **Lens coverage** line
   (for example: `matched 12, dispatched 15, succeeded 15, results used 15. Panels: architecture-fit (gemini-4-carbon + gpt-6.1-sol), privacy-egress (gemini-4-carbon + gpt-6.1-sol + grok-4.7). Adjudicators: finding-verification (gpt-6.1-sol), maintainer-decision (claude-sonnet-5.5). coverage=complete.`).
2. **Architecture:** **Fit**, **Shortcut?**, and any `[architecture-shortcut]` escalation (`n/a` for docs-only).
3. **Design assessment:** **Better approach?**, **User impact and blast radius** (offline path, egress,
   recording indicator, existing settings/word packs/history, `win-x64` + `win-arm64`, macOS parity), and
   **Code quality and shape**.
4. **Framing:** Step 2.5 partial-conversion findings not already in Design assessment (omit if clean).
5. **Findings:** ordered 🔴 Critical, 🟡 Important, 💡 Suggestion (rollups first in tier; concrete
   `file:line`, mechanism, and fix).
6. **What's good:** load-bearing patterns worth preserving.
7. **Acknowledgements:** agreed prior reviewer comments (omit if empty).
8. **Questions:** genuine non-rhetorical questions needed before approval (omit if empty).

---

## Read-only, dash-free, and posting gates

- **Read-only on repository files.** Never edit tracked files, commit, or push during a review; write
  scratch artifacts only under `artifacts/scribe-code-review/<run-id>/`.
- **Zero U+2014 or U+2013 dashes** and **zero speculative build/test claims** anywhere in output.
- **Explicit posting gate.** Present the draft in chat and wait for an explicit `"post it"` before any
  `gh pr review` or `gh pr comment` (reminding the user of `gh auth switch --user ChrisMcKee1` when needed).
