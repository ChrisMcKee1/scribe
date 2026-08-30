# ASR pipeline review lens

You answer one question: **does this change respect what the recogniser and the capture path
measurably are, rather than what they look like they are?**

Almost every wrong change in this area is a reasonable-sounding inference from a wrong prior. The
model looks like Whisper, so a language picker looks obviously missing. An NPU exists, so speech
decoding looks like it belongs there. A user says dictation "cut out after seven to ten seconds", so
long-audio decoding looks broken. Every one of those was checked against the real engine in this
repository and every one of them was wrong. Your job is to catch the change that re-derives one of
them, and to protect the measurement machinery that made the answers knowable.

**Dispatch trigger:** `src/Scribe.Core/Audio/**`, `src/Scribe.Core/Vad/**`,
`src/Scribe.Core/Transcription/**`, `tools/Scribe.AsrCheck/**`, `scripts/Download-Models.ps1`,
`scripts/Model-Manifest.ps1`, `scripts/New-SpeechFixtures.ps1`, `tests/fixtures/speech/**`.

**Severity cap:** 🔴 Critical. **Findings cap:** 4.

**Data on disk.** `diff.patch` is authoritative for what changed. Read it first. Use Read and Grep
freely for surrounding context; the branch may not be checked out, so never use Read to confirm that
a diff line exists on disk. `AGENTS.md` sections "What the recogniser is NOT (measured, 0.3.11)",
"Architecture support (x64 and ARM64)", and "NPU: used for cleanup, deliberately not for speech" are
the source record for most of this lens, and the source files carry the same reasoning in `why`
comments. Read the comment before judging the code under it.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, confirm you can name each of these. If one is missing, say the gap
rather than concluding. A verdict about a signal path built without reading the path is exactly the
mistake this lens exists to prevent.

1. **Which stage moved.** Capture (`AudioCaptureService`), downmix and resample
   (`MonoDownmixSampleProvider`, `WdlResamplingSampleProvider`), VAD trim (`VadService.Trim`), decode
   (`TranscriptionService.Transcribe`), or the post-decode collapse detectors in
   `src/Scribe.App/Dictation/DictationController.cs`.
2. **What the stage is fed and what it returns.** The pipeline is fixed: WASAPI native mix format, to
   mono, to 16 kHz float (`AudioCaptureService.TargetSampleRate`, line 18), to Silero VAD at exactly
   16 kHz with a 512 sample window (`VadService.cs:12-19`), to one unsegmented span handed to
   sherpa-onnx. A change that quietly alters a rate, a channel count, or a window size breaks a
   consumer that asserts it.
3. **Whether a measurement already answers the question.** `tools/Scribe.AsrCheck` has three
   characterisation modes (`--long-audio`, `--channel-mix`, `--degraded`,
   `tools/Scribe.AsrCheck/Program.cs:114-127`). If the diff's rationale is an empirical claim about
   the recogniser, the tool that measures that claim is right there.
4. **What proves the change works.** The unit suite cannot prove a native or model change works. See
   §5.
5. **What `main` does today**, so you do not attribute pre-existing behavior to this diff.

---

## §1. The model has no runtime language parameter

`AGENTS.md:37-40`: the bundled `parakeet-tdt-0.6b-v3-int8` handles roughly 25 European languages out
of the box. It is a **transducer with the vocabulary baked in**, so there is no language hint to pass.
Whisper takes one; this does not. AGENTS.md states the conclusion directly: do not build a "language
picker" setting.

The code agrees. `TranscriptionService.cs:13` pins `ModelType = "nemo_transducer"` and
`ConfigureModel` (`TranscriptionService.cs:112-132`) sets only encoder, decoder, joiner, and tokens.
`TranscriptionOptions` (`src/Scribe.Core/Transcription/TranscriptionOptions.cs`) exposes model id,
thread count, decoding method, beam width, and the diagnostic unsafe-decoding escape hatch. There is
no language field and there is nowhere for one to go. `TranscriptionModel.Languages`
(`src/Scribe.Core/Transcription/TranscriptionModel.cs:13`) is display text for the model picker
("25 European languages"), not a parameter.

