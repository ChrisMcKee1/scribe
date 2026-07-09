using Microsoft.Data.Sqlite;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Resolves and owns the per-user application directories. Everything writable lives under
/// <c>%LOCALAPPDATA%\ScribeData</c>: the SQLite database, logs, and the installed model fallback.
/// <para>
/// This folder is deliberately <b>separate</b> from the Velopack install root
/// (<c>%LOCALAPPDATA%\Scribe</c>). Re-running the installer over an existing install renames the
/// whole install root aside and deletes it once the new version is in place, so storing the
/// database there would wipe the user's settings, dictionary, and history on every reinstall.
/// Keeping data in a sibling folder Velopack never touches lets installs/updates preserve it.
/// A one-time migration (<see cref="EnsureCreated"/>) carries data forward from the legacy root.
/// </para>
/// </summary>
public sealed class AppPaths
{
    /// <summary>Writable data folder name (sibling of the Velopack install root).</summary>
    public const string AppFolderName = "ScribeData";

    /// <summary>Legacy data folder name — the Velopack install root that data used to share.</summary>
    public const string LegacyAppFolderName = "Scribe";

    public AppPaths(string? rootOverride = null)
    {
        // Resolution order: explicit override (tests) > SCRIBE_DATA_DIR env (isolated/portable
        // profiles, e.g. screenshot capture) > the per-user %LOCALAPPDATA%\ScribeData known folder.
        // Mirrors the SCRIBE_MODELS_DIR override honoured by ModelLocator.
        var envOverride = Environment.GetEnvironmentVariable("SCRIBE_DATA_DIR");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var usingDefaultRoot = rootOverride is null && string.IsNullOrWhiteSpace(envOverride);

        RootDir = rootOverride
            ?? (string.IsNullOrWhiteSpace(envOverride)
                ? Path.Combine(localAppData, AppFolderName)
                : envOverride);

        // Only consider the legacy Velopack-install-root location when running with the real default
        // root. Explicit overrides are self-contained and must never pull in unrelated legacy data.
        LegacyRootDir = usingDefaultRoot
            ? Path.Combine(localAppData, LegacyAppFolderName)
            : null;

        LogsDir = Path.Combine(RootDir, "logs");
        ModelsDir = Path.Combine(RootDir, "models");
        LibrariesDir = Path.Combine(RootDir, "libraries");
        DatabasePath = Path.Combine(RootDir, "scribe.db");
    }

    /// <summary>Root writable directory (<c>%LOCALAPPDATA%\ScribeData</c>).</summary>
    public string RootDir { get; }

    /// <summary>
    /// Legacy writable directory (<c>%LOCALAPPDATA%\Scribe</c>) used by builds that stored data in
    /// the Velopack install root. <c>null</c> when an explicit root or <c>SCRIBE_DATA_DIR</c> is in
    /// effect. Only used to migrate the database forward once.
    /// </summary>
    public string? LegacyRootDir { get; }

    /// <summary>Log output directory.</summary>
    public string LogsDir { get; }

    /// <summary>Installed-model fallback location (see <see cref="ModelLocator"/>).</summary>
    public string ModelsDir { get; }

    /// <summary>Imported custom dictionary libraries (one CSV per library).</summary>
    public string LibrariesDir { get; }

    /// <summary>Full path to the SQLite database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Creates the writable directories if they do not already exist.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(LibrariesDir);
        if (LegacyRootDir is not null)
        {
            TryMigrateDatabase(LegacyRootDir, RootDir);
        }
    }

    /// <summary>
    /// One-time, best-effort migration of the SQLite database from the legacy data root (the
    /// Velopack install directory) to the dedicated data folder. Copies only when the destination
    /// database does not yet exist but a legacy one does, so it never overwrites current data and is
    /// a no-op on every subsequent launch.
    /// </summary>
    internal static void TryMigrateDatabase(string legacyRoot, string newRoot)
    {
        if (string.Equals(legacyRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var newDb = Path.Combine(newRoot, "scribe.db");
        if (File.Exists(newDb))
        {
            return;
        }

        var legacyDb = Path.Combine(legacyRoot, "scribe.db");
        if (!File.Exists(legacyDb))
        {
            return;
        }

        var stagedDb = Path.Combine(newRoot, $".scribe-migration-{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(newRoot);
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = legacyDb,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = stagedDb,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();

            // SQLite's online backup API folds committed WAL data into one consistent staged file.
            // Publishing that file only after backup succeeds makes the destination main file a
            // reliable completion marker, so an interrupted attempt is retried next launch.
            using (var source = new SqliteConnection(sourceConnectionString))
            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            File.Move(stagedDb, newDb);
        }
        catch
        {
            // Best-effort: leave the destination absent so the next launch retries migration.
            TryDelete(stagedDb);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best-effort; the unique staging name cannot block a later retry.
        }
    }
}
