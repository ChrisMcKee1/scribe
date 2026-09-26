using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// The Start with Windows switch and the tray's AI cleanup toggle both rewrite the one stored
/// settings document. These pin that neither can put back a stale copy over the other.
/// </summary>
public sealed class StartupPreferenceWriteTests : IDisposable
{
    // A hang guard, never the verdict: every wait below is for a gate the test opens or a write it lets finish.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribe-settings-update-" + Guid.NewGuid().ToString("N"));
    private readonly ScribeDatabase _database;
    private readonly SettingsRepository _settings;

    public StartupPreferenceWriteTests()
    {
        Directory.CreateDirectory(_root);
        _database = new ScribeDatabase(new AppPaths(_root), NullLogger<ScribeDatabase>.Instance);
        _settings = new SettingsRepository(_database);
        _settings.Save(AppSettings.CreateDefault());
    }

    [Fact]
    public async Task Switch_write_that_lands_while_the_tray_toggle_is_mid_write_keeps_both_changes()
    {
        using var trayInside = new ManualResetEventSlim();
        using var releaseTray = new ManualResetEventSlim();

        // The tray toggle has read the document and is about to write it back.
        var tray = Task.Factory.StartNew(
            () => _settings.Update(stored =>
            {
                stored.EnableAiCleanup = true;
                trayInside.Set();
                Assert.True(releaseTray.Wait(Bound));
            }),
            TaskCreationOptions.LongRunning);
        Assert.True(trayInside.Wait(Bound));

        // The switch saves its preference exactly then, through the same path Settings uses.
        var switchWrite = StartupPreference.PersistAsync(_settings, enabled: true);

        releaseTray.Set();
        await tray.WaitAsync(Bound);
        await switchWrite.WaitAsync(Bound);

        var saved = _settings.Load();
        Assert.True(saved.EnableAiCleanup);
        Assert.True(saved.LaunchOnLogin);
    }

    [Fact]
    public async Task Tray_write_that_lands_while_the_switch_is_mid_write_keeps_both_changes()
    {
        using var switchInside = new ManualResetEventSlim();
        using var releaseSwitch = new ManualResetEventSlim();

        var switchWrite = Task.Factory.StartNew(
            () => _settings.Update(stored =>
            {
                stored.LaunchOnLogin = true;
                switchInside.Set();
                Assert.True(releaseSwitch.Wait(Bound));
            }),
            TaskCreationOptions.LongRunning);
        Assert.True(switchInside.Wait(Bound));

        var tray = Task.Run(() => _settings.Update(stored => stored.EnableAiCleanup = true));

        releaseSwitch.Set();
        await switchWrite.WaitAsync(Bound);
        await tray.WaitAsync(Bound);

        var saved = _settings.Load();
        Assert.True(saved.LaunchOnLogin);
        Assert.True(saved.EnableAiCleanup);
    }

    [Fact]
    public async Task No_other_connection_can_write_between_the_read_and_the_commit()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var update = Task.Factory.StartNew(
            () => _settings.Update(stored =>
            {
                stored.LaunchOnLogin = true;
                inside.Set();
                Assert.True(release.Wait(Bound));
            }),
            TaskCreationOptions.LongRunning);
        Assert.True(inside.Wait(Bound));

        try
        {
            // A plain save from anywhere else, such as a second process or a caller that does not
            // use Update, must wait for the commit instead of slipping in underneath it.
            using var other = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, AppPaths.DatabaseFileName),
                Pooling = false,
            }.ToString());
            other.Open();
            using var write = other.CreateCommand();
            write.CommandTimeout = 1;
            write.CommandText = "UPDATE settings SET value = value WHERE key = 'app_settings';";

            var busy = Assert.Throws<SqliteException>(() => write.ExecuteNonQuery());
            Assert.Equal(5, busy.SqliteErrorCode); // SQLITE_BUSY
        }
        finally
        {
            release.Set();
        }

        await update.WaitAsync(Bound);
        Assert.True(_settings.Load().LaunchOnLogin);
    }

    [Fact]
    public void Update_refuses_to_write_over_a_document_it_cannot_read()
    {
        _settings.Set("app_settings", "{ not json");

        Assert.Throws<InvalidOperationException>(() => _settings.Update(stored => stored.LaunchOnLogin = true));
        Assert.Equal("{ not json", _settings.Get("app_settings"));
    }

    [Fact]
    public void Update_on_a_database_with_no_settings_starts_from_the_first_run_defaults()
    {
        using var connection = _database.Open();
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM settings WHERE key = 'app_settings';";
            delete.ExecuteNonQuery();
        }

        var saved = _settings.Update(stored => stored.LaunchOnLogin = true);

        Assert.True(saved.LaunchOnLogin);
        Assert.Equal(AppSettings.CreateDefault().EnabledDictionaryLibraryIds, _settings.Load().EnabledDictionaryLibraryIds);
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabasePools.Release(new AppPaths(_root));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