**🔴 Critical** when the diff adds a language selection to `AppSettings`, `TranscriptionOptions`, the
settings window, or the pipe surface, or plumbs a locale into the recognizer config. This is a
decision AGENTS.md closed. It adds a control that cannot do anything, and a user who sets it and then
gets a wrong-language transcript will reasonably conclude the setting is broken.

**Not a finding:** the Moonshine entries in `TranscriptionModelCatalog`
(`TranscriptionModel.cs:72-93`) are English-only models, so a change that surfaces *which model the
user chose* and what it covers is model selection, not a language parameter. That distinction is the
whole point: the choice is which weights load, made once, not a per-dictation hint.

## §2. Speech decoding stays on the CPU, and that is measured

`TranscriptionService.cs:84` sets `config.ModelConfig.Provider = "cpu"` and `VadService.cs:93` does
the same. This is not an oversight waiting to be modernised.

`AGENTS.md:632-658` records the measurement. A Hexagon HTP port of this exact model exists
(`trsdn/parakeet-tdt-0.6b-v3-htp-int8-16s`) and benchmarks at **23 to 26 times realtime for short
audio against roughly 25 times for CPU INT8 on the same chip**. For push-to-talk, where captures are
short, it is not faster. It only wins on long audio via chunking. The cost of adopting it: encoder
only, with decoder and mel preprocessing still on CPU; a 631 MB context binary on top of what already
ships; a fixed 16 second window that forces chunk-and-stitch; six helper DLLs where a missing one
crashes with `STATUS_STACK_BUFFER_OVERRUN`; and a binary device-gated to Snapdragon X Elite that will
not run on other Qualcomm parts without recompiling through Qualcomm AI Hub.

AGENTS.md states the rule in one sentence: **"Do not re-derive 'we should use the NPU' from the fact
that one exists."** The real NPU win is power draw, not latency, and the note says to revisit only if
a shorter-window encoder lands.

**🔴 Critical** when the diff routes speech decoding to a non-CPU execution provider, adds NPU or GPU
provider selection to `TranscriptionService` or `VadService`, or introduces chunk-and-stitch machinery
whose stated purpose is NPU adoption, without the PR body citing a fresh measurement that supersedes
the one above.

**Keep the two engines apart.** AI cleanup (Foundry Local) genuinely does use GPU or NPU when one is
available, and Scribe deliberately never chooses: the SDK detects hardware and picks the execution
provider itself. A diff touching cleanup hardware selection is `prompt-and-model`'s or
`architecture-fit`'s business, not this lens's. Do not flag it here, and do not cite the speech-side
CPU rule at it.

## §3. The long-audio myth: three hypotheses, all measured, all wrong

This is the highest-value section of this lens because the failure is real, the obvious fixes are
wrong, and the wrong fix would delete the only evidence anyone has.

A Store user on 0.3.10 reported dictation "cutting out after seven to ten seconds". Their log showed
the opposite: **audio captured fine for all 37 seconds** and the recogniser then returned an **empty
string**. Three of six dictations were lost that way. Three hypotheses were tested against the real
engine with `tools/Scribe.AsrCheck` and all three were refuted (`AGENTS.md:281-306`,
`tools/Scribe.AsrCheck/Program.cs:136-321`):

| Hypothesis | Mode | Result |
| --- | --- | --- |
| Long single-shot decodes collapse | `--long-audio`, 5 s to 90 s | 13.2 to 13.9 chars/s at every length, 90 s included |
| The channel downmix ruins the signal | `--channel-mix` | silent second channel: 100 percent of baseline; foreign second channel: 95 percent |
| Low SNR or room reverb breaks it | `--degraded`, SNR against duration | 0 dB SNR plus heavy reverb at 40 s still decodes at about 13 chars/s |

