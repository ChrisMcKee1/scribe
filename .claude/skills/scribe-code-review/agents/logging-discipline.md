# Logging discipline review lens

You answer one question the per-file lenses miss: **is logging still non-throwing end to end, bounded,
detailed enough to debug an intermittent field bug, and incapable of reaching a destructive path?**

`AGENTS.md` states the mandate as non-negotiable: *"Logging is how we debug the hard, intermittent bugs
in this app: it must never be the cause of one."* (`AGENTS.md:240-253`). This lens is the enforcement of
that sentence plus the "what the log has to contain" contract added in 0.3.11 (`AGENTS.md:255-279`).

**Dispatch when the diff touches** `src/Scribe.App/Infrastructure/FileLoggerProvider.cs`,
`LogTraceProcessor.cs`, `SessionDiagnostics.cs`, `TelemetryRegistration.cs`,
`src/Scribe.Overlay/Logging/**`, any of
`src/Scribe.Core/Diagnostics/{SessionBanner,LogRetentionPolicy,ScribeLogFiles,ScribeTelemetry,DiagnosticsBundle}.cs`,
**or** the diff adds any logging or telemetry call inside a `catch` block anywhere in the tree.

Severity cap: 🔴 Critical. Findings cap: **5**.

**Data on disk.** `diff.patch` is authoritative for what the change does. Read it, plus `metadata.json`
when present. The reviewed branch may not be checked out, so never use Read or Grep to confirm a diff
line exists on disk. Do use Read and Grep freely for surrounding context: the two writers, the callers of
a log line, the sibling implementation in the other process, and the long `why` comments that record the
incidents these shapes exist to prevent.

**The rubric this lens shares with `architecture-fit`:** `references/patterns.md` **P-4, Diagnostics that
cannot take down the thing they describe**, and its privacy half **P-8**. Cite the pattern number when a
finding is a P-4 or P-8 break.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, confirm you can name each of the following. If you cannot, say the gap
instead of concluding. A logging verdict built on an unread `catch` block is exactly how a confidently
wrong review happens here, because the correctness of this area lives in the code three lines *around* the
hunk, not in the hunk.

1. **Which writer.** App side is `FileLoggerProvider.Append`
   (`src/Scribe.App/Infrastructure/FileLoggerProvider.cs:86`). Overlay side is `OverlayLog.Write`
   (`src/Scribe.Overlay/Logging/OverlayLog.cs:53`). A third writer appearing in a diff is itself the
   finding, see §1.
2. **Which process.** `Scribe.Overlay` deliberately has no reference to `Scribe.Core`
   (`src/Scribe.Core/Diagnostics/ScribeLogFiles.cs:8-15`), so a Core helper is not reachable from the
   overlay and `OverlayLog` carries its own copy of the file-name convention. A change to one side that
   assumes the other side can call it is wrong on its face.
3. **The enclosing `catch`, if any, and what that `catch` does.** Read it. If it kills a process, tears
   down a window, disposes a pipe, or disables a feature, §2 applies and the bar is 🔴.
4. **Call frequency.** Per session, per dictation, per pipe command, or per audio buffer. This decides
   whether §4 applies.
5. **What the line or tag actually carries.** A count, a boolean, an enum name, a duration, a device
   name, a target app name, and `configured` / `unset` are in contract. Text the user dictated or
   authored, a prompt, an endpoint, or a key is not. See §6.
6. **Current `main` behavior for the same path.** A line that looks new may be a move, and a line that
   looks removed may have moved to a better place.

---

## §1. The writer contract: share, retry, swallow, never propagate

Both processes append to the **same** daily file, `%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log`,
so dictation and overlay events interleave on one timeline. That is the whole reason the sharing rules
exist.

The two blessed writers, and the exact shape a new one must match:

