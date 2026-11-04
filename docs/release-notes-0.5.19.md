# Scribe 0.5.19

Scribe 0.5.19 keeps the space after each dictation in Teams, the GitHub Copilot app and other chat apps, pastes formatted lists and emphasis into Teams, Microsoft Copilot and Codex, and keeps an Excel dictation in one cell. Existing settings, dictionary, word packs and history carry over (`user_version` 7).

## Typing

- **The space after a dictation stays:** with **Add a space after each dictation** on (Settings > Dictation > Typing), the space was often missing in Teams, the GitHub Copilot app and Microsoft Copilot, so the next dictation ran straight into the last one ("Hello world.Next."). Scribe pasted the space together with the dictation, and those apps' message boxes drop a pasted space at the end of a line. Scribe now pastes the dictation and then types the space, the way you would. If the space can't be typed, for example because you clicked another window right after the paste, Scribe says the dictation was not all typed, so you can copy it from the tray menu.

## Pasting into Markdown apps

- **Formatted text in Teams, Microsoft Copilot and Codex:** these apps show pasted Markdown exactly as typed, so a dictated list arrived as `- item` and bold as `**bold**`. When a dictation has Markdown in it (lists, bold, links, code, tables), Scribe now pastes it as **Markdown with formatting**: the same text plus a formatted copy of it on the clipboard, so lists, emphasis, links, code and tables arrive formatted. A dictation without Markdown in it pastes as plain text, as before. GitHub Copilot, Claude, Scout and Visual Studio Code keep getting the Markdown text, which they already format themselves.
- **A new text format for App profiles:** **Markdown with formatting** sits beside Plain text and Literal Markdown source in the format choices, for any app you set up a profile for. When Scribe types a dictation instead of pasting it, it types the Markdown.
- **Excel keeps a dictation in one cell:** in Excel a line break leaves the cell (Shift+Enter selects the cell above), so a multi-line dictation wrote each line over the cell above. With **Automatic** line breaks, a dictation into Excel is now put on one line, and the Documents profile preset no longer lists Excel.

## Privacy and compatibility

Nothing new is sent anywhere, and no audio is ever sent. The formatted copy is made on this PC, holds nothing the text does not, carries the same marks that keep Scribe's clipboard writes out of clipboard history and cloud sync, and is removed when Scribe puts your clipboard back. Backslashes, entities and any markup you dictate stay as typed. Existing settings and the storage schema (`user_version` 7) are unchanged. An older Scribe reads a profile set to Markdown with formatting as Plain text.

The formatted copy is rendered with Markdig (BSD-2-Clause), now credited in the README.

The Store library-journal desktop gate remains unverified. The macOS port does not have app-aware formatting or the typed space after a paste yet; its insertion and profile rows in `macos/PORTING-PLAN.md` may now be stale.
