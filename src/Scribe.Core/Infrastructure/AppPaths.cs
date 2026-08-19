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

    /// <summary>Legacy data folder name: the Velopack install root that data used to share.</summary>
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
        DatabasePath = Path.Combine(RootDir, DatabaseFileName);
        PreferredRootDir = RootDir;
    }

    private AppPaths(string rootDir, string? legacyRootDir, string preferredRootDir, string creationFailureMessage)
    {
        RootDir = rootDir;
        LegacyRootDir = legacyRootDir;
        LogsDir = Path.Combine(RootDir, "logs");
        ModelsDir = Path.Combine(RootDir, "models");
        LibrariesDir = Path.Combine(RootDir, "libraries");
        DatabasePath = Path.Combine(RootDir, DatabaseFileName);
        PreferredRootDir = preferredRootDir;
        CreationFailureMessage = creationFailureMessage;
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

    /// <summary>The root Scribe first tried to use before falling back.</summary>
    public string PreferredRootDir { get; }

    /// <summary>Startup failure that forced the app onto a fallback root, if any.</summary>
    public string? CreationFailureMessage { get; }

    /// <summary>Fallback data folder name, used when the preferred root cannot be created.</summary>
    public const string FallbackAppFolderName = "ScribeData.fallback";

    /// <summary>SQLite database file name, shared by the live path and the migration probes.</summary>
    public const string DatabaseFileName = "scribe.db";

    /// <summary>True when Scribe is using a fallback data root for this process.</summary>
    public bool IsFallbackRoot => CreationFailureMessage is not null;

    /// <summary>
    /// Creates startup paths, falling back to a sibling folder when the preferred root fails.
    /// </summary>
    public static AppPaths CreateForStartup(string? rootOverride = null, string? fallbackRootOverride = null)
    {
        var preferred = new AppPaths(rootOverride);
        if (preferred.TryEnsureCreated(out var preferredFailure))
        {
            preferred.OrphanedFallbackRootDir = FindOrphanedFallback(rootOverride, fallbackRootOverride);
            return preferred;
        }

        // Deliberately NOT under Path.GetTempPath(): that resolves inside %LOCALAPPDATA%\Temp, which
        // Storage Sense and Disk Cleanup are entitled to empty. A fallback session still writes the
        // dictation database, the dictionary, and the encrypted API key, so putting them somewhere
        // Windows may delete would turn a transient folder failure into silent data loss.
        var fallbackRoot = fallbackRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FallbackAppFolderName);
        var fallback = new AppPaths(
            fallbackRoot,
            legacyRootDir: null,
            preferred.RootDir,
            FormatCreationFailure(preferred.RootDir, preferredFailure));

        if (fallback.TryEnsureCreated(out var fallbackFailure))
        {
            return fallback;
        }

        throw new AppPathsCreationException(
            preferred.RootDir,
            fallback.RootDir,
            preferredFailure,
            fallbackFailure);
    }

    /// <summary>
    /// A fallback root left behind by an earlier session that has data in it, when this session is
    /// running on the preferred root. Non-null means the user has dictation history, dictionary
    /// entries, or settings stranded in a second location and should be told, rather than quietly
    /// left with two divergent copies.
    /// </summary>
    public string? OrphanedFallbackRootDir { get; private set; }

    /// <summary>
    /// Looks for a fallback root containing real data. Best effort and non-throwing: this runs
    /// during startup before logging exists, so a probe failure must never be the thing that stops
    /// the app from launching.
    /// </summary>
    private static string? FindOrphanedFallback(string? rootOverride, string? fallbackRootOverride)
    {
        // An explicit root is a self-contained profile (tests, portable installs) and must not be
        // told about an unrelated fallback belonging to the normal installation.
        if (rootOverride is not null && fallbackRootOverride is null)
        {
            return null;
        }

        try
        {
            var fallbackRoot = fallbackRootOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FallbackAppFolderName);

            if (!Directory.Exists(fallbackRoot))
            {
                return null;
            }

            // The database is the only thing worth recovering; empty directories from a failed
            // attempt are noise and reporting them would train the user to ignore the warning.
            return File.Exists(Path.Combine(fallbackRoot, DatabaseFileName)) ? fallbackRoot : null;
        }
        catch
        {
            return null;
        }
    }

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

    /// <summary>Tries to create the writable directories and captures the startup error.</summary>
    public bool TryEnsureCreated(out Exception? exception)
    {
        try
        {
            EnsureCreated();
            exception = null;
            return true;
        }
        catch (Exception ex)
        {
            exception = ex;
            return false;
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

        var newDb = Path.Combine(newRoot, DatabaseFileName);
        if (File.Exists(newDb))
        {
            return;
        }

        var legacyDb = Path.Combine(legacyRoot, DatabaseFileName);
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

    private static string FormatCreationFailure(string rootDir, Exception? exception)
    {
        if (exception is null)
        {
            return $"Scribe could not create {rootDir}.";
        }

        return $"Scribe could not create {rootDir}. {exception.GetType().Name}: {exception.Message}";
    }
}

public sealed class AppPathsCreationException : Exception
{
    public AppPathsCreationException(
        string preferredRootDir,
        string fallbackRootDir,
        Exception? preferredFailure,
        Exception? fallbackFailure)
        : base(
            $"Scribe could not create its data folder at {preferredRootDir} or fallback folder at {fallbackRootDir}.",
            fallbackFailure ?? preferredFailure)
    {
        PreferredRootDir = preferredRootDir;
        FallbackRootDir = fallbackRootDir;
        PreferredFailure = preferredFailure;
        FallbackFailure = fallbackFailure;
    }

    public string PreferredRootDir { get; }

    public string FallbackRootDir { get; }

    public Exception? PreferredFailure { get; }

    public Exception? FallbackFailure { get; }
}
