using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="ISettingsRepository"/>
public sealed class SettingsRepository : ISettingsRepository
{
    /// <summary>The settings row holding the user's document; every other row is auxiliary.</summary>
    internal const string SettingsKey = "app_settings";

    /// <summary>
    /// Present while a document a repair lost has not been replaced (<see cref="ScribeDatabase.SettingsLostInRepair"/>
    /// writes it). Without it, the start after the repair would read the missing document as a first run and
    /// quietly make the defaults the user's settings. Older builds ignore it.
    /// </summary>
    internal const string LostMarkerKey = "app_settings_lost";

    private const string RecoveryKey = "app_settings_recovery";

    // The document member holding AppSettings.EnabledDictionaryLibraryIds, named as the serializer's camelCase policy
    // writes it; the serializer reads it without case, and so does the patch that splices a new list into it.
    private const string EnabledLibraryListMember = "enabledDictionaryLibraryIds";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly ScribeDatabase _database;

    // Every write of the settings document (Save, SaveBundle and both kinds of Update) runs whole under this one lock:
    // BEGIN, the reads it decides by, the write, COMMIT, and the bookkeeping after it. Within this process that fixes
    // the order writes happen in, whatever SQLite does with its own lock (after some failures it rolls back and
    // releases that lock at once), and it keeps two load-edit-save writers on different threads from overwriting each
    // other's field. The IMMEDIATE transactions are what exclude every other connection.
    private readonly Lock _updateLock = new();
    private int _updatingThread;

    // The settings the tray can change follow one invariant each: the newest intent wins, ordered by when the user made
    // it, from the tray or in the Settings window, and a whole-document save never writes over a stored value its window
    // neither showed nor changed. So a save carries the revision of its window's newest intent for each of them (a change
    // there, or a tray change the window took; see ExternalChoiceSync) and, carrying none for one, keeps that stored
    // value. A tray change is superseded by a committed save whose intent for the same setting is at least as new; an
    // intent for another setting never supersedes it. These are the newest intents committed saves carried. They change
    // only after that commit has succeeded, under the write lock, so no writer can ever act on a save that did not happen.
    private long _aiCleanupSavedThrough;
    private long _microphoneSavedThrough;

    public SettingsRepository(ScribeDatabase database) => _database = database;

    /// <summary>
    /// Test seam: runs where a whole-document save and a checked change meet, with the step's connection and
    /// transaction, on the writing thread and under the write lock. "update began": a checked
    /// <see cref="Update(Action{AppSettings}, long, out bool)"/> has begun its transaction and has not yet looked at
    /// what saves carried. "save committing": <see cref="SaveBundle"/> has written and not committed. "save
    /// committed": that commit has returned (no transaction) and the save has not yet recorded its intent. "library rows
    /// written": a library commit (a <see cref="SaveBundle"/> with a payload, or <see cref="CommitLibraryState"/>) has
    /// written its auxiliary rows and generation and not yet the document. "library state committing" and "library state
    /// committed": <see cref="CommitLibraryState"/>'s pair, like the save's. Unset in the app.
    /// </summary>
    internal Action<string, SqliteConnection, SqliteTransaction?>? WriteStep { get; set; }

    /// <summary>
    /// Test seam: runs on a writing thread just before it asks for the write lock, so a test knows that a writer has
    /// reached the lock instead of guessing from a timer. Unset in the app.
    /// </summary>
    internal Action? WriteLockRequested { get; set; }

    /// <summary>Whether the calling thread holds the write lock, for tests proving a step runs under it.</summary>
    internal bool WriteLockHeldByCurrentThread => _updateLock.IsHeldByCurrentThread;

    public bool LastLoadFailed { get; private set; }

    /// <summary>
    /// Whether this session starts on defaults the user never chose: the stored settings could not be read, or a
    /// repair of a damaged database lost them, at this start or an earlier one whose loss nothing has replaced yet.
    /// Reads the stored document, so ask before anything writes it. Never throws; a read that fails answers true,
    /// the answer that makes no decision on the user's behalf. Such a read leaves <see cref="LastLoadFailed"/> as it
    /// was, so a caller deciding by this answer (whether startup may save the whole document) must keep the answer
    /// rather than consult the flag again.
    /// </summary>
    public static bool StartsWithoutSavedSettings(ISettingsRepository settings, ScribeDatabase database)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(database);

