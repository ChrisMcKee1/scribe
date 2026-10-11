# Scribe 0.5.8

Scribe 0.5.8 targets screenshots that stop working while Scribe runs. Existing settings, dictionary, word packs and
history carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## Screenshots while Scribe runs

Some people found that Win+Shift+S and the Snipping Tool stopped working while Scribe was running, and came back as
soon as Scribe quit. An earlier investigation traced it to the recording indicator's window: screenshots worked until
the indicator had been shown once, then failed. Since 0.5.0 that window stays loaded between dictations, so the
problem could last until Scribe quit or dictation was paused.

The recording indicator now uses the window setting that lets clicks pass through it only while it is on screen, and
gives it back to Windows as soon as it hides. While the indicator shows, it looks and behaves as before. This is the
leading cause found, not yet confirmed on a PC that shows the problem. If screenshots still stop working, ending
Scribe.Overlay.exe in Task Manager (or pausing dictation from the tray) tells whether the indicator is involved, and
turning the recording indicator off in Settings avoids it until a fix is confirmed.

A review of how Scribe uses the clipboard, its keyboard shortcuts and every other system-wide setting found nothing
else that could do this on the default settings. Scribe types its text by default and does not touch the clipboard,
and its shortcut listener holds back only the keys you chose.

## Privacy and compatibility

Nothing about what Scribe sends, stores or keeps has changed. Audio never leaves the device. Existing settings and the
storage schema are unchanged.

The Store library-journal desktop gate remains unverified. No macOS behavior is changed by this Windows release; the
macOS recording indicator is a different kind of window.
