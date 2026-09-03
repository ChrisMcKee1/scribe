# AGENTS.md: Scribe

> Context for AI coding agents working on **Scribe**. Read this first when you pick up
> fresh work; it captures the durable facts, commands, architecture, and hard‑won gotchas
> so you don't relearn them every session. Human‑facing docs live in
> [`README.md`](README.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md).

## What Scribe is

Private, **fully offline** push‑to‑talk voice dictation for **Windows 11**. Hold a key,
speak, release: punctuated text is typed into whatever app has focus. Audio is captured,
transcribed in memory on the CPU, and discarded. Nothing is uploaded. The only optional
online feature is AI cleanup against a user‑configured Azure/Foundry/OpenAI‑compatible
endpoint (sends the *transcribed text only*, never audio, and is strictly opt‑in).

**Feature surface (so you don't reinvent what's shipped):** overlay pill with a 9‑anchor
position picker + on‑screen preview; user **dictionary** (CSV import/export, history‑mined
suggestions); **voice snippets** (spoken trigger → saved template); **per‑app profiles**
(writing style + newline mode by focused process); **AI cleanup** across four providers
(Foundry Local on‑device, Microsoft Foundry via `az login` **or an Entra service principal**, or
any OpenAI‑compatible endpoint like Ollama/LM Studio/OpenRouter); **silence auto‑stop** for toggle mode;
**playground** for testing normal push-to-talk with raw recognition, dictionary/library/snippet
replacement highlights, and per-step timings across the full pipeline;
**diagnostics** panel (P50/P95 decode latency + RTF from local history); **usage insights**
(local totals/trend chart/top apps/recurring terms with one-click dictionary add; opt-in AI
insight sends aggregate totals + dictionary-covered term labels ONLY; novel mined terms never
leave the machine); **dictation recovery** (last 5 transcripts in a tray submenu, injection
failure raises a recovery notification); **tray quick add to dictionary** (chip-style word picker
over a recent dictation that saves the fix and repairs that transcript in place); **dictionary
cleanup** (finds terms whose spoken and written forms have both never appeared in history, and
disables them by default rather than deleting); tray quick toggles (AI cleanup on/off, pause) and a
first-run **welcome**; an **About** page links privacy, support, source, and the GitHub star path.
The default writing style ships
editorial number/date/time/acronym + self‑correction + redundancy rules and is the
benchmark‑validated optimum (see `docs/model-leaderboard.md`; a stricter A/B regressed it).

**The model is multilingual already.** The bundled `parakeet-tdt-0.6b-v3-int8` handles ~25
European languages out of the box. It is a **transducer** with the vocabulary baked in, so
there is **no runtime language parameter**, so do NOT build a "language picker" setting; the
model auto‑handles whatever is spoken. (Whisper takes a language hint; this does not.)

## Mono-repo note: native macOS port (`macos/Scribe`, PR #61)

`macos/Scribe` is a real, hand-maintained macOS reimplementation, not a shared-code port of
`Scribe.Core`. It is a separate Swift Package Manager app: `macos/Scribe/Package.swift` declares
package `ScribeMac`, executable target `Scribe`, and test target `ScribeTests`. No macOS build
path references any C# project in this repo. If you change Windows-side behavior in `Scribe.Core`,
such as dictionary matching, snippets, cleanup prompt composition, diagnostics percentile math,
usage-insight aggregation, or CSV import/export, assume a parallel Swift copy under
`macos/Scribe/Sources/Scribe/` and `macos/Scribe/Tests/ScribeTests/` may need the same edit, then
check `macos/PORTING-PLAN.md` before you claim parity still holds.

Where the macOS port lives: `macos/Scribe/Sources/Scribe/` currently contains 59 tracked source
files on `origin/main`; `macos/Scribe/Tests/ScribeTests/` contains 27 tracked XCTest files, many
ported 1:1 from the C# xUnit suites where applicable. Packaging scripts live in
`macos/Scribe/scripts/` (`build-app.sh`, `make-dmg.sh`, `notarize.sh`, `setup-dev-signing.sh`).
The top-level docs are `macos/README.md`, the user-facing build/run guide and current feature list;
`macos/PORTING-PLAN.md`, the authoritative row-by-row Windows-feature-parity checklist and
implementation notebook, currently starting from `Status: Feature parity complete`; and
`macos/CLEANUP-MODEL-BENCHMARK.md` plus `macos/benchmark_cleanup.py`, the separate macOS local-model
cleanup benchmark whose heuristic scores are explicitly not directly comparable to Windows'
`docs/model-leaderboard.md`.

Key macOS architecture facts before you edit it: on-device ASR uses Foundry Local
`parakeet-tdt-0.6b-v2`, with the dynamic port resolved from `foundry status -o json`; managed
Ollama is the alternative local provider. Unlike Windows, the macOS app does not bundle an ASR
runtime or model, users install Foundry Local or Ollama themselves. Silence auto-stop is currently
`SilenceAutoStopDetector`, an energy-threshold RMS detector marked as a stopgap rather than a
trained Silero VAD. AI cleanup mirrors the Windows provider categories, Foundry Local, managed
Ollama, any OpenAI-compatible endpoint, and Microsoft Foundry cloud via `AzureCredential.swift`
plus `KeychainStore.swift`; verify the current configuration surface in `macos/PORTING-PLAN.md`
and `macos/README.md` before editing setup guidance, because that area is moving quickly. The
overlay pill is in-process: `OverlayPanelController` hosts a borderless, non-activating `NSPanel`
with SwiftUI content, no separate overlay process or IPC. Persistence is a separate SQLite store in
`PersistenceStore`, not Windows' `scribe.db`, with its own non-destructive probe-then-`ALTER TABLE`
migrations. Packaging today is still dev-focused: `build-app.sh` produces an ad-hoc `.app`, and
`macos/README.md` remains the source of truth for the current gaps around notarization and
auto-update.

If your Windows PR changes behavior that the Swift port mirrors, call out in your PR description or
commit message that the matching row in `macos/PORTING-PLAN.md` may now be stale. There is no
automation keeping the C# and Swift implementations in sync.

`origin/feat/macos-apple-silicon` exists as an earlier, separate prototype that was not merged, and
it is not the current macOS effort described here.

