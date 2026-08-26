# Overlay process contract review lens

You answer one question the per-file lenses miss: **does this change keep the out-of-process WinUI
overlay contract intact?** That contract has four load-bearing parts: the pill stays a separate
process rendering through DWM composition, the one-way pipe stays a one-way pipe, the twin enums stay
in sync by name, and the pill can never outlive the engine.

**Dispatch trigger.** The diff touches `src/Scribe.Overlay/**`, `src/Scribe.App/Overlay/**`,
`src/Scribe.Core/Infrastructure/OverlayExecutableSelector.cs`, the `OverlayPosition` enum in
`src/Scribe.Core/Models/Enums.cs`, or the overlay payload steps in `build/pack.ps1`; **or** the diff
text mentions a pipe command token (`RECORDING`, `WARNING`, `PROCESSING`, `FAILED`, `HIDE`, `METER`,
`POSITION`, `WARMUP`, `EXIT`), `AllowsTransparency`, `UpdateLayeredWindow`, `SystemBackdropElement`,
`TransparentBackdrop`, or a Job Object API (`CreateJobObject`, `SetInformationJobObject`,
`AssignProcessToJobObject`).

Severity cap: 🔴 Critical. Findings cap: **5**.

**Data on disk.** Read `diff.patch` (and `delta.patch` on a re-review) plus `metadata.json` from the
cache. The reviewed branch may not be checked out, so the patch is authoritative for what changed.
Use Read and Grep freely for surrounding context: this contract spans two projects that share no
compile-time reference, so the evidence for a break is almost never inside a single hunk.

**Rule zero: no automated gate exists for most of this.** `tests/Scribe.Core.Tests` references only
`Scribe.Core` (`tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj`), so no xUnit test can compare
`OverlayPosition` against `OverlayAnchor`, and no test loads the WinUI process at all. The only Core
type on this path with tests is `OverlayExecutableSelector`. Everything else in this lens is gated by
review or by nothing. Never close a finding here with "a test would have caught it".

---

## §0. Evidence map before any verdict

Before you flag or clear, confirm you can name each of the following. If one is missing, say the gap
instead of concluding.

1. **Which side changed**: the engine (`src/Scribe.App/Overlay/OverlayProcessClient.cs`), the overlay
   (`src/Scribe.Overlay/**`), the shared-by-name enum pair, or the packaging that puts the exe on
   disk.
