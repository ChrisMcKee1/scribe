# Settings and persistence review lens

You answer one question the per-file lenses miss: **does this survive an upgrade, a clone, a
deserialization, and a downgrade attempt?** Everything in scope here is state that outlives the
process. A defect on this surface does not show up in a build, usually does not show up on the
machine that wrote the change, and shows up on a stranger's install two releases later as lost
settings, a re-prompt for an API key, or a column that does not exist.

**Dispatch trigger:** `src/Scribe.Core/Models/AppSettings.cs`, `src/Scribe.Core/Models/AppProfile.cs`,
`src/Scribe.Core/Models/{DictionaryEntry,Snippet,HistoryEntry,HotkeyBinding,Enums}.cs`,
`src/Scribe.Core/Persistence/**`, `src/Scribe.Core/Security/**`. Also in scope once the diff reaches
it: `src/Scribe.Core/Infrastructure/AppPaths.cs`, because the paths those stores live at are the
other half of "does an existing install keep its data".

**Severity cap:** 🔴 Critical. **Findings cap: 5.**

**Data on disk.** Read `diff.patch` and `metadata.json` from the review cache. `diff.patch` is
authoritative for what the change adds, changes, or removes; the branch may not be checked out, so
never use Read or Grep to confirm that a diff line exists on disk. Do use Read and Grep freely for
surrounding context: this codebase records the incident behind a shape in a comment three lines
above it, and most of the rules below are quoting one.

---

## §0. Evidence map before any verdict

Before you flag or clear anything, name all five of these for each persisted thing the diff touches.
Write the ones you could not establish rather than concluding around them.

1. **Which store it lives in.** Scribe has two, and they behave nothing alike.
   - **The settings JSON document.** `AppSettings` is serialized whole into one row of the `settings`
     table under the key `app_settings` (`src/Scribe.Core/Persistence/SettingsRepository.cs:11`,
     `:57`). `AppProfile` and `HotkeyBinding` ride inside it. There is no per-field schema and no
     migration step: whatever the JSON does not carry comes back as the property initializer's value.
   - **A SQL table of its own.** `DictionaryEntry`, `Snippet`, `HistoryEntry`, and the cleanup failure
     log are rows with columns, created by the `SchemaV1` to `SchemaV6` constants in
     `src/Scribe.Core/Persistence/ScribeDatabase.cs:506-589` and reached through the repositories.
     A new field here is a schema change, not a property.
2. **Its value on a fresh install, and its value after deserializing an install that predates it.**
   These are two different numbers in this codebase and §1.1 exists because they were confused once.
3. **Every reader that takes a snapshot of it.** `AppSettings.Clone` is the snapshot mechanism;
   `DictationCaptureSettingsResolver.Resolve` (`src/Scribe.Core/Hotkeys/DictationCaptureSettingsResolver.cs:12`),
   `DictationController` (`src/Scribe.App/Dictation/DictationController.cs:209`, `:598`), and
   `TextActionController.ApplySettings` (`src/Scribe.App/TextActions/TextActionController.cs:77`) all
   depend on it.
4. **The migration step that puts it on an already-installed machine.** For a column, the
   `if (current < N)` block. For a settings property, `CreateDefault` or nothing at all.
5. **The test that would fail if it were removed.** See §5.

**Escalate rather than bury.** A schema or migration change to the SQLite store is an explicit
**"Ask first"** item in `AGENTS.md` (Ask first list). When the diff contains one, tag the finding
`[needs-maintainer]` so the maintainer-decision gate picks it up, whether or not you also have a
correctness finding about it. That routing is part of the lens output, not an optional extra.

---

## §1. A new `AppSettings` property carries four separate decisions

`AppSettings` is a **mutable sealed POCO class, not a record** (`src/Scribe.Core/Models/AppSettings.cs:10`),
precisely so it can back a settings view-model and round-trip through `System.Text.Json`. Every new
property has to answer four questions, and three of the four have already produced a shipped bug.

### 1.1 `CreateDefault` versus deserialization (🔴 for a first-run opt-in in the initializer)

`AppSettings.CreateDefault()` (`AppSettings.cs:296`) is **deliberately distinct** from
`new AppSettings()`. Its own summary says so: *"this is where first-run opt-ins live, so deserializing
an existing install can never acquire them."*