## Tech stack (be specific, versions matter)

- **Language / runtime:** C# / **.NET 10**, targeting **Windows 11** (`net10.0-windows10.0.22000.0`),
  **.NET 10 SDK 10.0.301+**. Scribe optimizes for current Windows and does not carry a Windows 10
  compatibility story: the Store package refuses to install below 19041 and the product is Windows 11
  only, so do not lower `SupportedOSPlatformVersion` to "widen support". It buys nothing real and
  blocks Windows 11 APIs and WinML hardware acceleration.
- **App shell:** **WPF** tray app (`src/Scribe.App`), **`win-x64` and `win-arm64`**, self-contained.
- **Recording overlay:** **WinUI 3 / Windows App SDK 2.2.0** as a *separate* unpackaged,
  self-contained process (`src/Scribe.Overlay`, `Scribe.Overlay.exe`), built for the same
  architecture as the app. See
  [Overlay architecture](#overlay-architecture-read-before-touching-the-pill); it is not
  a normal window.
- **ASR:** NVIDIA **Parakeet TDT 0.6b v3** (CC‑BY‑4.0) via **sherpa‑onnx 1.13.4**
  (Apache‑2.0) on CPU. **VAD:** Silero (MIT). Native runtime is per-architecture; see
  [Architecture support](#architecture-support-x64-and-arm64).
- **AI cleanup:** Microsoft **Agent Framework** (`AIAgent`), one code path for on‑device
  **Foundry Local** (`Microsoft.AI.Foundry.Local.WinML`) and cloud **Microsoft Foundry**.
- **Persistence:** SQLite via `Microsoft.Data.Sqlite`. **Packaging/updates:** Velopack.
- **Build system:** central package management (`Directory.Packages.props`), shared version
  in `Directory.Build.props`. Read `<VersionPrefix>` from that file rather than trusting a
  number quoted here; a version pinned in prose is stale the next time anyone ships.

### Dependency rules learned the hard way

- **Do not wrap an SDK capability that already exists.** If Foundry Local, Agent Framework or
  Extensions.AI exposes something, call it. Helper types that re-derive information the SDK already
  states (parsing model aliases, guessing hardware) are how correctness bugs get built.
- **Query the NuGet feed for versions, never a web search.** `dotnet package search <id>
  --exact-match --format json` is authoritative; a search result claimed 1.17.0 when the feed had
  1.18.0.
- **`OpenAI` is pinned at 2.12.0 on purpose.** `Microsoft.Extensions.AI.OpenAI` declares
  `[2.12.0, 2.13.0)`, so 2.13.0 breaks restore with NU1608. This also forces the stored-output
  workaround below: `ProjectResponsesClient` needs a constructor that only exists in 2.13.0, so it
  throws `MissingMethodException` at runtime while compiling perfectly.

### Cloud cleanup stores nothing (keep it that way)

The Azure **Responses API defaults to `store=true`**, which retains every cleaned dictation
server-side. Scribe sets `StoredOutputEnabled = false` through `ChatOptions.RawRepresentationFactory`
on both the project and account paths, and **fails closed** if it meets a raw representation it does
not recognize. This is a privacy control, not a preference: if it silently stops applying, Scribe
breaks its own promise. There is a test pinning the fail-closed behaviour; do not relax it.

## Commands (run these, including the flags)

```powershell
# One-time: download ASR + VAD models (~670 MB) into src/Scribe.App/models (gitignored)
pwsh ./scripts/Download-Models.ps1

# Build the whole solution (8 projects: Core, App, Overlay, tests, and four tools)
dotnet build Scribe.slnx -c Debug

# Run the app (Scribe appears in the system tray)
dotnet run --project src/Scribe.App

# Jump straight to the settings window (handy while iterating on UI)
dotnet run --project src/Scribe.App -- --settings

# Run the unit tests (must stay green; the count only ever grows, 878 as of 0.3.8)
dotnet test tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj

# Build the overlay alone. WinUI has no AnyCPU story, so Platform is REQUIRED and must match
# the architecture you intend to ship (x64 or ARM64).
dotnet build src/Scribe.Overlay/Scribe.Overlay.csproj -c Debug -p:Platform=x64
dotnet build src/Scribe.Overlay/Scribe.Overlay.csproj -c Debug -p:Platform=ARM64

# Cross-build the whole app for the other architecture from either machine
dotnet publish src/Scribe.App/Scribe.App.csproj -c Release -r win-arm64 --self-contained true

# Prove the NATIVE speech engine actually decodes on this machine (unit tests never touch it).
# Generates real speech with the Windows TTS engine, then runs it through TranscriptionService.
pwsh ./scripts/New-SpeechFixtures.ps1
dotnet run --project tools/Scribe.AsrCheck

# Characterise the recogniser rather than just smoke-test it. See "What the recogniser is not".
dotnet run --project tools/Scribe.AsrCheck -- --long-audio    # duration sweep, 5 s to 90 s
dotnet run --project tools/Scribe.AsrCheck -- --channel-mix   # what the multi-channel downmix costs
dotnet run --project tools/Scribe.AsrCheck -- --degraded      # SNR and reverb against duration

# Offline AI-cleanup quality eval (no network, no judge model)
dotnet run --project tools/Scribe.Evals
dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini

# Auxiliary prompt evals (UsageInsight + AiDictionarySuggester, deterministic checks)
dotnet run --project tools/Scribe.Evals -- --suite auxiliary

# Build the Velopack installer locally (version comes from Directory.Build.props)
./build/pack.ps1                        # x64 only (default)
./build/pack.ps1 -Architecture arm64    # Arm64 only
./build/pack.ps1 -Architecture all      # both, one channel each

# Build the Microsoft Store package (MSIX, needs the Windows SDK; never build an MSI).
# Defaults to -Architecture all, which emits a single .msixbundle covering both.
./build/pack-msix.ps1
```

**Always run `dotnet build Scribe.slnx -c Debug` and the tests before declaring work done.**
Target 0 warnings / 0 errors; warnings are treated seriously.

### A green build proves very little here

Three separate defects in one release compiled warning-clean and only appeared at runtime: a
`MissingMethodException` from a package version conflict, a probe token limit Azure rejects, and a
theme watcher that threw and silently forced the wrong theme. For anything touching a provider SDK,
a settings window, or startup:

1. **Run the app and read the log** at `%LOCALAPPDATA%\ScribeData\logs\scribe-<date>.log`. A XAML
   parse error, a runtime binding failure, and a swallowed exception are all invisible to the
   compiler.
2. **Verifying a string is present in a shipped DLL requires decoding UTF-16 at BOTH byte
   alignments** (offset 0 and 1). A single-alignment scan misses roughly half of .NET's metadata
   strings and will tell you a fix is missing when it is right there.
3. **Arm64 cannot be validated on an x64 box.** Cross-build and assert payload purity locally, then
   let the `windows-11-arm` CI runner exercise it on real hardware. Opening a PR is the cheapest way
   to get that.

## Project structure

```
Scribe.slnx                         solution (Core, App, Overlay, tests, 4 tools)
  src/Scribe.Core/                  services + domain (UNIT-TESTABLE, no UI)
    Audio/ Vad/ Transcription/      capture → 16 kHz mono, Silero VAD, Parakeet ASR
    PostProcessing/ Cleanup/        dictionary + snippets; optional AI cleanup (Agent Framework)
    Settings/                       pure builders extracted from the UI: DictionaryEntryBuilder,
                                    SnippetBuilder, ProfileBuilder, DictionaryImportMerger (tested)
    Diagnostics/                    DictationStats (P50/P95 latency + RTF percentiles)
    TextInjection/ Hotkeys/         Unicode/clipboard injection; Right Ctrl push-to-talk
    Persistence/ Security/ Infrastructure/ Models/ DependencyInjection/
  src/Scribe.App/                   WPF tray shell: bootstrap + DI, thin adapters over Core
    Settings/                       the nav-rail settings window (adapters call Core builders)
    Onboarding/                     WelcomeWindow (one-time first-run intro)
    Tray/ History/ Overlay/         tray menu + quick actions; history data/UI; OverlayProcessClient
    Infrastructure/                 FileLoggerProvider (shared daily log; see Logging mandate)
    models/                         downloaded ASR/VAD models (gitignored)
  src/Scribe.Overlay/               standalone WinUI 3 transparent pill (Scribe.Overlay.exe)
    OverlayWindow.xaml(.cs)         the pill geometry/visuals (LogicalWidth=264, Height=110)
    Ipc/ Logging/ Interop/          named-pipe server, OverlayLog (same log file), Win32 interop
  tests/Scribe.Core.Tests/          xUnit tests for Core
  tools/Scribe.Evals/               offline cleanup eval harness + the golden benchmark
    Benchmark/                      6-case golden suite -> docs/model-leaderboard.md (52 models)
  tools/Scribe.AsrCheck/            decodes real speech through the NATIVE engine (see below)
  tools/Scribe.Benchmarks/          BenchmarkDotNet hot paths (capture, cleanup, post-processing)
  tools/Scribe.InjectionLab/        times each injection path into a real focused Win32 control
  scripts/Download-Models.ps1       fetches ASR + VAD models
  build/pack.ps1                    Velopack installer + GitHub-release publisher
  build/pack-msix.ps1               Microsoft Store MSIX package (Store path; no MSI is built)
  Directory.Build.props             single source of version truth (<VersionPrefix>)
  Directory.Packages.props          central NuGet version management; add versions HERE
```

**Architectural rule:** most logic lives in **Scribe.Core** so it is testable without a UI.
New behavior lands in Core *with a test*; `Scribe.App` is a thin shell that binds it to the UI.
Settings-page validation/build logic belongs in `Scribe.Core/Settings/` (pure, tested); the
WPF row types are thin adapters that map to/from those Core inputs. Do not let logic drift
back into the code-behind; that is a recurring smell.

## Code style

- Honor `.editorconfig`. Keep the build warning‑clean.
- **Comment the *why*, not the *what*.** Only annotate genuinely non‑obvious decisions.
- **No em dashes or en dashes anywhere in the repo**, including code comments: rewrite with
  commas, colons, periods, or "to" for ranges. Ordinary ASCII hyphens are fine. Enforced in three
  layers, because a prompt instruction alone is advisory and models ignore it:
    1. Source prose and UI strings are dash-free (swept as of 0.3.5; the only deliberate exceptions
     are `Win32ClipboardTests` and `Scribe.InjectionLab`, which round-trip an em dash on purpose to
     prove Unicode survives the clipboard and injection paths).
    2. `CleanupPrompt.DefaultWritingStyle` / `DefaultFrontierPrompt` contain no dashes themselves.
     This matters more than it looks: the prompt is *shown to the model on every dictation*, so
     dashes in it were teaching the model to imitate the style straight into the user's text.
    3. `Scribe.Core/Cleanup/DashNormalizer` deterministically rewrites U+2014/U+2013 out of model
     output in `TextCleanupService.TrySanitize` and `CompleteAsync`. This is the only actual
     guarantee. It runs **after** the ramble/refusal guards (they compare the model's answer to the
     raw transcript, so mutating first could flip a borderline detection) and **only** on model
     output, never on dictionary entries or snippet templates, which are user-authored.
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
        catch (IOException) { Thread.Sleep(15); } // transient lock, retry, never propagate
    }
}
```

## Logging mandate (non‑negotiable)

Logging is how we debug the hard, intermittent bugs in this app: **it must never be the
cause of one.**

- Both processes append to the **same** daily file:
  `%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log` (so dictation + overlay events
  interleave on one timeline).
- All log writers open with **`FileShare.ReadWrite` + retry + swallow** and are
  **fully non‑throwing** end to end (`FileLoggerProvider` on the app side, `OverlayLog` on
  the overlay side). A throwing logger once tore down a healthy overlay (see below).
- **Never** let a logging/diagnostics failure reach a destructive code path (e.g. a catch
  that kills a process). Route diagnostics in catch blocks through non‑throwing helpers
  (`TryLog`). When in doubt, log *more* lifecycle/state detail, not less.

### What the log has to contain (added 0.3.11)

The file sink runs at **`Debug`**, with `Microsoft`/`System`/`Azure` filtered to `Warning`. Detail
is the point: this is a tray app with no console, reports arrive days later, and the failures that
matter are intermittent and hardware‑specific.

- **Every session opens with a banner** (`SessionBanner`, written from `SessionDiagnostics`):
  session id, pid, version, install channel, package family, OS/arch/runtime, cores/RAM, resolved
  paths, model and whether its files are actually on disk, audio devices, and the hotkey/pipeline/
  cleanup/injection settings. A daily file rolls at midnight, so without this the file a user hands
  over frequently has no record of how the process started. `OnExit` writes the matching
  `session end` line; its absence before the next banner means the process died.
- **Every dictation is stamped `#<n>`** and logs its start (trigger, mode, key, device, target app),
  its stop (**with a reason**: `HotkeyReleased`, `SilenceAutoStop`, `MicrophoneFault`, `Paused`)
  and the hold duration. `DictationController` warns when the captured audio is shorter
  than the hold, because WASAPI ends a stream cleanly with **no exception** when the endpoint is
  reconfigured mid‑capture and nothing else in the pipeline can see it.
