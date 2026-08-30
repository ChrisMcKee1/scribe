# Scribe canonical patterns catalog

The vocabulary of blessed shapes in this repository. Each entry names a recurring **situation**, the
**canonical shape** Scribe already uses for it, a live **exemplar** with a file path, and the
**anti-pattern** it replaces. This is the rubric for `agents/architecture-fit.md`: when a change
introduces a construct matching a situation below, it should reuse the canonical shape rather than
hand-roll a parallel one.

Most of these shapes exist because the alternative shipped a bug. Where that is true the entry says so,
because the incident is the argument.

**Staleness guard.** Exemplars move and line numbers drift. Before citing one in a finding, `Grep` for
the named symbol to confirm it still exists. If it is gone, find the current exemplar or drop the
citation. A dead citation in a review is worse than no citation. Line numbers below were correct when
this file was written; treat them as a hint and the symbol name as the anchor.

**Reuse is a default, not a law.** Diverging is correct when the situation genuinely differs. Hard-flag
only a clean one-to-one match with no contextual reason to differ; otherwise raise it as a Question.

**Catalog budget: 12 entries.** Add one only when a shape has at least two exemplars and a clear
anti-pattern, and retire one when you add a thirteenth. A catalog nobody can hold in mind stops being a
rubric.

---

## Layering: Core owns the decisions, the shell owns the pixels

### P-1: Pure decider in `Scribe.Core`, thin adapter in the WPF window

- **When:** a settings page, a tray action, or a dialog needs to validate, merge, deduplicate, plan, or
  transform user input.
- **Use:** a pure static type in `src/Scribe.Core/Settings/` (or the matching Core folder) that takes a
  plain input record and returns a result record. The WPF window maps its editable row type to that
  input, calls the Core type, and renders the result. The Core type gets a test; the window gets none
  and needs none.
- **Exemplars:** `DictionaryEntryBuilder.Build` (`src/Scribe.Core/Settings/DictionaryEntryBuilder.cs`),
  with its `readonly record struct Row` and `Result` carrying `DuplicateIndex`, called from
  `src/Scribe.App/Settings/SettingsWindow.xaml.cs` (around line 4280). Siblings that follow exactly the
  same shape: `SnippetBuilder`, `ProfileBuilder`, `DictionaryImportMerger.Merge`,
  `DictionaryLibraryOverlapAnalyzer`, and `QuickDictionaryAdd` (`Tokenize`, `Toggle`, `Select`, `Build`,
  `Apply`), which `src/Scribe.App/QuickAdd/QuickAddWindow.xaml.cs` describes in its own header as
  "the surface" over decisions that live in Core.
- **Not:** duplicate detection, trimming rules, merge planning, or preset selection written inline in a
  `.xaml.cs`. `AGENTS.md` names this by name: *"Do not let logic drift back into the code-behind; that
  is a recurring smell."* `SettingsWindow.xaml.cs` is already more than 5,000 lines, which is precisely
  why the next decision must not land there.
- **Tell:** a new `private bool Validate...` or `private List<...> Build...` method in a `.xaml.cs` with
  no corresponding test. Ask what the pure input and output types would be; if you can name them, the
  method belongs in Core.

### P-2: Reuse the real implementation, never a private copy of a rule

- **When:** a second surface needs to apply a rule the main pipeline already applies: one dictionary
  entry, one formatting pass, one normalization step.
- **Use:** expose a narrow entry point on the real implementation and call it. Take the same
  normalization, the same compiled matcher, the same guards.
- **Exemplar:** `TextPostProcessor.ApplyRule` (`src/Scribe.Core/PostProcessing/TextPostProcessor.cs`,
  around line 278). Its doc comment states the reasoning outright: it exists so the quick add popup can
  repair the transcript a correction came from, and it *"deliberately calls the real implementation
  rather than reproducing it, because a private copy of the matcher drifts silently: it would hand the
  user a 'corrected' transcript that disagrees with what their very next dictation actually produces."*
- **Not:** a small local regex or string replace in the calling window that "does the same thing". It
  will not, the moment the real matcher changes.
- **Tell:** a new `Regex` or `string.Replace` in `Scribe.App` that operates on transcript text.

---

## Never let plumbing take down the product

### P-3: Fan out a multicast event through `ResilientEvent.InvokeAll`

