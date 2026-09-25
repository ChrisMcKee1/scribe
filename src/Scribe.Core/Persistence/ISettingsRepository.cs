using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>Loads and persists the user's <see cref="AppSettings"/> plus arbitrary scalar keys.</summary>
public interface ISettingsRepository
{
    /// <summary>
    /// True when the settings in use are defaults standing in for the user's saved ones: the most recent read found
    /// the stored document unreadable, or found none because a repair of a damaged database lost it, at this start
    /// (<see cref="ScribeDatabase.SettingsLostInRepair"/>) or an earlier one. False again once a readable document
    /// is saved.
    /// </summary>
    bool LastLoadFailed { get; }

    /// <summary>Returns the persisted settings, or freshly-defaulted settings when none exist.</summary>
    AppSettings Load();

    /// <summary>
    /// Persists the full settings document. Once library state is stored (<see cref="Libraries.LibrarySettingKeys.State"/>),
    /// the document's <see cref="AppSettings.EnabledDictionaryLibraryIds"/> keeps the list stored, whatever
    /// <paramref name="settings"/> holds: that list is written only together with the library state row (review
    /// finding A17), so a window opened before an adoption patched it cannot put the old list back.
    /// </summary>
    void Save(AppSettings settings);

    /// <summary>
    /// Loads the settings document, applies <paramref name="mutate"/> and saves it as one atomic step,
    /// returning what was saved. For any writer that changes a few fields of the stored document from
    /// a thread of its own: two concurrent calls never lose each other's change, because the read
    /// and the write share one in-process lock and one SQLite write transaction (BEGIN IMMEDIATE).
    /// With no document stored it starts from the first-run defaults. A document that cannot be read
    /// is never written over: it is kept as a recovery copy, as <see cref="Load"/> keeps it, and the
    /// call throws <see cref="InvalidOperationException"/> without saving the change. A document a
    /// repair lost is refused the same way, until a whole document is saved with <see cref="Save"/> or
    /// <see cref="SaveBundle"/>. Never queues behind storage maintenance. <paramref name="mutate"/> must
    /// be quick and must not call back into the repository; if it throws, nothing is saved and the
    /// exception propagates. Otherwise it fails only the way <see cref="Save"/> can. Like <see cref="Save"/>, it keeps
    /// the stored enabled-library list once library state is stored.
    /// </summary>
    AppSettings Update(Action<AppSettings> mutate);

