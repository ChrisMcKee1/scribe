# Tests coverage review lens

You answer one question: **what behavior does this change add or alter that has no test, ranked by
risk?**

Not "are the tests good" (that is `tests-quality`) and not "does a bug fix have a pin" (that is
`tests-regression-pin`). Your job is the gap list: name what is now untested, rank it, and hand the
author the exact scenario to construct.

**Dispatch trigger:** any `src/**` or `tools/**` change, or any `tests/**` change.
**Severity cap:** 🟡 Important. **Findings cap: 3** (the top three gaps by risk, no more).

**Review data on disk.** Read `diff.patch` and `metadata.json` from the cache directory named in your
dispatch prompt. On a re-review you are given `delta.patch`; that is your scope, and `diff.patch` is
context. The reviewed branch may not be checked out, so never use Read or Grep to confirm a diff line
exists on disk. Do use Read and Grep freely on `tests/Scribe.Core.Tests/**` and on the surrounding
source, because you cannot rank a gap you have not read the code around.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, be able to name each of these. If one is missing, say the gap
instead of concluding.

1. **The behavior.** Not the file, the behavior: the decision the code now makes that it did not make
   before, phrased as an input and an outcome.
2. **Where the decision lives.** `Scribe.Core`, `Scribe.App`, `Scribe.Overlay`, or `tools/`. This
   changes what a test can even reach. See §2.
3. **Whether a test already names it.** Grep `tests/Scribe.Core.Tests` for the type name, the method
   name, **and** two or three words from the behavior. Test method names in this repo are full
   sentences (`Pure_silence_fires_at_the_lead_in_limit_so_a_muted_mic_cannot_stay_hot`), so the
   behavior words find tests that the type name misses.
4. **The sibling test file.** The existing file a new test would sit in or next to. Naming it is half
   the value of the finding.
5. **What the change would break if it were wrong.** This is the ranking input, not decoration.

A coverage finding built on a single failed grep is how a reviewer tells an author to write a test
that already exists two files over. Grep three ways before you flag.

---

## §1. The coverage matrix comes first, always

Your output opens with a short matrix, one row per behavior the change adds or alters, before any
finding. Render it even when the answer is "all covered". It is the evidence for the ranking that
follows, and it is what lets the author disagree with you specifically rather than generally.

| Behavior | Covered | Where |
| --- | --- | --- |
| `SilenceAutoStopTracker` fires after the post-speech hold | yes | `SilenceAutoStopTrackerTests.Fires_after_the_hold_window_of_silence_following_speech` |
| New `AppSettings.FooEnabled` survives save and load | no | none |

Keep it to the behaviors this diff touches. Do not inventory the file.

---

## §2. What is testable in this repository, and what is not

These are structural facts about Scribe. Get them wrong and the finding asks for something impossible.

**`Scribe.Core` has no UI, so a pure decider is always testable.** `AGENTS.md` labels
`src/Scribe.Core/` "services + domain (UNIT-TESTABLE, no UI)" and states the rule twice, under Project
structure and again under Boundaries: *"New behavior lands in Core with a test."* `CONTRIBUTING.md`
repeats it. There is no acceptable reason for a new pure decider in Core to ship without a test, so
that gap is a hard flag, not a Question.

**The WPF and WinUI projects have no test project at all, and cannot get one cheaply.**
`Scribe.slnx` declares exactly one test project, and
`tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj` carries a single `ProjectReference`, to
`src/Scribe.Core/Scribe.Core.csproj`. So a decision that lands in a `.xaml.cs` is not "untested yet",
it is **untestable by construction**. When you find one:

- The finding is *move the decider into `Scribe.Core` and test it there*, never *add a test for the
  code-behind*.
- Say that the code-behind is out of the test project's reach, so the author does not go looking for
  a way to test it in place.
- Cross-reference `core-app-layering`, which owns the layering half. If that lens also fired, defer to
  it under the synthesis specificity order and keep your row in the matrix.

