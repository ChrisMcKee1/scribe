using Scribe.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Scribe.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Default_data_root_is_separate_from_the_velopack_install_root()
    {
        // The Velopack installer renames/clears its install root (%LOCALAPPDATA%\Scribe) on every
        // overwrite-install, so the writable data folder must never be that directory or the user's
        // database would be deleted on reinstall. This guards against regressing data back into it.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCRIBE_DATA_DIR")))
        {
            return; // An explicit data dir override is in effect; the default-root invariant is moot.
        }

        var paths = new AppPaths();

        Assert.EndsWith(Path.DirectorySeparatorChar + "ScribeData", paths.RootDir);
        Assert.NotNull(paths.LegacyRootDir);
        Assert.EndsWith(Path.DirectorySeparatorChar + "Scribe", paths.LegacyRootDir!);
        Assert.False(
            string.Equals(paths.RootDir, paths.LegacyRootDir, StringComparison.OrdinalIgnoreCase),
            "The data root must differ from the Velopack install root.");
    }

    [Fact]
    public void Folder_name_constants_match_the_relocated_layout()
    {
        Assert.Equal("ScribeData", AppPaths.AppFolderName);
        Assert.Equal("Scribe", AppPaths.LegacyAppFolderName);
    }

    [Fact]
    public void Explicit_root_override_disables_legacy_migration()
    {
        var root = Path.Combine(Path.GetTempPath(), "scribe-test-" + Guid.NewGuid().ToString("N"));

        var paths = new AppPaths(root);

        Assert.Equal(root, paths.RootDir);
        Assert.Null(paths.LegacyRootDir);
        Assert.Equal(Path.Combine(root, "scribe.db"), paths.DatabasePath);
    }

    [Fact]
    public void TryMigrateDatabase_backs_up_committed_wal_data_when_destination_empty()
    {
        var (legacy, fresh) = CreateTempPair();
        try
        {
            Directory.CreateDirectory(legacy);
            var legacyDb = Path.Combine(legacy, "scribe.db");
            using var connection = new SqliteConnection($"Data Source={legacyDb}");
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                    "CREATE TABLE sample (value TEXT); INSERT INTO sample VALUES ('from-wal');";
                command.ExecuteNonQuery();
            }

            Assert.True(File.Exists(legacyDb + "-wal"));

            AppPaths.TryMigrateDatabase(legacy, fresh);

            using var migrated = new SqliteConnection($"Data Source={Path.Combine(fresh, "scribe.db")}");
            migrated.Open();
            using var read = migrated.CreateCommand();
            read.CommandText = "SELECT value FROM sample;";
            Assert.Equal("from-wal", read.ExecuteScalar());
        }
        finally
        {
            Cleanup(legacy, fresh);
        }
    }

    [Fact]
    public void TryMigrateDatabase_never_overwrites_an_existing_database()
    {
        var (legacy, fresh) = CreateTempPair();
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(fresh);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(legacy, "scribe.db")}"))
            {
                connection.Open();
            }
            File.WriteAllText(Path.Combine(fresh, "scribe.db"), "current");

            AppPaths.TryMigrateDatabase(legacy, fresh);

            Assert.Equal("current", File.ReadAllText(Path.Combine(fresh, "scribe.db")));
        }
        finally
        {
            Cleanup(legacy, fresh);
        }
    }

    [Fact]
    public void TryMigrateDatabase_is_a_noop_when_no_legacy_database_exists()
    {
        var (legacy, fresh) = CreateTempPair();
        try
        {
            Directory.CreateDirectory(legacy); // present but empty

            AppPaths.TryMigrateDatabase(legacy, fresh);

            Assert.False(File.Exists(Path.Combine(fresh, "scribe.db")));
        }
        finally
        {
            Cleanup(legacy, fresh);
        }
    }

    private static (string legacy, string fresh) CreateTempPair()
    {
        var stamp = Guid.NewGuid().ToString("N");
        return (
            Path.Combine(Path.GetTempPath(), "scribe-legacy-" + stamp),
            Path.Combine(Path.GetTempPath(), "scribe-new-" + stamp));
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
    }
}