- `FileLoggerProvider.Append` (`src/Scribe.App/Infrastructure/FileLoggerProvider.cs:86-163`): a bounded
  loop of **12 attempts, `Thread.Sleep(15)` apart**, opening with
  `new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)` (line 142), catching
  `IOException` (line 154) and `UnauthorizedAccessException` (line 158), and falling off the end of the
  loop without rethrowing. Above it, `FileLogger.Log` wraps formatting and the call to `Append` in a
  `try` with a bare `catch` (lines 219-233), so even a formatter that throws cannot reach the caller.
- `OverlayLog.Write` (`src/Scribe.Overlay/Logging/OverlayLog.cs:53-91`): the same 12 by 15 ms loop and the
  same `FileShare.ReadWrite` (line 70), the same two typed catches (lines 78 and 81), wrapped in an
  **outer** `try` with a bare `catch` (line 87) that also covers `Path` resolution, because
  `Directory.CreateDirectory` can throw before any retry loop is reached.

Flag 🔴 Critical when the diff introduces any of these:

- **A new log writer that does not share and retry.** A `File.AppendAllText`, a `StreamWriter` opened
  without `FileShare.ReadWrite`, or a single-attempt write against the shared daily file. `AGENTS.md:222`
  carries this exact snippet as the house style, and `ScribeLogFilesTests.Prune_leaves_a_file_another_process_holds_open`
  (`tests/Scribe.Core.Tests/ScribeLogFilesTests.cs:89`) pins the sibling half of the same contract.
- **A retry loop that rethrows on the last attempt.** `throw;` in the final `catch`, an `if (attempt ==
  last) throw`, or a `catch` that re-wraps into a custom exception. The loop must exhaust and return.
- **A narrowed catch on an existing writer.** Removing the `UnauthorizedAccessException` arm, removing the
  outer bare `catch` in `OverlayLog.Write`, or removing the bare `catch` in `FileLogger.Log`. Each of
  those was added because something reached the caller.
- **A logging path that becomes `async void`, or that awaits without its own guard**, so a fault surfaces
  on a thread pool thread rather than being swallowed at the write.
- **A new diagnostics helper that throws by design and is then called from a hot path.**
  `DiagnosticsBundle.Create` (`src/Scribe.Core/Diagnostics/DiagnosticsBundle.cs:45`) is the one deliberate
  exception and its own summary says why: it runs because a person pressed a button, so a silent no-op
  would be worse than an error message. That exemption does not generalize to anything the pipeline calls.

Raise as a **Question**, not a finding, when the retry count or the sleep interval changes without a
stated reason. 12 and 15 ms are not magic numbers with a test behind them, so a considered change is the
author's call, but an unexplained one is worth asking about.

## §2. A logging failure must never reach a destructive path

This is the highest-value rule in the lens because it has already cost this project a shipped bug.

**The canonical incident**, recorded in the code itself at
`src/Scribe.App/Overlay/OverlayProcessClient.cs:349-352`: the "overlay process launched" log line used to
sit **inside** the `try` that wraps launch and pipe connect. A transient lock on the shared log file threw
there, the surrounding `catch` read the throw as a launch failure, and `KillProcess()` (line 386) tore
down a perfectly healthy overlay. That was a root cause of the intermittent "pill disappears"
regressions. The fix was to move the line out of the `try` and route it through
`OverlayProcessClient.TryLog` (line 362), a local helper whose only job is to swallow: its comment states
that nearby catches *"treat any throw as an overlay failure and respond destructively with
KillProcess()"*.

`ResilientEvent.InvokeAll` (`src/Scribe.Core/Infrastructure/ResilientEvent.cs:28-40`) applies the same
reasoning one level up: it wraps its own `onError` callback in a nested `try` because *"a logger that
throws must not stop the fan-out it was only meant to describe."*

Flag 🔴 Critical when the diff:

- **Adds a raw `_log.Log*(...)` or `OverlayLog.*(...)` call inside a `try` whose `catch` does something
  destructive**: kills or disposes a process, disposes a pipe or writer, hides or closes a window,
  disables a feature, or clears state. Name the destructive statement in the finding.