`EnabledDictionaryLibraryIds` (`AppSettings.cs:100`) documents the mechanism in its `<remarks>`: it
defaults to empty in the property initializer **on purpose**, and `CreateDefault` seeds
`DefaultLibraryIds` instead, so a fresh install gets the libraries while an existing install that
predates a library is never silently opted in by deserialization filling the initializer for a key
its stored JSON does not contain.

**Flag 🔴** a new opt-in expressed as `= true` (or a non-empty collection initializer) on the property
when the feature changes what the user gets without them asking: a library enabled, a rewrite applied,
a capture behavior switched on, anything that reads out as Scribe changing its own output on upgrade.
The fix is a plain initializer default plus a seed in `CreateDefault`.

**Do not flag** a `= true` initializer whose effect on an upgrading install is the same behavior they
already had. `PreviewTextActions` (`AppSettings.cs:47`) and `ShiftEnterLineBreaks` (`AppSettings.cs:259`)
both default to true in the initializer and both are correct: their comments state that the safe value
is on, and an existing install acquiring them gets the safer behavior, not a surprise. The question is
never "is there an initializer", it is "what does an install that predates this key get, and is that
the right answer for them".

### 1.2 `Clone` deep-copies reference types (🔴 for any mutable reference property missing from it)

`Clone` (`AppSettings.cs:301`) starts from `MemberwiseClone` and then **explicitly rebuilds**
`Profiles`, constructing a fresh `AppProfile` per entry with a new `ProcessNames` list, and
`EnabledDictionaryLibraryIds`. The comment gives the reason: a shared list means an edit in the
settings editor mutates the snapshot the dictation loop is reading. The code even annotates plain
value types (`"A plain value type, so the memberwise Clone copies it correctly"`, `AppSettings.cs:273`)
so a reader knows the omission was deliberate rather than forgotten.

**Any new `List<T>`, `Dictionary<,>`, array, or other mutable reference-typed property that `Clone`
does not rebuild is 🔴 Critical.** This is not theoretical aliasing:
`DictationCaptureSettingsResolver.Resolve` (`DictationCaptureSettingsResolver.cs:12`) builds its
per-capture snapshot with `Clone` and then mutates it, so a shallow `Clone` leaks straight into a live
dictation while the settings window is open.

A new immutable reference type (a `record`, a `string`, an `IReadOnlyList<T>` that is never mutated in
place) needs nothing. If the diff adds one and does not touch `Clone`, that is correct; say so in the
clean pass rather than flagging it. When you are not sure whether the type is mutated in place, that
is a **Question**, not a finding.

### 1.3 A secret carries `[JsonConverter(typeof(DpapiProtectedStringConverter))]` (🔴)

Three properties already do: the Azure API key (`AppSettings.cs:210`), the service-principal client
secret (`AppSettings.cs:218`), and the OpenAI-compatible endpoint key (`AppSettings.cs:235`).
`src/Scribe.Core/Security/DpapiProtectedStringConverter.cs` encrypts under current-user DPAPI with
per-use entropy that ties the ciphertext to this specific use (`:19`), exposes plaintext only in
memory, and on a failed decrypt **returns null rather than throwing** (`:40-43`), so a settings file
copied from another machine prompts re-entry instead of bricking settings load.

**Flag 🔴** a new property that holds a key, a secret, a token, a password, or a connection string
carrying one, without that attribute. `AGENTS.md` is separately explicit that a secret must never be
parked in an environment variable or a script on disk, because persistent `AZURE_CLIENT_*` variables
would hijack every other Azure tool on the machine.

Egress of a secret (a log line, a telemetry tag, a diagnostics bundle entry) belongs to
`privacy-egress`, which outranks this lens in the dedup order. Note it in one line and let that lens
own it.

### 1.4 `Normalize` covers the explicit-null case (🟡)

`SettingsRepository.Normalize` (`SettingsRepository.cs:137`) repairs non-nullable members that arrive
as JSON `null`: `Hotkey`, `EnabledDictionaryLibraryIds`, `Profiles`, the prompt strings, the model
ids, and a clamped `DecodeThreads`. A stored `{"profiles":null}` deserializes to a null field on a
property the rest of the codebase treats as non-nullable, and `PersistenceTests.cs:122`
(`Settings_load_normalizes_null_required_members`) pins exactly that.

**Flag 🟡** a new non-nullable reference-typed property, or a new value that needs clamping to a valid
range, that `Normalize` does not cover. This is Important rather than Critical because the failure
needs a hand-edited or truncated settings row to reach it.

---

## §2. Renaming or reshaping something already on disk

