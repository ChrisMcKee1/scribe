# Scribe 0.5.0

Scribe 0.5.0 rebuilds Settings from the ground up, turns libraries into word packs you can edit, and
gives Scribe a new look of its own. It also lets a spare mouse button be your dictation shortcut, makes
dictating into Remote Desktop and virtual machines safer, and gives the recording indicator a new look
that tells you what each dictation did and appears the moment you press your shortcut. Everything you
set up in 0.4.4 carries over.

## What changes when you update

- **Settings looks different, and every setting is still there.** Eleven pages in four groups replace
  the old thirteen tabs, and "Where settings moved" below lists where each one went. Nothing was
  removed.
- **Libraries are now called word packs,** on the Dictionary page's Word packs tab, and your
  dictionary is its Your words tab. The packs you had on stay on.
- **Scribe uses its own blue** in Settings, Add to dictionary, the welcome and the tray menu. To keep
  your Windows accent color instead, turn on Settings, Advanced, "Use my Windows accent color".
  Contrast themes are unchanged.
- **Your shortcuts stay as they are.** Nothing about the mouse changes until you bind a mouse button:
  with keys only, Scribe doesn't watch the mouse.
- **The recording indicator looks different, and tells you how each dictation went.** It's solid and
  dark now, with five level bars while you speak, and when a dictation ends it briefly says "Typed" or
  tells you why not.
- **Closing Settings with unsaved changes asks first,** and so do quitting Scribe and restarting to
  update.
- **Text going into a Remote Desktop or virtual machine window is typed in small batches,** never
  pasted, which adds about a quarter of a second to a 180-character dictation there. Everywhere else,
  typing is unchanged.

## Settings, rebuilt

- **Eleven pages in four groups:** Dictation, Try dictation and AI cleanup; Personalize (Dictionary,
  Voice snippets, App profiles); Review (History, Usage); More (Advanced, Diagnostics, About). The
  everyday settings are on the page you'd look for them, and the ones almost nobody changes are on
  Advanced.
- **Find a setting.** A search box above the pages finds any setting by name, and older words still
  work, such as "hotkey", "overlay" and "library".
- **Try dictation** replaces the Playground: speak as you normally would and see what Scribe heard,
  what it changed and how long each step took.
- **Safer saving.** The footer says when something isn't saved. Closing Settings, quitting Scribe or
  restarting to update asks before throwing unsaved changes away. Shortening how long history is
  kept, or clearing a dictionary replacement, asks first, and mistakes are shown next to the field
  that has them.
- **Settings follows Windows text size** (Settings, Accessibility, Text size in Windows), and so does
  the recording indicator.

## Word packs and Your words

- **Word packs** replace libraries, with a full editor: turn packs and single words on and off, edit
  how a word is written, add your own packs, import and export CSV files, and restore a deleted pack
  from Recently deleted for 30 days. Your own words always win over a word pack.
- **Your words** is your dictionary, with search, Learn from history, and Clean up unused words.
- Each dictation takes one snapshot of your words and word packs when it starts and uses it for both
  AI cleanup and the replacements, so a change you save during a dictation applies from the next one.

## AI cleanup

- **One page with a clear choice of where AI cleanup runs:** on this PC with Foundry Local, Microsoft
  Foundry, GitHub Copilot, or another AI service.
- **Test connection** checks Microsoft Foundry or another AI service with the settings on the page,
  before you save them. It sends the same short check Scribe makes when AI cleanup starts: your
  cleanup instructions and writing style, and no dictation and no word from your dictionary or word
  packs.
- A plain summary on the page says what each request sends.

## Tray, notices and the recording indicator

- **The tray menu and its notices are rewritten in plain words,** and each problem is told once: on
  the recording indicator while it's on screen, otherwise as a notice. A dictation that couldn't be
  typed always gets a notice with a way to copy it.
