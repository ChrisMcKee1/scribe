namespace Scribe.Core.Lifecycle;

/// <summary>
/// What the user is told when startup fails partway. Scribe closes rather than stay half started, which used to
/// leave a process nobody could see holding the single-instance lock.
/// </summary>
public static class StartupFailureNotice
{
    /// <summary>The dialog's title.</summary>
    public const string Title = "Scribe couldn't start";

    private const string Advice = "Try starting it again. If this keeps happening, restart your PC, then reinstall Scribe.";

    /// <summary>
    /// The dialog's text. Names the log file when one is being written, because Settings, where the log is
    /// normally found, is out of reach while Scribe cannot start.
    /// </summary>
    /// <param name="logFile">The log file the failure was written to, or null when logging is unavailable.</param>
    public static string Compose(string? logFile) =>
        string.IsNullOrWhiteSpace(logFile)
            ? $"Scribe couldn't start and will close.\n\n{Advice}"
            : $"Scribe couldn't start and will close.\n\n{Advice}\n\nIf you report this, attach the log file:\n{logFile.Trim()}";
}
