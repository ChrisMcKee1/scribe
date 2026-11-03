# Correction record: design

Status: proposal, 2026-11-03. It adds a table to `scribe.db`, which AGENTS.md lists under "Ask first", so nothing
here is built until the maintainer approves it. Every claim about today's code below was read from the source on
2026-11-03 and names the file and line.

## The problem

When someone fixes a dictation with the tray's Add to dictionary (quick add), Scribe saves the dictionary rule and
rewrites its in-memory copy of that dictation, and nothing else. The history row keeps the text exactly as it was
typed, and nothing records what was wrong, what it became, or which dictation it came from. Learn from history reads
only that history text, so the one piece of evidence it cannot find on its own, a user saying "this was misheard, it
should be that", is thrown away.

The code already says that evidence is what it lacks. `DictionaryTermVariants`
([DictionaryTermVariants.cs:21](../src/Scribe.Core/PostProcessing/DictionaryTermVariants.cs)) says a phonetic fix
such as "Get Up" to "GitHub" "is not derivable and must come from the user correcting a dictation", and
`DictionaryHistoryLearner` ([DictionaryHistoryLearner.cs:6](../src/Scribe.Core/PostProcessing/DictionaryHistoryLearner.cs))
says history "never" shows the misrecognition a rule would repair.

## Goals

1. Keep a durable, local record of every correction a user makes to a dictation: the dictation it belongs to, the
   words they changed, what they changed them to, and the dictation before and after.
2. Show the corrected text wherever Scribe shows or hands back a dictation, after a restart too.
3. Let Learn from history and the Usage page tell a corrected mishearing apart from the user's real vocabulary, and,
   in a later phase, learn from the corrections themselves.
4. Change nothing that is sent anywhere until a later phase that changes the disclosure with it.

## Not goals

- Telemetry. The record never leaves the PC in phase 1, and the log gets shapes only.
- Watching edits made in the app the text went into. Scribe does not read other apps' fields, by design.
- Storing the recognizer's raw output. That is a separate decision with its own privacy and disk cost (see Risks).
- Rewriting dictionary rules automatically from corrections. Every rule still comes from the user, or from a
  suggestion the user reviews.

## What happens today

| Step | Code | What it does |
| --- | --- | --- |
| Insertion | [DictationInsertion.cs:56](../src/Scribe.Core/TextInjection/DictationInsertion.cs) | `recovery.Set(text)` puts the dictation in `LastTranscriptStore` before typing it |
| Recent dictations | [LastTranscriptStore.cs:39](../src/Scribe.Core/PostProcessing/LastTranscriptStore.cs) | A slot is a Guid, the text as recorded, the current text, a time and a revision. Five slots. No history row id |
| History write | [DictationController.cs:1654](../src/Scribe.App/Dictation/DictationController.cs), [HistoryWriter.cs:115](../src/Scribe.Core/Persistence/HistoryWriter.cs) | Queued only after a successful insertion and committed later on the writer thread. `HistoryRepository.Add` returns the row with its new id, and the writer throws it away |
| Seeding after a restart | [App.xaml.cs:1577](../src/Scribe.App/App.xaml.cs), [LastTranscriptStore.cs:149](../src/Scribe.Core/PostProcessing/LastTranscriptStore.cs) | `SeedHistory` fills empty slots from `history.text` and drops `history.Id` |
| Quick add save | [QuickAddWindow.xaml.cs:970](../src/Scribe.App/QuickAdd/QuickAddWindow.xaml.cs) | Saves the rule, then `QuickDictionaryAdd.Apply(transcript, rule)` gives the corrected text |
| After the save | [App.xaml.cs:2552](../src/Scribe.App/App.xaml.cs) | `LastTranscriptStore.Update(old, new)` rewrites matching slots in memory, then the vocabulary reloads. The "Your last dictation was corrected to match." text built at line 2588 is never shown |
| History table | [ScribeDatabase.cs:1399](../src/Scribe.Core/Persistence/ScribeDatabase.cs) | `text` is the final inserted text. The only UPDATE `HistoryRepository` makes is the rating ([HistoryRepository.cs:398](../src/Scribe.Core/Persistence/HistoryRepository.cs)) |

