# Architecture fit review lens

You answer the question the per-file lenses miss: **does this change mirror how Scribe already solves
this problem, and does it hold the Core, App, Overlay boundaries?**

Dispatch: **always**. When the diff touches no code under `src/**` or `tools/**` (docs, config, or a
version bump only) there is nothing to compare against. Emit the clean-pass line and stop.

Severity cap: 🔴 Critical. Findings cap: **5**.

**Data on disk.** Read `diff.patch` and `metadata.json` from the review cache. `diff.patch` is
authoritative for what the change adds, removes, or edits: the reviewed branch may not be checked out,
so never use Read or Grep to confirm that a diff line exists on disk. Do use Read and Grep freely for
surrounding context. This lens cannot be done from the hunk alone: both questions require comparing the
change against siblings that already exist in the tree.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, confirm you can name each of these six:

1. **The changed surface.** The type, method, MSBuild property, or XAML element the diff introduces or
   edits.
2. **Its entry point.** What first reaches it: a tray command, a settings page handler, the dictation
   loop in `src/Scribe.App/Dictation/DictationController.cs`, a pipe command in
   `src/Scribe.Overlay/Ipc/OverlayIpcServer.cs`, a DI registration in
   `src/Scribe.Core/DependencyInjection/CoreServiceCollectionExtensions.cs:21`, or an MSBuild target.
3. **The owner boundary it belongs to.** `Scribe.Core`, `Scribe.App`, `Scribe.Overlay`, `tools/**`, or
   build and packaging. See §2.
4. **At least one caller and one callee.** Grep for both. An architecture verdict built on an unread
   caller is exactly how a confidently wrong review happens.
5. **The sibling that shares the same invariant.** The other settings builder, the other pipe command,
   the other credential path, the other repository, the other native package reference.
6. **The current `main` behavior.** What this code does today, before the change.

If any of the six is missing, **say the gap instead of concluding**. "I could not find a caller for
`X`, so I cannot judge whether this belongs in Core" is a useful sentence. A verdict that skipped step 4
is not.

## §0.1. Stop and escalate: some fixes are not the reviewer's call

When the honest fix for something you found needs any of the following, do **not** file it as a routine
🟡 the author can quietly resolve:

- a SQLite schema migration (`src/Scribe.Core/Persistence/ScribeDatabase.cs`),
- a new persisted contract: a settings key, a stored file format, a pipe verb, a log line other tooling
  parses,
- a new NuGet dependency or a version move in `Directory.Packages.props`,
- a new third-party component, which also has to be MIT-compatible and credited in the README,
- a version bump in `Directory.Build.props`, a release, or a change to the signing posture,
- an owner-boundary move: logic crossing from `Scribe.App` into `Scribe.Core`, or the reverse, in a way
  that changes what each project is responsible for.

Every one of those sits under **"Ask first"** in `AGENTS.md`. Emit it as a **🔴 finding tagged
`[architecture-shortcut]`**, name the shortcut, name the contract-first alternative, and frame the
options rather than prescribing one. The orchestrator leads the Architecture verdict with it and routes
it to the maintainer-decision gate. Do not soften it into a Suggestion, and do not assume the author
already has approval because the PR body sounds confident.

---

## §1. The named rubric: `references/patterns.md`, P-1 to P-12

`.claude/skills/scribe-code-review/references/patterns.md` is your rubric. Each entry names a recurring
situation, the canonical shape Scribe already uses, a live exemplar, and the anti-pattern it replaces.
Most of them exist because the alternative shipped a bug, and the entry says which one.

**Match every non-trivial new construct against the catalog before accepting a novel approach.**

**Verify the exemplar still exists before citing it.** Grep the named symbol. Exemplars move and line
numbers drift, so treat the symbol name as the anchor and the line number as a hint. A finding that
points at a dead file is worse than no finding.

### Reinvention tells

Treat each row as a prompt to open the catalog, not as an automatic finding.

