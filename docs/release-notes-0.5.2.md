# Scribe 0.5.2

Scribe 0.5.2 makes AI cleanup on your own PC a real choice. It now runs with Ollama or LM Studio as well as
with the model Scribe sets up for you, and small open models clean dictation well: the best of them scores 90
out of 100, against about 95 for the cloud models, in about half a second on a graphics card, at no cost per
dictation. Every AI cleanup request, on your PC or in the cloud, sends much less, lists come out as lists, and
adding a word to your dictionary takes one form. Everything you set up in 0.5.1 carries over.

## What changes when you update

- **AI cleanup on this PC with Ollama or LM Studio.** Settings, AI cleanup, On this PC now offers three
  ways to run it: Let Scribe manage it (Foundry Local, as before), Ollama, and LM Studio. Choose the app,
  then one of the models you downloaded in it, and Save; Scribe uses the app's own address. Settings doesn't
  recommend a model. The [local model benchmark](local-model-benchmark.md) compares 39 open models across
  Foundry Local, Ollama and LM Studio, and the cloud models, on quality and time.
- **Small models now clean dictation well.** 0.5.1 sent requests that small models on your PC couldn't
  follow: Ollama kept the model's thinking on, ignored the length limit, and cut Scribe's instructions to
  fit its default memory for a conversation, so the model never saw them. Scribe now gives a model on your
  PC short instructions it can follow, turns thinking off, and removes what small models wrap around an
  answer. The same nine models scored 26 points higher on average. Some that did well on an NVIDIA RTX
  5080, out of 100:

  | Model | In | Quality | Typical time, graphics card | Typical time, processor only |
  |---|---|---:|---:|---:|
  | Gemma 4 E4B | Ollama | 90.1 | 0.51 s | 4.6 s |
  | Qwen3 4B Instruct 2507 | Ollama, LM Studio | 85.5 | 0.45 s | 5.0 s |
  | Qwen2.5 7B | Scribe's own (Foundry Local) | 85.4 | 0.74 s | not tested |
  | Gemma 4 E2B | Ollama, LM Studio | 84.6 | 0.32 s | 2.4 s (LM Studio) |
  | Granite 4 3B | Ollama | 82.2 | 0.34 s | 3.9 s |
  | Qwen2.5 1.5B | Scribe's own (Foundry Local) | 74.0 | 0.37 s | 6.7 s |

  Differences under about 5 points are within the judge's noise. For comparison, the cloud models scored
  94.9 to 96.0.
