# Contributing to Scribe

Thanks for your interest in improving Scribe! It's an open-source, fully offline
voice-dictation app for Windows, and contributions of all kinds are welcome:
bug reports, feature ideas, documentation, and code.

By participating, you agree to keep things friendly and respectful. Be kind, assume
good intent, and help newcomers.

---

## Ways to contribute

- **Report a bug.** Open an issue with steps to reproduce, what you expected, and
  what happened. Logs live in `%LOCALAPPDATA%\ScribeData\logs` and are very helpful.
- **Suggest a feature.** Open an issue describing the problem you're trying to solve
  (not just the solution). Real-world dictation pain points are gold.
- **Improve the docs.** README clarity, typos, and screenshots are always welcome.
- **Send a pull request.** See the workflow below.

---

## Development setup

| Requirement | Notes |
|---|---|
| **Windows 11 (x64)** | Scribe is a Windows tray app and uses Win32 + WASAPI directly. |
| **.NET 10 SDK** (`10.0.301`+) | Includes the Windows Desktop runtime. `dotnet --version` to check. |
| **~1 GB free disk** | For the speech model downloaded by the setup script. |
| A microphone | For actually testing dictation. |

```powershell
# 1. Clone
git clone https://github.com/ChrisMcKee1/scribe.git
cd scribe

# 2. One-time: download the speech models (~670 MB) into src/Scribe.App/models.
#    The installer ships with them bundled, but they are too large to live in git,
#    so source builds fetch them once. The folder is gitignored.
pwsh ./scripts/Download-Models.ps1

# 3. Restore + build (all five projects, including the x64-only overlay)
dotnet build Scribe.slnx -c Debug

# 4. Run the app: Scribe appears in your system tray
dotnet run --project src/Scribe.App
```

To jump straight to the settings window on launch (handy while iterating on UI):

```powershell
dotnet run --project src/Scribe.App -- --settings
```

---

## Project layout

```
Scribe.slnx
  src/Scribe.Core            Services + domain logic: audio capture, transcription, VAD,
                             post-processing, snippets, per-app profiles, text injection,
                             hotkeys, persistence, and the AI text-cleanup providers.
  src/Scribe.App             WPF tray app: bootstrap + DI, settings window, dictation loop.
  src/Scribe.Overlay         The recording pill: a standalone WinUI 3 process the app drives
                             over a named pipe (DWM-composited transparency; AGENTS.md
                             explains why it is out-of-process).
  tests/Scribe.Core.Tests    Unit tests for the Core services.
  tools/Scribe.Evals         Offline eval harness for AI-cleanup quality (see below).
  scripts/                   Helper scripts (model download, etc.).
  build/pack.ps1             Velopack installer + GitHub-release packaging (see below).
  docs/screenshots/          Images used by the README.
  Directory.Build.props      Shared versioning + package metadata (semver lives here).
  Directory.Packages.props   Central NuGet version management (add versions here).
```

Most logic lives in **Scribe.Core** so it can be unit-tested without a UI. The WPF app
is a thin shell that wires services together. New behavior should generally land in Core
with a test, and the App project just binds it to the UI.

The optional AI cleanup is built on the **Microsoft Agent Framework** (`AIAgent`), so the
on-device (Foundry Local), cloud (Microsoft Foundry) and bring-your-own (OpenAI-compatible)
providers share one code path and are easy to extend.

---

## Building, testing, and style

- **Build everything:** `dotnet build Scribe.slnx -c Debug`
- **Run the tests:** `dotnet test tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj`
- **Style:** the repo ships an `.editorconfig`; please keep to it. Run a build before
  pushing; warnings are treated seriously.
- **Dependencies:** add/upgrade NuGet versions in `Directory.Packages.props` (central
  package management is on). Prefer current **stable** releases; call out any prerelease
  in your PR description and why it's needed.

A few conventions that keep the codebase consistent:

- Comment the *why*, not the *what*. Keep comments for genuinely non-obvious decisions.
- Keep the offline-first promise intact: the core dictation path must never require a
  network. Online features (like Azure AI cleanup) are strictly opt-in.
- Don't commit secrets, API keys, or the downloaded models. They're gitignored for a
  reason.

---

## Evaluating AI-cleanup quality

`tools/Scribe.Evals` is an offline harness that drives the real cleanup service across a
suite of writing-style prompts (pirate, Old English, French translation, bulleted to-do,
plus semantic condensation scenarios covering spoken self-corrections and redundancy
merging), then scores each output with a deterministic
[`Microsoft.Extensions.AI.Evaluation`](https://learn.microsoft.com/dotnet/ai/evaluation/)
`IEvaluator`. It proves a prompt change actually changes the output and lets you compare
models head-to-head, with no judge model and no network. If your PR touches the cleanup
prompt or providers, run it. The eval packages are referenced `PrivateAssets="all"`, so
they never ship with the app.

```powershell
# Score the default on-device model
dotnet run --project tools/Scribe.Evals

# Compare two Foundry Local models
dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini
```

---

## Pull request workflow

1. Fork the repo and create a branch: `git checkout -b my-feature`.
2. Make your change, with a test in `Scribe.Core.Tests` where it makes sense.
3. Run `dotnet build` and `dotnet test`; both should be green.
4. Write a clear commit message (what changed and why).
5. Open a PR against `main` describing the change and how you verified it. Screenshots
   help a lot for UI tweaks.

Small, focused PRs are easier to review and merge. If you're planning something large,
open an issue first so we can agree on the approach.

---

## Releases & updates (maintainers)

`build/pack.ps1` publishes a self-contained `win-x64` build, packs it with Velopack
(installer + delta updates), and can upload the result to GitHub Releases. Code signing
is opt-in via **Azure Trusted Signing** (`-AzureTrustedSignFile`) or a local certificate
(`-SignToolParams`); installed apps then auto-update from the matching release channel.
Versioning is semantic and lives in `Directory.Build.props`.

---

## A note on the speech model & licenses

Scribe uses **NVIDIA Parakeet TDT 0.6b v3** (CC-BY-4.0) via **sherpa-onnx** (Apache-2.0)
and **Silero VAD** (MIT). When contributing, make sure any new third-party component is
license-compatible with an MIT-licensed app and credited in the README's Attribution
section.

`Microsoft.Data.Sqlite` transitively references a SQLite build affected by
**CVE-2025-6965**; Scribe pins `SQLitePCLRaw.bundle_e_sqlite3 3.0.3` to pull the patched
native. Please don't remove that pin without an equivalent fix.

Thanks again, and happy hacking! 🎙️
