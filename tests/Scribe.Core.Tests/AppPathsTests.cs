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
    public void CreateForStartup_uses_fallback_when_preferred_root_is_blocked_by_a_file()
    {
        var stamp = Guid.NewGuid().ToString("N");
        var blockedRoot = Path.Combine(Path.GetTempPath(), "scribe-blocked-" + stamp);
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "scribe-fallback-" + stamp);

        try
        {
            File.WriteAllText(blockedRoot, "not a directory");

            var paths = AppPaths.CreateForStartup(blockedRoot, fallbackRoot);

            Assert.Equal(fallbackRoot, paths.RootDir);
            Assert.Equal(blockedRoot, paths.PreferredRootDir);
            Assert.True(paths.IsFallbackRoot);
            Assert.Null(paths.LegacyRootDir);
            Assert.Contains(blockedRoot, paths.CreationFailureMessage!);
            Assert.True(Directory.Exists(paths.LogsDir));
            Assert.True(Directory.Exists(paths.LibrariesDir));
        }
        finally
        {
            Cleanup(blockedRoot, fallbackRoot);
        }
    }

    [Fact]
    public void CreateForStartup_reports_a_fallback_root_left_behind_by_an_earlier_session()
    {
        // A session that recovers onto the preferred root must not leave the user silently running
        // two divergent copies of their history.
        var stamp = Guid.NewGuid().ToString("N");
        var preferredRoot = Path.Combine(Path.GetTempPath(), "scribe-preferred-" + stamp);
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "scribe-orphan-" + stamp);

        try
        {
            Directory.CreateDirectory(fallbackRoot);
            File.WriteAllText(Path.Combine(fallbackRoot, AppPaths.DatabaseFileName), "stranded history");

            var paths = AppPaths.CreateForStartup(preferredRoot, fallbackRoot);

            Assert.Equal(preferredRoot, paths.RootDir);
            Assert.False(paths.IsFallbackRoot);
            Assert.Equal(fallbackRoot, paths.OrphanedFallbackRootDir);
        }
        finally
        {
            Cleanup(preferredRoot, fallbackRoot);
        }
    }

    [Fact]
    public void CreateForStartup_ignores_an_empty_fallback_root()
    {
        // Directories left by a failed attempt carry nothing worth recovering, and warning about
        // them would train the user to ignore the warning that matters.
        var stamp = Guid.NewGuid().ToString("N");
        var preferredRoot = Path.Combine(Path.GetTempPath(), "scribe-preferred-" + stamp);
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "scribe-empty-" + stamp);

        try
        {
            Directory.CreateDirectory(fallbackRoot);

            var paths = AppPaths.CreateForStartup(preferredRoot, fallbackRoot);

            Assert.Null(paths.OrphanedFallbackRootDir);
        }
        finally
        {
            Cleanup(preferredRoot, fallbackRoot);
        }
    }

    [Fact]
    public void TryEnsureCreated_reports_failure_without_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "scribe-blocked-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(root, "not a directory");
            var paths = new AppPaths(root);

            var created = paths.TryEnsureCreated(out var exception);

            Assert.False(created);
            Assert.NotNull(exception);
            Assert.False(Directory.Exists(paths.LogsDir));
        }
        finally
        {
            Cleanup(root);
        }
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

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
    }
}
