# Win32 interop review lens

You answer one question the other lenses cannot: **is this P/Invoke, hook, input, clipboard, and focus
code correct under the constraints Windows actually imposes?** Not "is it tidy C#". Windows enforces a
hard deadline on the hook callback, drops synthetic keystrokes without telling anyone, hands the
clipboard to whichever process asked first, and moves the foreground window out from under a running
sequence. Every rule below exists because one of those cost this project real time, and most of them are
written down in a `why` comment three lines above the code.

**Dispatch trigger.** The diff touches `src/Scribe.Core/Hotkeys/**`, `src/Scribe.Core/TextInjection/**`,
`src/Scribe.Overlay/Interop/**`, or
`src/Scribe.App/Infrastructure/{HotkeyCapture,ShellIconCache,StartupRegistration}.cs`; **or** it adds
any of `DllImport`, `LibraryImport`, `SendInput`, `SetWindowsHookEx`, `CallNextHookEx`, `OpenClipboard`,
`GetClipboardData`, `SetForegroundWindow`, `GetForegroundWindow`, `AttachThreadInput`, `GUITHREADINFO`,
`Marshal.PtrToStructure`, or `ApartmentState`.

**Severity cap:** 🔴 Critical. **Findings cap:** 5.

**Data on disk.** Read `diff.patch` (and `delta.patch` on a re-review) plus `metadata.json` from the
cache. The reviewed branch may not be checked out, so `diff.patch` is authoritative for what changed.
Use Read and Grep freely for surrounding context: the sibling interop file, the existing callers, the
tests, and above all the long `why` comments. This lens is worthless without them.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, confirm you can name each of the following. If one is missing, say
the gap instead of concluding.

1. **Which thread the new code runs on.** The hook callback thread (`Scribe.HotkeyHook`, STA, with a
   native message pump), the dispatch thread (`Scribe.HotkeyDispatch`), the STA injection worker
   (`Scribe.TextInjection` / `Scribe.SelectionReader`), the WPF UI thread, or a pool thread. Almost
   every rule below is thread-conditional.
2. **Whether it sits inside the OS deadline.** Anything reachable from `HotkeyService.HookCallback`
   (`src/Scribe.Core/Hotkeys/HotkeyService.cs:310-357`) is on the path every keystroke in the system
   takes. Anything on the watchdog, the reconciler task, or the consumer thread is not.
3. **The comment that explains the shape.** These files carry incident records, not narration.
   `HookLivenessProbe`'s summary, `SuppressedKeyReconciler`'s summary, `Win32Clipboard.CanBorrow`'s
   remarks, `GlobalHotkey`'s remarks, and the `TypeUnicode` / `ChunkLength` comments are the reasoning
   you are reviewing against. A hunk that deletes one of them deserves a hard look on its own.
4. **What the failure looks like to the user.** Every rule here maps to a symptom: a key the user cannot
   release, dictation that stops working until restart, a long dictation that truncates, a destroyed
   clipboard, or text typed into the wrong window.
5. **Whether the code is new or pre-existing.** This lens reviews the diff. Pre-existing interop that
   the diff merely moves past is out of scope.

---

## §1. The hook callback races a hard OS deadline