- **Adds a diagnostic call inside a destructive `catch` block itself** without routing it through a
  non-throwing helper. Inside the `catch` the throw does not get another chance to be handled.
- **Replaces a `TryLog` call with a direct logger call**, or deletes a `TryLog` helper and inlines its
  body without the guard.
- **Moves a log line back inside a launch, connect, or teardown `try`.** This is the exact regression
  shape. Treat a hunk that widens a `try` to enclose a pre-existing log line as the same defect.
- **Adds an error callback, event fan-out, or span processor that logs without wrapping the log call.**

Flag 🟡 Important, not 🔴, when the enclosing `catch` is merely lossy rather than destructive: it returns
a default, skips a step, or shows a message. Still worth fixing, but it does not tear anything down.

**Scope note.** The trigger for this lens includes "the diff adds any logging or telemetry call inside a
catch block anywhere", which is deliberately wide. Most such calls are fine. Only the destructive ones are
findings. Do not report a per-catch inventory.

## §3. Detail is the product, so removing it is a finding

`AGENTS.md:253` is explicit: *"When in doubt, log more lifecycle and state detail, not less."* This is a
tray app with no console, users report problems days later, and the failures that matter are intermittent
and hardware specific. The file sink runs at `Debug`
(`src/Scribe.App/App.xaml.cs:115`) with `Microsoft`, `System` and `Azure` filtered to `Warning`
(lines 119-121) precisely so pipeline detail survives and framework chatter does not bury it.

Treat these as regressions in their own right:

- **Raising the sink's minimum level** above `Debug`, or removing the framework filters so that framework
  chatter returns and buries the pipeline events. Either changes what a support log can answer.
- **Deleting or downgrading a session banner field.** `SessionBanner.Compose`
  (`src/Scribe.Core/Diagnostics/SessionBanner.cs:69-131`) is written from `SessionDiagnostics.Compose`
  (`src/Scribe.App/Infrastructure/SessionDiagnostics.cs:83`) and carries session id and pid, version,
  install channel, package family, OS, arch, runtime, cores, RAM, the resolved paths including the
  virtualization notice, the model **and whether its files are actually on disk**, audio devices, and the
  hotkey, pipeline, cleanup and injection settings. Every one of those answers a first support question
  without a round trip. Removing one is 🟡 Important; removing the model-completeness or the paths block
  is 🔴, because those are the two that closed the 0.3.10 dead end.
- **Breaking the session bookends.** `SessionBanner.StartMarker`
  (`src/Scribe.Core/Diagnostics/SessionBanner.cs:62`) opens the story and `OnExit` writes the matching
  `===== Scribe session end =====` line (`src/Scribe.App/App.xaml.cs:1234`). The **absence** of the end
  line before the next banner is the signal that the process died, so a change that makes the end line
  conditional, best-effort in a way that silently skips it, or emitted on a crash path too, destroys the
  one crash indicator the log has. 🔴.
- **Dropping the one-line-per-entry banner shape.** `SessionDiagnostics.WriteBanner`
  (`src/Scribe.App/Infrastructure/SessionDiagnostics.cs:36-52`) logs each line separately with the comment
  that the log is read with grep as often as with an editor. A change to one multi-line message breaks
  every line-oriented tool. 🟡.
- **Dropping the dictation stamp or the stop reason.** Every dictation is stamped `#<n>` from
  `_dictationId` (`src/Scribe.App/Dictation/DictationController.cs:447`), logs its start with trigger,
  mode, key, device and target app (line 473), and logs its stop **with a reason** and the hold duration
  (line 608), where the reason is one of `HotkeyReleased`, `SilenceAutoStop`, `MicrophoneFault`, `Paused`
  (`DictationStopReason`, line 558). A hold that ends early and a toggle that auto-stops look identical to
  the user and have completely different causes, so an unstamped or reasonless stop line is 🔴.
