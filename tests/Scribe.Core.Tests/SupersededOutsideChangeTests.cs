using Microsoft.Data.Sqlite;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The AI cleanup switch between the tray and a whole-document Settings save. The newest intent wins, ordered by when
/// the user made it, and a save never writes over a stored value its window neither showed nor changed. Every settings
/// write runs whole under one lock, and a save's intent is recorded only once its commit has succeeded, so no ordering
/// of the two, and no way SQLite rolls a save back, lets an older tray change land over a newer save or a failed save
/// supersede a tray change.
/// </summary>
public sealed class SupersededOutsideChangeTests : IDisposable
{
    private readonly TempDatabaseFolder _folder = new();
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;

    public SupersededOutsideChangeTests()
    {
        _database = _folder.Open();
        _settings = new SettingsRepository(_database);
        _settings.Save(new AppSettings { EnableAiCleanup = false });
    }

    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void A_change_no_save_has_accounted_for_is_written()
    {
        var revision = ExternalSwitchSync.NextRevision();
        _settings.SaveBundle(new AppSettings { EnableAiCleanup = false }, null, null, aiCleanupIntent: revision - 1);

        var stored = _settings.Update(s => s.EnableAiCleanup = true, revision, out var superseded);

        Assert.False(superseded);
        Assert.True(stored.EnableAiCleanup);
        Assert.True(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public void A_change_a_committed_save_accounted_for_is_not_written_and_the_stored_settings_come_back()
    {
        var revision = ExternalSwitchSync.NextRevision();
        _settings.SaveBundle(
            new AppSettings { EnableAiCleanup = false, HistoryRetentionDays = 30 }, null, null, aiCleanupIntent: revision);

        var stored = _settings.Update(s => s.EnableAiCleanup = true, revision, out var superseded);

        Assert.True(superseded);
        Assert.False(stored.EnableAiCleanup);
        Assert.Equal(30, stored.HistoryRetentionDays);
        Assert.False(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public void A_save_that_commits_while_the_change_waits_for_the_write_lock_supersedes_it()
    {
        // No save can commit while the change holds the write lock, so the latest one can commit is just before the
        // change's transaction begins. Standing for such a save at the first moment inside that transaction, the change
        // is still superseded, because it decides only there.
        var revision = ExternalSwitchSync.NextRevision();
        _settings.WriteStep = (step, _, _) =>
        {
            if (step == "update began")
            {
                _settings.RecordSavedThrough(revision);
            }
        };

        _settings.Update(s => s.EnableAiCleanup = true, revision, out var superseded);

        Assert.True(superseded);
        Assert.False(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public async Task A_change_that_tries_to_write_the_moment_a_save_commits_waits_for_its_intent_and_is_superseded()
    {
        // Between a save's commit and the record of its intent, SQLite's lock is already free; the settings lock the
        // save still holds is what keeps every other settings write out. Without it, a waiting change could write in
        // that gap, find nothing, and land over the save.
        var revision = ExternalSwitchSync.NextRevision();
        using var race = new LockRace<bool>(_settings, _database);
        _settings.WriteStep = (step, _, _) =>
        {
            if (step == "update began")
            {
                race.WriterBegan();
            }
            else if (step == "save committed")
            {
                race.StartWhileTheSaveHoldsTheLock(() =>
                {
                    _settings.Update(s => s.EnableAiCleanup = true, revision, out var superseded);
                    return superseded;
                });
            }
        };

        _settings.SaveBundle(new AppSettings { EnableAiCleanup = false }, null, null, aiCleanupIntent: revision);

        race.AssertTheSaveKeptTheWriterOut();
        Assert.True(await race.Writer.WaitAsync(BlockedThreads.SafetyTimeout));
        Assert.False(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public async Task A_save_that_sqlite_rolls_back_at_once_never_supersedes_the_tray_opt_out_queued_behind_it()
    {
        // Cleanup is on, the tray's opt-out is queued, and a Settings save carrying a newer intent fails. After some
        // errors SQLite rolls the transaction back by itself and releases its write lock at once
        // (sqlite.org/lang_transaction.html, response to errors within a transaction). Had the save's intent been
        // recorded before its commit, the opt-out could take the released lock, find that intent, count itself
        // superseded and leave cleanup on.
        _settings.Save(new AppSettings { EnableAiCleanup = true });
        var trayOff = ExternalSwitchSync.NextRevision();
        var newerIntent = ExternalSwitchSync.NextRevision();
        using var race = new LockRace<(bool Superseded, bool Enabled)>(_settings, _database);
        _settings.WriteStep = (step, connection, transaction) =>
        {
            if (step == "update began")
            {
                race.WriterBegan();
            }
            else if (step == "save committing")
            {
                // What SQLite does by itself after such an error: the transaction is gone and its write lock released.
                using var rollback = connection.CreateCommand();
                rollback.Transaction = transaction;
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();

                race.StartWhileTheSaveHoldsTheLock(() =>
                {
                    var stored = _settings.Update(s => s.EnableAiCleanup = false, trayOff, out var superseded);
                    return (superseded, stored.EnableAiCleanup);
                });
            }
        };

        Assert.ThrowsAny<Exception>(() => _settings.SaveBundle(
            new AppSettings { EnableAiCleanup = true }, null, null, aiCleanupIntent: newerIntent));
        _settings.WriteStep = null;

        race.AssertTheSaveKeptTheWriterOut();
        var (superseded, enabled) = await race.Writer.WaitAsync(BlockedThreads.SafetyTimeout);
        Assert.False(superseded);
        Assert.False(enabled);
        Assert.False(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public void A_save_that_fails_to_commit_supersedes_nothing()
    {
        var revision = ExternalSwitchSync.NextRevision();
        _settings.WriteStep = (step, connection, transaction) =>
        {
            if (step == "save committing")
            {
                // Checked only at the commit, a reference to an audio blob that does not exist makes the commit fail.
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "PRAGMA defer_foreign_keys = ON; " +
                    "INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, audio_blob_id) " +
                    "VALUES ('2026-01-01T00:00:00.0000000+00:00', 'orphan', 1, 1, 424242);";
                command.ExecuteNonQuery();
            }
        };

        Assert.ThrowsAny<SqliteException>(() => _settings.SaveBundle(
            new AppSettings { EnableAiCleanup = false, HistoryRetentionDays = 7 }, null, null, aiCleanupIntent: revision));
        _settings.WriteStep = null;

        var stored = _settings.Update(s => s.EnableAiCleanup = true, revision, out var superseded);

        Assert.False(superseded);
        Assert.True(stored.EnableAiCleanup);
        Assert.NotEqual(7, _settings.Load().HistoryRetentionDays);
    }

    [Fact]
    public void A_save_with_no_intent_for_the_switch_keeps_the_stored_value_and_hands_it_back()
    {
        // The window never set the switch nor took a tray change, so what it shows can be older than what is stored.
        var window = new AppSettings { EnableAiCleanup = true, HistoryRetentionDays = 30 };

        _settings.SaveBundle(window, null, null, aiCleanupIntent: 0);

        var stored = _settings.Load();
        Assert.False(stored.EnableAiCleanup);
        Assert.Equal(30, stored.HistoryRetentionDays);
        Assert.False(window.EnableAiCleanup);
    }

    [Fact]
    public void A_save_with_an_intent_for_the_switch_writes_the_windows_value()
    {
        var window = new AppSettings { EnableAiCleanup = true };

        _settings.SaveBundle(window, null, null, aiCleanupIntent: ExternalSwitchSync.NextRevision());

        Assert.True(_settings.Load().EnableAiCleanup);
        Assert.True(window.EnableAiCleanup);
    }

    [Fact]
    public void A_save_with_no_intent_and_no_stored_document_writes_the_windows_value()
    {
        // A first run, or a document a repair lost that the user has reviewed: there is no stored value to keep.
        using (var connection = _database.Open())
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM settings WHERE key = 'app_settings';";
            delete.ExecuteNonQuery();
        }

        _settings.SaveBundle(new AppSettings { EnableAiCleanup = true }, null, null, aiCleanupIntent: 0);

        Assert.True(_settings.Load().EnableAiCleanup);
    }

    [Fact]
    public void An_unchecked_update_is_never_superseded()
    {
        _settings.SaveBundle(new AppSettings { EnableAiCleanup = false }, null, null, aiCleanupIntent: long.MaxValue);

        Assert.True(_settings.Update(s => s.EnableAiCleanup = true).EnableAiCleanup);
        Assert.True(_settings.Load().EnableAiCleanup);
    }

    /// <summary>
    /// A settings write started from inside a save at the save's critical moment, and what the save saw of it there,
    /// recorded without a timer: whether the save held the settings lock, whether SQLite's own write lock was free (so
    /// only the settings lock could keep the writer out), whether the writer reached the settings lock, and whether it
    /// got any further while the save was still inside that moment.
    /// </summary>
    private sealed class LockRace<T>(SettingsRepository settings, ScribeDatabase database) : IDisposable
    {
        private readonly ManualResetEventSlim _arrived = new();
        private readonly ManualResetEventSlim _began = new();
        private bool _saveHeldTheLock;
        private bool _databaseLockWasFree;
        private bool _writerArrived;
        private bool _writerBeganDuringTheSave = true;
        private bool _writerFinishedDuringTheSave = true;
        private Task<T>? _writer;

        public Task<T> Writer => _writer ?? throw new InvalidOperationException("The save never reached its critical moment.");

        // On the save's thread, from its WriteStep callback.
        public void StartWhileTheSaveHoldsTheLock(Func<T> write)
        {
            _saveHeldTheLock = settings.WriteLockHeldByCurrentThread;
            _databaseLockWasFree = DatabaseWriteLockIsFree();

            // From here on only the writer asks for the settings lock; the save already holds it.
            settings.WriteLockRequested = _arrived.Set;
            _writer = Task.Run(write);

            // Whichever comes first: the writer reaching the lock, as it must, or beginning its transaction without
            // having asked for it. Both are signals, so the wait ends the moment either happens; the timeout is only a
            // safeguard, and running out fails the test.
            WaitHandle.WaitAny([_arrived.WaitHandle, _began.WaitHandle], BlockedThreads.SafetyTimeout);
            _writerArrived = _arrived.IsSet;
            _writerBeganDuringTheSave = _began.IsSet;
            _writerFinishedDuringTheSave = _writer.IsCompleted;
        }

        // The writer's "update began" step: its transaction has begun, which under the lock means it holds the lock.
        public void WriterBegan() => _began.Set();

        public void AssertTheSaveKeptTheWriterOut()
        {
            Assert.True(_saveHeldTheLock, "The save did not hold the settings write lock at its critical moment.");
            Assert.True(_databaseLockWasFree, "SQLite's write lock was still held there, so nothing is shown about the settings lock.");
            Assert.True(_writerArrived, "The writer never asked for the settings write lock.");
            Assert.False(_writerBeganDuringTheSave, "The writer got past the settings lock while the save held it.");
            Assert.False(_writerFinishedDuringTheSave, "The writer finished while the save held the settings lock.");
        }

        public void Dispose()
        {
            settings.WriteLockRequested = null;
            _arrived.Dispose();
            _began.Dispose();
        }

        // Whether another connection can take SQLite's write lock at once.
        private bool DatabaseWriteLockIsFree()
        {
            try
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout = 0; BEGIN IMMEDIATE; ROLLBACK;";
                command.ExecuteNonQuery();
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
        }
    }
}