namespace Scribe.Core.Infrastructure;

/// <summary>
/// Resolves and owns the per-user application directories. Everything writable lives under
/// <c>%LOCALAPPDATA%\Scribe</c>: the SQLite database, logs, and the installed model fallback.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "Scribe";

    public AppPaths(string? rootOverride = null)
    {
        // Resolution order: explicit override (tests) > SCRIBE_DATA_DIR env (isolated/portable
        // profiles, e.g. screenshot capture) > the per-user %LOCALAPPDATA%\Scribe known folder.
        // Mirrors the SCRIBE_MODELS_DIR override honoured by ModelLocator.
        var envOverride = Environment.GetEnvironmentVariable("SCRIBE_DATA_DIR");
        RootDir = rootOverride
            ?? (string.IsNullOrWhiteSpace(envOverride)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName)
                : envOverride);

        LogsDir = Path.Combine(RootDir, "logs");
        ModelsDir = Path.Combine(RootDir, "models");
        DatabasePath = Path.Combine(RootDir, "scribe.db");
    }

    /// <summary>Root writable directory (<c>%LOCALAPPDATA%\Scribe</c>).</summary>
    public string RootDir { get; }

    /// <summary>Log output directory.</summary>
    public string LogsDir { get; }

    /// <summary>Installed-model fallback location (see <see cref="ModelLocator"/>).</summary>
    public string ModelsDir { get; }

    /// <summary>Full path to the SQLite database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Creates the writable directories if they do not already exist.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogsDir);
    }
}