- **Retention is bounded and enforced** (`LogRetentionPolicy`): 7 days, 16 MB per day, 64 MB total.
  Swept at startup and at each midnight rollover. Today's file is never swept.
- **Privacy is a contract, not a habit.** No transcripts, dictionary entries, snippet bodies,
  prompts, endpoints or keys. Report shapes instead: counts, enum names, `configured`/`unset`.
  `SessionBannerTests.Banner_never_contains_a_secret` asserts it; keep it passing.
- **Users export logs from Settings > About > "Save diagnostics…"** (`DiagnosticsBundle`), which
  writes the retained logs plus `report.txt` to a zip wherever they choose. Never add `scribe.db`
  to that bundle: it holds every dictation and the saved API keys.

## What the recogniser is NOT (measured, 0.3.11)

A user on 0.3.10 reported that dictation "cut out after seven to ten seconds". Their log showed the
opposite of what that sounds like: **audio captured fine, all 37 seconds of it**, and the recogniser
then returned an **empty string**. Three of their six dictations were lost that way, every capture
over ~13 s failed, and everything under ~11 s decoded. It is tempting to conclude Parakeet cannot
handle long audio. It can. Three hypotheses were tested against the real engine with
`tools/Scribe.AsrCheck`, and **all three were wrong**:

| Hypothesis | Test | Result |
|---|---|---|
| Long single-shot decodes collapse | `--long-audio`, 5 s to 90 s | 13.2-13.9 chars/s at **every** length, including 90 s |
| The channel downmix ruins the signal | `--channel-mix` | silent 2nd channel: **100 %** of baseline; foreign 2nd channel: **95 %** |
| Low SNR or room reverb breaks it | `--degraded`, SNR x duration | **0 dB SNR + heavy reverb at 40 s still decodes at ~13 chars/s** |