2. **The matching side.** For every verb the engine sends, the `case` in
   `OverlayIpcServer.Dispatch` (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs`, around line 87). For
   every `case`, the `Enqueue` or `WriteLine` that sends it.
3. **The full by-name surface** for anything anchor-shaped (Section 2). There are four places, not
   two.
4. **The lifecycle path**: launch, connect, replay, reconnect, teardown. A change to one of these is
   usually a change to all five.
5. **The `why` comment.** Nearly every shape on this path carries a comment recording the incident it
   prevents. A hunk that deletes one of those comments is a finding on its own, because the next agent
   will then "simplify" the shape.

---

## §1. The pill is a separate process, and that is not negotiable

**The incident.** The recording pill was a WPF window. On .NET 10, WPF `AllowsTransparency` plus
layered-window rendering (`UpdateLayeredWindow`, dotnet/wpf #11321) intermittently painted an opaque
black box over the pill, or made it vanish. That is the long-recurring "black box / pill disappears"
bug. The permanent fix was to move the pill into a standalone WinUI 3 process that renders through DWM
composition: `TransparentBackdrop` (`src/Scribe.Overlay/TransparentBackdrop.cs:23`) is a custom
`SystemBackdrop` that fills the system-backdrop region with an alpha-0 composition color brush, which
never touches the legacy layered path.

`AGENTS.md` lists this under **Never**: *"Reintroduce a WPF transparent/layered-window pill, or revert
the overlay to in-process; that bug is solved by the out-of-process WinUI 3 design."*

**Flag 🔴 Critical, no discussion, no framing as a Question**, when the diff:

- Adds `AllowsTransparency` to any WPF window that renders the pill, or adds a `UpdateLayeredWindow`
  P/Invoke anywhere.
- Adds a WPF implementation of `IOverlayController`
  (`src/Scribe.App/Overlay/IOverlayController.cs:12`) that draws the pill in the WPF process, or
  rewires the composition root to use one.
- Removes the `SystemBackdrop = new TransparentBackdrop()` assignment in the `OverlayWindow`
  constructor (`src/Scribe.Overlay/OverlayWindow.xaml.cs`, around line 64) without replacing it with
  another DWM-composition backdrop.
- Deletes `src/Scribe.Overlay/TransparentBackdrop.cs`, or changes its brush source from a
  `Windows.UI.Composition.Compositor` to the `Microsoft.UI` element compositor. The file's own comment
  records why the system compositor is required for
  `ICompositionSupportsSystemBackdrop.SystemBackdrop`.

Write the finding as a boundary crossing, not as an opinion: name the `AGENTS.md` **Never** entry, name
the bug it prevents, and tag it `[architecture-shortcut]` so `maintainer-decision` picks it up. Do not
soften it because the diff has a plausible reason. The reasons were plausible the last three times too.

**A note on terminology.** `AGENTS.md` line 313 writes the fix as
"`SystemBackdropElement`/`TransparentBackdrop`". Only `TransparentBackdrop` exists as a symbol in this
repo. Do not cite `SystemBackdropElement` as code; cite `TransparentBackdrop` and the
`Microsoft.UI.Xaml.Media.SystemBackdrop` base class it derives from.

---

## §2. The twin enums, kept in sync by name

**This is the single highest-value check in this lens.** The wire token for the pill's anchor is the
enum *value name*. The overlay deliberately holds no reference to `Scribe.Core`
(`src/Scribe.Overlay/Scribe.Overlay.csproj` has exactly one `PackageReference`, to
`Microsoft.WindowsAppSDK`, and no `ProjectReference`), so the compiler cannot see the two enums at
once. Add or rename a value in one and not the other and the overlay silently ignores the command,
with only `OverlayIpcServer POSITION with unknown anchor '<name>'` in the log
(`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:129`).

The nine values, identical in both, in this order:
`TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight`.

**There are four by-name surfaces, not two.** A complete anchor change touches all four:

| # | Surface | Where |
| --- | --- | --- |
| 1 | `Scribe.Core.Models.OverlayPosition` | `src/Scribe.Core/Models/Enums.cs:29` |
| 2 | `Scribe.Overlay.OverlayAnchor` | `src/Scribe.Overlay/OverlayAnchor.cs:8` |
| 3 | The picker's `Tag` strings | `src/Scribe.App/Settings/SettingsWindow.xaml`, the `OverlayPositionGrid` `RadioButton` list, around line 613. `SelectedOverlayPosition` reads them back with `Enum.TryParse<OverlayPosition>((string)zone.Tag, ...)` (`src/Scribe.App/Settings/SettingsWindow.xaml.cs`, around line 4581) |
| 4 | The geometry switch arms | `OverlayWindow.SizeAndPosition` (`src/Scribe.Overlay/OverlayWindow.xaml.cs`, around line 145) |

Surface 4 is the one people miss after they remember surfaces 1 and 2. Both switch expressions there
end in a `_ =>` catch-all: x falls through to horizontally centered, y falls through to bottom. A new
anchor added to both enums but not to the switch arms therefore does not fail, it silently renders as
`BottomCenter`. Same for surface 3: `SelectedOverlayPosition` returns `OverlayPosition.BottomCenter`
when no tag parses, so a typo'd `Tag` degrades to the default instead of erroring.

**Confidence bar for this section.** A diff that changes the member list of one enum and not the other
is a **hard 🔴** with no hedging: the mechanism is fully determined and you can point at the exact
missing line. A diff that changes only surface 3 or surface 4 out of step with the enums is a hard 🟡
with the same certainty. Do not raise this as a Question; there is nothing to ask.

**Also flag** a diff that adds a `Scribe.Core` `ProjectReference` to `Scribe.Overlay.csproj` "to keep
the enums in sync". That inverts the documented boundary: the overlay is a self-contained,
unpackaged WinUI process with a required `Platform`, and pulling in Core drags the whole engine
dependency graph into it. If the author wants the drift caught mechanically, the honest options are a
generated file or a pack-time assertion, both of which are maintainer decisions, not reviewer calls.

---

## §3. The pipe contract

**Shape.** One-way named pipe, newline-delimited `COMMAND arg` lines, UTF-8. The overlay is the
server, opened `PipeDirection.In` (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:43`); the engine is the
client, opened `PipeDirection.Out`
(`src/Scribe.App/Overlay/OverlayProcessClient.cs:325`). Nothing flows back. There is no
request/response, no acknowledgement, no return value.

