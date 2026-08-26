# Tests regression pin review lens

You answer one question, in two halves: **if I mentally revert the production change, does the new
test fail, and does that test construct the exact state that triggered the bug?**

**Dispatch trigger (conditional).** You fire only when the change is a bug fix:

- the title starts with `fix:` or `hotfix:`, **or**
- the body contains `fixes #`, `closes #`, or `resolves #`.

The body link is the broad net, the title is the false-fire filter. When you are dispatched and the
title is `refactor:` or `chore:` **and** the linked issue is not actually a bug (a tech-debt cleanup
ticket, a docs task, a dependency chore), this is a false fire: **exit silently**, emit nothing, not
even a clean-pass line. A `feat:` change that closes a bug-labelled issue still needs a pin.

**Severity cap: 🔴 Critical.** A bug fix landing with no regression pin is a correctness gap, not a
style preference: the bug re-opens at any future refactor, and in this repository the refactor is
usually an agent moving logic into `Scribe.Core` six months later with no memory of why the shape
was that way. **Findings cap: 3.**

**Data on disk.** Read `diff.patch` and `metadata.json` from the cache. On a re-review round, your
scope is `delta.patch`; the full diff is context. Identify the bug from the title, the body, and the
linked issue before you read a single test.

`diff.patch` is authoritative for what the change adds. Do not use Read or Grep to confirm a diff
line exists on disk, because the reviewed branch may not be checked out. Do use Read and Grep freely
for the surrounding code: the production file's `why` comments, the sibling tests in
`tests/Scribe.Core.Tests/`, and the exemplars named below.

---

## §0. Evidence map before any verdict

Before you flag or clear, confirm you can name each of these. If you cannot, say which one is
missing instead of concluding. A regression-pin verdict built on a production hunk you never
located is exactly the confidently-wrong review this skill exists to avoid.

1. **The bug.** What failed, for whom, under what condition. One sentence, taken from the issue or
   the body, not inferred from the fix.
2. **The production hunk that IS the fix.** The specific `+`/`-` block whose removal restores the
   bug. A bug fix usually also carries refactoring, renames, and log lines; those are not the fix.
3. **The added or changed test that claims to pin it**, by file and test method name.
4. **The state the bug required.** Not the symptom: the precondition. "The key-up was suppressed
   while the system still believed the key was down", not "the key got stuck".
5. **The seam the test uses to construct that state.** In this codebase that is almost always a
   delegate or a constructor argument, not a mocking framework.
6. **The assertion that observes the outcome**, and which channel it observes.

**Two structural facts about this repository that change your verdict, so establish them first.**

- **`tests/Scribe.Core.Tests` references only `src/Scribe.Core`.** See
  `tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj:23`, whose single `ProjectReference` is
  `Scribe.Core.csproj`. Nothing in `src/Scribe.App` (the WPF tray shell) or `src/Scribe.Overlay` (the
  separate WinUI 3 process) is reachable from the unit suite. A fix that lands entirely in either
  project cannot be pinned by a unit test today. That is a **documented gap plus a Question**, per
  §6, not a 🔴 for a missing test the author could not have written.
- **Internal types are reachable.** `src/Scribe.Core/Scribe.Core.csproj:40` carries
  `<InternalsVisibleTo Include="Scribe.Core.Tests" />`. So `internal sealed class
  SuppressedKeyReconciler`, `internal sealed class HookLivenessProbe`, and the internal statics on
  `TextInjector` are all directly testable. "It is internal" is **never** an accepted reason a Core
  fix went unpinned.

---

## §1. Locate the pin

Identify which added or changed test asserts the **new** behavior. If none exists, that is the
finding, 🔴 Critical, regardless of how much test churn the diff carries.

Things that are not a pin, and that this repository produces a lot of:

- **Test files moved, renamed, or reformatted.** Movement is not coverage.
- **A larger test count.** `AGENTS.md` states the suite count only ever grows, so "+18 tests" in the
  diff is not evidence that any of them touch this bug.
- **A test added for a neighbouring behavior in the same file.** A fix to `SuppressedKeyReconciler`
  and a new case for `CandidateKeys` are not the same thing.
- **An assertion loosened so the existing suite goes green again.** That is `tests-quality`'s §11
  territory; if it is the *only* test change on a bug fix, it is also your finding, because the
  change has no pin at all.

---

## §2. Mentally revert, then walk the test

For each candidate pin:

1. Take the production hunk you named in §0 step 2.
2. Imagine reverting **just that block**, leaving every other line of the change in place.
3. Walk the test line by line. Does the assertion fail?

If it passes on both old and new code, flag it and **name the reason**. The four that recur:

