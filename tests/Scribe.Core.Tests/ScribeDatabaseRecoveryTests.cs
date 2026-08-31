using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// Startup corruption handling against real database files. These exist because of a real data
/// loss: a probe error from a transiently locked (healthy) database used to be treated as
/// corruption, and the destructive rebuild reset the user's settings to defaults on 2026-08-31.
/// </summary>
public sealed class ScribeDatabaseRecoveryTests : IDisposable
{
    private readonly string _root;

    public ScribeDatabaseRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scribe-dbtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private AppPaths Paths() => new(_root);

    private string DbPath => Path.Combine(_root, AppPaths.DatabaseFileName);

    private string[] CorruptAsideFiles() => Directory.GetFiles(_root, "scribe.db.corrupt-*");

    [Fact]
    public void Healthy_database_initializes_without_repair()
    {
        using (var db = new ScribeDatabase(Paths(), NullLogger<ScribeDatabase>.Instance))
        {
            db.Initialize();
            Assert.False(db.RepairedAtStartup);
        }

        // Reopen: the healthy file must pass the probe untouched.
        using var reopened = new ScribeDatabase(Paths(), NullLogger<ScribeDatabase>.Instance);
        reopened.Initialize();
        Assert.False(reopened.RepairedAtStartup);
        Assert.False(reopened.SettingsLostInRepair);
        Assert.Empty(CorruptAsideFiles());
    }

    [Fact]
    public void Locked_healthy_database_is_never_rebuilt()
    {
        // Create a healthy database with a settings row, then hold its file exclusively the way
        // an exiting instance's unclosed handle does.
        using (var db = new ScribeDatabase(Paths(), NullLogger<ScribeDatabase>.Instance))
        {
            db.Initialize();
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO settings (key, value) VALUES ('app_settings', '{\"precious\":true}');";
            command.ExecuteNonQuery();
        }

        using (File.Open(DbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var locked = new ScribeDatabase(Paths(), NullLogger<ScribeDatabase>.Instance);

            // The probe cannot open the file. The old behavior treated that as corruption and
            // moved the database aside; the only acceptable outcomes now are a thrown startup
            // error with the file left in place.
            Assert.ThrowsAny<Exception>(locked.Initialize);
        }

        Assert.True(File.Exists(DbPath), "the locked database must not be moved aside");
        Assert.Empty(CorruptAsideFiles());

        // Once the lock clears, the untouched database opens with its data intact.
        using var recovered = new ScribeDatabase(Paths(), NullLogger<ScribeDatabase>.Instance);
        recovered.Initialize();
        Assert.False(recovered.RepairedAtStartup);
        using var check = recovered.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT value FROM settings WHERE key = 'app_settings';";
        Assert.Equal("{\"precious\":true}", (string?)read.ExecuteScalar());
    }

    [Fact]
    public void Garbage_file_is_rebuilt_and_reports_settings_lost()
    {
        File.WriteAllText(DbPath, "this is not a sqlite database, not even close, padding padding");

        using var db = new ScribeDatabase(Paths(), NullLogger<ScribeDatabase>.Instance);
        db.Initialize();

        Assert.True(db.RepairedAtStartup);
        Assert.True(db.SettingsLostInRepair, "nothing was salvageable, so settings were lost");
        Assert.Single(CorruptAsideFiles());

        // The rebuilt database is fully usable.
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM settings;";
        Assert.Equal(0L, (long?)command.ExecuteScalar());
    }
}
