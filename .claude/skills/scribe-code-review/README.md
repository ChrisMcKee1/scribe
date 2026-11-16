# scribe-code-review

A multi-lens, multi-model code review skill for **Scribe** (Windows 11 C# / .NET 10 WPF tray shell +
separate WinUI 3 overlay process + `Scribe.Core` + xUnit, plus the native Swift 6 macOS port in
`macos/Scribe`). Built specifically for **GitHub Copilot App** and **GitHub Copilot CLI**.

This is a **read-only** skill. Every specialist lens runs as a read-only custom agent
(`tools: ["read", "search"]`), writes nothing to tracked repository files, never pushes, and never posts
to GitHub without an explicit `"post it"` from the user.

---

## Why it exists in this shape

Scribe is not a web app, and a generic reviewer is close to useless on it. The things that break here are
Win32 low-level hooks under a hard OS deadline, a separate WinUI 3 overlay process driven over a one-way
named pipe, a shared two-process daily log that must never throw, a word-pack journal and vocabulary
admission point (`TryHandOff`) that bind AI permission to exact file bytes, a privacy policy that must
fail closed, two Windows CPU architectures (`win-x64` and `win-arm64`) shipped from one tree, and a
hand-maintained Swift 6 macOS port (`macos/Scribe`). Three separate defects in one release compiled
warning-clean and only appeared at runtime, which is why no lens is allowed to claim "the build or tests
will catch it".

Every lens is grounded in incidents recorded in [`AGENTS.md`](../../../AGENTS.md).

---

## Layout

```
.claude/skills/scribe-code-review/
  SKILL.md                             orchestrator: target resolution, 4-model dispatch, synthesis, output
  README.md                            this file
  agents/                              26 canonical specialist & adjudicator lens prompts (<30k chars each)
  references/
    copilot-agent-dispatch.md          GitHub Copilot App & CLI sub-agent initialization & CLI fallback guide
    patterns.md                        Scribe canonical patterns catalog (P-1 to P-12)
  docs/derived-rules/                  empirical rules with candidate / active / retired status

.github/agents/<lens>.agent.md         26 synced read-only Copilot custom agents (tools: ["read", "search"])
scripts/New-ReviewCache.ps1            deterministic workspace review cache builder (artifacts/scribe-code-review/)
scripts/Sync-ReviewAgents.ps1          syncs .claude/skills/scribe-code-review/agents/*.md -> .github/agents/
tests/Scribe.Core.Tests/ScribeCodeReviewSkillTests.cs
                                       xUnit contract test pinning models, 1:1 agent sync, <30k limit, & dash ban
```

Note on directory layout: `.github/skills/` holds Squad's framework skills (managed by `squad upgrade`),
while Scribe's own project skills (`scribe-code-review` and `scribe-word-pack`) live under `.claude/skills/`,
which GitHub Copilot App, GitHub Copilot CLI, Squad, and Claude Code all discover natively.

---

## Ordered 4-model roster

Every sub-agent dispatch passes `model`, `reasoning_effort`, and `context_tier: "long_context"` explicitly
on `task` (or `--model` and `--reasoning-effort` on standalone `copilot` CLI) so user `/subagents`
defaults never downgrade a lens:

| Order | Model | Exact ID | Reasoning | Context | Role |
| --- | --- | --- | --- | --- | --- |
| **1** | Gemini 4 Carbon | `gemini-4-carbon` | `high` | `long_context` | **Primary workhorse:** 23 of 24 Pass A specialist lenses |
| **2** | GPT-6.1 Sol | `gpt-6.1-sol` | `max` | `long_context` | **Adversarial & Pass B panels:** post-synthesis `finding-verification` refuter + cross-family panels (`architecture-fit`, `win32-interop`, `privacy-egress`, `word-packs-and-vocabulary`, `settings-and-persistence`, `tests-quality`) |
| **3** | Grok 4.7 | `grok-4.7` | `high` | `long_context` | **Pass C confirmation & alternatives:** `solution-alternatives` + 3rd-family panels (`overlay-process-contract`, `fragile-area`, `win32-interop`, `privacy-egress`, `macos-port`) |
| **4** | Claude Sonnet 5.5 | `claude-sonnet-5.5` | `max` (`xhigh` fallback) | `long_context` | **4th-family adjudicator & fallback:** post-synthesis `maintainer-decision` preflight gate + Pass D tie-breaker / ordered fallback |

See [`references/copilot-agent-dispatch.md`](references/copilot-agent-dispatch.md) for full `task`,
`create_session`, and standalone `copilot` CLI examples.

---

## How to invoke it

Ask for a review in GitHub Copilot App or GitHub Copilot CLI (or invoke `/scribe-code-review`):

| Target | Example prompt |
| --- | --- |
| Working tree | `"review my changes"`, `"run scribe-code-review on the working tree"` |
| Branch | `"review this branch against main"`, `"review feat/x against main"` |
| GitHub PR | `"review PR 61"`, or paste a PR URL |

Before reviewing or posting on a GitHub PR:

- **`gh` account check:** `ChrisMcKee1/scribe` fails with 404/403 when the active `gh` account is the
  `chrismckee_microsoft` EMU account. Run `gh auth status` first; switch to `ChrisMcKee1` only with user
  awareness, and never post without an explicit `"post it"`.
- **Workspace-local cache:** `scripts/New-ReviewCache.ps1` writes diffs and metadata under
  `artifacts/scribe-code-review/<run-id>/` (gitignored by `.gitignore`), never `$env:TEMP`, so Copilot App
  sub-agents can read every artifact directly via `view`.

---

## The 26 lenses

- **Always on (alpha):** `core-app-layering`, `architecture-fit`, `merit`, `guardrail-erosion`,
  `comment-and-dash-hygiene`
- **Native, interop & macOS (beta):** `win32-interop`, `overlay-process-contract`, `asr-pipeline`,
  `macos-port`
- **Data, privacy & providers (gamma):** `logging-discipline`, `privacy-egress`, `azure-credential`,
  `prompt-and-model`
- **Persisted state & word packs (delta):** `settings-and-persistence`, `word-packs-and-vocabulary`
- **Tests (epsilon):** `tests-coverage`, `tests-quality`, `tests-regression-pin`
- **Build & packaging (zeta):** `build-packaging`
- **Conditional (eta):** `ui-shell-quality`, `fragile-area`, `solution-alternatives`, `docs-sync`,
  `learned-patterns`
- **Post-synthesis adjudicators (theta):** `finding-verification` (`gpt-6.1-sol`), `maintainer-decision`
  (`claude-sonnet-5.5`)

After editing any file under `.claude/skills/scribe-code-review/agents/*.md`, run
`pwsh ./scripts/Sync-ReviewAgents.ps1` so `.github/agents/*.agent.md` stays in 1:1 sync (`dotnet test`
verifies zero drift via `ScribeCodeReviewSkillTests`).