**The nine verbs handled by `Dispatch`:** `RECORDING`, `WARNING`, `PROCESSING`, `FAILED`, `HIDE`,
`METER`, `POSITION`, `WARMUP`, `EXIT`. The verb is upper-cased before the switch
(`OverlayIpcServer.cs:99`), so casing on the sending side does not matter; spelling does.

Flag when the diff:

- **Adds a `case` to `Dispatch` with no sender**, or an `Enqueue("VERB ...")` in
  `OverlayProcessClient` with no `case`. This is the partial-conversion shape for the pipe. Hard 🟡,
  or 🔴 if the missing half means a user-visible state never renders. Name both files in the finding.
- **Makes an unknown verb or an unparseable argument throw.** Today both paths log a warning and
  return: unknown verb at `OverlayIpcServer.cs:138`, unparseable `POSITION` argument at
  `OverlayIpcServer.cs:129`, and `METER` guarded by `int.TryParse` at `OverlayIpcServer.cs:117`. A
  throw inside `Dispatch` propagates out of the `while` loop in `RunAsync`, hits the outer
  `catch (Exception)`, and ends the reader loop; the `finally` then calls `_onDisconnected()` and the
  overlay exits. One malformed line would therefore kill the pill for the rest of the session. Hard
  🔴.
- **Adds a return channel**, a second pipe, or a `PipeDirection.InOut`. That is a protocol change with
  a lifecycle and a deadlock story that nobody has designed. Emit it as 🔴 tagged
  `[architecture-shortcut]` rather than a routine finding.
- **Logs `METER`.** It arrives up to forty times a second (`MeterIntervalMs = 25`,
  `OverlayProcessClient.cs:30`) and is deliberately excluded from the log; the class comment on
  `OverlayIpcServer` says so. A log call on that path floods the shared daily log that the dictation
  pipeline also writes to. Hard 🟡.
- **Changes the meter encoding on one side only.** The client sends the level as an integer,
  `level * 1000` (`OverlayProcessClient.cs:476` and the preview sweep at line 149); the server divides
  by `1000.0` (`OverlayIpcServer.cs:119`). Both or neither. Hard 🟡.
- **Removes the meter throttle or the single-slot coalescing.** `EnqueueMeter`
  (`OverlayProcessClient.cs:480`) keeps at most one meter command in the queue and always sends the
  latest value; the 25 ms gate is upstream of that. Removing either turns a VU bar into a pipe flood.
  Hard 🟡.
- **Blocks the UI thread on pipe or process work.** Every public method on `OverlayProcessClient` only
  enqueues; the single `ScribeOverlayIpc` background thread (`OverlayProcessClient.cs:69`) owns all
  launch, connect and write work, and writes are bounded by `WriteTimeoutMs`
  (`WriteWithTimeout`, line 265). A new synchronous `Process.Start`, `Connect`, or `WriteLine` called
  straight from a WPF handler is a hard 🟡: this app has a hard hotkey deadline and the pill is on the
  dictation path.

