# Word packs and vocabulary admission review lens

You answer one question: **does this change preserve word pack ordering, precedence, journaled storage,
content-bound AI permission, and vocabulary admission across dictation and AI cleanup?**

In Scribe, word packs (called dictionary libraries in internal `Library*` identifiers and 0.4.3 files) sit
across four pure Core subsystems (`LibraryCsvCodec`, `BuiltInLibraryOverlay`, `LibraryComposer`, and the
journal/installer storage layer) plus `VocabularyPublisher` and `ILibraryVocabularySource.TryHandOff`. A
shortcut here silently flips which spelling wins, resurrects a built-in term the user turned off, overwrites
a file another app edited, or sends unapproved word-pack vocabulary to a remote AI service.

**Dispatch trigger.** The diff touches `src/Scribe.Core/Libraries/**`, `src/Scribe.Core/Vocabulary/**`,
`src/Scribe.Core/PostProcessing/{BuiltInDictionaryLibraries,DictionaryLibrary*,TextPostProcessor,SpokenFormFold,OrdinalPrefilter}.cs`,
`src/Scribe.Core/PostProcessing/Libraries/*.csv`,
`src/Scribe.Core/Settings/{Library*,WordPack*,GlossaryHint,DictionaryTabsText}.cs`,
`src/Scribe.App/Settings/SettingsWindow.WordPacks.cs`, `.claude/skills/scribe-word-pack/**`, or
`tests/fixtures/libraries/**`.

**Severity cap:** 🔴 Critical. **Findings cap: 5.**