`SettingsRepository` serializes with `JsonSerializerDefaults.Web` plus a `JsonStringEnumConverter`
(`SettingsRepository.cs:14-18`). That means camelCase keys, case-insensitive matching on read, and
enum values stored **by name**. Four consequences, all of which the diff can trip silently.

- **A renamed or removed enum member reachable from `AppSettings` resets the user's entire settings
  document. 🔴** The stored string no longer matches any member, `JsonStringEnumConverter` raises a
  `JsonException`, and `SettingsRepository.Load` catches it (`:46`), sets `LastLoadFailed`, preserves
  the old document under `app_settings_recovery`, and returns `AppSettings.CreateDefault()`. The user
  does not lose one setting, they lose all of them. `PersistenceTests.cs:107` pins that fallback path
  for malformed JSON. Adding a member at the end is safe; renaming or deleting one is not. Confirm the
  enum is actually reachable from a persisted property before flagging, because plenty of enums in
  Core never touch the settings document.
- **A renamed property key loses its stored value silently. 🔴** Web defaults are case-insensitive, so
  changing casing alone is safe. Changing a word is not: the stored key becomes an unmatched member,
  it is ignored without an exception, and the property comes back as its initializer default while
  every other setting survives. There is no rescue path for this one, so it is worse than the enum
  case even though it throws nothing.
- **A renamed positional parameter on a persisted record is the same defect wearing a different hat.
  🔴** `HotkeyBinding` (`src/Scribe.Core/Models/HotkeyBinding.cs:16-23`) is a positional `sealed record`
  serialized inside the settings document, and its own doc comment says of `SuppressChordMembers`:
  *"The name is load-bearing for settings deserialization, so it stays even though the state machine
  now narrows what it means."* A rename here silently reverts that field to its default for every
  existing install. `AppProfile` is a mutable class, so it does not have this hazard, but
  `DictionaryEntry`, `Snippet`, and `HistoryEntry` are positional records mapped to SQL columns by
  hand in their repositories, where a rename must move the column mapping with it.
- **Changing the DPAPI entropy string makes every stored secret vanish without an error. 🔴** The
  entropy is versioned in the constant itself (`DpapiProtectedStringConverter.cs:19`,
  `"Scribe.AzureApiKey.v1"`). Because `Read` returns null instead of throwing, an entropy change is
  indistinguishable from "the user never entered a key": no exception, no log, no recovery copy, just
  a cleanup path that quietly stops working. If a rotation is genuinely intended, it needs a stated
  migration story, and that is a maintainer decision.

---

## §3. SQLite schema and migration

`ScribeDatabase.Migrate` (`src/Scribe.Core/Persistence/ScribeDatabase.cs:383`) is additive and
forward-only, gated on `PRAGMA user_version`, with every step inside one transaction and
`user_version` set at the end of it (`:427-428`). `SchemaVersion` is currently **6** (`:23`).

**The single most common miss: a new column or table with no `SchemaVersion` bump and no matching
`if (current < N)` block. 🔴** On an upgraded install the table already exists, so the `CREATE TABLE`
in `SchemaV1` never runs again and the column simply is not there. The change works perfectly on any
machine whose database was created after it, which includes the author's if they wiped their profile,
and fails on every real user's.

The rest of the rubric for a schema change:

- **Additive only.** No `DROP`, no column rename, no destructive rewrite. `SchemaV4` (`:572`) is the
  one data migration in the set, a targeted `DELETE` of timestamp-shaped junk snippet rows, and it is
  `internal` rather than `private` because the salvage path re-executes it (`:280`) after copying rows
  out of a damaged file. **A data migration must therefore be idempotent**, because it runs more than
  once by design. Flag a new non-idempotent data step 🔴.
- **Guard a late step with a column probe.** Steps 5 and 6 are additionally gated on
  `HistoryNeedsCleanupColumn` and `HistoryNeedsColumn` (`:416`, `:421`), so a database that was
  partially migrated, or whose column was added lazily by the repository, converges instead of failing
  on a duplicate `ALTER`. A new `ALTER TABLE` step with no probe is 🟡 unless you can show the column
  can only ever come from this one place.
- **One transaction, `user_version` last.** Splitting the steps across transactions, or setting
  `user_version` before the last step, leaves a half-migrated database on a crash. 🔴.
- **Never weaken the downgrade guard.** `Migrate` throws when `current > SchemaVersion` (`:386-390`)
  with a message telling the user to install a newer Scribe rather than silently operating on data it
  does not understand. Pinned by `SnippetMigrationTests.Future_schema_is_rejected_without_retry_leaks`.
  Removing or softening that branch is 🔴.
