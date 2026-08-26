# scribe-code-review

A multi-lens code review skill for this repository. It resolves a review target, fans a set of
hand-curated review lenses out across more than one model family, adjudicates the resulting findings
adversarially, and renders one consolidated review.

This is a **read-only** skill. It never edits code, never pushes, and never posts without an explicit
"post it" from the user.

---

## Why it exists in this shape

Scribe is not a web app, and a generic reviewer is close to useless on it. The things that break here
are Win32 interop under a hard OS deadline, a second WinUI 3 process driven over a named pipe, a logger
that must never throw, a privacy promise that has to fail closed, and two CPU architectures shipped from
one source tree. Three separate defects in one release compiled warning clean and only appeared at
runtime, which is why no lens is allowed to write "the build will catch it".

Every lens is built from something that has already cost this project time. Most of that history is
written down in [`AGENTS.md`](../../../AGENTS.md), and the lenses cite it directly.

---

## Layout

```
SKILL.md                      the orchestrator: target resolution, dispatch, synthesis, output format
README.md                     this file
agents/                       24 lens prompts, one per review surface
references/patterns.md        the P-1 to P-12 architecture rubric, each entry anchored to a live exemplar
docs/derived-rules/           empirical rules with a candidate/active/retired status field
```

---

## How to invoke it

Ask for a review in the session. The skill resolves one of three targets from what you say:

| Target | How to ask |
| --- | --- |
| Working tree | "review my changes", "review the diff" |
| Branch | "review this branch", "review feature/x against main" |
| GitHub PR | "review PR 61", or paste a PR URL |

Anything narrower works too: "review PR 61, focus on the overlay" still runs the matched lenses, it just
tells the orchestrator where to lead the Summary.

Two things to know before running it against a PR:

- **The `gh` account matters.** The repository is owned by `ChrisMcKee1`. If `chrismckee_microsoft` is
  the active account, every `gh pr` and `gh api` call against the repo fails. The skill checks
  `gh auth status` first and asks you to switch; it will not switch on your behalf.
- **Posting is gated.** The draft is always presented in chat first. Nothing reaches GitHub until you
  say so.

### Model paths

Lenses dispatch two ways, and SKILL.md Step 3 has the literal invocations.

- **Claude subagents via the Task tool.** The default. `opus` for the deep context lenses, `sonnet` for
  breadth, `fable` (`claude-fable-5`) for adversarial work. Copilot offers no Claude model, so any panel
  row naming `fable` has to run here.
- **GitHub Copilot CLI for the GPT family.** Adds a non-Anthropic family to a panel, and takes the wide
  cheap lenses when the Claude subscription is under pressure. The working invocation passes the prompt
  from a file via `Get-Content -Raw`, and carries `--deny-tool 'write'` because every lens here is
  review-only.

Every lens ends with `[[agent-done:<lens> findings=<n> coverage=complete]]`. That line is the only
evidence a lens actually ran, and coverage accounting keys off it.

---

## The lenses

Group letters map to the dispatch order in SKILL.md Step 3. The full trigger, severity cap, findings cap
and default model for each one live in the Lens inventory table there.

### Always on (α)

| Lens | What it answers |
| --- | --- |
| `core-app-layering` | Did the new decision land in `Scribe.Core` with a test, or drift back into a `.xaml.cs`? |
| `architecture-fit` | Does the change mirror a cataloged P-1 to P-12 shape, and does it hold the Core, App, Overlay boundaries? |
| `merit` | Does the change say what and why, state verification beyond a green build, and respect the AGENTS.md "Ask first" boundaries? |
| `guardrail-erosion` | Did this reach green by weakening a safety net rather than by being correct? |
| `comment-and-dash-hygiene` | Does every added comment earn its place, and does the diff respect the U+2014 and U+2013 ban? |

### Native and interop surfaces (β)