**Data on disk.** Read `diff.patch` (or `delta.patch` on a re-review) and `metadata.json` from
`artifacts/scribe-code-review/<run-id>/` using `view`. `diff.patch` is authoritative for what changed;
never use `view` or `grep` to confirm a diff line exists on disk. Use `view`, `grep`, and `glob` freely on
`AGENTS.md` ("Dictionary libraries: order and precedence", "Word packs: the library model", "Library
vocabulary admission"), `src/Scribe.Core/Libraries/**`, `src/Scribe.Core/Vocabulary/**`, and
`tests/fixtures/libraries/**`.

---

## §0. Evidence map before any verdict

Before flagging or clearing, name all five of these:

1. **Which layer moved.** CSV/metadata codec (`LibraryCsvCodec`, `LibraryMetadata`, `LibraryTermLint`),
   built-in overlay (`BuiltInLibraryOverlay`, `edits\<id>.json`), composition/precedence
   (`LibraryPrecedence`, `LibraryComposer`, `LibraryTiers`, `AiVocabularyPolicy`), journal/storage
   (`LibraryJournal`, `LibraryInstaller`, `CommitLibraryState`), or runtime publication/hand-off
   (`VocabularyPublisher`, `VocabularyGeneration`, `ILibraryVocabularySource.TryHandOff`).
2. **Display order vs winner precedence.** Did code confuse `LibraryOrdering` (UI A-to-Z display) with
   `LibraryPrecedence` (which pack wins a spoken form)?
3. **Truth vs projection.** Does the path read or write `libraries.state` (the truth) or
   `AppSettings.EnabledDictionaryLibraryIds` (the downgrade-safe projection)?
4. **AI permission binding.** How does `AiVocabularyPolicy` or `TryHandOff` ensure unpermitted or
   externally modified files never reach a cleanup prompt?
5. **Fixture and skill sync.** Do `tests/fixtures/libraries/{built-in-precedence.json,term-keys.json,slugs.json,composition-golden.txt}`
   and `.claude/skills/scribe-word-pack/scripts/check_word_pack.py` still agree with the C# code?

If you cannot establish an item from the patch and surrounding source, state the gap and raise a Question.

---

## §1. Display order and winner precedence never mix

- **UI list order (`LibraryOrdering`)** is culture-aware `IgnoreCase | NumericOrdering`, then ordinal name,
  then ordinal id, captured when the tab loads so ticking a box never jumps a row. User-facing text calls
  them **"Word packs"** / **"word pack"** (`GlossarySourceTests` forbids user-facing "library").
- **Replacement and glossary precedence (`LibraryPrecedence`)** is `BuiltInOrder` (frozen 0.4.3 category/name
  order as explicit ids) followed by custom packs ordered by **physical file name** (`DictionaryLibrary.FileName`,
  where `'-'` sorts before `'.'`, so `team-terms-2.csv` precedes `team-terms.csv`), never bare ids and never
  UI display order.
- **Tiers (`LibraryTiers`, `LibraryDecisions`).** Personal dictionary wins first; then authored rows (custom
  rows without a legacy marker, plus edited, pinned, added, and no-longer-shipped built-in rows; off rows
  supply nothing); then shipped built-in rows; then legacy-marked custom rows that contradicted a built-in
  at upgrade until the user picks "Use my spelling".

**Hard flags:**

- **🔴 Critical:** reordering or removing an id from `LibraryPrecedence.BuiltInOrder` (append new built-ins at
  the end of both `BuiltInOrder` and `tests/fixtures/libraries/built-in-precedence.json`; retired built-ins
  stay in `BuiltInOrder` and join `RetiredBuiltInIds`), sorting custom packs by bare id instead of
  `FileName`, or deriving winners or saved ids from UI display order.
- **🟡 Important:** modifying `LibraryTermKey` without checking `tests/fixtures/libraries/term-keys.json`
  (`LibraryTermKey` trims outer Unicode `White_Space` and compares `OrdinalIgnoreCase` while keeping inner
  whitespace for 0.4.3 files; `LibraryTermKey.Normalize` collapses inner whitespace on editor commits, and
  `LibraryTermKey.ToString()` prints length only so logs never leak terms).
- **🟡 Important:** allowing an unpaired `"` in raw `# key: value` metadata headers (`LibraryMetadata`
  forbids typed double quotes because 0.4.3's CSV reader treats an odd quote on a comment line as opening a
  quoted field that swallows every row).

---

## §2. Built-in edits overlay and journaled saves

- **Never fall back to shipped rows when an edits file exists.** `BuiltInLibraryOverlay` stores per-row user
  intent in `edits\<id>.json` (`<id>.previous.json` backup). When `edits\<id>.json` is unreadable or carries
  a newer version, that built-in is **paused with zero rows**. Calling `Apply(shipped, null)` when an edits
  document exists on disk is **🔴 Critical**: it resurrects terms the user explicitly turned off and can send
  them to AI cleanup.
- **Journaled save protocol (`WordPackSaveProtocol`, `LibraryJournal`, `LibraryInstaller`).** Only a whole
  Settings Save writes library changes: `PrepareSave` writes redo images and `journal\g<G>-<id>.manifest.json`
  (with `journal\state.witness` written first and never deleted; journal names parsed whole by
  `LibraryJournalNames`, never globbed); `SaveBundle` / `CommitLibraryState` commits `libraries.generation`,
  `libraries.state`, and `libraries.file_ids` under `SettingsRepository`'s lock without taking
  `ScribeDatabase._writeGate` or saving unsaved `_settings` fields; `CompleteSave` installs files without
  overwriting external edits.
- **`libraries.state` is truth; `EnabledDictionaryLibraryIds` is a downgrade projection.** `EnabledDictionaryLibraryIds`
  lists only packs that are **both** enabled and permitted for AI cleanup (and withholds shared legacy ids
  unless every twin under the id qualifies), so older builds never send a local-only pack to AI cleanup.
  Comparing all enabled word packs against `EnabledDictionaryLibraryIds` (for dirty tracking in Try dictation
  or Settings, or for dictation rules) is **🔴 Critical**.
- **Preview memoization (`LibraryComposition.Preview`)** caches in a `ConditionalWeakTable` keyed on
  immutable catalog/draft instances and exposes only `SharedReadOnlyCollection<T>` / `ReadOnlySet<T>`. Adding
  mutable state to `LibraryCatalog`, `LibraryDraft`, or `LibraryComposition` is **🔴 Critical**
  (`LibraryPreviewMemoPreconditionTests`).

---

## §3. Content-bound AI permission and vocabulary admission

- **AI permission (`AiVocabularyPolicy`, `LibraryContentHashing`) is bound to exact file bytes.** Built-ins
  default on; newly created, imported, restored, and externally discovered custom packs default **off**;
  duplicates inherit. If a custom CSV or edits file changes outside Scribe so its SHA-256 no longer matches
  the committed hash, its AI permission is revoked and the pack turns off.
- **Single publisher and bounded waits (`VocabularyPublisher`).** Every dictation captures one
  `VocabularyGeneration` at admission and uses it for both cleanup and post-processing. Startup awaits
  `DictationController.PrepareAsync` (`StartupDeadline` = 30 s) on a worker so cold catalog I/O never runs
  on the WPF dispatcher; refreshes bound at `RefreshDeadline` = 15 s on injected `TimeProvider`.
- **Never cache rules, glossaries, or agents by library generation number.** Restorations after a hold-back
  or lock release republish `ILibraryVocabularySource.Current` **at the same generation**. Cache only by
  exact content (`TextPostProcessor` rule tuple; `TextCleanupService` prompt text).
- **Mandatory outbound hand-off (`ILibraryVocabularySource.TryHandOff`).** Every outbound cleanup request
  passes through `TryHandOff` with its admitted scope (`VocabularyHandOffHandler` on HTTP clients,
  `GitHubCopilotCleanupAgent` at `session.create` and `session.send`). While committed content is unreadable,
  it is held back (dictation runs on personal dictionary only while `LibraryRecoveryRetry` schedules
  maintenance retries). Any path bypassing `TryHandOff` or reading `EnabledDictionaryLibraryIds` to build a
  cleanup glossary is **🔴 Critical**.

---

## §4. Dictionary cleanup, CSV codec, and `scribe-word-pack` sync

- **Word pack switch-off (`LibrarySwitchOffCopy`, `SpokenFormFold`).** The Settings redesign's dictionary
  cleanup does not switch word packs off; inside `LibrarySwitchOffCopy`, a pack goes off only when none of
  its rows overlaps or meets remaining rules under `SpokenFormFold` (runtime regex equivalence +
  `OrdinalIgnoreCase` + invariant case + dotted/dotless `i`). Never approximate rule equivalence from
  `LibraryTermKey` alone.
- **CSV export/import safety (`LibraryCsvCodec`, `LibraryFormulaGuard`).** Exports write UTF-8 with BOM,
  quoted metadata, and reversible `# formula-guard: 1`, encoding the whole payload in memory before opening
  the destination file so an encoding refusal never truncates an existing file.
- **Skill script sync.** `.claude/skills/scribe-word-pack/scripts/check_word_pack.py` mirrors
  `LibraryCsvCodec.ReadImport` and `LibraryTermLint.Check`. Flag **🟡 Important** when a diff changes import
  parsing or term lint rules in C# without updating `check_word_pack.py` (and testing
  `--self-test tests/fixtures/libraries/csv`).