- **When:** one producer raises an event that several independent subscribers care about, and one of
  them is a UI object with a lifecycle of its own.
- **Use:** `ResilientEvent.InvokeAll(handler, argument, onError)`
  (`src/Scribe.Core/Infrastructure/ResilientEvent.cs`). It walks the invocation list itself, so a throw
  from one subscriber is reported and the remaining subscribers still run. The error callback is itself
  wrapped, because a logger that throws must not stop the fan-out it was only meant to describe.
- **Exemplar:** `DictationController.Raise` (`src/Scribe.App/Dictation/DictationController.cs`, around
  line 1153) for `StateChanged`. Pinned by `tests/Scribe.Core.Tests/ResilientEventTests.cs`.
- **Not:** `Handler?.Invoke(x)` inside a single `try`/`catch`. It looks safe and is not: .NET stops
  walking the invocation list at the first exception. In production a disposed tray icon threw out of
  the first dictation-state subscriber, and every later subscriber, including the recording overlay,
  froze on whatever state it last saw while dictation kept working. Note that the sibling
  `CleanupFailed?.Invoke` a few lines above still uses the plain shape; do not copy that line for a new
  multicast event.
- **Tell:** a new `public event Action<T>` in Core or App raised with `?.Invoke`.

### P-4: Diagnostics that cannot take down the thing they describe

- **When:** writing to the shared daily log, or logging anything from a path whose `catch` does something
  destructive.
- **Use, on the writer side:** open with `FileShare.ReadWrite`, retry a bounded number of times on
  `IOException`, and swallow. Both processes append to the same file
  (`%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log`) so dictation and overlay events interleave on
  one timeline, and a plain `File.AppendAllText` throws a sharing violation.
  - Exemplars: `FileLoggerProvider.Append` (`src/Scribe.App/Infrastructure/FileLoggerProvider.cs`,
    around line 86) and `OverlayLog.Write` (`src/Scribe.Overlay/Logging/OverlayLog.cs`, around line 53).
- **Use, on the caller side:** route any diagnostic near a destructive `catch` through a local
  non-throwing `TryLog`.
  - Exemplar: `OverlayProcessClient.TryLog` (`src/Scribe.App/Overlay/OverlayProcessClient.cs`, around
    line 362). Its comment records the incident: the launch log line previously sat inside the `try`, a
    transient log-file lock threw there, the surrounding `catch` read that as a launch failure and called
    `KillProcess()`, and that was a root cause of the intermittent "pill disappears" regressions.
- **Not:** a logging call inside a `catch` that kills a process, tears down a window, or disables a
  feature. Not a log writer that opens without `FileShare.ReadWrite`. Not a retry loop that rethrows
  after its last attempt.
- **The privacy half of this contract is P-8.** Log shapes, not content.
- **Tell:** any new `_logger.Log*` or `OverlayLog.*` call inside a `catch` block in
  `src/Scribe.App/Overlay/**`, `src/Scribe.Core/Hotkeys/**`, or `src/Scribe.Core/TextInjection/**`.

---

## Win32 and the second process

### P-5: STA thread plus `Join` for clipboard and injection work

- **When:** code touches the Win32 clipboard or drives a save, set, paste, restore sequence.
- **Use:** run the whole sequence on a dedicated `Thread` with
  `SetApartmentState(ApartmentState.STA)`, `IsBackground = true`, `Start()`, then `Join()`. Capture any
  exception on the worker and rethrow it on the caller after the join, so the caller sees a normal
  failure rather than a lost thread.
- **Exemplar:** `TextInjector.RunOnStaThread<T>` (`src/Scribe.Core/TextInjection/TextInjector.cs`, around
  line 578). `Win32Clipboard` (`src/Scribe.Core/TextInjection/Win32Clipboard.cs`) states the requirement
  in its own summary: *"All methods must be called on an STA thread that owns a message queue."*
- **Also part of this shape:** open the clipboard with bounded retries rather than once, restore what was
  there afterward, and check the foreground window is still the expected one before and after, because
  the target can change mid-sequence.
- **Not:** calling `Win32Clipboard` from a thread pool thread, an `async` continuation, or the WPF
  dispatcher without an STA guarantee. Not `Thread.Start()` without `Join()`, which turns a synchronous
  contract into a race.
- **Tell:** a new call to any `Win32Clipboard` member, or a new `OpenClipboard` P/Invoke, outside a
  `RunOnStaThread` body.