So: do not "fix" long-audio decoding, do not rewrite the downmix, and do not assume a noisy room is
the problem. sherpa-onnx does document VAD-segmented decoding for long audio
(`sherpa-onnx-vad-with-offline-asr`) and moving to it would make a collapse cost one segment instead
of the whole dictation, but the measurements above say it is not the cause here.

**The cause is still unknown**, and that is the point: the log could not distinguish the candidates,
because the only thing it said about the audio was "peak audio was present", which means nothing
more than "not digital silence" (a -60 dBFS bar). `CaptureSignalAnalyzer` now records the measurable
shape of every capture (peak/RMS in dBFS, clipping, DC offset, and **per-channel levels taken before
the downmix**) so the next report of this arrives answerable. Statistics only, never audio.

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
  and `Scribe.Overlay.OverlayAnchor` (overlay, which deliberately has no Scribe.Core reference).
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

## Azure authentication (read before touching credentials)

The Microsoft Foundry provider authenticates one of two ways, chosen by
`AppSettings.AiCleanupAzureAuthMode`: the user's **Azure CLI** sign-in (default) or an Entra
**service principal**. `Scribe.Core/Cleanup/AzureCredentialFactory.cs` is the single place that
builds the `TokenCredential`; everything else goes through it.

- **Do not swap in `DefaultAzureCredential`, with or without `Exclude*` options.** This was tried
  and shipped a real bug: `ManagedIdentityCredential` probed a nonexistent IMDS endpoint on a
  desktop and blocked cleanup. Microsoft's own guidance agrees, saying the winning credential in a
  chain "can't be guaranteed ahead of time", that persistent `AZURE_*` variables "apply globally
  and therefore alter the behavior of `DefaultAzureCredential` at runtime in any app running on
  that machine", and that once several `Exclude` flags are set "the advantages of using
  `DefaultAzureCredential` diminish".
- **The credential instance is cached and reused** because Azure.Identity caches tokens per
  instance and Microsoft warns that an app which doesn't reuse them "may encounter HTTP 429
  throttling responses from Microsoft Entra ID". Any change of identity MUST call
  `AzureCredentialInvalidation.Invalidate()`, or the next request authenticates as the old one.
- **Azure CLI token requests are serialized** through `AzureCliProcessCoordinator`: `az` shares one
  token cache, and concurrent processes made it time out on multi-tenant machines. A service
  principal never shells out, so it skips that path.
- **Service principal mode deliberately hides ARM discovery.** Enumerating subscriptions and
  deployments is a control-plane operation needing `Reader` across the subscription, while
  inference only needs a data-plane role on the one resource. Requiring only the smaller grant is
  what makes the feature approvable in a locked-down tenant, so that mode takes the endpoint and
  deployment name by hand. Don't "fix" this by adding discovery.
- **Roles that actually work** (assign by GUID; Microsoft renamed the Foundry ones):
  **`Foundry User`** (`53ca6127-db72-4b80-b1b0-d745d6d5456d`) for a Foundry resource
  (`kind=AIServices`), including project endpoints; `Cognitive Services OpenAI User`
  (`5e0bd9bd-7b93-4f28-af87-19fc36ad61bd`) for a true Azure OpenAI account (`kind=OpenAI`).