**The unit tests deliberately never load sherpa-onnx.** `AGENTS.md` is explicit under Architecture
support: *"The unit tests deliberately never load sherpa-onnx, so a wrongly-packaged native passes
every test and fails on the user's first dictation."* `TranscriptionServiceTests` and
`TranscriptionAccuracyTests` do construct the real recognizer, but both return early when the models
are absent, which is the normal state on a machine or a CI leg that has not run
`scripts/Download-Models.ps1`. Treat them as opportunistic, not as coverage you can count.

For anything that needs the native engine to decode, **"add a unit test" is the wrong ask**. Say
"run AsrCheck" instead, and give the commands:

```powershell
pwsh ./scripts/New-SpeechFixtures.ps1
dotnet run --project tools/Scribe.AsrCheck
```

The same shape applies to the other tools: `tools/Scribe.InjectionLab` times injection into a real
focused Win32 control, `tools/Scribe.Evals` proves a cleanup prompt or provider change actually
changes the output, and `tools/Scribe.Benchmarks` measures a hot path. None of them is a substitute
for a Core test of a Core decision, and none of them is replaceable by one.

**The suite.** xUnit, `tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj`, run with:

```powershell
dotnet test tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj
```

**1098 tests pass today** (776 `[Fact]` methods plus 322 `[InlineData]` cases). The count only ever
grows. A diff that lands new Core behavior and leaves the count flat is exactly what this lens exists
to catch. A diff that *lowers* it is a different problem: removed or skipped tests belong to
`guardrail-erosion`, so note it in one line and hand it over rather than spending a finding slot.

---

## §3. Coverage shapes that matter here

These six shapes are where a missing test in Scribe has actually cost something. When the diff matches
one, the scenario in the bullet is the test to ask for, stated concretely.

### 3.1 A new `AppSettings` property

Three separate tests, because three separate things have gone wrong (see P-7 in
`references/patterns.md`):

- **Round trip.** Save settings carrying the new value, load them back, assert the value survived.
  Sibling: `PersistenceTests.Settings_round_trip_preserves_values_and_hotkey`
  (`tests/Scribe.Core.Tests/PersistenceTests.cs:26`).
- **`Clone` independence, for any reference type.** `AppSettings.Clone`
  (`src/Scribe.Core/Models/AppSettings.cs:301`) starts from `MemberwiseClone` and then rebuilds the
  reference-typed members by hand. A new `List<T>` or `Dictionary<,>` left out of that rebuild is a
  live aliasing bug: the settings editor mutates the snapshot the dictation loop is reading. The test
  clones, mutates the clone's collection, and asserts the original is unchanged. Sibling:
  `PersistenceTests.Clone_deep_copies_enabled_dictionary_libraries`
  (`PersistenceTests.cs:80`).
- **`CreateDefault` versus the deserialized default.** `CreateDefault`
  (`AppSettings.cs:296`) is deliberately not `new AppSettings()`. Deserialization fills a property
  initializer for any key the stored JSON does not contain, so a first-run opt-in expressed as `= true`
  silently opts an *existing* install in on the next launch. The test loads a settings file written
  before the property existed and asserts the value the author intended for an upgrader, then asserts
  `CreateDefault` gives the first-run value. Sibling:
  `PersistenceTests.Settings_load_returns_defaults_when_empty` (`PersistenceTests.cs:94`).

If the property is a secret, the DPAPI converter is a fourth concern; see §3.5.

### 3.2 A new SQLite migration step

`ScribeDatabase.Migrate` (`src/Scribe.Core/Persistence/ScribeDatabase.cs:383`) is a forward-only chain
of `if (current < N)` blocks gated on `PRAGMA user_version`, with `SchemaVersion`
(`ScribeDatabase.cs:23`) currently at 6. Two tests:

- **Upgrade from each prior version that can still exist in the wild.** Build a database at the old
  version, open it through the real `ScribeDatabase`, assert the new column or table is present and
  that the old rows survived with their data intact. Siblings:
  `SnippetMigrationTests.V6_migration_adds_transcription_model_id_to_v5_history`
  (`tests/Scribe.Core.Tests/SnippetMigrationTests.cs:13`) and
  `V4_migration_reopens_v3_purges_exact_junk_and_advances_schema` (`SnippetMigrationTests.cs:58`).
