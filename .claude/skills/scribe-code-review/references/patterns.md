# Scribe canonical patterns catalog

The vocabulary of blessed shapes in this repository. Each entry names a recurring **situation**, the
**canonical shape** Scribe already uses for it, a live **exemplar** with a file path, and the
**anti-pattern** it replaces. This is the rubric for `agents/architecture-fit.md`: when a change
introduces a construct matching a situation below, it should reuse the canonical shape rather than
hand-roll a parallel one.

Most of these shapes exist because the alternative shipped a bug. Where that is true the entry says so,
because the incident is the argument.

**Staleness guard.** Exemplars move and line numbers drift. Before citing one in a finding, use `grep`
for the named symbol to confirm it still exists. If it is gone, find the current exemplar or drop the
citation. A dead citation in a review is worse than no citation. Treat the symbol name as the anchor.

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
  with its `readonly record struct Row` and `Result` carrying `DuplicateIndex`, called from the
  `SettingsWindow` partial classes (`src/Scribe.App/Settings/SettingsWindow*.cs`). Siblings following the
  same shape include `SnippetBuilder`, `ProfileBuilder`, `DictionaryImportMerger.Merge`,
  `LibraryWorkspace`, `LibraryEditor`, `LibraryImportPlanner`, `WordPackSaveProtocol`,
  `HotkeyCaptureSession`, `StoredSettingsReapply`, `AiRequestConsent`, `AccentContrastPlanner`, and
  `QuickDictionaryAdd` (`Tokenize`, `Toggle`, `Select`, `Build`, `Apply`), which
  `src/Scribe.App/QuickAdd/QuickAddWindow.xaml.cs` describes in its header as "the surface" over
  decisions that live in Core.
- **Not:** duplicate detection, trimming rules, merge planning, or preset selection written inline in a
  `.xaml.cs`. `AGENTS.md` names this directly: *"Do not let logic drift back into the code-behind; that
  is a recurring smell."* The `SettingsWindow*.cs` partials are adapters over Core deciders, and new
  decisions must land in `Scribe.Core` with tests.
- **Tell:** a new `private bool Validate...` or `private List<...> Build...` method in a `.xaml.cs` with
  no corresponding test. Ask what the pure input and output types would be; if you can name them, the
  method belongs in Core.

### P-2: Reuse the real implementation, never a private copy of a rule

- **When:** a second surface needs to apply a rule the main pipeline already applies: one dictionary
  entry, one formatting pass, one normalization step.
- **Use:** expose a narrow entry point on the real implementation and call it. Take the same
  normalization, the same compiled matcher, the same guards.
- **Exemplar:** `TextPostProcessor.ApplyRule` (`src/Scribe.Core/PostProcessing/TextPostProcessor.cs`).
  Its doc comment states the reasoning outright: it exists so the quick add popup can repair the
  transcript a correction came from, and it *"deliberately calls the real implementation rather than
  reproducing it, because a private copy of the matcher drifts silently: it would hand the user a
  'corrected' transcript that disagrees with what their very next dictation actually produces."*
  Siblings: `CleanupPrompt.IsVocabularyReplacement` and `CleanupPrompt.ComposeVocabulary` (shared across
  dictation cleanup, `GlossaryHint`, and `UsageAnalyzer.TermUsage.Shareable`), and `CleanupDisclosure`
  (bound directly in XAML via `{x:Static cleanup:CleanupDisclosure.*}`).
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
- **Exemplar:** `DictationController.Raise` (`src/Scribe.App/Dictation/DictationController.cs`) for
  `StateChanged`, paired with `PresentationRelay` and `UiThreadDispatch` (`src/Scribe.Core/Lifecycle/`)
  so dictation never blocks on `Dispatcher.Invoke` and stale revisions never overwrite newer states.
  Pinned by `tests/Scribe.Core.Tests/ResilientEventTests.cs`.
