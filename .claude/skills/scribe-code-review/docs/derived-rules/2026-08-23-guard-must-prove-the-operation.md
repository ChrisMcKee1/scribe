---
status: retired
added: 2026-08-23
retired: 2026-08-31
surface: text-injection
severity: important
paths:
  - src/Scribe.Core/TextInjection/**
  - src/Scribe.Core/Hotkeys/**
  - src/Scribe.App/TextActions/**
evidence:
  - "src/Scribe.Core/TextInjection/SelectionReader.cs:70-79 records two rejected proofs: GetClipboardSequenceNumber is machine-wide so a change proves nothing, and 'the payload differs from what was there before' failed on the most ordinary cases of all."
  - "src/Scribe.Core/TextInjection/SelectionReader.cs:288-298 records the inverse failure: a sequence-number guard on the restore path always fired, because Scribe's own clear plus the target's write are two bumps of Scribe's own making, so the user's clipboard was never put back."
  - "src/Scribe.Core/Hotkeys/HookLivenessProbe.cs:10-22 records the same shape outside the clipboard: two TickCount64 stamps reported a dead hook 3,775 times over 22 days, on 13.3 percent of watchdog ticks, and every false positive stopped a dictation in progress."
---

# A guard that proves an operation happened must observe the operation, not a shared counter and not a payload difference

> **Retired 2026-08-31.** The text action (highlight and rewrite) feature was removed, taking
> `SelectionReader` with it. Three of the four occurrences behind this rule lived there, leaving
> only `HookLivenessProbe`, which `agents/win32-interop.md` §2 already covers. One occurrence
> cannot clear the three-occurrence activation bar, so the rule can no longer graduate on this
> evidence. Kept on disk for provenance, per the retirement policy in [`README.md`](README.md).

## Guideline

When new code needs to decide whether an operation it triggered actually happened, the evidence must
be something **this process owns**. Establish a known state first and treat any departure from it as
the proof, or count something your own code increments. Do not infer success from a counter the whole
machine can move, and do not infer it from the new value differing from the old one. When the guard is
wrong, the code abandons work the user asked for, so the cost of a weak proof is not a wrong log line,
it is lost data.

## Why

Both weak proofs were tried in Scribe's clipboard-based selection read, and both failed in
production. Then the mirror-image mistake shipped on the restore path.

- **A machine-wide counter proves nothing.** `GetClipboardSequenceNumber`
  (`src/Scribe.Core/TextInjection/InjectionNativeMethods.cs:97`) advances for **any** process on the
  machine. A clipboard manager, a password manager, or a browser tab moves it. "The number changed,
  therefore my Ctrl+C landed" is not an implication.
- **A payload comparison breaks the ordinary cases.** Adding "and the text differs from what was
  there before" fails exactly when the user is doing something normal: they had already copied that
  same text themselves, or they ran a second action over text Scribe had just pasted. The copy
  worked perfectly, and Scribe told them something else had changed their clipboard.
- **The same counter used as a positive guard on the restore path fired every single time.** The old
  restore skipped putting the user's clipboard back whenever the sequence number had moved. It always
  moves there: Scribe empties the clipboard and the target then writes to it, two bumps of Scribe's
  own making. So the restore never ran, and the user was left holding their selection instead of what
  they had copied. That is the "Clipboard changed during capture" line in the log.

The same shape has bitten outside the clipboard. `HookLivenessProbe`'s predecessor decided whether
Windows had silently removed the low-level keyboard hook by comparing two `Environment.TickCount64`
stamps. Injected input reaches the hook chain **before** `SendInput` returns, so the callback always
looked older than the probe it had just answered. Over 22 days of production logs it reported a dead
hook 3,775 times, on 13.3 percent of watchdog ticks, and every false positive tore down the hook
thread, reset chord state, and stopped any dictation in progress. The fix was a monotonic counter this
process increments itself, which needs no clock at all.

## Detection signal

Path filter: `src/Scribe.Core/TextInjection/**`, `src/Scribe.Core/Hotkeys/**`,
`src/Scribe.App/TextActions/**`.

Fire when the diff adds a boolean guard whose **positive** conclusion is "my operation succeeded" and
whose evidence is one of:

- `Win32Clipboard.SequenceNumber` (`src/Scribe.Core/TextInjection/Win32Clipboard.cs:98`) or a direct
  `GetClipboardSequenceNumber`, read as **changed**;
- `DateTime.Now`, `DateTimeOffset.UtcNow`, `Stopwatch`, or `Environment.TickCount64`, compared across
  a call that can dispatch work re-entrantly;
- a file `LastWriteTime`, a registry value, a named mutex, or any other process-external state that a
  second writer can move;
- `newValue != oldValue` on user data: clipboard text, a transcript, a settings blob, a document
  selection.

And the guard's failing branch **abandons or skips** something the user asked for: it discards a
capture, skips restoring their clipboard, reports a failure on a success, or refuses an injection.

The tell that separates this rule from ordinary change detection is that last clause. A comparison
used to decide whether to repaint a row is not this rule. A comparison used to decide whether the
user gets their clipboard back is.

## Safe shapes

Three forms in this repository are correct and must never be flagged. The distinction is the
direction of the inference.

**1. Establish a state you own, then any change is yours.** `SelectionReader.CaptureCore`
(`src/Scribe.Core/TextInjection/SelectionReader.cs:236`) calls `Win32Clipboard.Clear()` immediately
before the synthesized Ctrl+C, so `WaitForClipboardText` (`:266`) can accept **any** non-empty text as
the proof. There is no comparison left to get wrong. This is the shape to propose in a finding.

**2. The negative direction of a shared counter is a valid conservative check.**
`TextInjector.PasteViaClipboard` (`src/Scribe.Core/TextInjection/TextInjector.cs:162, 168, 176, 195,
205`) records the sequence number after its own write and restores the user's clipboard **only when
the number has not moved since**. That reads as "nothing else has written, so this is still mine to
put back", which is sound: an unchanged machine-wide counter really does mean nobody wrote. Do not
flag it, and do not propose "simplifying" it into the positive form.

**3. A counter your own process increments.** `HookLivenessProbe.IsHookDead(callbackCount)`
(`src/Scribe.Core/Hotkeys/HookLivenessProbe.cs:36`) judges against a baseline taken **before** the
send (`:43`), on a counter incremented only by Scribe's own hook callback. No clock, and no shared
state. This is P-10 in `references/patterns.md`.

## Example

From the class remarks on `SelectionReader` (`src/Scribe.Core/TextInjection/SelectionReader.cs:70-79`):

> Two weaker tests were tried first and both failed in production. `GetClipboardSequenceNumber` is
> machine-wide and any process can bump it, so a change proves nothing on its own. Adding "and the
> payload differs from what was there before" then broke the most ordinary cases of all.

From the remarks on `RestoreClipboard` (`:288-298`), the inverse mistake:

> That guard was meant to avoid clobbering a clipboard manager's write, but the sequence number
> ALWAYS moves here: Scribe empties the clipboard and the target then writes to it, which is two
> bumps of Scribe's own making. So the guard fired on every capture and the restore never ran.

## Exceptions

Do not flag any of these.

- **The negative direction.** See Safe shapes 2. `TextInjector`'s use of `SequenceNumber` is correct
  and load bearing.
- **`RestoreClipboard` being unconditional.** Its remarks record why the guard was removed and state
  the trade explicitly: a few hundred milliseconds of exposure against losing the clipboard on every
  single invocation. Do not ask for the guard back. `win32-interop` lists this as an exception too.
- **`PRAGMA user_version` in `ScribeDatabase.Migrate`.** The schema version is written by this process
  inside the same transaction as the migration it gates. It is not shared mutable state, and P-11 in
  `references/patterns.md` owns migrations regardless.
- **`ScribeDatabase.ExpectedSqliteVersion`.** A version comparison used to **refuse** rather than to
  prove. Refusing on a mismatch is the fail-closed direction.
- **A timeout or deadline.** `Environment.TickCount64` used to bound a poll loop, as in
  `WaitForClipboardText` (`:272`) and `ForegroundReadiness.WaitForInputReady`, decides when to give
  up, not whether the operation succeeded. Give-up is not a proof.
- **Ordinary change detection with no operation behind it.** Dirty tracking on a settings row,
  repaint suppression, a cache key comparison. Nothing is being proved to have happened.
- **`SendInput` short counts.** The returned event count is the API reporting on its own delivery, not
  an external signal, and `win32-interop` owns it in detail.
- **Pre-existing guards the diff only moved past.** This rule is about what the change adds.

<details>
<summary>Provenance</summary>

**Source:** defects fixed while building the text-actions selection read path, 2026-08, plus one
older production incident recorded in `HookLivenessProbe`. There is no mined PR-comment history for
this repository yet; see `README.md` in this directory.

**Occurrences:**

1. `SelectionReader`, proof #1: machine-wide sequence number read as positive evidence. Rejected
   because any process bumps it.
2. `SelectionReader`, proof #2: payload comparison against the pre-copy snapshot. Rejected because it
   failed on the two most ordinary user situations, telling the user the copy had failed when it had
   worked.
3. `SelectionReader.RestoreClipboard`: the same counter used as a positive skip-guard, firing on
   100 percent of captures and silently losing the user's clipboard every time.
4. `HookLivenessProbe`: clock comparison across a re-entrant call, 3,775 false positives in 22 days,
   each one stopping a dictation in progress. Different subsystem, same shape.

**Novel signal check against the hand curated lenses:**

- `agents/win32-interop.md` §2 covers the `HookLivenessProbe` clock comparison specifically, and its
  Exceptions list covers `RestoreClipboard`. It does **not** state the general rule, and it does not
  cover a new guard elsewhere in the injection path that proves success from a shared counter or a
  payload difference. That generalization is what this rule adds.
- `references/patterns.md` P-10 is the closest cataloged shape ("a deterministic decider fed
  timestamps or counters, never reading the clock"), but P-10 is about testability and clock reads.
  It says nothing about a counter the machine shares, and nothing about payload comparison.
- `agents/tests-quality.md` would ask whether such a guard can be tested. It would not say the guard
  is wrong.

**Activation status:** blocked on a replay. The detection signal has to separate the invalid positive
direction from the valid negative direction that `TextInjector.PasteViaClipboard` uses today, and that
separation has not been tested against real diffs. A rule that flags `TextInjector` would be worse
than no rule, because it would push an author toward removing a correct guard.

**Testability:** `HookLivenessProbeTests` pins occurrence 4, because the decider is pure. Occurrences
1 to 3 cannot be pinned the same way. `Win32ClipboardTests` does drive the real clipboard from an STA
thread, so the primitives are testable, but the guards themselves sit inside
`SelectionReader.CaptureCore`, which needs a foreign application that answers a synthesized Ctrl+C.
A grep of `tests/Scribe.Core.Tests` finds no `SelectionReader` test. Review is the only detector for
that half.

</details>
