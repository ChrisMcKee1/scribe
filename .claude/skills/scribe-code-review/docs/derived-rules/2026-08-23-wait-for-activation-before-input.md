---
status: retired
added: 2026-08-23
retired: 2026-08-31
surface: text-injection
severity: important
paths:
  - src/Scribe.Core/TextInjection/**
  - src/Scribe.App/TextActions/**
  - src/Scribe.Core/Hotkeys/**
evidence:
  - "src/Scribe.App/TextActions/TextActionController.cs:286-290 records the write-side defect: injecting straight after SetForegroundWindow sent the opening keystrokes into the activation gap, so the first character of every rewrite went missing."
  - "src/Scribe.Core/TextInjection/SelectionReader.cs:180-183 records the read-side defect: a Ctrl+C delivered while the window was still mid-restoration copied nothing, which surfaced to the user as a spurious 'select some text first'."
  - "src/Scribe.Core/TextInjection/ForegroundReadiness.cs:8-27 and :68-71 record the fix and a third failure inside it: returning early without the settle on the already-foreground path, which was the common path rather than the rare one."
---

# An asynchronous Windows activation must be waited on before any input is synthesized into it

> **Retired 2026-08-31.** The text action (highlight and rewrite) feature was removed, and with it
> every shape this rule guards: `TextActionController`, `SelectionReader`, and
> `ForegroundReadiness` itself. Nothing left in the repository synthesizes input into a window it
> has just activated. Kept on disk for provenance, per the retirement policy in
> [`README.md`](README.md).

## Guideline

When code brings **another process's window** forward and then synthesizes input into it, there must
be an observable readiness wait between the two, and the code must act on the wait failing.
`SetForegroundWindow` posts a request; it does not perform the activation. Call
`ForegroundReadiness.WaitForInputReady(target)` (`src/Scribe.Core/TextInjection/ForegroundReadiness.cs:50`)
and treat a `false` return as "do not inject", the way both live callsites do. A `Thread.Sleep` with a
guessed constant is not a substitute, and neither is checking `GetForegroundWindow()` once.

## Why

Activation on Windows has two stages and only the first is observable through `GetForegroundWindow`.
The window becomes foreground when its thread processes the request; only after that does the thread
restore focus to a child control and rebuild its caret and selection. **Input delivered between those
two points is silently dropped.** There is no error, no return code, and no log line: the keystrokes
simply do not arrive.

At typing speed that gap is one or two characters, so the failure is small, silent, and constant. It
has cost this project three separate defects:

- **Write side.** The text-action write-back injected immediately after requesting activation, and
  the first character of every rewrite went missing.
- **Read side.** The selection reader synthesized Ctrl+C into the same gap. The copy landed nowhere,
  and the user was told to select some text they had already selected.
- **Inside the fix itself.** The readiness helper originally returned early when the window was
  already foreground on entry, skipping its settle. That path turned out to be the common one, not
  the rare one, because closing the palette usually hands foreground back before the check runs.

The third one is the important one for review purposes: the bug survived the first fix, because "it
already looks foreground" reads as "nothing to wait for" and is wrong.

## Detection signal

Path filter: `src/Scribe.Core/TextInjection/**`, `src/Scribe.App/TextActions/**`,
`src/Scribe.Core/Hotkeys/**`.

Fire when the diff adds, inside one method or one call chain:

1. An activation of a **foreign** window: `SetForegroundWindow`, `SetActiveWindow`,
   `BringWindowToTop`, `SwitchToThisWindow`, `ShowWindow`, or `AllowSetForegroundWindow`; then
2. synthesized input into that window: `SendInput`, `ITextInjector.Inject`, `SendCtrlC`, `SendCtrlV`,
   `SendMarkedKeyEvent`, or any new `INPUT` array build; and
3. **no** `ForegroundReadiness.WaitForInputReady` between them.

Three variants of the same defect, all in scope:

- **The wait is there and its result is discarded.** `_ = ForegroundReadiness.WaitForInputReady(t);`
  followed by the injection regardless. The bool is the whole contract: false means the window never
  arrived, and injecting anyway types the user's text into whatever did.
- **A sleep stands in for the wait.** `Thread.Sleep(100)` between the activation and the send. Too
  short on a loaded machine, wasted latency on an idle one, impossible to tune for both.
- **The wait is made conditional on the window not already looking foreground.**
  `if (GetForegroundWindow() != target) { SetForegroundWindow(target); Wait(); }` is the exact shape
  the third defect had. Both live callsites issue the activation and the wait **unconditionally**,
  and both say in a comment why.

## Safe shapes

Two live callsites, both correct. Grep the symbol before citing either; line numbers drift.

| Callsite | Shape |
| --- | --- |
| `TextActionController.RestoreForeground` (`src/Scribe.App/TextActions/TextActionController.cs:337`) | `SetForegroundWindow` at `:342` unconditionally, `WaitForInputReady` at `:347`, and the caller at `:291` copies to the clipboard and tells the user instead of injecting when it returns false. |
| `SelectionReader.Capture` (`src/Scribe.Core/TextInjection/SelectionReader.cs:184-194`) | Same pair, and a `false` return produces a `SelectionFailure.NoTarget` capture rather than a Ctrl+C into nothing. |

Note what `ForegroundReadiness` actually waits on: `GetGUIThreadInfo` reporting a non-zero
`hwndFocus` for the target's thread, which is the OS-level signal that focus restoration finished,
followed by a 60 ms settle on **every** success path. Reuse it. Do not write a second one.

## Example

From `src/Scribe.App/TextActions/TextActionController.cs`, above the write-back guard:

> And it must be WAITED FOR. SetForegroundWindow only requests activation; the change lands
> when the target thread processes it. Injecting immediately sent the opening keystrokes into
> the activation gap, where they went to the outgoing window instead of the document, which
> is why the first character of every rewrite went missing. SelectionReader already polls
> for this on the read side; the write side has to do the same.

From `src/Scribe.Core/TextInjection/SelectionReader.cs`, above the read-side pair:

> Issued unconditionally rather than only when the window looks like it is not foreground:
> "already foreground" can mean "mid-restoration", and a Ctrl+C delivered in that gap copies
> nothing, which surfaced as a spurious "select some text first".

## Exceptions

Do not flag any of these.

- **Scribe activating its own WPF window.** `_settingsWindow.Activate()`, `_welcomeWindow.Activate()`,
  `_quickAddWindow.Activate()` (`src/Scribe.App/App.xaml.cs`), and
  `window.Activate()` in `TextActionController.ShowPalette`
  (`src/Scribe.App/TextActions/TextActionController.cs:167`). The next input is a human, and a human
  is not delivered into an activation gap. `TextActionPaletteWindow`'s remarks
  (`src/Scribe.App/TextActions/TextActionPaletteWindow.xaml.cs:15-23`) say why that one activates
  normally at all: a `WS_EX_NOACTIVATE` window receives no `WM_KEYDOWN`, so type-to-filter, the arrow
  keys, and Escape would all have to be stolen from the app underneath. The rule is about
  **synthesized** input into a **foreign** window.
- **`TextInjector.Inject`, which deliberately does not activate at all.** It captures an
  `expectedForegroundWindow` and re-checks it before and during the send, failing with "The focused
  window changed while processing." rather than stealing focus. Not activating is a valid answer and
  the correct one there. Never ask for a readiness wait where there is no activation to wait on.
- **`tools/Scribe.InjectionLab/TargetWindow.cs`.** `EnsureForeground` (`:177`) already polls, in a
  harness process that owns the window it is activating. It is a measurement rig, not the shipped
  path.
- **The overlay.** `src/Scribe.Overlay` sets `WS_EX_NOACTIVATE` and never takes focus, so there is no
  activation here to wait on.
- **Tuning the constants.** `SettleMs = 60`, `DefaultTimeoutMs = 900`, `PollIntervalMs = 10`
  (`ForegroundReadiness.cs:30-43`) each carry a comment explaining the trade. A change to one is a
  question about measurement, which belongs to `win32-interop`, not a learned-pattern finding.
- **`AttachThreadInput` as an alternative.** Scribe does not use it in shipped code and
  `win32-interop` owns that conversation. Do not propose it here.

<details>
<summary>Provenance</summary>

**Source:** defects fixed while building the text-actions selection and write-back path, 2026-08.
There is no mined PR-comment history for this repository yet; see `README.md` in this directory.

**Occurrences:**

1. `src/Scribe.App/TextActions/TextActionController.cs:286-290`. Write side. Symptom: the first
   character of every rewrite went missing.
2. `src/Scribe.Core/TextInjection/SelectionReader.cs:180-183`. Read side. Symptom: a spurious
   "select some text first" on a selection that was genuinely there.
3. `src/Scribe.Core/TextInjection/ForegroundReadiness.cs:68-71`. Inside the fix. The already-foreground
   early return skipped the settle, and that was the common path.

**Novel signal check against the hand curated lenses:**

- `agents/win32-interop.md` covers this surface and already carries a 🟡 bullet for a new activation
  that calls `SetForegroundWindow` and proceeds immediately, naming `ForegroundReadiness`. **This is
  substantial overlap and it is the main argument against activating this rule.** What this rule adds
  that the lens bullet does not: the discarded-bool variant, and the conditional-wait variant that
  caused the third defect. If the lens is extended to cover those two variants, this candidate should
  be retired rather than activated.
- `agents/core-app-layering.md` is about where logic lives, not about activation ordering.
- `references/patterns.md` P-5 covers the STA thread and the join, not the activation wait.

**Activation status:** blocked. Two of the three occurrences are the same mechanism seen on the read
and write sides of one feature, which is one pattern twice rather than three independent times, and
the `win32-interop` overlap is unresolved. Needs a replay against merged changes under
`src/Scribe.Core/TextInjection/**` and a decision on whether to extend `win32-interop` instead.

**No test pins this.** Both types live in `Scribe.Core`, so the suite can reference them, but neither
can be exercised: `ForegroundReadiness.WaitForInputReady` and `SelectionReader.Capture` need a real
foreground window and a real input queue, and a grep of `tests/Scribe.Core.Tests` finds neither name.
The invariant is held by review and by `tools/Scribe.InjectionLab`, which is part of why a rule is
worth having.

</details>
