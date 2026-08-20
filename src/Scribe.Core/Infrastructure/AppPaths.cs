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

        // Store builds up to 0.3.10 ran with AppData write virtualization on, so the ScribeData
        // folder they created was written into the package's LocalCache and never appeared at the
        // path the app reports. The manifest now excludes ScribeData from virtualization, which
        // means an updated Store install starts reading the real path and would otherwise look
        // brand new. This is where that data is carried forward from.
        VirtualizedRootDir = usingDefaultRoot
            ? WindowsPackageIdentity.TryGetVirtualizedLocalAppData(localAppData) is { } virtualLocal
                ? Path.Combine(virtualLocal, AppFolderName)
                : null
            : null;

        LogsDir = Path.Combine(RootDir, LogsFolderName);
        ModelsDir = Path.Combine(RootDir, "models");
        LibrariesDir = Path.Combine(RootDir, LibrariesFolderName);
        DatabasePath = Path.Combine(RootDir, DatabaseFileName);
        PreferredRootDir = RootDir;

        // Honest default until EnsureCreated probes for redirection. A caller that never creates
        // the directories still gets a usable path rather than an empty string.
        EffectiveRootDir = RootDir;
    }

    private AppPaths(string rootDir, string? legacyRootDir, string preferredRootDir, string creationFailureMessage)
    {
        RootDir = rootDir;
        LegacyRootDir = legacyRootDir;
        LogsDir = Path.Combine(RootDir, LogsFolderName);
        ModelsDir = Path.Combine(RootDir, "models");
        LibrariesDir = Path.Combine(RootDir, LibrariesFolderName);
        DatabasePath = Path.Combine(RootDir, DatabaseFileName);
        PreferredRootDir = preferredRootDir;
        CreationFailureMessage = creationFailureMessage;
        EffectiveRootDir = RootDir;
    }

    /// <summary>Root writable directory (<c>%LOCALAPPDATA%\ScribeData</c>).</summary>
    public string RootDir { get; }

    /// <summary>
    /// Legacy writable directory (<c>%LOCALAPPDATA%\Scribe</c>) used by builds that stored data in
    /// the Velopack install root. <c>null</c> when an explicit root or <c>SCRIBE_DATA_DIR</c> is in
    /// effect. Only used to migrate the database forward once.
    /// </summary>
    public string? LegacyRootDir { get; }

    /// <summary>
    /// Where a virtualized Store build's data physically landed
    /// (<c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\ScribeData</c>), or
    /// <see langword="null"/> when this process is unpackaged or running on an explicit root.
    /// Migrated forward once, then only reported so support can tell a user which folder an older
    /// build's logs are in.
    /// </summary>
    public string? VirtualizedRootDir { get; }

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

    /// <summary>Imported-library subfolder name, shared by the live path and the migration probes.</summary>
    public const string LibrariesFolderName = "libraries";

    /// <summary>Log subfolder name, shared by the live path and the outside-the-container path.</summary>
    public const string LogsFolderName = "logs";

    /// <summary>True when Scribe is using a fallback data root for this process.</summary>
    public bool IsFallbackRoot => CreationFailureMessage is not null;

    /// <summary>
    /// True when this process's writes under <see cref="RootDir"/> are being redirected by Windows
    /// into the package's private store. Determined by <b>probing</b>, not by inference: see
    /// <see cref="ResolveEffectiveRoot"/>.
    /// </summary>
    public bool WritesAreVirtualized { get; private set; }

    /// <summary>
    /// The root as it exists <b>outside</b> this process: the path a user can type into File
    /// Explorer, quote in a support thread, or back up. Equal to <see cref="RootDir"/> unless
    /// Windows is redirecting the app's writes, in which case it is the package's private store.
    /// <para>
    /// This is a <b>display and hand-off</b> path. Scribe's own file I/O must keep using
    /// <see cref="RootDir"/> and <see cref="LogsDir"/>, because inside the container those resolve
    /// through the merged view and are correct either way. Pointing internal I/O at the private
    /// store instead would work today and break the moment redirection is turned off.
    /// </para>
    /// </summary>
    public string EffectiveRootDir { get; private set; }

    /// <summary>Log folder as it exists outside this process. See <see cref="EffectiveRootDir"/>.</summary>
    public string EffectiveLogsDir => Path.Combine(EffectiveRootDir, LogsFolderName);

    /// <summary>Database file as it exists outside this process. See <see cref="EffectiveRootDir"/>.</summary>
    public string EffectiveDatabasePath => Path.Combine(EffectiveRootDir, DatabaseFileName);

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
        ResolveEffectiveRoot();
        if (LegacyRootDir is not null)
        {
            TryMigrateDatabase(LegacyRootDir, RootDir);
        }

        // Ordered after the legacy migration on purpose. Both are no-ops once a database exists at
        // the new root, so whichever source ran first wins and the second cannot overwrite it. A
        // machine that has been through both channels keeps the Velopack data, which is the copy
        // its user has been looking at all along.
        if (VirtualizedRootDir is not null)
        {
            TryMigrateDatabase(VirtualizedRootDir, RootDir);
            TryMigrateLibraries(Path.Combine(VirtualizedRootDir, LibrariesFolderName), LibrariesDir);
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

    /// <summary>
    /// Works out where this process's writes physically land, and records it in
    /// <see cref="EffectiveRootDir"/> / <see cref="WritesAreVirtualized"/>.
    /// <para>
    /// This is a <b>probe</b>, not a deduction, and that is the point. A packaged app cannot tell
    /// from its own paths whether Windows is redirecting its AppData writes: the merged read view
    /// hands back exactly the same path either way, which is precisely how a Store build shipped
    /// telling users to open a folder that was not there. Whether redirection applies depends on
    /// the package manifest, the OS build, and whether the folder already existed, so anything
    /// short of writing a file and looking for it is a guess.
    /// </para>
    /// <para>
    /// Cost is one file create, one existence check and one delete per launch, all best effort. On
    /// an unpackaged build the whole thing short-circuits before touching the disk.
    /// </para>
    /// </summary>
    private void ResolveEffectiveRoot()
    {
        EffectiveRootDir = RootDir;
        WritesAreVirtualized = false;

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (ComposeVirtualTwin(RootDir, localAppData) is not { } twin)
            {
                return; // unpackaged, or a root Windows does not virtualize
            }

            // A unique name, so a leftover marker from a previous launch (or from the other
            // architecture's build running side by side) can never be mistaken for this one's.
            var markerName = $".scribe-virtualization-probe-{Guid.NewGuid():N}";
            var marker = Path.Combine(RootDir, markerName);
            try
            {
                File.WriteAllBytes(marker, []);
                if (File.Exists(Path.Combine(twin, markerName)))
                {
                    EffectiveRootDir = twin;
                    WritesAreVirtualized = true;
                }
            }
            finally
            {
                TryDelete(marker);
            }
        }
        catch
        {
            // A failed probe leaves the honest default: report the path the app itself uses. Worst
            // case a packaged user is shown the path they were shown before this existed.
        }
    }

    /// <summary>
    /// The location Windows redirects writes to <paramref name="rootDir"/> into, or
    /// <see langword="null"/> when this process is unpackaged or the root is not under
    /// <paramref name="localAppData"/> (only AppData is virtualized).
    /// </summary>
    private static string? ComposeVirtualTwin(string rootDir, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData)
            || WindowsPackageIdentity.TryGetVirtualizedLocalAppData(localAppData) is not { } virtualLocal)
        {
            return null;
        }

        var prefix = localAppData.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!rootDir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Redirection preserves the path relative to LocalAppData, so ScribeData.fallback and any
        // other sibling root map across without needing to be enumerated here.
        return Path.Combine(virtualLocal, rootDir[prefix.Length..]);
    }

    /// <summary>
    /// One-time, best-effort copy of imported dictionary-library CSVs from a previous data root.
    /// Copies only files the destination does not already have, so it never overwrites a library
    /// the user has edited since, and re-running it is harmless.
    /// </summary>
    internal static void TryMigrateLibraries(string legacyLibrariesDir, string newLibrariesDir)
    {
        if (string.Equals(legacyLibrariesDir, newLibrariesDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(legacyLibrariesDir))
            {
                return;
            }

            Directory.CreateDirectory(newLibrariesDir);
            foreach (var source in Directory.GetFiles(legacyLibrariesDir, "*.csv", SearchOption.TopDirectoryOnly))
            {
                var destination = Path.Combine(newLibrariesDir, Path.GetFileName(source));
                if (!File.Exists(destination))
                {
                    File.Copy(source, destination);
                }
            }
        }
        catch
        {
            // Best-effort: the libraries are re-importable and must never block startup.
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