        try
        {
            settings.Load();
            return settings.LastLoadFailed || database.SettingsLostInRepair;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>True when <paramref name="json"/> is a settings document <see cref="Load"/> can read.</summary>
    internal static bool IsReadableDocument(string? json)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(json) && TryDeserialize(json) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public AppSettings Load()
    {
        var (stored, json) = ReadDocument();
        if (!stored)
        {
            // No document is a first run, unless a repair lost the one the user had. Those defaults are no more
            // their choice than an unreadable document's, so they are reported the same way until one is saved, and
            // they are an existing install's: a first run's hotkeys would move a key this person relies on.
            LastLoadFailed = _database.SettingsLostInRepair || Get(LostMarkerKey) is not null;
            return LastLoadFailed ? AppSettings.CreateForExistingInstall() : AppSettings.CreateDefault();
        }

        if (!string.IsNullOrWhiteSpace(json) && TryDeserialize(json) is { } settings)
        {
            LastLoadFailed = false;
            return settings;
        }

        // Unreadable, blank or white space included: a stored row, however empty, means this is no first run.
        LastLoadFailed = true;
        PreserveRecoveryCopy(json);
        return AppSettings.CreateForExistingInstall();
    }

    /// <summary>For tests: raised inside the read transaction of the library settings read, after the document is read.</summary>
    internal Action<string>? ReadStep { get; set; }

    // One SQLite read transaction: whatever commits meanwhile, every value comes from one committed generation (round 2,
    // A6). Load's rules for whether the document can be used, without its side effects: the recovery copy is Load's to
    // write, and LastLoadFailed stays the session's.
    LibrarySettingsRead ISettingsRepository.ReadLibrarySettings()
    {
        bool stored;
        string json;
        bool lost;
        string? generation;
        string? state;
        string? fileIds;
        using (var connection = _database.Open())
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            (stored, json) = ReadDocument(connection, transaction);
            ReadStep?.Invoke("document read");
            lost = ReadValue(connection, transaction, LostMarkerKey) is not null;
            generation = ReadValue(connection, transaction, LibrarySettingKeys.Generation);
            state = ReadValue(connection, transaction, LibrarySettingKeys.State);
            fileIds = ReadValue(connection, transaction, LibrarySettingKeys.FileIds);
            transaction.Commit();
        }

        if (!stored)
        {
            var unusable = _database.SettingsLostInRepair || lost;
            return new LibrarySettingsRead(
                unusable ? null : [.. AppSettings.CreateDefault().EnabledDictionaryLibraryIds], unusable, generation, state, fileIds);
        }

        var document = string.IsNullOrWhiteSpace(json) ? null : TryDeserialize(json);
        return new LibrarySettingsRead(
            document is null ? null : [.. document.EnabledDictionaryLibraryIds], document is null, generation, state, fileIds);
    }

    public AppSettings Update(Action<AppSettings> mutate) => UpdateCore(mutate, setting: null, revision: null, out _);