So a fix lasts until Scribe restarts: seeding reads `history.text`, and the tray's recent dictations show the mistake
again. Settings > History, its search and its Copy show the mistake all along.

### Who reads history text

| Reader | Code | Reads |
| --- | --- | --- |
| Settings > History list, search, Copy, AI report | [SettingsWindow.History.cs](../src/Scribe.App/Settings/SettingsWindow.History.cs) | `Text` |
| Tray seeding and Copy last dictation's fallback | [App.xaml.cs:1584](../src/Scribe.App/App.xaml.cs), [App.xaml.cs:2168](../src/Scribe.App/App.xaml.cs) | `Text` |
| Learn from history, tray (adds rules without review) | [App.xaml.cs:2329](../src/Scribe.App/App.xaml.cs) | `Text`, through `DictionaryHistoryLearner` |
| Learn from history, Settings (AI, or the miner as fallback; rows go to the grid for review) | [SettingsWindow.YourWords.cs:867](../src/Scribe.App/Settings/SettingsWindow.YourWords.cs) | `Text` |
| Dictionary cleanup | [SettingsWindow.YourWords.cs:1134](../src/Scribe.App/Settings/SettingsWindow.YourWords.cs) | `Text` |
| Usage page (totals, recurring terms, words you could add) | [UsageReport.cs:75](../src/Scribe.Core/Diagnostics/UsageReport.cs) | `Text` |
| Diagnostics stats, App profiles' recent apps | `DictationStats`, `RecentApps` | durations and app names only |

## Decisions for the maintainer

Each has a recommendation; the design below follows it.

1. **History text stays as typed** (recommended), and the correction is stored beside it. The row stays a true record
   of what the app received, every older build and every existing reader keeps working, and the mistake stays
   available as evidence. The alternative, rewriting `history.text`, loses the evidence, and an older build would
   show the rewritten text with no way to tell.
2. **Copy in History copies the corrected text** (recommended). Fixing the dictation is why the user made the
   correction. The text as typed stays visible on the row.