- **The recording indicator says how each dictation went,** then fades away:
  - "Recognizing speech…", or "Running AI cleanup…" when AI cleanup is on, while Scribe works. 0.4.4
    said "Transcribing…" and "AI polishing…".
  - "Typed", with a check, for a moment, when all of your text went in.
  - "Typed without AI cleanup" when AI cleanup was on but failed or wasn't ready. Your text went in as
    it was recognized, and Settings, AI cleanup says why.
  - "Nothing typed" or "Not all of it was typed", with what to do next, such as "Check your
    microphone" or "Copy it from the tray menu".
  - If Scribe heard no speech, the indicator just goes away.
- **A new dictation always wins.** Press your shortcut again and the indicator starts listening at
  once; a late word about the previous dictation never covers a new recording.
- **It appears as soon as you start dictating.** In 0.4.4, Scribe closed the indicator's helper after a
  while without a dictation (the wait set in Free memory when Scribe isn't used, 10 minutes unless you
  changed it) and started it again on your next dictation. So the indicator could appear a second or
  two late, several seconds late on a busy PC, and rarely not at all. Now the helper stays ready while
  the recording indicator is on, and after that wait it gives most of its memory back to Windows
  instead of closing. With the indicator off it closes as before. Pausing dictation still closes it,
  and resuming starts it again.
- **It follows Windows.** In a contrast theme it uses your theme's colors. With animation effects off,
  its dots stand still and it appears and goes without fading, and with a larger Windows text size it
  grows to match, so its words still fit.

## History, Usage and Diagnostics

- **History** can load older dictations and search all of them, not only the latest 200. Deleting
  dictations from History also removes them from the tray's recent dictations and from Add to
  dictionary.
- **Diagnostics** shows how long speech recognition and AI cleanup take with the speech model you use,
  and it now holds Save diagnostics, the logs folder and where Scribe keeps your data.

## A new look

- Scribe's own blue in Settings, Add to dictionary, the welcome and the tray menu, with clearer
  contrast in light, dark and contrast themes.
- New tray icons: recording shows a lit blue tile, processing three dots and paused two bars, sharp at
  every display scale.
- Scribe's own icon in its title bars, on About and on the welcome.
- Links in Settings stay blue when you point at them, instead of turning red.

## Mouse buttons

- **Middle, Back and Forward bind directly.** In Settings, Dictation, choose Change next to a shortcut
  and press the button with the pointer on the Settings window: the middle button (on most mice,
  pressing the wheel), or the Back or Forward side button. Either shortcut can use one, whether you
  press and hold or press to start and stop, on its own or after a key, such as Ctrl then Back; for a
  key and a button together, press the key first. Left and right clicks can't be used.
- **Every other mouse button binds through the key it sends.** Windows passes only five mouse buttons
  to apps: left, right, middle, Back and Forward. For any other button, set it to a key such as F13 in
  your mouse's software, then choose Change and press the button. F13 to F24, media and browser keys,
  and shortcuts with Ctrl, Alt or Shift all bind this way, and a button that already sends such a key
  binds as it is. A key such as F13 on its own works best: with a shortcut, Windows still sees its Ctrl,
  Alt and Shift keys, and Settings warns you when a Ctrl+Shift or Alt+Shift shortcut could switch your
  keyboard language or layout.
- **A shortcut can hold more.** Besides one or two keys, Change now records the middle, Back and
  Forward mouse buttons, and a key or button held with Ctrl, Alt or Shift, including more than one of
  them, such as Ctrl+Shift+F13. Before, it stopped at two keys.
- **A button bound on its own no longer does its usual job in other apps,** so a bound Back button
  stops going back in your browser while Scribe runs. Pressed with Ctrl, Shift, Alt, Win or the
  Narrator key, it still does its usual job, and so does every button while dictation is paused from
  the tray. In a shortcut such as Ctrl then Back, only that shortcut is Scribe's: Back on its own
  still goes back.
- **If Windows briefly stops passing input to Scribe** (it does this to any app that answers too
  slowly), a click made or held while that lasts reaches the app under the pointer. Scribe reconnects,
  normally within 30 seconds, and ends a dictation a mouse button shortcut started. If you were holding
  a bound button across that time, letting go of it can do its usual job once, which for Back or
  Forward is one step back or forward. That never leaves a button held down in Windows.