### P-6: Newline-delimited pipe commands with by-name enum twins

- **When:** the WPF engine needs to drive the WinUI overlay process.
- **Use:** a one-way named pipe carrying newline-delimited `COMMAND arg` lines. The server upper-cases
  the verb, switches on it, parses the argument, and on an unrecognized verb or argument logs a warning
  and ignores it rather than throwing. Wire tokens for a position are the **value names of two enums
  kept in sync by name**, because the overlay deliberately holds no reference to `Scribe.Core`.
- **Exemplars:** `OverlayIpcServer.Dispatch` (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs`, around line
  87) handling `RECORDING`, `WARNING`, `PROCESSING`, `FAILED`, `HIDE`, `METER`, `POSITION`, `WARMUP`,
  `EXIT`; the sending side in `src/Scribe.App/Overlay/OverlayProcessClient.cs`. The enum twins are
  `Scribe.Core.Models.OverlayPosition` (`src/Scribe.Core/Models/Enums.cs`, around line 29) and
  `Scribe.Overlay.OverlayAnchor` (`src/Scribe.Overlay/OverlayAnchor.cs`), whose own doc comment records
  the contract.
- **Also part of this shape:** the client replays the applied `POSITION` right after every pipe
  reconnect, so a relaunched overlay keeps the user's anchor; high-frequency `METER` commands are
  deliberately not logged; and the overlay is launched into an OS Job Object with kill-on-close **and**
  runs a `--parent` PID watchdog, so the pill can never outlive the engine.
- **Not:** adding a value to one enum and not the other, which makes the overlay silently ignore the
  command with only a warning in the log. Not a two-way or request/response protocol; the pipe is one
  way by design. Not throwing on an unknown verb, which would kill the reader loop.
- **Tell:** a diff that touches `OverlayPosition` or `OverlayAnchor` without touching the other, or adds
  a `case` in `Dispatch` with no sender, or an `Enqueue("...")` with no `case`.

---

## Settings, secrets, and stored state

### P-7: `AppSettings` growth: `CreateDefault` for opt-ins, explicit deep copy in `Clone`, DPAPI for secrets

- **When:** adding a property to `src/Scribe.Core/Models/AppSettings.cs`.
- **Use:** three separate decisions, each of which has bitten before.
  1. **First-run opt-ins live in `CreateDefault`, not in the property initializer.** `CreateDefault`
     (around line 296) is deliberately distinct from `new AppSettings()`. `EnabledDictionaryLibraryIds`
     documents why: deserialization fills the property initializer for any key the stored JSON does not
     contain, so a default expressed as an initializer silently opts an existing install into a library
     that did not exist when it was installed.
  2. **A reference-typed property is deep copied in `Clone`** (around line 301). `Clone` starts from
     `MemberwiseClone` and then explicitly rebuilds `Profiles` and `EnabledDictionaryLibraryIds`, because
     a shared list means an edit in the settings editor mutates the snapshot the dictation loop is
     reading. A plain value type needs nothing and the code says so.
  3. **A secret carries `[JsonConverter(typeof(DpapiProtectedStringConverter))]`**
     (`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs`), which keeps it encrypted at rest under
     the current user with extra entropy, exposes the plaintext only in memory, and returns null rather
     than throwing when decryption fails so a settings file copied between machines prompts re-entry
     instead of bricking load. The Azure API key, the client secret, and the OpenAI-compatible key all
     use it.
- **Not:** a new `List<T>` or `Dictionary<,>` property left out of `Clone`. Not a default expressed only
  as `= true`. Not a secret stored in the clear, in an environment variable, in a `.env`, or in a script.
  `AGENTS.md` is explicit that persistent `AZURE_CLIENT_*` variables would hijack every other Azure tool
  on the machine.
- **Tell:** any `+` line adding a property to `AppSettings` where `Clone` and `CreateDefault` are
  unchanged in the same diff.

### P-8: A privacy control that fails closed, pinned by a test

- **When:** code sits between dictated text and anything that could retain or transmit it.
- **Use:** set the safe value explicitly, and when the code meets a shape it does not recognize, return
  the safe value rather than passing the unknown shape through.
- **Exemplar:** `TextCleanupService.WithStoredOutputDisabled`
  (`src/Scribe.Core/Cleanup/TextCleanupService.cs`, around line 1854). The Azure Responses API defaults
  to `store=true`, which would retain every cleaned dictation server side. Scribe sets
  `StoredOutputEnabled = false` through `ChatOptions.RawRepresentationFactory` on both the project and
  account paths, and when the inner factory returns something that is not a `CreateResponseOptions` it
  builds a fresh one with the flag off rather than forwarding the unknown object. The comment states the
  rule: *"Fail CLOSED."* Pinned by `tests/Scribe.Core.Tests/TextCleanupServiceTests.cs`, which asserts
  the flag is false even when a caller-supplied factory tries to set it true.
- **Related shapes in this family:** `SessionBanner`, whose contents are asserted secret-free by
  `SessionBannerTests.Banner_never_contains_a_secret`; `DiagnosticsBundle`, which ships the retained logs
  and a report and never `scribe.db`, because the database holds every dictation and the saved API keys;
  and the usage-insight path, which sends aggregate totals and dictionary-covered term labels only, never
  a mined novel term.
- **Not:** relaxing the fail-closed branch because a dependency bump made it look unreachable. Not adding
  a new provider path that skips the wrapper. Not logging a transcript, a prompt, a dictionary entry, a
  snippet body, an endpoint, or a key. Report shapes instead: counts, enum names, `configured` or
  `unset`.
- **Tell:** a new outbound call in `src/Scribe.Core/Cleanup/**`, a new field on a diagnostics or telemetry
  payload, or an edit to a fail-closed branch.

### P-9: One owner for an identity, with an explicit cache and an `Invalidate`

- **When:** building a credential, a client, or any object whose construction has a per-instance cache.
- **Use:** a single factory that everything goes through, holding one cached instance keyed on a
  normalized request, plus a public `Invalidate()` that every identity-changing path must call.
- **Exemplar:** `AzureCredentialFactory` (`src/Scribe.Core/Cleanup/AzureCredentialFactory.cs`):
  `Create(AzureCredentialRequest)` (around line 43) returns the cached credential while the normalized
  request is unchanged, and `Invalidate()` (around line 64) drops it. `AzureCredentialInvalidation` is
  the public front door. The reason is in the comments: Azure.Identity caches tokens per credential
  instance and Microsoft warns that an app which does not reuse them may meet HTTP 429 throttling, while
  a stale cache after a settings change means the next request authenticates as the previous identity.
- **Also part of this shape:** `AzureCliProcessCoordinator` serializes Azure CLI token requests, because
  `az` shares one token cache and concurrent processes made it time out on multi-tenant machines; a
  service principal never shells out and correctly skips that path.
- **Not:** `DefaultAzureCredential`, with or without `Exclude*` options. That was tried and shipped a real
  bug: `ManagedIdentityCredential` probed a nonexistent IMDS endpoint on a desktop and blocked cleanup.
  Not a second place that builds a `TokenCredential`. Not a settings save path that changes tenant,
  client, or secret without calling `Invalidate()`.
- **Tell:** a new `new *Credential(` outside `AzureCredentialFactory.Build`, or a settings-write path
  touching an `AiCleanupAzure*` property with no `AzureCredentialInvalidation.Invalidate()` nearby.

---

## Determinism and schema

### P-10: A deterministic decider fed timestamps or counters, never reading the clock

- **When:** a decision depends on elapsed time, on repetition, or on whether something answered.
- **Use:** a small class whose inputs are the timestamps and counters, with no ambient `DateTime.Now` or
  `Environment.TickCount64` inside. The caller reads the clock; the decider only decides. That makes it
  unit-testable without real audio, real keyboards, or `Thread.Sleep` in a test.
- **Exemplars:**
  - `SilenceAutoStopTracker` (`src/Scribe.Core/Audio/SilenceAutoStopTracker.cs`), whose summary says it
    is *"Pure function of the supplied timestamps, so it is deterministic and unit-testable without real
    audio."* Tested by `tests/Scribe.Core.Tests/SilenceAutoStopTrackerTests.cs`.
  - `HookLivenessProbe` (`src/Scribe.Core/Hotkeys/HookLivenessProbe.cs`), which decides whether Windows
    silently removed the low-level keyboard hook. Its predecessor compared two `TickCount64` stamps and
    carried a race that could only be found in production logs: it armed the probe with the stamp read
    *after* `SendInput` returned while the hook callback stamped itself *during* that call, so any
    advance of the roughly 15.6 ms tick counter read as a dead hook. Over 22 days of production logs that
    fired 3,775 times, on 13.3 percent of watchdog ticks, and every false positive tore down the hook
    thread, reset chord state, and stopped any dictation in progress. The fix was a monotonic counter,
    which needs no clock at all.
  - `SuppressedKeyReconciler` (`src/Scribe.Core/Hotkeys/SuppressedKeyReconciler.cs`), fed two predicate
    delegates rather than calling Win32 itself.
- **Not:** a decision inlined into a timer callback that reads the clock twice. Not a test that sleeps to
  advance state.
- **Tell:** a new `DateTime.Now`, `DateTimeOffset.Now`, `Stopwatch`, or `Environment.TickCount64` read
  inside a decision method in `Scribe.Core`.

### P-11: Additive forward-only SQLite migration gated on `PRAGMA user_version`

- **When:** the SQLite schema changes.
- **Use:** bump the `SchemaVersion` constant, add one more `if (current < N) { Execute(..., SchemaVN, ...) }`
  block that only adds, run the whole sequence inside one transaction, and set `PRAGMA user_version` at
  the end of it. A database whose `user_version` is **greater** than this build supports throws with a
  message telling the user to install a newer Scribe rather than silently downgrading their data.
- **Exemplar:** `ScribeDatabase.Migrate` (`src/Scribe.Core/Persistence/ScribeDatabase.cs`, around line
  383), with `SchemaVersion` (around line 23) currently at 6 and the later steps additionally guarded by
  a column probe (`HistoryNeedsColumn`) so a partially migrated database converges.
- **Also part of this shape:** `ExpectedSqliteVersion` (around line 20) asserts the exact native SQLite
  version at runtime. `SQLitePCLRaw.bundle_e_sqlite3` is referenced directly to override a transitive
  bundle affected by CVE-2025-6965, so that constant moves only deliberately, together with the package.
- **Not:** a destructive migration, a column rename, or a `DROP`. Not a new column without a version bump
  (the table exists on an upgraded install and the `CREATE TABLE` will not run again). Note that a schema
  or migration change is an **"Ask first"** item in `AGENTS.md`, so it also belongs in the
  maintainer-decision gate.
- **Tell:** any diff touching `ScribeDatabase.cs` where `SchemaVersion` is unchanged, or where
  `ExpectedSqliteVersion` moves without a matching `Directory.Packages.props` change.

### P-12: One architecture-specific native asset, selected by RID, with a build error for the rest

- **When:** a package or asset exists per architecture and both variants ship the same file names.
- **Use:** compute an effective RID property, reference exactly one variant under a condition on it, and
  emit an MSBuild `Error` for any RID outside the supported set so the failure is explicit at build time
  rather than a payload with no engine.
- **Exemplar:** `src/Scribe.Core/Scribe.Core.csproj`. `ScribeNativeRid` falls back from
  `RuntimeIdentifier` to `NETCoreSdkRuntimeIdentifier` to `win-x64`, an `<Error>` rejects anything that is
  not `win-x64` or `win-arm64`, and exactly one of `org.k2fsa.sherpa.onnx.runtime.win-x64` or
  `...win-arm64` is referenced under a condition on it. Referencing both drops two different-architecture
  `onnxruntime.dll` files into one folder.
- **Also part of this shape:** every project declares `RuntimeIdentifiers=win-x64;win-arm64` and **none**
  pins `PlatformTarget`, because a hardcoded `x64` silently produces an x64 assembly inside an ARM64
  publish. The overlay is a separate process and must be built with `-p:Platform=x64` or
  `-p:Platform=ARM64` matching the app, since WinUI has no AnyCPU story. `scripts/Payload-Architecture.ps1`
  asserts payload purity at pack time by reading the PE COFF machine field, and both installers call it.
- **Not:** referencing both native packages. Not adding `PlatformTarget`. Not a silent fallback for an
  unsupported RID; Windows on Arm emulates a mispackaged x64 binary rather than crashing, so this failure
  is invisible without the explicit error and the payload check.
- **Tell:** a new `PackageReference` whose id ends in a RID, a new project file without
  `RuntimeIdentifiers`, or any appearance of `PlatformTarget`.
