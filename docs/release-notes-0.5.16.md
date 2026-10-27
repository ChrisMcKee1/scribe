# Scribe 0.5.16

Scribe 0.5.16 fixes numbered and bullet lists that came out doubled ("2. 2.", "- - ") in Markdown apps such as Claude, GitHub Copilot and Visual Studio Code, and turns **Use app-aware formatting** on for everyone. Existing settings, dictionary, word packs and history carry over (`user_version` 7).

## Lists in Markdown apps no longer double up

- **The paste no longer gives up because of your clipboard:** with app-aware formatting on, Scribe pastes a dictation into a known Markdown app in one go, so the app cannot continue a list with markers of its own. Until now that paste fell back to typing whenever the clipboard held anything but plain text: a screenshot, text copied with formatting from a browser, the Claude app, VS Code or Word, or copied files. Typed line by line, every list item after the first gained a second marker.
- **Your clipboard comes back as it was:** Scribe now keeps everything on the clipboard for the paste (images, formatted text, copied files), in memory only, and puts it all back afterwards. Anything you copy during the paste is left alone.
- **When Scribe still types instead:** if something on the clipboard cannot be put back exactly (content only the app that copied it can draw, more than 128 MB, or an app that is not responding), Scribe leaves the clipboard untouched and types the text as before.
- **One trade-off:** an Office or Excel copy comes back as a plain copy, so Excel's cut and move does not survive a dictation made in between.

## App-aware formatting is on for everyone

- **Use app-aware formatting** in **Settings > Dictation** is now on by default. Every earlier version saved it off for everyone, so this version turns it on once, at its first start. If you turn it off afterwards, it stays off.

## Privacy and compatibility

No audio is ever sent anywhere. When app-aware formatting pastes Markdown into an app Scribe recognizes, Scribe reads what else is on the clipboard, keeps it in memory only for the paste, and puts it back; its own clipboard writes stay excluded from clipboard history and cloud sync. Existing settings and the storage schema (`user_version` 7) are unchanged. An older version that saves your settings drops the app-aware formatting choice, which reads as on again here.

The Store library-journal desktop gate remains unverified. The macOS port has not ported app-aware formatting; its profile, insertion and tray rows in `macos/PORTING-PLAN.md` may now be stale.