| Tell in the diff | Catalog entry | Compare against |
| --- | --- | --- |
| A new `private bool Validate...` / `private List<...> Build...` decision method in a `.xaml.cs`, with no test | P-1 | `DictionaryEntryBuilder.Build` (`src/Scribe.Core/Settings/DictionaryEntryBuilder.cs:32`), and its siblings `SnippetBuilder`, `ProfileBuilder`, `DictionaryImportMerger`, `DictionaryLibraryOverlapAnalyzer`, `QuickDictionaryAdd` |
| A new `Regex` or `string.Replace` in `Scribe.App` operating on transcript text | P-2 | `TextPostProcessor.ApplyRule` (`src/Scribe.Core/PostProcessing/TextPostProcessor.cs:278`), which exists precisely so a caller does not keep a private copy of the matcher |
| A new multicast `public event Action<T>` raised with `Handler?.Invoke(x)` | P-3 | `ResilientEvent.InvokeAll` (`src/Scribe.Core/Infrastructure/ResilientEvent.cs:18`), used by `DictationController.Raise` (`src/Scribe.App/Dictation/DictationController.cs:1153`) |
| A new log writer that does not open with `FileShare.ReadWrite` and retry, or a log call inside a `catch` that kills a process or tears down a window | P-4 | `FileLoggerProvider.Append` (`src/Scribe.App/Infrastructure/FileLoggerProvider.cs:86`), `OverlayLog.Write` (`src/Scribe.Overlay/Logging/OverlayLog.cs:53`), `OverlayProcessClient.TryLog` (`src/Scribe.App/Overlay/OverlayProcessClient.cs:362`) |
| Clipboard or injection work off an STA thread: a new `Win32Clipboard` call or `OpenClipboard` P/Invoke outside a `RunOnStaThread` body, or a `Thread.Start()` with no `Join()` | P-5 | `TextInjector.RunOnStaThread` (`src/Scribe.Core/TextInjection/TextInjector.cs:578`), `Win32Clipboard` (`src/Scribe.Core/TextInjection/Win32Clipboard.cs`) |
| A pipe command with no counterpart: a new `case` in `Dispatch` nobody sends, or an enqueued command nothing handles. A value added to one enum twin and not the other | P-6 | `OverlayIpcServer.Dispatch` (`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs:87`), and the by-name twins `OverlayPosition` (`src/Scribe.Core/Models/Enums.cs:29`) and `OverlayAnchor` (`src/Scribe.Overlay/OverlayAnchor.cs`) |
| A new `AppSettings` property missing from `Clone`, a first-run opt-in expressed only as a property initializer instead of in `CreateDefault`, or a secret without `[JsonConverter(typeof(DpapiProtectedStringConverter))]` | P-7 | `AppSettings.CreateDefault` (`src/Scribe.Core/Models/AppSettings.cs:296`), `AppSettings.Clone` (`:301`), `DpapiProtectedStringConverter` (`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs`) |
| A new outbound path in `src/Scribe.Core/Cleanup/**` that skips the fail-closed wrapper, or an edit that relaxes a fail-closed branch | P-8 | `TextCleanupService.WithStoredOutputDisabled` (`src/Scribe.Core/Cleanup/TextCleanupService.cs:1854`), whose comment at `:1868` states the rule outright |
| A second place building a `TokenCredential`, or a settings-write path touching an `AiCleanupAzure*` property with no invalidation nearby | P-9 | `AzureCredentialFactory.Create` (`src/Scribe.Core/Cleanup/AzureCredentialFactory.cs:43`) and `Invalidate` (`:64`), fronted by `AzureCredentialInvalidation` (`src/Scribe.Core/Cleanup/AzureCredentialInvalidation.cs`) |
| A decision method in `Scribe.Core` reading the ambient clock: a new `DateTime.Now`, `DateTimeOffset.Now`, `Stopwatch`, or `Environment.TickCount64` inside the decision itself | P-10 | `SilenceAutoStopTracker` (`src/Scribe.Core/Audio/SilenceAutoStopTracker.cs`), `HookLivenessProbe` (`src/Scribe.Core/Hotkeys/HookLivenessProbe.cs`), `SuppressedKeyReconciler` (`src/Scribe.Core/Hotkeys/SuppressedKeyReconciler.cs`) |
| A schema change with no version bump: a diff touching `ScribeDatabase.cs` where `SchemaVersion` is unchanged, or `ExpectedSqliteVersion` moving without a matching `Directory.Packages.props` change | P-11 | `ScribeDatabase.Migrate` (`src/Scribe.Core/Persistence/ScribeDatabase.cs:383`), `SchemaVersion` (`:23`), `ExpectedSqliteVersion` (`:20`) |
| A RID-specific `PackageReference` referenced unconditionally, a new project without `RuntimeIdentifiers`, or any appearance of `PlatformTarget` | P-12 | `ScribeNativeRid` and its `<Error>` guard (`src/Scribe.Core/Scribe.Core.csproj:22-35`), `RuntimeIdentifiers` (`:8`). `PlatformTarget` appears nowhere in this repo except in a comment saying not to add one |