- **Each request sends less.** Every AI cleanup request, to any service, now carries only the words from
  your dictionary and word packs that the dictation appears to mention, instead of your whole vocabulary (up
  to 5,000 words, or the first 80 with Scribe's own model). A cloud request fell from about 7,500 to 1,400
  input tokens, and the right word reached the text more often (97% of the time against 67% for Gemma 4 E4B).
  PRIVACY.md says the same.
- **Lists when you list things.** Scribe's writing style now writes the items you dictate as a list.
- **Microsoft Foundry answers faster.** Scribe asks a Microsoft Foundry model for the least reasoning it
  accepts, which cleaned dictation in about a third less time at the same quality. With Azure CLI sign-in,
  Scribe also reuses the access token until shortly before it expires instead of asking Azure CLI for a new
  one for every dictation, which cost 1.2 to 7 seconds each time. An account you change outside Scribe is
  picked up at the next token refresh.
- **Your PC gets its memory back.** After 10 minutes without a dictation (Free memory when Scribe isn't used,
  in Settings, Advanced), Scribe frees its speech model and the model Scribe sets up for you, and Ollama and
  LM Studio free theirs on their own clock, because every request asks them to keep the model only that
  long. Pausing dictation frees the model at once, even in the middle of a dictation (once it's typed), and
  so do turning AI cleanup off, moving it to another model, and the Free memory button in Settings; with
  Ollama or LM Studio that unloads it for any other app that uses the same model too. A shorter time applies
  at once, saving an unrelated setting no longer puts the release off, and a model a dictionary suggestion
  or Test connection loaded is freed after the same time. When you start recording, Scribe asks the app to
  have the model ready. After the model was freed, the next dictation waits for it to load, for up to 30
  seconds, and the recording indicator says Starting local model and This can take time; past that, Scribe
  types what it heard.
- **Foundry Local 2.1.** The model Scribe sets up for you now runs on Foundry Local 2.1, which runs models
  on NVIDIA graphics cards through CUDA: Phi-4 Mini and Qwen3 4B went from about 16 seconds on the processor
  to under a second. A first setup on an NVIDIA RTX graphics card with 8 GB or more starts from Qwen2.5 7B,
  and everywhere else from Qwen2.5 1.5B, which also runs on the processor. If 0.5.1 moved your model to the
  processor after a graphics card problem, 0.5.2 tries the graphics card again.
- **Adding a word takes one form.** In Dictionary, Your words, Add word now lets you enter several ways
  Scribe hears a word with one written spelling, using Add another way, instead of adding each entry
  separately. Edit opens the same form for an existing entry; editing directly in the list still works,
  and changes take effect when you save Settings. Text fields start editing on the first click, Tab moves
  to the right field, and focus comes back to the entry when you return to it.

## Under the hood

- Foundry Local moves from the `Microsoft.AI.Foundry.Local.WinML` 1.2.4 package to `Microsoft.AI.Foundry.Local`
  2.1.0, the one dependency change. It brings ONNX Runtime 1.30.0, which speech recognition now runs on as
  well; the recognizer check and the scenario suite pass on it on x64 and Arm64.
- On NVIDIA RTX 50 series cards, Scribe turns off ONNX Runtime's cuDNN attention before Foundry Local starts
  (`ORT_ENABLE_CUDNN_FLASH_ATTENTION=0`, unless you set it yourself): with it on, Qwen3 4B failed every
  request and Gemma 4 E2B's first request took 16 seconds.
- A graphics card build that fails its first request moves to its processor build for the rest of the
  session, and the next start tries the graphics card again, since a busy graphics card fails the same way.
- To list their models and free memory, Scribe talks to Ollama's and LM Studio's own management
  interfaces, only on this PC, and never follows a redirect elsewhere. PRIVACY.md lists those requests.
- An idle release is decided when it runs, not when its timer was set: a release whose timer raced a
  dictation or a settings change does nothing, and a release waits for any request still using the model.
- The performance flag `CliTokenEveryRequest` brings back a new Azure CLI token for every dictation, for
  comparison, for this release only.

## Known limitations

- Foundry Local's catalog has no Gemma 4 E4B and no Qwen3 4B Instruct 2507, the models that lead the local
  benchmark; for the best quality on your PC, use Ollama or LM Studio. In Foundry Local 2.1, every build of
  Qwen3.5 2B fails (it left Scribe's list), SmolLM3 3B's chat template fails, Gemma 4 E2B gives no answer on
  the processor, and on CUDA it is erratic.
- Ollama and LM Studio appear under On this PC only at their usual addresses on this PC and without an API
  key. At any other address, or with a key, use Another AI service.
- After the time you set, Ollama and LM Studio free the model on their own clock, counted from the last
  request they served; a model you loaded yourself in LM Studio stays loaded until you pause dictation, turn
  AI cleanup off or choose Free memory. With the time set to Never, Ollama and LM Studio may still free a
  model on their own.
- Word pack changes are written to disk through a journal so that a crash can't leave a pack half saved.
  That was tested on the direct download; saving word packs has not yet been checked on a Microsoft Store
  install, which keeps its files in a different place.
- At a large Windows text size (225%) in a small Settings window, the Your words and History tables scroll
  sideways or shorten long entries; widening the window shows them whole.
- The mouse button and shortcut limitations listed in the 0.5.0 notes still apply.

## For the macOS app

What each AI cleanup request carries, the requests to a model on this PC, the lists in the writing style,
Foundry Local 2.1 and the dictionary form are Windows only for now, so the matching rows of
macos/PORTING-PLAN.md may be out of date.

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store
availability follows certification.