- **Do NOT use the `Cognitive Services *` roles on a Foundry resource.** Microsoft states it
  verbatim: "Don't assign built-in roles that start with **Cognitive Services**. These roles are
  designed for accessing AI Services resources directly and don't apply to Foundry scenarios."
  `Cognitive Services User` currently still *works* against a Foundry endpoint, which is exactly why
  this doc previously recommended it. Working is not the same as supported; don't re-derive that
  recommendation from an experiment. Same page also rules out **`Azure AI Developer`** (it targets ML
  workspaces and Foundry hubs). Source:
  <https://learn.microsoft.com/azure/foundry/concepts/rbac-foundry>
- **`Azure AI User` is the old name for `Foundry User`, not a separate role.** The whole family was
  renamed (`Azure AI Owner`→`Foundry Owner`, `Azure AI Account Owner`→`Foundry Account Owner`,
  `Azure AI Project Manager`→`Foundry Project Manager`) with IDs unchanged, so **always assign by
  GUID** while the rename rolls out.
- `Azure AI Inference Deployment Operator` has **zero** dataActions despite the name;
  `Cognitive Services Contributor` can create deployments but not call them; and
  `Foundry Project Manager` cannot deploy models despite one Microsoft scenario table saying it can
  (the per-permission reference wins).
- The role goes on the **account resource** even for a project endpoint; a project is not a separate
  assignment scope for inference.
- **Role propagation outlasts the documented five minutes.** A fresh assignment on a Foundry
  resource took closer to ten before the data plane stopped returning 403. Do not diagnose a 403 as
  the wrong role until the assignment has existed for at least that long; swapping roles during the
  window destroys the evidence about which change worked. This exact trap cost a live debugging
  session, and is why `TextCleanupService.DescribeAzureFailure` now leads with propagation rather
  than "check az login".
- Entra auth requires the resource to have a **custom subdomain**; a regional endpoint rejects the
  token regardless of roles.
- The client secret is DPAPI-encrypted at rest via `DpapiProtectedStringConverter` (same as the API
  keys). **Never** write it to an environment variable, a `.env`, or a script: those are plaintext
  on disk, and persistent `AZURE_CLIENT_*` variables would hijack every other Azure tool on the box.
- User-facing setup lives in `docs/service-principal-setup.md` and is linked from Settings.

## Releases & Velopack (gotchas)

`build/pack.ps1` publishes a self-contained app for each requested architecture (`-Architecture
x64|arm64|all`), bundles the matching overlay self-contained into the payload under `Overlay\`,
packs with Velopack, and (with `-Publish`) uploads to GitHub Releases.
Production artifacts are intentionally unsigned. Packaging must not access a certificate
store, GitHub signing secrets, or a publisher trust bundle.

- The script derives `-Version` from `Directory.Build.props` when omitted and rejects an explicit
  value that does not match `<VersionPrefix>`.
- Installer branding (`--icon`, `--packTitle`, `--packAuthors`) is read from
  `Directory.Build.props` and `src/Scribe.App/Assets/scribe.ico`, so shipped metadata cannot drift
  from the project file. Never hardcode the title, author, or icon path in the pack arguments.
- `vpk` **refuses to pack an equal/greater version that already exists** in `releases\`.
  To repack the same version, delete that version's `*-full.nupkg`, `*-delta.nupkg`,
  `Scribe-win-<arch>-Setup.exe`, `Scribe-win-<arch>-Portable.zip`, and `releases.win-<arch>.json`
  but keep the older `*-full.nupkg`s so the delta can build.
- One Velopack channel per architecture, `win-x64` and `win-arm64`, so an install only ever
  receives updates built for its own silicon. The full nupkg is large (~650 MB, the overlay adds
  ~90 MB self‑contained); the delta is small (~86 MB).
- The release workflow downloads the latest prior stable full nupkg before packing so a clean
  hosted runner can produce the delta package. `pack.ps1` requires the delta whenever a prior
  full package is present.
- To publish without a rebuild, capture the token first (`$t = (gh auth token | Out-String).Trim()`,
  see Environment notes) and run
  `vpk upload github -o releases --channel win-x64 --repoUrl https://github.com/ChrisMcKee1/scribe --publish --releaseName "Scribe <ver>" --tag v<ver> --targetCommitish main --merge --token $t`.
  Repeat per channel; each architecture uploads separately.

### Manual release (GitHub Actions credits exhausted)

The hosted workflow is not always available. To cut a release entirely from this machine:

```powershell
# 1. main must already carry the version bump and the tag
./build/pack.ps1                       # version comes from Directory.Build.props

# 2. publish the release and upload every asset
$t = (gh auth token | Out-String).Trim()
vpk upload github -o releases --channel win-x64 `
    --repoUrl https://github.com/ChrisMcKee1/scribe --publish `
    --releaseName "Scribe <ver>" --tag v<ver> --targetCommitish main --merge --token $t
