# GitHub Copilot App and Copilot CLI Agent Dispatch Reference

This document defines how `scribe-code-review` initializes specialist review lenses across **GitHub Copilot App** and **GitHub Copilot CLI**, why that differs from Claude Code, and the exact model, reasoning-effort, and context-window settings required on every run.

---

## 1. Why GitHub Copilot App and CLI Differ from Claude Code

The original `scribe-code-review` skill targeted Claude Code (`allowed-tools: Bash(...)`, Claude-only `Task` model IDs without reasoning or context tiers, and `$env:TEMP` scratch folders). GitHub Copilot App and GitHub Copilot CLI initialize skills and sub-agents differently:

1. **Skill discovery across hosts.** GitHub Copilot CLI, GitHub Copilot App, Squad, and Claude Code all scan `.claude/skills/<name>/SKILL.md` alongside `.github/skills/<name>/SKILL.md` and `.agents/skills/<name>/SKILL.md`. In Scribe, `.github/skills/` holds Squad's framework skills while Scribe's own repository skills (`scribe-code-review` and `scribe-word-pack`) live under `.claude/skills/` so `squad upgrade` never overwrites them and no duplicate skill name collides across directories.
2. **Custom sub-agent registration (`.github/agents/<lens>.agent.md`).** Copilot's `task` tool dispatches only built-in agent types (`explore`, `general-purpose`, `rubber-duck`, `code-review`, `research`, `security-review`) or repository custom agents defined in `.github/agents/<name>.agent.md`. Every review lens in `.claude/skills/scribe-code-review/agents/<lens>.md` is synced 1:1 into `.github/agents/<lens>.agent.md` by `scripts/Sync-ReviewAgents.ps1` and verified mechanically by `ScribeCodeReviewSkillTests`.
3. **Platform-enforced read-only toolset.** Each `.github/agents/<lens>.agent.md` sets `tools: ["read", "search"]` and `user-invocable: false`:
   - `read` grants `view` (file reading) and `search` grants `grep` and `glob`. Omitting `edit` and `execute` enforces read-only review at the host layer: a lens agent cannot edit files, run shell commands, push commits, or call `gh`.
   - `user-invocable: false` keeps the 26 internal review lenses out of the interactive agent dropdown while keeping them callable programmatically via `task(agent_type: "<lens>", ...)` and `copilot --agent=<lens>`.
   - **Never** set `disable-model-invocation: true` or `infer: false` on a lens agent: Copilot treats both as blocking programmatic `task` delegation, hiding the agent from the orchestrator.
   - **Prompt length ceiling:** GitHub custom agents enforce a **30,000-character maximum** on the Markdown body below the YAML frontmatter. Every lens prompt stays strictly below 30,000 characters.
4. **Explicit per-call model, reasoning, and context overrides.** In GitHub Copilot App and CLI:
   - `.agent.md` YAML frontmatter supports `model`, not `context_tier`, and user `/subagents` settings (`~/.copilot/settings.json` / `<subagent_model_preferences>`) override `.agent.md` `model` defaults unless the `task` call passes explicit overrides.
   - When a sub-agent resolves to a different model from the parent session without an explicit `reasoning_effort`, the runtime falls back to that model's default effort rather than inheriting the parent's.
   - Therefore, **every `task` call in `scribe-code-review` passes `model`, `reasoning_effort`, and `context_tier: "long_context"` explicitly**. Because `scribe-code-review` is an active repository skill explicitly specifying these sub-agent parameters, passing them satisfies the runtime sub-agent override contract.
