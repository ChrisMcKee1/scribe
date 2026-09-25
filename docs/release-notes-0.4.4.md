# Scribe 0.4.4

This release is about fitting the PC you already have. Scribe follows the microphone Windows uses,
the moment you change it. New installs get push-to-talk keys that every keyboard has, a space now
follows each dictation so the next one doesn't run into it, and every window stays readable whatever
accent colour you use. Settings also says exactly what AI cleanup sends, and history you delete is
overwritten inside Scribe's database instead of lingering there.

## What changes when you update

- **Your hotkeys stay as they are.** New installs dictate with hold Page Down (with AI cleanup) and
  hold Page Up (dictation only), because not every keyboard has a Right Ctrl. To switch, open
  Settings, General, choose Restore default hotkeys, then Save.
- **A space after each dictation**, on for everyone, new and existing installs alike. Turn it off in
  Settings > Dictation > Add a space after each dictation if an app you use needs text without a
  trailing space.
- **Locking your PC ends a dictation.** If your key is held, or your toggle is on, when Windows
  locks or shows a secure prompt such as Ctrl+Alt+Del or a UAC prompt, the dictation ends at that
  moment instead of recording on.
- **"Windows default" means the microphone Windows Settings shows.** If your communications device
  differs from your default input device, Scribe now records from the default input device (the one
  under Sound, Input). Pick the other microphone by name in Settings or from the tray.
- **If you had bound Page Up or Page Down on its own**, pressing it with Ctrl, Shift, Alt, Win or
  the Narrator key now reaches the app instead of starting a dictation, as with the new defaults.
- **Presentation remotes.** Many clickers send Page Down and Page Up, so while those keys are bound
  to Scribe, a clicker stops changing slides. Choose other keys in Settings if you present.

## Microphone

- Scribe follows the Windows default input device from your next dictation, with no restart, and the
  microphone list in Settings stays up to date as devices come and go. Settings and the log name the
  same device.
- The tray has a Microphone menu: Windows default, each microphone, and Sound settings. An open
  Settings window picks up a choice you make there, and its next Save keeps it.
- A chosen microphone that is unplugged or disabled no longer makes every dictation fail: Scribe
  records from the Windows default instead and tells you once.

## Hotkeys and dictation

