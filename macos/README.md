# Scribe for macOS

A native Swift menu bar port of [Scribe](../README.md), Windows' offline push-to-talk dictation
app. Built with Swift Package Manager and bundled into a minimal, locally self-signed `.app` by a shell
script. Feature parity with the Windows app is close (see `PORTING-PLAN.md` for the parity table, the
row-by-row checklist and known gaps). The current code passes CI's builds, unit and scenario tests and
sanitizer runs on macOS 15 and 26, with native Intel build/test/package coverage too. Right Option recording
and direct keyboard insertion have been exercised on an Apple Silicon Mac, including Teams. This is not a
complete interactive acceptance pass: full-screen presentation, alternative speech models and the remaining
permission/device combinations still need checks (see Tests below).

## Requirements

- macOS 13 or later
- Apple Silicon (`arm64`)
- Xcode Command Line Tools or Xcode with `swift` available on PATH
- [Foundry Local](https://github.com/microsoft/homebrew-foundrylocal) for on-device ASR and the
  default AI cleanup provider: `brew install microsoft/foundrylocal/foundrylocal`. Use the fully
  qualified name: since Homebrew 6.0, a short name from a third-party tap is refused until the tap is
  trusted (`brew trust`), while installing by the full name trusts that one formula
- Optional: [Ollama](https://ollama.com) as an alternative local AI cleanup provider

## Build

```bash
swift build --package-path macos/Scribe -c release
./macos/Scribe/scripts/build-app.sh release
```

The app bundle is written to:

```text
macos/Scribe/dist/Scribe.app
```

`build-app.sh` provisions or reuses the stable local `Scribe Local Dev` signing identity from
`setup-dev-signing.sh`. That identity must stay stable across rebuilds because macOS keys Accessibility and Input
Monitoring grants to the signing identity as well as the bundle id. If you intentionally delete and recreate the dev
certificate, rebuild and re-grant the permissions once for the new certificate.

## Releasing

The release pipeline targets a notarized direct download through GitHub Releases, not the Mac App Store.
Developer ID signing and notarization still need an end-to-end credentialed release verification.

One-time setup:

1. Create a Developer ID Application certificate in Xcode:
   Xcode > Settings > Accounts > Manage Certificates.
2. Confirm it appears in the default keychain search list:

   ```bash
   security find-identity -v -p codesigning
   ```

   The release script auto-detects a single `Developer ID Application: ... (TEAMID)` identity. If more than one exists,
   set `SCRIBE_SIGN_IDENTITY` to the exact identity string.
3. Store notary credentials:

   ```bash
   xcrun notarytool store-credentials scribe-notary \
     --apple-id "you@example.com" \
     --team-id "TEAMID1234" \
     --password "app-specific-password"
   ```

   Use `SCRIBE_NOTARY_PROFILE` if you store the credentials under a different profile name.

Release command:

```bash
./macos/Scribe/scripts/release.sh release
```

`release.sh` reads the version from `macos/Scribe/VERSION`, builds the app, re-signs it with the Developer ID
Application identity using the hardened runtime and a secure timestamp, notarizes and staples the app, creates the
drag-to-Applications DMG, signs and notarizes the DMG, staples it, and validates Gatekeeper acceptance. The release
entitlement file grants microphone input for the hardened runtime. The app is not sandboxed, so outbound network
access to localhost, Foundry, Ollama, GitHub, or cloud cleanup endpoints does not require a network entitlement.

## Run

From Finder, double-click `macos/Scribe/dist/Scribe.app`, or from Terminal:

```bash
open macos/Scribe/dist/Scribe.app
```

On first launch you'll be asked to grant Microphone, Accessibility and Input Monitoring access (System Settings >
Privacy & Security), and a one-time Welcome window explains the push-to-talk gesture and the privacy/offline promise.

## What works today

- Menu bar app shell (`NSStatusItem`, background-only via `LSUIElement`) with tray items for test
  dictation, Settings, AI Cleanup/Pause toggles, Recent Dictations, Quick Add to Dictionary,
  Welcome, and Quit
- A Microphone submenu refreshes available devices when opened, preserves an unavailable saved choice and links to
  Sound settings.
- Global push-to-talk hotkey, real audio capture, and text injection into the app that had focus when the
  recording started. Scribe types Unicode keyboard events directly, matching Windows' default, without
  changing your clipboard or writing the editor's Accessibility text attributes. Accessibility permission
  is still required for keyboard events and focus checks. If focus moves to another app before or
  while the text is going in, Scribe stops and keeps the dictation for recovery
- The dictation pipeline: raw speech recognition; with AI cleanup on, every replacement decided on that
  transcript exactly as cleanup off would make it, your dictionary and library spellings made in the text sent
  for cleanup (with the app profile's writing style, and a one-line request for a terminal), the reply checked
  against what was sent and its dashes rewritten, and then your snippets and every other dictionary replacement
  made where the reply kept the words that set them off (where the model rewrote, dropped or repeated those
  words, or already wrote the whole replacement around them, as a model that writes the comma of a ", Inc"
  itself does, its words stay). Those others are the template-like replacements: one that is more than one line,
  longer than 100 characters, holds an em or en dash, deletes the words, or has spacing the reply's
  normalization would change (a tab, a run of spaces, a space at either end, or a space before a punctuation
  mark that no letter or number follows). The reply's normalization keeps the space before a mark that begins a
  word, so ".NET" after a word, whether your dictionary or the model wrote it, is not glued to it. With
  cleanup off, snippets and then your dictionary, as on Windows; then line breaks for the target app. A cleanup
  request contains the dictation with your vocabulary corrections applied, never a snippet body or a
  template-like replacement; no rule runs twice, none is matched against the model's text, and a model that
  returns what it was sent gives exactly the cleanup-off text (except that an em or en dash the transcript itself
  held is rewritten with the reply's). If cleanup fails, the text is exactly what it would be with cleanup off. You
  can start the next dictation while the last one is still being processed; the text goes in in the order you spoke
  it
- On-device ASR via Foundry Local's `parakeet-tdt-0.6b-v2`, an English model (`TranscriptionEngine.swift`).
  The recognizer runs off the main thread with a deadline and can be cancelled, and the recording it
  reads is a private temporary file that is deleted as soon as it returns. Long Foundry recordings decode sequentially
  in chunks of at most 30 seconds, with jointly planned quiet seams. Advanced discovers the installed speech-model
  catalog and offers model selection and explicit downloads; cancellation or quit stops and reaps a download.
- AI cleanup supports staged editing of the global writing style and local/detailed guardrails with restore-default
  actions. Microsoft Foundry also accepts a Keychain-backed resource API key, which takes precedence over Entra sign-in.
- Capture that belongs to one recording at a time: every input channel is mixed in, so a microphone on
  any input of an interface is heard; a device change ends the recording and keeps what it captured;
  Right Option (the default key) is held while you talk and never stops on silence. Caps Lock is still available as
  a toggle key: it is tapped on and off and stops only when you tap it again, unless you turn on "Also stop after a
  pause" in Settings > Input (off by default, as on Windows, because a pause to think would end the dictation); the
  tray's test dictation always stops after a pause; other held keys never do; and every recording stops at ten
  minutes, even if the microphone stops delivering. Scribe only listens to Caps Lock and never changes its lock
  state, so a recording starts only when your tap turns the light on, and ends at your next tap. After a dictation
  that ended some other way than your tap (a pause with the setting on, the ten minute limit, a microphone fault,
  Pause Dictation, a change of key or a press Scribe turned away), or if the light was on when Scribe started, the
  light is on with nothing recording: your next tap turns it off and starts nothing, and the tap after it starts a
  new dictation
- Overlay pill with a 9-anchor position picker and live recording/processing state, and a short notice
  that names what went wrong (for example "Cleanup failed, raw text used" or "Not inserted, text kept").
  A notice never covers a recording and never replaces a newer failure; one that cannot be shown waits
  for the pill. Microphone, empty-audio and recognition problems also have plain-language notifications,
  once per problem episode, reset when that stage works again or the saved microphone or shortcut changes.
  A cleanup fallback that cannot be shown at once is posted once until cleanup recovers or its setup changes.
  Every failed insertion still has its own Copy Transcript recovery action; those are never suppressed.
  Known near-silent audio is not sent to the recognizer, and an empty recognition on real audio says
  "No words recognized", rather than disappearing. No modal alerts while you dictate
- If a chosen microphone is unavailable, Scribe says when it uses the system default instead, once until the
  chosen microphone works again or the saved selection changes. A selection that cannot be confirmed says so
  without claiming which microphone recorded. Recent Dictations reports whether copying succeeded; Quick Add
  says "Saved to your dictionary" only once the new rules are in use, otherwise "Saved, but not in use yet".
  Notifications contain no dictated text. Copy Transcript retains only the last five texts in memory and
  Clear history withdraws those copies. See the [notice trigger matrix](PORTING-PLAN.md#tray-and-dictation-notices)
  for the Windows mapping and platform-specific limits
- Releasing the key never waits for the recording to be finished off: that happens in the background,
  and dictations are still processed in the order you spoke them
- Quitting hides the pill at once, then waits for a paste in progress to put your clipboard back, and for a
  running recognizer or a Settings or Usage Insights check that started `az` or `foundry` to be stopped,
  before Scribe exits
- Settings window with Overlay, Input, Dictionary, Word packs, Snippets, App Profiles, AI Cleanup,
  Playground, Diagnostics, Usage Insights, History, and About sections; Find a setting searches the
  sidebar, opens matching pages and scrolls to matching cards; a change made from the tray shows in
  an open window, Open at Login shows what macOS reports, and no tab waits on the database on the
  main thread
- Settings has a persistent unsaved-changes footer. Pending voice snippet and app profile input,
  credential input and word pack edits are kept across page navigation; Save uses their existing
  stores, Discard changes clears pending input, and the window's Close button or Command-W asks
  Save / Discard changes / Keep editing. Keep editing is the default. A failed save stays open,
  keeps uncommitted edits and shows its error in the footer. Settings that already apply immediately
  on macOS, including tray choices, Open at Login, dictation controls, history retention and existing
  dictionary/snippet/profile row actions, stay immediate and are not rolled back by Discard.
  `SettingsWindowController.prepareForApplicationTermination()` reuses the same decisions for quit
  or restart, waits for pending saves/adds, shares an already-open close prompt, and returns false
  on Keep editing, save failure or newer edits. It leaves Settings editable on refusal.
  `AppDelegate.applicationShouldTerminate` awaits that decision before starting shutdown.
- History shows the newest 200 stored dictations, with copy and confirmed per-item delete. Search
  runs asynchronously against every stored dictation's text and recorded app identity before limiting
  the displayed matches to 200, with a visible "first 200 matches, newest first" disclosure.
  Search is debounced; late results cannot replace a newer query, and
  Clear search restores the recent list. Delete and Delete all history refresh the active query and
  invalidate the existing recovery UI through the same history-cleared callback.
- User dictionary (CSV import/export, history-mined suggestions, unused-entry cleanup), Word packs
  (all 11 built-in packs, custom CSV import/export, staged editing with undo, redo, save and discard,
  per-pack AI vocabulary permission), voice snippets, and per-app profiles (writing style + newline
  mode by focused app)
- AI cleanup across Foundry Local (default), Ollama and LM Studio at their own addresses (with a model list
  read from the app), any OpenAI-compatible endpoint, and Microsoft Foundry cloud (Azure CLI or service-principal auth,
  secrets in Keychain, an https resource or pasted Foundry project URL works). Each provider is
  built once per configuration and reused across dictations, Test Connection sends a real
  cleanup request for a test word and passes only if the model answers with text, and em and en
  dashes are rewritten out of the model's answer
- For Ollama and LM Studio on this Mac, each recording checks whether the selected model is resident at the needed
  context size; only a missing or differently sized model gets a fixed local readying request. It contains no dictated
  text or vocabulary, has a bounded wait, and a failure leaves dictation text intact while cleanup is skipped. Cleanup
  failure notifications use plain language and are suppressed until cleanup recovers or its configuration changes.
- `LocalModelLifecycle` coordinates Ollama and LM Studio releases on pause, cleanup off, a provider or model change and
  Free memory, and retires Scribe-loaded LM Studio copies at shutdown.
  Every release waits for readiness, cleanup and Test connection uses in flight (bounded, cancellable), LM Studio copies
  Scribe loaded are tracked and retired by instance id, and a failed unload stays owed. AI cleanup stages the idle time
  for Ollama and LM Studio (10 minutes by default, Never supported); Save applies it, Cancel discards it. A shorter
  nonzero duration (including turning it on from Never) retires the model held under the old retention after active
  uses finish; a newer use withdraws that retirement and sends the new retention itself. Owned-copy countdowns use
  the end of the last use, not the time the setting changed. New requests wait for a model resize or
  unload already in progress. The next LM Studio request retires tracked copies on that server other than the copy
  its requests reach, only while it is the sole use; failures stay owed. Test connection loads at its candidate size
  and key, not the saved settings; a refused load, unreadable residency or concurrent use fails that test before a
  completion is sent. A copy loaded by hand is tested as it is and never replaced for the test. Only an instance id
  returned by LM Studio establishes ownership, never an invented id from the model name.
  A copy loaded solely for an unsaved Test connection candidate is retired by its instance id after that check,
  including a load that lands after cancellation. It is kept when those settings became the saved configuration.
  Saving or rotating its key does not change which local copy those settings use. Retirement tries the key saved for
  that same server now, even with cleanup off, then the key used to load the copy. A settings or key change during
  the Keychain read refuses that reading; an unreadable key is logged by shape and the original key can still be tried.
  Management loads/unloads go to the configured address once, never raced across
  loopback aliases. A refused idle/pause unload retries on the same lifecycle task after 30 seconds, doubling
  to at most five minutes; every retry rechecks the original revision and rereads the destination's current key.
  A newer use, resume or changed idle duration withdraws that task. Idle still unloads only tracked instance ids,
  never another app's model. Other failed releases stay owed until a later existing reconciliation/release.
  Foundry Local cleanup and speech-memory release remain open work. Test connection waits
  180 s for a recognized local app (Ollama, LM Studio), 90 s for other custom endpoints.
- A chosen Ollama context size uses its native `/api/chat` route, for managed Ollama as well as Ollama selected under
  another AI service. Dictation, Test connection and the fixed readying request carry the same captured `num_ctx`,
  `num_predict`, `think: false` and retention, plus any key saved for that address. With the app's own size selected,
  the existing Chat Completions route stays. Readiness checks the configured `/v1` app address and a held copy's context, not an unrelated
  default address or a different-size copy. Test connection uses its candidate size without saving it.
  Before a chosen-size Ollama request is sent, Scribe conservatively estimates the full prompt, the actual wrapped
  transcript, the output limit, chat-template room and margin. A request that does not fit fails without being sent;
  dictation then keeps its recognized text. Managed Ollama gets bounded output and glossary planning even without
  a selected local-app row. With a size chosen, every native request first reads that model's maximum through
  `/api/show` at the configured address, with its model name and saved key but no dictated text or instructions.
  The smaller of that limit and the choice decides both `num_ctx` and the fit check, even below the size picker's
  minimum. Missing metadata refuses the text request with a reason rather than guessing. Readiness recognizes a
  model held at a previously learned cap instead of repeatedly starting it at an impossible size.
  After a native answer, Scribe reads `/api/ps` at that same address with no body, only its saved key if any.
  A smaller loaded size stays as a ceiling for that provider's later requests and its readiness checks. An answer
  whose request did not fit the reported loaded size, or whose size cannot be confirmed, is refused and dictation
  keeps the recognized text. The first request can still meet an unknown runtime cap, and another app can change
  the shared model between requests; a residency reading cannot close that race.
  These are estimates, not tokenizer counts; oversized local requests split at whitespace as described below.
  Ollama's own-size requests keep Chat Completions but are fitted before sending to the smaller of 4,096 tokens
  and the copy's reported size. A larger copy may belong to another app and cannot vouch for Ollama's default
  after Scribe's request. A cold model receives only the fixed one-token readying request before its size is
  read again; missing or insufficient size refuses user content. Every actual completion has an enforced output
  ceiling and a fresh loaded-size reading after its answer; an unconfirmed or insufficient size refuses the answer.
  A missing output ceiling reserves 4,096 tokens, which cannot fit beside the instructions in this conservative
  default budget, so it is refused. Dictation and auxiliary callers must supply a fitting ceiling.
  Ollama may have a default below the assumed 4,096, and other apps can change the shared copy between readings:
  the post-answer refusal cannot undo text already sent. Choosing a context size gives Scribe a firmer budget.
  A chosen LM Studio size is checked against the matching copy's reported loaded context after reconciliation,
  before a completion sends text. A smaller manual or busy copy is used only when the full request fits it; a
  missing size or unreadable residency refuses the text request with a reason. A requested size alone never
  authorizes the send. Another app can still change that shared copy between the reading and the completion.
  LM Studio's own-size mode uses this same actual-copy guard. With nothing held, a fixed one-token "ok" request
  readies the model first; no dictated text, vocabulary or user instructions go in that request. Only a confirmed
  loaded size permits the actual completion. An omitted output ceiling becomes 4,096 tokens and is sent as a
  ceiling, not merely reserved in the estimate. Test connection never retries context-bound requests without
  an output limit or claims it did. This may refuse strict servers or thinking models that need that retry;
  unlimited output cannot be safely fitted into a bounded context.
  Dictation now fits mentioned glossary terms to the assumed local context even when the whole-vocabulary switch
  is off. Instructions use the same conservative rate as the send guard, including the glossary separator.
  The answer budget accounts for non-spaced text as well as words. Usage summaries on recognized local apps ask
  for at most 1,024 output tokens instead of an unspecified ceiling; a payload that still cannot fit is refused.
  Remote summary requests are unchanged. Smaller actual contexts remain protected by the transport guard, but
  discovering a cold model's limit before planning remains open.
  Local-app providers now give dictation a fresh conservative planning limit from the matching loaded copy,
  never larger than the selected or assumed context. Its glossary is reduced before the transport's final fit
  check; mentioned terms keep priority. An unreadable state or a held copy without a size refuses cleanup.
  No held copy uses only the configured estimate while fixed readying and the final transport guard confirm it.
  This hint cannot authorize a send or exclude a shared-model replacement. The live dictation adapter now
  binds planning, every actual send/retry and its reply to one saved-settings revision: an A to B to A change
  while reading context sends no user content, and a change after delivery discards the obsolete answer.
  Usage summaries and dictionary suggestions also use that local planning hint to lower their answer ceiling,
  without dropping any input or enlarging their requested limit. At least 512 output tokens must fit beside the
  full instructions, sample and safety margin; otherwise nothing is sent and the existing error/fallback applies.
  Unknown or remote services keep their existing requests. Final transport checks still refuse a context that
  became smaller after planning; this does not make shared-model changes atomic.
  Oversized local dictations now split sequentially at whitespace only when the full request cannot fit. Each
  segment reserves its own bounded answer, and all receive the same glossary, fitted to the smallest remaining
  room. Vocabulary and template decisions still happen once on the original dictation. Each reply and then the
  joined reply must pass the response guard; any failure uses the entire recognized dictation, never a cleaned
  prefix. Shutdown inserts nothing. Boundary whitespace is preserved; an oversized token with no safe whitespace
  boundary or instructions leaving no room is refused rather than split inside a word. Remote requests remain
  whole. A shared model can still change after planning and cause the final transport check to refuse.
  Segments share one 30-second answer budget instead of multiplying the normal answer wait; each later request
  gets only the remaining time, and an answer arriving after the budget is refused. Separate local-management
  checks retain their own bounds, so this is not an absolute wall-clock guarantee.
  Dictation planning and a saved-settings Test connection use the instructions and writing style from their settings
  store, not a separate live defaults read. A nonempty app-profile style still takes precedence.
  Ollama/LM Studio tuning is bound to that app's recognized local address. A stale app selection beside a different
  address sends neither whole-vocabulary tuning nor app-specific request fields. Literal loopback IPs are parsed as
  IPs: domains such as `127.example.com` are remote, not a server on this Mac.
- Dictionary's **Suggest with AI** asks before sending a bounded raw sample from the latest Try dictation report and
  suggestion instructions. It never reads saved history or sends expanded snippets/templates. Consent is tied to the
  saved cleanup configuration's revision: changing away and back still requires consent again. Every request/retry
  shares the settings/send admission boundary, and a stale reply is discarded. Only reviewed, selected words are added.
- Diagnostics (P50/P95 decode latency, real-time factor) and Usage Insights (totals, trend chart,
  top apps, recurring terms with one-click dictionary add, and an opt-in AI summary that sends only your
  totals and the recurring terms that are dictionary spellings: never a word mined from your dictations,
  and never a template-like replacement)
- Dictation recovery: last 5 transcripts survive both the current run and an app restart (seeded
  from persisted history), in a Recent Dictations submenu that fills itself as it opens, plus a
  notification with Copy Transcript for a dictation that did not go in. After Clear history neither
  an entry already on show nor an earlier notification copies the deleted text
- Startup problems (a database that could not be read, missing Input Monitoring or Accessibility) are
  reported once, in a notification that opens the right System Settings pane. Scribe tries the
  push-to-talk key again whenever it becomes active or its menu opens, so granting Input Monitoring may
  take effect without a relaunch; that has not been checked on a real Mac yet, so if the key still does
  nothing, quit and reopen Scribe
- Dictation history written in the background after the text is delivered, in dictation order. It is
  best-effort until committed: a crash in that moment loses the entry. A new install keeps 90 days of
  text, a history from an earlier build keeps everything until a limit is chosen, and a missing or
  unreadable setting never deletes anything. Retention is swept at launch and daily, and freed space is
  reclaimed only while no dictation is running. Deleted text is written over with zeros rather than left in
  the database's free space, and after a history deletion (Clear History, the retention sweep), after space is
  reclaimed, and after a dictionary entry, snippet or app profile is deleted or an entry is changed, Scribe
  checkpoints the database's write-ahead log and truncates it, so the old text is gone from both files once
  that checkpoint succeeds; a dictation, a Clear History still running or another reader of the database can
  hold it off, and it is retried. One still owed when Scribe quits or crashes is made at the next launch: the
  first maintenance pass of each launch, about 30 seconds after Scribe starts or sooner, checkpoints too.
  Text deleted by an earlier build, before this was set, can remain in free pages until they are reused or
  reclaimed.
  Settings > History chooses the limit (7, 30, 90 days, 1 year or Forever) and clears all history after a
  confirmation, which also empties Recent Dictations and the Playground's last dictation and closes an open
  Quick Add window
- Scribe removes only what it made: the private recording it hands the recognizer, as soon as the recognizer
  returns, and any a crash left behind, at the next launch. It never deletes Foundry Local's or Ollama's model
  caches, which you installed and which other apps share
- Foundry Local cleanup accepts only an HTTP or HTTPS address on literal loopback or `localhost`, without embedded
  credentials, query or fragment. It bypasses proxies and refuses redirects, so a service response cannot move the
  cleanup request to another destination. A refused address sends no text and reports why; the network session keeps
  no cookies or response cache. An intentionally remote server belongs under Another AI service instead.

## Tests

```bash
swift test --package-path macos/Scribe --parallel
```

runs both test targets, each test in a worker process of its own; CI runs them the same way on macOS 15 and 26
and under the thread and address sanitizers, fails on any compiler warning, and builds the app bundle, lints its
Info.plist, verifies its signature and runs its library listing from inside it.

- `Tests/ScribeTests`: the unit tests. Every suite uses a defaults suite, Keychain service, temporary directory
  and pasteboard of its own, so no test reads or changes your settings or credentials.
- `Tests/ScribeScenarioTests`: headless scenarios on the committed speech fixtures in `tests/fixtures/speech`
  (the phrases of `fixtures.json` and `scenario-fixtures.json`, which the Windows scenario suite also uses). They
  play the fixtures through the real capture engine on scripted devices at 16, 44.1 and 48 kHz, mono and stereo,
  with the voice on either channel; run silence auto-stop on room tone, steady noise, a quiet microphone and speech
  followed by silence; drive the whole dictation pipeline with a stand-in recognizer that answers with each
  fixture's text; time a compile of the rules with every built-in library switched on; and read and sweep 50,000
  rows of history. They need no microphone, recognizer, permission or network. They find the fixtures from their
  own source path; `SCRIBE_FIXTURES_DIR` points them at another copy,
  and they are skipped when there are none. Their timings and levels are printed, and written to the folder
  `SCRIBE_SCENARIO_REPORT_DIR` names when it is set.
- With Foundry Local and its model installed,
  `SCRIBE_REAL_ASR=1 swift test --package-path macos/Scribe --filter RealRecognizerScenarioTests` transcribes the
  short fixtures, the repeated long fixture and six degraded/stereo captures with the real recognizer. Each
  asserted phrase is held to Windows' 0.6 word-overlap bar. Degraded cases include seeded white noise at 10 and
  0 dB SNR, early reflections, reflections with 10 dB noise, 44.1 kHz left-channel speech and 48 kHz right-channel
  speech with the other channel's floor. They all pass through production capture/resampling before decoding.
  On this Mac's cached Parakeet v2, all six scored 1.0 word overlap; that is a controlled fixture result, not a
  promise for real microphones, competing speech or every room. The noise SNR and reflection delays/gains have
  deterministic tests even when real-ASR is off. No new model is downloaded by these added tests. The
  workflow's optional real speech recognition job does the same on a hosted runner, only when it is dispatched by
  hand; it fits one, with the model taking about 700 MB of disk and Foundry Local about 1.1 GB of memory.
- The opt-in real-ASR suite also sweeps exactly 5, 20, 45 and 90 seconds of repeated committed speech, clean,
  with 10 dB white noise, and with early reflections plus 0 dB white noise. It pins contiguous complete chunk
  coverage and the 30-second maximum, then checks both unique-word overlap (at least 0.6) and repeated-word
  retention (at least 0.8). The second check cannot pass on just the first repetition. On cached Parakeet v2
  here, all twelve cases had 1.0 unique overlap and 0.90 to 1.0 retention, including four chunks at 90 seconds.
  These are controlled repeated-phrase measurements, not real-room validation.
- Varied real-ASR coverage joins four different committed passages into 75.78 seconds, clean and with 10 dB
  white noise: three production chunks retained 1.0 and 0.995 of expected word occurrences here, with every
  passage at 1.0 unique-word overlap. Stereo competing-voice cases put a second fixture 20 dB below the primary
  voice on the other channel, on both sides. Production capture equaled the exact arithmetic downmix, and the
  primary voice scored 1.0 overlap and retention on both. This tests a quieter competing voice, not equal-level
  speakers, speaker separation, arbitrary background media or real microphones.
- Foundry startup cancellation regressions fire the recording-readiness deadline while a load is held and
  cancel a direct completion queued behind another load. Both send no text, cancel/remove the waiting work,
  and allow the next admitted completion to use the lane. These are scripted cancellation checks, not proof
  that a daemon load already accepted by Foundry is undone or that Scribe owns the model it loaded.
- Long Foundry recordings now omit chunks whose every sample is exactly zero, including negative zero.
  Previously a zero-only chunk's empty recognizer answer discarded speech from the rest of the recording.
  A real fixture with two passages separated by 65 seconds of zeros reproduced that failure; both passages
  now retain every expected word. Any nonzero sample still reaches recognition, even the smallest normal
  float. A wholly zero long recording reports no text, not success. Speech-model VAD and noisy-silence
  detection remain open; this fix does not guess that quiet audio contains no speech.
- A refused automatic release after shortening local-model retention now retries after 30 seconds, doubling
  to a five-minute ceiling, like idle and pause releases. The original use revision and retention generation
  still govern every attempt: a new use, Never or a longer retention withdraws it. Each retry goes through
  the existing drain/commit barrier and destination-scoped saved-key lookup. Free memory stays a one-shot
  action that reports failure; this does not add automatic retirement of Foundry's shared models.
- Shutdown permanently stops the local lifecycle's automatic-release scheduler, cancels its pending task
  and advances its generation before the bounded final release. Neither a pending retry, a later lease end
  nor a retention change can rearm automatic unloads afterward. An unload already committed still follows
  the existing barrier and bound; shutdown does not retract a request already sent to the local app.
- The same shutdown state is checked at every release commit. A late configuration-change, candidate,
  Free memory or automatic release is refused as no longer wanted, even when it started waiting for
  uses before shutdown. The final shutdown release remains allowed; already-committed unloads are unchanged.
- Shutdown also closes local-model use admission, including a use already waiting behind an unload.
  An empty cache still closes its lifecycle. Recognized-local cleanup providers report this as cancellation,
  not a fictitious timeout, without reaching transport. LM Studio reconciliation checks closing before its
  read and at its unload/load commit, so a read spanning shutdown cannot start a new model change.
 - Cleanup-cache shutdown also withdraws admitted dictation and one-off requests, plus saved and candidate
  connection tests. Lifetime closure and HTTP task resume share the existing settings/send boundary:
  later attempts send nothing and late successful replies are refused. Foundry planning resumed after
  closure cannot start a model load. This does not retract requests or daemon work already started.
  Independent caches keep independent lifetimes, without changing saved settings.
  Recording readiness after closure reports cancellation, not "not applicable"; a Foundry residency
  read spanning closure cannot proceed to a load.
  Cancelling recording readiness also seals its displayed state and awaited result: a backend's
  late successful answer cannot turn it back into "Starting local model" or permit cleanup.
 - An LM Studio load that finishes after shutdown's wait still records its instance, ends its
  change barrier and extended use, then makes one separately bounded shutdown retirement attempt.
  It unloads only tracked instance ids, never an ordinary shared model. A refusal or timeout keeps
  ownership recorded and starts no automatic retry. If the process exits before settlement, the
  daemon can still keep the copy; this is best-effort cleanup, not a guarantee of released memory.
  Replacement-load admission is checked atomically when its extended lease is taken. If shutdown
  begins while a wrong-size copy is unloading, reconciliation starts no replacement load.
  A request already cancelled cannot take a local-model lease or withdraw an owed idle retirement.
  Cancelled releases stop before trying another key or instance. Their barrier always ends,
  confirmed frees are retained, and unconfirmed copies stay recorded for a later release.
  LM Studio reconciliation refuses cancelled reads before touching ownership or committing a change.
  A late empty residency answer after cancellation cannot erase an instance still owed for retirement.
- Foundry Local cleanup now reads its selected chat model's supported `model info` metadata for context planning
  and again before each send, including an endpoint-refresh retry.
  Recording readiness first fits its fixed, one-token readiness request to that capacity; an unknown
  or insufficient capacity starts no residency work or load. This is a minimal readiness check,
  not a promise that the later dictation or its app-profile instructions will fit.
  The reported capacity only lowers a conservative
  4,096-token ceiling. Missing, invalid or mismatched capacity refuses cleanup explicitly, preserving ordinary
  dictation; no user content is sent by the metadata command. Full instructions, text, bounded output, template and
  margin must fit before sending. Local chunk/glossary and auxiliary-output planning apply to Foundry too, and Test
  connection cannot retry without an output limit. This is catalog-capacity checking, not proof of the active runtime's
  context or atomic ownership. Older CLIs without usable metadata cannot serve cleanup until upgraded.
  `SCRIBE_REAL_FOUNDRY_CLEANUP=1 swift test --package-path macos/Scribe --filter FoundryLocalCleanupProviderTests`
  checks a synthetic bounded completion only when the default `qwen2.5-1.5b` is already cached.
- Refused Test connection candidate-copy retirements retry after 30 seconds, doubling up to five minutes,
  even with idle release set to Never. Each candidate has one retry task, and each attempt rechecks whether
  its configuration became saved, drains active uses and unloads only that candidate's tracked instance ids.
  Keys are reread for the matching server. Shutdown cancels these retries permanently; an already committed
  unload cannot be retracted. Refusal remains best effort while Scribe is running, not a promise after exit.
- Live dictation forwards cancellation to its detached provider lookup. An already cancelled lookup reads
  no secret, and a secret read completing after cancellation returns no provider. A synchronous Keychain
  consent/read still cannot be interrupted; waiting ends only once that read returns.
- Foundry cleanup readiness now checks the runtime's loaded chat list and, if absent, loads only the exact variant
  that `model info` positively identifies as cached. No download or unload command is used. Recording startup shares
  the existing 30-second readiness bound and shows Starting local model; direct completions also wait for confirmed
  residency within that bound. Loads share a lane and every completed load is reread before text is sent. Changed-back
  saved settings withdraw readiness, and cancellation stops/reaps the CLI child. A load already accepted by the shared
  daemon may still finish after cancellation; Scribe claims no ownership or retirement for it.
  `SCRIBE_REAL_FOUNDRY_COLD=1 swift test --package-path macos/Scribe --filter FoundryLocalResidencyTests`
  exercises cached Qwen2.5 0.5B readiness and a synthetic completion. It passed from a cold cached model on this Mac.
- Foundry direct completion now checks request fit before inspecting/loading residency, then checks capacity again
  immediately before sending. Unknown capacity or oversized instructions/text therefore load no cold model.
  Endpoint-refresh retries repeat both preflight and residency confirmation. The load CLI must return explicit
  boolean success, followed by a matching loaded-model reading. Local readiness captures its connection and
  saved-settings revision in one admission, avoiding separately captured configuration/revision state.

The runners cannot grant Microphone, Accessibility or Input Monitoring access and have no screen to look at, so the
event tap, a real microphone, insertion into real apps and the menu bar and overlay UI still need a real Mac.

## Formatting

`macos/Scribe/.swift-format` is the style: four-space indentation and 120 columns. Format with the Swift 6.1
toolchain's formatter (Xcode 16.4), which CI's style job lints with, strictly:

```bash
swift format format --in-place --recursive --configuration macos/Scribe/.swift-format \
    macos/Scribe/Sources macos/Scribe/Tests
swift format lint --strict --recursive --configuration macos/Scribe/.swift-format \
    macos/Scribe/Sources macos/Scribe/Tests
```

## Known gaps vs. Windows

See `PORTING-PLAN.md`, "Remaining parity gaps, checked against current source", for evidence, the smallest
implementation surface and any dependency, runtime or credential blocker. Confirmed gaps include VAD trimming,
multilingual and bundled ASR, chunking long recordings, GitHub Copilot cleanup,
automatic updates, Intel validation, and full real-ASR scenario coverage. Settings also lacks a separate indicator
preview and visibility toggle, global writing-style and advanced-prompt editing, Azure resource API-key auth, and
speech-model/thread controls, an editable idle memory-release duration, and a tray microphone picker. Mouse-button
shortcuts are not implemented; confirm that product-scope decision before treating them as platform-inapplicable.
Shortcut input is one key, with Caps Lock as the only toggle, and is deliberately listen-only. Idle and pause release
for local cleanup models remains incomplete.

The macOS port deliberately keeps its distinct cleanup pipeline and privacy choices; those differences are documented
separately and are not treated as missing features. The separate parity table also distinguishes those choices from
stale historical rows. Build signing and release notarization still need real Developer ID credentials for
end-to-end verification.