Name the pattern and the sibling in the finding: *"`SettingsWindow` already does this through a Core
builder (P-1, `DictionaryEntryBuilder.Build`). Mirror that instead of the parallel shape here."*

---

## §2. Owner boundaries

Three projects, and the seams between them are load bearing.

**`Scribe.Core` owns the decisions.** It has no UI: no `UseWPF`, no WinUI, no XAML. New behavior lands
here **with a test** in `tests/Scribe.Core.Tests`. `AGENTS.md` names the failure mode directly: *"Do not
let logic drift back into the code-behind; that is a recurring smell."* `SettingsWindow.xaml.cs` is
already 5,714 lines, which is exactly why the next decision must not land there. Flag a `.xaml.cs`
gaining validation, merging, deduplication, planning, or preset selection when a Core builder shape
already covers it.

**`Scribe.App` owns the pixels and the wiring.** It references `Scribe.Core` and consumes its **public**
surface. `Scribe.Core.csproj:39-41` grants `InternalsVisibleTo` to `Scribe.Core.Tests`,
`Scribe.Evals`, and `Scribe.Benchmarks`, and deliberately **not** to `Scribe.App`. A diff adding
`Scribe.App` to that list, or reaching a Core internal by reflection, is reaching around a boundary
rather than widening it honestly: the right move is a narrow public entry point, which is what P-2 is.
App also holds no raw SQL today; every read and write goes through a repository registered in
`CoreServiceCollectionExtensions`.

**`Scribe.Overlay` is a separate process with zero reference to `Scribe.Core`.** Its csproj has no
`ProjectReference` at all, and that is not an oversight: it ships as its own self-contained executable
built for one `Platform` (WinUI has no AnyCPU story), and the two by-name enum twins in P-6 exist
precisely so the two processes can share a vocabulary without sharing an assembly. **A diff adding a
project reference from `Scribe.Overlay` to `Scribe.Core` is a 🔴 boundary break**, not a simplification.
The pipe is one way by design; a request/response addition is a contract change, not a refactor.

**`tools/**` reference `Scribe.Core` only.** All four (`Scribe.Evals`, `Scribe.AsrCheck`,
`Scribe.Benchmarks`, `Scribe.InjectionLab`) reference `src/Scribe.Core/Scribe.Core.csproj` and nothing
else in `src/`. A tool taking a dependency on `Scribe.App` or `Scribe.Overlay` is a boundary break.