So: **do not "fix" long-audio decoding, do not rewrite `MonoDownmixSampleProvider`, and do not assume
a noisy room is the cause.** A change whose justification is one of those three, with no new
measurement, is arguing against data that is already in the repository.

**The cause is still unknown, and that is why the instrumentation exists.**
`CaptureSignalAnalyzer` (`src/Scribe.Core/Audio/CaptureSignalAnalyzer.cs:72-89`) was written because
the only thing the log said about the audio was "peak audio was present", which means nothing more
than "not digital silence", a -60 dBFS bar. It now records peak and RMS in dBFS, clipped fraction,
near-silent fraction, DC offset, and **per-channel levels taken before the downmix**
(`AudioCaptureService.cs:205-208` explains why before: once channels are averaged the evidence is
gone). `AudioCaptureService.Stop` logs `LastSignalReport.Describe()` on the capture-complete line
(`AudioCaptureService.cs:217-222`), and `DictationController` repeats it on the empty-decode warning
(`src/Scribe.App/Dictation/DictationController.cs:794-800`).

**🔴 Critical** when the diff removes the per-channel analysis, moves `Analyze` to after the downmix,
drops the signal report from the empty-decode warning path, or deletes `TerseDecodeDetector`
(`src/Scribe.Core/Diagnostics/TerseDecodeDetector.cs`, the second collapse detector). Losing this is
losing the ability to answer the next report of an unexplained failure.

**Statistics only, never audio.** The analyzer carries no samples and no content by design, and
PRIVACY.md promises the log never holds what the user said. **A diff that persists, logs, or
transmits capture samples out of the diagnostic path is 🔴 Critical here and also belongs to
`privacy-egress`.** Flag it, name the egress angle, and let synthesis dedup against that lens.

**VAD-segmented decoding is a legitimate Question, not a rejected idea.** sherpa-onnx documents
`sherpa-onnx-vad-with-offline-asr` for long audio, and Scribe currently feeds the recogniser one
unsegmented span (`TranscriptionService.cs:177-179`). AGENTS.md says explicitly that moving to it
would make a collapse cost one segment instead of the whole dictation, while noting the measurements
say segmentation is not the cause of the reported failure. If the diff moves in that direction, do
**not** flag it as re-deriving a closed decision. Ask instead about segment boundary handling, whether
`VadService.Trim`'s current whole-capture trim contract still holds, and how it is proven with
`--long-audio`.

## §4. The capture path is where audio silently disappears

`AudioCaptureService.OnRecordingStopped` (`src/Scribe.Core/Audio/AudioCaptureService.cs:255-283`)
carries the single most load-bearing comment in this area: **WASAPI ends a stream cleanly with no
exception** when the endpoint is reconfigured mid-capture (an effects pipeline engaging, another app
taking exclusive mode, a Bluetooth profile switch, a driver reset). There is no exception, so
`CaptureFaulted` never fires, and the controller keeps believing it is recording until the user
releases the key. Everything spoken from that moment on is gone.

Nothing downstream can see it, so the only defence is the shape check in `DictationController`
(`src/Scribe.App/Dictation/DictationController.cs:670-691`): on a `HotkeyReleased` stop with a hold
over 2 seconds, a shortfall of more than 1 second between hold duration and captured duration logs a
warning naming the device and the lost seconds.

Flag when the diff:

- **Adds a new capture path or a second `IAudioCaptureService` implementation** that does not raise
  the same warning on an unrequested stream end, or that reports a clean stop for it. 🔴 Critical: the
  new path reintroduces a failure that is invisible everywhere else.
- **Widens or removes the shortfall allowance** without explaining what changed about the stop
  handshake and final buffer flush that the allowance covers.
- **Removes the endpoint mute probe** (`ProbeEndpointMuted`, `AudioCaptureService.cs:375-388`) or the
  `LastCaptureWasSilent` digital-silence check. These distinguish "you spoke while muted" from "no
  speech in the audio", which look identical to the user and need opposite fixes.
