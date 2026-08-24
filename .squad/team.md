# Squad Team

> scribe

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Lead | Lead | .squad/agents/lead/charter.md | 🏗️ Active |
| Frontend | macOS UI Dev | .squad/agents/frontend/charter.md | ⚛️ Active |
| Backend | Audio/ASR Dev | .squad/agents/backend/charter.md | 🔧 Active |
| Platform | Core Porting Dev | .squad/agents/platform/charter.md | 🔩 Active |
| Tester | Tester | .squad/agents/tester/charter.md | 🧪 Active |
| Scribe | Scribe | .squad/agents/scribe/charter.md | 📋 Active |
| Ralph | Work Monitor | .squad/agents/ralph/charter.md | 🔄 Active |
| Rai | RAI Reviewer | .squad/agents/Rai/charter.md | 🛡️ RAI |
| Fact Checker | Fact Checker | .squad/agents/fact-checker/charter.md | 🔍 Verifier |


## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Project:** scribe
- **Created:** 2026-08-24
- **User:** x3nc0n
- **Current focus:** New macOS port of Scribe (voice dictation). The shipped product is Windows-11
  only (WPF/WinUI/.NET 10); no macOS code exists yet in this repo. This is greenfield porting work,
  not a continuation of prior (unsaved) work.
