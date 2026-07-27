# 🎙️ Scribe 0.2.1 to 0.2.15: What's New

A dozen releases since 0.2.0, focused on knowing how you dictate, never losing a word, making the push-to-talk key bulletproof, and getting cloud AI cleanup to authenticate as the identity you actually meant. Grab the latest from the [Releases page](https://github.com/ChrisMcKee1/scribe/releases/latest); existing installs update automatically.

---

## Scribe 0.2.15 (July 27)

### 🔑 Sign in with a service principal
If you live in more than one Entra tenant, the Azure CLI's single active account was a coin flip: Scribe would authenticate as whichever tenant `az login` happened to be pointed at, and you would get `AADSTS700016` with no clue why. AI cleanup settings now offer an explicit **sign-in method**: your Azure CLI account as before, or an **Entra service principal** pinned by tenant, client ID, and client secret. The same identity, every time.

- The secret is encrypted on your PC with Windows DPAPI and is **never** written to an environment variable, a `.env`, or a script. Those sit in plaintext on disk, and a persistent `AZURE_CLIENT_*` variable would quietly change how every other Azure tool on your machine picks its credentials.
- **Verify service principal** requests a real token, so you find out whether the identity works before you save, not on your next dictation.
- Service principal mode asks for **less access, not more**. Browsing your subscriptions needs Reader across the whole subscription, while calling a model needs only one role on the one resource. That mode skips discovery and takes the endpoint and deployment name directly, so what you have to ask an admin for stays small.
- A full [setup guide](https://github.com/ChrisMcKee1/scribe/blob/main/docs/service-principal-setup.md) covers the app registration, the secret, and the exact role to grant, linked straight from Settings.

### ↩️ Line breaks no longer vanish
Unicode typing, the default insertion method, sent line breaks as raw control characters, and most text boxes throw those away. The result was two sentences fused with **no space at all**: `first line.second line`. It hid in plain sight because the terminal flattening modes cover most cases, but not VS Code's integrated terminal or the Keep newlines setting. Line breaks are now real Return keypresses.

### 🩺 Errors that tell you what to do
When a service principal is rejected, Scribe now says which thing is wrong instead of listing everything it could be. The one that bites everybody: Entra rejects a **freshly created secret** for a minute or two with "Invalid client secret provided", which reads like you mistyped it. Scribe now tells you to wait and try again before it suggests anything else, and the reason lands in the log rather than being swallowed.

---

## Scribe 0.2.14 (July 26)

### 🫧 The overlay stopped freezing
The recording pill could stick on "Transcribing…" forever after the first dictation. Dictation itself kept working, which made it look cosmetic, but the cause was real: the tray icon code handed out shared icon objects that Windows disposed after first use, and the resulting exception aborted the state update before it ever reached the overlay. Icons are now created per assignment, and a tray failure can no longer take the pill down with it.

---

## Scribe 0.2.13 (July 26)

### 🎨 A real brand mark
The tray, window, installer, and Add or Remove Programs entry all now use the Scribe icon, including distinct **recording**, **processing**, and **paused** states. The icons ship embedded in the executable, so an update can never leave stale artwork behind, and Windows' icon cache is refreshed on install so you see the new mark immediately.

### 📊 AI cleanup timings in Diagnostics
The diagnostics panel now reports AI cleanup latency alongside decode latency, which is how we learned cleanup costs roughly seven times more than transcription. The decode threads slider also finally shows its scale instead of an unlabeled track.

### 📖 Bigger dictionary libraries
More built-in vocabulary, so more terms come out spelled right without you teaching them.

---

## Scribe 0.2.8 to 0.2.12 (July 19 to 24)

### ☁️ Azure sign-in that tells the truth
A run of fixes to Microsoft Foundry authentication and the settings that drive it:

- Scribe pins authentication to **Azure CLI** explicitly rather than letting Azure's credential chain wander off and probe a managed identity endpoint that does not exist on a desktop. That probe was the actual cause of cleanup silently failing.
- Concurrent Azure CLI calls no longer trip over each other's token cache, which had been causing intermittent timeouts on multi-tenant machines.
- **Model and subscription dropdowns stay hidden until sign-in is verified.** Previously they rendered regardless, which implied AI cleanup was working when it was not.
- Cleanup readiness in Settings refreshes when it changes, instead of showing a stale verdict.

---

## Scribe 0.2.4 to 0.2.7 (July 13 to 18)

### ⌨️ Two hotkeys, and a choice of ear
A second **dictation only** hotkey always skips AI cleanup while keeping your dictionary, snippets, and profiles. Alongside it, **selectable offline speech models**: keep multilingual Parakeet, or pick a smaller English-only Moonshine model.

### 🧪 Playground
A full-pipeline diagnostic view showing raw recognition, every dictionary, library, and snippet replacement highlighted, and per-step timings, so you can see exactly where your text changed and what it cost.

### 🎤 A muted microphone now says so
Capturing against a muted device used to fail silently. Scribe now surfaces it.

### 🔎 Subscription filter for model discovery
Narrow Azure model discovery to one subscription instead of browsing every one your account can reach.

---

## Scribe 0.2.3 (July 12)

### ⌨️ Hotkey fixes you will feel
- **Rebinding your hotkey finally just works.** While the capture box is armed, Scribe's global keyboard hook passes every key straight through: your current push-to-talk key can be part of the new chord, and pressing it can no longer start a recording mid-setup.
- **No more stuck Ctrl.** Windows enforces a hard deadline on low-level keyboard hooks, and during a long hold a key event could slip past Scribe and leave Ctrl logically stuck down system-wide. Scribe now detects exactly that state after every release and injects the missing key-up automatically.
- **Push-to-talk can no longer silently die.** Windows removes a hook that misses its deadline without telling the app. Scribe now probes hook liveness every 30 seconds and reinstalls it if it went missing, and a reinstall that interrupts a recording stops the recording cleanly.

### 🛟 Dictation recovery
- The tray menu keeps your **last five dictations** under "Copy recent dictation"; click any entry to copy the full text.
- If a dictation fails to insert into the focused app, Scribe **notifies you immediately** and keeps the text ready to copy from the tray. Nothing you said is lost.

### 📈 Usage insights, sharper
- New **trend bar chart**, and correct period labels (weekly buckets now read "Week of ...", fixing 90-day views that looked like single days).
- Recurring terms your dictionary does not cover yet get a one-click **Add** button that locks in their spelling and feeds the AI cleanup glossary.
- **Privacy tightened:** the optional AI insight sends only aggregate totals and term labels already in your dictionary. Terms mined from your dictations never leave the machine.
- Term analysis is much faster: one scan per history entry instead of hundreds of regex passes.

### 🔧 Reliability
- A single-character dictionary entry no longer blanks the Usage page.
- The engine's log now rotates at midnight instead of staying pinned to its launch day, keeping the app and overlay on one interleaved timeline.
- New deterministic offline evals cover the usage-insight and dictionary-suggestion AI prompts.

---

## Scribe 0.2.2 (July 11)

### 📊 Usage without surveillance
A new **Usage** section in Settings shows your dictation totals, words, speech time, active days, top apps, trends, and recurring terminology across 7, 30, or 90 days, or all retained history. Everything is computed locally from your own device; opening the page never uploads anything. An optional, explicit AI insight button can summarize the aggregates using the cleanup model you already configured.

### 🛟 Copy last dictation
A new tray action recovers your most recent finalized dictation to the clipboard, including when insertion into the focused app failed.

### 📖 Modern Developer Stack library
A new opt-in dictionary library with roughly 100 current developer terms: Supabase, Cloudflare, Vercel, Next.js, Tailwind CSS, Drizzle ORM, OpenTelemetry, and friends, so they come out spelled right the first time.

---

## Scribe 0.2.1 (July 10)

### 🗂️ History lives in Settings
The separate history window is gone; dictation history is now a first-class Settings section, with **one-click vocabulary learning straight from the tray**.

### ⚡ Faster hot paths
Measured improvements to the audio capture and AI cleanup pipelines, plus new local performance benchmark tooling so the speed claims stay verifiable on your own machine.

### 🧪 Benchmark-validated prompts
A focused GPT-5.6 phonetic cleanup benchmark (11 audio-backed cases, including sound-alike transcript challenges) confirmed the shipped cleanup prompts remain the measured optimum; two tuning candidates were rejected by the regression gate.

---

**Privacy, always:** audio is captured, transcribed in memory on your CPU, and discarded. Nothing is uploaded. AI cleanup remains strictly opt-in and sends transcribed text only, to the endpoint you choose.