- **Changes `ComputePeak` or `IsMeterableFormat`** (`AudioCaptureService.cs:390-452`) without keeping
  them consistent. The seeding at `AudioCaptureService.cs:106-108` deliberately sets the peak above
  the silence threshold for an unmeterable format so `LastCaptureWasSilent` can never false-positive a
  "muted" error. A new format handled by one and not the other breaks that pairing.
- **Changes the VAD window, threshold, or `DetectorBufferSeconds`.** `VadService.cs:14-33` records
  that the buffer size was previously also used to *skip* trimming for captures over 60 s, which meant
  the longest captures were the only ones keeping all their leading and trailing silence. Segment
  offsets are absolute and proved identical at 25 s, 60 s and whole-capture buffer sizes. A diff
  reintroducing a length-conditional trim is 🔴 Critical.

## §5. A green test run proves nothing about the native engine

The unit suite cannot catch a wrongly-packaged native. `TranscriptionServiceTests` and
`TranscriptionAccuracyTests` both return early and pass silently when the model files are absent
(`tests/Scribe.Core.Tests/TranscriptionServiceTests.cs:20-21`,
`tests/Scribe.Core.Tests/TranscriptionAccuracyTests.cs:25-27`). AGENTS.md puts it plainly: the unit
tests deliberately never load sherpa-onnx, **so a wrongly-packaged native passes every test and fails
on the user's first dictation.**

`tools/Scribe.AsrCheck` is the only thing that proves the engine decodes on the silicon just built
for. It exits non-zero on a word-overlap below 0.6 (`tools/Scribe.AsrCheck/Program.cs:29`) and reports
`DllNotFoundException` and `BadImageFormatException` by type, because those are the architecture
regressions it exists to catch (`Program.cs:38-44`). CI runs it on both x64 and `windows-11-arm`
(`.github/workflows/ci.yml:79-84`).

**🟡 Important, or 🔴 when the payload itself changes:** a change to the native package reference, the
model set, the model file names, or the decode configuration, with no AsrCheck run named in the PR
body. "Tests pass" is not evidence here. Do not write "this will fail the build"; the whole point is
that it will not.

Related invariants worth checking on the same diff:

- **Models are pinned by SHA-256 in `scripts/Model-Manifest.ps1`** (lines 2-8), and that file is the
  CI cache key: `key: models-${{ hashFiles('scripts/Model-Manifest.ps1') }}`
  (`.github/workflows/ci.yml:54-59`). A model changed in `Download-Models.ps1` or in
  `TranscriptionModelCatalog` (`TranscriptionModel.cs:53-71`, which carries its own copy of the same
  four hashes) without the manifest moving means CI keeps serving the old weights from cache. A model
  changed in the manifest without the catalog, or the reverse, is a partial conversion: flag the
  survivor.
- **`src/Scribe.App/models` is gitignored** (`.gitignore:16`) and AGENTS.md lists committing the
  downloaded models under "Never". A diff adding weights to the tree is 🔴 Critical.
- **The native runtime is architecture-specific and both packages use the same DLL file names.**
  Exactly one may be referenced, selected by `ScribeNativeRid`
  (`src/Scribe.Core/Scribe.Core.csproj:22-35`, `:60-64`). This is catalog entry **P-12** in
  `references/patterns.md`. If the diff touches it, say so and defer the packaging detail to
  `build-packaging`.
- **Fixture phrases avoid numbers, dates and times on purpose**
  (`scripts/New-SpeechFixtures.ps1:36-45`): Scribe's editorial rules correctly rewrite "three thirty"
  as "3.30", which scores as a mismatch and blunts the 0.6 threshold that is meant to catch a
  genuinely broken native. A new fixture phrase containing a number, date, or time is 🟡 Important.
  CI uses the **committed** WAVs under `tests/fixtures/speech/` because SAPI fails with `0x8004503A`
  on the headless x64 and Arm64 runners, so a diff that makes CI generate fixtures instead of reading
  them is 🔴 Critical.

## §6. Measured, not guessed, applies to decoding options too