```

A delta package only builds when the previous version's `*-full.nupkg` is present in `releases\`;
download it from the prior GitHub release first, or the upload ships a full package only.

## Microsoft Store (MSIX, not MSI)

`build/pack-msix.ps1` builds the Store package. **Do not build an MSI**: the Store accepts either
an MSIX or an existing `.exe`/`.msi`, so an MSI would be a third installer to maintain with no
benefit over the Velopack `.exe` we already ship.

MSIX is the chosen path because Microsoft signs and hosts it for free, which removes the
SmartScreen friction the unsigned Velopack installer carries, and because it is the only option
supporting S Mode and Windows 11 backup and restore. See issue #42 for the full comparison.

**Free Microsoft signing is MSIX only.** This is the single most misunderstood point, so do not
re-litigate it from memory:

| Path | Who signs | Cost |
| --- | --- | --- |
| Store, MSIX | Microsoft re-signs after certification | free |
| Store, MSI or EXE | **you must Authenticode-sign before submission**, chaining to a CA in the Microsoft Trusted Root Program; self-signed is rejected | $150-500/yr, or Azure Artifact Signing ~$10/mo |
| Direct download (our GitHub Releases) | you | same as above |

Choosing an MSI therefore *buys* a signing bill rather than avoiding one. Azure Artifact Signing
(~$9.99/month) is the option worth considering for the GitHub Releases channel, which the Store
never covers; note it builds SmartScreen reputation over weeks rather than granting instant trust.

- The script needs `makeappx.exe` from the Windows SDK. It never touches a certificate: Store
  packages are signed by Microsoft after upload.
- Store identity metadata is recorded in `Directory.Build.props`: the technical identity is
  `53984VeteranApps.ScribeAI` / `CN=A4B26056-B631-480C-912C-5EF24F1CBD6B`, the reserved display
  name is `Scribe AI`, and the public publisher is `McKee AI Solutions`. These values must match
  Partner Center exactly; the package family name is derived from the technical identity.
- MSIX versions are four-part and the revision field is reserved for the Store, so it is always
  `<VersionPrefix>.0`.
- Store logos are generated from `docs/icon.png` at build time so the listing artwork can never
  drift from the in-app brand mark.
- Store-installed packages are detected through Windows package identity. `UpdateService` then
  disables the Velopack/GitHub update path, and `StoreUpdateService` takes over the "check for
  updates" button using `Windows.Services.Store`. That check is gated on
  `Package.Current.SignatureKind == Store`, not merely on being packaged: the Store re-signs what it
  publishes, so a sideloaded MSIX carries our own signature and the Store has no record of it.
  Calling those WinRT APIs is why `Scribe.App` targets `net10.0-windows10.0.19041.0`.
- `docs/microsoft-store-submission.md` is the working Partner Center checklist and contains listing
  copy, certification notes, screenshot order, and the remaining pre-submission decisions.

### Submitting to the Store

`.github/workflows/store.yml` builds the MSIX and submits it through the
[Microsoft Store Developer CLI](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/overview).
**It runs automatically after a successful Release**, and can also be dispatched by hand.

The hand-off is a `gh workflow run` call at the end of `release.yml`, **not** an `on: release`
trigger here. That is load-bearing: the release is created with `GITHUB_TOKEN`, and events raised
by `GITHUB_TOKEN` do not start new workflow runs. `workflow_dispatch` and `repository_dispatch` are
the only two exceptions. An `on: release` trigger would read as correct and never fire once.

Still true, and the reason the workflow used to be manual: once a submission is created through the
API it **must not** be edited in Partner Center, or the API can no longer commit it. Pick one path
per release, the workflow or a manual upload, never both. Set the repository **variable**
`STORE_AUTO_SUBMIT` to `false` to go back to manual uploads without touching any YAML.

Repository secrets it needs (Settings > Secrets and variables > Actions):
`STORE_TENANT_ID`, `STORE_SELLER_ID`, `STORE_CLIENT_ID`, `STORE_CLIENT_SECRET`, `STORE_PRODUCT_ID`
(the 12-character Store ID from Partner Center > Product management > Product identity). The first
four come from an Entra app registration associated with the Partner Center account. Two different
Partner Center pages are involved and they are easy to confuse: the tenant is linked under
**Account settings > Tenants**, and the application is added under **Account settings > User
management > Microsoft Entra applications**. The client secret can be generated on either that
Partner Center page ("Add new key") or in the Azure portal under the app registration's
Certificates & secrets. **No PAT is involved.** Until all five exist the workflow
fails fast, before packaging, with the list of what is missing. `release.yml` skips the hand-off
entirely when they are absent and leaves a warning annotation on the run.

Constraints that are easy to trip over:
- The API **cannot** be used on a product that uses **mandatory app updates**; it returns HTTP 409.
- The app must already have **one completed manual submission**, including the age ratings
  questionnaire. Scribe satisfies this.
- MSIX packages may be up to 25 GB, so our package is fine (~650 MB per architecture, ~1.3 GB for
  the combined `.msixbundle` that actually gets uploaded), but the upload uses an Azure
  blob SAS with its own expiry; a very slow upload needs a fresh submission GET rather than a retry.
- **MSIX only.** The `api.store.microsoft.com` submission API documented for MSI/EXE apps does not
  handle MSIX, and the `microsoft/store-submission` action is unmaintained (still `node16`) and has
  no MSIX upload path. Use `microsoft/microsoft-store-apppublisher` as the workflow does.

Both installers are kept on purpose. The Store is the recommended path (Microsoft signs it, so
there is no SmartScreen friction), but Store certification adds latency to a hotfix, a low-level
keyboard hook plus microphone capture is the kind of profile that attracts policy review, and many
managed corporate devices block the Store outright. The direct download is the escape hatch for all
three. Both installs share `%LOCALAPPDATA%\ScribeData`, so a user can move between channels
without losing settings, dictionary, or history.

### AppData write virtualization (this section was wrong until 0.3.11)

Earlier revisions of this file claimed `Environment.GetFolderPath(LocalApplicationData)` "is not
virtualized for a packaged Win32 app". **That is false.** On Windows 10 1903 and later, a folder a
packaged app *creates* under `AppData` is redirected into
`%LOCALAPPDATA%\Packages\<family>\LocalCache\Local\`. Reads come back through a merged view, so the
app sees its own path and everything works, but File Explorer, running outside the container, sees
nothing at `%LOCALAPPDATA%\ScribeData`.

Verified directly against the shipped 0.3.10 Store package:

```powershell
Invoke-CommandInDesktopPackage -PackageFamilyName '53984VeteranApps.ScribeAI_e3jkm6dfkwwbm' `
  -AppId 'Scribe' -Command 'cmd.exe' -Args '/c mkdir "%LOCALAPPDATA%\Probe"'
# %LOCALAPPDATA%\Probe                                     -> does not exist
# %LOCALAPPDATA%\Packages\<family>\LocalCache\Local\Probe   -> exists
```

The cost was a support dead end: a Store user asked for `%LOCALAPPDATA%\ScribeData\logs` correctly
reported that the folder was not there, and the bug behind the request went uninvestigated. It
never reproduced on a dev machine because redirection only applies to **new** folders, and any
machine that has also run the Velopack build already has a real `ScribeData` for the package to
write straight into.

The fix is a `virtualization:ExcludedDirectory` for `$(KnownFolder:LocalAppData)\ScribeData` in
`build/pack-msix.ps1`, which requires the `unvirtualizedResources` restricted capability, plus a
one-time migration in `AppPaths` (`VirtualizedRootDir`) so existing Store users keep their data.
**Do not remove either.**

**Two families of path, and they are not interchangeable.** `AppPaths` exposes `RootDir`/`LogsDir`/
`DatabasePath` alongside `EffectiveRootDir`/`EffectiveLogsDir`/`EffectiveDatabasePath`:

- **Scribe's own file I/O uses the plain ones.** Inside the container the merged view resolves them
  correctly whether or not redirection is on. Pointing internal I/O at the package store would work
  today and break the moment redirection is turned off.
- **Anything handed outside the process uses the `Effective` ones**: the About page text boxes, the
  Copy buttons, `OpenFolder`, and the session banner. Explorer and the clipboard live outside the
  container, so the plain path is the one that reads as "that folder isn't there".

`EffectiveRootDir` comes from an actual **probe** in `EnsureCreated` (write a uniquely named marker
through `RootDir`, look for it at the package-store twin, delete it), not from inference. It has to
be, because the answer differs per machine: redirection applies to folders the app *creates*, so a
PC that has also run the direct-download build already has a real `ScribeData` and is not
redirected, while a Store-only PC is. Both cases verified against the live 0.3.10 package.

## GitHub release automation

`.github\workflows\release.yml` validates that the source version matches the tag and that
the release commit is current `origin/main`. A pushed `v*` tag tests, packages, and publishes;
a manual dispatch retains the generated artifacts without creating a GitHub Release.

For a same-version replacement, remove the old release and tag only after the replacement
commit and local artifacts are ready. Recreate the annotated tag at current `main`, let the
workflow publish it, then verify every remote asset before reinstalling. Existing installations
at that same version will **not** auto-update; they need a manual installer run.

## Branding and icons

The tray icon, window icon, installer, and Add/Remove Programs entry all resolve to one brand
mark. Changing it means changing every one of these together:

- `src/Scribe.App/Assets/scribe.ico` plus the `-recording`, `-processing`, and `-paused` state
  variants, each carrying 16/24/32/48/64/128/256 px frames.
- All four are **embedded resources** (`Scribe.App.Assets.*.ico`) loaded by `Tray/TrayIcons.cs`, so
  an upgrade replaces them atomically with the executable and can never leave stale artwork beside
  the new binary.
- `<ApplicationIcon>` in `Scribe.App.csproj` sets the executable icon, which is what the uninstall
  entry's `DisplayIcon` and every shortcut resolve to.
- `docs/icon.png` is the README mark and the source for the generated Store logos.
- Windows caches shortcut and search artwork aggressively, so `Infrastructure/ShellIconCache.cs`
  calls `SHChangeNotify` after install, update, and restart. Without it an upgraded install keeps
  showing the previous icon until the cache expires.

## Architecture support (x64 and ARM64)

Scribe ships **two architectures from one source tree**. The failure mode of getting this wrong is
invisible at build time: Windows on Arm silently emulates an x64 binary, so a mispackaged build does
not crash, it just runs slower and drains battery. That is why the checks below are enforced
mechanically rather than by review.

- **Every project declares `RuntimeIdentifiers=win-x64;win-arm64`, and none pins `PlatformTarget`.**
  A hardcoded `x64` silently produces an x64 assembly inside an ARM64 publish. Do not add one back.
- **The sherpa-onnx native runtime is architecture-specific and both packages use the same DLL file
  names.** Exactly one may ever be referenced. `Scribe.Core.csproj` computes `ScribeNativeRid` from
  the effective RID and selects one; referencing both drops two different-architecture
  `onnxruntime.dll`s into the same folder. An unsupported RID fails the build with an explicit error
  rather than silently producing a payload with no native engine.
- **`RuntimeIdentifier` is never empty in practice**: the SDK defaults it to
  `NETCoreSdkRuntimeIdentifier` (the host). So a plain `dotnet build` on an ARM64 box is already an
  ARM64 build; that is what makes CI on `windows-11-arm` work with no special casing.
- **The overlay must match the app's architecture.** It is a separate process, so an x64 pill beside
  an ARM64 app runs emulated. WinUI has no AnyCPU story: `Platform` is required (`x64` or `ARM64`)
  and the csproj derives `RuntimeIdentifier` from it so the two can never disagree.
- **`scripts/Payload-Architecture.ps1` asserts payload purity at pack time**, reading the PE COFF
  machine field directly (no `dumpbin`, which needs the C++ workload). Both installers call it.
  Verified working: it accepts a real ARM64 payload and rejects that same payload when claimed as
  x64.
- **`tools/Scribe.AsrCheck` is the only thing that proves the native engine actually decodes.**
  The unit tests deliberately never load sherpa-onnx, so a wrongly-packaged native passes every test
  and fails on the user's first dictation. CI runs it on both architectures against speech generated
  by `scripts/New-SpeechFixtures.ps1`.
- **Fixture phrases avoid numbers, dates and times on purpose.** Scribe's editorial rules correctly
  rewrite "three thirty" as "3.30", which scores as a mismatch and blunts the threshold that is
  meant to catch a broken native.

### NPU: used for cleanup, deliberately not for speech

Two different engines run on this machine and they make opposite choices, so be precise about which
one a question is about.

**Speech decoding (sherpa-onnx / Parakeet) stays on the CPU on every machine**, and that is a
measured decision, not an omission. A Hexagon HTP port of our exact model exists
(`trsdn/parakeet-tdt-0.6b-v3-htp-int8-16s`) and benchmarks at **23-26x realtime for short audio
versus ~25x for CPU INT8 on the same chip**, and no faster for push-to-talk. It only wins on long
audio via chunking. The cost to adopt it would be: encoder only (decoder and mel preprocessing stay
on CPU), a 631 MB context binary on top of what we ship, a fixed 16 s window forcing
chunk-and-stitch, six helper DLLs where a missing one crashes with `STATUS_STACK_BUFFER_OVERRUN`,
and a binary device-gated to Snapdragon X Elite that will not run on other Qualcomm parts without
recompiling through Qualcomm AI Hub. **Do not re-derive "we should use the NPU" from the fact that
one exists.** The real NPU win is power draw, not latency; revisit if a shorter-window encoder lands.

**AI cleanup (Foundry Local) does use the GPU or NPU when one is available**, but Scribe never
chooses: the SDK performs hardware detection and picks the execution provider itself. See the
Foundry Local section below.