- **Not:** `Handler?.Invoke(x)` inside a single `try`/`catch`. It looks safe and is not: .NET stops
  walking the invocation list at the first exception. In production a disposed tray icon threw out of
  the first dictation-state subscriber, and every later subscriber, including the recording overlay,
  froze on whatever state it last saw while dictation kept working.
- **Tell:** a new `public event Action<T>` in Core or App raised with `?.Invoke`.

### P-4: Diagnostics that cannot take down the thing they describe

- **When:** writing to the shared daily log, or logging anything from a path whose `catch` does something
  destructive.
- **Use, on the writer side:** open with `FileShare.ReadWrite`, retry a bounded number of times on
  `IOException`, and swallow. Both processes append to the same file
  (`%LOCALAPPDATA%\ScribeData\logs\scribe-<yyyyMMdd>.log`) so dictation and overlay events interleave on
  one timeline, and a plain `File.AppendAllText` throws a sharing violation.
  - Exemplars: `FileLoggerProvider` -> `BackgroundLogWriter` -> `DailyLogFile`
    (`src/Scribe.App/Infrastructure/FileLoggerProvider.cs`, `src/Scribe.Core/Diagnostics/BackgroundLogWriter.cs`,
    `DailyLogFile.cs`) and `OverlayLog.Write` (`src/Scribe.Overlay/Logging/OverlayLog.cs`), plus
    `AppendOnlyFile.Open` / `AppendOnlyLogMode.DecideForLaunch` under `PerfFlags.AppendOnlyLog`.