`WH_KEYBOARD_LL` callbacks race `LowLevelHooksTimeout`, capped at 1000 ms since Windows 10 1709.
**Windows silently removes a hook whose callback misses it and gives no notification of any kind.** The
consequence when it happens is documented in `SuppressedKeyReconciler`'s summary
(`src/Scribe.Core/Hotkeys/SuppressedKeyReconciler.cs:5-14`): one event is delivered **past** the hook. If
that leaked event is an autorepeat key-down during a long hold and the final key-up is suppressed as
usual, the system's logical key state is stuck down, and because the hook keeps swallowing that key the
user can never release it. `SuppressedKeyReconciler` is the self-heal for exactly that state (system says
down, the hook's physical view says released, so it injects a synthetic key-up).

This is why the callback is written the way it is. `HotkeyService` precomputes two field offsets at type
load (`HotkeyService.cs:15-20`) and does two direct reads, `Marshal.ReadIntPtr` for `dwExtraInfo` and
`Marshal.ReadInt32` for `vkCode` (`HotkeyService.cs:329-338`), instead of `Marshal.PtrToStructure`, which
would marshal the whole `KBDLLHOOKSTRUCT` on every keystroke the machine sees.

**🔴 Critical, hard flag, when the diff adds any of these inside the callback path:**

- `Marshal.PtrToStructure<KBDLLHOOKSTRUCT>` or any full-struct marshal in place of a field read.
- An allocation on the hot path: a `new` of a reference type, a LINQ chain, a `List<T>`, string
  formatting, string concatenation, or a boxed enum.
- A `lock`, `Monitor`, `SemaphoreSlim.Wait`, `Task.Wait`, `.Result`, `.GetAwaiter().GetResult()`, or any
  other blocking wait.
- A log write. `_logger.Log*` reaches `FileLoggerProvider`, which does file I/O with retries; it is fine
  on the dispatch thread and on the watchdog, and it is not fine inside the callback.
- Any file, registry, process, or named-pipe access.
- A call that can throw where the throw is not contained. An exception escaping a low-level hook callback
  terminates the process. `TryEnqueue` (`HotkeyService.cs:384-404`) exists solely for that reason: a
  keyboard message can still be in the native queue when `Stop` marks the transition queue complete, and
  `BlockingCollection.TryAdd` throws in that race.

**Also hard flag** a callback path that stops calling `CallNextHookEx` on the paths that currently call
it. `HookCallback` returns `1` only when the chord machine says suppress; every other exit chains
(`HotkeyService.cs:316-356`). Swallowing an event Scribe did not intend to suppress makes that keystroke
vanish system-wide.

**Raise as a Question**, not a finding: new work added to the callback whose cost you cannot bound from
reading it, for example a call into a type you cannot see the body of. Say what you could not establish
and ask the author to state the worst-case cost.

**Do not flag** `Interlocked.Increment(ref _hookCallbackCount)` at the top of the callback
(`HotkeyService.cs:314`), or the `Task.Run` in `ScheduleReconcile` (`HotkeyService.cs:468`). The counter
is a single interlocked add and is deliberately before all filtering so the marker-tagged probe still
proves liveness; `ScheduleReconcile` deliberately moves the leak check off the callback because
`GetAsyncKeyState` is meaningless until the callback returns.

## §2. Hook liveness is inferred by a monotonic counter, never by comparing clocks

`HookLivenessProbe` (`src/Scribe.Core/Hotkeys/HookLivenessProbe.cs`) decides whether Windows removed the
hook. Its summary records the incident in full: the previous inline version compared two
`Environment.TickCount64` stamps and armed the probe with the stamp read **after** `SendInput` returned,
while the callback stamped itself **during** that call, because injected input is dispatched into the
hook chain before `SendInput` returns. The callback therefore always looked older than the probe it had
just answered, and any advance of the roughly 15.6 ms tick counter read as a dead hook. Over 22 days of
production logs that fired **3,775 times, on 13.3 percent of watchdog ticks**. Every false positive tore
down the hook thread, reset chord state, and stopped any dictation in progress.

**🔴 Critical, hard flag:**

- Any reintroduction of a clock comparison into liveness: a `DateTime.Now`, `DateTimeOffset.UtcNow`,
  `Stopwatch`, or `Environment.TickCount64` read used to decide whether the hook is alive. The counter
  needs no clock, and a clock here is the exact defect that shipped. This also violates P-10 in
  `references/patterns.md`, so cross-reference it.
- Moving `_livenessProbe.Baseline(...)` after the send. `WatchdogTick` takes the baseline **before**
  `SendMarkedKeyEvent` (`HotkeyService.cs:552-560`) precisely because a baseline taken afterwards would
  already include the callback the probe caused, so the probe could never be answered.
- Dropping `Arm(sendSucceeded)`. `Arm` is passed the boolean result of the send
  (`HotkeyService.cs:559-560`); a rejected `SendInput` (UIPI, a desktop switch mid-tick) proves nothing
  about the hook and must not read as dead on the next tick.
- Removing a `Disarm()` guard: `ReinstallHookLocked` disarms first (`HotkeyService.cs:576`) so a probe
  armed against the destroyed hook is never judged against its replacement, which would reinstall in a
  loop; `WatchdogTick` disarms when `CanAccessInputDesktop()` is false (`HotkeyService.cs:527-531`) so the
  lock screen does not churn a reinstall all night.

**🟡 Important:** a change to `ShouldWithholdProbe` or to `WatchdogPeriod` (30 seconds,
`HotkeyService.cs:22`) with no matching change to `HookLivenessProbeTests`. Probing unconditionally is
what previously kept machines from ever sleeping, because injected input resets the power manager's idle
timer; the withhold rule is what restored normal sleep, and `TryGetSystemIdleTime`
(`src/Scribe.Core/Hotkeys/NativeMethods.cs:141-154`) returning null must keep probing rather than
silently disabling detection.

The regression pins live in `tests/Scribe.Core.Tests/HookLivenessProbeTests.cs`, including
`Callback_raised_during_the_send_answers_the_probe`, `Failed_send_never_reports_a_dead_hook`, and
`Disarming_on_reinstall_prevents_an_immediate_second_reinstall`. A change to this decision with no test
movement is a finding in its own right; hand it to `tests-regression-pin` rather than duplicating it here.

## §3. There is exactly one low-level keyboard hook in this process, on purpose

`HotkeyService.cs:270` is the only `SetWindowsHookEx` call in the repository. That is deliberate: two
low-level hooks in one process means two callbacks per keystroke inside one `LowLevelHooksTimeout` budget,
and two reconcilers competing over the same physical keys.

The text action trigger is the worked example of the correct alternative. It runs on `RegisterHotKey` and
a message-only window (`src/Scribe.App/Infrastructure/GlobalHotkey.cs`), whose remarks state both
reasons: the semantics differ (push-to-talk needs press and release as separate events, opening a palette
is a single tap the OS can match itself) and the risk differs more (a third chord state machine in the
callback puts new work on the path every keystroke takes, while `RegisterHotKey` shares no state with the
hook, cannot slow it down, and cannot leave a key stranded). `App.xaml.cs:303-304` says the same thing at
the wiring site.

**🔴 Critical, hard flag:** a second `SetWindowsHookEx` anywhere in the process, or a new global trigger
implemented by adding another `ChordStateMachine` to `HookCallback` when a tap-to-fire
`RegisterHotKey` binding would serve. Name `GlobalHotkey` as the shape to reuse.

**Note, do not flag:** `HotkeyService` already carries a *second* chord machine, `_dictationOnlyState`,
inside the callback (`HotkeyService.cs:339-345`). That one is existing, load-bearing, and shares the
push-to-talk press/release semantics. The rule is about adding a *new* one for a trigger whose semantics
are a tap.

## §4. `SendInput` returns a short count and Scribe treats that as real

Windows silently drops synthetic keystrokes when a batch exceeds what the focused app's input queue can
drain. That is why a short dictation types fine and a long one truncates. `TextInjector` is built around
the short count rather than around hope:

- `UnicodeChunkChars = 50` code units per batch, `InterChunkSettleMs = 5` between batches,
  `ChunkRetryDelayMs = 12`, `MaxChunkRetries = 5` (`src/Scribe.Core/TextInjection/TextInjector.cs:24-30`).
- `SendWithRetry` (`TextInjector.cs:521-550`) resends **only the unsent remainder**, advancing `offset`
  by the reported count and slicing `inputs[offset..]`, and gives up after `MaxChunkRetries` with a
  warning rather than looping forever.
- `ChunkLength` (`TextInjector.cs:451-488`) keeps a CRLF pair inside one batch, because split across two
  `SendInput` calls the CR and the LF each become their own Return and type an extra blank line. It also
  prefers to end a batch on a word boundary, backing up no more than half a batch, because a fixed cut
  tore words in half mid-render.
- `CountKeyEvents` (`TextInjector.cs:497-519`) computes the expected event total the same way
  `BuildUnicodeChunk` produces it, so a completed send is never misreported as truncated.

**🔴 Critical, hard flag:**

- A new `SendInput` call whose return value is discarded, or assigned and never compared against
  `nInputs`.
- A new path that sends a whole string in one `SendInput` batch instead of chunking.
- A retry that resends the **whole** batch after a short count rather than the remainder. That
  double-types whatever already landed.
- A chunk boundary change that can split a CRLF pair, or an edit to `CountKeyEvents` that no longer
  mirrors `BuildUnicodeChunk`. `tests/Scribe.Core.Tests/TextInjectorUnicodeChunkTests.cs` pins both
  (`A_crlf_pair_survives_every_chunk_boundary`, `The_event_total_matches_what_is_actually_built`,
  `Word_boundary_batching_never_loses_or_reorders_text`); a change here with those tests untouched is a
  finding.

**🟡 Important:** a new chord send (a modifier held across several events) with no key-up cleanup on the
short-count path. A truncated chord leaves the modifier logically down and turns the user's next real
keystroke into a shortcut. The two live exemplars are `ReleaseCtrlV` (`TextInjector.cs:258-271`, fired
when `ctrlV.Sent < ctrlV.Total`) and `ReleaseShift` (`TextInjector.cs:364-372`), the latter also reached
through an exception **filter**, `ReleaseShiftOnFault` (`TextInjector.cs:379-394`), which returns false so
the original exception keeps unwinding with its stack untouched. If a diff converts that filter into a
`catch`/rethrow, flag it: the point of the filter is that the Shift key-up runs before the stack unwinds.

**Also check `dwExtraInfo`.** Every synthetic event Scribe sends carries
`SyntheticInputMarker.Value` (`src/Scribe.Core/Hotkeys/ChordStateMachine.cs:227-231`) so the hook can tell
Scribe's own input from the user's. It is set in `TextInjector.KeyboardInput`
(`TextInjector.cs:562-576`), `SelectionReader.KeyboardInput`
(`src/Scribe.Core/TextInjection/SelectionReader.cs:275-291`), and
`NativeMethods.SendMarkedKeyEvent` (`src/Scribe.Core/Hotkeys/NativeMethods.cs:221-240`). A new synthetic
input path that leaves `dwExtraInfo` at zero is 🔴: the hook will treat Scribe's own Ctrl as a user
keypress and can start or stop a dictation from its own injection.

**And the extended-key flag.** `SendMarkedKeyEvent` sets `KEYEVENTF_EXTENDEDKEY` for right-hand modifiers
and the nav cluster (`NativeMethods.cs:209-215`), because a synthetic key-up without it maps to the
left-hand sibling and fails to release the right key. A new synthetic release of an extended key that
omits the flag is 🟡, or 🔴 when it is on the reconciler path, since a failed release is precisely the
stuck-key symptom that path exists to cure.

## §5. Clipboard: STA, retries, and two deliberately different predicates

`Win32Clipboard` states the requirement in its own summary
(`src/Scribe.Core/TextInjection/Win32Clipboard.cs:9`): *"All methods must be called on an STA thread that
owns a message queue."* `TextInjector.RunOnStaThread<T>` (`TextInjector.cs:578-608`) is the canonical
entry, and it is **P-5** in `references/patterns.md`: a dedicated `Thread` with
`SetApartmentState(ApartmentState.STA)`, `IsBackground = true`, `Start()`, then `Join()`, capturing any
worker exception and rethrowing it on the caller after the join. `SelectionReader.RunOnStaThread`
(`SelectionReader.cs:314-339`) is the sibling.

**🔴 Critical, hard flag:**

- A new call to any `Win32Clipboard` member, or a new `OpenClipboard` P/Invoke, outside a
  `RunOnStaThread` body: from a pool thread, an `async` continuation, or the WPF dispatcher with no STA
  guarantee. Cite P-5.
- `Thread.Start()` without `Join()` on an injection or clipboard sequence. That turns a synchronous
  contract into a race.
- A new clipboard open that calls `OpenClipboard` once instead of retrying. `TryOpen`
  (`Win32Clipboard.cs:302-315`) retries `OpenRetries = 6` times with `OpenRetryDelayMs = 15` because
  another process routinely holds the lock.
- A `CloseClipboard` that is not in a `finally`, or a `GlobalLock` without a matching `GlobalUnlock` in a
  `finally`, or a `GlobalAlloc` that is not freed on the failure path. `SetText`
  (`Win32Clipboard.cs:143-193`) frees the handle when `SetClipboardData` returns 0, because ownership only
  transfers to the system on success.

**Understand before judging the two predicates.** `HasNonTextContent` and `CanBorrow` look like inverses
and are deliberately not. Read the remarks on `CanBorrow` (`Win32Clipboard.cs:67-92`) before touching
either.

- `HasNonTextContent` (`Win32Clipboard.cs:23-42`) asks *"would borrowing lose anything at all"*, and
  answers yes for rich text, because restoring plain text drops the HTML and RTF companions. That is the
  right question for `TextInjector`, which has somewhere to go when the answer is yes: it falls back to
  typing (`TextInjector.cs:141-148`).
- `CanBorrow` (`Win32Clipboard.cs:93-94`) asks the narrower *"is the user's content recoverable"*: empty,
  or text-bearing. A selection read has no fallback, and reusing `HasNonTextContent` there disabled the
  whole feature for any ordinary copy from a browser, Word, Teams, or an editor, because those put
  CF_UNICODETEXT, CF_TEXT, CF_OEMTEXT, CF_LOCALE and HTML Format on the clipboard, five formats, which
  trips the more-than-four heuristic even though the text round-trips perfectly.

**🔴 Critical, hard flag:** a diff that collapses the two into one predicate, or swaps one call site to
the other guard. Name which failure it reintroduces.

**Do not touch `PrivateMarkerCount` without understanding it.** `MarkPrivate`
(`Win32Clipboard.cs:228-236`) writes three registered formats,
`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`, and
`CanUploadToCloudClipboard`, on every write this class performs. `HasNonTextContent` discounts them
(`Win32Clipboard.cs:36-41`) because not discounting them pushed a plain text item from 1 format to 4 and a
rich one past the threshold, so Scribe's own clipboard content reported as unrestorable and the paste
path refused to use it. **🔴** if the discount is removed, or if a new format is added to
`PrivateMarkerFormats` without being counted, or if a new clipboard write skips `MarkPrivate` entirely.
That last one is also a privacy egress issue; cross-reference `privacy-egress` rather than duplicating.

## §6. Focus: the target window can change mid sequence

`TextInjector.Inject` captures an `expectedForegroundWindow` and re-checks `GetForegroundWindow()`
against it before starting (`TextInjector.cs:47-50`), again on the STA worker
(`TextInjector.cs:62-65`), before the paste (`TextInjector.cs:165-174`), inside
`TryInsertIntoStandardEdit` (`TextInjector.cs:227-231`), and once per chunk in the typing loop
(`TextInjector.cs:311-314`). The single predicate is `IsExpectedForeground`
(`TextInjector.cs:107-108`), and the user-visible failure string is exactly
`"The focused window changed while processing."`, which `DictationController` maps to the
"focus changed, so the dictation was not inserted" message
(`src/Scribe.App/Dictation/DictationController.cs:947-960`). Both call sites pass a real window:
`session.TargetWindow` from the dictation loop, `capture.TargetWindow` from the text action path
(`src/Scribe.App/TextActions/TextActionController.cs:308-312`).

**🔴 Critical, hard flag:** a new injection or synthetic-input path with no foreground check, or one that
checks only on entry and then runs a multi-second loop. Typing a transcript into whatever window happened
to arrive is the worst non-privacy outcome this product has.

**🟡 Important:** a new activation that calls `SetForegroundWindow` and proceeds immediately.
Activation on Windows has two stages and only the first is observable through `GetForegroundWindow`:
`ForegroundReadiness` (`src/Scribe.Core/TextInjection/ForegroundReadiness.cs`) exists because input
delivered between "window became foreground" and "its thread restored focus to a child control" is
silently dropped, which is one or two characters at typing speed. It polls `GetGUIThreadInfo` for a
non-zero `hwndFocus` and then applies a 60 ms settle on **every** success path, including the one where
the window was already foreground on entry; returning early without the settle was the original defect.
Reuse `ForegroundReadiness.WaitForInputReady` rather than a bare `Thread.Sleep`.

**`AttachThreadInput` is not used anywhere in Scribe today.** If the diff introduces it, that is a
**Question at minimum**: Scribe reads focus through `GetGUIThreadInfo` instead, which needs no attach and
cannot deadlock two input queues together. Ask why the readiness probe is not sufficient, and if the
answer is "to read a caret position", check whether `GUITHREADINFO.rcCaret`
(`src/Scribe.Core/TextInjection/InjectionNativeMethods.cs:106-118`) already answers it. Flag 🔴 only if
the attach is left unpaired with a detach.

## §7. P/Invoke declaration hygiene

Check each new or edited declaration against what the repository already does. These are cheap to verify
and each has bitten somebody somewhere.

- **`SetLastError = true` where the error is read.** Present on `SetWindowsHookEx`
  (`Hotkeys/NativeMethods.cs:55`), `SendInput` (`InjectionNativeMethods.cs:76`), `RegisterHotKey`
  (`GlobalHotkey.cs:42`), and the clipboard and Global* imports. Flag 🟡 when a diff reads
  `Marshal.GetLastWin32Error()` after a call declared without it: the value is then whatever unrelated
  call last set it.
- **Read the error immediately.** `GlobalHotkey.cs:91-99` reads it on the line after the failing
  `RegisterHotKey`; `HotkeyService.cs:270-276` builds the `Win32Exception` immediately after the failed
  install. Flag 🟡 when a log call, a string interpolation, or another P/Invoke sits between the failure
  and the read.
- **`cbSize` / `dwSize` uses `Marshal.SizeOf<T>()`, never a literal.** Every site in this repository does:
  `Marshal.SizeOf<INPUT>()` (`TextInjector.cs:283`, `TextInjector.cs:525`, `SelectionReader.cs:272`,
  `Hotkeys/NativeMethods.cs:239`), `Marshal.SizeOf<GUITHREADINFO>()` (`TextInjector.cs:234`,
  `ForegroundReadiness.cs:108`), `Marshal.SizeOf<LASTINPUTINFO>()` (`Hotkeys/NativeMethods.cs:143`),
  `Marshal.SizeOf<DispatcherQueueOptions>()`
  (`src/Scribe.Overlay/Interop/WindowsSystemDispatcherQueueHelper.cs:38`). A hardcoded byte count is 🔴,
  because the struct is a different size on ARM64 than the number the author measured on x64 and Scribe
  ships both.
- **A managed delegate handed to a native API is held in a field.** Two live exemplars: `HotkeyService._proc`
  (`HotkeyService.cs:26`, assigned at `:58`) and `ForegroundTracker._callback`
  (`src/Scribe.App/Infrastructure/ForegroundTracker.cs:46-48`), whose comment states it plainly, the OS
  stores the native function pointer and letting the delegate be collected leaves Windows calling into
  freed memory. A new `SetWindowsHookEx`, `SetWinEventHook`, or any callback registration passing a
  freshly constructed lambda is 🔴.
- **Every acquired handle is released on every path.** `OpenInputDesktop` is paired with `CloseDesktop`
  (`Hotkeys/NativeMethods.cs:113-123`), the hook handle is unhooked on the hook thread's own exit with
  `Interlocked.CompareExchange` so a replaced thread never unhooks its successor's hook
  (`HotkeyService.cs:262-265, 306-307`), `GlobalHotkey.Dispose` unregisters and removes its hook
  (`GlobalHotkey.cs:158-174`), and the clipboard globals are freed on failure. Flag an unpaired acquire 🔴.
- **`LibraryImport` versus `DllImport`.** `Hotkeys/NativeMethods.cs:52-56` records the rule: the blittable
  calls use source-generated `LibraryImport`, and `SetWindowsHookEx` stays on classic `DllImport` because
  it marshals a managed delegate. Do not flag a `DllImport` that marshals a delegate, a `char[]`, or a
  `string` overload, and do not demand a blanket conversion. Do flag a **new** blittable-only import added
  as `DllImport` in `Hotkeys/NativeMethods.cs` as a 💡, since the file's own convention is otherwise.
- **`StartupRegistration` is registry, not P/Invoke, and must stay non-throwing.** Every method in
  `src/Scribe.App/Infrastructure/StartupRegistration.cs` swallows and reports false, because a locked-down
  registry must not crash startup, and `Sync` self-heals a stale path by comparing the stored value against
  `Environment.ProcessPath`. Flag 🟡 if a diff lets an exception escape, or hardcodes an install path
  instead of using `Environment.ProcessPath`. Same shape for `ShellIconCache.Refresh`
  (`src/Scribe.App/Infrastructure/ShellIconCache.cs:16-26`), which runs inside time-limited Velopack
  lifecycle hooks and therefore must stay synchronous and fully non-throwing.

## §8. Overlay interop

`src/Scribe.Overlay/Interop/NativeMethods.cs` styles the WinUI 3 pill in ways the AppWindow surface
cannot express. Two rules here.

- **Extended styles are read, modified, written.** `ApplyExtendedStyles`
  (`src/Scribe.Overlay/OverlayWindow.xaml.cs:103-115`) reads the current `GWL_EXSTYLE`, ORs in
  `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, and writes it back. A diff
  that writes a bare literal to `SetWindowLongPtr`, dropping whatever WinUI already set, is 🔴: the pill
  loses click-through or starts stealing focus from the window the user is dictating into.
- **Best-effort DWM calls are checked, not assumed.** `RemoveDwmFrame`
  (`OverlayWindow.xaml.cs:121-134`) captures both HRESULTs and logs them, and the header records that
  both attributes fail harmlessly on Windows 10. A diff that starts throwing on a non-zero HRESULT there
  is 🟡.
- Note that `Interop/NativeMethods.cs:9` still says *"The pill is win-x64 only"*. That comment is stale
  relative to the ARM64 story; the `*Ptr` variants are correct on both. Mention it only if the diff edits
  that file anyway, and route the packaging question to `build-packaging`.

---

## Confidence bar

**Hard flag (a Finding)** when you can point at the exact hunk line and state the mechanism in one
sentence without a hedge: *"this `SendInput` return value is discarded, so a truncated batch reports
success and the user's text silently stops mid-sentence."* The severity ladder for this lens:

- 🔴 **Critical** for anything that can kill push-to-talk, strand a key the user cannot release, drop or
  duplicate the user's text, destroy their clipboard, type into the wrong window, or leak Scribe's own
  synthetic input into the hook as if it were the user.
- 🟡 **Important** for a correctness gap with a bounded, recoverable symptom: a missing extended-key flag
  off the reconciler path, a missing `SetLastError`, a best-effort call that starts throwing.
- 💡 **Suggestion** for a convention drift with no failure mode, for example a new blittable import
  declared as `DllImport` in a file that otherwise uses `LibraryImport`. At most one per review; drop it
  entirely on a re-review.

**Raise a Question** instead when the mechanism depends on something you could not read: the cost of a
call inside the callback whose body you cannot see, whether a new timing constant was measured or guessed,
or whether an `AttachThreadInput` has a reason `ForegroundReadiness` cannot serve. Phrase it as a genuine
question with the specific fact you need.

**Never write** "this will fail the build" or "the tests will catch this". Three defects in one release
compiled warning clean and only appeared at runtime; the claim carries no weight in either direction here.

---

## Output format

The two findings below are **illustrative shapes**, not live defects, and
`SelectionWriteBack.cs` is an invented path used only to show the format. Never cite either as an
existing exemplar.

```markdown
## Win32 interop findings

🔴 **New `SendInput` in `SelectionWriteBack` discards the return count** (`src/Scribe.Core/TextInjection/SelectionWriteBack.cs:64`)

`SendInput` returns how many events it actually queued, and Windows drops the remainder when the focused
app's input queue cannot drain the batch. This call sends the whole formatted result as one array and
ignores the result, so a long write-back reports success while the target receives a prefix. That is the
same failure `TextInjector` was rebuilt around: chunk at `UnicodeChunkChars` (50), settle
`InterChunkSettleMs` (5) between batches, and resend only the unsent remainder. Route this through
`TextInjector.SendWithRetry` rather than adding a second sender.

🔴 **The watchdog's new elapsed-time check reintroduces the clock comparison that fired 3,775 times** (`src/Scribe.Core/Hotkeys/HotkeyService.cs:541`)

`IsHookDead` is answered by a monotonic callback counter for a documented reason: injected input reaches
the hook chain before `SendInput` returns, so a stamp read after the send is always newer than the
callback that answered it. Comparing `Environment.TickCount64` here restores that race, and every false
positive tears down the hook thread, resets chord state, and stops dictation in progress. Keep
`Baseline(...)` before the send and judge on the counter; see `HookLivenessProbe`'s summary and P-10.
```

**If clean:** "Win32 interop clean: the hook callback stays allocation-free and non-blocking, liveness
still judges on the monotonic counter with the baseline taken before the send, every new `SendInput`
checks its short count and resends only the remainder, clipboard work stays on the `RunOnStaThread` STA
path with bounded open retries, and the foreground window is re-checked before and during injection."

---

## Exceptions

Do not raise any of these.

- **Work on the dispatch thread, the watchdog, or the reconciler task is not "inside the callback".**
  `ConsumeTransitions`, `DispatchTransition`, `WatchdogTick`, and the `ScheduleReconcile` continuation all
  log, allocate, and take `_sync`. That is correct; only `HookCallback` and what it calls synchronously
  are on the deadline.
- **`SelectionReader.SendCtrlC` checking only for zero is not a missing short-count check.**
  `CaptureCore` (`SelectionReader.cs:183-201`) treats zero as "Windows refused the copy" and then proves
  the copy landed by polling a clipboard it emptied first (`WaitForClipboardText`). A partial chord is
  caught by the poll, not by the count. Only flag this file if the diff changes that proof.
- **`SelectionReader.RestoreClipboard` restoring unconditionally is deliberate.** Its remarks record why
  the sequence-number guard was removed: Scribe empties the clipboard and the target then writes to it,
  two bumps of Scribe's own making, so the guard fired on every capture and the restore never ran. Do not
  ask for the guard back.
- **`TextInjector`'s `Thread.Sleep` calls are measured, not lazy.** `PasteSettleDelayMs = 130`,
  `ClipboardSettleDelayMs = 30`, `InterChunkSettleMs = 5`, and `ForegroundReadiness.SettleMs = 60` each
  carry a comment. Do not propose replacing them with `async`/`await`: the whole sequence is synchronous
  by contract on an STA worker that is joined.
- **`Win32Clipboard` being text-only is a stated scope decision**, not an oversight
  (`Win32Clipboard.cs:6-9`). Do not ask for image or file format preservation.
- **`app.manifest` requesting `asInvoker` with `uiAccess="false"` is settled.** Injecting into elevated
  windows needs uiAccess plus a signed install and was deferred past v1. That is why a UIPI-rejected
  `SendInput` is reported as `Failed` rather than healed
  (`SuppressedKeyReconciler.cs:26-27`, `HotkeyService.cs:489-495`). Do not reopen it.
- **Pre-existing interop the diff only moves past** is out of scope. This lens reviews what changed.
- **The em dash in `Win32ClipboardTests` and `Scribe.InjectionLab` is deliberate.** Those round-trip
  U+2014 on purpose to prove Unicode survives the clipboard and injection paths, and AGENTS.md names them
  as the two exceptions to the repository-wide dash ban. Never flag them.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:win32-interop findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
