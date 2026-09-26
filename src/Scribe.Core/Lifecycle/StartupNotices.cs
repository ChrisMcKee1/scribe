namespace Scribe.Core.Lifecycle;

public sealed record StartupNotice(string Title, string Body);

public static class StartupNotices
{
    public static StartupNotice AlreadyRunning() => new("Scribe is already running", "Select the Scribe icon in the system tray, next to the clock, to open Settings. If you don't see it, select Show hidden icons (the up arrow) first.");

    public static StartupNotice DataFolderProblem(string? folder = null) => new("Scribe can't start", $"Scribe couldn't create the folder where it keeps your settings and dictations, so it has to close.{Environment.NewLine}{Environment.NewLine}{folder ?? string.Empty}{Environment.NewLine}{Environment.NewLine}Check that the disk has free space and that folder permissions or antivirus rules aren't blocking it, then start Scribe again.");

    public static StartupNotice TemporaryDataFolder(string preferred, string fallback) => new("Scribe is using a temporary data folder", $"Scribe couldn't use its usual data folder:{Environment.NewLine}{preferred}{Environment.NewLine}{Environment.NewLine}Until that's fixed, your settings, dictations and logs are kept here:{Environment.NewLine}{fallback}{Environment.NewLine}{Environment.NewLine}Check that the disk has free space and that folder permissions or antivirus rules aren't blocking the usual folder. Settings, Diagnostics shows both folders.");

    public static StartupNotice NewerDatabase() => new("Scribe needs an update", "Your Scribe data was saved by a newer version of Scribe, so this version can't open it. Install the latest version of Scribe, then start it again.");

    public static StartupNotice StartupFailure(string? logFile = null)
    {
        const string body = "Scribe couldn't start and will close.\n\nTry starting it again. If this keeps happening, restart your PC, then reinstall Scribe.";
        return string.IsNullOrWhiteSpace(logFile)
            ? new StartupNotice("Scribe couldn't start", body)
            : new StartupNotice("Scribe couldn't start", body + $"\n\nIf you report this, attach the log file:\n{logFile}");
    }
}