- **A new stop or failure path that does not log a reason.** Adding a value to `DictationStopReason`, or a
  new early return in the dictation loop, without a line that says which one fired, leaves the same blind
  spot. 🟡, or 🔴 if the new path can silently swallow a dictation.
- **Removing a `why`-bearing warning.** For example the capture-shortfall warning at
  `src/Scribe.App/Dictation/DictationController.cs:684`, which exists because WASAPI ends a stream cleanly
  with no exception when the endpoint is reconfigured mid capture and nothing else in the pipeline can see
  it. Deleting a line like that removes the only observer of a silent fault.

Raise as a **Question** when a line is reworded or its fields are reordered but the same facts survive.
That is not a finding.

## §4. Bounded: retention, budget, and volume

Detail is only affordable because the folder is bounded. `LogRetentionPolicy`
(`src/Scribe.Core/Diagnostics/LogRetentionPolicy.cs`) sets the three numbers: **7 days**
(`DefaultRetentionDays`, line 29), **16 MB per day** (`DefaultDailyBudgetBytes`, line 42), **64 MB total**
(`DefaultTotalBudgetBytes`, line 36). `SelectForDeletion` (line 52) sweeps by age first, then oldest-first
by size, and **never selects today's file** (lines 90-96), because that is the one file a user reporting a
problem right now actually needs. `ScribeLogFiles.Prune`
(`src/Scribe.Core/Diagnostics/ScribeLogFiles.cs:105`) applies it and is non-throwing throughout. It runs at
**startup** (`FileLoggerProvider` constructor, line 57) and at **each midnight rollover** (line 120). Past
the daily budget the app-side writer degrades to warnings and errors only and emits a one-time notice
explaining why (`ShouldDropForDailyBudget`, lines 170-190). PRIVACY.md:102-106 promises the user all of
this, so a change here is also a docs-sync concern.

Flag when the diff:

- **Adds a new sink or a new log file** that the retention sweep cannot see. `ScribeLogFiles.SearchPattern`
  is `scribe-????????.log` (line 20) and `TryParseDay` (line 38) rejects anything outside the convention
  so a sweep can never delete a file it did not write. A file named outside that pattern is retained
  forever. 🔴.
