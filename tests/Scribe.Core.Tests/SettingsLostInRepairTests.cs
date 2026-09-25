using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests;

/// <summary>
/// Whether a session starts on the user's settings is decided by the settings document itself. A repair used to
/// count the rows it recovered from the settings table, so one that kept an auxiliary row (the damaged-copy ledger,
/// a migration flag) but lost the document reported the settings as recovered, a missing document read as a first
/// run, and maintenance applied the default 90-day retention to a user who had chosen to keep everything.
/// </summary>
public sealed class SettingsLostInRepairTests : IDisposable
{
    // The damaged-copy ledger: a settings row a repair can recover without the document.
    private const string AuxiliaryKey = DamagedCopyLedger.SettingsKey;

    // Ahead of the wall clock, as the maintenance policy tests do, so entries written now are already in the past.
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(2);
    private readonly TempDatabaseFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_repair_that_keeps_only_auxiliary_settings_rows_keeps_a_half_year_old_entry_the_user_chose_to_keep()
    {
        Seed(document: KeepEverything(), loseDocument: true);

        using var repaired = _folder.Open();
        var settings = new SettingsRepository(repaired);
        var startsOnDefaults = SettingsRepository.StartsWithoutSavedSettings(settings, repaired);

        // The scenario: the repair ran, the auxiliary row came back, the document did not.
        Assert.True(repaired.RepairedAtStartup);
        Assert.NotNull(settings.Get(AuxiliaryKey));
        Assert.Null(settings.Get("app_settings"));

        Assert.True(repaired.SettingsLostInRepair);
        Assert.True(settings.LastLoadFailed);
        Assert.True(startsOnDefaults);

        var defaults = settings.Load();
        Assert.Equal(90, defaults.HistoryRetentionDays);
        using var maintenance = CreateMaintenance(repaired);
        if (startsOnDefaults)
        {
            maintenance.KeepAllTextThisSession();
        }

        Assert.Equal(0, maintenance.RunOnce(defaults)!.HistoryEntriesRemoved);
        Assert.Contains(new HistoryRepository(repaired).GetRecent(100), e => e.Text == "half a year old");

        // Without the latch the same defaults delete it: the latch is what kept it.
        using var ordinary = CreateMaintenance(repaired);
        Assert.Equal(1, ordinary.RunOnce(defaults)!.HistoryEntriesRemoved);
    }

    [Fact]
    public void A_repair_that_recovers_the_document_is_not_a_loss()
    {
        Seed(document: KeepEverything(), loseDocument: false);

        using var repaired = _folder.Open();
        var settings = new SettingsRepository(repaired);

        Assert.False(SettingsRepository.StartsWithoutSavedSettings(settings, repaired));
        Assert.True(repaired.RepairedAtStartup);
        Assert.False(repaired.SettingsLostInRepair);
        Assert.False(settings.LastLoadFailed);
        Assert.Equal(0, settings.Load().HistoryRetentionDays);

        // The repair records the loss before it recovers anything, and withdraws the record once the document is back.
        Assert.Null(settings.Get(SettingsRepository.LostMarkerKey));
    }

    [Fact]
    public void A_repair_that_recovers_an_unreadable_document_is_a_loss_and_keeps_its_recovery_copy()
    {
        Seed(document: null, loseDocument: false, rawDocument: "{ \"historyRetentionDays\": 0, not json");

        using var repaired = _folder.Open();
        var settings = new SettingsRepository(repaired);

        Assert.True(SettingsRepository.StartsWithoutSavedSettings(settings, repaired));
        Assert.True(repaired.SettingsLostInRepair);
        Assert.True(settings.LastLoadFailed);
        Assert.Equal("{ \"historyRetentionDays\": 0, not json", settings.Get("app_settings_recovery"));
    }

    [Fact]
    public void No_document_without_a_repair_is_an_ordinary_first_run()
    {
        using var db = _folder.Open();
        var settings = new SettingsRepository(db);

        Assert.False(SettingsRepository.StartsWithoutSavedSettings(settings, db));
        Assert.False(db.RepairedAtStartup);
        Assert.False(settings.LastLoadFailed);
    }

    [Fact]
    public void A_document_lost_in_repair_is_reported_until_a_readable_one_is_saved()
    {
        Seed(document: KeepEverything(), loseDocument: true);

        using var repaired = _folder.Open();
        var settings = new SettingsRepository(repaired);
        settings.Load();
        Assert.True(settings.LastLoadFailed);
        Assert.NotNull(settings.Get(SettingsRepository.LostMarkerKey));

        // Saving is what the Settings window does once the user has reviewed the defaults.
        settings.Save(AppSettings.CreateDefault());
        Assert.False(settings.LastLoadFailed);
        Assert.Null(settings.Get(SettingsRepository.LostMarkerKey));
        settings.Load();
        Assert.False(settings.LastLoadFailed);
    }