- **A new table joins `SalvageTables`. 🔴** The salvage rebuild copies a **fixed list**
  (`ScribeDatabase.cs:26-27`: `settings`, `dictionary`, `snippets`, `audio_blobs`, `history`,
  `cleanup_failures`), ordered so foreign-key targets are restored before the rows referencing them
  (`:271`). A table absent from that list is silently dropped the first time a user's database is
  rebuilt after corruption. Order matters as much as membership: a new table referencing `audio_blobs`
  must land after it.
- **Thread a new optional column through the repository, not just the schema.**
  `HistoryRepository` self-heals with `EnsureHistoryColumn` (`src/Scribe.Core/Persistence/HistoryRepository.cs:193`),
  probing for the column, attempting one `ALTER`, and **degrading gracefully** if that fails, with the
  insert and select column lists composed conditionally (`:44-58`, `:105-111`). A new column added to
  the schema but hard-coded into the SQL text without that probe turns a failed `ALTER` from a missing
  timing number into a broken history page. This is the classic N-1 of N partial conversion on this
  surface: schema bumped, write path updated, read path left behind (or the reverse).
- **A multi-section save stays in one transaction.** `SettingsRepository.SaveBundle` (`:61`) writes
  settings, dictionary, and snippets under a single `BeginTransaction`; pinned by
  `PersistenceTests.Settings_bundle_rolls_back_all_sections_when_a_later_section_fails` (`:233`). A new
  persisted section wired into the settings save path but saved outside that transaction can leave
  settings and dictionary disagreeing after a mid-save failure. 🟡, 🔴 when the two halves are read
  together on the dictation path.
- **WAL and `busy_timeout` stay.** `PRAGMA journal_mode=WAL` (`:141`, and again on the rebuilt
  database at `:253`) and `PRAGMA busy_timeout=10000` (`:22`, `:163`) exist because two processes and
  several short-lived connections share this file. Flag their removal.
- **`ExpectedSqliteVersion` and the CVE pin. 🔴** `ExpectedSqliteVersion` (`:20`, currently `3.53.4`)
  asserts the exact native version at runtime, and `PersistenceTests.Database_loads_the_CVE_patched_native_sqlite`
  (`:11`) proves the pinned native is the one in use. `SQLitePCLRaw.bundle_e_sqlite3` is referenced
  directly in `Directory.Packages.props:29` at 3.0.5 to override a transitive bundle affected by
  **CVE-2025-6965**; `AGENTS.md` puts removing that pin on the **Never** list and requires it stay at
  or above 3.0.3. The constant moves only deliberately and only together with the package version.
  Flag a constant that moved without the package, a package that moved without the constant, and any
  removal of either. `guardrail-erosion` may also fire on this; keep the finding here (this lens is
  more specific in the dedup order) and note the overlap in one line.

---

## §4. `AppPaths`: two families of path that are not interchangeable

`AGENTS.md` rewrote this section at 0.3.11 after a shipped Store build told users to open a folder
that was not there, and the bug behind the support request went uninvestigated as a result. On
Windows 10 1903 and later, a folder a packaged app **creates** under AppData is redirected into the
package's private store, and reads come back through a merged view, so the app sees its own path
working perfectly while File Explorer sees nothing.

- **Scribe's own file I/O uses the plain `RootDir`, `LogsDir`, `DatabasePath`** (`AppPaths.cs:81`,
  `:100`, `:109`). Inside the container the merged view resolves them correctly whether or not
  redirection is on. Pointing internal I/O at the package store would work today and break the moment
  redirection is turned off. **Flag 🔴** a new `File.*`, `Directory.*`, or connection string built on
  an `Effective*` path.
- **Anything handed outside the process uses `EffectiveRootDir`, `EffectiveLogsDir`,
  `EffectiveDatabasePath`** (`:150`, `:153`, `:156`): the About page text boxes
  (`src/Scribe.App/Settings/SettingsWindow.xaml.cs:192-193`), the Copy buttons (`:2791`, `:2794`),
  `OpenFolder` (`:2797`, `:2854`), and the session banner
  (`src/Scribe.Core/Diagnostics/SessionBanner.cs:99-102`, `:163`). Explorer and the clipboard live
  outside the container, so a plain path there is the one that reads as "that folder is not there".
  **Flag 🔴** a new user-visible path string, clipboard write, or shell-open built on `RootDir`.