- **Scribe watches the mouse only while one of your shortcuts uses a mouse button,** because while it
  does, Windows passes every pointer move through Scribe. After you remove such a shortcut, Scribe
  keeps watching only if it is still waiting to see that button let go (you were holding it, say), and
  then only until it next sees that button pressed or let go.
- Scribe never sends mouse input of its own: it never presses or releases a mouse button for you.
- If you go back to an earlier version, a mouse button shortcut shows its name there but does nothing;
  choose a key again. A key or shortcut such as F13 or Ctrl+Shift+F13 keeps working there.

## Remote Desktop and virtual machines

- **Dictating into a Remote Desktop, Azure Virtual Desktop, Windows 365, Hyper-V, VMware, VirtualBox
  or Citrix window is safer.** A remote client can install a keyboard hook of its own that sees your
  shortcut before Scribe does. After such a window comes to the front, Scribe now moves its
  own hook ahead of the client's, and again while the window stays in front. A press in the moment
  before a move, or right after one, can still reach the remote session, and it goes there whole, its
  repeats and its release included: a Page Down shortcut pages the session for as long as it is
  held. A key you are already holding when Scribe moves is left alone, so its repeats and its release
  go where its press went, as long as its next repeat comes within the time the keyboard's repeat
  settings allow (under a second with the Windows defaults); a later repeat, or a keystroke a program
  sends stamped with a time in the future, is taken as a new press.
- **Text going into those windows is always typed, never pasted,** even with "Paste the text" chosen: a
  remote session reads the clipboard only when it pastes, which can be after Scribe has put back what
  you had copied.
- **It is typed in small batches with a short pause between them** instead of bursts of up to 100
  keystrokes, which adds about a quarter of a second to a 180-character dictation and about a second
  to a 770-character one.
- The keys Scribe presses for you (Shift+Enter for a line break, and the release of a key it finds
  stuck) now carry real key codes, which Remote Desktop and virtual machine clients forward.
- Scribe's once-every-30-seconds keyboard check no longer travels past its own hook into other apps
  or a remote session.

## Where settings moved

| In 0.4.4 | In 0.5.0 |
| --- | --- |
| General > Microphone | Dictation > Microphone |
| General > Dictation with AI cleanup, Dictation only | Dictation > Shortcuts ("Dictation shortcut", "Shortcut without AI cleanup") |
| General > default hotkeys | Dictation > Shortcuts > Restore default shortcuts |
| Dictation > End dictation on silence (toggle mode) | Dictation > Shortcuts > Stop when I stop talking |
| Dictation > Add a space after each dictation | Dictation > Typing |
| Overlay > Show the recording overlay, position | Dictation > Recording indicator |
| General > Start with Windows | Dictation > Startup |
| General > Keep audio with history, Keep dictation history for | History > History settings |
| General > Speech recognition model, Decode threads | Advanced > Speech recognition ("Speech model", "Processor threads") |
| Dictation > Release speech models when idle | Advanced > Speech recognition ("Free memory when Scribe isn't used") |
| Dictation > Voice activity detection, Maximum dictation length | Advanced > Recording ("Trim silence", "Longest recording") |
| Dictation > Post-processing | Advanced > Text changes ("Apply your dictionary and snippets") |
| Dictation > How text gets inserted, Line breaks, Don't send chat messages early | Advanced > Typing into apps |
| General > Updates | About |
| AI cleanup > Provider | AI cleanup > Where AI cleanup runs |
| AI cleanup > Cleanup prompt, Edit the prompts | AI cleanup > Advanced AI settings |
| Dictionary > Import CSV, Export CSV, Get template | Dictionary > Your words > More |
| Libraries | Dictionary > Word packs |
| Playground | Try dictation |
| Diagnostics > Performance | Diagnostics > Speed |
| About > Where your data is stored, logs, Save diagnostics | Diagnostics |

Nothing was removed except the Libraries page's tip line.

## Fixes

