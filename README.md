<div align="center">

<img src="docs/icon.png" alt="Scribe" width="128" height="128" />

# 🎙️ Scribe

**You talk three times faster than you type. Scribe closes the gap — privately.**

Hold a key, speak, release — polished text lands at your cursor in any app on Windows 11.
No cloud. No account. No subscription. No audio ever leaves your PC.

<img src="docs/screenshots/pill.png" alt="The Scribe recording pill listening, with a live level meter" width="420" />

**⚡ ~¼-second response** &nbsp;·&nbsp; **🏎️ transcribes ~30× faster than realtime** &nbsp;·&nbsp; **🔒 100% on-device** &nbsp;·&nbsp; **💸 $0 forever**

</div>

---

Scribe is a lightweight tray app that turns your voice into text anywhere on Windows — your
editor, browser, chat, terminal, notes, email. Dictation apps usually make you choose: the
accurate ones ship your voice to someone else's server and charge monthly for the privilege,
and the private ones type like it's 2009. Scribe refuses the trade: a state-of-the-art speech
model — **NVIDIA Parakeet TDT 0.6b v3**, the same family topping open ASR leaderboards — runs
**entirely on your CPU**, decoding a sentence in the time it takes to lift your finger off the
key. Measured on a desktop CPU: **~223 ms typical decode, real-time factor ~0.03×**.

## ✨ Why people switch

- **🔒 Private by architecture, not by promise** — audio is captured, transcribed in memory, and
  discarded on your machine. There is no server to trust, because there is no server.
- **⚡ One key, zero friction** — hold **Right Ctrl** (or any key), talk, release. That's the whole
  gesture. Prefer hands-free? Toggle mode ends the dictation by itself when you stop talking.
- **🧠 It understands how people actually talk** — say *"send it Wednesday… I mean Thursday"* and,
  with AI cleanup on, only Thursday survives. Repeat yourself and it writes the point once.
- **🔢 Numbers, dates and acronyms come out written, not spoken** — "twenty three licenses at
  three thirty p m on july third" becomes *23 licenses at 3:30 PM on July 3* — the way an editor
  would write it, applied automatically.
- **🎭 Different apps, different voices** — per-app profiles give Outlook polished prose, Slack a
  casual tone, and your terminal one terse line, automatically, based on where your cursor is.
- **⌨️ Terminal-smart** — line breaks become spaces in terminals so a long dictation arrives as one
  message instead of firing Enter mid-thought. Built by someone who dictates into CLIs all day.
- **📖 Your vocabulary, your snippets** — a dictionary locks in your jargon (`azure` → `Azure`,
  `dot net` → `.NET`), imports/exports as CSV to share with your team, and even **suggests terms
  from your own dictation history**. Say a trigger phrase and a whole saved template types itself.
- **🧹 AI polish on your terms** — grammar and structure cleaned by an on-device model (fully
  offline), your Azure deployment, or **any OpenAI-compatible server you already run** (Ollama,
  LM Studio, OpenRouter…). Your models, your keys, your costs — flip it on or off right from the
  tray.
- **📊 Performance you can verify** — a built-in diagnostics panel computes latency percentiles
  from your own dictations, on your own disk. We don't ask you to take the speed claims on faith.
- **🪶 Stays out of the way** — a tray app with a small glass recording pill you can place on any
  corner or edge of your screen, and a Windows 11-style settings app when you want to tune it.

## 📸 A quick look

### A settings app that respects you
Everything lives in a clean, Windows 11-style settings window: pick your microphone and
push-to-talk key (hold or toggle), then browse focused sections for dictation behaviour, the
overlay, AI cleanup, your dictionary, snippets, per-app profiles and diagnostics.

![Scribe general settings — microphone, hotkey and startup, with the navigation rail](docs/screenshots/settings-general.png)

### Put the pill exactly where you want it
Click a spot on the mini screen and **preview the real pill** at that position before you save.
Optional silence auto-stop ends a toggle dictation when you go quiet.

![Scribe overlay settings — position picker with on-screen preview](docs/screenshots/overlay.png)