- **`EffectiveRootDir` comes from a probe, not from inference.** `ResolveEffectiveRoot` (`:355`) writes
  a uniquely named marker through `RootDir`, looks for it at the package-store twin, and deletes it
  (`:370-384`); the whole thing is wrapped so a failed probe leaves the honest default. It has to be a
  probe because the answer differs per machine: redirection applies only to folders the app *creates*,
  so a PC that has also run the direct-download build already has a real `ScribeData` and is not
  redirected, while a Store-only PC is. **Flag 🔴** a replacement that infers the answer from package
  identity, an OS build check, or a manifest flag. `PackagedDataMigrationTests.The_probe_leaves_nothing_behind_in_the_data_folder`
  (`:75`) pins the cleanup half.
- **Both install channels share `%LOCALAPPDATA%\ScribeData` on purpose**, so a user can move between
  the Store and the direct download without losing settings, dictionary, or history. `AppFolderName`
  is a constant (`:20`) and the data root is deliberately a sibling of the Velopack install root
  (`:9-14`), which the installer renames aside and deletes on every reinstall. Flag any change that
  moves data under the install root or makes the two channels diverge.
- **A one-time migration is copy-only-when-the-destination-is-absent.** `TryMigrateDatabase` (`:283`)
  returns early if a database already exists at the new root, so it never overwrites current data and
  is a no-op on every subsequent launch, and `EnsureCreated` runs the legacy migration before the
  virtualized one on purpose (`:245-258`) so a machine that went through both channels keeps the
  Velopack copy. Flag a new migration that can overwrite, or one inserted in a position that changes
  which source wins. `AppPathsTests.TryMigrateDatabase_never_overwrites_an_existing_database` (`:182`)
  is the pin.
- **The fallback root is deliberately not under `Path.GetTempPath()`** (`:170-176`), because that
  resolves inside `%LOCALAPPDATA%\Temp`, which Storage Sense and Disk Cleanup are entitled to empty,
  and a fallback session still writes the database, the dictionary, and the encrypted API key. Flag a
  change that moves it there.

---

## §5. The test that has to come with it

`tests/Scribe.Core.Tests` has the pins for every rule above, and a change on this surface without one
is a real gap rather than a style preference. Name the specific file when you ask for a test:

| Change | Where its test belongs |
| --- | --- |
| A new settings property, a round trip, a null-normalize case | `tests/Scribe.Core.Tests/PersistenceTests.cs` |
| A `Clone` deep-copy entry | `PersistenceTests.Clone_deep_copies_enabled_dictionary_libraries` (`:80`) is the shape to copy |
| A first-run opt-in, or a default that must not reach existing installs | `tests/Scribe.Core.Tests/DefaultLibraryOptInTests.cs` |
| A schema step, a downgrade rejection | `tests/Scribe.Core.Tests/SnippetMigrationTests.cs` |
| A salvage-visible table, corruption behavior | `tests/Scribe.Core.Tests/DatabaseSalvageTests.cs` |
| A path family, a fallback root, a legacy migration | `tests/Scribe.Core.Tests/AppPathsTests.cs` |
| Virtualization, the probe, a forward-copy of package data | `tests/Scribe.Core.Tests/PackagedDataMigrationTests.cs` |

The migration test that counts is the one that **starts from the previous schema version and asserts
the new state**, the way `SnippetMigrationTests.V6_migration_adds_transcription_model_id_to_v5_history`
(`:13`) does. A test that creates a fresh database at the current version and checks the column exists
passes with or without the migration block and is worse than no test, because it manufactures
coverage for exactly the defect this section is about. Say that plainly when you see it, and hand the
detailed test critique to `tests-quality`.

---

## Confidence bar

**Hard-flag** (🔴, or 🟡 where the section says so) only when you can point at the hunk and name the
specific install that breaks:

- the new property is in the diff and `Clone`, `CreateDefault`, or the converter list is visibly
  unchanged in the same diff,
- the new column or table is in the diff and `SchemaVersion` is visibly unchanged,
- the renamed identifier is on a type you have confirmed is reachable from a persisted document,
- the path is user-visible and built on the wrong family, or internal and built on the wrong family.

**Raise a Question** when the mechanism is right but the intent is not yours to settle: whether a new
reference-typed property is ever mutated in place, whether a default is meant to reach existing
installs, whether an entropy or schema change is a deliberate reset with a migration plan you cannot
see, whether a repository field maps to a column you cannot locate. The author has context you lack,
and a Question costs one line while a wrong 🔴 on this surface costs trust in the whole review.