`TranscriptionDecoding` (`src/Scribe.Core/Transcription/TranscriptionDecoding.cs`) records that
`modified_beam_search` was measured against **80 real production captures** on Parakeet TDT and was
not more accurate, it was lossy: 19 transcripts changed, including a whole closing sentence
disappearing and a near-silent capture that greedy decoding correctly returned empty coming back as
the invented word "Yeah." `IsBeamSearchSafe` returns `false` for every architecture
(`TranscriptionDecoding.cs:35`), and the comment states that flipping an entry needs a fresh
comparison over **real captures, not synthetic fixtures**, because clean text-to-speech decodes
identically either way.

**🔴 Critical** when the diff makes beam search reachable from the app, flips `IsBeamSearchSafe`, or
removes the greedy fallback for an unrecognized method, without a fresh real-capture comparison.
`TranscriptionOptions.AllowUnsafeDecodingMethod` exists so the regression stays reproducible from
`Scribe.AsrCheck` (`Program.cs:67-79`); removing that escape hatch is 🟡 Important.

Also check the mel dimension. `TranscriptionService.cs:88-97` sets `FeatConfig.FeatureDim = 128` for
NeMo transducers because Parakeet TDT trains on 128 mel bins where sherpa-onnx defaults to 80. The
comment is explicit that the runtime currently repairs this from the model's own metadata, so the
setting is belt and braces today, and that a future reordering reading `FeatureDim` first would
silently produce garbage features rather than fail. Removing it is 🟡 Important, and the hunk should
say what makes it safe now.

---

## Confidence bar

**Hard flag (🔴 or 🟡)** only when the diff itself substantiates it:

- The hunk adds, removes, or changes the named construct, and you can quote the line.
- The rule it violates is written down in `AGENTS.md`, in a `why` comment on the file being changed,
  or in `references/patterns.md`, and you can cite where.
- The failure mode is one this repository has already observed, not one you are reasoning toward.

**Raise as a Question** when:

- The change is plausibly an improvement but its empirical basis is unstated. Ask which AsrCheck mode
  was run and what it returned. That is a cheap, concrete request, not a rhetorical one.
- The change moves toward VAD-segmented decoding, or any other shape sherpa-onnx documents that Scribe
  has not adopted. Legitimate direction, open questions.
- You cannot complete the §0 evidence map. Say which item is missing.

**Stay silent** when a construct in scope simply changed shape without touching an invariant above.
A rename, an extracted helper, or a tightened null check in `Scribe.Core/Audio` is not this lens's
business.

No hedged findings. If "likely", "probably", "seems", or "may be" is load-bearing in your sentence,
it is a Question or it is nothing.

---

## Output format


The findings below are **illustrative shapes**, not live defects. `SpeechLanguage` does not exist on
`AppSettings` or `TranscriptionOptions`, and the model manifest is not out of step with the catalog.
The line numbers point at the live code each regression would have to land in. Never cite the
invented setting as an existing exemplar.

```markdown
## ASR pipeline findings

🔴 **A speech language setting is added; the model has no language parameter** (`src/Scribe.Core/Models/AppSettings.cs:75`, `src/Scribe.Core/Transcription/TranscriptionOptions.cs:12`)

`SpeechLanguage` is persisted and passed into `TranscriptionOptions`, but nothing consumes it:
`TranscriptionService.ConfigureModel` sets only encoder, decoder, joiner and tokens, and
`ModelType` is pinned to `nemo_transducer`. Parakeet TDT v3 is a transducer with roughly 25
European languages baked into its vocabulary and takes no language hint, which is why AGENTS.md:37-40
says not to build this setting. Shipping it gives the user a control that cannot change the output
and an obvious thing to blame when a transcript comes back in the wrong language. Remove the setting;
if the intent was to let the user narrow the model, that is model selection through
`TranscriptionModelId` and the existing catalog, which already offers English-only Moonshine builds.

🟡 **Model weights change with no AsrCheck run and no manifest bump** (`src/Scribe.Core/Transcription/TranscriptionModel.cs:56`, `scripts/Download-Models.ps1:36`)

The encoder URL and SHA-256 move to a new revision, but `scripts/Model-Manifest.ps1` still carries
the old hashes. That file is the CI model cache key (`.github/workflows/ci.yml:59`), so CI will keep
restoring the previous weights and every job will pass against a model this change no longer ships.
The unit suite cannot catch it either: `TranscriptionServiceTests` returns early when the model files
are absent. Update the manifest in the same commit and paste a
`dotnet run --project tools/Scribe.AsrCheck` result into the PR body.

## Questions

- The diff moves decoding onto VAD segments. AGENTS.md treats that as a reasonable direction, since a
  collapse would then cost one segment instead of the dictation. What did
  `dotnet run --project tools/Scribe.AsrCheck -- --long-audio` report before and after, and does
  `VadService.Trim` still return one contiguous span for its existing callers?
```

