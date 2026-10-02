# Settings session contract

This is a module-internal API for the macOS Settings workstreams. No page owns a store or a serving configuration.

## Pages

`SettingsSessionPage` supplies stable raw IDs and the eleven Windows titles. Presentation/navigation types map through
the raw ID rather than defining another `SettingsSession` or modifying this enum per page.

## Editing and close

Construct `SettingsSession(initial:access:)`. Read `draft`, `baseline`, `dirtyPages`, `footerText`, `isSaving` and `lastSave`.
Only `edit { document in ... }` changes persisted fields. Nil row collections mean not loaded; install their initial read
with `adoptLoadedRows(from:)`. New rows use `temporaryRowID()`; existing rows keep their SQLite IDs. Stage a credential
through `editCredential(id, edit)`, never in preferences or a serialized row.

`save()` writes the whole submission. `saveAndClose()` permits closing only after successful application with no newer
edits. `cancel()` discards the draft unless a write or an uncertain commit is being settled. `closeDecision(trigger)`
and `SettingsCloseCoordinator` guard close, quit and restart; their proceed callback starts the real lifecycle shutdown.
Keep editing starts no shutdown. An idle Escape does not close the window.

`SettingsSaveResult` distinguishes:

- `notCommitted`: validation, preparation or a definitive storage failure; edits remain.
- `outcomeUnknown`: a lost commit reply; retain prepared credentials and recover the durable receipt before retrying.
- `committed`: storage succeeded, with an independent applied/notApplied result and newer-edit state.

An adapter must throw `SettingsCommitUncertain` for an unknown outcome, not an ordinary storage error. A persisted but
unapplied submission retries application without another write.

## Storage and participants

`SettingsLegacyStore.load()` reads the existing stores without modifying defaults or credentials. After the first Save,
`settings.document.v1` in the existing SQLite settings table is the authoritative committed preferences record. The
legacy defaults and fixed Keychain accounts remain a safe pre-redesign rollback profile; they are not an authority to
which an arbitrary live page may write. The final shell cutover must route runtime and tray readers through this API.

`PersistenceStore.commitSettingsSession(submission)` commits preferences, changed loaded rows, history retention,
credential references and word-pack attachment values in one transaction. It checks expected values and row baselines,
retains external additions, and preserves durable idempotent receipts. Unloaded rows are never replaced with empty lists.

WS4 prepares images before commit and returns a `SettingsCommitAttachment`:

```swift
var attachment = SettingsCommitAttachment()
attachment.expectedValues["word_pack_state_v1"] = .some(previousState)
attachment.values["word_pack_state_v1"] = nextState
submission.attachment = attachment
```

To expect absence, use `.some(nil)`; assigning nil to the dictionary subscript removes the check. Do not attach a write
to `settings.document.v1` or `history_retention_days`: those belong to the session. `receipt.attachment` is available to
the post-commit installer. Install/publication failure returns notApplied, never an ordinary partial Save.

`SettingsSavePreparation` composes bounded Keychain preparation and the participant. Use
`SettingsLegacyStore.access(preparation:apply:)` to retire preparation ownership after commit and clean uncommitted
generated accounts. References select new UUID accounts; no serving/fixed account changes before commit. A late native
write is quarantined and its generated account discarded, not awaited on the UI or during shutdown.

`SettingsReferencedSecretStore` is read-only. Hand its committed reference image to the serving or candidate provider
factory. It distinguishes a staged removal tombstone from an absent migration entry, which falls back to the legacy
account. Runtime consumers must use the committed preferences and referenced stores together.

## Outside writes and explicit commands

Allocate `SettingsIntentRevision.next()` at the user action, not completion. Call
`SettingsLegacyStore.updateExternal(setting:values:revision:)` and publish its stored result to the session with
`adoptExternal`. A request and its completion carry the same revision. Separate settings have independent intent slots.
Load the store before accepting intents so its persisted revision floor seeds the clock.

`SettingsExternalChoice` is the pure Windows intent port. Changes deferred during capture still participate in Save.
No-intent Save preserves the current stored outside value; a newer committed intent supersedes an older queued write.

`SettingsCommandPolicy.dispositions` is the sole table of immediate commands versus staged edits. Usage Add is immediate
and reapplies storage through `SettingsStoredReapply`, never the editing document. Permanent word-pack deletion,
credential removal, restore defaults and accepted learned words remain staged.

## Effective cleanup and native commands

`SettingsEffectiveCleanup.resolve(snapshot:environment:purpose:)` preserves legacy environment precedence. Serving uses
active environment overrides; a candidate test uses the shown draft and receives an explanation of ignored overrides.
The result carries setting names, never an environment key's secret value. WS3 owns the actual independent candidate
provider construction and send gate.

Retain `SettingsMainMenu` in the app owner, then install its menu. Standard Edit items target the responder chain.
Save/Close target supplied guarded commands only while Settings is key and capture/composition does not own input.
Quit calls the supplied close coordinator, not termination directly. The final shell, not this foundation, supplies the
key-window predicate, prompt presentation and ApplicationTermination continuation.

## Boundary

These components intentionally do not replace the old page UI, wire a second runtime authority into the old app delegate,
or implement WS3's outbound permission gate or WS4's file installer. The cutover must be coherent: no staged footer over
old direct writers, no prepared candidate in the serving cache, and no automatic reset of existing valid shortcut keys.
