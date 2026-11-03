# Scribe 0.5.18

Scribe 0.5.18 stops Learn from history from suggesting words you have already fixed, and says so when Add to dictionary fixes a recent dictation too. Existing settings, dictionary, word packs and history carry over (`user_version` 7).

## Learn from history

- **No more suggestions that undo your fixes:** history keeps every dictation as it was typed, including the ones you later fixed with Add to dictionary. When AI cleanup is set up, **Learn from history** in **Settings > Dictionary** asks the AI model for suggestions, and the model could read such a mistake as one of your own terms and suggest writing it back: with "MCB" written as "MCP" in your dictionary, it could suggest writing "m c b" as "MCB". Those suggestions are now left out. A suggestion that writes exactly what one of your words writes, such as "git hub" as "GitHub" beside "github" as "GitHub", is still offered, and a word you switched off no longer counts as a mistake.

## Add to dictionary

- **The notice says when your dictation was fixed:** when you choose Save and close in Add to dictionary from the tray, the notice now also says when the save fixed that dictation, so copying it from the tray gives the fixed text. Scribe already made that fix; the notice just never said so.

## Privacy and compatibility

Nothing new is sent anywhere, and no audio is ever sent. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port does not leave out those suggestions; its AI dictionary suggestions row in `macos/PORTING-PLAN.md` may now be stale.