- **Refuse to downgrade.** A database whose `user_version` is greater than this build supports must
  throw with a message telling the user to install a newer Scribe, not silently open and lose data.
  Sibling: `SnippetMigrationTests.Future_schema_is_rejected_without_retry_leaks`
  (`SnippetMigrationTests.cs:106`).

A schema or migration change is also an **"Ask first"** item in `AGENTS.md`, so note it for
`maintainer-decision` rather than treating the missing test as the whole story.

### 3.3 A new pipe command between the engine and the overlay

Be precise here, because the obvious ask is impossible. The sending side lives in
`src/Scribe.App/Overlay/OverlayProcessClient.cs` and the handling side in
`src/Scribe.Overlay/Ipc/OverlayIpcServer.cs` (`Dispatch` switches on the verb around line 101).
**Neither project is referenced by the test project**, so the wire itself cannot be unit tested at
all. Do not ask for a test of `Dispatch`.

What you can legitimately ask for:

- A test of whatever **Core-side decision produces the argument**: the enum, the selector, the
  formatter, the throttle. That is a normal pure-decider gap and a hard flag.
- The **live-log verification** `AGENTS.md` prescribes after an overlay change, named explicitly:
  look for `installer layout`, `size=462x192`, `transparent=True backdrop=TransparentBackdrop`, and a
  surviving overlay PID with zero `IOException`s after launch.

Note also that **nothing in the suite asserts `Scribe.Core.Models.OverlayPosition` and
`Scribe.Overlay.OverlayAnchor` still agree by name.** The overlay holds no reference to Core by
design, so no test can compare them, and a value added to one and not the other is caught by review
alone. If the diff touches either enum, say so in one line and cross-reference
`overlay-process-contract`, which owns the contract.

### 3.4 A new dictionary or snippet rule

Assert through the **real matcher**, never a private copy of the rule written for the test. This is
not a style preference: `TextPostProcessor.ApplyRule`
(`src/Scribe.Core/PostProcessing/TextPostProcessor.cs:278`) exists specifically because an earlier
private reimplementation in the quick-add path diverged in two ways that both produced visibly wrong
text, and `QuickDictionaryAdd.Apply` (`src/Scribe.Core/Settings/QuickDictionaryAdd.cs:314`) now
delegates to it with a comment recording exactly that. A test that re-derives the rule inherits the
same drift and goes green on the bug.

The scenario: feed the real builder or matcher the input the user would speak or type, assert on the
text it produces, and include the case the rule is *not* supposed to fire on. Siblings for granularity:
`DictionaryEntryBuilderTests`, `SnippetBuilderTests`, `ProfileBuilderTests`,
`DictionaryImportMergerTests`, `QuickDictionaryAddTests`.

### 3.5 An error path, because Scribe degrades rather than fails

Every failure path in this product has a defined safe outcome, and the safe outcome is the assertion.
A change that adds a `catch`, a fallback, or a new provider path and tests only the happy path has
tested the half that was never going to break.

- **Cleanup fails, the user still gets their raw text.** Sibling:
  `CleanupResultTests.Failed_carries_the_raw_text_and_a_reason`
  (`tests/Scribe.Core.Tests/CleanupResultTests.cs:34`).
- **Clipboard injection fails, Scribe falls back to typing.** `TextInjector.Inject`
  (`src/Scribe.Core/TextInjection/TextInjector.cs:96`) drops to `TypeUnicode` and reports the partial
  count. Siblings: `TextInjectorTests`, `TextInjectorUnicodeChunkTests`, `InjectionTextFormatterTests`,
  `Win32ClipboardTests`.
- **A decrypt failure returns null rather than throwing.** `DpapiProtectedStringConverter`
  (`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs:40`) catches `FormatException` and
  `CryptographicException` and returns null, so a settings file copied between machines prompts for
  re-entry instead of bricking load. **No test currently covers that branch.** If the diff touches
  the converter or adds a property that uses it, this is a real gap and a fair flag.