`ComputeCapabilityReport` reports process/OS architecture, emulation, and any NPU Windows lists
under the `ComputeAccelerator` device class (`{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}`), which every
vendor registers into. **An empty result is the normal answer on most PCs and is never an error.**

The one genuinely actionable case the report drives is **emulation**: an x64 build on an ARM64 OS
gets a warning in the log and a caution line in Settings, Diagnostics telling the user to install the
Arm64 build.

## Foundry Local (on-device cleanup)

**The SDK owns hardware selection. Do not try to take it back.** Microsoft's architecture reference
is explicit: "The Core API automatically identifies available hardware and chooses the best
execution provider for each model." There is no supported override, so Scribe *reports* the choice
rather than offering one. Supported providers and their device types:

| Execution provider | Device |
| --- | --- |
| NVIDIA CUDA, NvTensorRTRTX | GPU |
| WebGPU (via Dawn), Intel OpenVINO | GPU |
| Qualcomm QNN, AMD Vitis AI | **NPU** |
| CPU | always available as fallback |

**Use `Microsoft.AI.Foundry.Local.WinML`, not the cross-platform package.** Same API surface, but EP
plugins are sourced from the OS and Windows Update with driver compatibility negotiation, which is
what reaches an NPU at all. The cross-platform package also carries Linux and macOS payloads Scribe
can never run. The WinML package requires build 18362 or later, which is why the tree targets
Windows 11; `net10.0-windows` on its own silently means `net10.0-windows7.0` and will not resolve it.

**Read the execution provider from the SDK (`model.Info.Runtime.ExecutionProvider`), never from the
alias text.** `FoundryExecutionProviders` maps a provider to a device type for display. Alias
suffixes only ever spell `cpu` or `gpu`, so inferring from them cannot express an NPU at all. The
alias-shape helpers in `FoundryModelVariant` exist only for the case where a variant has already
failed to load and the SDK's answer is unavailable.

**Curated aliases are family names.** `qwen3-1.7b` resolves at load time to a hardware variant such
as `qwen3-1.7b-generic-gpu:2`. Anything matching on the configured alias will miss every real user,
which is exactly how the first GPU fallback shipped broken.

**The WebGPU shader crash is real and not vendor specific.** A `QuickGelu` / "Failed to create a
WebGPU compute pipeline" failure reproduced on Snapdragon Adreno and on Intel Lunar Lake with
different models. Scribe demotes to the CPU build automatically, on both the shader failure at
inference and the provider-unavailable failure at load, and remembers it.

## Git workflow

- Branch off `main`; keep PRs small and focused. Open an issue first for large changes.
- This checkout is a fork workflow. `origin` is John's fork, `https://github.com/x3nc0n/scribe.git`,
  and is the only remote this working tree should push to. `upstream` is Chris McKee's original
  repo, `https://github.com/ChrisMcKee1/scribe.git`, and is the review and merge target. Never push
  directly to `upstream`.
- John's ongoing split on this repo is: primary maintainer for the native macOS port under
  `macos/Scribe/`, secondary contributor for Windows bug fixes under `src/Scribe.*`,
  `tests/Scribe.Core.Tests/`, and related docs.
- Normal contribution flow: create or update a branch on `origin`, push there, then open a pull
  request from that fork branch to `upstream:main`. To update an existing upstream pull request,
  push more commits to the exact same `origin` branch that the pull request already uses as its
  head branch.
- Current example: upstream PR #61 is `x3nc0n/scribe:main` into `ChrisMcKee1/scribe:main` and
  carries the native macOS port. Because Chris can merge unrelated Windows work into
  `upstream/main` at any time, `origin/main` must periodically catch up from `upstream/main`, then
  be pushed back to `origin/main` so PR #61 stays mergeable.
- Commit message: what changed **and why**. Always append this trailer (per house rule):

```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

- Run build + tests green before committing. `releases/` and `publish/` are gitignored;
  never commit build artifacts or the downloaded models.

## Boundaries

**Always:**
- Keep the **offline‑first promise** intact: the core dictation path must never require a
  network. Online features (Azure/Foundry cleanup) are strictly opt‑in.
- Put new logic in `Scribe.Core` with a test; keep the build warning‑clean.
- Keep all logging non‑throwing and use `FileShare.ReadWrite` + retry on the shared log.
- Build the overlay with `-p:Platform=` matching the architecture you are shipping (`x64` or
  `ARM64`); verify the pill via logs after overlay changes.

**Ask first:**
- Bumping the version, cutting a release, or changing the signing posture.
- Adding/upgrading NuGet dependencies, or anything touching `Directory.Packages.props`.
- Adding a new third‑party component (must be license‑compatible with MIT and credited in
  the README attribution section).
- Schema/migration changes to the SQLite store.

**Never:**
- Commit secrets, API keys, private keys, certificate bundles, or the downloaded models
  (`src/Scribe.App/models`).
- Remove the SQLite pin: `SQLitePCLRaw.bundle_e_sqlite3` is referenced directly to override a
  transitive bundle affected by **CVE-2025-6965**. It must stay at or above 3.0.3, whose native
  `e_sqlite3` is past the 3.50.2 fix. `ScribeDatabase.ExpectedSqliteVersion` asserts the exact
  native version at runtime, so bump that constant deliberately whenever the package moves.
- Reintroduce a WPF transparent/layered‑window pill, or revert the overlay to in‑process;
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
- **`gh` has two accounts:** `chrismckee_microsoft` (an Enterprise Managed User, often active)
  and `ChrisMcKee1` (owns the repo). PR/issue/API calls on the repo fail under the EMU account
  ("As an Enterprise Managed User, you cannot access this content"), though `git push` still
  works. Run `gh auth switch --user ChrisMcKee1` before `gh pr create`/`gh pr merge`/`gh issue
  create`, then switch back to `chrismckee_microsoft` afterward.
- For `vpk upload`/`pack.ps1 -Publish`, capture the token into a variable first
  (`$t = (gh auth token | Out-String).Trim()`) and pass `--token $t`; an inline
  `$env:GITHUB_TOKEN = gh auth token` in the same statement chain has produced an empty token.
- **Merge stacked/dependent PRs one at a time**, confirming each retargeted to `main` before the
  next, because GitHub's base retargeting lags a rapid merge loop and can merge PRs into
  intermediate branches or auto‑close them.