    public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) =>
        UpdateCore(mutate, ExternalSetting.AiCleanup, revision, out superseded);

    public AppSettings Update(Action<AppSettings> mutate, ExternalSetting setting, long revision, out bool superseded) =>
        UpdateCore(mutate, setting, revision, out superseded);

    private AppSettings UpdateCore(Action<AppSettings> mutate, ExternalSetting? setting, long? revision, out bool superseded)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var (stored, wasSuperseded) = Serialized(() =>
        {
            // Not behind the write gate, like SaveBundle: a caller can be on the UI thread (the
            // first-run welcome's flag is), so it asks a running VACUUM to yield instead of
            // queueing behind it.
            _database.RequestYield();
            using var connection = _database.Open();

            // Non-deferred is BEGIN IMMEDIATE in Microsoft.Data.Sqlite: the read and the write
            // below are one SQLite write transaction, so no other connection or process can
            // commit between them either.
            using var transaction = connection.BeginTransaction(deferred: false);
            WriteStep?.Invoke("update began", connection, transaction);

            // Decided inside the transaction the change would be written in. Every save that could supersede it has
            // already committed and recorded its intent, or has not begun: saves take the write lock this change holds.
            if (revision is { } asked && setting is { } which && asked <= SavedThrough(which))
            {
                return (ReadForUpdate(connection, transaction).Settings, true);
            }

            var (settings, documentStored) = ReadForUpdate(connection, transaction);
            var keptLibraries = documentStored && LibraryStateStored(connection, transaction)
                ? settings.EnabledDictionaryLibraryIds.ToList()
                : null;
            mutate(settings);
            if (keptLibraries is not null)
            {
                settings.EnabledDictionaryLibraryIds = keptLibraries;
            }

            WriteValue(connection, transaction, SettingsKey, JsonSerializer.Serialize(settings, JsonOptions));
            ForgetLoss(connection, transaction);
            transaction.Commit();
            LastLoadFailed = false;
            return (settings, false);
        });

        superseded = wasSuperseded;
        return stored;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Not behind the write gate, like SaveBundle. The document and the end of any recorded loss are one commit.
        Serialized(() =>
        {
            _database.RequestYield();
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            var document = settings;
            if (StoredLibraryList(connection, transaction) is { } kept)
            {
                document = settings.Clone();
                document.EnabledDictionaryLibraryIds = kept;
            }

            WriteValue(connection, transaction, SettingsKey, JsonSerializer.Serialize(document, JsonOptions));
            ForgetLoss(connection, transaction);
            transaction.Commit();

            // The stored document is the one just written, which is readable, whatever the last load found.
            LastLoadFailed = false;
        });
    }

    public void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        long aiCleanupIntent = 0) =>
        SaveBundleCore(settings, dictionaryEntries, snippets, aiCleanupIntent, microphoneIntent: null, libraries: null);

    public void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        ExternalIntents intents) =>
        SaveBundleCore(settings, dictionaryEntries, snippets, intents.AiCleanup, intents.Microphone, libraries: null);

    public void SaveBundle(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        ExternalIntents intents,
        LibrarySavePayload? libraries) =>
        SaveBundleCore(settings, dictionaryEntries, snippets, intents.AiCleanup, intents.Microphone, libraries);

    // A null microphone intent is a caller that does not track one, which writes the microphone as given and records
    // nothing, exactly as every save did before the tray could change the microphone.
    private void SaveBundleCore(
        AppSettings settings,
        IReadOnlyList<DictionaryEntry>? dictionaryEntries,
        IReadOnlyList<Snippet>? snippets,
        long aiCleanupIntent,
        long? microphoneIntent,
        LibrarySavePayload? libraries)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Serialized(() =>
        {
            // Not behind the write gate: Settings save runs on the UI thread and must never queue behind
            // storage maintenance. Asking a running VACUUM to yield keeps SQLite's busy wait short.
            _database.RequestYield();
            using var connection = _database.Open();

            // IMMEDIATE, so the stored values read below cannot change before this transaction writes.
            using var transaction = connection.BeginTransaction(deferred: false);

            // A library commit is refused before anything is written when another commit moved the generation since
            // it was prepared: disposing the transaction unwritten rolls it back.
            if (libraries is not null)
            {
                RequireGeneration(connection, transaction, libraries);
            }

            // Without an intent for a setting, the window neither changed it nor took a tray change, so what it shows can
            // be older than what is stored: a tray change that committed before word of it reached the window. The stored
            // value stays. With no readable document there is nothing to keep, and the window's value is saved.
            var keepAiCleanup = aiCleanupIntent <= 0;
            var keepMicrophone = microphoneIntent is <= 0;

            // The document's enabled-library list is the library state's projection, and once that state is stored the
            // list is written only with it (review finding A17): a save that carries no list keeps the stored one.
            var keepLibraries = libraries?.EnabledLibraryIds is null && LibraryStateStored(connection, transaction);
            var document = settings;
            bool? keptAiCleanup = null;
            (string? Id, string? Name)? keptMicrophone = null;
            List<string>? keptLibraries = null;
            if ((keepAiCleanup || keepMicrophone || keepLibraries) &&
                ReadValue(connection, transaction, SettingsKey) is { } storedJson &&
                TryDeserialize(storedJson) is { } stored)
            {
                if (keepAiCleanup && stored.EnableAiCleanup != settings.EnableAiCleanup)
                {
                    keptAiCleanup = stored.EnableAiCleanup;
                }

                if (keepMicrophone && !SameMicrophone(stored, settings))
                {
                    keptMicrophone = (stored.InputDeviceId, stored.InputDeviceName);
                }

                if (keepLibraries)
                {
                    keptLibraries = [.. stored.EnabledDictionaryLibraryIds];
                }
            }

            var libraryList = libraries?.EnabledLibraryIds is { } list ? list.ToList() : keptLibraries;
            if (keptAiCleanup is not null || keptMicrophone is not null || libraryList is not null)
            {
                document = settings.Clone();
                Keep(document, keptAiCleanup, keptMicrophone);
                if (libraryList is not null)
                {
                    document.EnabledDictionaryLibraryIds = [.. libraryList];
                }
            }

            if (dictionaryEntries is not null)
            {
                DictionaryRepository.SaveAll(connection, transaction, dictionaryEntries);
            }

            if (snippets is not null)
            {
                SnippetRepository.SaveAll(connection, transaction, snippets);
            }

            if (libraries is not null)
            {
                WriteLibraryRows(connection, transaction, libraries);
                WriteStep?.Invoke("library rows written", connection, transaction);
            }

            WriteValue(connection, transaction, SettingsKey, JsonSerializer.Serialize(document, JsonOptions));
            ForgetLoss(connection, transaction);
            WriteStep?.Invoke("save committing", connection, transaction);
            transaction.Commit();
            WriteStep?.Invoke("save committed", connection, null);

            // Only now that the commit has succeeded, and still under the write lock: no writer can ever see an intent
            // carried by a save that failed, however SQLite rolled it back. The caller's document takes the values kept,
            // so what it shows and applies is what is stored.
            RecordSavedThrough(ExternalSetting.AiCleanup, aiCleanupIntent);
            if (microphoneIntent is { } microphone)
            {
                RecordSavedThrough(ExternalSetting.Microphone, microphone);
            }

            Keep(settings, keptAiCleanup, keptMicrophone);
            if (libraryList is not null)
            {
                settings.EnabledDictionaryLibraryIds = [.. libraryList];
            }

            LastLoadFailed = false;
        });
    }

    public void CommitLibraryState(LibrarySavePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Serialized(() =>
        {
            // Not behind the write gate, like every settings write; adoption runs off the dispatcher but must not queue
            // behind a VACUUM either.
            _database.RequestYield();
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            RequireGeneration(connection, transaction, payload);

            // Every refusal happens here, before anything is written, so a refused commit leaves the database as it was.
            string? patched = null;
            if (payload.EnabledLibraryIds is { } list)
            {
                var (stored, json) = ReadDocument(connection, transaction);
                if (!stored)
                {
                    throw new InvalidOperationException(
                        _database.SettingsLostInRepair || ReadValue(connection, transaction, LostMarkerKey) is not null
                            ? "The saved settings were lost when the database was repaired, so the library list was not changed."
                            : "No settings are saved yet, so the library list was not changed.");
                }

                if (ReadValue(connection, transaction, LostMarkerKey) is not null ||
                    string.IsNullOrWhiteSpace(json) || TryDeserialize(json) is null)
                {
                    throw new InvalidOperationException("The stored settings could not be read, so the library list was not changed.");
                }

                patched = PatchEnabledLibraryList(json, list) ??
                    throw new InvalidOperationException("The stored settings have no single library list to change, so nothing was changed.");
            }

            WriteLibraryRows(connection, transaction, payload);
            WriteStep?.Invoke("library rows written", connection, transaction);
            if (patched is not null)
            {
                WriteValue(connection, transaction, SettingsKey, patched);
            }

            WriteStep?.Invoke("library state committing", connection, transaction);
            transaction.Commit();
            WriteStep?.Invoke("library state committed", connection, null);
        });
    }

    /// <summary>
    /// The stored library generation: <see cref="LibrarySettingKeys.Generation"/> as a canonical decimal integer of at
    /// least 1, or 0 when the row is absent or holds anything else (which the library service reads as lost).
    /// </summary>
    internal static long ParseLibraryGeneration(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation >= 1 &&
        string.Equals(value, generation.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? generation
            : 0;

    private static void RequireGeneration(SqliteConnection connection, SqliteTransaction transaction, LibrarySavePayload payload)
    {
        var stored = ParseLibraryGeneration(ReadValue(connection, transaction, LibrarySettingKeys.Generation));
        if (stored != payload.ExpectedGeneration)
        {
            throw new LibraryGenerationConflictException(stored, payload.ExpectedGeneration);
        }
    }

    private static void WriteLibraryRows(SqliteConnection connection, SqliteTransaction transaction, LibrarySavePayload payload)
    {
        foreach (var row in payload.Values)
        {
            if (row.Value is null)
            {
                DeleteValue(connection, transaction, row.Key);
            }
            else
            {
                WriteValue(connection, transaction, row.Key, row.Value);
            }
        }

        WriteValue(
            connection, transaction, LibrarySettingKeys.Generation, payload.Generation.ToString(CultureInfo.InvariantCulture));
    }

    private static bool LibraryStateStored(SqliteConnection connection, SqliteTransaction transaction) =>
        ReadValue(connection, transaction, LibrarySettingKeys.State) is not null;

    // The stored document's enabled-library list, when library state is stored and the document can be read: the list a
    // whole-document write without a library payload keeps (review finding A17). Null when there is nothing to keep.
    private static List<string>? StoredLibraryList(SqliteConnection connection, SqliteTransaction transaction) =>
        LibraryStateStored(connection, transaction) &&
        ReadValue(connection, transaction, SettingsKey) is { } json && !string.IsNullOrWhiteSpace(json) &&
        TryDeserialize(json) is { } stored
            ? [.. stored.EnabledDictionaryLibraryIds]
            : null;

    /// <summary>
    /// The stored document with only the value of its top-level <c>enabledDictionaryLibraryIds</c> member replaced by
    /// <paramref name="ids"/>, or the member appended before the closing brace when it is absent; null when the document
    /// is not a JSON object or holds the member more than once (compared without case, as the serializer reads it).
    /// </summary>
    /// <remarks>
    /// A splice of bytes, never a typed round trip: deserializing into <see cref="AppSettings"/> and serializing again
    /// would give missing members their defaults, drop members this build does not know and re-escape strings, so
    /// every byte outside that one value stays exactly as stored (review findings A5 and its round 3 note).
    /// </remarks>
    internal static string? PatchEnabledLibraryList(string json, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(ids);
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            var found = 0;
            var valueStart = -1L;
            var valueEnd = -1L;
            var closingBrace = -1L;
            var members = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    closingBrace = reader.TokenStartIndex;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return null;
                }

                members++;
                var matches = string.Equals(reader.GetString(), EnabledLibraryListMember, StringComparison.OrdinalIgnoreCase);
                if (!reader.Read())
                {
                    return null;
                }

                var start = reader.TokenStartIndex;
                reader.Skip();
                if (matches)
                {
                    found++;
                    valueStart = start;
                    valueEnd = reader.BytesConsumed;
                }
            }

            // Nothing may follow the object but white space; Read throws on anything else.
            if (closingBrace < 0 || found > 1 || reader.Read())
            {
                return null;
            }

            var array = JsonSerializer.SerializeToUtf8Bytes(ids.ToList(), JsonOptions);
            byte[] patched;
            if (found == 1)
            {
                patched = [.. bytes.AsSpan(0, (int)valueStart), .. array, .. bytes.AsSpan((int)valueEnd)];
            }
            else
            {
                var member = Encoding.UTF8.GetBytes((members > 0 ? "," : string.Empty) + "\"" + EnabledLibraryListMember + "\":");
                patched = [.. bytes.AsSpan(0, (int)closingBrace), .. member, .. array, .. bytes.AsSpan((int)closingBrace)];
            }

            return Encoding.UTF8.GetString(patched);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the AI cleanup intent a committed save carried, keeping the newest. Tests call it to stand for such a save.
    /// Only on a thread that holds the write lock, as inside a <see cref="WriteStep"/> callback.
    /// </summary>
    internal void RecordSavedThrough(long revision) => RecordSavedThrough(ExternalSetting.AiCleanup, revision);

    /// <summary>
    /// Records the intent a committed save carried for one setting, keeping the newest. <see cref="SaveBundleCore"/>
    /// calls it once its commit has succeeded; tests call it to stand for such a save. Only on a thread that holds the
    /// write lock, as inside a <see cref="WriteStep"/> callback.
    /// </summary>
    internal void RecordSavedThrough(ExternalSetting setting, long revision)
    {
        switch (setting)
        {
            case ExternalSetting.AiCleanup when revision > _aiCleanupSavedThrough:
                _aiCleanupSavedThrough = revision;
                break;
            case ExternalSetting.Microphone when revision > _microphoneSavedThrough:
                _microphoneSavedThrough = revision;
                break;
        }
    }

    // Caller holds the write lock.
    private long SavedThrough(ExternalSetting setting) => setting switch
    {
        ExternalSetting.AiCleanup => _aiCleanupSavedThrough,
        ExternalSetting.Microphone => _microphoneSavedThrough,
        _ => 0,
    };

    // The microphone is one choice: its endpoint ID and the name it had when chosen, the ID blank for the Windows default.
    private static bool SameMicrophone(AppSettings a, AppSettings b) =>
        string.Equals(NullIfBlank(a.InputDeviceId), NullIfBlank(b.InputDeviceId), StringComparison.Ordinal) &&
        (NullIfBlank(a.InputDeviceId) is null ||
         string.Equals(a.InputDeviceName, b.InputDeviceName, StringComparison.Ordinal));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void Keep(AppSettings target, bool? aiCleanup, (string? Id, string? Name)? microphone)
    {
        if (aiCleanup is { } enabled)
        {
            target.EnableAiCleanup = enabled;
        }

        if (microphone is { } kept)
        {
            target.InputDeviceId = kept.Id;
            target.InputDeviceName = kept.Name;
        }
    }

    private void Serialized(Action write) => Serialized(() =>
    {
        write();
        return 0;
    });

    // Runs one write of the settings document whole under the write lock (see _updateLock).
    private T Serialized<T>(Func<T> write)
    {
        // A nested write would sit in SQLite's busy handler behind its own open transaction until
        // the timeout; failing at once turns that hang into an obvious bug.
        if (Volatile.Read(ref _updatingThread) == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Settings Update cannot be called from inside its own mutate callback.");
        }

        WriteLockRequested?.Invoke();
        lock (_updateLock)
        {
            Volatile.Write(ref _updatingThread, Environment.CurrentManagedThreadId);
            try
            {
                return write();
            }
            finally
            {
                Volatile.Write(ref _updatingThread, 0);
            }
        }
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        // Not behind the write gate, like SaveBundle: the tray AI toggle writes here on the UI thread.
        _database.RequestYield();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void PreserveRecoveryCopy(string json)
    {
        try
        {
            if (TakesRecoverySlot(json, Get(RecoveryKey)))
            {
                Set(RecoveryKey, json);
            }
        }
        catch
        {
            // Recovery metadata must never turn a settings fallback into a startup failure.
        }
    }

    // The recovery copy keeps the first unreadable document that holds something. It is written once and never
    // overwritten, so a blank document, which holds nothing to recover, must not take the slot: it would shut out the
    // real document a later failure has to keep. For the same reason a blank copy, which only a hand edit or a
    // pre-release build could have written, gives way to the first document with content.
    private static bool TakesRecoverySlot(string json, string? existingCopy) =>
        !string.IsNullOrWhiteSpace(json) && string.IsNullOrWhiteSpace(existingCopy);

    // The settings document the way Load reads it: the same options (so the DPAPI-protected
    // secrets decrypt exactly as they do there) and the same normalization. Null when unreadable.
    private static AppSettings? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) is { } settings
                ? Normalize(settings)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Load's rules inside Update's transaction: defaults when nothing is stored. A document that cannot be read, a blank
    // one included, is never written over, because defaults plus one field would discard everything else the user
    // saved: it gets Load's recovery copy (kept for the first such document with content, never overwritten),
    // committed on its own, and the change is refused. The copy is written on this connection because a second one
    // would wait behind this very transaction. A document a repair lost is refused the same way: defaults plus one
    // field would stand in for it as if the user had chosen them.
    private (AppSettings Settings, bool DocumentStored) ReadForUpdate(SqliteConnection connection, SqliteTransaction transaction)
    {
        var (stored, json) = ReadDocument(connection, transaction);
        if (!stored)
        {
            if (_database.SettingsLostInRepair || ReadValue(connection, transaction, LostMarkerKey) is not null)
            {
                LastLoadFailed = true;
                throw new InvalidOperationException(
                    "The saved settings were lost when the database was repaired, so they were not changed.");
            }

            LastLoadFailed = false;
            return (AppSettings.CreateDefault(), false);
        }

        if (!string.IsNullOrWhiteSpace(json) && TryDeserialize(json) is { } settings)
        {
            LastLoadFailed = false;
            return (settings, true);
        }

        LastLoadFailed = true;
        if (TakesRecoverySlot(json, ReadValue(connection, transaction, RecoveryKey)))
        {
            WriteValue(connection, transaction, RecoveryKey, json);
        }

        transaction.Commit();
        throw new InvalidOperationException("The stored settings could not be read, so they were not changed.");
    }

    private static string? ReadValue(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private (bool Stored, string Json) ReadDocument()
    {
        using var connection = _database.Open();
        return ReadDocument(connection, transaction: null);
    }

    // Whether the document row exists, and its text. Only a missing row is a first run: an empty or white-space value
    // was written by something, so it is judged, and copied for recovery, like any other unreadable document. The cast
    // reads a value stored as another type (only a hand edit could store one) as the text it holds.
    private static (bool Stored, string Json) ReadDocument(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CAST(value AS TEXT) FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SettingsKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (false, string.Empty);
        }

        return (true, reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
    }

    // A readable document is being stored, so a loss a repair recorded is over.
    private static void ForgetLoss(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", LostMarkerKey);
        command.ExecuteNonQuery();
    }

    private static void WriteValue(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void DeleteValue(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        // A stored document is an existing install's, so a missing hotkey is the one it has been using.
        settings.Hotkey ??= HotkeyBinding.Legacy;
        settings.EnabledDictionaryLibraryIds ??= [];
        settings.Profiles ??= [];
        settings.Profiles = settings.Profiles
            .Where(profile => profile is not null)
            .Select(profile => new AppProfile
            {
                Name = profile.Name ?? string.Empty,
                ProcessNames = profile.ProcessNames ?? [],
                WritingStyle = profile.WritingStyle,
                NewlineHandling = profile.NewlineHandling,
            })
            .ToList();
        settings.AiCleanupModel ??= Cleanup.CleanupModelCatalog.DefaultAlias;
        settings.AiCleanupWritingStyle ??= string.Empty;
        settings.AiCleanupFrontierPrompt ??= string.Empty;
        settings.AiCleanupLocalPrompt ??= string.Empty;
        settings.TranscriptionModelId ??= Transcription.TranscriptionModelCatalog.DefaultId;
        settings.DecodeThreads = Math.Clamp(settings.DecodeThreads, 0, 16);
        return settings;
    }
}