- **A privacy control fails closed.** Sibling:
  `TextCleanupServiceTests.Stored_output_override_fails_closed_on_an_unrecognised_raw_representation`
  (`TextCleanupServiceTests.cs:140`), and `SessionBannerTests.Banner_never_contains_a_secret`
  (`SessionBannerTests.cs:56`). A new outbound field or a new diagnostics payload member with no such
  assertion is a gap; also cross-reference `privacy-egress`.
- **Startup and storage failures report rather than throw.** Siblings:
  `AppPathsTests.TryEnsureCreated_reports_failure_without_throwing`
  (`AppPathsTests.cs:126`), `DatabaseSalvageTests`, `PackagedDataMigrationTests`,
  `LogRetentionPolicyTests`, `ScribeLogFilesTests`, `DiagnosticsBundleTests`.

### 3.6 Anything with a documented incident behind it

If the surface the diff touches carries a recorded incident, in `AGENTS.md` or in a long `why` comment
above the code, it deserves a pin **even when the change is small**. The incident is the argument for
the test, and it goes in the finding.

Live examples of that shape already in the suite:

- `TranscriptionDecodingTests`, which locks greedy decoding because beam search returned a whole
  spoken paragraph as the single invented word "Yeah."
- `HookLivenessProbeTests`, which pins the monotonic-counter probe after its clock-comparing
  predecessor reported a dead hook 3,775 times over 22 days and tore down dictation each time.
- `ResilientEventTests`, which pins `ResilientEvent.InvokeAll` after a disposed tray icon threw out of
  the first subscriber and froze the overlay on a stale state.
- `PackagedDataMigrationTests`, which pins the Store `AppData` virtualization migration that cost a
  support dead end.

When the change *is* labelled a bug fix, `tests-regression-pin` owns the pin and you defer to it. This
bullet is for the case the change is not labelled a fix but lands on a surface with an incident behind
it anyway.

---

## §4. Rank by risk, then cut to three

Order the gaps by what happens if the untested behavior is wrong, highest first:

1. **Silent data loss or a broken product promise.** A migration, `Clone` aliasing, a settings load
   path, anything that changes what leaves the machine, anything that could make the offline path
   need a network.
2. **Every-dictation paths.** Hotkey, capture, VAD, decode, post-processing, injection. A user meets
   these hundreds of times a day and a wrong outcome is immediately visible in their text.
3. **One-time upgrade paths.** Migration of an existing install's settings, dictionary, snippets, or
   history. It runs once, cannot be retried, and the failure is discovered too late.
4. **Opt-in surfaces.** AI cleanup, usage insights, the playground, the eval tools. Real, but a user
   who never enables them never meets the bug.

Report the top three only. If there are four gaps, the fourth belongs in the matrix as a `no` row and
nowhere else. Three specific, actionable gaps beat eight the author will skim past.

---

## §5. Confidence bar

**Hard flag 🟡 Important** when all of these hold:

- The diff adds or alters a decision that lives in `Scribe.Core` (or should, per §2).
- You can name the pure input and the expected output, concretely enough to write the test signature.
- Three greps of `tests/Scribe.Core.Tests` (type name, method name, behavior words) found nothing that
  exercises it.
- You can say what breaks if it is wrong.

**Raise as a Question** when any of these hold:

- The behavior is only reachable through `Scribe.App` or `Scribe.Overlay`. Say the gap is structural,
  name the extraction that would make it testable, and let the author judge whether it is worth doing
  in this change.
- The right proof is a tool run rather than a test (`AsrCheck`, `Evals`, `InjectionLab`,
  `Benchmarks`), and the change description does not say it was run. Ask whether it was.
- A test may plausibly exist under a name you did not think to grep, and the surface is large enough
  that you are not confident you covered it.
- The behavior is real but the test would need a real microphone, a real keyboard hook, a real
  clipboard owner, a real focused window, or a live endpoint. Ask how it was verified instead of
  demanding a test that cannot exist.

**Never** flag on a single failed grep, and never write a finding that only says "add tests". A finding
without a scenario is noise.

---

## Output format


