<div align="center">

<img src="docs/icon.png" alt="Scribe" width="128" height="128" />

# 🎙️ Scribe AI

**You talk three times faster than you type. Scribe closes the gap, privately.**

Hold a key, speak, release. Punctuated text lands at your cursor in any app on Windows 11.
No required cloud. No account. No subscription. No audio ever leaves your PC.

## 🎉 Scribe AI is officially live in the Microsoft Store

Install the trusted, Microsoft-signed release and let the Store keep it updated automatically.

<a href="https://apps.microsoft.com/detail/9N2P0SG059TJ?hl=en-us&amp;gl=US&amp;ocid=pdpshare">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get Scribe AI from Microsoft" width="200" />
</a>

**[Download Scribe AI free from the Microsoft Store](https://apps.microsoft.com/detail/9N2P0SG059TJ?hl=en-us&gl=US&ocid=pdpshare)**

<img src="docs/screenshots/pill.png" alt="The Scribe recording indicator listening, with live level bars" width="420" />

**⚡ ~¼-second response** &nbsp;·&nbsp; **🏎️ recognizes speech ~30× faster than real time** &nbsp;·&nbsp; **🔒 Speech recognition on your PC** &nbsp;·&nbsp; **💸 $0 forever**

</div>

---

If Scribe earns a place in your workflow, click **Star** at the top of this
[GitHub repository](https://github.com/ChrisMcKee1/scribe). It helps other people find private,
offline dictation.

Scribe is a lightweight tray app that turns your voice into text anywhere on Windows: your
editor, browser, chat, command window, notes, email. Dictation apps usually make you choose. The
accurate ones ship your voice to someone else's server and charge monthly for the privilege,
and the private ones type like it's 2009. Scribe refuses the trade: a state-of-the-art speech
model (**NVIDIA Parakeet TDT 0.6b v3**, the same family topping open ASR leaderboards) runs
**entirely on your CPU**, turning a sentence into text in the time it takes to lift your finger off
the key. Measured on a desktop CPU: **~223 ms typical, about 30 times faster than real time**.

## ✨ Why people switch

- **🔒 Private by architecture, not by promise.** Audio is captured and turned into text on your PC, then
  discarded unless you explicitly enable local audio history, which keeps a compact copy for at
  most 7 days and 250 MB.
- **⚡ Two keys, your choice.** Hold **Page Down** (or any key or spare mouse button you pick), talk, release. **Page Up**
  is set up too, for dictation that always skips AI cleanup. No Page Down key on your laptop? Most
  put it on Fn with the Down arrow. Present with a clicker? Its buttons send those same two keys, so
  pick other keys in Settings. Prefer hands-free? Set a shortcut to Press to start and stop, and
  Scribe can stop by itself when you stop talking.
- **🌍 Speaks your language.** The bundled speech model recognizes about 25 European languages out of the
  box, no setup: dictate in English, German, Spanish, French, Italian and more, and it just works.
- **🧠 It understands how people actually talk.** Say *"send it Wednesday… I mean Thursday"* and,
  with AI cleanup on, only Thursday survives. Repeat yourself and it writes the point once.
- **🔢 Numbers, dates and acronyms come out written, not spoken.** "Twenty three licenses at
  three thirty p m on july third" becomes *23 licenses at 3:30 PM on July 3*, the way an editor
  would write it, applied automatically.
- **🎭 Different apps, different voices.** App profiles give Outlook polished prose, Slack a
  casual tone, and your command window one terse line, automatically, based on where your cursor is.
- **⌨️ Command-window smart.** Line breaks become spaces in command windows, such as Terminal, so a
  long dictation arrives as one message instead of firing Enter mid-thought. Built by someone who
  dictates into command-line tools all day.
- **📖 Your vocabulary, your snippets.** A dictionary locks in your jargon (`azure` → `Azure`,
  `dot net` → `.NET`), imports/exports as CSV to share with your team, and even **suggests terms
  from your own dictation history**. Eleven curated word packs cover AI models and terminology, Azure,
  Microsoft 365, GitHub, modern developer tools, .NET and C#, data engineering, data science, machine
  learning, and more, and you can edit them or build your own. Say a trigger phrase and a whole saved
  template types itself.
- **🧹 AI cleanup on your terms.** Grammar and structure cleaned by a model on your PC (fully
  offline), your Azure deployment, **your own GitHub Copilot subscription**, or **any AI service you
  already run that works like the OpenAI API** (Ollama, LM Studio, OpenRouter…). Your models, your
  keys, your costs. Turn it on or off right from the tray.
- **📊 Performance you can verify.** The Diagnostics page shows how long each step takes, computed
  from your own dictations, on your own disk. We don't ask you to take the speed claims on faith.
- **📈 Usage without surveillance.** Track local dictation totals, speech time, active days, top
  apps, a trend chart and recurring terminology, and add uncovered terms to your dictionary with
  one click. The AI summary is a separate explicit action and sends only aggregate totals and
  dictionary word labels to the AI service you set up.
- **🪶 Stays out of the way.** A tray app with a small recording indicator you can place on any
  corner or edge of your screen, and a Windows 11-style settings app when you want to tune it.

## 📸 A quick look

### A settings app that respects you
Everything lives in a clean, Windows 11-style settings window with eleven pages in four groups:
Dictation (microphone, shortcuts, typing, the recording indicator and startup), Try dictation and AI
cleanup; Personalize (Dictionary, Voice snippets, App profiles); Review (History, Usage); and More
(Advanced, Diagnostics, About). **Find a setting** searches every page by name, Settings follows your
Windows text size, and closing it with unsaved changes asks before throwing them away.

![The Dictation page: the microphone, and the two dictation shortcuts with how each one works](docs/screenshots/dictation.png)

### Try it before you trust it
Try dictation records one normal dictation with your shortcut and shows what Scribe heard, what it typed,
each change your dictionary, word packs, snippets and AI cleanup made, and how long every step took.

![Try dictation after a test: what Scribe heard, what it typed, and each change it made](docs/screenshots/try-dictation.png)

### Put the recording indicator exactly where you want it
Click a spot on the position picker under Dictation, Recording indicator, and choose **Preview on
screen** to see the real indicator at that position before you save. It appears the moment you press
your shortcut. With **Stop when I stop talking** on, a shortcut set to Press to start and stop ends
the dictation when you go quiet.

### Say a phrase, type a template
Voice snippets expand a spoken trigger, like *"insert my standup update"*, into a saved,
multi-line template. Text-expander speed, no keyboard required.

![The Voice snippets page: say a short phrase and Scribe types the saved text](docs/screenshots/snippets.png)

### One voice, many registers
Profiles adapt dictation to the app you're speaking into: the AI writing style and line-break
behaviour switch automatically based on the focused window. First matching profile wins; everything
else uses your global settings.

![The App profiles page: a different writing style for email in Outlook](docs/screenshots/profiles.png)

### Clean up your words with AI, on your PC
Turn on **AI cleanup** to have a language model fix punctuation, capitalization, sentence structure,
spoken self-corrections and repeated points *before* the text is typed. By default it runs
**fully offline** on your PC through [Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/).
If the model isn't ready, Scribe types what it heard.

Browsing the model list downloads nothing. Setting up Foundry Local fetches the AI runtime for your
PC, which can be several GB, and loading a model downloads that model. Scribe keeps only the model
you chose, and when you move AI cleanup somewhere else it removes what Foundry Local downloaded,
with a tray notice saying how much space it freed.

![The AI cleanup page: cleanup on, running on this PC with Foundry Local, and a model ready to set up](docs/screenshots/ai-cleanup.png)

### …or bring your own model
Point Scribe at a model you've already deployed in **Microsoft Foundry**: it uses your existing
Azure CLI sign-in (`az login`), discovers your deployments, and lists them in a **browsable dropdown**
(type to filter) so you pick a model instead of remembering deployment names. Live in more than one
tenant? Choose **An app registration (service principal)** instead and Scribe authenticates as
exactly the identity you name, every time ([setup guide](docs/service-principal-setup.md)). Or aim it
at **any other AI service that works like the OpenAI API**: Ollama or LM Studio on localhost, vLLM on
your homelab, OpenRouter, or api.openai.com with your own key. Scribe sends the recognized *text* (never audio) only to the
service **you** configure, together with its cleanup instructions, your writing style and your
dictionary plus the word packs you let AI cleanup use, and it asks Microsoft Foundry not to store the response
(Microsoft's abuse monitoring can still keep a sample of flagged prompts and responses for review, as
its [data privacy page](https://learn.microsoft.com/azure/foundry/responsible-ai/openai/data-privacy)
explains). The [privacy policy](PRIVACY.md#optional-ai-features-and-data-transmission) lists exactly
what each request carries. **Test connection** checks a Microsoft Foundry or other remote setup with the
settings on the page before you save them. And when you want exactly what Scribe heard, **turn AI
cleanup off straight from the tray menu** with no settings trip required.

> **No Foundry resource yet?** [`docs/foundry-setup.md`](docs/foundry-setup.md) walks you through it
> from scratch with a script that creates the resource, project and model deployment in one run.
> Most people can run it for free: a Visual Studio subscription includes monthly Azure credits ($150
> on Enterprise, $50 on Professional), no credit card is needed, and Azure stops rather than billing
> you if the credit ever ran out. Every Microsoft employee has one.

### Teach it your words
Your words, the dictionary, replaces spoken words and phrases with the spelling you actually want, and
gives AI cleanup your preferred spellings as vocabulary. Word packs add curated vocabulary you can
turn on and off, edit word by word, extend with packs of your own, import and export as CSV, and
restore for 30 days after you delete one; your own words always win over a word pack. When AI
cleanup runs anywhere but on your PC, that vocabulary, your dictionary plus the word packs you let AI
cleanup use, up to 5,000 words or phrases, goes with every cleanup request whether or not you said them (a word
whose written text spans more than one line or runs past 100 characters, such as a signature, stays
out), so turn off any word, or keep a word pack out of AI cleanup, if you would rather keep it to
yourself. Build it in seconds: **import a CSV** your team
shares, grab the self-documenting **template**, or let **Learn from history** spot the acronyms
and product names you keep saying and add them for you.

For a quick correction, choose **Add to dictionary** from the tray menu and pick words from a recent
dictation. **Save** adds the rule, refreshes the corrected words, and keeps the window open so you can
keep working through the text. Choose **Save and close** when you're finished.

![The Dictionary page's Your words tab: words Scribe hears and how it writes them](docs/screenshots/dictionary.png)

![The Word packs tab: ready-made lists of product names and terms, with one pack open in the editor](docs/screenshots/word-packs.png)

### Know exactly how fast it is
Diagnostics shows how long speech recognition and AI cleanup take, typically and for 19 in 20
dictations, from your own dictation history. Nothing is collected; it's your data on your disk. On a
typical desktop CPU, the speech model recognizes speech about **30 times faster than real time**.

![The Diagnostics page: save diagnostics for a report, AI cleanup problems, and how fast dictation runs](docs/screenshots/diagnostics.png)

### Everything you said, on your disk
History keeps your recent dictations reviewable and copyable, with per-entry audio if you opt in.
Delete one entry or clear everything; it never leaves your PC either way.

History storage stays bounded on its own: recordings are kept for 7 days and 250 MB at most, stored
as 16-bit audio at about half the earlier size, and history text follows the retention setting.
After upgrading, Scribe compacts an older database once in the background, only while you are not
dictating and Settings is closed, and it stops the moment you start dictating.

![The History page: recent dictations with the app each went to](docs/screenshots/history.png)

### See how dictation fits your work
The Usage section summarizes retained history across 7, 30 or 90 days, or all retained history.
Every metric uses the same selected period. It shows totals, active days, speech time, top apps,
a trend chart and recurring words, and any recurring word your dictionary doesn't cover
yet gets an **Add** button that locks in its spelling on the spot. Opening or refreshing Usage
stays fully local. The optional AI summary sends only aggregate totals and the labels of words
already in your dictionary, leaving out any whose written text spans more than one line or runs past
100 characters. Words found in your dictations but not yet in your dictionary stay on your machine,
and it never sends dictation text, audio, app names or timestamps.

![The Usage page: how much you dictated, and in which apps](docs/screenshots/usage.png)

## 🚀 Getting started

**You'll need:** Windows 11 on Intel, AMD, or Arm (including Copilot+ PCs). That's it. The speech
model is bundled, so there's nothing else to install.

1. Open **[Scribe AI in the Microsoft Store](https://apps.microsoft.com/detail/9N2P0SG059TJ?hl=en-us&gl=US&ocid=pdpshare)**.
2. Select **Install**. Microsoft signs, delivers and updates the app through the Store.
3. Launch Scribe AI. It appears in your **system tray**.

Prefer a portable build, or need the original standalone installer? Advanced users can still visit
the **[GitHub Releases](../../releases/latest)** page. Pick the file matching your PC:
**`Scribe-win-x64-Setup.exe`** for Intel and AMD, or **`Scribe-win-arm64-Setup.exe`** for Arm
(Snapdragon / Copilot+). Portable zips are published for both. Not sure which you have? Open
Settings, System, About and read "System type", or just install from the Store, which picks for you.

> **Windows security prompt for GitHub downloads:** Direct GitHub releases are intentionally
> unsigned, so Windows may show an "Unknown publisher" or SmartScreen warning. The Microsoft Store
> version is signed by Microsoft and is the recommended installation.

Then **hold Page Down, say a sentence, and let go.** The text lands wherever your cursor is. Hold
Page Up instead for dictation that always skips AI cleanup.
Right-click the tray icon for settings, one-click vocabulary learning, copying any of your last
five dictations, and pausing or quitting. If a dictation ever fails to insert, Scribe notifies you
and keeps the text ready to copy from the tray. Review history and local Usage from Settings.

> **Upgrading?** Your shortcuts stay as they were (Right Ctrl, unless you changed it). To switch to
> Page Down and Page Up, choose **Restore default shortcuts** in Settings, Dictation, then Save. While
> they are bound, Page Down and Page Up pressed on their own no longer page through documents in
> other apps, and a presentation remote stops changing slides (except the few presses Scribe lets
> through, described in [Remote Desktop and virtual machines](#remote-desktop-and-virtual-machines));
> with Ctrl, Shift, Alt or Win held they work there as before. Pick any other key or shortcut in
> Settings if you would rather keep them, or if you present.

## 🎛️ How it works

1. **Hold** your shortcut. The recording indicator appears at once and shows it's listening, with
   live level bars.
2. **Speak** naturally. Trim silence removes the silence around your words.
3. **Release.** Scribe recognizes your speech on your CPU, optionally runs AI cleanup (with the app
   profile for the app you're in), applies your dictionary and snippets, and types the result into
   that app. The recording indicator then briefly says what happened: "Typed", or why not, and what
   to do next.

Everything is configurable in Settings: microphone, shortcuts (Press and hold, or Press to start and
stop), Stop when I stop talking, the recording indicator and where it appears, Trim silence, line
breaks, app profiles, voice snippets, Apply your dictionary and snippets, Start with Windows (applied
the moment you switch it), how text is typed into apps, and the space Scribe adds after each
dictation; the tray menu covers the everyday switches.

### Remote Desktop and virtual machines

Dictating into a Remote Desktop, Azure Virtual Desktop, Windows 365, Hyper-V, VMware, VirtualBox
or Citrix window works as it does anywhere else, with a few things Scribe does there. A remote
client can install a keyboard hook of its own, which sees your shortcut before Scribe's
does. So after a remote window comes to the front, Scribe moves its hook ahead of the client's, and
moves it ahead again while the window stays in front: a bound, not a seal. A press in the gap
before a move can still reach the remote session, and so can a press in the moment right after one,
which Scribe cannot tell from the repeat of a key held across the move: Scribe lets that keystroke
through whole, repeats and release included, so a Page Down shortcut pages the session for as
long as you hold it (the dictation still starts and ends). A key you are already holding when
Scribe moves is left alone, even if you change Scribe's shortcut settings meanwhile: its repeats and
its release go where its press went, as long as its next repeat reaches Scribe within the time your
keyboard's repeat settings allow (under a second with the Windows defaults, a second and a half at
most). A key whose repeat comes later than that, or a keystroke a program sends stamped with a time
in the future, is taken as a new press. And if you let go of a key Scribe is letting through where
Scribe cannot see it, on the lock screen for example, its next press goes through once, whole, too.
Text is always typed into a remote window, never pasted, even when you chose Paste the text, because a
remote session reads the clipboard only when it pastes, which can be after Scribe has put back what
you had copied. It is typed in small batches with a short pause between them, never hundreds of
keystrokes at once, which adds about a quarter of a second to a 180-character dictation and about a
second to a 770-character one. And the keys Scribe presses for you (Shift+Enter for a line break)
carry the real key codes remote clients forward.

## 📚 The full feature catalog

**Dictation core**

| Feature | What it does |
|---|---|
| Shortcuts | A dictation shortcut and a shortcut without AI cleanup (hold Page Down and hold Page Up on a new install), each Press and hold or Press to start and stop, on any key, two keys together, or a spare mouse button (middle, back or forward, alone or after a key; any other button through the key your mouse's software sends for it, such as F13), with Change, which pauses dictation while you set a new one, and a one-click restore of the defaults |
| Microphone choice | Follows the Windows default input device (the one Windows Settings shows under Sound, Input) from your next dictation, with no restart; or pick any microphone in Settings or from the tray's Microphone menu. A chosen microphone that is unplugged falls back to the Windows default, and Scribe tells you once |
| Stop when I stop talking | A dictation started with Press to start and stop ends itself when you go quiet, adapting to a quiet microphone and to steady background noise |
| Speech recognition on your PC | Bundled NVIDIA Parakeet TDT 0.6b v3 handles ~25 European languages automatically; optional verified Moonshine Base and Tiny downloads provide fast English-only alternatives |
| Recording indicator | Appears the moment you press your shortcut, with live level bars, and says what each dictation did ("Typed", "Typed without AI cleanup", or "Nothing typed" with the next step); it follows your contrast theme, your Windows animation setting and your Windows text size, and sits at any of 9 places on screen, with an on-screen preview |
| Smart typing into apps | Typed or pasted, with automatic fallback, line breaks turned into spaces in command windows so they never fire Enter, and paced typing, never a paste, into Remote Desktop and virtual machine sessions |
| Space after each dictation | On by default, so back-to-back dictations don't run together: Scribe types one space after your text unless it already ends in white space, such as a space, a tab or a line break. History and the tray's copies keep the text without it; switch it off under Settings, Dictation |
| Shortcut self-healing | Detects and repairs stuck modifiers, restores the keyboard and mouse hooks Windows removes silently, and moves its keyboard hook ahead of a Remote Desktop client's while the client is in front, so your shortcut keeps working across long sessions |

**Text quality**

| Feature | What it does |
|---|---|
| Your words | Your dictionary: spoken-form to replacement rules with whole-word matching, search, CSV import/export, Learn from history, and Clean up unused words |
| Word packs | Eleven curated packs (the two AI packs are on by default), including dedicated .NET and C#, data engineering, and data science and machine learning packs, plus your own and imported packs, all in one A to Z list, with an editor for single words, a per-pack switch for AI cleanup, and Recently deleted for 30 days |
| Voice snippets | A spoken trigger phrase expands into a saved multi-line template |
| App profiles | Writing style and line-break behavior switch automatically based on the app you're in |
| AI cleanup | Optional, through Foundry Local on your PC (fully offline), Microsoft Foundry (your Azure CLI sign-in or an app registration), your own GitHub Copilot subscription through the GitHub Copilot command-line tool, or another AI service that works like the OpenAI API; Test connection before you save, benchmark-validated instructions, your dictionary as vocabulary, and what Scribe heard typed instead if the model misbehaves |
| Try dictation | Captures a normal dictation with your shortcut, then shows what Scribe heard, what it changed (dictionary, word pack and snippet replacements highlighted), and how long each step took |

**Review and recovery**

| Feature | What it does |
|---|---|
| History | Retained dictations with optional audio (kept 7 days and 250 MB at most), copyable and deletable, with older dictations loaded on demand and search across all of them, all local |
| Usage | Totals, speech time, active days, top apps, a trend chart, and recurring words with one-click add to dictionary |
| Dictation recovery | Your last five dictations stay copyable from the tray, and a failed insertion notifies you instead of losing text |
| Diagnostics | How long speech recognition and AI cleanup take, typically and for 19 in 20 dictations, and how many times faster than real time, for the speech model you use, computed from your own history |
| AI summary | Opt-in, explicit, and aggregate-only: sends totals and dictionary word labels, never dictation text, audio, app names, or timestamps |

**App**

| Feature | What it does |
|---|---|
| Find a setting | A search box above the Settings pages finds any setting by name, older names included ("hotkey", "overlay", "library") |
| Safer saving | The footer says when something isn't saved; closing Settings, quitting or restarting to update asks first, and mistakes show next to the field that has them |
| Text size | Settings and the recording indicator follow Windows' Text size setting |
| Tray quick actions | Pause (your shortcut then works normally in other apps until you resume), choose the microphone, AI cleanup on/off, learn from history, copy recent dictations, reopen the welcome tour |
| Start with Windows | Applies the moment you flip it, with nothing to save, and shows what Windows reports, including a choice made in Task Manager or Windows Settings |
| Keyboard and screen readers | Tab follows the visible layout in every window, controls carry screen reader names, and text stays readable on Scribe blue or your Windows accent colour (Settings, Advanced, "Use my Windows accent color"), with Windows contrast themes left as they are |
| Auto-updates | Microsoft Store installs are signed and updated by Microsoft; standalone GitHub installs use Velopack delta updates |
| Offline by architecture | The dictation path needs no network, sends no telemetry, and keeps every stat on your disk |

## 📏 Performance, measured

Numbers below come from the checked-in benchmark reports, reproducible with the commands in each
document. Speech recognition runs on the CPU; your Diagnostics page shows the same figures for your
own hardware.

| Path | Measurement |
|---|---|
| Speech recognition (typical desktop CPU) | **~223 ms** typical, real-time factor **~0.03×** (about 30× faster than the audio itself) |
| 10-second audio aggregation | 69 µs and 625 KB allocated (was 164 µs and 2.6 MB before the 0.2.1 hot-path work) |
| 48k-character cleanup chunking | 30 µs and 191 KB allocated (down 21% time, 49% allocation) |
| AI cleanup, fully offline (`phi-4` via Foundry Local) | ~1.6 s median added time, best quality grade on this PC |
| AI cleanup, cloud default (`gpt-5.4`) | ~1.8 s median added time, grade B+ across the 46-model golden suite |

Details and methodology: the [model leaderboard](docs/model-leaderboard.md) (52 models against
Scribe's real cleanup pipeline with a golden-reference judge), the
[GPT-5.6 phonetic benchmark](docs/gpt56-phonetic-benchmark.md) (sound-alike transcript challenges
and prompt A/B results), and the [local performance benchmark](docs/local-performance-benchmark.md)
(BenchmarkDotNet, production code paths).

## 🔐 Your privacy, precisely

- **Audio never leaves your machine. Ever.** It is captured and turned into text on your PC, then
  discarded unless you explicitly enable local audio history, which keeps a compact copy for at most
  7 days and 250 MB.
- **Speech recognition is 100% local** (Parakeet via [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) on CPU).
- **AI cleanup is optional and yours to control.** Foundry Local, which runs on your PC, is fully
  offline. If you choose Microsoft Foundry, GitHub Copilot or another AI service that works like the
  OpenAI API, each cleanup request sends the recognized *text* of that dictation (never audio),
  Scribe's cleanup instructions with your writing style, and your dictionary plus the word packs you
  let AI cleanup use (up to 5,000 words or phrases),
  whether or not the dictation mentions them. It goes only to the service **you** configure, under
  **your** credentials. Scribe asks Microsoft Foundry not to store the response, though Microsoft's
  abuse monitoring can still keep a sample of flagged prompts and responses for review.
- **Even the stats are local.** Performance and Usage are computed from history already on your disk.
  The Usage page's AI summary runs only when you click it and sends bounded aggregate data without
  dictation text, audio, app names or timestamps.

See the full **[Scribe AI Privacy Policy](PRIVACY.md)** for data storage, optional transmissions,
security, retention, and user controls.

For help, feature requests, or bug reports, use **[GitHub Issues](../../issues/new)**. Do not include
transcripts, audio, credentials, or other sensitive information in a public issue.

## 🛠️ Building from source

**You'll need:** Windows 11 (Intel, AMD, or Arm) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/ChrisMcKee1/scribe.git
cd scribe

# One-time: fetch the speech model (~670 MB). The installer ships with it bundled,
# but the model files are too large to live in git, so source builds download it once.
pwsh ./scripts/Download-Models.ps1

dotnet build Scribe.slnx -c Debug
dotnet run --project src/Scribe.App
```

The build targets whichever architecture you are on, and either machine can cross-build the other
with `-r win-x64` or `-r win-arm64`.

Want to contribute? Everything else you need (project layout, code style, tests, the pull-request
workflow, the AI-cleanup eval harness, and how releases are packed) lives in
**[CONTRIBUTING.md](CONTRIBUTING.md)**.

Preparing a Store release? Use the checked-in
**[Microsoft Store submission checklist](docs/microsoft-store-submission.md)** for Partner Center
answers, listing copy, screenshots, certification notes, and package validation.

## 📄 Licenses & attribution

Scribe is released under the **[MIT License](LICENSE)**.

It stands on the shoulders of excellent open work:

- **Parakeet TDT 0.6b v3**: © NVIDIA, [CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/)
- **Moonshine**: © Useful Sensors, MIT
- **sherpa-onnx**: Apache-2.0 (Next-gen Kaldi / k2-fsa)
- **Silero VAD**: MIT
- **Microsoft Agent Framework** (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.GitHub.Copilot`): © Microsoft, MIT
- **GitHub Copilot SDK** (`GitHub.Copilot.SDK`): © GitHub, MIT

---

<div align="center">
<strong>🎉 Scribe AI is now available free from the Microsoft Store.</strong>
<br /><br />
<a href="https://apps.microsoft.com/detail/9N2P0SG059TJ?hl=en-us&amp;gl=US&amp;ocid=pdpshare">Download Scribe AI</a>
<br /><br />
<sub>Built for people who'd rather talk than type. 🎙️</sub>
</div>