- **Use, on the caller side:** route any diagnostic near a destructive `catch` through a local
  non-throwing `TryLog`, and log exceptions by shape (`FailureShape.Describe` / `DescribeWithStack`,
  enforced across App and provider-facing Core by `LogPrivacyGuardTests` and `LogCallScanner`), never by
  passing the exception object or reading `.Message` or `.ToString()`.
  - Exemplar: `OverlayProcessClient.TryLog` (`src/Scribe.App/Overlay/OverlayProcessClient.cs`). Its
    comment records the incident: the launch log line previously sat inside the `try`, a transient
    log-file lock threw there, the surrounding `catch` read that as a launch failure and called
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
- **Also part of this shape:** open the clipboard with bounded retries rather than once, verify ownership
  via `ClipboardBorrower` (a 16-byte random receipt in Scribe's registered format plus sequence-number
  validation while holding the clipboard lock; `keepWholeClipboard: true` and `MarkdownClipboardHtml` for
  Markdown paste), type trailing spaces after paste (`SplitTrailingSpaces`), check the foreground window
  is still the expected one before and during injection, and never paste into `RemoteClientProcesses`
  (always type at `TypingPace` with `KeyScanCodes`).
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
- **Exemplars:** `OverlayPipeProtocol` (`src/Scribe.Core/Overlay/OverlayPipeProtocol.cs`) defines all twelve
  pipe verbs (`WARMUP`, `RECORDING`, `WARNING`, `PROCESSING`, `TYPED`, `TYPEDWITHOUTCLEANUP`,
  `NOTHINGTYPED`, `PARTLYTYPED`, `HIDE`, `METER`, `POSITION`, `EXIT`); `OverlayProcessClient` builds every
  line through `OverlayPipeProtocol`; `OverlayIpcServer.Dispatch` (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs`)
  parses them; and `OverlayPipeProtocolTests` pins verb and outcome parity plus the by-name enum twins
  `Scribe.Core.Models.OverlayPosition` and `Scribe.Overlay.OverlayAnchor`. Pure shared calculations
  (`PillGeometry`, `PillTextScale`, `PillLevelBars`, `AppendOnlyFile`) are compiled into `Scribe.Overlay`
  as linked `Compile` items rather than adding a `ProjectReference` to `Scribe.Core`.
- **Also part of this shape:** `OverlayHelperLifetime` (in Core) owns keep-resident working-set trim,
  suspend, backoff, and outcome hold timing; the client replays `POSITION` and `_desired.ReplayLine`
  (never replaying transient outcomes) after reconnect; high-frequency `METER` commands are never logged;
  `AppWindow.Show(activateWindow: false)` never steals focus; and the overlay runs inside a
  `KILL_ON_JOB_CLOSE` Job Object **and** a `--parent` PID watchdog.
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
  1. **First-run opt-ins live in `CreateDefault`, not in the property initializer; unreadable-load
     fallbacks use `CreateForExistingInstall`.** `CreateDefault` seeds `DefaultDictation` (Page Down),
     `DefaultDictationOnly` (Page Up), and `DefaultLibraryIds`, while property initializers preserve what
     existing installs without the key expect (`HotkeyBinding.Legacy`, empty `EnabledDictionaryLibraryIds`).
     Deliberate universal defaults approved by the maintainer (`AddSpaceAfterDictation = true`,
     `AppAwareFormattingEnabled = true` with one-shot `AppAwareFormattingRollout`,
     `AccentSource = AccentSource.Scribe`, `AiCleanupPromptCaching = true`) document why in XML remarks
     and use tolerant converters so unknown future enum strings never break `SettingsRepository.Load`.
  2. **A reference-typed property is deep copied in `Clone`**, and carried through `DraftSnapshot`,
     dirty tracking, `SettingsSavePreflight`, and `StoredChangeAcknowledgement`. `Clone` starts from
     `MemberwiseClone` and explicitly rebuilds `Profiles` (with `ProcessNames` and formatting/delivery
     overrides) and `EnabledDictionaryLibraryIds`.
  3. **A secret carries `[JsonConverter(typeof(DpapiProtectedStringConverter))]`**
     (`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs`), which keeps it encrypted at rest under
     the current user with extra entropy, exposes plaintext only in memory, and returns null rather than
     throwing when decryption fails. All four API-key and client-secret properties on `AppSettings` use it.
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
  (`src/Scribe.Core/Cleanup/TextCleanupService.cs`). Chosen per surface (`IChatClient.GetService`):
  Responses (Microsoft Foundry and custom `/responses`) always sets `StoredOutputEnabled = false`, Chat
  Completions never sets `store` true, unknown raw representations fail closed to Responses options with
  storage off, and `PromptCachePolicy.TurnOff` fails closed on 400 rather than retrying without the
  option. Pinned by `CleanupStoredOutputWireTests`, `StoredOutputWireContractTests`,
  `CustomApiStyleTests`, and `PromptCacheWireTests`.
- **Related shapes in this family:** `VocabularyHandOffHandler` and `GitHubCopilotCleanupAgent` enforcing
  `ILibraryVocabularySource.TryHandOff` on every outbound request; `CleanupVocabularyMode.Mentioned`
  (`VocabularyMentions.Select`) with zero vocabulary on readiness probes (`CleanupProbeVocabularyTests`);
  `FeatureUsageTokenHandler` stripping Agent Framework's `(feat=v1.<hex>)` token; `SessionBannerTests`;
  `DiagnosticsBundle` (never `scribe.db`); and `UsageInsight` (`Covered && Shareable` labels only).
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
  `Create(AzureCredentialRequest, bool cacheCliTokens)` returns the cached credential while the normalized
  request is unchanged, wraps `AzureCliCredential` in `CachingCliTokenCredential` for serving cleanup
  clients (Settings discovery and Test connection stay uncached), and `Invalidate()` (fronted by
  `AzureCredentialInvalidation`) drops cached instances and moves the token-cache version so identity
  changes apply immediately.
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

### P-11: Additive, idempotent schema on every open; `user_version` stays 7

- **When:** the SQLite schema needs a new column, index, or table.
- **Use:** add it to `ScribeDatabase.EnsureAdditiveSchema`. Probe first (`PRAGMA table_info` for a column,
  `sqlite_master` for a table), then either `ALTER TABLE ... ADD COLUMN` with a default that rows written
  by older builds already satisfy, or `CREATE INDEX IF NOT EXISTS`. It runs after `Migrate` on every open
  (`Migrate` returns early once `user_version` matches) and on the fresh file before a corruption salvage
  copies rows into it, so the salvage carries the column. The lazy repository probe
  (`HistoryRepository.EnsureColumn`) stays as the fallback for a table that comes back without the column,
  and a new column is read only after probing for it.
- **Why the version stays 7:** builds up to 0.4.2, and the Store's 0.4.1, throw at startup when
  `user_version` is above 7, inside `async void OnStartup` after the single-instance mutex is taken and
  before the tray icon exists, so the old build keeps running invisibly and blocks relaunch. The Store and
  direct-download channels share `%LOCALAPPDATA%\ScribeData`, and users roll back, so any build may open a
  file a newer one wrote. From 0.4.3 a build that meets a newer schema throws
  `NewerDatabaseSchemaException`, which `App.OnStartup` catches around its explicit
  `ScribeDatabase.Initialize()` to tell the user to install the latest version and exit cleanly, but the
  older builds are still in the field.
- **Exemplar:** `ScribeDatabase.EnsureAdditiveSchema` (`src/Scribe.Core/Persistence/ScribeDatabase.cs`,
  around line 928), called after `Migrate` (around line 525) and before salvage (around line 717), with
  `SchemaVersion` (around line 30) still 7. `audio_blobs.encoding INTEGER NOT NULL DEFAULT 0` is the
  model: 0 means float32, the only format older builds wrote. `AudioStorageSchemaTests` pins that a file
  this build wrote keeps `user_version` at or below 7 and still works for the SQL 0.4.2 runs.
- **Also part of this shape:** where a lost column would make data misread, the data describes itself:
  PCM16 blobs open with `AudioBlobCodec.Pcm16Magic`, so an older build's repair that drops `encoding`
  cannot turn them into float32 noise. `ExpectedSqliteVersion` (around line 21) asserts the exact native
  SQLite version at runtime; `SQLitePCLRaw.bundle_e_sqlite3` is referenced directly to override a
  transitive bundle affected by CVE-2025-6965, so that constant moves only deliberately, together with the
  package.
- **Not:** raising `user_version` above 7. Not a destructive migration, a column rename, or a `DROP`. Not
  a `NOT NULL` column without a default older rows satisfy. A schema change is still an **"Ask first"**
  item in `AGENTS.md`, so it also belongs in the maintainer-decision gate.
- **Tell:** any change to `SchemaVersion`; any new column read without a probe (a statement naming it on a
  path that neither checks `table_info` nor goes through `EnsureColumn`); `ExpectedSqliteVersion` moving
  without a matching `Directory.Packages.props` change.

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
- **Also part of this shape:** every Windows project (except `Scribe.Overlay`, which derives its RID from
  `<Platforms>x64;ARM64</Platforms>`) declares `RuntimeIdentifiers=win-x64;win-arm64` and **none** pins
  `PlatformTarget`, because a hardcoded `x64` silently produces an x64 assembly inside an ARM64 publish.
  The overlay is a separate process built with `-p:Platform=x64` or `-p:Platform=ARM64` matching the app.
  `scripts/Payload-Architecture.ps1` asserts payload purity at pack time by reading the PE COFF machine
  field, `scripts/Model-Manifest.ps1` pins bundled speech models and their `ModelLicenses` notices, and
  changes to `Scribe.Core` domain behavior check `macos/Scribe` and `macos/PORTING-PLAN.md` for parity.
- **Not:** referencing both native packages. Not adding `PlatformTarget`. Not a silent fallback for an
  unsupported RID; Windows on Arm emulates a mispackaged x64 binary rather than crashing, so this failure
  is invisible without the explicit error and the payload check.
- **Tell:** a new `PackageReference` whose id ends in a RID, a new project file without
  `RuntimeIdentifiers`, or any appearance of `PlatformTarget`.
