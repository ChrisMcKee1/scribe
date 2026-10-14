# Scribe 0.5.9

Scribe 0.5.9 improves app-aware formatting for Markdown apps, local AI model error diagnostics, text typing recovery and keyboard shortcut reliability. Existing settings, dictionary, word packs and history carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## App-aware formatting in Markdown apps

- When **Use app-aware formatting** is turned on in Settings, Dictation, Scribe now automatically pastes literal Markdown in known Markdown-aware desktop apps (including GitHub Copilot, Microsoft Copilot, Claude, ChatGPT, Microsoft 365 Copilot, Microsoft Scout, Visual Studio Code, Obsidian, Notion, Slack, Discord and Microsoft Teams) whenever line breaks are kept and no custom app profile overrides the app.
- Pasting multi-line bullet lists as a single unit prevents Markdown editors from auto-inserting a second bullet (`- - `) after each line break.
- Added `copilotapp` (the Microsoft Copilot desktop app) to the **AI chat and agents** app profile preset.

## Local AI cleanup diagnostics and guidance

- When Ollama fails to load or run a model (for example, when a model process crashes or runs out of memory), Scribe preserves the server's error explanation for Settings and Diagnostics rather than replacing it with a generic HTTP status message, and avoids futile automatic reconnect loops for a crashed model process.
- Failure notices in the recording indicator, tray and Settings now point directly to **Settings, Diagnostics**, which shows the current AI cleanup setup problem and refreshes the failure list whenever you open the page.

## Text typing, shortcut watchdog and dictionary suggestions

- When typing text with Unicode keystrokes is interrupted mid-batch because the focused window changed, Scribe stops retrying immediately instead of waiting through five retries, and only sends a defensive Shift key-up when a shifted line break actually left Shift held down.
- The keyboard shortcut watchdog now withholds and disarms its liveness probe while an elevated (higher-integrity) window is in front, avoiding false-positive hook reinstalls during installer or UAC activity.
- **Learn from history** (`AiDictionarySuggester`) now ignores bracketed notes inside `<think>` blocks or preamble text, accepts object-wrapped suggestion lists, and recovers all complete suggestions from truncated model output.

## Privacy and compatibility

Nothing about what Scribe sends, stores or keeps has changed. Audio never leaves the device. Existing settings and the storage schema are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for app-aware formatting and local model diagnostics remain separate.
