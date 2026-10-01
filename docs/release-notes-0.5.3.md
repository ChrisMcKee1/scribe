# Scribe 0.5.3

Scribe 0.5.3 gives AI cleanup on your own PC two more choices: how much the model reads at once, and whether it
gets your whole vocabulary. It also lets you choose which API another AI service is reached through. Every new
setting starts at what 0.5.2 did, and everything you set up before carries over. One change needs no setting:
requests to a model on this PC are now fitted to what it reads, as the first item below says.

## What changes when you update

- **Every request to a model on this PC is kept to what the model reads.** Foundry Local, and Ollama or LM
  Studio at their own addresses, now get each request planned to fit the model's context, your dictation
  first and your vocabulary in what is left. A dictation too long for the model is split into smaller pieces. If
  the instructions leave no room for even a short piece, setup says the context is too small instead of
  reporting ready and then typing every dictation as heard.
- **Context size for Ollama and LM Studio.** Settings, AI cleanup, On this PC now has a folded section under each
  app ("Ollama settings", "LM Studio settings"; "Model settings" for the model Scribe sets up). **Context size**
  sets how much the model reads at once, from the app's own setting (the default, as in 0.5.2) up to larger
  sizes. A larger size fits more of your vocabulary and can use more memory. With a size chosen for Ollama,
  Scribe reaches it through Ollama's own chat API, the only one that takes a size; with one chosen for LM Studio,
  Scribe loads the model at that size and frees that copy after the time you set. A status line says what the
  model reads and whether your vocabulary fits.
- **Send your whole vocabulary when it fits.** A switch in each app's section, off by default. On, a request
  carries every word from your dictionary and the word packs you let AI cleanup use when they fit beside your
  dictation, instead of only the ones the dictation appears to mention; when they don't fit, the mentioned ones
  go first and then as many others as fit. In the round-three [local model benchmark](local-model-benchmark.md)
  it scored no better on Gemma 4 E4B (87.7 against 87.0) and got the 2B models a fifth to a third fewer word
  pack terms right, so it stays off unless you want it. It applies only to a model on this PC; a cloud service
  or a server elsewhere still gets only the mentioned words.
- **Another AI service: Chat Completions or Responses.** The new **API** box under the address chooses which API
  Scribe uses; it starts at Chat Completions, as before. An address that ends in `/chat/completions` or
  `/responses` is used as that API with the path taken off, so a pasted address works. With Responses, Scribe asks
  the service not to store responses, as it does for Microsoft Foundry. An address ending in the older
  `/completions` is refused with the reason.

## Under the hood

- New dependency: OllamaSharp 5.4.30 (MIT), the `IChatClient` for Ollama's own API, built against
  Microsoft.Extensions.AI 10.8.0, which 10.9.0 satisfies. It is credited in the README.
- Scribe estimates tokens, never counts them, at rates below what the tokenizers measured, so an estimate errs
  toward sending less. The measurements and the whole-vocabulary arms are in
  [`benchmarks/context-window-2026-09-30.json`](benchmarks/context-window-2026-09-30.json).
- PRIVACY.md describes the new requests: Scribe asks Ollama and LM Studio, as each dictation starts, what each
  model reads at once, and a load at a chosen size sends the single word "ok" with a one-token limit that LM Studio
  is asked not to keep. They go only to that app on this PC. With the whole-vocabulary switch on, the readying request
  at the start of a recording also carries the leading part of your vocabulary; with it off, none.

## Known limitations

- With a size chosen for Ollama, Ollama reloads the model whenever another app uses it at a different size, and the
  other app reloads it back.
- LM Studio takes a size only when it loads a model, so a model you loaded yourself keeps its own size. If LM Studio
  refuses the size you chose (for example, not enough memory for 128K), Scribe uses the copy LM Studio loads at its
  own size until you choose another size or restart Scribe.
- The mouse button and shortcut limitations listed in the 0.5.0 notes, and the Store and text size notes in the
  0.5.2 notes, still apply.

## For the macOS app

The context size, the whole-vocabulary switch and the API choice are Windows only for now, so the matching rows of
macos/PORTING-PLAN.md may be out of date.

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store availability
follows certification.