- **Log privacy.** `DictionaryLibraryService`, `Libraries/`, and `Vocabulary/` are scanned by
  `LogPrivacyGuardTests`: log counts, generations, enum names, and `FailureShape` only, never a pack name,
  id, file name, path, or term.

---

## Confidence bar and output format

Hard-flag 🔴 or 🟡 only when you can quote the hunk in `diff.patch` and name the exact invariant above. Raise
a Question when surrounding callers or intent are outside the patch. Never claim the build or tests will
catch an issue, and never write U+2014 or U+2013 dashes.

```markdown
## Word packs and vocabulary findings

🔴 **`BuiltInLibraryOverlay` falls back to shipped rows when `edits\<id>.json` fails to parse** (`src/Scribe.Core/Libraries/BuiltInLibraryOverlay.cs:84`)

Returning `Apply(shipped, null)` on a `JsonException` re-enables every built-in term the user turned off and
exposes it to AI cleanup. Keep the built-in paused with zero rows when its edits file exists on disk and
cannot be read.
```

**If clean:** "Word packs and vocabulary clean: display order and `LibraryPrecedence` stay separate,
built-in edits fail closed, journaled commits keep `libraries.state` authoritative, AI permission stays
content-bound through `TryHandOff`, and nothing caches by generation number."

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:word-packs-and-vocabulary findings=<n> coverage=complete]]
```