5. **Workspace-local review cache (`artifacts/scribe-code-review/<run-id>/`).** Sub-agents in GitHub Copilot App scope `view`, `grep`, and `glob` to the repository workspace and deny paths under `$env:TEMP` (`AppData\Local\Temp`) in non-interactive or autopilot runs. Writing the review cache to `<repo-root>\artifacts\scribe-code-review\<run-id>\` keeps every diff, metadata file, comment digest, and verification packet readable via `view` while staying untracked (`artifacts/` is gitignored in `.gitignore`).
6. **`AGENTS.md` inheritance.** Custom sub-agents spawned via `task` do not automatically receive `AGENTS.md` on every host. Every dispatch prompt instructs the lens to read `AGENTS.md` (or its relevant sections) with `view` when checking repository rules and settled decisions.

---

## 2. Ordered 4-Model Roster and Exact Runtime Settings

Use this exact priority order and parameter configuration. Never pass `"auto"`, never invent model IDs, and never omit `reasoning_effort` or `context_tier`.

| Priority | Model | Exact `model` ID | `reasoning_effort` | `context_tier` | Role in `scribe-code-review` |
| --- | --- | --- | --- | --- | --- |
| **1** | Gemini 4 Carbon | `gemini-4-carbon` | `"high"` *(max supported: `low`, `medium`, `high`; `xhigh` and `max` unsupported)* | `"long_context"` | **Primary workhorse (Pass A):** runs the majority of specialist lenses (23 of 24 Pass A lenses). |
| **2** | GPT-6.1 Sol | `gpt-6.1-sol` | `"max"` *(supports `low`, `medium`, `high`, `xhigh`, `max`)* | `"long_context"` | **Adversarial & Pass B cross-family confirmation:** runs post-synthesis adversarial `finding-verification` (refuting Pass A findings from a distinct model family), Pass B panel confirmation (`architecture-fit`, `win32-interop`, `privacy-egress`, `word-packs-and-vocabulary`, `settings-and-persistence`, `tests-quality`), and optional built-in `rubber-duck`. |
| **3** | Grok 4.7 | `grok-4.7` | `"high"` *(supports `low`, `medium`, `high`, `xhigh`; `max` unsupported)* | `"long_context"` | **Pass C xAI confirmation & alternatives challenger:** runs `solution-alternatives` in Pass A, Pass C 3rd-family confirmation (`overlay-process-contract`, `fragile-area`, `win32-interop`, `privacy-egress`, `macos-port`), optional built-in `security-review`, and `finding-verification` whenever `gpt-6.1-sol` authored most drafted findings. |
| **4** | Claude Sonnet 5.5 | `claude-sonnet-5.5` | `"max"` *(use `"xhigh"` only if a host rejects `"max"`)* | `"long_context"` | **4th-family adjudicator & ordered fallback:** runs post-synthesis `maintainer-decision` concurrently with `finding-verification`, Pass D 4th-family tie-breaker on critical changes, and ordered fallback if Rank 3 (`grok-4.7`) is unavailable or returns empty after retry. |

---

## 3. Dispatch Path A: In-Session `task` Tool (GitHub Copilot App & Interactive Copilot CLI)

Path A is the primary dispatch mechanism in both **GitHub Copilot App** and **GitHub Copilot CLI**.

### Example: Pass A specialist lens (`gemini-4-carbon`)

```json
{
  "agent_type": "win32-interop",
  "name": "pass-a-win32-interop",
  "description": "Review Win32 interop lens",
  "mode": "background",
  "model": "gemini-4-carbon",
  "reasoning_effort": "high",
  "context_tier": "long_context",
  "prompt": "Run the win32-interop review lens.\n\n## Target\n- Kind: branch (main..HEAD)\n- Head SHA: <sha>\n- Round: 1\n- Cache directory: C:\\Users\\chrismckee\\GitHub\\Scribe\\artifacts\\scribe-code-review\\<run-id>\n\n## Patch in scope\nRead C:\\Users\\chrismckee\\GitHub\\Scribe\\artifacts\\scribe-code-review\\<run-id>\\diff.patch with view. Read AGENTS.md and surrounding repository files with view, grep, and glob for context.\n\n## Description\nRead C:\\Users\\chrismckee\\GitHub\\Scribe\\artifacts\\scribe-code-review\\<run-id>\\metadata.json.\n\n## Prior review comments\nRead C:\\Users\\chrismckee\\GitHub\\Scribe\\artifacts\\scribe-code-review\\<run-id>\\prior-comments-digest.md.\n\n## Framing hints\n<Step 2.5 hints for this lens, or 'None.'>\n\n## Repository output rules\n- Do not use U+2014 (em dash) or U+2013 (en dash) anywhere in your output.\n- Never claim the compiler, build, or test suite will catch or prove an issue.\n- End with [[agent-done:win32-interop findings=<n> coverage=complete|incomplete]] on its own final line."
}
```

### Example: Post-synthesis adversarial verification (`gpt-6.1-sol`) and maintainer gate (`claude-sonnet-5.5`)

Dispatch both concurrently in one response after Step 4 synthesis writes `findings-for-verification.json`:

```json
{
  "agent_type": "finding-verification",
  "name": "theta-finding-verification",
  "description": "Verify drafted review findings",
  "mode": "background",
  "model": "gpt-6.1-sol",
  "reasoning_effort": "max",
  "context_tier": "long_context",
  "prompt": "Adjudicate the synthesized candidate findings in C:\\Users\\chrismckee\\GitHub\\Scribe\\artifacts\\scribe-code-review\\<run-id>\\findings-for-verification.json against diff.patch and the codebase. Read AGENTS.md for settled repository decisions. Output no U+2014 or U+2013 dashes and end with [[agent-done:finding-verification findings=<n> coverage=complete|incomplete]]."
}
```

```json
{
  "agent_type": "maintainer-decision",
  "name": "theta-maintainer-decision",
  "description": "Check maintainer decision gates",
  "mode": "background",
  "model": "claude-sonnet-5.5",
  "reasoning_effort": "max",
  "context_tier": "long_context",
  "prompt": "Evaluate the synthesized findings and architecture/design drafts in C:\\Users\\chrismckee\\GitHub\\Scribe\\artifacts\\scribe-code-review\\<run-id>\\findings-for-verification.json against metadata.json, diff.patch, and AGENTS.md Ask-first boundaries. Output no U+2014 or U+2013 dashes and end with [[agent-done:maintainer-decision findings=<n> coverage=complete|incomplete]]."
}
```

### Collecting background `task` results

1. Launch independent Pass A tasks in parallel (`mode: "background"`), followed by any warranted Pass B, Pass C, or Pass D panel tasks.
2. Wait for automatic completion notifications rather than polling `read_agent` in a loop.
3. Read each finished agent once with `read_agent(agent_id: "<id>", wait: true)`.
4. Verify the trailing `[[agent-done:<lens> findings=<n> coverage=complete|incomplete]]` line. If an agent returned empty or omitted its marker, retry once; if a `grok-4.7` in-session task returns empty in the Copilot App, use Path B CLI dispatch or fall back to Rank 4 (`claude-sonnet-5.5`, `reasoning_effort: "max"`, `context_tier: "long_context"`) and record the substitution in the Summary.

---

## 4. Dispatch Path B: Standalone GitHub Copilot CLI Process (`copilot`)

Use Path B when driving reviews from an external terminal script or as a process-isolated fallback when an in-session `grok-4.7` sub-agent returns an empty response.

```powershell
$RunDir = Resolve-Path "artifacts\scribe-code-review\<run-id>"
$PacketPath = Join-Path $RunDir "prompts\overlay-process-contract.packet.md"
# Keep -p short so Windows CreateProcess never hits its 32,767-character command-line limit!
# --agent loads .github/agents/<lens>.agent.md directly from disk.
$Bootstrap = "Read the dispatch packet at $PacketPath and execute every instruction in full."