    /// <summary>
    /// <see cref="Update(Action{AppSettings})"/> for a change asked for outside the settings window, the tray's AI
    /// cleanup switch, with the revision it took when the user asked for it
    /// (<see cref="Scribe.Core.Settings.ExternalSwitchSync.NextRevision"/>). When a whole-document save whose intent
    /// for the switch is at least as new (<see cref="SaveBundle"/>) has already committed, the change is superseded:
    /// nothing is written, and the settings as stored come back with <paramref name="superseded"/> set. Otherwise it
    /// is exactly <see cref="Update(Action{AppSettings})"/>. Every settings write in this process runs whole under one
    /// lock, and the check is made inside the transaction the change would be written in, so no save can land between
    /// the check and the write.
    /// </summary>
    AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded);

    /// <summary>
    /// <see cref="Update(Action{AppSettings}, long, out bool)"/> for any setting the tray can change, checked against that
    /// setting's own record of intent: a committed whole-document save (<see cref="SaveBundle"/> with
    /// <see cref="ExternalIntents"/>) whose intent for <paramref name="setting"/> is at least <paramref name="revision"/>
    /// supersedes the change, and an intent for another setting never does. This default checks only the AI cleanup
    /// switch and treats any other setting as an unchecked <see cref="Update(Action{AppSettings})"/>;
    /// <see cref="SettingsRepository"/> checks every one.
    /// </summary>
    AppSettings Update(Action<AppSettings> mutate, ExternalSetting setting, long revision, out bool superseded)
    {
        if (setting == ExternalSetting.AiCleanup)
        {
            return Update(mutate, revision, out superseded);
        }

        superseded = false;
        return Update(mutate);
    }

    /// <summary>
    /// Atomically persists settings plus any changed dictionary and snippet collections.
    /// A null collection leaves that section untouched. <paramref name="aiCleanupIntent"/> is the revision of the
    /// window's newest intent for the AI cleanup switch: the user's click there, or a tray change the window took.
    /// Once the save commits, every tray change up to it is superseded
    /// (<see cref="Update(Action{AppSettings}, long, out bool)"/>). Zero means the window has no intent, so its switch
    /// may be older than what is stored: the stored value is kept, and <paramref name="settings"/> takes it once the
    /// save has succeeded, so the caller shows and applies what is stored. The microphone is written as given. Once
    /// library state is stored, the enabled-library list is kept as stored as well (and <paramref name="settings"/>
    /// takes it), because only a save with a library payload may change it.
    /// </summary>
    void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        long aiCleanupIntent = 0);

    /// <summary>
    /// The whole-document save above with the window's intent for every setting the tray can change. Each works as the AI
    /// cleanup switch does there: zero keeps that setting's stored value (and <paramref name="settings"/> takes it once the
    /// save has succeeded), and once the save commits every tray change to that setting up to its intent is superseded
    /// (<see cref="Update(Action{AppSettings}, ExternalSetting, long, out bool)"/>). This default passes on only the AI
    /// cleanup intent; <see cref="SettingsRepository"/> honors every one.
    /// </summary>
    void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        ExternalIntents intents) =>
        SaveBundle(settings, dictionaryEntries, snippets, intents.AiCleanup);

    /// <summary>
    /// The whole-document save above that also commits a library Save (<paramref name="libraries"/>, built only by
    /// <see cref="Libraries.ILibraryCatalogStore.PrepareSave"/>), in the same BEGIN IMMEDIATE transaction: the settings
    /// document, the dictionary and snippets, the library's auxiliary rows and its generation. When the stored
    /// <see cref="Libraries.LibrarySettingKeys.Generation"/> is not <see cref="Libraries.LibrarySavePayload.ExpectedGeneration"/>,
    /// nothing is written and <see cref="LibraryGenerationConflictException"/> is thrown. A payload whose
    /// <see cref="Libraries.LibrarySavePayload.EnabledLibraryIds"/> is set stores that list in the document, and
    /// <paramref name="settings"/> takes it once the save has succeeded. With a null payload this is exactly
    /// <see cref="SaveBundle(AppSettings, IReadOnlyList{DictionaryEntry}, IReadOnlyList{Snippet}, ExternalIntents)"/>.
    /// The caller completes the library Save (<see cref="Libraries.ILibraryCatalogStore.CompleteSave"/>) whether this
    /// returned or threw. This default serves only test fakes: a null payload is passed on, and any other throws
    /// <see cref="NotSupportedException"/>; <see cref="SettingsRepository"/> honors both.
    /// </summary>
    void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        ExternalIntents intents,
        Libraries.LibrarySavePayload? libraries)
    {
        if (libraries is not null)
        {
            throw new NotSupportedException("This settings store cannot commit library state.");
        }

        SaveBundle(settings, dictionaryEntries, snippets, intents);
    }

    /// <summary>
    /// Commits library state the library service records without a Save (an adoption, a lost state's denial, the
    /// <c>Import</c> and <c>Remove</c> wrappers): the generation check of <see cref="SaveBundle(AppSettings, IReadOnlyList{DictionaryEntry}, IReadOnlyList{Snippet}, ExternalIntents, Libraries.LibrarySavePayload)"/>,
    /// then the library's auxiliary rows and its generation, and, when <see cref="Libraries.LibrarySavePayload.EnabledLibraryIds"/>
    /// is set, the stored document's enabled-library field and nothing else of the document, patched in place. A
    /// document that is not stored, cannot be read or that a repair lost is never patched: the call throws
    /// <see cref="InvalidOperationException"/> and writes nothing. This default serves only test fakes, and throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    void CommitLibraryState(Libraries.LibrarySavePayload payload) =>
        throw new NotSupportedException("This settings store cannot commit library state.");

    /// <summary>
    /// What the library service reads from the settings store before a load, all of one committed generation (round 2,
    /// A6): the document's enabled list (null when the document cannot be used), whether it can be used, and the library
    /// rows. A commit landing between these reads must never leave the old list beside the new state row, which reads as
    /// an older build's change. <see cref="SettingsRepository"/> reads them in one SQLite read transaction; this default,
    /// for test fakes, reads the generation before and after and reads again until the two agree, which every library
    /// commit makes observable because each one advances the generation.
    /// </summary>
    internal LibrarySettingsRead ReadLibrarySettings()
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = Get(Libraries.LibrarySettingKeys.Generation);
            var document = Load();
            var unusable = LastLoadFailed;
            var state = Get(Libraries.LibrarySettingKeys.State);
            var fileIds = Get(Libraries.LibrarySettingKeys.FileIds);
            var generation = Get(Libraries.LibrarySettingKeys.Generation);
            if (string.Equals(before, generation, StringComparison.Ordinal) || attempt == 16)
            {
                return new LibrarySettingsRead(
                    unusable ? null : [.. document.EnabledDictionaryLibraryIds], unusable, generation, state, fileIds);
            }
        }
    }

    /// <summary>Reads a single raw value by key, or <see langword="null"/> when absent.</summary>
    string? Get(string key);

    /// <summary>Inserts or updates a single raw value by key.</summary>
    void Set(string key, string value);
}

/// <summary>The settings a library load reads, all of one committed generation (<see cref="ISettingsRepository.ReadLibrarySettings"/>).</summary>
/// <param name="DocumentEnabledIds">The stored document's enabled-library list, or null when the document cannot be used.</param>
/// <param name="DocumentUnusable">The document is missing after a loss, unreadable, or the session runs on defaults.</param>
/// <param name="GenerationRow">The raw <see cref="Libraries.LibrarySettingKeys.Generation"/> row, or null when absent.</param>
/// <param name="StateRow">The raw <see cref="Libraries.LibrarySettingKeys.State"/> row, or null.</param>
/// <param name="FileIdsRow">The raw <see cref="Libraries.LibrarySettingKeys.FileIds"/> row, or null.</param>
internal sealed record LibrarySettingsRead(
    IReadOnlyList<string>? DocumentEnabledIds,
    bool DocumentUnusable,
    string? GenerationRow,
    string? StateRow,
    string? FileIdsRow);
