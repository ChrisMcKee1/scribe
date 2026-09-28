# Scribe 0.5.1

Scribe 0.5.1 makes the recording indicator's level bars follow your voice, fixes text that could not be
read in the light or dark theme, makes the Dictionary page's two tabs easy to tell apart, and repairs
colors in Windows contrast themes. Under the hood, Scribe uses much less memory for each dictation and
gives more memory back to Windows when it goes idle. Everything you set up in 0.5.0 carries over.

## What changes when you update

- **The level bars move with your voice.** While you dictate, the recording indicator's five bars now
  rise and fall with how loudly you speak: they rest low in a quiet room, fill when you talk normally,
  and stand tall when you speak up. In 0.5.0 they barely moved.
- **Text that was hard or impossible to read is fixed.** Some text used the other theme's color, so it
  looked missing: word pack names and their sources on the Dictionary page's Word packs tab (blank when
  the page first opened, or dark on dark after Windows switched themes), and the descriptions in the
  App profiles menu in the dark theme. Scribe now takes every such color from the current theme.
- **The Dictionary page's tabs are easier to tell apart.** Your words and Word packs each have an icon,
  a live summary ("31 words", "11 of 11 on") and a line that says what the tab holds, with a link to the
  other tab. The selected tab is underlined.
- **Contrast themes look right.** Status colors (success, caution and error), menu separators and
  greyed-out menu items no longer show as red in a Windows contrast theme, and the tray menu follows a
  theme change without keeping colors from the old one.
- **A new switch for Microsoft Foundry's prompt cache.** Settings, AI cleanup, Microsoft Foundry now has
  "Let Microsoft Foundry cache what Scribe sends". It is on, which is how AI cleanup already worked, and
  the page explains the trade-off: with it on, Microsoft Foundry may keep temporary data derived from
  cleanup requests (your dictation, the instructions and your vocabulary) for at least 30 minutes, up
  to 24 hours on some models, to respond faster. Turning it off asks Microsoft Foundry not to use its
  prompt cache for new requests; that works on GPT-5.6 and later models on Standard deployments, and on
  earlier models or provisioned deployments AI cleanup stops and Scribe types what it hears until you
  turn it back on. Another AI service and GitHub Copilot follow their own caching policy. PRIVACY.md
  says the same.

## Faster and lighter

Measured on the same PC against 0.5.0 (Release builds, .NET 10; allocation is the memory a task asks
the runtime for, which the garbage collector then has to clean up):

| What | 0.5.0 | 0.5.1 |
|---|---:|---:|
| Memory allocated per 8-second dictation (48 kHz stereo microphone, large dictionary) | 9.2 MB | 1.2 MB |
| Full (generation 2) memory collections per 25 dictations, after the first 25 | 6 to 12 | 0 |
| Converting a 25-second recording (48 kHz stereo) | 39.3 MB | 2.0 MB |
| Your words and every word pack applied to a short dictation | 616 KB | 8 KB |
| Usage page over 5,000 dictations | 335 MB | 8 MB |
| History search over 10,000 dictations | 10.6 MB | 1.1 MB |
| Typing a 770-character dictation | 125 KB | 8 KB |
| Tray icon change (three or more per dictation) | 97 to 117 KB | 56 bytes |

- **More memory goes back to Windows when Scribe goes idle.** When Scribe unloads the speech models
  after its idle time, it now compacts its memory fully. In a test shaped like a day of dictation, the
  memory Scribe kept dropped by about three quarters, from about 212 to about 53 megabytes. The
  one-time pause for this is about 10 milliseconds longer and happens only while you are not dictating.
- **Real speech gives the same text.** The full scenario suite, which runs real speech through the
  speech engine, produces identical text before and after, and allocates about a quarter less memory
  (362 MB to 270 MB).

## Under the hood

- Apart from the idle memory change above, every change to how Scribe works inside is either proven to
  give the same results as 0.5.0 by a test in which 0.5.0's own code decides every answer, or ships
  switched off. The switched-off ones can be turned on by name for comparison with the
  `SCRIBE_PERF_FLAGS` environment variable, and the log's session start says which are on. AGENTS.md
  lists them.
- The recording indicator's helper no longer writes exception messages to the log, only their type and
  where they came from, like the rest of Scribe. The log's cleanup of older files now wipes its working
  buffer after each file, and when Scribe borrows the clipboard to paste, it no longer copies the bytes
  another app left after the end of the text.
- The log records AI cleanup's token counts and how much memory the idle release gave back. Numbers only.
- No dependency changes in the app: the speech engine, the AI libraries and the Windows App SDK are the
  same builds as in 0.5.0.

## Known limitations

- Word pack changes are written to disk through a journal so that a crash can't leave a pack half
  saved. That was tested on the direct download; the Microsoft Store version keeps its files in a
  different place, and saving word packs there has not yet been checked on a Store install.
- At a large Windows text size (225%) in a small Settings window, the Your words and History tables
  scroll sideways or shorten long entries; widening the window shows them whole.
- The mouse button and shortcut limitations listed in the 0.5.0 notes still apply.

## For the macOS app

The new level bars, the Dictionary tabs, the contrast-theme repairs and the prompt cache switch are
Windows only for now, so the matching rows of macos/PORTING-PLAN.md may be out of date. The memory
changes are Windows code only.

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store
availability follows certification.