# Optional: disable unrelated global MCP servers so one-shot CLI startup does not wait on external MCP handshakes.
$McpDisableArgs = @()
$McpJson = copilot mcp list --json 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($McpJson)) {
    try {
        $Servers = $McpJson | ConvertFrom-Json
        foreach ($s in @($Servers)) {
            if ($s.name) { $McpDisableArgs += @("--disable-mcp-server", [string]$s.name) }
        }
    } catch { }
}

$Output = & copilot --agent=overlay-process-contract `
    --model=grok-4.7 `
    --reasoning-effort=high `
    -p $Bootstrap `
    -s --no-remote-export `
    --deny-tool='write' `
    --deny-tool='shell' `
    @McpDisableArgs
if ($LASTEXITCODE -ne 0) {
    throw "Copilot CLI exited with code $LASTEXITCODE for overlay-process-contract."
}
```

### Critical Windows and Copilot CLI Rules for Path B

- **Never pass a full lens file plus diff via `-p` on Windows.** Win32 `CreateProcess` caps the entire command line at 32,767 characters. Passing `--agent=<lens>` loads `.github/agents/<lens>.agent.md` from disk automatically; `-p` carries only the one-line pointer to `$PacketPath` inside `artifacts\scribe-code-review\<run-id>\prompts\`.
- **Always check `$LASTEXITCODE` and the `[[agent-done:...]]` marker.** A `0` exit code without the completion marker is still an incomplete lens run.
- **Initialize `@McpDisableArgs` as `@()`**, never `$null`, so PowerShell splatting (`@McpDisableArgs`) succeeds when no MCP servers are configured.
- **Enforce read-only twice:** `.github/agents/<lens>.agent.md` sets `tools: ["read", "search"]`, and `--deny-tool='write' --deny-tool='shell'` guarantees no file writes or shell commands even if invoked without `--agent`.

---

## 5. Starting a Dedicated Review Session in GitHub Copilot App

When launching a fresh child session in GitHub Copilot App (`create_session` or `open_pr_session`) to execute `scribe-code-review` end to end, initialize the orchestrator itself on Rank 1 (`gemini-4-carbon`, `high`, `long_context`):

```json
{
  "name": "Scribe code review",
  "kickoff": {
    "prompt": "Invoke the scribe-code-review skill (skill: \"scribe-code-review\") as your first action and run a full review of <target>.",
    "model": "gemini-4-carbon",
    "reasoning_effort": "high",
    "context_tier": "long_context",
    "mode": "autopilot"
  }
}
```