**Never** write "this will not compile" or "the tests will catch it". Three defects in one Scribe
release compiled warning clean. A missing `Clone` entry compiles, a missing schema bump compiles, and
both pass every test on a machine whose database is new.

---

## Output format


The findings below are **illustrative shapes**, not live defects. `ProfileHotkeyOverrides` and
`history.language_id` are invented and do not exist. `SchemaV6` currently adds
`transcription_model_id`, and `Clone` already rebuilds every reference-typed property it carries.
Never cite either invented name as an existing exemplar.

```markdown
## Settings and persistence findings

🔴 **`ProfileHotkeyOverrides` is a `Dictionary<string, string>` that `Clone` does not rebuild** (`src/Scribe.Core/Models/AppSettings.cs:288`)

`Clone` (`AppSettings.cs:301`) memberwise-copies and then explicitly rebuilds only `Profiles` and
`EnabledDictionaryLibraryIds`, so the new dictionary is shared between the original and the clone.
`DictationCaptureSettingsResolver.Resolve` builds its per-capture snapshot with `Clone`
(`DictationCaptureSettingsResolver.cs:12`), so an edit in the open settings window mutates the
settings a dictation already in flight is reading. Add the rebuild next to the existing two:

    clone.ProfileHotkeyOverrides = new Dictionary<string, string>(ProfileHotkeyOverrides);

🔴 **`history.language_id` is added to `SchemaV6` with no `SchemaVersion` bump** `[needs-maintainer]` (`src/Scribe.Core/Persistence/ScribeDatabase.cs:586`)

`SchemaVersion` is still 6 (`:23`), so `Migrate` returns at `if (current == SchemaVersion)` (`:393`)
on every already-installed machine and the column never appears. It exists only on a database created
after this change. Bump `SchemaVersion` to 7, move the `ALTER` into a new `SchemaV7`, and add
`if (current < 7 && HistoryNeedsColumn(connection, transaction, "language_id"))` alongside the v5 and
v6 blocks. `AGENTS.md` lists schema changes under "Ask first", so this also needs the maintainer.
```

**If clean:** `Settings and persistence clean: new state is defaulted, cloned, and migrated correctly, and no persisted identifier changed shape.`

---

## Exceptions

Do not flag any of these. A lens that only knows how to flag things produces noise, and every item
here is a shape that is already correct in this repository.

- **A `= true` or non-empty initializer whose effect on an upgrading install is the behavior they
  already had.** `PreviewTextActions` and `ShiftEnterLineBreaks` are correct as written. §1.1 is about
  a default that changes what the user gets, not about the presence of an initializer.
- **A new value type, `string`, `record`, or nullable primitive absent from `Clone`.**
  `MemberwiseClone` copies it correctly, and `AppSettings.cs:273` says so in the source for exactly
  this reason. Only mutable reference types need the rebuild.
- **A new enum member appended to an existing enum.** Only a rename or a removal breaks a stored
  document. Adding a value to `OverlayPosition` has its own hazard, the by-name overlay enum twin, and
  that belongs to `overlay-process-contract`.
- **`SchemaV4` being a `DELETE` rather than a `CREATE` or `ALTER`.** It is the documented data-cleanup
  exception, it is `internal` on purpose, and the salvage path re-runs it deliberately. Flag a *new*
  non-idempotent data step, not this one.
- **`HistoryRepository` doing its own `ALTER TABLE`.** The lazy `EnsureHistoryColumn` self-heal is the
  blessed shape here, not a migration bypass. What earns a finding is a new column that skips it.
- **The DPAPI converter returning null on a failed decrypt.** That is the fail-soft contract: a
  settings file copied between machines prompts re-entry instead of bricking load. Do not ask for it
  to throw.
- **Anything about where a secret goes after it leaves settings**, and anything about
  `AzureCredentialFactory.Invalidate` after a settings write. `privacy-egress` and `azure-credential`
  both outrank this lens in the dedup order and own those. One cross-reference line is the right
  amount of coverage.
- **Test construction critique beyond "the migration test starts from the wrong version".**
  `tests-quality` owns mock honesty and assertion strength; `tests-coverage` owns whether a test
  exists at all. Name the missing pin and the file it belongs in, then stop.
- **Pre-existing state that this diff did not introduce.** A property that was already missing from
  `Clone` before this change is not a finding on this change unless the diff makes it newly reachable
  from the dictation path.


---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:settings-and-persistence findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
