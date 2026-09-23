using Microsoft.Extensions.Logging;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// The shape of one entry in the shared daily log: <c>HH:mm:ss.fff [Level] Category: message</c>,
/// followed by the exception on the next lines when there is one.
/// <para>
/// The overlay's <c>OverlayLog</c> writes the same shape from its own process (it deliberately has no
/// Core reference), and <see cref="HistoricalLogRedaction"/> anchors to it, so the app side defines it
/// once here. The timestamp keeps the current culture's time separator on purpose: the overlay formats
/// it the same way, and changing only one side would make the two processes' lines disagree.
/// </para>
/// </summary>
public static class LogLineFormat
{
    /// <summary>Category the writer uses for its own notices (daily budget, dropped lines).</summary>
    public const string WriterCategory = "FileLoggerProvider";

    /// <summary>The last segment of a logger category, which is what each line shows.</summary>
    public static string ShortCategory(string? category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return string.Empty;
        }

        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }

    /// <summary>
    /// Formats one entry. The timestamp is supplied by the caller so a line that waits in a queue still
    /// records the moment it was logged rather than the moment it reached the disk.
    /// </summary>
    public static string Format(
        DateTime timestamp, LogLevel level, string shortCategory, string message, Exception? exception = null)
    {
        var line = $"{timestamp:HH:mm:ss.fff} [{level}] {shortCategory}: {message}";
        return exception is null ? line : line + Environment.NewLine + exception;
    }
}
