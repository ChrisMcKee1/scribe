# AGENTS.md — Scribe

> Context for AI coding agents working on **Scribe**. Read this first when you pick up
> fresh work; it captures the durable facts, commands, architecture, and hard‑won gotchas
> so you don't relearn them every session. Human‑facing docs live in
> [`README.md`](README.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md).

## What Scribe is

Private, **fully offline** push‑to‑talk voice dictation for **Windows 11**. Hold a key,
speak, release — punctuated text is typed into whatever app has focus. Audio is captured,
transcribed in memory on the CPU, and discarded. Nothing is uploaded. The only optional
online feature is AI cleanup against a user‑configured Azure/Foundry endpoint (sends the
*transcribed text only*, never audio, and is strictly opt‑in).

## Tech stack (be specific — versions matter)

- **Language / runtime:** C# / **.NET 10** (`net10.0-windows`), **.NET 10 SDK 10.0.301+**.
- **App shell:** **WPF** tray app (`src/Scribe.App`), `win-x64`, self‑contained.
- **Recording overlay:** **WinUI 3 / Windows App SDK 2.2.0** as a *separate* unpackaged,
  self‑contained `x64` process (`src/Scribe.Overlay`, `Scribe.Overlay.exe`). See
  [Overlay architecture](#overlay-architecture-read-before-touching-the-pill) — it is not
  a normal window.
- **ASR:** NVIDIA **Parakeet TDT 0.6b v3** (CC‑BY‑4.0) via **sherpa‑onnx 1.13.3**
  (Apache‑2.0) on CPU. **VAD:** Silero (MIT).
- **AI cleanup:** Microsoft **Agent Framework** (`AIAgent`) — one code path for on‑device
  **Foundry Local** and cloud **Microsoft Foundry**.
- **Persistence:** SQLite via `Microsoft.Data.Sqlite`. **Packaging/updates:** Velopack.
- **Build system:** central package management (`Directory.Packages.props`), shared version
  in `Directory.Build.props`. Current version: **0.1.10**.

## Commands (run these — include the flags)

```powershell
# One-time: download ASR + VAD models (~670 MB) into src/Scribe.App/models (gitignored)
pwsh ./scripts/Download-Models.ps1

# Build the whole solution (all 5 projects, incl. the x64-only overlay)
dotnet build Scribe.slnx -c Debug

# Run the app — Scribe appears in the system tray
dotnet run --project src/Scribe.App

# Jump straight to the settings window (handy while iterating on UI)
dotnet run --project src/Scribe.App -- --settings

# Run the unit tests (must stay green; currently 86/86)
dotnet test tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj

# Build the overlay alone (it is x64-only — Platform MUST be x64)
dotnet build src/Scribe.Overlay/Scribe.Overlay.csproj -c Debug -p:Platform=x64

# Offline AI-cleanup quality eval (no network, no judge model)
dotnet run --project tools/Scribe.Evals
dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini

# Build the Velopack installer locally (unsigned is fine for testing)
./build/pack.ps1 -Version 0.1.5
```

**Always run `dotnet build Scribe.slnx -c Debug` and the tests before declaring work done.**
Target 0 warnings / 0 errors — warnings are treated seriously.

## Project structure

```
Scribe.slnx                         solution (Core, App, Overlay[x64], tests, tools)
  src/Scribe.Core/                  services + domain — UNIT-TESTABLE, no UI
    Audio/ Vad/ Transcription/      capture → 16 kHz mono, Silero VAD, Parakeet ASR
    PostProcessing/ Cleanup/        dictionary fixups; optional AI cleanup (Agent Framework)
    TextInjection/ Hotkeys/         Unicode keystroke injection; Right Ctrl push-to-talk
    Persistence/ Settings/ Security/ Infrastructure/ Diagnostics/ Models/ DependencyInjection/
  src/Scribe.App/                   WPF tray shell: bootstrap + DI, Settings, Tray, History
    Overlay/                        OverlayProcessClient (drives the WinUI 3 pill over a pipe)
    Infrastructure/                 FileLoggerProvider (shared daily log — see Logging mandate)
    models/                         downloaded ASR/VAD models (gitignored)
  src/Scribe.Overlay/               standalone WinUI 3 transparent pill (Scribe.Overlay.exe)
    OverlayWindow.xaml(.cs)         the pill geometry/visuals (LogicalWidth=264, Height=110)
    Ipc/ Logging/ Interop/          named-pipe server, OverlayLog (same log file), Win32 interop
  tests/Scribe.Core.Tests/          xUnit tests for Core
  tools/Scribe.Evals/               offline cleanup eval harness (eval pkgs are PrivateAssets=all)
  scripts/Download-Models.ps1       fetches ASR + VAD models
  build/pack.ps1                    Velopack installer + GitHub-release publisher
  Directory.Build.props             single source of version truth (<VersionPrefix>)
  Directory.Packages.props          central NuGet version management — add versions HERE
```

**Architectural rule:** most logic lives in **Scribe.Core** so it is testable without a UI.
New behavior lands in Core *with a test*; `Scribe.App` is a thin shell that binds it to the UI.

## Code style

- Honor `.editorconfig`. Keep the build warning‑clean.
- **Comment the *why*, not the *what*.** Only annotate genuinely non‑obvious decisions.
- Add NuGet versions to `Directory.Packages.props` (central management is on). Prefer
  current **stable** releases; justify any prerelease in the PR.
- Example of the expected style (descriptive names, real error handling, `why` comment):

```csharp
// FileShare.ReadWrite + retry: the overlay process appends to this SAME daily log
// concurrently, so a plain File.AppendAllText would throw a sharing violation.
private static void Append(string path, string line)
{
    for (var attempt = 0; attempt < 12; attempt++)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(fs);
            w.WriteLine(line);
            return;
        }
        catch (IOException) { Thread.Sleep(15); } // transient lock — retry, never propagate
    }
}
```

## Logging mandate (non‑negotiable)

Logging is how we debug the hard, intermittent bugs in this app — **it must never be the
cause of one.**

- Both processes append to the **same** daily file:
  `%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log` (so dictation + overlay events
  interleave on one timeline).
- All log writers open with **`FileShare.ReadWrite` + retry + swallow** and are
  **fully non‑throwing** end to end (`FileLoggerProvider` on the app side, `OverlayLog` on
  the overlay side). A throwing logger once tore down a healthy overlay — see below.
- **Never** let a logging/diagnostics failure reach a destructive code path (e.g. a catch
  that kills a process). Route diagnostics in catch blocks through non‑throwing helpers
  (`TryLog`). When in doubt, log *more* lifecycle/state detail, not less.

## Overlay architecture (read before touching the pill)

The recording "pill" is a **separate WinUI 3 process**, not a WPF window. This is the
permanent fix for a long‑recurring **"black box / pill disappears"** bug: .NET 10 WPF
`AllowsTransparency` + layered‑window rendering (`UpdateLayeredWindow`, dotnet/wpf #11321)
intermittently painted an opaque black box. WinUI 3 renders through DWM composition
(`SystemBackdropElement`/`TransparentBackdrop`) and sidesteps the legacy layered path.

- The WPF engine drives the overlay one‑way over a **named pipe** via
  `src/Scribe.App/Overlay/OverlayProcessClient.cs` (state changes, meter levels, position,
  hide/exit).
- The pill's screen anchor is set with the `POSITION <name>` pipe command. The wire tokens are the
  value names of **two enums kept in sync by name**: `Scribe.Core.Models.OverlayPosition` (engine)
  and `Scribe.Overlay.OverlayAnchor` (overlay — it deliberately has no Scribe.Core reference).
  Add/rename values in BOTH or the overlay silently ignores the command. The client replays the
  applied position right after every pipe (re)connect, so relaunches keep the user's anchor.
- `Scribe.Overlay.exe` is resolved in this order: `SCRIBE_OVERLAY_EXE` env →
  **installer layout** `AppContext.BaseDirectory\Overlay\Scribe.Overlay.exe` → dev fallback
  walking the repo to `src\Scribe.Overlay\bin\...\Scribe.Overlay.exe`.
- **Orphan safety:** the overlay is launched into an OS **Job Object** (kill‑on‑close) and
  also runs a parent‑PID watchdog (`--parent`), so the pill can never outlive the engine.
- If you change overlay behavior, verify with the live log: look for `installer layout`,
  `size=462x192`, `transparent=True backdrop=TransparentBackdrop`, and that the overlay PID
  stays alive (no teardown) with **zero IOExceptions** after launch.
- `src/Scribe.App/Overlay/RecordingOverlay.xaml.cs` is the **dead‑code WPF fallback** —
  do not extend it; the WinUI 3 path is canonical.

## Releases & Velopack (gotchas)

`build/pack.ps1` publishes a self‑contained `win-x64` app, bundles the overlay
self‑contained into the payload under `Overlay\`, packs with Velopack, and (with
`-Publish`) uploads to GitHub Releases.

- The script default `-Version` is `0.1.0` — **always pass the real version**
  (e.g. `-Version 0.1.5`) and keep it in sync with `Directory.Build.props`.
- `vpk` **refuses to pack an equal/greater version that already exists** in `releases\`.
  To repack the same version, delete that version's `*-full.nupkg`, `*-delta.nupkg`,
  `Scribe-win-x64-Setup.exe`, `Scribe-win-x64-Portable.zip`, and `releases.win-x64.json`
  — but **keep the older `*-full.nupkg`s** so the delta can build.
- Channel is `win-x64`. The full nupkg is large (~640 MB, the overlay adds ~90 MB
  self‑contained); the delta is small (~86 MB).
- To publish without a rebuild, set `$env:GITHUB_TOKEN = gh auth token` and run
  `vpk upload github -o releases --channel win-x64 --repoUrl https://github.com/ChrisMcKee1/scribe --publish --releaseName "Scribe <ver>" --tag v<ver> --targetCommitish main --merge`.
- Code signing is **opt‑in** (Azure Trusted Signing `-AzureTrustedSignFile`, or
  `-SignToolParams`). Prior 0.1.x releases shipped **unsigned** (SmartScreen warns) — that
  is the established norm; confirm with the user before changing it.

## Git workflow

- Branch off `main`; keep PRs small and focused. Open an issue first for large changes.
- Commit message: what changed **and why**. Always append this trailer (per house rule):

  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  ```

- Run build + tests green before committing. `releases/` and `publish/` are gitignored —
  never commit build artifacts or the downloaded models.

## Boundaries

**Always:**
- Keep the **offline‑first promise** intact — the core dictation path must never require a
  network. Online features (Azure/Foundry cleanup) are strictly opt‑in.
- Put new logic in `Scribe.Core` with a test; keep the build warning‑clean.
- Keep all logging non‑throwing and use `FileShare.ReadWrite` + retry on the shared log.
- Build the overlay with `-p:Platform=x64`; verify the pill via logs after overlay changes.

**Ask first:**
- Bumping the version, cutting a release, or changing the signing posture (signed vs unsigned).
- Adding/upgrading NuGet dependencies, or anything touching `Directory.Packages.props`.
- Adding a new third‑party component (must be license‑compatible with MIT and credited in
  the README attribution section).
- Schema/migration changes to the SQLite store.

**Never:**
- Commit secrets, API keys, or the downloaded models (`src/Scribe.App/models`).
- Remove the SQLite pin: `SQLitePCLRaw.bundle_e_sqlite3 3.0.3` overrides a transitive build
  affected by **CVE‑2025‑6965** (pulls patched `e_sqlite3` 3.50.3). Don't remove without an
  equivalent fix.
- Reintroduce a WPF transparent/layered‑window pill, or revert the overlay to in‑process —
  that bug is solved by the out‑of‑process WinUI 3 design.
- Let a logging failure reach a destructive catch (process kill, teardown).
- Send audio anywhere off the device.

## Environment notes (this dev box)

- Windows; use **Windows‑style paths** (`\`) and PowerShell (not DOS) commands.
- Logs to read when debugging: `%LOCALAPPDATA%\ScribeData\logs\scribe-<date>.log`. Config +
  `scribe.db` live under `%LOCALAPPDATA%\ScribeData`. Installed app:
  `%LOCALAPPDATA%\Scribe\current\` (overlay at `current\Overlay\`, models at `current\models`).
- When killing Scribe processes here, query PIDs first and use **`Stop-Process -Id <literal-PID>`**
  (name/pipe kills and `-Id $_.Id` in a pipeline are blocked by the sandbox guard).
- `gh` is authenticated (`ChrisMcKee1`, `repo`+`workflow` scopes); there is no `GITHUB_TOKEN`
  env var, so set `$env:GITHUB_TOKEN = gh auth token` for `vpk upload`/`pack.ps1 -Publish`.
