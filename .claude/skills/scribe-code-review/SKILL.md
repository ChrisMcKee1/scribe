---
name: scribe-code-review
description: Multi-lens code review for the Scribe repository (private offline push-to-talk dictation for Windows 11; C#/.NET 10, WPF tray shell, separate WinUI 3 overlay process, Scribe.Core plus xUnit). Reviews a local diff, a branch comparison, or a GitHub PR. Not for writing code, cutting releases, or driving CI.
allowed-tools:
  - Read
  - Grep
  - Glob
  - Write
  - Task
  - Agent
  - Bash(git diff:*)
  - Bash(git log:*)
  - Bash(git status:*)
  - Bash(git merge-base:*)
  - Bash(git show:*)
  - Bash(gh pr view:*)
  - Bash(gh pr diff:*)
  - Bash(gh pr checks:*)
  - Bash(gh api:*)
  - Bash(gh auth status:*)
  - Bash(gh pr review:*)
  - Bash(gh pr comment:*)
  - Bash(copilot:*)
  - PowerShell
---

# Scribe code review

This skill runs a structured, multi-lens review over a change to **Scribe**: a private, fully offline
push-to-talk dictation app for Windows 11. It dispatches hand-curated review lenses across more than
one model family, adjudicates the resulting findings adversarially, and renders one consolidated
review.

Scribe is not a web app. The things that break here are Win32 interop under a hard OS deadline, a
second WinUI process driven over a named pipe, a logger that must never throw, a privacy promise that
has to fail closed, and two CPU architectures shipped from one source tree. The lens inventory is
built from what has actually cost this project time, most of which is written down in
[`AGENTS.md`](../../../AGENTS.md).

---

## Review philosophy

**High signal, low noise.** Only report a finding you are at least 80 percent confident is a real
issue. No style nits. No "consider using X". Every rendered comment must prevent a bug, fix an
architecture violation, protect a stated guarantee (offline, privacy, non-throwing logging, warning
clean), or name a missing test that matters.

**Understand before judging.** Read the surrounding code, not just the hunk. Scribe's files carry long
`why` comments that record the incident a shape exists to prevent; a change that looks wrong in
isolation is often correct once you read the comment three lines above it. Conversely, a hunk that
deletes one of those comments is worth a hard look.

**Re-verify when challenged; never defend a shallow verdict.** If the user pushes back on any verdict,
most often the Architecture verdict, re-read the exact code, its callers, its siblings, and the current
hunks *before* answering. A verdict reversed on a fresh read is a good outcome. A confidently wrong one
that was defended costs trust.

**Be concrete.** Every finding states what is wrong, why it matters, and how to fix it, with code where
possible.

**Judge the design, not only the lines.** A clean list of line findings is not a complete review. Every
non-trivial change also gets an **Architecture verdict** and a **Design assessment**, rendered even when
no line finding survives. Architecture leads: if a not-great shape lands here, the next agent editing
this repo multiplies it.

**A green build proves very little in this repository.** Three separate defects in one release compiled
warning clean and only appeared at runtime: a `MissingMethodException` from a package version conflict,
a probe token limit Azure rejected, and a theme watcher that threw and silently forced the wrong theme.
Never write "this will fail the build" or "typecheck will catch it" as a finding. Conversely, never
accept "tests pass" as evidence for a provider SDK change, a settings window change, or a startup change.

**No em dashes or en dashes.** The repository bans U+2014 and U+2013 everywhere, including code comments,
UI strings, and the cleanup prompts. That ban applies to everything this skill writes as well. Rewrite
with commas, colons, periods, or "to" for ranges. ASCII hyphens are fine.

---

## Review surfaces

- **Core lenses stay hand curated.** Every lens prompt lives in `agents/`. See the
  [Lens inventory](#lens-inventory) for the full table.
- **The patterns catalog is the architecture rubric.** `references/patterns.md` holds Scribe's blessed
  shapes (P-1 to P-12), each anchored to a live exemplar with a file path. `agents/architecture-fit.md`
  matches new constructs against it.
- **Learned rules stay separate.** `docs/derived-rules/*.md` carry rules mined from real review history
  with a `candidate` / `active` / `retired` status. Only `agents/learned-patterns.md` applies them. Do
  not fold an active rule back into a core lens prompt; the status directory is the single source of
  truth and folding a rule in makes it impossible to retire.

---

## Step 1: Resolve the target and build the review cache

The skill reviews one of three targets. Resolve it from what the user said, and never guess.

| Target | How the user asks | Diff command |
| --- | --- | --- |
| Working tree | "review my changes", "review the diff" | `git diff HEAD` plus `git status --short` for untracked files |
| Branch | "review this branch", "review feature/x against main" | `git diff $(git merge-base main <branch>)..<branch>` |
| GitHub PR | "review PR 61", a PR URL | `gh pr diff <n>` |

**`gh` account note.** This repository is owned by `ChrisMcKee1`, while `chrismckee_microsoft` (an
Enterprise Managed User) is often the active `gh` account. Any `gh pr` or `gh api` call against the repo
fails under the EMU account. Run `gh auth status` first; if the active account is not `ChrisMcKee1`,
tell the user and ask them to run `gh auth switch --user ChrisMcKee1` before continuing, then switch
back afterward. Do not switch accounts on their behalf.

**Build a small on-disk cache, then read from disk.** Lenses must not each shell out to `gh` or `git`.
One orchestrator-side block writes the cache; the dispatch prompt then names the files. There is
deliberately no helper script: this is a handful of commands, and an untested script in a language the repo does
not otherwise use would be a liability rather than an asset.

```powershell
$Cache = Join-Path $env:TEMP "scribe-code-review\<target-id>"
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

# PR target. --paginate matters: a long-running PR outruns the default first page.
gh pr diff <n>                                  | Set-Content "$Cache\diff.patch" -Encoding utf8
gh pr view <n> --json number,title,body,author,headRefName,headRefOid,files,labels,state `
                                                | Set-Content "$Cache\metadata.json" -Encoding utf8
gh api --paginate "repos/ChrisMcKee1/scribe/pulls/<n>/reviews"  | Set-Content "$Cache\reviews.json" -Encoding utf8
gh api --paginate "repos/ChrisMcKee1/scribe/pulls/<n>/comments" | Set-Content "$Cache\pulls-comments.json" -Encoding utf8
gh api --paginate "repos/ChrisMcKee1/scribe/issues/<n>/comments"| Set-Content "$Cache\issues-comments.json" -Encoding utf8
gh api user --jq '{login: .login}'              | Set-Content "$Cache\viewer.json" -Encoding utf8

# Local target: the diff, the untracked list, and recent commits for context.
git diff HEAD                                   | Set-Content "$Cache\diff.patch" -Encoding utf8
git status --short                              | Set-Content "$Cache\status.txt" -Encoding utf8
git log --oneline -20                           | Set-Content "$Cache\recent-commits.txt" -Encoding utf8

# Branch target: substitute the merge-base range for the diff line above.
git diff (git merge-base main <branch>)..<branch> | Set-Content "$Cache\diff.patch" -Encoding utf8
```

On a local or branch target there is no `metadata.json`, `reviews.json`, `pulls-comments.json`,
`issues-comments.json`, or `viewer.json`. Write the metadata each lens needs by hand (target kind, head
SHA, changed-file list, and whatever description the user gave) and tell every lens the existing-comment
digest is empty. A lens must never invent a PR body it was not handed.

**Untracked files are part of a local review.** `git diff HEAD` does not show them, and this repository
routinely carries whole untracked feature folders under `src/`. Read `status.txt`, and for every `??`
entry either pull the file into scope or state in the Summary that it was excluded and why. A review
that silently skipped a new file is not a complete review.

Record the head SHA. Report it in the Summary as *"Reviewed at `<sha>`"* so the user knows which
iteration was judged.

**The reviewed branch may not be checked out.** `diff.patch` is authoritative for what the change adds,
changes, or removes. A lens must **not** use Read or Grep to confirm that a diff line exists on disk,
because it will not when the working tree is on another branch. A lens **should** use Read and Grep
freely for surrounding context: neighbouring files, sibling implementations, existing callers, the
patterns catalog, and the long `why` comments this codebase relies on.

**Read `AGENTS.md` before dispatching.** It is the repository's record of bugs that already cost real
time, and several lenses depend on facts that live only there.

## Step 1.5: Detect the round

Work out whether this is a first pass or a re-review, because it changes the bar and the scope.

- **PR target.** Compare each entry in `reviews.json` and `issues-comments.json` against
  `viewer.json.login` character for character. Round `N` is the count of formal reviews authored by us
  plus one. A review by anyone else is **not** ours, however its body reads; it never increments `N`,
  and its only role is dedupe context. `since_sha` is the `commit_id` of our most recent review, or the
  `Reviewed at <sha>` line from our last posted summary.
- **Local or branch target.** There is no posted history. The anchor is the previous rendered review in
  this conversation, if any. If there is none, this is round 1.

On round `N > 1`, compute the delta scope with `git diff <since_sha>..<head>` (or
`gh pr diff <n>` filtered to the commits since `since_sha`) and write it as `delta.patch`. Lenses fan
out over `delta.patch`; `diff.patch` and the codebase are context only.

## Step 2: Select lenses

Match the changed paths and the diff content against the [Lens inventory](#lens-inventory) trigger
column. Every matched lens runs once. Do not add a lens by reinterpreting the diff after selection, and
do not skip a matched lens because you expect it to come up clean; a clean pass from a matched lens is a
real result that goes into coverage.

If the diff touches only `docs/**`, `*.md`, or a version bump, run `merit`, `comment-and-dash-hygiene`,
and `docs-sync` only, and render the Architecture verdict as a one-line `n/a`.

## Step 2.5: Framing pass (orchestrator, not a dispatched lens)

Skip entirely for a **trivial diff**: under 20 changed lines in a single file, no new user-visible
surface, no behavior change. That definition is the one predicate used by every skip clause in this
skill.

**Partial-conversion audit.** This is the classic miss in this repository and it is worth doing by hand.
Whenever the diff shows a partial conversion, some callsites moved to a new shape and others left
behind, or a new producer or consumer added to a contract a sibling already implements, grep for the
un-converted form and judge each survivor. Scribe-specific shapes to check:

- **A value added to one enum of a by-name twin pair.** `Scribe.Core.Models.OverlayPosition` and
  `Scribe.Overlay.OverlayAnchor` are kept in sync by name and the overlay has no reference to Core, so a
  value added to one and not the other means the overlay silently ignores the command.
- **A new `AppSettings` property that `Clone` does not deep copy.** A new `List<T>` or reference type
  shared between the editor snapshot and the dictation loop is a live aliasing bug.
- **A new `AppSettings` property meant as a first-run opt-in but set only via the property initializer.**
  Deserialization fills initializers for keys the JSON does not contain, so an existing install silently
  acquires it. First-run opt-ins belong in `CreateDefault`.
- **A new secret field on `AppSettings` without `[JsonConverter(typeof(DpapiProtectedStringConverter))]`.**
- **A new SQLite column without a schema-version bump and a matching `if (current < N)` block.**
- **A new native asset or package that is architecture specific**, referenced unconditionally rather than
  selected through `ScribeNativeRid`.
- **A new pipe command handled by the server but never sent, or sent but not handled.**
- **A new project without `RuntimeIdentifiers=win-x64;win-arm64`.**

Surface un-converted siblings as 🟡 Important findings and pass them as hints to the relevant lenses.
If every sibling landed the conversion, say so positively.

**Cost versus benefit.** Read the description for the claimed benefit, weigh it against the complexity
added, and grep for an existing Core helper that would have delivered the same result. Frame a
disproportionate-complexity concern as a **Question**, not a Finding; the author has context you lack.

**Problem fit.** Does the change address the root cause or a symptom? Scribe has a documented case where
three plausible hypotheses about a bug were all wrong when measured. If the mechanism does not obviously
match the reported failure, ask.

## Step 3: Dispatch

### Building the prompt file

Every dispatch, on either path, sends the same assembled prompt. Write it to
`$Cache\prompts\<lens>.md` and dispatch from there, because a lens prompt plus a diff is far past a
comfortable command line and Path B needs a file anyway. Assemble in this order:

1. The full contents of `agents/<lens>.md`, verbatim. Do not summarize it, and do not reorder its
   sections; the evidence-map-first ordering is what stops a lens from rendering a verdict it has not
   earned.
2. A `## Target` block: target kind (working tree, branch, or PR), head SHA, round number, and the
   absolute path of the cache directory.
3. A `## Patch in scope` block naming the file to read: `delta.patch` on round `N > 1`, otherwise
   `diff.patch`. Give the absolute path. Do not paste the patch inline when it is large; name the file
   and let the lens read it.
4. The description: the PR title and body on a PR target, or whatever the user said on a local target.
   Say explicitly when there is none.
5. The existing-comment digest, or the sentence "No prior review comments." Never omit this block
   silently, or the lens cannot tell "nothing to dedupe against" from "you were not told".
6. The Step 2.5 framing hints relevant to this lens, if any.
7. A closing reminder of the two repository-wide rules that outrank the lens's own preferences: no em
   dashes or en dashes anywhere in the output, and no claim that the build or the tests will catch
   something.

The lens file already carries its own severity cap, findings cap, confidence bar, output format and
completion marker, so the dispatch prompt does not restate them.

### Two dispatch paths

**Path A: Claude subagents via the Task tool.** The default, and the only path that can reach a Claude
model family. Pass the assembled prompt file. Available families:

| Model | Use it for |
| --- | --- |
| `opus` | Architecture fit, the Win32 and overlay lenses, fragile-area, and finding verification. The lenses where reading twenty files of surrounding context is the job. |
| `sonnet` | The Pass A default for every other lens. Breadth at reasonable cost. |
| `fable` (`claude-fable-5`) | A genuinely different Claude family. Best on adversarial work: finding verification and solution alternatives. GitHub Copilot does not offer it, so it is the diversity member no other path can supply. Any inventory or panel row naming `fable` must go through Path A. |

**Path B: GitHub Copilot CLI, for the GPT family.** Use this to add a non-Anthropic family to a panel,
and whenever the Claude subscription budget is a concern. Copilot offers **no Claude model**, so it can
never substitute for an `opus`, `sonnet`, or `fable` row.

This is the invocation that clears the permission classifier. Take it literally:

```powershell
$prompt = Get-Content -Raw "$env:TEMP\scribe-code-review\<target-id>\prompts\<lens>.md"
copilot -p $prompt --model gpt-5.6-sol -s --no-remote-export -C "C:\Users\chrismckee\GitHub\Scribe" `
  --allow-tool 'write' --allow-tool 'shell(dotnet:*)' --allow-tool 'shell(git:*)' `
  --deny-tool 'shell(git push)' `
  --deny-tool 'write'
```

Rules that make this work:

- **The last line, `--deny-tool 'write'`, is what makes the run read only.** Every lens in this skill is
  review-only, so it is always present here. The base invocation without it is the form to use if this
  skill is ever reused for work that legitimately edits files; nothing in this skill does.
- **`--allow-tool 'write'` stays even though `--deny-tool 'write'` follows it.** That pairing is the
  shape that passes the classifier. It looks redundant and is not. Do not "simplify" it.
- **`--allow-all-tools` together with `--no-ask-user` gets denied by the permission classifier.** Use the
  explicit `--allow-tool` list above instead.
- **`-s`** runs the session non-interactively so the process exits when the answer is printed rather than
  waiting on a prompt. **`--no-remote-export`** keeps the session off the remote history. **`-C`** sets
  the working directory, so the lens's relative paths resolve against the repository.
- **`-p $prompt` reads the prompt from a variable, never from a heredoc or an inline string.**
  `Get-Content -Raw` preserves the newlines the lens file depends on.
- **If a run exits without printing its answer**, resume it once with
  `copilot --continue -p "print your final findings"`. Do not re-dispatch a fresh run; that doubles the
  spend and produces a second independent answer you then have to reconcile. If the resume also comes
  back empty, record the lens as failed and render `coverage=incomplete`. Do not substitute your own
  reading of that lens's surface and present it as the lens's result.
- **Why both paths exist:** Claude subagents bill against a subscription that has hit its monthly limit
  mid-task before, which strands a review halfway through. Copilot bills separately. When a long review
  is planned, or when the subscription is already under pressure, push the wide, cheap lenses to Copilot
  and keep Claude for the deep ones.

### The completion marker

Every lens ends its output with exactly one line:

```
[[agent-done:<lens> findings=<n> coverage=complete|incomplete]]
```

This is the only signal that separates a lens that ran from one that returned nothing useful. Treat it
as the contract:

- **Marker present, `coverage=complete`.** Count the lens as dispatched, succeeded, and results used.
- **Marker present, `coverage=incomplete`.** Count it as dispatched and succeeded, but the run carries a
  named gap. Propagate `coverage=incomplete` to the Summary and do not recommend approval on that run.
- **Marker absent.** The lens did not complete, whatever text came back. On Path B, resume once per the
  rule above. On Path A, re-dispatch once. If it is still absent, mark that lens failed, render
  `coverage=incomplete`, and say which lens in the Summary. Never treat a truncated body as a clean pass:
  a lens that came up clean still emits the marker with `findings=0`.
- **`findings=<n>` is a checksum, not a budget.** If the count disagrees with the findings you can see in
  the body, trust the body and note the discrepancy in coverage accounting.

Strip the marker line before anything reaches the rendered review. It is orchestration bookkeeping and
never appears in a posted comment.

### Model diversity policy

**Pass A: coverage.** Every matched lens runs exactly once. Default `sonnet`; the lenses marked `opus` in
the inventory run on `opus`.

**Panel: independent confirmation on the surfaces that hurt.** Add a second and, on a high-risk change, a
third model family to these lenses only:

| Lens | Panel | Why |
| --- | --- | --- |
| `architecture-fit` | opus + gpt-5.6-sol | The verdict with the longest blast radius. |
| `win32-interop` | opus + gpt-5.6-sol | Hook deadlines and `SendInput` short counts are exactly where one model's prior is not enough. |
| `overlay-process-contract` | opus + fable | Two processes and two enums; the failure is silent. |
| `privacy-egress` | opus + gpt-5.6-sol | A missed egress is the worst outcome this product can have. |
| `fragile-area` | opus + fable | The surfaces with a documented regression history. |
| `tests-quality` | sonnet + gpt-5.6-sol | Two families disagree usefully about whether a test can fail. |

A panel is warranted when the diff touches a fragile path, changes a Win32 or overlay contract, changes
what leaves the machine, or exceeds roughly 400 changed lines. Otherwise Pass A alone is the right
amount of work.

**Merging a panel.** Union the finding sets *before* dedup, then:

- **Match on root cause, not on `file:line`.** Two models describing the same defect corroborate even
  when they cite different lines or different files. Keying corroboration on an exact line splits one
  defect into two and loses the signal.
- **`corroborated (N/M)` counts distinct model families, not lenses.** Two lenses agreeing under the same
  model is ordinary intra-model dedup, not independent confirmation.
- **A single-family finding is the expected case, not a weak one.** The whole reason to run a panel is to
  catch what one family structurally misses. Never down-weight a finding for being raised once. Record
  which family raised it so verification and the Summary can reference it.
- Never drop a corroborated finding to hit the global cap, and surface the most-corroborated findings
  first within their severity tier.

### Existing-comment digest

Before dispatching, read `reviews.json`, `pulls-comments.json`, and `issues-comments.json` and produce a
short digest, one bullet per substantive comment, and pass it verbatim to every lens. Skip bot comments,
comments under 30 characters, approval-only comments, thread replies, and comments by the PR author.
When there is nothing to say, the dispatch prompt still carries the sentence "No prior review
comments." rather than nothing at all, so a lens can tell "nothing to dedupe against" from "you were
not told".

### Coverage accounting

Track, and render in the Summary: lenses matched, dispatched, succeeded, failed, and results actually
read and used. **The completion marker is the evidence**, per the rules above: a lens counts as a result
used only when its `[[agent-done:...]]` line came back. A lens whose result was never read did not run,
whatever its exit status. If any matched lens is missing, or any returned `coverage=incomplete`, render
`coverage=incomplete` and do not recommend approval on that run.

Name failures rather than absorbing them. "Dispatched 11, succeeded 10, `win32-interop` returned no
marker after one resume" is a usable result. Silently reviewing that surface yourself and presenting it
as the lens's output is not.

## Step 4: Synthesize

1. **Dedup by (file, root cause).** When two lenses flag the same defect, keep the more specific one and
   append a one-line cross-reference. Specificity order, highest first:
   `fragile-area`, `overlay-process-contract`, `win32-interop`, `privacy-egress`, `azure-credential`,
   `logging-discipline`, `settings-and-persistence`, `asr-pipeline`, `prompt-and-model`,
   `core-app-layering`, `architecture-fit`, `build-packaging`, `ui-shell-quality`,
   `tests-regression-pin`, `tests-coverage`, `tests-quality`, `guardrail-erosion`, `docs-sync`,
   `learned-patterns`, `comment-and-dash-hygiene`, `merit`.
2. **Honor per-lens severity caps.** Never escalate a finding past its originating lens's cap.
3. **Honor per-lens findings caps** from the inventory table.
4. **Global cap of 12 findings post-dedup**, lower on a re-review. Drop 💡 first, then the least specific
   🟡. Never drop a 🔴. Emit a one-line footer naming what was consolidated.
5. **Roll up clusters.** Three or more findings from two or more lenses sharing one root cause become a
   single rollup finding at the maximum child severity, with the children listed under it as downstream
   symptoms. The bar is a shared root cause that one shape change would dissolve, not mere co-location in
   the same file. Typical Scribe clusters: several findings on one settings property (missing from
   `Clone`, missing from `CreateDefault`, missing a converter, missing a test); several findings on one
   pipe command (sent, not handled, no enum twin, no log line).
6. **Route by kind.** Findings to Findings, questions to Questions, acknowledgements to Acknowledgements,
   maintainer decisions to block 0. Acknowledgements and rollups do not count against the cap; each child
   under a rollup counts once.

## Step 4.5: Adjudicate before drafting

No finding reaches the user unchecked.

**`finding-verification` (adversarial).** Dispatch it over the deduped 🔴 and 🟡 findings, each carrying a
stable `finding_id`, on a **different model family from the one that produced most of them**. Its job is
to try to refute each finding against the actual diff. It returns `keep`, `downgrade`, or `drop`. Apply
its verdicts first. Common reasons to drop in this codebase:

- The concern is answered by a `why` comment or a guard the lens did not read.
- The finding asserts the build or the tests will fail. They will or they will not; CI decides, and this
  repository has shipped three defects that compiled clean, so the claim carries no weight either way.
- The finding flags code that already existed and worked; the diff did not introduce it.
- The finding hedges with "likely", "probably", "seems", or "may be". Either the hunk substantiates it,
  in which case remove the hedge, or it does not, in which case drop it or route it to Questions.
- The finding re-derives a decision AGENTS.md explicitly closed: a language picker for the transducer
  model, `DefaultAzureCredential`, an in-process WPF transparent pill, an MSI, NPU speech decoding,
  lowering `SupportedOSPlatformVersion`, or the `Cognitive Services` roles on a Foundry resource. Those
  are settled; a lens re-opening one is drifting, not reviewing.

**`maintainer-decision` (preflight).** Dispatch concurrently. It identifies decisions that are not the
reviewer's to clear and not the author's to wave away. Triggers:

- (a) an `[architecture-shortcut]` finding fired,
- (b) a `fragile-area` 🔴, or a blast-radius verdict co-occurring with a concrete risk finding,
- (c) an "Ask first" boundary from AGENTS.md crossed with no stated approval: a version bump, a release,
  a signing-posture change, a NuGet add or upgrade, a new third-party component, or a SQLite schema
  migration,
- (d) a `guardrail-erosion` 🔴, or a privacy finding tagged `[needs-signoff]`,
- (e) a verified 🔴 that would change what leaves the machine.

Join the preflight candidates against verification: a candidate whose source finding was dropped
disappears, and a severity-sensitive candidate is recalculated after a downgrade.

Carry one line into the Summary: *"Verified N findings; dropped M as unsubstantiated."*

---

## Re-review convergence

This is the anti "a brand new nit every round" path, and it rests on structure rather than on asking the
model to be satisfied.

1. **Scope: look at what changed.** Round `N > 1` fans out over `delta.patch`, so already-reviewed,
   unchanged code is not re-scanned. The full diff and the codebase remain context. Do not raise a new
   finding whose primary evidence sits outside the delta unless it is 🔴 Critical or a privacy or
   security issue.
2. **Reconcile against what was already posted.** For each prior finding:
   - the author changed the code and fixed it, so it is **resolved**; count it, do not relist it,
   - it is still real on changed code, so keep it, labelled continuing, not "new",
   - anything already posted by you or by another reviewer is **never restated**; it is on the PR
     already,
   - a previously-resolved issue that reappears is surfaced **loudly** as a regression.
3. **Cap hard and prefer silence.** Drop 💡 Suggestions entirely on a re-review. Surface only 🔴 and
   load-bearing 🟡, consolidated into one tight comment. **Do not manufacture findings because the skill
   ran again.** If the delta is clean and nothing regressed, say exactly that. *"Nothing new since round
   N-1"* is a correct and complete outcome, not a failure to find something.

Lead the Summary on a re-review with the convergence line: `Round N: X resolved, Y still open, W new`.

---

## Lens inventory

Lens prompts live in `agents/`. Group letters map to the dispatch order in Step 3.

| File | Group | When to dispatch | Severity cap | Findings cap | Default model |
| --- | --- | --- | --- | --- | --- |
| `agents/core-app-layering.md` | α | always (silent when no `src/**` change): logic landing in WPF or WinUI code-behind that belongs in `Scribe.Core` with a test | 🔴 Critical | 5 | sonnet |
| `agents/architecture-fit.md` | α | always (silent when no `src/**` change): matches new constructs against `references/patterns.md` P-1 to P-12 | 🔴 Critical | 5 | opus |
| `agents/merit.md` | α | always: does the change say what and why, state how it was verified beyond a green build, and respect the AGENTS.md "Ask first" boundaries | 🟡 Important, 🔴 for an un-flagged "Ask first" crossing | 3 | sonnet |
| `agents/guardrail-erosion.md` | α | always (self-silences): removed or skipped xUnit tests, new `#pragma warning disable` or `NoWarn`, weakened CI steps, relaxed fail-closed guards, the SQLite CVE pin, `Payload-Architecture.ps1`, `DashNormalizer`, `SessionBannerTests` | 🔴 Critical | 5 | sonnet |
| `agents/comment-and-dash-hygiene.md` | α | always: comments that narrate history instead of explaining why, plus the U+2014 and U+2013 ban across source, UI strings, prompts, and docs | 🟡 Important | 5 | sonnet |
| `agents/win32-interop.md` | β | `src/Scribe.Core/Hotkeys/**`, `src/Scribe.Core/TextInjection/**`, `src/Scribe.Overlay/Interop/**`, or the diff adds `DllImport`, `LibraryImport`, `SendInput`, `SetWindowsHookEx`, `OpenClipboard`, `SetForegroundWindow`, `AttachThreadInput`, or `ApartmentState` | 🔴 Critical | 5 | opus |
| `agents/overlay-process-contract.md` | β | `src/Scribe.Overlay/**`, `src/Scribe.App/Overlay/**`, `src/Scribe.Core/Infrastructure/OverlayExecutableSelector.cs`, `OverlayPosition` in `src/Scribe.Core/Models/Enums.cs`, or the diff mentions a pipe command, `AllowsTransparency`, or the Job Object | 🔴 Critical | 5 | opus |
| `agents/asr-pipeline.md` | β | `src/Scribe.Core/Audio/**`, `src/Scribe.Core/Vad/**`, `src/Scribe.Core/Transcription/**`, `tools/Scribe.AsrCheck/**`, `scripts/Download-Models.ps1`, `scripts/Model-Manifest.ps1` | 🔴 Critical | 4 | sonnet |
| `agents/logging-discipline.md` | γ | `src/Scribe.App/Infrastructure/FileLoggerProvider.cs`, `LogTraceProcessor.cs`, `SessionDiagnostics.cs`, `src/Scribe.Overlay/Logging/**`, `src/Scribe.Core/Diagnostics/{SessionBanner,LogRetentionPolicy,ScribeLogFiles,ScribeTelemetry,DiagnosticsBundle}.cs`, or the diff adds a log or telemetry call inside a `catch` | 🔴 Critical | 5 | sonnet |
| `agents/privacy-egress.md` | γ | `src/Scribe.Core/Cleanup/**`, `src/Scribe.Core/Diagnostics/**`, `src/Scribe.Core/Security/**`, `PRIVACY.md`, or the diff adds a network call, a telemetry tag, or a log statement carrying transcript-shaped data | 🔴 Critical | 5 | opus |
| `agents/azure-credential.md` | γ | `src/Scribe.Core/Cleanup/Azure*.cs`, `src/Scribe.Core/Settings/Azure*.cs`, `src/Scribe.Core/Security/**`, `docs/service-principal-setup.md`, `docs/foundry-setup.md`, `scripts/Setup-ScribeFoundry.ps1` | 🔴 Critical | 4 | sonnet |
| `agents/prompt-and-model.md` | γ | `src/Scribe.Core/Cleanup/{CleanupPrompt,CleanupModel,FoundryModelVariant,FoundryExecutionProviders,TextCleanupService}.cs`, `tools/Scribe.Evals/**`, `docs/model-leaderboard.md`, or any diff that edits prompt text | 🔴 Critical | 4 | sonnet |
| `agents/settings-and-persistence.md` | δ | `src/Scribe.Core/Models/AppSettings.cs`, `AppProfile.cs`, `src/Scribe.Core/Persistence/**`, `src/Scribe.Core/Security/**` | 🔴 Critical | 5 | sonnet |
| `agents/tests-coverage.md` | ε | any `src/**` or `tools/**` change, or any `tests/**` change | 🟡 Important | 3 | sonnet |
| `agents/tests-quality.md` | ε | same trigger as `tests-coverage` | 🟡 Important | 5 | sonnet |
| `agents/tests-regression-pin.md` | ε | the change is a bug fix: title starts `fix:` or `hotfix:`, or the body contains `fixes #`, `closes #`, or `resolves #` | 🔴 Critical | 3 | sonnet |
| `agents/build-packaging.md` | ζ | `*.csproj`, `Directory.Build.props`, `Directory.Packages.props`, `Scribe.slnx`, `build/**`, `scripts/**`, `.github/workflows/**`, `src/Scribe.App/app.manifest`, `src/Scribe.Overlay/app.manifest` | 🔴 Critical | 5 | sonnet |
| `agents/ui-shell-quality.md` | η | `*.xaml`, `*.xaml.cs`, `src/Scribe.App/Tray/**`, `src/Scribe.App/Onboarding/**`, `src/Scribe.App/QuickAdd/**`, `src/Scribe.App/Settings/**` | 🟡 Important | 5 | sonnet |
| `agents/fragile-area.md` | η | the diff touches a surface with a documented regression history (path list inside the lens file) | 🔴 Critical | 6 | opus |
| `agents/solution-alternatives.md` | η | non-trivial change: over 50 changed lines, a linked issue, a new service or abstraction, a new dependency, or a bug fix whose mechanism is not obviously the root cause | verdict and questions only | 2 | fable |
| `agents/docs-sync.md` | η | the diff edits `AGENTS.md`, `README.md`, `CONTRIBUTING.md`, `PRIVACY.md`, `PRODUCT.md`, or `docs/**`, **or** it changes a surface those documents assert (overlay architecture, Azure auth, packaging, logging, privacy, architecture support) | 🟡 Important | 3 | sonnet |
| `agents/learned-patterns.md` | η | the changed paths overlap the paths declared by any `active` rule in `docs/derived-rules/` | 💡 Suggestion, 🟡 maximum | 3 | sonnet |
| `agents/finding-verification.md` | θ | after synthesis, over the deduped 🔴 and 🟡 findings; runs on a different family from the one that produced most of them | adjudicates | n/a | opus or fable |
| `agents/maintainer-decision.md` | θ | after synthesis, concurrently with verification | adjudicates | n/a | sonnet |

Group meaning: α always on, β native and interop surfaces, γ data, privacy, and providers, δ persisted
state, ε tests, ζ build and packaging, η conditional, θ post-synthesis adjudicators.

---

## Output format

Present two blocks in chat, in this order.

**A. Walkthrough. Chat only, never posted.** For a non-trivial change, lead with a plain-language
walkthrough: what the change does, the mechanism step by step, and how it fits or diverges from the
shapes already in this codebase. This exists so the user understands the change before reading findings.
Skip for a trivial diff.

**B. The review. The only block that gets posted.**

**0. ⛔ Maintainer decision required.** Rendered first, before the Summary, whenever at least one
maintainer-decision item is open. Omit the whole block when there are none. One bullet per open item:
the decision to make, why it needs the maintainer rather than the reviewer or the author, and what
resolves it. Close with `Recommended action: request changes`, naming the open items. This block blocks
approval.

**1. Summary.** One paragraph: what the change does, the overall assessment, the confidence level, the
model families actually used, `Reviewed at <sha>`, and `Verified N findings; dropped M`. Follow with the
coverage line, for example:

> **Lens coverage:** matched 11, dispatched 11, succeeded 11, results used 11. Panel: `architecture-fit`
> (opus + gpt-5.6-sol), `win32-interop` (opus + gpt-5.6-sol). `coverage=complete`.

On a re-review, lead with `Round N: X resolved, Y still open, W new`.

**2. Architecture.** Required for every non-trivial change touching `src/**` or `tools/**`. One line of
`n/a` for a docs, config, or version-bump-only diff. Rendered before the Design assessment because
architecture is the highest-order property of the change. One firm position, stated plainly:

- **Fit.** Does the change mirror how this codebase already solves the problem, per the P-1 to P-12
  catalog in `references/patterns.md`? Does new logic land in `Scribe.Core` with a test, leaving the WPF
  shell thin? Does it hold the Core, App, Overlay boundaries, remembering that `Scribe.Overlay`
  deliberately has no reference to `Scribe.Core`?
- **Shortcut?** Name any architecture shortcut: a hand-rolled parallel of a cataloged shape, decision
  logic parked in a `.xaml.cs`, a private copy of a rule the real implementation already applies, a
  guarantee (non-throwing logging, fail-closed privacy, offline-first) weakened rather than preserved,
  or a seam left unmodeled. If there is none, say so, and say why: *"no architectural shortcut, mirrors
  P-1 and `DictionaryEntryBuilder`."*
- **Escalate when it is not the reviewer's call.** If the honest fix needs a schema migration, a new
  persisted contract, a new dependency, an owner-boundary move, or would make the change no longer
  describe the same behavior, do not bury it in a routine 🟡. Emit it as a 🔴 tagged
  `[architecture-shortcut]`, summarize it at the top of this verdict, and frame the options.

"No architectural concern" is a valid verdict, but it is stated with a one-line reason and never
silently dropped. A clean findings list is not a substitute for this verdict.

**3. Design assessment.** Required for every non-trivial change. Three labelled verdicts, each one to
three sentences and each a concrete answer rather than a hedge. This block is exempt from the "no
consider using X" rule, because design judgment is exactly what belongs here. Do not restate the
Architecture verdict.

- **Better approach?** Is there a materially simpler or more idiomatic way to reach the same goal: an
  existing Core helper, a cataloged shape, less machinery? Fold in the `solution-alternatives`
  `better-approach` item when that lens ran; do not also render it as a Question. If the chosen shape is
  right, say so and why.
- **User impact and blast radius.** What changes for the user, and what could regress? Name it against
  Scribe's promises specifically: does the offline dictation path still work with no network, does
  anything new leave the machine, does the pill still appear, does an existing install's settings,
  dictionary, and history survive, does ARM64 behave the same as x64.
- **Code quality and shape.** Any structural concern the Architecture verdict did not cover: process
  boundary, single responsibility, abstraction altitude, half-done conversion. Point at the shape, not at
  individual lines.

**4. Framing.** Whatever Step 2.5 produced that did not fold into the Design assessment, most often the
partial-conversion audit result. Omit when everything came up clean and the Summary already says so.

**5. Findings.** Ordered 🔴 Critical, then 🟡 Important, then 💡 Suggestion. Rollups lead within their
tier. Each finding gives the file and line, what is wrong, why it matters, and a concrete fix with code
where possible.

**6. What's good.** Patterns worth calling out positively. Do not skip this. In a codebase where the
shapes are the value, naming a correct one is how it survives the next agent.

**7. Acknowledgements.** Prior reviewer findings the lenses agreed with but did not duplicate. Format:
`**@<reviewer>** on <file:line>, <one-sentence summary>. Agreed.` Omit when empty.

**8. Questions.** Things that are not wrong but need the author's answer before approval. Genuine
questions, not rhetorical criticism.

**If no high-confidence issues exist**, say so clearly and summarize why the change looks good. A clean
review is a valid outcome, and a non-trivial change still gets the Architecture verdict and the Design
assessment.

---

## Drafting and posting

- **Present options, do not prescribe.** When more than one fix is reasonable, lay them out. A hard "do
  it this way" is right only for a clear correctness or convention violation.
- **Direct, not discouraging.** State the ask plainly. No performative flattery, no piling on a change
  that has already iterated, and no softening a real blocker into a maybe.
- **Concise by default.** One consolidated comment beats a scattered list. Mandatory on a re-review.
- **No speculative build claims.** Never write "this will not compile" or "this fails the build".
- **No hedged findings.** "Likely", "probably", "seems", and "may be" do not appear in Findings. If
  verification proved it, state it. If it cannot point at the hunk, drop it or move it to Questions.
- **No em dashes or en dashes** in anything this skill writes, including the posted comment.

**Read only.** A review never edits code, never pushes, and never runs a fix. If the task drifts into
changing code, stop and say so. The `Write` permission exists for one purpose, writing the assembled
lens prompts and the cache under `$env:TEMP\scribe-code-review\`. Nothing under the repository working
tree is ever written, and `--deny-tool 'write'` is on every Copilot dispatch for the same reason.

**Approval gate.** Always present the draft in chat and wait for an explicit "post it" before any
`gh pr review` or `gh pr comment`. Remember the account switch: `gh auth switch --user ChrisMcKee1`
before posting, and switch back afterward.

**Maintainer-decision gate.** While any maintainer-decision item is open, the recommended action is
`request changes`, never `approve`, and the draft states `blocked on maintainer decision: <item>` for
each one. Only the maintainer resolves an item, by accepting the trade-off, directing the change, or
dismissing it as a false positive.