- **The fake short-circuits the fix path.** The delegate the test supplies never reaches the changed
  branch.
- **The assertions do not touch the changed behavior.** The test asserts a neighbouring property
  that was correct before the fix.
- **The test exercises a different branch than the bug.** Common when the fix adds a guard and the
  test drives the already-guarded case.
- **The test asserts a value the fake supplied.** This one has a specific shape here, because Scribe
  injects behavior as plain delegates rather than through a mocking framework.
  `SuppressedKeyReconciler` takes three `Func<uint, bool>` (see
  `src/Scribe.Core/Hotkeys/SuppressedKeyReconciler.cs:39`). A test whose `releaseKey` delegate
  returns `true` and then asserts the key appears in `Result.Released` is asserting on what the fake
  returned, **unless** `isLogicallyDown` and `isPhysicallyPressed` were set up to disagree. The
  disagreement is the bug; the release is just bookkeeping.

Say which reason applies. "This test does not pin the fix" without the mechanism is not actionable
and will be dropped by `finding-verification`.

---

## §3. The precondition must construct the exact state

A pin builds the *exact* state that triggered the bug, not a state that resembles it. Scribe's bugs
are mostly ordering, timing, and state-disagreement bugs, so the resemblance trap is the normal
failure here rather than the exotic one.

### The Scribe bug classes, and what a pin must construct for each

**A hook deadline miss.** Windows caps a `WH_KEYBOARD_LL` callback at `LowLevelHooksTimeout` (1000 ms
since Windows 10 1709) and a late callback means **one key event is delivered past the hook**. The
consequence is a *disagreement*: the system's logical key state says down, the hook's physical view
says released, and because the hook keeps swallowing that key the user can never release it. The
mechanism is written out at `src/Scribe.Core/Hotkeys/SuppressedKeyReconciler.cs:5-14`.

- **A pin must set up the disagreement**: `isLogicallyDown` true, `isPhysicallyPressed` false, then
  assert the synthetic release is injected for exactly that key. Model:
  `Reconciler_releases_only_keys_the_system_holds_but_the_hook_saw_released`
  (`tests/Scribe.Core.Tests/HotkeyServiceTests.cs:279`), with its negative twin
  `Reconciler_never_releases_a_key_the_user_genuinely_holds` (line 295) proving a genuinely held
  modifier survives reconciliation.
- **Not** a pin that pre-sets a stuck state and asserts the recovery path runs. That skips the
  moment the bug happens and tests a different branch.

**An ordering hazard around `SendInput`.** Injected input is dispatched into the hook chain **before
`SendInput` returns**, so a hook callback can land *during* the send. That ordering was the entire
bug in the watchdog: the old code baselined after the send returned, so the callback the send itself
raised always looked older than the probe, and 13.3 percent of watchdog ticks declared a healthy
hook dead, each one tearing down the hook thread and stopping any dictation in progress. See
`src/Scribe.Core/Hotkeys/HookLivenessProbe.cs:10-22`.

- **A pin must place the event inside the window.** Model:
  `Callback_raised_during_the_send_answers_the_probe`
  (`tests/Scribe.Core.Tests/HookLivenessProbeTests.cs:30`), which increments the callback count
  *between* `Baseline()` and `Arm()`.
- **Not** `Callback_raised_after_the_send_returns_answers_the_probe` (line 43) on its own. That
  ordering is the easy case and never exhibited the bug. A change that adds only the after case has
  not pinned anything.

**A `SendInput` short count.** `SendInput` can report fewer events delivered than requested when the
input stream is momentarily blocked. `TextInjector.SendWithRetry`
(`src/Scribe.Core/TextInjection/TextInjector.cs:523`) advances `offset` by the reported count and
resends **only** `inputs[offset..]`, bounded by `MaxChunkRetries`.

- **A pin must return a short count and assert only the remainder is resent**, and that the retry
  loop is bounded. A test that sends a full count and asserts one call proves nothing about the
  short-count path.
- **Honest caveat, check before you demand it.** `SendWithRetry` is `private` and calls the static
  `InjectionNativeMethods.SendInput` P/Invoke directly, so there is no injectable seam in the current
  code. `TextInjectorTests` and `TextInjectorUnicodeChunkTests` cover the pure chunking helpers
  instead (`ChunkLength`, `BuildUnicodeChunk`, `CountKeyEvents`). If the change adds the seam, hold
  it to the rule above. If it does not, the finding is that the fix is unpinnable as written, and the
  ask is the seam, not a test that cannot exist.