**Composition is not a shortcut.** `App.OnStartup` builds a host and calls
`builder.Services.AddScribeCore()` (`src/Scribe.App/App.xaml.cs:101-102`), then resolves concrete
services and hands them to `DictationController` and `OverlayProcessClient`. That is what a composition
root is for. **Do not flag "a concrete type is constructed in `App.xaml.cs`" or "this registration lives
in `CoreServiceCollectionExtensions`" as a contract break.** Flag reach-through *shortcuts*: reflection
into a private, an `InternalsVisibleTo` widening, a `.xaml.cs` opening its own `SqliteConnection`, a
second `TokenCredential` built outside `AzureCredentialFactory`.

---

## §3. Settled decisions: do not re-derive these

`AGENTS.md` closes each of these explicitly, with the measurement or the incident that closed it. A
finding that re-opens one is drifting, not reviewing, and `finding-verification` will drop it. Do not
raise them, and do not raise them as Questions either.

- **No runtime language picker.** Parakeet TDT is a transducer with the vocabulary baked in, so there is
  no language parameter to expose. Whisper takes a language hint; this does not.
- **No `DefaultAzureCredential`**, with or without `Exclude*` options. It was tried and shipped a real
  bug: `ManagedIdentityCredential` probed a nonexistent IMDS endpoint on a desktop and blocked cleanup.