3. **A correction stays shown when its rule is later disabled or deleted, but stops feeding Learn from history**
   (recommended). The fix was right for that dictation when it was made, but a rule the user turned off is their
   decision about the term in general (the same reasoning as `DictionaryHistoryLearner`'s comment on disabled rows).
4. **Store the dictation before and after each correction** (recommended), not character offsets alone. Each row is
   then complete in itself, survives corrections chained on one dictation, and needs no replay of
   `TextPostProcessor.ApplyRule`, whose behavior can change between releases. The cost is two copies of a dictation's
   text per correction, deleted with the row.
5. **Phase 2 sends correction pairs with an AI Learn from history request** under its existing consent, with the
   disclosure changed in the same commit (recommended, decided when phase 2 starts).

## Design

### The `history_corrections` table

```sql
CREATE TABLE IF NOT EXISTS history_corrections (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    history_id    INTEGER NOT NULL,
    created_utc   TEXT    NOT NULL,
    source        INTEGER NOT NULL,          -- CorrectionSource: 1 = quick add
    kind          INTEGER NOT NULL,          -- CorrectionKind: 1 = replace, 2 = remove
    pattern       TEXT    NOT NULL,          -- the rule's spoken form, as saved
    replacement   TEXT    NOT NULL,          -- the rule's written form, as saved ('' for remove)
    whole_word    INTEGER NOT NULL,
    dictionary_id INTEGER NULL,              -- the rule's row; informational, it may be edited or deleted later
    before_text   TEXT    NOT NULL,          -- the dictation as it read before this correction
    after_text    TEXT    NOT NULL           -- the dictation as it reads after it
);
CREATE INDEX IF NOT EXISTS ix_history_corrections_history ON history_corrections (history_id, id);
CREATE TRIGGER IF NOT EXISTS tr_history_corrections_follow_history
AFTER DELETE ON history
BEGIN
    DELETE FROM history_corrections WHERE history_id = old.id;
END;
```

- One row per correction. A dictation corrected twice has two rows: the second's `before_text` is the first's
  `after_text`, and the newest row's `after_text` is the dictation's corrected text.
- `pattern`, `replacement` and `whole_word` come from the `DictionaryEntry` the save returned, which is exactly what
  `QuickDictionaryAdd.Apply` applied. They are kept even though the rule is in `dictionary`, because the rule can be
  edited or deleted later and the record must still say what this correction was.
- **A trigger, not a foreign key, ties a correction to its row.** Today's file connections set `ForeignKeys = true`
  ([ScribeDatabase.cs:505](../src/Scribe.Core/Persistence/ScribeDatabase.cs)), but a trigger runs whatever a
  connection sets: an older build, the macOS port's store, or a SQLite tool that deletes a history row deletes its
  corrections too. That is a privacy property, not a convenience, because the corrections hold the dictation's text.
  With no `REFERENCES` clause, a correction for a row that is gone is refused by the insert's own guard (below)
  rather than by a constraint error.
- The trigger covers every way history is deleted today: `Delete`, `Clear`, `PruneOlderThan` and
  `DeleteEntriesOlderThan` ([HistoryRepository.cs:415](../src/Scribe.Core/Persistence/HistoryRepository.cs) to 562),
  in the same transaction as the history delete.

### Schema and compatibility

- `user_version` stays 7 (pattern P-11). `EnsureAdditiveSchema`
  ([ScribeDatabase.cs:1157](../src/Scribe.Core/Persistence/ScribeDatabase.cs)) creates the table, the index and the
  trigger on every open, in one transaction, so the trigger never exists without its table. Nothing ever drops the
  table; dropping it alone would leave a trigger that fails every history delete.
- `HistoryRepository` probes for the table once, the way it probes its newer columns, and treats a missing table as
  "no corrections": nothing is recorded and the reads return the text as typed. A database where the additive step
  failed still works.
- A database repair copies `history_corrections` after `history` (add it to `SalvageTables`,
  [ScribeDatabase.cs:38](../src/Scribe.Core/Persistence/ScribeDatabase.cs)), into a fresh schema that already has the
  trigger.
- Older builds ignore the table, and their deletes still remove corrections through the trigger. An older build's
  repair rebuilds without the table, so the corrections are lost there; that loses data, never exposes it.
- **Clear gets slower.** SQLite erases a whole table without visiting rows only when the table has no triggers, so
  `DELETE FROM history` becomes a row-by-row delete. Measure Clear on a large history (50,000 rows) before and after;
  if it matters, Clear can delete `history_corrections` first in the same transaction, which leaves the trigger
  nothing to find but does not bring the optimization back.

### Linking a recent dictation to its history row

A correction needs the history row's id, and today nothing that quick add can see has it.

1. `LastTranscriptStore.Slot` gains `long? HistoryId`. `Set(text)` returns the new slot's Guid, and
   `DictationInsertionResult` carries it (internal, like `Typed`).
2. `IHistoryWriter.Enqueue` gains an optional `Action<long>? committed`, called once on the writer thread with the new
   row's id after its transaction commits, never for a write that was dropped, abandoned or failed. It runs right
   after `Add` returns in `Consume` and before the write counts as finished (before `_unfinished.Remove`), so a caller
   whose `WaitForAcceptedWrites` returned true knows the callback has run. A callback that throws is caught and
   logged by shape; it can never stop the writer.
3. `EnqueueHistory` passes `id => _lastTranscript.AttachHistory(slotId, id)`. A slot that was replaced or evicted in
   the meantime ignores it.
4. `SeedHistory` keeps `history.Id`, and seeds a slot's current text from the corrected text (below) while its
   `HistoryText` stays the text as typed, which is what `HistoryDeletionNotifier.Covers` compares a deleted row with.
   With ids on slots, `Covers` can match by id first and fall back to text.
5. `RetainedTranscript`, `QuickAddSource` and `QuickAddResult` carry the slot's Guid and its `HistoryId` as known
   when the window opened, and `QuickAddResult` also the slot's `HistoryText`.

A dictation whose insertion failed has a slot and no history row (history keeps text only after a successful
insertion, as PRIVACY.md says), so a correction to it is not recorded. Neither is one to a slot seeded by the older
text-only `Seed(IEnumerable<string>)`.

### Recording a correction

A new Core type, `CorrectionRecorder`, owns the write; `OnQuickAddSaved` hands it the result right after the in-memory
repair and does not wait for it.

1. Only a save that changed the dictation is recorded (`QuickAddResult.CorrectedTranscript` is not null).
2. The recorder runs its work one item at a time on a background lane, in the order the saves were made, so chained
   corrections land in order.
3. It first waits, at most `OrderedHistoryRepository.MaintenanceWait` (10 s), for history writes accepted before the
   save (`IHistoryWriter.WaitForAcceptedWrites`), so a dictation made just before the user opened quick add has its
   row. Then it takes the id the window already had (a seeded slot always has one), or else reads the slot's
   `HistoryId`; with none (the slot was evicted, or its write never committed), it records nothing.
4. The insert is guarded in SQL, under `EnterWriteScope` like every other non-settings writer:

   ```sql
   INSERT INTO history_corrections (history_id, created_utc, source, kind, pattern, replacement,
                                    whole_word, dictionary_id, before_text, after_text)
   SELECT $history_id, $created, $source, $kind, $pattern, $replacement,
          $whole_word, $dictionary_id, $before, $after
   WHERE EXISTS (SELECT 1 FROM history WHERE id = $history_id AND text = $history_text);
   ```

   A row deleted while quick add was open, or one whose text is not the slot's, gets nothing, in one statement, so
   there is no window in which an orphan exists.
5. Failures are logged by shape and never shown: the rule is saved and working, exactly as the ring repair's failure
   is treated today.
6. Exit drains the lane for at most 2 s, as one more `StagedTeardown` step before host disposal; a correction still
   queued after that is dropped with a shape-only line. Exit's worst case grows by those 2 s.

### Reading: the text as typed and the corrected text

`HistoryEntry` gains a trailing `string? CorrectedText = null` (source compatible with its 179 callers) and a
`CurrentText` property, `CorrectedText ?? Text`. `CreateHistoryReadCommand`
([HistoryRepository.cs:289](../src/Scribe.Core/Persistence/HistoryRepository.cs)) selects

```sql
(SELECT after_text FROM history_corrections c WHERE c.history_id = history.id ORDER BY c.id DESC LIMIT 1)
```

or `NULL` when the table is missing; the index makes it one lookup per row. `Search` matches either text.

| Reader | Reads after this change | Why |
| --- | --- | --- |
| History list | `CurrentText`, with a "Corrected" marker and the text as typed shown on the row | The fix is what the user expects to see |
| History Copy | `CurrentText` | Decision 2 |
| History AI report | `Text` | The report is about what AI cleanup produced |
| History search | both | Either wording finds the row |
| Tray seeding, Copy last dictation's fallback | `CurrentText` | A fix survives a restart |
| Learn from history, offline miner (tray and Settings) | `CurrentText`, and every live correction's `pattern` added to the miner's known set | A corrected mishearing is not the user's vocabulary and is never suggested back |
| Learn from history, AI (Settings) | `Text`, unchanged in phase 1 | PRIVACY.md says the request carries dictations "as they were inserted"; changing that waits for phase 2's disclosure change |
| AI suggestion results | the phase 0 filter, applied to live corrections' pattern and replacement as well as the rules | A suggestion that writes a corrected mistake is dropped on this PC |
| Dictionary cleanup | `Text`, and a rule with a live correction counts as used | A correction is direct evidence the rule matters |
| Usage page | `CurrentText` | Recurring terms and "words you could add" stop counting mishearings |

A correction is live when a dictionary row with its `pattern` (trimmed and compared case-insensitively, as the
dictionary compares spoken forms) still exists and is enabled (decision 3).

Before switching the Usage page and dictionary cleanup, run their equivalence suites (`UsageEquivalenceCorpus`,
`DictionaryUsageAnalyzerTests`) over corpora that include corrected rows: the algorithms do not change, their input
does, and the AI usage insight still sends only labels of terms the dictionary covers.

### Learn from history learning from corrections (phase 2)

Phase 1 only stops corrected mistakes from being treated as vocabulary. Phase 2 learns from the pairs.

- **Offline, Settings only.** For each live correction, look through `CurrentText` for words that sound like
  `pattern` (`VocabularyMentions.SoundKey` and `Distance`, the matching AI cleanup's glossary already uses) and are
  neither `pattern` nor `replacement`, and offer `heard variant -> replacement` as suggestions in the grid for review.
  Never on the tray path, which adds rules without review. Ship it only once a fixture corpus shows its false
  positives (a variant that is an ordinary word) are rare, with the corpus committed beside the tests.
- **AI.** `AiDictionarySuggester.BuildFullContextRequest` and `BuildHistorySample` gain a bounded "corrections you
  made" section: each live pair with its `before_text`, within the request's existing token budget, and the system
  prompt asks for other mishearings of the same terms. This changes what the request carries, so the same commit
  changes `CleanupDisclosure.SuggestionConsentFor` and `FullContextSuggestionConsentFor`, PRIVACY.md (the paragraph at
  line 372), the README if it describes the request, and `CleanupDisclosureTests`, and the AI path then reads
  `CurrentText`.

### Where corrections come from

| Source | Recorded | Notes |
| --- | --- | --- |
| Quick add, Save or Save and close, replace or remove | Phase 1 | "Fix instead" stays in the same window, so it is covered |
| Quick add's "Add it in Settings" | Phase 3 | Opens a Settings draft that knows only the spoken form; the history id would have to travel with the draft and be recorded only by the Save that stores the rule |
| A "Fix a word" action on a Settings > History row | Phase 3 | Opens quick add on that row, whose id is known. Today quick add reaches only the last five dictations, so this is where most corrections could come from |
| Thumbs down on an AI result | Already stored (`ai_rating`) | A cleanup quality signal, not a vocabulary one; Learn from history does not read it today and this design does not change that |
| Repeating a dictation right away | Not planned | Inferring a correction from a retry is a guess; it would need its own evaluation and could never create a rule by itself |
| Your words edits, Learn from history additions, the Usage page's add | Never | Not tied to a dictation |
| Edits in the app the text went into | Never | Scribe does not read other apps' fields |

## Privacy, retention and deletion

- A correction holds a dictation's text and is treated exactly as history text: only in `scribe.db`, deleted with its
  row by every path (the trigger), under the history retention setting, kept with history while
  `KeepAllTextThisSession` holds, and overwritten with zeros on delete (`secure_delete` is on for every connection).
  A history deletion already owes the WAL checkpoint (`StorageMaintenance.NoteDeletion`), and the corrections go in
  the same statement, so PRIVACY.md's timing still holds. If a later phase lets a user remove a correction on its own,
  that path owes the checkpoint too and joins PRIVACY.md's list.
- PRIVACY.md's "A history entry may include" list (line 58) gains: "Corrections you made to it with Add to
  dictionary: the words you changed, what you changed them to, and the dictation before and after".
- Phase 1 sends nothing new anywhere. The diagnostics zip never includes `scribe.db`, so it is unaffected.

## Logging

Shapes only, never text, a pattern, a replacement or a history id:

- `Recorded a correction ({Source}, {Kind}).` at Debug.
- `Correction not recorded: {Reason}.` at Debug, with `NoHistoryRow`, `RowChanged`, `HistoryWriteStillPending`,
  `TableUnavailable` or `ShuttingDown`, and at Warning with `FailureShape.Describe` for a failed write.
- Learn from history's existing lines gain the count of live corrections it used.

Add `CorrectionRecorder.cs` and the new repository file to `LogPrivacyGuardTests`' scan, since `Persistence` is not
scanned today.

## macOS

The port has its own store and its own copies of quick add, the history writer and the learner
(`macos/Scribe/Sources/Scribe/QuickDictionaryAdd.swift`, `HistoryWriter.swift`, `PersistenceStore.swift`,
`DictionaryHistoryLearner.swift`). The same table, index and trigger work in its `scribe.db`. Until it is ported, the
`macos/PORTING-PLAN.md` rows for tray quick add, history-mined suggestions and the History list are stale, and the PR
says so.

## Phases

| Phase | Contents | Schema | Disclosure |
| --- | --- | --- | --- |
| 0 | Drop an AI suggestion whose written form matches an existing rule's spoken form ignoring case but is not, exactly, that rule's written form (`ParseSuggestions`, [AiDictionarySuggester.cs:253](../src/Scribe.Core/PostProcessing/AiDictionarySuggester.cs), keys only on the spoken form today). Rule "MCB" to "MCP" drops a suggestion writing "MCB"; rule "github" to "GitHub" keeps one writing "GitHub" and drops one writing "Github". Show the unused "corrected to match" notice | None | None |
| 1 | Table, slot to row link, recording from quick add, the reads above, the local filter against corrections | Additive | PRIVACY.md's history list |
| 2 | Learning from pairs: offline sound-alike suggestions in Settings, pairs in the AI request | None | Suggestion consent, PRIVACY.md, tests |
| 3 | "Fix a word" on History rows, "Add it in Settings" carrying the dictation | None | None |

## Tests

Core, all with no UI:

- `HistoryCorrectionStoreTests`: a correction is recorded for a row whose text matches; nothing is recorded for a
  missing row or a row whose text differs; the newest correction's text is `CorrectedText`; chained corrections read
  back in order; `Delete`, `Clear`, `PruneOlderThan` and `DeleteEntriesOlderThan` each remove the row's corrections;
  a plain `DELETE FROM history` on a connection with `foreign_keys` off removes them (an older build); a database
  without the table reads and writes history as before; the table, index and trigger appear on open with
  `user_version` still 7.
- `SecureDeleteTests`: a deleted correction's bytes are gone from the file after the checkpoint.
- `DatabaseSalvageTests`: a repair carries corrections across and the rebuilt file has the trigger.
- `HistoryWriterTests`: the committed callback runs once after commit, never for a dropped, abandoned or failed write,
  and a callback that throws does not stop the next write.
- `LastTranscriptStoreTests`: a slot learns its id; a replaced slot ignores a late id; seeding keeps ids, shows the
  corrected text and still matches deletions by the text as typed.
- `CorrectionRecorderTests` (manual clock, scripted writer): records after the barrier; records nothing with no id,
  after a timeout, or once shut down; keeps save order; never throws.
- `DictionaryHistoryLearnerTests` and `AiDictionarySuggesterTests`: a corrected mishearing is never suggested back,
  and a suggestion that writes one is dropped.
- `UsageEquivalenceCorpus` and `DictionaryUsageAnalyzerTests` over corpora with corrected rows.
- `LogPrivacyGuardTests` covering the new files; `CleanupDisclosureTests` in phase 2.

The App changes (quick add, History list, seeding) are thin over these; the History list's "Corrected" marker gets a
render at 100% and 225% text size like other Settings rows.

## Risks and open questions

- **The link is best effort.** A correction made before the dictation's history write was even queued (quick add
  opened during a long insertion) finds no id after the barrier and is not recorded. That is rare and logged.
- **Text matching guards the insert.** Two rows with the same text are told apart by id, not text, so the guard
  only refuses a mismatch; it never picks the wrong row.
- **Where a mistake came from is unknown.** Scribe keeps neither the recognizer's raw output nor the text before AI
  cleanup, so a correction cannot say whether recognition, AI cleanup or an existing rule produced the mistake. That
  context would sharpen phase 2, and getting it means storing raw text on history rows, which is a separate decision
  with its own disclosure and disk cost.
- **Clear's speed** with a trigger on `history` needs the measurement above before phase 1 ships.