    [Fact]
    public void No_partial_update_writes_defaults_over_a_lost_document_and_the_welcome_flag_is_one()
    {
        // The first-run welcome records its flag through Update, and on the defaults a lost document leaves behind
        // that flag is unset: it used to save those defaults as though the user had chosen them.
        Seed(document: KeepEverything(), loseDocument: true);

        using var repaired = _folder.Open();
        var settings = new SettingsRepository(repaired);
        Assert.False(settings.Load().HasCompletedFirstRun);

        Assert.Throws<InvalidOperationException>(() => settings.Update(s => s.HasCompletedFirstRun = true));
        Assert.Throws<InvalidOperationException>(() => settings.Update(s => s.LaunchOnLogin = true));

        Assert.True(settings.LastLoadFailed);
        Assert.Null(settings.Get("app_settings"));

        // Once the user saves a whole document, the loss is over and partial updates go through again.
        settings.SaveBundle(KeepEverything(), dictionaryEntries: null, snippets: null);
        Assert.Null(settings.Get(SettingsRepository.LostMarkerKey));
        Assert.True(settings.Update(s => s.HasCompletedFirstRun = true).HasCompletedFirstRun);
        Assert.Equal(0, settings.Load().HistoryRetentionDays);
    }

    [Fact]
    public void A_lost_document_stays_lost_on_later_starts_until_one_is_saved()
    {
        // The start after the repair finds no document and no repair; without the recorded loss it would read that
        // as a first run and apply default retention to the half-year-old entry.
        Seed(document: KeepEverything(), loseDocument: true);
        using (var repaired = _folder.Open())
        {
            repaired.Initialize();
            Assert.True(repaired.SettingsLostInRepair);
        }

        using (var later = _folder.Open())
        {
            var settings = new SettingsRepository(later);
            var startsOnDefaults = SettingsRepository.StartsWithoutSavedSettings(settings, later);

            Assert.False(later.RepairedAtStartup);
            Assert.False(later.SettingsLostInRepair);
            Assert.True(startsOnDefaults);
            Assert.True(settings.LastLoadFailed);
            Assert.Throws<InvalidOperationException>(() => settings.Update(s => s.HasCompletedFirstRun = true));

            using var maintenance = CreateMaintenance(later);
            maintenance.KeepAllTextThisSession();
            Assert.Equal(0, maintenance.RunOnce(settings.Load())!.HistoryEntriesRemoved);
            Assert.Contains(new HistoryRepository(later).GetRecent(100), e => e.Text == "half a year old");

            // The user reviews the defaults and saves, keeping everything again.
            settings.SaveBundle(KeepEverything(), dictionaryEntries: null, snippets: null);
        }

        using var afterSave = _folder.Open();
        var saved = new SettingsRepository(afterSave);
        Assert.False(SettingsRepository.StartsWithoutSavedSettings(saved, afterSave));
        Assert.Equal(0, saved.Load().HistoryRetentionDays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_repair_says_whether_the_dictionary_came_back(bool dictionaryHadEntries)
    {
        Seed(document: KeepEverything(), loseDocument: false, dictionary: dictionaryHadEntries);

        using var repaired = _folder.Open();
        repaired.Initialize();

        Assert.True(repaired.RepairedAtStartup);
        Assert.False(repaired.SettingsLostInRepair);
        Assert.Equal(!dictionaryHadEntries, repaired.DictionaryLostInRepair);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_helpers_write_no_defaults_over_settings_the_session_could_not_use(bool lostInRepair)
    {
        // Lost in the repair, or recovered but unreadable: either way the decision comes first and the one-time
        // startup migrations, which save the whole document with their flag set, leave it alone.
        const string Unreadable = "{ not json";
        if (lostInRepair)
        {
            Seed(document: KeepEverything(), loseDocument: true);
        }
        else
        {
            using var db = _folder.Open();
            new SettingsRepository(db).Set("app_settings", Unreadable);
        }

        using var database = _folder.Open();
        var settings = new SettingsRepository(database);

        Assert.True(SettingsRepository.StartsWithoutSavedSettings(settings, database));
        Assert.Equal(0, SeedVocabularyRetirement.Apply(settings, new DictionaryRepository(database), []));
        Assert.False(FoundryDemotionReset.Apply(settings, new AppPaths(_folder.Root)));

        Assert.Equal(lostInRepair ? null : Unreadable, settings.Get("app_settings"));
    }

    [Fact]
    public void A_start_whose_first_settings_reads_fail_saves_no_defaults_over_a_lost_document()
    {
        // The start after a repair that lost the document. Its first two reads (the session banner's and the startup
        // probe's) fail on a transient lock, so the probe answers "without saved settings" while nothing records a
        // failed load, and the migrations' own reads then succeed and find the recorded loss. Saving their flags there
        // wrote default retention over the loss and ended it, so the start after it could delete the half-year-old
        // entry without the user ever saving.
        Seed(document: KeepEverything(), loseDocument: true);
        using (var repaired = _folder.Open())
        {
            repaired.Initialize();
        }

        using (var later = _folder.Open())
        {
            var stored = new SettingsRepository(later);
            var settings = new FirstReadsFail(stored, failures: 2);
            Assert.ThrowsAny<SqliteException>(() => settings.Load());
            Assert.True(SettingsRepository.StartsWithoutSavedSettings(settings, later));
            Assert.False(settings.LastLoadFailed);

            // Startup now skips both after such a probe, and each still refuses on the read it makes itself.
            Assert.Equal(0, SeedVocabularyRetirement.Apply(settings, new DictionaryRepository(later), []));
            Assert.False(FoundryDemotionReset.Apply(settings, new AppPaths(_folder.Root)));

            Assert.True(settings.LastLoadFailed);
            Assert.Null(stored.Get("app_settings"));
            Assert.NotNull(stored.Get(SettingsRepository.LostMarkerKey));
        }

        using var next = _folder.Open();
        Assert.True(SettingsRepository.StartsWithoutSavedSettings(new SettingsRepository(next), next));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("settings")]
    [InlineData("dictionary")]
    [InlineData("snippets")]
    [InlineData("audio_blobs")]
    [InlineData("history")]
    [InlineData("cleanup_failures")]
    public void A_repair_cut_short_at_any_step_never_leaves_recovered_history_in_what_reads_as_a_first_run(string step)
    {
        // A crash partway through a repair leaves a healthy file, so the next start repairs nothing and goes by what
        // that file holds. The loss used to be recorded only after every table was copied, so a crash between the
        // history and the record left the half-year-old entry in a database that read as a first run.
        Seed(document: KeepEverything(), loseDocument: true);
        using var crashed = new TempDatabaseFolder();
        using (var repaired = _folder.Open())
        {
            repaired.RepairStepCommitted = (connection, committed) =>
            {
                if (committed == step)
                {
                    TakeCrashImage(connection, crashed.DatabasePath);
                }
            };
            repaired.Initialize();
            Assert.True(repaired.RepairedAtStartup);
        }

        using var next = crashed.Open();
        var startsOnDefaults = SettingsRepository.StartsWithoutSavedSettings(new SettingsRepository(next), next);
        var history = new HistoryRepository(next).GetRecent(100);

        Assert.False(next.RepairedAtStartup);
        Assert.Equal(step is "history" or "cleanup_failures", history.Any(e => e.Text == "half a year old"));
        Assert.True(history.Count == 0 || startsOnDefaults, $"History recovered by the '{step}' step reads as a first run.");
    }

    [Fact]
    public void A_repair_that_cannot_record_the_loss_recovers_no_history_a_later_start_could_delete()
    {
        // The record is the first row the rebuilt database is asked to hold, and this one refuses it. Recovering the
        // history anyway would leave it beside no settings and no record: the next start would take the file for a
        // first run and apply the default retention to it.
        Seed(document: KeepEverything(), loseDocument: true);
        using (var repaired = _folder.Open())
        {
            repaired.RepairStepCommitted = (connection, step) =>
            {
                if (step == "schema")
                {
                    using var refuse = connection.CreateCommand();
                    refuse.CommandText =
                        "CREATE TRIGGER refuse_loss_record BEFORE INSERT ON settings " +
                        $"WHEN NEW.key = '{SettingsRepository.LostMarkerKey}' BEGIN SELECT RAISE(ABORT, 'refused'); END;";
                    refuse.ExecuteNonQuery();
                }
            };
            repaired.Initialize();

            Assert.True(repaired.RepairedAtStartup);
            Assert.True(repaired.SettingsLostInRepair);
            Assert.True(repaired.DictionaryLostInRepair);
            Assert.Empty(new HistoryRepository(repaired).GetRecent(100));
        }

        // Every row is still in the damaged copy the repair keeps, for recovery by hand.
        Assert.Single(
            Directory.GetFiles(_folder.Root, AppPaths.DatabaseFileName + ".corrupt-*"),
            path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal));

        using var next = _folder.Open();
        Assert.False(next.RepairedAtStartup);
        Assert.Empty(new HistoryRepository(next).GetRecent(100));
    }

    [Fact]
    public void A_session_on_a_document_lost_in_repair_keeps_right_ctrl_and_one_hotkey()
    {
        // Not a first run: whoever lost this document has been pressing Right Ctrl, and Page Up and Page Down still
        // belong to their other apps until they choose otherwise.
        var document = KeepEverything();
        document.Hotkey = HotkeyBinding.Legacy;
        document.DictationOnlyHotkey = null;
        Seed(document, loseDocument: true);

        using var repaired = _folder.Open();
        var settings = new SettingsRepository(repaired);
        var session = settings.Load();

        Assert.True(repaired.SettingsLostInRepair);
        Assert.True(settings.LastLoadFailed);
        Assert.Equal(HotkeyBinding.Legacy, session.Hotkey);
        Assert.Null(session.DictationOnlyHotkey);
    }

    private static AppSettings KeepEverything()
    {
        var settings = AppSettings.CreateDefault();
        settings.HistoryRetentionDays = 0;
        return settings;
    }

    private StorageMaintenance CreateMaintenance(ScribeDatabase db) =>
        new(db, new HistoryRepository(db), new CleanupFailureLog(db), NullLogger.Instance,
            new ManualTimeProvider(_now), StorageMaintenanceOptions.Default);

    // A database holding the user's document (or raw text in its place), an auxiliary settings row, a dictionary entry
    // unless told otherwise, a half-year-old entry near the front of the file and enough later history for damage at
    // the tail to stay clear of all of them. With loseDocument the damaged file no longer holds the document, as when
    // the page holding it is lost.
    private void Seed(AppSettings? document, bool loseDocument, string? rawDocument = null, bool dictionary = true)
    {
        using (var db = _folder.Open())
        {
            var settings = new SettingsRepository(db);
            if (document is not null)
            {
                settings.Save(document);
            }
            else if (rawDocument is not null)
            {
                settings.Set("app_settings", rawDocument);
            }

            settings.Set(AuxiliaryKey, "{}");
            if (dictionary)
            {
                new DictionaryRepository(db).Add(DictionaryEntry.New("see sharp", "C#"));
            }

            var history = new HistoryRepository(db);
            history.Add(new HistoryEntry(0, _now.AddDays(-180), "half a year old", 1, 1));
            for (var i = 0; i < 50; i++)
            {
                history.Add(new HistoryEntry(0, _now.AddMinutes(-60 + i), new string('x', 4000), 1000, 50));
            }

            if (loseDocument)
            {
                using var connection = db.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM settings WHERE key = 'app_settings';";
                command.ExecuteNonQuery();
            }
        }

        // Every handle on the file must be gone before its bytes are overwritten, but only this file's pool is emptied:
        // other test classes run in parallel with pools of their own.
        DatabasePools.Release(new AppPaths(_folder.Root));

        CorruptTail(_folder.DatabasePath, pages: 4);
    }

    // Overwrites the last pages with garbage, leaving the header, the schema and the early pages that hold the
    // settings rows and the oldest history intact (the shape DatabaseSalvageTests uses).
    private static void CorruptTail(string path, int pages)
    {
        const int PageSize = 4096;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var garbage = new byte[PageSize * pages];
        new Random(42).NextBytes(garbage);
        stream.Seek(-garbage.Length, SeekOrigin.End);
        stream.Write(garbage);
    }

    // What a crash at this moment leaves on disk: every transaction the rebuild has committed, each durable because
    // it runs with synchronous=FULL, and nothing it had yet to do.
    private static void TakeCrashImage(SqliteConnection rebuilt, string path)
    {
        using var image = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        image.Open();
        rebuilt.BackupDatabase(image);
    }

    // The real repository, except that its first reads fail the way they do while another process holds the database.
    private sealed class FirstReadsFail(SettingsRepository inner, int failures) : ISettingsRepository
    {
        private int _failuresLeft = failures;

        public bool LastLoadFailed => inner.LastLoadFailed;

        public AppSettings Load()
        {
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new SqliteException("database is locked", 5);
            }

            return inner.Load();
        }

        public void Save(AppSettings settings) => inner.Save(settings);

        public AppSettings Update(Action<AppSettings> mutate) => inner.Update(mutate);

        public AppSettings Update(Action<AppSettings> mutate, long revision, out bool superseded) =>
            inner.Update(mutate, revision, out superseded);

        public void SaveBundle(
            AppSettings settings,
            IReadOnlyList<DictionaryEntry>? dictionaryEntries,
            IReadOnlyList<Snippet>? snippets,
            long aiCleanupIntent = 0) =>
            inner.SaveBundle(settings, dictionaryEntries, snippets, aiCleanupIntent);

        public string? Get(string key) => inner.Get(key);

        public void Set(string key, string value) => inner.Set(key, value);
    }
}