- **Adds a high-frequency log line** on a per-buffer, per-meter-tick, or per-poll path. The live precedent
  is explicit: **METER pipe commands are deliberately not logged**
  (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:15`), and the client throttles them before sending
  (`src/Scribe.App/Overlay/OverlayProcessClient.cs:24`). 🟡, or 🔴 when the line rides the audio callback.
- **Adds a high-frequency line on the overlay side specifically.** Worth its own note:
  `OverlayLog.Write` has **no** level filter and **no** daily-budget degrade of its own, and the overlay
  never prunes. The app-side budget only drops app-side lines; it re-stats the file every
  `SizeRecheckInterval` writes (`FileLoggerProvider.cs:24`, line 122) so overlay bytes are counted in the
  app's decision, but the overlay itself keeps writing at full volume. A chatty new overlay line therefore
  crowds out the app's warnings rather than being degraded alongside them.
- **Weakens a bound**: raising a constant, disabling the size sweep, skipping the startup or midnight
  prune, or making today's file eligible for deletion. Each has a test in
  `tests/Scribe.Core.Tests/LogRetentionPolicyTests.cs`; a change here that does not also update those
  tests is either wrong or under-covered. 🔴 for today's file, 🟡 otherwise, and cross-reference
  `guardrail-erosion` if the change came with a test deletion.
- **Pins the daily file at construction instead of per write.** `FileLoggerProvider.Append` rotates on
  every write (lines 107-121) with a comment saying why: the tray app runs for days, and a launch-day file
  pinned at construction diverges from the overlay's properly rotated file at midnight and splits the
  shared timeline the logs exist for. 🔴.

## §5. Tracing stays opt-in, and every tag lands in the log file

`ScribeTelemetry` (`src/Scribe.Core/Diagnostics/ScribeTelemetry.cs`) is the single `ActivitySource`,
named `Scribe.Dictation` (line 17), with two spans, `dictation.process` (line 20) and `text.inject`
(line 23), and tag names in `snake_case` under a `scribe.` namespace (lines 26-50). Spans are only created
when a listener subscribes, so tracing is nearly free when off. `TelemetryRegistration.AddScribeTelemetry`
(`src/Scribe.App/Infrastructure/TelemetryRegistration.cs:20-38`) adds the source, always bridges it to the
file log through `LogTraceProcessor`, and adds an OTLP exporter **only** when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set, so no backend means no connection-error spam.

The load-bearing consequence, and the one this lens exists to catch:
`LogTraceProcessor.OnEnd` writes **every tag value verbatim** into the log line
(`src/Scribe.App/Infrastructure/LogTraceProcessor.cs:32`:
`builder.Append(' ').Append(key).Append('=').Append(tag.Value)`). There is no allowlist and no
redaction. **A new tag carrying content is a new content leak to disk**, automatically, with no further
code change. Every existing tag is a shape: `TagDecodeChars` and `TagFinalChars` are `.Length` values,
`TagCaptureSeconds` and `TagRealTimeFactor` are rounded numbers, `TagVadKept` and `TagAiChanged` are
booleans, `TagAiOutcome` and `TagOutcome` are enum-shaped constants (`DictationOutcome`, line 59), and
`TagTargetApp` is an app name. Confirm at the `SetTag` callsites in
`src/Scribe.App/Dictation/DictationController.cs:656-979` and
`src/Scribe.Core/TextInjection/TextInjector.cs:58`.

Flag when the diff:

- **Adds a tag whose value is text rather than a shape.** 🔴, and hand it to `privacy-egress` as well;
  that lens owns the egress contract, this one owns the fact that it also hits the local file.
- **Adds a tag outside the `scribe.` prefix or outside `snake_case`.** `LogTraceProcessor` strips exactly
  `scribe.` (line 12) when rendering, so an off-prefix tag renders with its full key and reads as a
  foreign field. 🟡.
- **Adds an OTLP exporter, a second processor, or an always-on exporter** that is not gated on the
  environment variable. 🔴 if anything leaves the machine unconditionally; route to `privacy-egress`.
- **Adds work to `LogTraceProcessor.OnEnd` before the logger call.** Today `OnEnd` (lines 24-47) is
  non-throwing only because the underlying `FileLogger.Log` swallows everything; `OnEnd` itself has no
  guard, and it runs inside the `using var activity` disposal in the dictation loop. New formatting,
  parsing, or IO added ahead of the `_log.Log*` call is unprotected. 🟡, and say exactly that: the safety
  here is borrowed from the sink, not owned by the processor.
- **Starts an activity on a path that runs when no dictation is happening**, which turns "nearly free when
  off" into a per-tick cost. Raise as a Question unless the frequency is obvious from the diff.

## §6. Log privacy is a contract, not a habit

`AGENTS.md:274-276` and `PRIVACY.md:94-100` state it as a promise to the user: no transcripts, no
dictionary entries, no snippet bodies, no prompts, no endpoints, no keys. Report shapes instead: counts,
enum names, `configured` or `unset`. `SessionBanner` implements it (see `DescribeCleanup`,
`src/Scribe.Core/Diagnostics/SessionBanner.cs:209-234`, whose `Presence` helper collapses a value to
`configured` or `unset`), and `SessionBannerTests.Banner_never_contains_a_secret`
(`tests/Scribe.Core.Tests/SessionBannerTests.cs:56`) asserts it against a real API key, a client secret, a
tenant-bearing endpoint, a writing style and a local prompt. **That test must keep passing.**

Flag 🔴 Critical when a new banner field, log line, or report line carries an endpoint address, a key or
secret, a prompt, a writing style, a dictionary entry, a snippet body, or transcript text. Flag 🟡 when a
new field is a borderline identifier that should be reduced to a presence flag or a count.

**Boundary with `privacy-egress`.** That lens owns what leaves the machine and holds the fail-closed
contract (P-8). This lens owns what lands in the local log file, the banner, the trace bridge and the
exported bundle. When both fire on one line, synthesis keeps the more specific one; `privacy-egress`
outranks this lens in the dedup order, so defer to it on egress and keep the local-file angle as the
cross-reference.

## §7. The diagnostics bundle never contains the database

Users export logs from Settings, About, "Save diagnostics", which calls `DiagnosticsBundle.Create`
(`src/Scribe.Core/Diagnostics/DiagnosticsBundle.cs:45`) from
`src/Scribe.App/Settings/SettingsWindow.xaml.cs:2830-2836`. The zip contains the retained log files plus a
`report.txt` (line 70) that tells the user in plain language what is inside, because the bundle is meant to
be attached to a public issue.

**`scribe.db` is never added.** It holds every dictation the user has ever made and their saved API keys
(`DiagnosticsBundle.cs:20-25`, `AGENTS.md:277-279`, `PRIVACY.md:107-112`). Flag 🔴 Critical for anything
that adds the database, the settings store, a credential file, or a captured audio file to the bundle, and
for anything that widens the bundle beyond the logs folder, which
`DiagnosticsBundleTests.Bundle_never_reaches_outside_the_logs_folder`
(`tests/Scribe.Core.Tests/DiagnosticsBundleTests.cs:50`) pins.

Also flag when the diff:

- **Changes the bundle's read to a non-sharing open.** Line 81 opens each log with
  `FileShare.ReadWrite` because both processes hold the file open for append in bursts, and `File.Copy`
  would fail exactly when the app is busy, which is when a user is most likely to be exporting. 🟡.
- **Removes the per-file `catch` at line 86** that turns one unreadable day into an `.unreadable.txt`
  entry instead of costing the user the other six. 🟡.
- **Changes what the bundle contains without updating the `report.txt` preamble**
  (`src/Scribe.App/Infrastructure/SessionDiagnostics.cs:64-68`), which enumerates what is and is not in
  the file. The user reads that before sharing, so a drifted preamble is a broken promise. 🟡, and
  cross-reference `docs-sync` for the matching PRIVACY.md paragraph.

---

## Confidence bar

**Hard-flag** only when you can point at the hunk and name the mechanism. Specifically:

- 🔴 requires: the diff line, the enclosing construct you read (the `catch` and its destructive statement,
  the missing `FileShare.ReadWrite`, the tag whose value is content, the added `scribe.db` entry), and a
  one-sentence failure story that does not use "likely", "probably", "seems" or "may be".
- 🟡 requires the same evidence for a non-destructive consequence: lost detail, an unbounded line, a
  drifted document, a borrowed rather than owned guard.
- **Raise a Question instead** when the concern depends on something the diff does not show: the real
  call frequency of a new line, whether a reworded line still carries the same fields, whether a changed
  retry constant was deliberate, or whether a new tag's value is a shape or content when the producing
  expression is outside the patch.

**Do not flag** on suspicion that a change "could" throw without naming what throws. Do not write "this
will fail the build" or "the tests will catch it"; this repository has shipped three defects that compiled
warning clean, so that claim carries no weight in either direction.

If the diff only moves logging code without changing its guards, its level, its fields, or its frequency,
say so and emit the clean-pass line.

---

## Output format


The findings below are **illustrative shapes**, not live defects. The "overlay ready" line and the
per-buffer capture line are invented; `OverlayProcessClient` already logs its launch confirmation
below the `try` through `TryLog`. The line numbers point at the live code a real regression would
have to touch.

```markdown
## Logging discipline findings

🔴 **Overlay teardown log sits inside the launch `try`, so a log-file lock reads as a launch failure** (`src/Scribe.App/Overlay/OverlayProcessClient.cs:341`)

The new "overlay ready" line was added inside the `try` that wraps `Process.Start` and `_pipe.Connect`.
The shared daily log is appended to by both processes, so a transient lock throws here, the `catch` at
line 334 treats any throw as a launch failure, and `KillProcess()` runs against a healthy overlay. This is
the exact shape of the "pill disappears" regression the comment at line 349 records. Move the line below
the `try` and route it through `TryLog` like the launch-confirmation line already does (P-4).

🟡 **New per-buffer capture line is unbounded and unbudgeted on the overlay side** (`src/Scribe.Overlay/OverlayWindow.xaml.cs:212`)

`OverlayLog.Write` has no level filter and no daily-budget degrade, so this line writes at full rate for
the whole session and crowds the app's warnings out of the 16 MB daily budget. METER commands are
deliberately not logged for this reason (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:15`). Log the state
transition once rather than each meter tick, or gate it behind an explicit diagnostic flag.
```

If clean: **"Logging discipline clean: writers still share, retry and swallow, no diagnostic sits on a
destructive path, the banner and dictation detail are intact, retention stays bounded, and no new log line
or telemetry tag carries content."**

---

## Exceptions

Do not flag any of the following. Each is a deliberate, load-bearing shape in this repository.

- **`DiagnosticsBundle.Create` throwing.** Its summary (`src/Scribe.Core/Diagnostics/DiagnosticsBundle.cs:36-39`)
  says why: it runs because a person pressed a button, and the caller shows the error
  (`src/Scribe.App/Settings/SettingsWindow.xaml.cs:2843-2846`). A silent no-op would be worse. This is not
  a violation of the non-throwing mandate, which is about the logging path.
- **`OverlayLog` duplicating the file-name convention.** `Scribe.Overlay` has no reference to
  `Scribe.Core` on purpose, so it cannot call `ScribeLogFiles`. Do not propose extracting a shared helper.
  Do flag a change to the pattern on one side only, since that silently splits the two processes onto
  different files.
- **The bare `catch` blocks in the logging and retention paths.** `FileLogger.Log`, `OverlayLog.Write`,
  `ScribeLogFiles.Enumerate` and `ScribeLogFiles.Prune` all swallow by design and say so in comments. A
  general "empty catch block" objection is noise here; the mandate requires them.
- **`ShouldDropForDailyBudget` letting warnings and errors through past the cap.** That is deliberate
  (`src/Scribe.App/Infrastructure/FileLoggerProvider.cs:165-169`): past the cap the interesting lines are
  exactly the ones that would otherwise be crowded out.
- **The best-effort, approximate `_dayBytes` accounting.** The comment at
  `src/Scribe.App/Infrastructure/FileLoggerProvider.cs:33-35` states that the overlay appends to the same
  file so the app's own byte count is always a lower bound, and that it only has to be close enough to
  catch a runaway. Do not propose exact accounting.
- **Verbose overlay lifecycle logging.** `src/Scribe.Overlay/App.xaml.cs` and `OverlayWindow.xaml.cs` log
  constructor entry and exit, presenter configuration, extended styles, DWM results, size and position,
  and every state transition. AGENTS.md:328 tells a maintainer to verify overlay changes by looking for
  those exact lines. Volume there is the feature, not a nit. Only a genuinely per-tick line is a finding.
- **A device name, a focused-application name, or a model or provider identifier in the log.**
  PRIVACY.md:94-100 discloses all three explicitly. They are in contract.
- **Log levels chosen per line.** Whether one line is `Debug` or `Information` is a style call. Only a
  change to the sink's minimum level or to the framework filters is in scope.
- **Existing code the diff did not touch.** This lens reviews the change, not the file.
- **Test-only logging helpers** under `tests/**` and `tools/**` that do not write to the shared daily
  file.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:logging-discipline findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
