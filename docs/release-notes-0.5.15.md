# Scribe 0.5.15

Scribe 0.5.15 turns on high reasoning (`ReasoningEffort.High`) for **Learn from history**, enables self-healing dictionary and word pack recommendations when an existing rule's spelling or casing is wrong in context, and uses the Foundry Local 2.1 SDK's native context-length metadata and request preflight before falling back to `genai_config.json`. Existing settings, dictionary, word packs and history carry over (`user_version` 7).

## High reasoning for Learn from history

- **High reasoning across every AI cleanup service:** While normal push-to-talk dictation cleanup keeps low or disabled reasoning for fast typing latency, **Learn from history** now requests `ReasoningEffort.High` (`reasoning.effort = "high"` on Microsoft Foundry and OpenAI-compatible services, `reasoning_effort = "high"` without `/no_think` on Foundry Local, `think = "high"` on Ollama's native API, `think = true` with `reasoning_effort = "high"` on TensorSharp, and `SessionConfig.ReasoningEffort = "high"` on GitHub Copilot), with up to 8,192 output tokens reserved for reasoning and JSON suggestions.
- **Automatic fallback for non-reasoning models:** If a configured model rejects `reasoning_effort` or `think` with HTTP 400, or exhausts its token budget inside `<think>` tags without emitting usable JSON, Scribe automatically retries the request once without high reasoning so every model continues to work.
- **Full-context learning on this PC:** When AI cleanup runs on this PC (Foundry Local, Ollama, LM Studio or TensorSharp) and its context window is known, **Learn from history** automatically uses the model's context window to include your personal dictionary and AI-permitted word packs alongside recent dictation history.

## Self-healing dictionary and word pack suggestions

- **Supporting evidence and rule repair:** `AiDictionarySuggester` now uses your existing dictionary and word pack entries as domain evidence to infer what failed or mis-rewritten dictations in history were meant to say, and recommends corrected written replacements when an existing rule has the wrong spelling or casing in context.
- **In-place dictionary updates:** When **Learn from history** suggests a corrected written form for a spoken phrase already in **Your words**, Scribe updates the existing row in place and enables it instead of skipping the correction or adding a duplicate spoken form.

## Foundry Local 2.1 SDK context discovery

- **Native SDK context and preflight first:** `FoundryContextTokensAsync` now checks `ModelInfo.ContextLength` and `ChatSession.PreflightRequestAsync` (`ContextLimitTokens`) from `Microsoft.AI.Foundry.Local` 2.1.0 before falling back to reading `genai_config.json` on disk.

## Privacy and compatibility

No audio is ever sent by AI cleanup or **Learn from history**, and remote consent dialogs and recipient checks remain unchanged. Existing settings and the storage schema (`user_version` 7) are unchanged.

The Store library-journal desktop gate remains unverified. The macOS port's rows in `macos/PORTING-PLAN.md` for AI dictionary suggestions and local model context discovery may now be stale.
