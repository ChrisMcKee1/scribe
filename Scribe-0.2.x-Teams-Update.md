# 🎙️ Scribe 0.2.1 to 0.2.3: What's New

Three releases since 0.2.0, focused on knowing how you dictate, never losing a word, and making the push-to-talk key bulletproof. Grab the latest from the [Releases page](https://github.com/ChrisMcKee1/scribe/releases/latest); existing installs update automatically.

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