- **A space after each dictation.** Scribe now types a space after the text of every dictation, so
  back-to-back dictations no longer run together (thanks to agustaf9, issue #78). It is skipped when
  the text already ends in white space, such as a space, a tab or a line break (a snippet that ends
  on a new line, for example). Only the app you dictate into gets the space: history and the tray's
  copies keep your text without it. If you go back to an earlier version and change a setting there,
  the space comes back on when you return to 0.4.4 or later.
- Restore default hotkeys in Settings, General sets hold Page Down and hold Page Up. As with every
  other setting, nothing changes until you Save, and Cancel discards it.
- If Scribe cannot read your saved settings, that session keeps the keys earlier versions shipped
  (hold Right Ctrl) instead of switching you to the new ones.
- While Page Up and Page Down are bound to Scribe, pressing them on their own no longer pages
  through documents in other apps. With Ctrl, Shift, Alt, Win or the Narrator key held they still
  work in other apps as before, so Ctrl+Page Down still switches tabs. Most laptops without those
  keys put them on Fn with the Up and Down arrows.
- A dictation that ends because Windows locked or showed a secure prompt is transcribed as usual,
  and if the text cannot be typed while the PC is locked, it waits in the tray for you to copy, as
  any dictation that could not be typed does.
- In toggle mode, when a dictation ends without your key (the microphone disconnects, the silence
  auto-stop or the time limit ends it, or you pause it), your first press once it has been
  transcribed starts a new dictation (after a pause, once you resume). A press Scribe handles while
  the dictation is still being transcribed is ignored, and now costs nothing; a press still waiting
  to be handled when the transcription finishes can start the next dictation. Before, after a
  disconnect your first press did nothing, and after the others a press handled during the
  transcription made your next press do nothing too.
- Page Down no longer appears as "Next" in Settings, in the welcome or while you press a key to bind
  it, and a key Windows gives two names, such as a Korean keyboard's Hangul key, is no longer shown
  under the other key's name.
- The welcome names the keys you actually use, says where laptops keep Page Up and Page Down, and
  its text no longer runs past the edge of its cards.

## AI cleanup and privacy

- **Settings now says exactly what AI cleanup sends.** With Microsoft Foundry, an OpenAI-compatible
  endpoint or GitHub Copilot, every cleanup request carries the text Scribe recognized for that
  dictation, the cleanup instructions with your writing style (or the matching app profile's), and
  your enabled dictionary and library terms as vocabulary: up to 5,000 terms and 24,000 characters,
  or 80 terms with the Local prompt style, whether or not the dictation mentions them. Earlier
  versions said "relevant dictionary terms", which understated it. Foundry Local keeps your text on
  your PC, and audio and snippet templates are never sent. PRIVACY.md, the README and the setup
  guides say the same.
- The short test request each provider gets when cleanup connects no longer includes your
  vocabulary.
- A dictionary entry whose replacement spans more than one line or runs past 100 characters, such as
  a signature, still works on your PC, but it is no longer sent as vocabulary or shared as an AI
  usage insight label.
- Before AI dictionary suggestions send up to 6,000 characters of your recent dictations to a
  provider outside your PC, Scribe asks and names that provider, and the request goes only there: if
  your AI provider changes before it goes out, nothing is sent. Earlier versions could skip the
  question after a Save that failed.
- Deleting history now overwrites the deleted text and recordings inside Scribe's database, and
  Scribe empties its write-ahead log soon after, normally within a minute, so deleted dictations
  don't linger in its files; it also tries to empty the log when it closes. Deleted dictionary
  entries, snippets and profiles are overwritten too, and Clear history removes recordings in small
  batches.
- If a Settings save fails, the changes you made in the window stay unapplied. Adding a term from
  the usage page used to apply them anyway, including a different AI provider or hotkeys; it now
  applies only your saved settings. Your library selection also always comes from the settings in
  use, so a settings file that becomes unreadable can no longer switch the default AI libraries back
  on.
- ".NET" keeps its space: "we use .NET" no longer becomes "we use.NET". The same goes for other
  words that start with a punctuation mark, such as .gitignore.
- PRIVACY.md now also notes that, although Scribe asks Microsoft Foundry not to store responses,
  Microsoft's abuse monitoring can still keep a sample of flagged prompts and responses for review.

## Libraries and dictionary

- The Libraries page lists the built-in libraries and your own in one A to Z list, with "Built-in"
  or "Your library" under each name. Ticking a library's box no longer moves its row, and the
  columns no longer sort. What dictation writes is unchanged: when two libraries define the same
  phrase, the same one wins as before.
- Ctrl+C on a library row still copies its name, and screen readers read each name with its source,
  for example "GitHub, Built-in".
- The dictionary cleanup now keeps a library on whenever switching it off could change what
  dictation writes, and when it finishes it lists the libraries it kept on. When it does switch a
  library off, it first copies the terms you still use into your dictionary, so they keep working.
- The Dictionary page's line about AI cleanup counts your vocabulary exactly as dictation builds it,
  says when a limit cuts it, and says so when post-processing is off and your entries are not
  applied on this PC.
- Check boxes in the Libraries and Dictionary lists toggle on the first click, and the Libraries,
  Snippets and Profiles detail panels have a border.

## Settings, keyboard and screen readers

- Tab follows the visible layout in every window. Cards no longer take focus, each list is one Tab
  stop with the arrow keys inside, Tab in a Dictionary row moves from Spoken to Replacement, the
  tray menu takes keyboard focus while it is open, and Shift+Tab goes back exactly. A pass through
  Settings no longer stops on anything you can't see or use.
- Many controls that had no screen reader name, or the wrong one, now have the right one, and
  History's cells keep theirs when the list reloads.
- Confirmations that delete something or send text to a cloud provider (Clear history, removing a
  library, deleting entries, cloud consent) now start on Cancel. Escape closes the welcome.
- Azure CLI sign-in: Tenant ID (optional) is a plain field shown before you sign in, and the
  "Optional Azure details" section, which could open to nothing, is gone. A sign-in you retry or
  abandon can no longer report a result or open a browser, an abandoned az login no longer blocks
  the next attempt for up to five minutes, and cancelling one ends only the Azure CLI's own
  processes, never your browser. Editing a service principal field while it is being verified frees
  Verify again.
- **Readable on any accent colour.** Text on accent-coloured buttons, badges and selections is now
  chosen from the colour it sits on. With a dark accent in the dark theme, or a light one such as
  gold in the light theme, earlier versions drew black or white text there that was hard to read.
- Pressed buttons keep a readable label. Before, a pressed button's label always turned black, which
  on most buttons in the dark theme was nearly invisible.
- Headings, links and other accent-coloured text are made lighter or darker only when they would be
  hard to read, keeping their hue. Links are readable on the dark page, and when you hover them in
  either theme.
- A checked box or an on switch that doesn't stand out from the page gets a visible edge or a
  stronger track, a selected item in the navigation rail, Snippets and Profiles gets a thin outline
  where its highlight is faint, and the selected library shows an outline and a bold name.
- Windows contrast themes are left exactly as they were. With the default blue accent, little
  changes: the selected library's outline and name, the Dictionary badges in the light theme, links,
  and in the dark theme the red delete buttons and pressed buttons.

## Under the hood

- No dependency changes: the speech engine, the AI libraries and the Windows App SDK are the same
  builds as in 0.4.3.

## Known limitations

- While Page Up and Page Down are bound, they don't page through documents in other apps on their
  own, a presentation remote can't change slides, and the keypad's 3 and 9 with Num Lock off count
  as Page Down and Page Up too. Choose other keys if that gets in your way.
- On laptops that put Page Up and Page Down on Fn with the arrow keys, what the keys send depends on
  the keyboard's firmware. If they misbehave, choose other keys.
- Like other apps that are not running as administrator, Scribe's hotkeys may not respond while a
  window of an app running as administrator is active.
- The space after a dictation goes to every app. A dictation typed just before text that starts with
  a space leaves two spaces there, apps that adjust spacing around pasted text, such as Word, may
  change it, and text in a script without spaces between sentences gets the space too. Turn it off
  in Settings > Dictation if it gets in your way.
- A dictation that is already being cleaned up when you save a change to your dictionary or
  libraries finishes with the vocabulary it started with.
- A term you add with quick add or learn from history is applied on your PC at once, but AI cleanup
  receives it only the next time your settings are applied, for example when you save Settings or
  restart Scribe.
- Text deleted with 0.4.3 or earlier can remain in unused space in Scribe's database until it is
  written over, and nothing Scribe does reaches copies outside the database, such as backups or the
  storage device itself. If another program has the database open when Scribe closes, an earlier
  copy of deleted data can stay in the write-ahead log until the log is next emptied.
- The dictionary cleanup is cautious: it keeps a library on whenever one of its terms could overlap,
  in something you dictate, a term that stays in use, even by a single letter. With the two AI
  libraries on, as on a default install, it currently keeps every other built-in library on, even
  one you never use; turn those off yourself on the Libraries page instead.
- In Windows contrast themes Scribe leaves the theme's colours alone, so a few weak spots of the
  underlying UI library there, such as some pressed buttons, may remain. In quick add, a selected
  word is marked only by the accent colour, which is hard to see with a very dark accent in the dark
  theme.
- Escape in Settings still closes the window without saving and without asking, and a few small
  helper buttons inside some controls (spin buttons, sliders, the password reveal) still have no
  screen reader names.

## For the macOS app

The native Apple Silicon port proposed in issue #40 has shipped since 0.3.16; build the app from
macos/README.md (thanks to x3nc0n). A performance and privacy overhaul of the port is in
review (#81).

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store
availability follows certification.