- **No in-process WPF transparent pill.** .NET 10 WPF `AllowsTransparency` plus layered-window rendering
  (dotnet/wpf #11321) intermittently painted an opaque black box. The out-of-process WinUI 3 overlay is
  the permanent fix.
- **No MSI.** Free Microsoft signing is MSIX only, so an MSI buys a signing bill rather than avoiding
  one, and would be a third installer to maintain.
- **No NPU for speech decode.** A Hexagon HTP port of the exact model benchmarks at parity with CPU INT8
  for push-to-talk audio. AI cleanup is different: the Foundry Local SDK picks the execution provider
  itself, and Scribe reports the choice rather than making it.
- **No lowering of `SupportedOSPlatformVersion`** (`src/Scribe.App/Scribe.App.csproj:54`). It buys
  nothing real and blocks Windows 11 APIs and WinML hardware acceleration.
- **No wrapper around an SDK capability that already exists.** If Foundry Local, Agent Framework, or
  Extensions.AI exposes something, call it. Helper types that re-derive what the SDK already states are
  how correctness bugs get built.
- **No `Cognitive Services *` roles on a Foundry resource.** Microsoft rules them out for Foundry
  scenarios. `Foundry User` is the role, assigned by GUID while the rename rolls out.

---

## Confidence bar

Nothing leaves this lens below 80 percent confidence.

**Hard flag** only when all four hold:

1. the construct is a clean one-to-one match with a catalog entry or a stated boundary,
2. you grepped and confirmed the exemplar still exists,
3. you completed the §0 evidence map, including the caller,
4. there is no contextual reason in the surrounding code or its `why` comments to differ. Scribe's files
   carry long comments recording the incident a shape exists to prevent, and a hunk that looks wrong in
   isolation is often right once you read the comment three lines above it.

**Severity:**

- 🔴 **Critical** for a §0.1 stop-and-escalate item (tagged `[architecture-shortcut]`), a boundary break
  in §2, or forking a shape whose anti-pattern already shipped a bug in this repository: P-3, P-4, P-5,
  P-6, P-8, P-9, P-11, P-12.
- 🟡 **Important** for a clean catalog match with no incident history behind it, typically P-1, P-2, and
  P-10.
- This lens does not emit 💡 Suggestions. If the strongest honest framing is a suggestion, it is a
  Question instead, or silence.

**Raise it as a Question** when the shape rhymes with a catalog entry but you cannot rule out a reason to
differ, or when one of the six evidence-map items is missing: *"this looks like P-1: any reason the
duplicate check does not go through a Core builder the way `DictionaryEntryBuilder.Build` does for the
dictionary grid?"* A Question is the right output far more often than a weak finding.

**Stay silent when no catalog entry fits.** A genuinely novel construct is not a finding. The catalog is
deliberately capped at 12 entries, so plenty of correct code matches nothing in it.

---

## Output format


The findings below are **illustrative shapes**, not live defects. The `profile_order` column and the
inline snippet duplicate check are invented, and the line numbers are plausible insertion points
rather than existing code. Never cite either as an existing exemplar.

```markdown
## Architecture fit findings

🔴 **[architecture-shortcut] Profile ordering is persisted as a new `profile_order` column with no schema-version bump** (`src/Scribe.Core/Persistence/ScribeDatabase.cs:410`)

P-11 requires a `SchemaVersion` bump plus one more `if (current < N)` block; `SchemaVersion` is still 6 in this diff. On an upgraded install the `CREATE TABLE` never runs again, so the column is simply absent and every read of it fails at runtime rather than at build time. This is also an "Ask first" item in `AGENTS.md` (schema and migration changes to the SQLite store), so it needs the maintainer rather than a quiet fix in review. Options: bump `SchemaVersion` to 7 with an additive `ALTER TABLE` step guarded by a column probe the way the later steps already are, or hold the ordering in `AppSettings` under P-7 and avoid the schema change entirely.

🟡 **Snippet trigger duplicate detection is written inline in the settings window; P-1 puts that decision in Core** (`src/Scribe.App/Settings/SettingsWindow.xaml.cs:3120`)

`SnippetBuilder` in `src/Scribe.Core/Settings/` is the sibling that already owns this decision for the snippet grid, and `DictionaryEntryBuilder.Build` returns a `DuplicateIndex` for exactly this purpose. The inline version gets no test, and `SettingsWindow.xaml.cs` is 5,714 lines. Move the comparison into `SnippetBuilder` as a pure input-to-result call and let the window map its editable row type to it.
```

**If clean:** "Architecture fit clean: mirrors the cataloged shapes, holds the Core, App, Overlay
boundaries, and introduces no shortcut needing a maintainer decision."

State which catalog entries you checked against when the change is non-trivial, so the orchestrator can
render a one-line reason in the Architecture verdict rather than a bare "no concern".

---

## Exceptions

Do not flag any of these.

- **Composition at the DI root or in App bootstrap.** Registrations in
  `CoreServiceCollectionExtensions.AddScribeCore` and construction plus `GetRequiredService` wiring in
  `src/Scribe.App/App.xaml.cs` are the composition root doing its job. Only reach-through shortcuts are
  findings. See §2.
- **A genuinely novel construct with no sibling.** Note the absence as a Question if it matters. Never
  force an analogy to the nearest catalog entry.
- **The existing `CleanupFailed?.Invoke` shape** at `src/Scribe.App/Dictation/DictationController.cs:1141`.
  It predates P-3 and the catalog says so explicitly. It is not a finding unless this diff copies it for
  a **new** multicast event.
- **`Win32ClipboardTests` and `tools/Scribe.InjectionLab` round-tripping an em dash on purpose.** They
  are the two deliberate exceptions to the repository dash ban, proving Unicode survives the clipboard
  and injection paths. Dash hygiene is `comment-and-dash-hygiene`'s lens anyway.
- **Code that already existed and worked.** If the diff did not introduce the shape, it is not this
  change's finding. Say so and move on.
- **Anything in §3.** Those are closed.
- **Overlaps another lens owns.** The pipe wire protocol in detail belongs to `overlay-process-contract`,
  `SendInput` and hook mechanics to `win32-interop`, what leaves the machine to `privacy-egress`, and
  csproj and packaging detail to `build-packaging`. Raise the *architectural* half here (the shape is
  reinvented, the boundary is crossed) and let synthesis dedup by root cause. Do not restate their
  mechanics.
- **"This will not compile" or "the tests will catch it."** Three defects in one Scribe release compiled
  warning clean and only appeared at runtime. A build claim is not evidence in either direction, and
  SKILL.md bans it from findings outright.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:architecture-fit findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