**A `Clone` aliasing bug.** `AppSettings.Clone` starts from `MemberwiseClone` and then explicitly
rebuilds every reference-typed member, because a shared list means an edit in the settings editor
mutates the snapshot the dictation loop is reading
(`src/Scribe.Core/Models/AppSettings.cs:301-316`).

- **A pin must mutate the clone and assert the original is unchanged.** Models:
  `Clone_deep_copies_profiles` (`tests/Scribe.Core.Tests/AppProfileTests.cs:83`) and
  `Clone_deep_copies_enabled_dictionary_libraries` (`tests/Scribe.Core.Tests/PersistenceTests.cs:80`).
- **Not** a test asserting `Clone()` returned non-null, or that the clone's list has the expected
  contents. Both hold on the aliasing bug, because a shared list has the right contents right up
  until somebody writes to it.

**A `CreateDefault` versus deserialization bug.** Deserialization fills a property initializer for
any key the stored JSON does **not** contain, so a first-run opt-in expressed as `= [...]` on the
property silently opts an existing install into something that did not exist when it was installed.
That is why `CreateDefault` (`src/Scribe.Core/Models/AppSettings.cs:296`) is deliberately distinct
from `new AppSettings()`; the reasoning is recorded at lines 92-99.

- **A pin must deserialize JSON that OMITS the key and assert the opt-in did not appear.** Model:
  `Existing_install_predating_the_setting_is_not_opted_in`
  (`tests/Scribe.Core.Tests/DefaultLibraryOptInTests.cs:67`), which writes
  `{"hotkey":null,"enableAiCleanup":true}` straight into the settings store and asserts the list
  comes back empty.
- **Not** a test that round-trips a fully populated settings object. That never exercises the absent
  key, which is the only case the bug had.

**A SQLite migration bug.** `ScribeDatabase.Migrate`
(`src/Scribe.Core/Persistence/ScribeDatabase.cs:383`) is forward-only and additive, gated on
`PRAGMA user_version`, with every step inside one transaction.

- **A pin must start from each prior `user_version` the fix claims to repair**, by creating a
  database at that schema and opening it through `ScribeDatabase`. Models:
  `V6_migration_adds_transcription_model_id_to_v5_history` and
  `V4_migration_reopens_v3_purges_exact_junk_and_advances_schema`
  (`tests/Scribe.Core.Tests/SnippetMigrationTests.cs:13` and `:58`), which write the old `CREATE
  TABLE` plus `PRAGMA user_version=5;` (respectively `=3;`) by hand and then assert both the column
  and the resulting `user_version`.
- **Not** a test that opens a fresh in-memory database. On a fresh database the full `CREATE TABLE`
  runs and the migration path the bug lived in never executes.
- A schema or migration change is also an **"Ask first"** item in `AGENTS.md`, so expect
  `maintainer-decision` to fire alongside you. Do not duplicate that; stay on whether the pin exists.