| Lens | What it answers |
| --- | --- |
| `win32-interop` | Is this P/Invoke, hook, input, clipboard and focus code correct under the constraints Windows actually imposes? |
| `overlay-process-contract` | Does the out-of-process WinUI overlay contract survive: separate process, one-way pipe, twin enums in sync by name, pill never outliving the engine? |
| `asr-pipeline` | Does the change respect what the recogniser and capture path measurably are, rather than what they look like? |

### Data, privacy and providers (γ)

| Lens | What it answers |
| --- | --- |
| `logging-discipline` | Is logging still non-throwing end to end, bounded, detailed enough to debug a field bug, and incapable of reaching a destructive path? |
| `privacy-egress` | Does anything new leave this machine, and does every privacy guarantee still hold and still fail closed? |
| `azure-credential` | Does credential and role handling stay inside the rules learned from shipped bugs and Microsoft's own guidance? |
| `prompt-and-model` | Does this prompt, model or execution-provider change carry the evidence this repository requires, and respect what the benchmarks already measured? |

### Persisted state (δ)

| Lens | What it answers |
| --- | --- |
| `settings-and-persistence` | Does this survive an upgrade, a clone, a deserialization, and a downgrade attempt? |

### Tests (ε)

| Lens | What it answers |
| --- | --- |
| `tests-coverage` | What behavior does this change add or alter that has no test, ranked by risk? |
| `tests-quality` | Can the tests in this diff actually fail when the code is wrong? |
| `tests-regression-pin` | If I mentally revert the fix, does the new test fail, and does it construct the exact state that triggered the bug? |

### Build and packaging (ζ)

| Lens | What it answers |
| --- | --- |
| `build-packaging` | Will this still produce two correct, pure, installable payloads from one source tree? |

### Conditional (η)

| Lens | What it answers |
| --- | --- |
| `ui-shell-quality` | Is the UI itself correct, consistent and usable: Fluent vocabulary, system theme, keyboard reachability, meaning without colour? |
| `fragile-area` | For a surface with a documented regression history, why is this safe? Name the invariant, then show the diff preserves it. |
| `solution-alternatives` | Is there a materially simpler or more durable shape, and does the mechanism match the reported failure? |
| `docs-sync` | Does every shipped document still tell the truth, and did a changed document bring the code with it? |
| `learned-patterns` | Applies the `active` rules in `docs/derived-rules/` and nothing else. |

### Post-synthesis adjudicators (θ)

| Lens | What it answers |
| --- | --- |
| `finding-verification` | Adversarial. Tries to refute each drafted finding against the actual diff, and returns keep, downgrade, or drop. |
| `maintainer-decision` | Which of these is genuinely the maintainer's call rather than something the author can quietly resolve? |

---

## The patterns catalog

`references/patterns.md` holds twelve blessed shapes, each with a live exemplar and the anti-pattern it
replaces. `architecture-fit` matches new constructs against it, and other lenses cite entries by number.

P-1 pure decider in Core with a thin WPF adapter; P-2 reuse the real implementation, never a private copy
of a rule; P-3 multicast fan-out through `ResilientEvent.InvokeAll`; P-4 diagnostics that cannot take
down what they describe; P-5 STA thread plus `Join` for clipboard and injection; P-6 newline-delimited
pipe commands with by-name enum twins; P-7 `AppSettings` growth (`CreateDefault`, deep copy in `Clone`,
DPAPI for secrets); P-8 a privacy control that fails closed, pinned by a test; P-9 one owner for an
identity with an explicit cache and `Invalidate`; P-10 a deterministic decider fed timestamps, never
reading the clock; P-11 additive forward-only SQLite migration gated on `PRAGMA user_version`; P-12 one
architecture-specific native asset selected by RID, with a build error for the rest.

Every exemplar was verified against the tree when this skill was audited. Line numbers drift, so the
catalog's own staleness guard tells a lens to grep for the symbol before citing it.

---

## Derived rules