---

## §4. Position replay after reconnect

A freshly launched overlay starts at its own built-in default (`OverlayAnchor.BottomCenter`,
`src/Scribe.Overlay/OverlayWindow.xaml.cs`, around line 37). The engine therefore replays the applied
anchor and the desired visible state immediately after every connect
(`OverlayProcessClient.cs:332` and `:333`), so a relaunch after a crash or a lazy first launch comes up
where the user chose rather than waiting for the next dictation transition.

Flag 🟡 when the diff adds a new connect or reconnect path that does not replay `POSITION` and
`_desiredState`, or reorders the replay after the first state-dependent write.

Two details that look like bugs but are not, so do not flag them:

- `SetPosition` enqueues with `ensureAlive: false` (`OverlayProcessClient.cs:123`). Moving the pill
  must not launch the helper; the relaunch replays `_position` anyway.
- `Preview` changes what is on screen but never writes `_position` (see the field comment at
  `OverlayProcessClient.cs:57`), so a cancelled settings dialog costs nothing. A preview that did
  write `_position` would be the finding.

---

## §5. Orphan safety is belt AND braces, and both must survive

There are three independent guards. They are not redundant, they cover different failure windows, and
a diff that removes any one of them is a finding.

1. **The OS Job Object.** `OverlayChildJob` (`OverlayProcessClient.cs:587`) creates a job with
   `JobObjectLimitKillOnJobClose` (`0x2000`, line 589) via `CreateJobObject` plus
   `SetInformationJobObject`, and assigns the overlay to it with `AssignProcessToJobObject` right
   after `Process.Start` (line 322). The handle is **deliberately never closed**: the OS releases it as
   the engine process dies, which is exactly when the kill should fire. A diff that adds a
   `CloseHandle` on `_handle`, or wraps it in a `SafeHandle` with a finalizer, breaks the guarantee.
   Hard 🔴.
2. **The `--parent` PID watchdog.** The engine passes `--parent <pid>`
   (`OverlayProcessClient.cs:313`); the overlay resolves it in `ResolveParentPid` and arms
   `StartParentWatchdog` (`src/Scribe.Overlay/App.xaml.cs`, around line 153) **before** it starts the
   IPC server. The watchdog waits on the parent's process handle and calls `Environment.Exit(0)`, and
   if the parent is already gone at startup it exits immediately. This is the only guard that covers
   the window before the pipe ever connects, because pipe EOF cannot fire without a connection. Hard
   🔴 if removed, moved after `StartIpc`, or downgraded to a PID-polling loop (the current code binds
   to the process handle precisely so PID reuse cannot fool it).
3. **The initial-connection timeout.** `InitialConnectionTimeout` is 12 seconds
   (`OverlayIpcServer.cs:19`); on expiry the server logs
   `OverlayIpcServer connection timed out; exiting orphaned helper` and returns, and the `finally`
   fires `_onDisconnected()` so the process exits. Hard 🟡 if removed or raised to something that
   leaves a helper resident for minutes.

Also flag 🟡 when the diff adds a launch path that starts `Scribe.Overlay.exe` without passing both
`--pipe` and `--parent`. Without `--pipe` the overlay falls into the standalone dev mode driven by
`SCRIBE_OVERLAY_STATE` (`App.xaml.cs`, `StartStandalone`), which nothing will ever tell to exit.

---

## §6. Executable resolution order

`ResolveOverlayExe` (`OverlayProcessClient.cs:496`) tries exactly three strategies, in this order, and
the order is load-bearing:

1. `SCRIBE_OVERLAY_EXE` environment variable, so a developer can point at one specific build.
2. **Installer layout**: `AppContext.BaseDirectory\Overlay\Scribe.Overlay.exe`. This is what a shipped
   install uses, and `build/pack.ps1` publishes the overlay into exactly that folder (around line 142).