### Say a phrase, type a template
Voice snippets expand a spoken trigger — like *"insert my standup update"* — into a saved,
multi-line template. Text-expander speed, no keyboard required.

![Scribe snippets — a trigger phrase expands to a saved template](docs/screenshots/snippets.png)

### One voice, many registers
Profiles adapt dictation to the app you're speaking into: the AI writing style and line-break
behaviour switch automatically based on the focused window. First matching profile wins; everything
else uses your global settings.

![Scribe profiles — per-app writing style and line-break overrides](docs/screenshots/profiles.png)

### Polish your words with AI — on your PC
Turn on **AI cleanup** to have a language model fix punctuation, capitalization, sentence structure,
spoken self-corrections and repeated points *before* the text is inserted. The default provider runs
**fully offline** through [Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/).
If the model isn't ready, dictation just continues with the raw transcript.

![Scribe AI cleanup with Foundry Local — on-device model](docs/screenshots/ai-foundry-local.png)

### …or bring your own model
Point Scribe at a model you've already deployed in **Microsoft Foundry** — it signs in with your
existing `az login`, discovers your deployments, and lists them in a **browsable dropdown** (type
to filter) so you pick a model instead of remembering deployment names. Or aim it at **any
OpenAI-compatible endpoint**: Ollama or LM Studio on localhost, vLLM on your homelab, OpenRouter,
or api.openai.com with your own key. Only the transcribed *text* is ever sent — never audio — and
only to the endpoint **you** configure. And when you want the raw transcript, **toggle AI cleanup
straight from the tray menu** — no settings trip required.

### Teach it your words
The dictionary replaces spoken words and phrases with the spelling you actually want, and feeds the
AI cleanup a glossary of your preferred vocabulary. Build it in seconds: **import a CSV** your team
shares, grab the self-documenting **template**, or let **Suggest from history** spot the acronyms
and product names you keep saying and add them for you.

![Scribe dictionary editor — spoken-to-replacement rules](docs/screenshots/dictionary.png)

### Know exactly how fast it is
The Diagnostics section computes latency percentiles from your own dictation history — nothing is
collected, it's your data on your disk. On a typical desktop CPU, Parakeet decodes at a real-time
factor around **0.03×** — that's ~30× faster than the audio itself.

![Scribe diagnostics — decode latency P50/P95 and real-time factor from local history](docs/screenshots/diagnostics.png)

## 🚀 Getting started

**You'll need:** Windows 11 (x64). That's it — the speech model is bundled, so there's nothing else
to install.

1. Go to the **[Releases](../../releases/latest)** page.
2. Download **`Scribe-win-x64-Setup.exe`** (the installer). It installs Scribe and keeps it up to
   date automatically. Prefer not to install? Grab **`Scribe-win-x64-Portable.zip`** and run it from
   any folder instead.
3. Run the installer and launch Scribe — it appears in your **system tray**.

> **Heads up:** current builds are **unsigned**, so Windows SmartScreen may warn you on first run.
> Click **More info → Run anyway** to continue.

Then **hold Right Ctrl, say a sentence, and let go.** The text lands wherever your cursor is.
Right-click the tray icon for settings, history, and to pause or quit.

## 🎛️ How it works