`docs/derived-rules/` is the single source of truth for `learned-patterns`, and the only lens allowed to
apply those rules. Each rule carries a `candidate` / `active` / `retired` status.

**There are zero active rules today.** Both seed rules are `candidate`, so `learned-patterns` currently
has nothing to apply and its correct output on every run is the no-active-rules line. That is the
intended state. The split exists so a rule can be retired later; folding an active rule into a
hand-written lens makes it permanent and untrackable.

---

## What was deliberately left out of the port

This skill was ported from `m-code-review` in the `scout-m` repository, which targets a TypeScript,
Electron and React codebase and runs inside an automated PR queue. Several things there were dropped on
purpose rather than overlooked.

**The Python tooling, all four scripts.**

- `review-plan.py` builds a deterministic, budgeted task plan: lanes, passes, panel shapes, reasoning
  effort, and a model-family assignment per lens. It exists because an automated queue needs the same
  input to produce the same plan without a human in the loop. Scribe's reviews are started by a person
  who can read the inventory table and pick. A planner here would be an untested Python dependency in a
  repository that otherwise contains only C# and PowerShell, which SKILL.md says plainly is a liability
  rather than an asset.
- `run-clock.py` enforces a wall-clock budget with a review freeze at 26 minutes, an adjudication
  deadline at 41, and a hard stop at 43. Those numbers exist to fit a scheduled runner's timeout. An
  interactive review has no such deadline, and a clock that truncates a review nobody is waiting on
  only costs coverage.
- `recover-results.py` reconstructs completed lens payloads from persisted session events after an
  interruption. That is recovery machinery for an unattended run. Here, an interrupted review is simply
  re-run, and the completion-marker rule in SKILL.md Step 3 already gives the orchestrator a clean way
  to say which lens did not come back.
- `fetch-pr-cache.py` builds the on-disk PR cache. SKILL.md does the same thing inline in about eight
  PowerShell commands, in the shell this repository already uses everywhere.

**`references/rollup-synthesis.md`.** The source keeps a separate escalation-pattern catalog, an output
template, and a worked example for clustering findings. The rollup rule that matters, three or more
findings from two or more lenses sharing one root cause become a single finding at the maximum child
severity, is inline in SKILL.md Step 4 along with the Scribe-specific clusters that actually recur (one
settings property, one pipe command). A second file for one rule was not worth the indirection.

**Nine source lenses with no Scribe equivalent.** `react`, `react-query`,
`react-query-best-practices`, `renderer-primitives`, `electron-architecture`, `main-process`,
`backend-abstraction`, `lint-cast-hygiene`, and `compliance`. None of React, TanStack Query, Electron's
main and renderer split, nor a TypeScript lint and cast surface exists here. `backend-abstraction`
polices a five-interface provider abstraction Scribe does not have. `compliance` covers audit-flag
surfaces for a multi-user product; Scribe is a single-user offline desktop app whose one real
audit surface, what leaves the machine, is owned by `privacy-egress` at a higher severity cap than the
source lens carried.

**Three source lenses rewritten rather than translated.** `telemetry-logger` became
`logging-discipline`, rebuilt around the non-throwing mandate and the shared two-process daily log.
`architecture-doc-sync` became `docs-sync`, rebuilt around the specific documents that are load-bearing
here, including `PRIVACY.md` as a published policy Partner Center serves. `comment-hygiene` became
`comment-and-dash-hygiene` and picked up the repository-wide U+2014 and U+2013 ban.

**Ten lenses have no source counterpart at all**, because Win32 interop, the overlay process contract,
the ASR pipeline, Azure credentials, prompt and model evidence, settings persistence, build and
packaging, UI shell quality, Core and App layering, and privacy egress are Scribe's problems and nobody
else's.

**No automated posting path.** The source skill can be driven by a queue. This one always presents the
draft in chat and waits for an explicit "post it", and reminds you to switch `gh` accounts around the
post.