**A pipe protocol bug.** The WPF engine drives the overlay one way over a named pipe. The parsing
lives in `OverlayIpcServer.Dispatch` (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:87`), which splits
on the first space, upper-cases the verb, and switches; the sending side is
`src/Scribe.App/Overlay/OverlayProcessClient.cs`.

- **The craft rule holds: if the bug was in string parsing, a test that calls the window method
  directly proves nothing.** `_window.SetAnchor(anchor)` succeeding says nothing about whether
  `POSITION BottomRight` reaches it.
- **The structural reality, which you must check before flagging.** Neither `Scribe.Overlay` nor
  `Scribe.App` is referenced by the unit suite (§0), so `Dispatch` is unreachable from
  `tests/Scribe.Core.Tests` as the projects stand. For a fix on that side, the correct output is a
  **Question** naming the gap plus the verification `AGENTS.md` prescribes: run it and read
  `%LOCALAPPDATA%\ScribeData\logs\scribe-<date>.log` for `installer layout`, `size=462x192`,
  `transparent=True backdrop=TransparentBackdrop`, a surviving overlay PID, and zero `IOException`s
  after launch.
- **What is pinnable is the Core half of the contract.** `Scribe.Core.Models.OverlayPosition`
  (`src/Scribe.Core/Models/Enums.cs:29`) supplies the wire tokens and its twin
  `Scribe.Overlay.OverlayAnchor` (`src/Scribe.Overlay/OverlayAnchor.cs:8`) is kept in sync **by
  name** with no compiler check and no test in either direction. A fix that adds or renames a value
  in one enum and not the other is a silent no-op at runtime; check both by eye and say that you did.

**A fail-closed privacy bug.** Code sitting between dictated text and anything that could retain or
transmit it must return the safe value when it meets a shape it does not recognize. Exemplar:
`TextCleanupService.WithStoredOutputDisabled`, which the Azure Responses API forces because that API
defaults to `store=true` and would otherwise retain every cleaned dictation server side.

- **A pin must supply an unrecognized raw representation and assert the safe value.** This is exactly
  what `Stored_output_override_fails_closed_on_an_unrecognised_raw_representation`
  (`tests/Scribe.Core.Tests/TextCleanupServiceTests.cs:140`) does: it sets
  `RawRepresentationFactory = _ => new object()` and asserts `StoredOutputEnabled` is still false.
  **Point at that test by name.** It is the model for the whole family.
- **Not** a pin that only asserts the happy path sets the flag false, and not one that only asserts
  a caller-supplied `CreateResponseOptions` gets overridden (that is line 121, the recognized
  shape). The unrecognized shape is the branch that fails closed.
- `AGENTS.md` states this behavior is pinned by a test and must not be relaxed, so a diff that
  weakens it is `guardrail-erosion`'s 🔴 as well as yours. Defer to whichever states it more
  specifically; synthesis dedups.

---

## §4. Negative assertions need the channel captured

A pin whose assertion is a negative, "no exception thrown", "nothing was logged", "the callback was
not invoked", "the process did not die", only pins the fix if the test **captures the channel the
signal would arrive on**. If it does not, the absence holds on the buggy code too, your §2 mental
revert passes falsely, and the pin proves nothing.

This bites hard in Scribe because the logging mandate makes swallowing the normal, correct behavior.
`FileLoggerProvider`, `OverlayLog`, and `ResilientEvent.InvokeAll` are all deliberately non-throwing
end to end, so "it did not throw" is true of virtually every code path here, fixed or broken.

Confirm the test observes the channel, or convert the pin to a positive post-condition. The model is
`ResilientEventTests` (`tests/Scribe.Core.Tests/ResilientEventTests.cs`): rather than asserting that
a throwing handler does not blow up, `EveryFailureIsReported` (line 22) passes a reporter delegate
and asserts the collected messages are `["one", "two"]`, and
`AHandlerThatThrowsDoesNotStopTheOnesRegisteredAfterIt` (line 8) asserts the *reached* list, not the
absence of an exception. Both would fail on the bug; a bare "does not throw" would not.

The general "structurally incapable of failing" framing belongs to `tests-quality` §6. This is only
the regression-pin angle. When both fire on the same test, synthesis keeps the more specific one.

---

## §5. A pin that cannot fail where it runs

Two shapes look like coverage and are not.

**A model-gated test that returns early.** `TranscriptionServiceTests` and its siblings check
`locator.Resolve().AsrComplete` and `return` when the roughly 670 MB of speech models are absent
(`tests/Scribe.Core.Tests/TranscriptionServiceTests.cs:20` and `:39`). CI does download them
(`.github/workflows/ci.yml:61-64`), but a contributor machine that has not run
`scripts/Download-Models.ps1` sees a silent pass. A regression pin placed behind that gate is not a
pin on any machine without the models. If the fix genuinely needs the recogniser, say so and route
it to §6.

**A pin skipped or suppressed in the same change.** A checked-in `[Fact(Skip = "...")]`, a removed
test, a new `#pragma warning disable`, or a `NoWarn` addition next to the fix is
`guardrail-erosion`'s 🔴. Note it in one line and let that lens own it; do not spend one of your
three findings on it.

---

## §6. Confidence bar

**Hard flag (🔴 Critical)** only when you can point at the production hunk *and* state the mechanism
by which the test survives its removal. You are asserting a fact about the diff, so it must be
checkable from the diff:

- No test in the change asserts the new behavior at all, and the fix is in `src/Scribe.Core`.
- You walked the test and named which of §2's four reasons makes it pass on the reverted code.
- The precondition builds a resembling state rather than the exact one, and you can name the
  specific difference against the §3 model for that bug class.

**Raise a Question instead** when the gap is real but the ask is not the author's to satisfy, or when
you cannot fully resolve it from the diff:

- The fix lands in `src/Scribe.App` or `src/Scribe.Overlay`, which the unit suite cannot reach.
- The bug is a genuine OS race, hardware-specific behavior, or anything needing the native speech
  engine.
- The production hunk is spread across several files and you cannot isolate the one block whose
  revert restores the bug. Say that, and say what you would need.
- The pin exists and looks thin, but the seam that would make it exact is not present in the code
  and the change did not add it.

**Never** write "likely", "probably", "seems", or "may be" in a finding. If the diff substantiates
it, drop the hedge. If it does not, this is a Question. And never write that the build or the suite
will catch it: `AGENTS.md` records three defects in one release that compiled warning clean and only
appeared at runtime.

---

## §7. Output format


The verdicts below are **illustrative shapes**, not live defects. `SuppressedKeyReconcilerTests` is
invented; the real reconciler pins live in `tests/Scribe.Core.Tests/HotkeyServiceTests.cs`. Never cite
the invented class as an existing exemplar.

Emit one block headed `## Regression pin verdict`, at most three findings.

Missing pin:

```markdown
## Regression pin verdict

🔴 **Bug fix lands with no test that pins it**

Title: `fix: stop the watchdog reinstalling a healthy hook (closes #74)`. The fix is the move to a
monotonic callback counter in `src/Scribe.Core/Hotkeys/HookLivenessProbe.cs`, but no test in the
change places a callback between `Baseline()` and `Arm()`. Reverting to the tick comparison leaves
the whole suite green, and the next refactor of the watchdog silently reopens a fault that fired on
13.3 percent of production ticks and stopped dictation each time.

Construct: baseline at N, increment the counter (the callback the send itself raises), arm with
`sendSucceeded: true`, assert `IsHookDead` is false. That is
`tests/Scribe.Core.Tests/HookLivenessProbeTests.cs:30`; the change needs the equivalent for the new
code path.
```

Pin present but not exact:

```markdown
## Regression pin verdict

🔴 **The new test builds a state that resembles the bug, not the state that caused it**

`SuppressedKeyReconcilerTests.Stuck_key_is_released` pre-sets `isLogicallyDown` and
`isPhysicallyPressed` both true, then asserts the recovery path runs. The bug is the disagreement:
the key-up was suppressed while the system still believed the key was down, so a pin needs
`isLogicallyDown` true and `isPhysicallyPressed` false. As written the assertion holds on the
reverted code, because that branch was already reachable.

Construct: mirror `Reconciler_releases_only_keys_the_system_holds_but_the_hook_saw_released`
(`tests/Scribe.Core.Tests/HotkeyServiceTests.cs:279`) and keep its negative twin at line 295, so a
modifier the user genuinely holds is still never released.
```

Pin is solid:

```markdown
## Regression pin verdict

✅ `DefaultLibraryOptInTests.Existing_install_predating_the_setting_is_not_opted_in`
(`tests/Scribe.Core.Tests/DefaultLibraryOptInTests.cs:67`) pins the fix.

- Writes settings JSON that omits the new key, which is the exact state an existing install has
- Loads through `SettingsRepository`, so deserialization actually runs
- Asserts the opt-in list is empty, a positive observation of the value the bug produced wrongly
- Reverting the move from the property initializer to `CreateDefault` makes this assertion fail
```

**Clean pass line**, when a pin exists, is exact, and survives the mental revert:

> Regression pin verdict: clean. The fix is pinned by a test that constructs the exact triggering
> state and fails when the production block is reverted.

---

## §8. Exceptions

Do not flag any of these.

- **A bug that is infeasible to pin in a unit test.** A genuine OS race, hardware-specific behavior,
  or anything requiring the native speech engine, which the unit suite deliberately never loads
  (`AGENTS.md`, "Architecture support", and the comment at `.github/workflows/ci.yml:78-81`). Raise a
  **Question** plus a documented gap. Where the native engine is involved, the ask is to run
  `dotnet run --project tools/Scribe.AsrCheck` (after `pwsh ./scripts/New-SpeechFixtures.ps1`), which
  is the only thing that proves sherpa-onnx actually decodes; where the overlay is involved, the ask
  is the live-log check in §3.
- **A doc-only or config-only fix.** Out of scope entirely, even under a `fix:` title.
- **A fix in `src/Scribe.App` or `src/Scribe.Overlay` with the logic left there.** The unit suite
  cannot reach it. Whether that logic should have been extracted into `Scribe.Core` with a test is
  `core-app-layering`'s finding, not yours. Your output is the Question naming the gap.
- **A test that landed in a different commit of the same change.** Check the whole diff, not the
  head commit, before flagging.
- **A pin that is correct but stylistically unlike its neighbours.** You judge whether it can fail,
  not whether it reads nicely.
- **Coverage for code the change did not touch.** That is `tests-coverage`'s call.
- **A pin for a behavior the change deliberately altered.** If the linked issue asked for the old
  behavior to change, the updated assertion is the pin, not a loosened one. Confirm the body says so,
  note it as verified, and move on.
- **A hypothesis about the root cause that the change did not claim.** `AGENTS.md` records a case
  where three plausible hypotheses about a dictation failure were all wrong when measured against the
  real engine. Do not demand a pin for a mechanism nobody asserted.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:tests-regression-pin findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
