# Scribe 0.5.4

Scribe 0.5.4 fixes the Ollama check in Settings for people whose Ollama listens on only one local address. Nothing
else changes, and everything you set up before carries over.

## What changes when you update

- **Ollama and LM Studio are found whichever local address they listen on.** Settings, AI cleanup, On this PC,
  Ollama showed "Ollama didn't answer", and the model list held only the saved model, when Ollama listened on only
  one of the two local addresses `localhost` stands for (`127.0.0.1` and `::1`). That happens, for example, when
  you start Ollama yourself with `ollama serve` and `OLLAMA_HOST=localhost`. Dictation worked all along, because
  it reaches Ollama another way. Scribe tried the two addresses one after the other, and Windows takes about two
  seconds to refuse a connection to a port nothing listens on, which was the whole time Scribe allowed, so it never
  reached the address Ollama was on. It now tries both at once and uses the first that answers. Check again, the
  model list and Free memory all use the same connection.
- **A slow Ollama no longer hides your models.** The check gives the model list 10 seconds, each request its own
  limit, and the list of models in memory 3 more. If an app is busy loading a large model and is slow to say
  what it holds, you still get the models it listed; only the "is using memory" line can be missing.

## Under the hood

- `LocalServerClient` connects through a connect callback that tries every address `localhost` resolves to at once
  (`ConnectToFirstAsync`) and wraps a refusal or a timeout as a connection error, so an app that is not open still
  reads as "can't reach" rather than "didn't answer".
- A failed read now says why in the log: `Settings read Ollama: Failed, ... (<failure shape>)`, with exception types
  and HTTP status only, never the answer.
- A build with a `VersionSuffix` in `Directory.Build.props` is a test build: `pack.ps1` will not publish it, and
  `release.yml`, `store.yml` and `pack-msix.ps1` refuse it, so it cannot reach the Microsoft Store. Release builds
  leave it unset.
- The macOS port was not changed. Its Ollama check may have the same weakness; that has not been checked.