1. **Hold** your push-to-talk key — the glass pill shows it's listening, with a live level meter.
2. **Speak** naturally. Voice-activity detection trims the silence around your words.
3. **Release** — Scribe transcribes on your CPU, optionally polishes with AI (using the profile for
   the app you're in), applies your dictionary and snippets, and types the result into the focused
   app.

Everything is configurable from the tray: microphone, hotkey (hold or toggle), silence auto-stop,
the pill and where it appears, voice-activity detection, line-break handling, per-app profiles,
snippets, post-processing, start-with-Windows, and how text is inserted.

## 🔐 Your privacy, precisely

- **Audio never leaves your machine — ever.** It is captured, transcribed in memory, and dropped.
- **Transcription is 100% local** (Parakeet via [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) on CPU).
- **AI cleanup is optional and yours to control.** The on-device provider (Foundry Local) is fully
  offline. If you choose Azure or a custom endpoint, only the *transcribed text* (never audio) is
  sent to the server **you** configure, under **your** credentials.
- **Even the stats are local.** The performance panel is computed from history already on your disk.

## 🛠️ For contributors

Scribe is open source and contributions are welcome — see **[CONTRIBUTING.md](CONTRIBUTING.md)**.

### Building & running from source

Want to hack on Scribe? Build it locally — it takes a couple of minutes.

**You'll need:** Windows 11 (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# 1. Download the speech model (~670 MB) into src/Scribe.App/models
pwsh ./scripts/Download-Models.ps1

# 2. Build
dotnet build Scribe.slnx -c Debug

# 3. Run — Scribe appears in your system tray
dotnet run --project src/Scribe.App
```

### Repository layout

```
Scribe.slnx
  src/Scribe.Core            services + domain: audio capture, transcription, VAD, post-processing,
                             snippets, profiles, text injection, hotkeys, persistence, AI cleanup
  src/Scribe.App             WPF tray app: bootstrap + DI, settings window, dictation loop
  src/Scribe.Overlay         the recording pill: a standalone WinUI 3 process driven over a named
                             pipe (DWM-composited transparency — see AGENTS.md for the why)
  tests/Scribe.Core.Tests    unit tests (post-processor, snippets, profiles, stats, cleanup prompt…)
  tools/Scribe.Evals         offline style/format eval harness for AI cleanup (model comparison)
  scripts/Download-Models.ps1  fetches the ASR + VAD models into src/Scribe.App/models (gitignored)
  build/pack.ps1             builds the Velopack installer + GitHub-release updates
  Directory.Build.props        shared versioning + package metadata (semver lives here)
  Directory.Packages.props     central NuGet version management
```

The optional AI cleanup is built on the **Microsoft Agent Framework** (`AIAgent`), so the on-device
(Foundry Local), cloud (Microsoft Foundry) and bring-your-own (OpenAI-compatible) providers share
one code path and are easy to extend.

### Evaluating cleanup quality

`tools/Scribe.Evals` is an offline harness that drives the real cleanup service across a suite of
writing-style prompts — pirate, Old English, French translation, bulleted to-do, plus **semantic
condensation scenarios** (spoken self-corrections, redundancy merging) that the shipped default
style must handle — then scores each output with a deterministic
[`Microsoft.Extensions.AI.Evaluation`](https://learn.microsoft.com/dotnet/ai/evaluation/) `IEvaluator`.
It proves a prompt change actually changes the output and lets you compare models head-to-head — with
no judge model and no network. The eval packages are referenced `PrivateAssets="all"`, so they never
ship with the app.

```powershell
# Score the default on-device model
dotnet run --project tools/Scribe.Evals

# Compare two Foundry Local models
dotnet run --project tools/Scribe.Evals -- --models qwen3-1.7b,phi-3.5-mini
```

### Releases & updates

`build/pack.ps1` publishes a self-contained `win-x64` build, packs it with Velopack (installer + delta
updates), and can upload the result to GitHub Releases. Code signing is opt-in via **Azure Trusted
Signing** (`-AzureTrustedSignFile`) or a local certificate (`-SignToolParams`); the app then
auto-updates from the matching release channel. Versioning is semantic and lives in
`Directory.Build.props`.

### Security note

`Microsoft.Data.Sqlite` transitively references a SQLite build affected by **CVE-2025-6965**. Scribe
pins `SQLitePCLRaw.bundle_e_sqlite3` `3.0.3` directly to pull the patched native (`e_sqlite3` 3.50.3),
overriding the vulnerable transitive version.

## 📄 Licenses & attribution

Scribe is released under the **[MIT License](LICENSE)**.

It stands on the shoulders of excellent open work:

- **Parakeet TDT 0.6b v3** — © NVIDIA, [CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/)
- **sherpa-onnx** — Apache-2.0 (Next-gen Kaldi / k2-fsa)
- **Silero VAD** — MIT

---

<div align="center">
<sub>Built for people who'd rather talk than type. 🎙️</sub>
</div>