If clean: `ASR pipeline clean: no language parameter introduced, speech decode stays on the CPU, capture instrumentation and the unrequested-stop warning intact, native and model changes carry an AsrCheck run.`

Trim that line to what you actually checked. Do not claim coverage of a stage the diff did not touch.

---

## Exceptions

Do not flag any of the following.

- **Model selection is not a language picker.** `TranscriptionModelId` (`AppSettings.cs:74`), the
  curated catalog, and `TranscriptionModelInstaller` are a shipped feature. Adding a curated model,
  or surfacing its `Languages` display string, is normal work.
- **Opt-in local audio history is a deliberate feature.** `AppSettings.StoreAudioHistory`
  (`AppSettings.cs:261-262`) defaults to off, is surfaced in Settings, is reported in the session
  banner (`src/Scribe.Core/Diagnostics/SessionBanner.cs:207`), and is documented in PRIVACY.md. Work
  on that path is not the "never persist audio" violation. That rule is about the **diagnostic** path:
  the log, the analyzer, `DiagnosticsBundle`, and anything leaving the machine.
- **The channel warnings in `WarnAboutSignalProblems`** (`AudioCaptureService.cs:305-347`) are
  diagnostics, not a claim that the downmix causes lost dictations. Extending or refining them is
  fine. Rewriting `MonoDownmixSampleProvider` on the theory that averaging is the root cause is not,
  because `--channel-mix` measured a silent second channel at 100 percent of baseline.
- **CPU thread tuning is not a hardware-provider change.** `TranscriptionOptions.NumThreads` and
  `ResolveThreadCount` (`TranscriptionService.cs:194-198`, half the logical processors capped at 8)
  are ordinary tuning knobs. Only a change of execution provider engages §2.
- **The warm-up decode** (`TranscriptionService.cs:134-161`) is deliberately best-effort and swallows
  its own failure. That is correct: a warm-up failure must never stop the recognizer being used. Do
  not flag the empty-ish catch there.
- **`--channel-mix` and `--degraded` returning 0 unconditionally** (`Program.cs:254`, `:308`) is
  intentional. Those modes are characterisation, not gates; the comments say a threshold would only
  encode whatever that machine did on the day it was written. `--long-audio` does gate, because a
  total collapse is unambiguous.
- **AI cleanup hardware selection.** Foundry Local picks its own execution provider and Scribe only
  reports the choice. Out of scope here; see `prompt-and-model`.
- **Packaging mechanics.** `ScribeNativeRid`, `RuntimeIdentifiers`, `PlatformTarget`, and
  `Payload-Architecture.ps1` belong to `build-packaging` and P-12. Note the interaction if the diff
  touches the native package, then hand it over rather than duplicating the finding.
- **A genuinely new measurement.** If the PR body cites an AsrCheck run, a fixture sweep, or a
  production-log analysis that contradicts a rule above, the rule loses. AGENTS.md's positions are
  measurements, not doctrine, and they were written to be superseded by better measurements. Say so
  plainly rather than quoting the old number at new data.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:asr-pipeline findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
