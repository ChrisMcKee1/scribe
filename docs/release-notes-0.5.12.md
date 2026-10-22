# Scribe 0.5.12

Scribe 0.5.12 adds context window options up to 1M tokens and whole-vocabulary support for frontier and custom AI services, expands **Learn from history** to send your entire dictionary, all AI-permitted word packs, and as much dictation history as fits in your selected context window, and improves context-aware vocabulary suggestions. Existing settings, dictionary, word packs and history carry over. Speech recognition remains offline and on the CPU; AI cleanup stays optional.

## Frontier context sizing and Learn from history

- **Context sizes up to 1M tokens (`256K`, `512K`, `1M`):** Added `256K (262,144 tokens)`, `512K (524,288 tokens)`, and `1M (1,048,576 tokens)` to the context size selector, and added **Context and vocabulary** expanders (**Context size** and **Send your whole vocabulary when it fits**) for **Microsoft Foundry**, **GitHub Copilot**, and **Another AI service**.
- **Full-context dictation cleanup and Learn from history:** When a context size and **Send your whole vocabulary when it fits** are enabled, dictation cleanup fits your full vocabulary into that context window, and **Learn from history** sends your entire dictionary, all AI-permitted word packs, and every distinct dictation in your history that fits into the window (scaling up to 100 suggestions and 8,192 output tokens).
- **Surrounding-context analysis for Learn from history:** Updated the dictionary suggestion instructions so the model uses the surrounding sentence, topic, nearby dictations, and existing vocabulary rules to spot phrases that speech recognition or an earlier rewrite got wrong in context.

## Privacy and compatibility

All local-app requests stay on this PC. Remote AI cleanup and dictionary suggestions remain opt-in, honor word-pack AI permissions and recipient checks, and disclose full-context transmission before sending. Audio never leaves the device. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for remote context sizing and full-context dictionary suggestions remain separate.
