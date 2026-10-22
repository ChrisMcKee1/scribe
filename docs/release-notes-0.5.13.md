# Scribe 0.5.13

Scribe 0.5.13 fixes full-context vocabulary and **Learn from history** on Microsoft Foundry, GitHub Copilot, and custom AI services when **Send your whole vocabulary when it fits** is turned on with the service's default context size setting, or when a context size is chosen before saving. Existing settings, dictionary, word packs and history carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## Frontier context sizing and Learn from history fix

- **Whole vocabulary works with the service's default context setting:** Checking **Send your whole vocabulary when it fits** on **Microsoft Foundry**, **GitHub Copilot**, or **Another AI service** now activates full-context cleanup and **Learn from history** (up to 1,048,576 tokens) even when **Context size** is left on the service's default setting.
- **Full-context Learn from history and confirmation dialog:** Choosing **Learn from history** with **Send your whole vocabulary when it fits** enabled or a context size selected now displays the full-context confirmation dialog and sends your dictionary, all AI-permitted word packs, and all distinct dictation history that fits in that context window.

## Privacy and compatibility

All local-app requests stay on this PC (Foundry Local runs on this PC and requires no remote confirmation dialog). Remote AI cleanup and dictionary suggestions remain opt-in, honor word-pack AI permissions and recipient checks, and disclose full-context transmission before sending. Audio never leaves the device. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for remote context sizing and full-context dictionary suggestions remain separate.