3. **Dev fallback**: walk up to the repo root (identified by `Scribe.slnx`) and search
   `src\Scribe.Overlay\bin` recursively, then pick with
   `OverlayExecutableSelector.Select(matches, BuildConfig, RuntimeInformation.ProcessArchitecture)`.

Flag 🔴 when the diff reorders these, in particular when it puts the dev fallback ahead of the
installer layout: an installed build that also happens to sit next to a source tree would then launch a
developer's stale `bin` output.

Flag 🔴 when the diff makes the dev fallback pick a path without going through
`OverlayExecutableSelector` (`src/Scribe.Core/Infrastructure/OverlayExecutableSelector.cs:24`). Its
remarks record the incident: alphabetical order puts `bin\ARM64\` first, Windows refuses a
mismatched binary with Win32 error 216 ("not a valid application for this OS platform"), the user sees
a "Machine Type Mismatch" dialog, and the pill never appears. Architecture match is checked first and a
mismatched binary is never returned. `DetectArchitecture` (line 63) walks path segments from the
executable outwards so the RID folder (`win-arm64`) wins over a platform folder higher up; a change
that reverses that walk direction is a hard 🟡.

That selector is the one part of this lens with real tests
(`tests/Scribe.Core.Tests/OverlayExecutableSelectorTests.cs`, 11 cases). A behavior change there with
no test change is a hard 🟡; hand it to `tests-regression-pin` if the change is a bug fix.

---

## §7. The destructive catch, and why logging goes through TryLog

**The incident, in the code's own words** (`OverlayProcessClient.cs:349`, the comment above the launch
log line): the launch log line once sat *inside* the `try`. A transient lock on the shared log file
threw there, the surrounding `catch` read that throw as a launch failure, and called `KillProcess()` on
a perfectly healthy overlay. That was a root cause of the intermittent "pill disappears" regressions.
The fix was to move the log line out of the `try`, after a confirmed-good launch, and to route it
through the non-throwing `TryLog` helper (line 362). `AGENTS.md` states the general rule under
**Never**: *"Let a logging failure reach a destructive catch (process kill, teardown)."*

Flag 🔴 when the diff:

- Adds a raw `_log?.Log*` call inside a `try` whose `catch` calls `KillProcess()`, kills a process,
  or tears down the pipe, anywhere in `src/Scribe.App/Overlay/**`. It must go through `TryLog`.
- Moves the existing launch log line back inside the `try`.
- Deletes or rewrites the `why` comment at line 349 or the one on `TryLog` at line 359. Those comments
  are the only thing stopping the next agent from "tidying" the shape back into the bug.
- Adds a `throw` to `OverlayLog` (`src/Scribe.Overlay/Logging/OverlayLog.cs:14`), or removes its
  `FileShare.ReadWrite` retry. Two processes append to the same daily log file, so a plain
  `File.AppendAllText` throws a sharing violation on the overlay side under load.

This overlaps `logging-discipline` and pattern **P-4** in `references/patterns.md`. Raise it here when
the destructive catch is on the overlay launch path; synthesis will dedup on root cause.

---

## §8. Build and packaging of the second process

- **`Platform` is required.** WinUI has no AnyCPU story. `Scribe.Overlay.csproj` declares
  `<Platforms>x64;ARM64</Platforms>` (line 20) and derives `RuntimeIdentifier` from it (lines 31 to 39)
  so a normal build cannot pair an ARM64 platform with an x64 native payload. A diff that removes
  either `PropertyGroup`, adds `AnyCPU` to `Platforms`, or hardcodes `RuntimeIdentifier`
  unconditionally, is a hard 🔴.
- **Unpackaged and self-contained stays.** `WindowsPackageType=None` plus
  `WindowsAppSDKSelfContained=true` (lines 23 and 24) are why the pill starts with no machine-wide
  Windows App SDK runtime installed. Flipping either to reduce payload size is a hard 🔴: an
  unpackaged WinUI app silently fails to start when the runtime is missing, which presents as the pill
  never appearing.
- **The pack payload.** `build/pack.ps1` maps each runtime to an overlay platform (`win-x64` to `x64`,
  `win-arm64` to `ARM64`, around line 65), publishes the overlay into `$publishDir\Overlay` **after**
  the app publish because that step wipes the directory (around line 142), asserts the exe exists, and
  then calls `Test-ScribePayloadArchitecture`. A diff that reorders the two publishes, drops the
  existence check, or skips the architecture assertion is a hard 🔴. An x64 pill beside an ARM64 app
  runs emulated: it works, so nothing fails, and the only symptom is battery and latency.
- **Windows App SDK version.** Pinned centrally at `2.2.0`
  (`Directory.Packages.props:13`). A bump is an `AGENTS.md` "Ask first" item; surface it for
  `maintainer-decision` rather than judging it here.

---

## §9. Confidence bar

**Hard flag** only when you can point at the specific missing or changed line and state the mechanism
end to end. On this contract that bar is usually reachable, because the failures are structural rather
than probabilistic: a verb with no `case`, an enum value with no twin, a guard that is gone. Say what
breaks, not that something might.

**Raise a Question instead** when the mechanism depends on runtime behavior you cannot read out of the
tree:

- Whether a new state transition produces the right visual sequence. You cannot see the pill from here.
- Whether a timing constant (the 12 second connect timeout, the 8 second `ConnectTimeoutMs`, the
  1300 ms failed hold, the 1800 ms recording-warning hold) is right for a machine other than this one.
- Whether a new `ProjectReference` from `Scribe.App` to `Scribe.Overlay` actually breaks the installer
  layout. The runtime resolution is by path, not by reference, so the honest question is: does the
  overlay still land in `Overlay\` after pack, and does the app still start on a machine with no
  Windows App SDK runtime?

**Never hard flag** on "this will not compile" or "the build will catch it". The two projects share no
reference, which is the whole reason this lens exists, and three defects in one release compiled warning
clean.

---

## §10. Verification to ask for on an overlay change

Any change in this lens's scope should be verified against the live log at
`%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log`, not against a green build. Ask the author for
these five, and say which one is missing rather than asking for "testing":

1. `Overlay exe via installer layout:` (or the strategy the change is supposed to exercise).
2. `size=462x192` on the author's display, from `OverlayWindow.SizeAndPosition`. That is 264 x 110
   logical DIPs at 175 percent scaling, so the exact numbers move with the monitor; what matters is a
   plausible scaled size rather than a zero or an unscaled 264x110.
3. `transparent=True backdrop=TransparentBackdrop` from the `LogState` snapshot
   (`src/Scribe.Overlay/OverlayWindow.xaml.cs`, around line 458). This is the line that proves the
   DWM-composition path is live.
4. The overlay PID still alive after the transition under test, with no `KillProcess` teardown in
   between.
5. **Zero `IOException`s after launch.** That is the signature of the shared-log-lock incident in
   Section 7.

For a build or packaging change, the commands are
`dotnet build src/Scribe.Overlay/Scribe.Overlay.csproj -c Debug -p:Platform=x64` and the same with
`-p:Platform=ARM64`.

---

## §11. Output format


The findings below are **illustrative shapes**, not live defects. `MiddleCenter` and the `TOAST` verb
are invented and appear in neither enum nor `Dispatch`. Never cite either as an existing value.

```markdown
## Overlay process contract findings

🔴 **`MiddleCenter` added to `OverlayPosition` but not to `OverlayAnchor`** (`src/Scribe.Core/Models/Enums.cs:36`)

The wire token for `POSITION` is the enum value name, and `Scribe.Overlay` holds no reference to
`Scribe.Core`, so nothing fails at build time. The engine will send `POSITION MiddleCenter`,
`Enum.TryParse<OverlayAnchor>` at `src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:123` will fail, and the
overlay will log `POSITION with unknown anchor 'MiddleCenter'` and keep the previous anchor. The user
picks the new position, saves, and the pill does not move.

Add the value to `src/Scribe.Overlay/OverlayAnchor.cs`, add the matching `RadioButton Tag="MiddleCenter"`
in the `OverlayPositionGrid` in `src/Scribe.App/Settings/SettingsWindow.xaml`, and add the switch arms in
`OverlayWindow.SizeAndPosition`. Without the last one the anchor parses and then falls into the `_ =>`
catch-all, rendering as `BottomCenter`.

🟡 **New `TOAST` case in `Dispatch` has no sender** (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:134`)

Nothing in `src/Scribe.App/Overlay/OverlayProcessClient.cs` enqueues `TOAST`, so this branch is dead.
Either wire the sending half or drop the case; a half-built verb reads as a working feature to the next
person who greps for it.
```

**Clean pass line**, emitted verbatim when nothing survives:

> Overlay process contract clean: the pill stays an out-of-process WinUI surface on the
> `TransparentBackdrop` path, pipe verbs match on both sides, the anchor names are in sync across all
> four by-name surfaces, position replay survives reconnect, the job object and the `--parent` watchdog
> are both intact, and the exe resolution order is unchanged.

---

## §12. Exceptions

Do not flag any of the following. Each one looks like a violation and is not.

- **`WS_EX_LAYERED` on the overlay window.** `ApplyExtendedStyles`
  (`src/Scribe.Overlay/OverlayWindow.xaml.cs`, around line 103) deliberately sets
  `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, and
  `src/Scribe.Overlay/Interop/NativeMethods.cs:19` labels it *"layered (DWM-composited here, not legacy
  ULW)"*. The banned thing is WPF `AllowsTransparency` and `UpdateLayeredWindow`, not the extended
  style. Flagging the style would be a confidently wrong reading of the incident.
- **`Scribe.Overlay` not referencing `Scribe.Core`.** That is the design, stated in the doc comment on
  `src/Scribe.Overlay/OverlayAnchor.cs`. Duplicated enum values across the boundary are the intended
  cost, not a DRY violation.
- **`OverlayLog` duplicating the app's log format instead of using `Microsoft.Extensions.Logging`.**
  Same reason: no reference, and the two processes append to one file on purpose so dictation and
  overlay events interleave on one timeline.
- **The engine ignoring an unknown response from the overlay.** There are no responses. The pipe is one
  way.
- **The nine-way `switch` in `SizeAndPosition` not being "refactored" into a lookup table.** It is
  exhaustive by construction and reads clearly. Suggesting a table is a style nit.
- **`presenter.IsAlwaysOnTop` not being used.** The comment at `ConfigurePresenter` records that it
  breaks `WS_EX_TRANSPARENT` click-through; top-most is asserted with `SetWindowPos` instead. Do not
  suggest the presenter property.
- **The `SCRIBE_OVERLAY_DIAG_NOWINDOW` and `SCRIBE_OVERLAY_DIAG_NOBACKDROP` escape hatches.** They are
  deliberate diagnostic switches for isolating a transparency failure, default off.
- **Preview not persisting the anchor.** Covered in Section 4; that is the feature.
- **Overlay code with no unit test.** `Scribe.Overlay` is a WinUI process with no test project and no
  practical way to get one. Ask for log evidence (see Verification), not for a test. The exception is
  `OverlayExecutableSelector`, which lives in `Scribe.Core` and does have tests.
- **A pure XAML or visual change in `OverlayWindow.xaml`** that touches no state machine, no pipe verb,
  and no anchor. Out of scope for this lens; `ui-shell-quality` owns it.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:overlay-process-contract findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