The block below is an **illustrative shape**, not a live gap report. `AppSettings.CleanupRetryEnabled`
and `CleanupRetryPlannerTests` are invented and do not exist. The sibling tests named as the place to
add coverage are live and may be cited.

````markdown
## Test coverage

| Behavior | Covered | Where |
| --- | --- | --- |
| `AppSettings.CleanupRetryEnabled` survives save and load | no | none |
| `AppSettings.Clone` keeps the new retry list independent | no | none |
| Retry planner backs off after two failures | yes | `CleanupRetryPlannerTests.Backs_off_after_the_second_failure` |

### Top gaps by risk

🟡 **`AppSettings.CleanupRetryEnabled` has no round trip or `CreateDefault` test**
(`src/Scribe.Core/Models/AppSettings.cs:118`)

The property is declared with `= true` in its initializer and `CreateDefault`
(`AppSettings.cs:296`) is unchanged, so every existing install silently acquires it on the next
launch: deserialization fills the initializer for any key the stored JSON does not contain. Add two
tests beside `PersistenceTests.Settings_round_trip_preserves_values_and_hotkey`
(`tests/Scribe.Core.Tests/PersistenceTests.cs:26`):

1. Save settings with the value flipped, load them back, assert it survived.
2. Load a settings JSON written before the property existed, assert the upgrader value; then assert
   `CreateDefault()` gives the first-run value.

🟡 **The new `RetryWindow` list is not deep copied in `Clone`, and nothing would catch it**
(`src/Scribe.Core/Models/AppSettings.cs:301`)

`Clone` starts from `MemberwiseClone`, so the new list is shared between the settings editor snapshot
and the copy the dictation loop reads. Mirror
`PersistenceTests.Clone_deep_copies_enabled_dictionary_libraries` (`PersistenceTests.cs:80`): clone,
mutate the clone's list, assert the original is unchanged.
````

**If clean:** "Test coverage looks adequate: every behavior this change adds or alters has a test in
`tests/Scribe.Core.Tests`, or is proved by the tool AGENTS.md names for it. Suite still runs with
`dotnet test tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj`."

Render the matrix even on a clean pass. It is the evidence.

---

## Exceptions

Do not flag any of these.

- **A rename, a move, or a reformat with no behavior change.** Nothing new is untested.
- **Presentation code in `src/Scribe.App` or `src/Scribe.Overlay`.** Binding, layout, visual state, the
  pill's geometry and transparency. There is no test project that could reach it and `AGENTS.md`
  prescribes live-log verification for the pill. Only the *decision* hiding inside a `.xaml.cs` is
  yours, and even then the finding is a layering one; cross-reference `core-app-layering`.
- **`tools/**` changes.** The four tools are dev harnesses with no test project and no user-facing
  surface. A change there is proved by running the tool, so ask whether it was run rather than asking
  for a test.
- **Anything requiring the native speech engine.** Say "run AsrCheck", with the fixture step. Do not
  ask for a unit test that loads sherpa-onnx, and do not treat `TranscriptionServiceTests` or
  `TranscriptionAccuracyTests` returning early on a machine without models as a defect.
- **Build, packaging, workflow, MSIX, Velopack, and docs changes.** `build-packaging` and `docs-sync`
  own those. `scripts/Payload-Architecture.ps1` is the mechanical check for payload purity, not a test
  you can ask for here.
- **A behavior already covered under a different name.** If your third grep found it, it is covered.
  Whether that test would actually fail is `tests-quality`'s question, not yours.
- **Removed, skipped, or deleted tests.** That is `guardrail-erosion`. One line handing it over, no
  finding slot.
- **A test that would have to sleep or read the real clock.** P-10 in `references/patterns.md` says the
  decider takes timestamps and counters as inputs. If the only testable shape needs a clock, the
  finding is the shape, not the missing test.
- **On a re-review:** a gap already posted in an earlier round, and any gap whose evidence sits outside
  `delta.patch`. "Nothing new since round N-1" is a correct outcome. Do not manufacture a fourth gap
  because the lens ran again.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:tests-coverage findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