- The recording indicator no longer activates itself when it first appears after Scribe starts.
- Settings no longer mistakes certain dictionary and snippet edits for no change. In 0.4.4, an edit
  that only moved a vertical bar (|) between a dictionary entry's spoken and written forms, or between
  a voice snippet's phrase and its text, could be skipped by Save without a word.
- Pressing Enter in a Settings field no longer saves the whole window: in 0.4.4 Save was the window's
  default button.
- The Diagnostics page scrolls with the mouse wheel over its list of AI cleanup failures.
- The recording indicator no longer stays away when its helper is slow to start on a busy PC. 0.4.4
  gave up after 8 seconds, sometimes just as the helper was about to show; Scribe now waits up to 30
  seconds.
- Every message now uses the names the Settings pages use, in the tray, the notices, the AI cleanup
  status and the recording indicator: a shortcut, not a hotkey or chord; word packs, not libraries;
  another AI service, not an OpenAI-compatible endpoint.

## Under the hood

- No dependency changes: the speech engine, the AI libraries and the Windows App SDK are the same
  builds as in 0.4.4.
- The shortcut self-healing that releases a key Windows still thinks is held covers keys only. It
  starts no release while you record a new shortcut with Change, and immediately before each release
  it checks that nothing has reset Scribe's view of your keys since the release was asked for. After
  such a reset it waits for your shortcut's next release instead of guessing.
- The log says whether each shortcut uses a key, a mouse button or both, and when Scribe's mouse hook
  is added, removed or found gone. It records no pointer movement and no other clicks. It also says
  whether a dictation went into a remote client, how its typing was paced, whether a paste was typed
  instead, and how many line breaks and special characters it held (never the text), and which
  outcome the recording indicator showed.
- The log also records how long the microphone took to start for each dictation, how long the
  recording indicator's helper took to start, and when its memory was given back to Windows. Numbers
  only.

## Known limitations

- At a large Windows text size (225%) in a small Settings window, the Your words and History tables
  scroll sideways or shorten long entries; widening the window shows them whole.
- Word pack changes are written to disk through a journal so that a crash can't leave a pack half
  saved. That was tested on the direct download; the Microsoft Store version keeps its files in a
  different place, and saving word packs there has not yet been checked on a Store install.
- A mouse button bound on its own doesn't do its usual job in other apps while Scribe runs, unless you
  press it with a modifier or pause dictation. Choose another button or a key if that gets in your way.
- A game that reads the mouse directly may still see a bound button.
- Another program's mouse hook may not see a bound button either, so a utility that remaps that same
  button can stop working on it while Scribe runs. Let the utility send a key such as F13 instead, and
  bind that key.
- While Scribe watches the mouse, every pointer move passes through Scribe, so a moment when Scribe is
  busy can briefly hold up the pointer.
- A shortcut of one modifier and a key, such as Ctrl+F13, is recorded as those two exact keys, left
  or right. If a mouse button set to such a pair doesn't respond, set it to a key on its own, such as
  F13, or add a second modifier.
- Recording a shortcut ignores a few unassigned or reserved key codes, so a button whose software
  sends one of those can't be bound. F13 to F24 and the media and browser keys all work.
- Very rarely, after a press of a bound button that Scribe could not see (on a Windows security
  prompt, say), letting go of it while a window of an app running as administrator is in front can
  leave Windows thinking the button is still held. The next time you use the button with another
  window in front, Windows sees it let go, which for Back or Forward can navigate once.
- A key you press at the very moment the self-healing releases it can be let go in Windows while you
  hold it; let go of the key and press it again. If another program holds up keyboard input for more
  than a quarter of a second, a release the self-healing is already sending can also arrive just after
  you choose Change.
- Like other apps that are not running as administrator, Scribe's shortcuts, mouse buttons included,
  may not respond while a window of an app running as administrator is active.

## For the macOS app

The rebuilt Settings, word packs, mouse button shortcuts, the new recording indicator and the Remote
Desktop changes are Windows only for now. The native Apple Silicon port proposed in issue #40 has
shipped since 0.3.16; build the app from macos/README.md (thanks to x3nc0n). A performance and privacy
overhaul of the port is in review (#81).

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store
availability follows certification.
