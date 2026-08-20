using System.Globalization;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// The on-disk naming convention for Scribe's diagnostic logs, and the non-throwing helpers that
/// read a log folder.
/// <para>
/// One file per day, <c>scribe-yyyyMMdd.log</c>, shared by the WPF host and the out-of-process
/// overlay so a dictation and the overlay lifecycle it drove interleave in a single timeline.
/// <b>The overlay cannot reference this class</b> (Scribe.Overlay deliberately has no dependency on
/// Scribe.Core so it stays a standalone, self-contained exe), so <c>OverlayLog</c> carries its own
/// copy of the pattern. Changing the shape here means changing it there too, or the two processes
/// silently stop writing to the same file.
/// </para>
/// </summary>
public static class ScribeLogFiles
{
    /// <summary>Glob matching every daily log file, and nothing else in the folder.</summary>
    public const string SearchPattern = "scribe-????????.log";

    private const string FilePrefix = "scribe-";
    private const string FileExtension = ".log";
    private const string DayFormat = "yyyyMMdd";

    /// <summary>File name for a given day.</summary>
    public static string FileNameFor(DateOnly day) =>
        $"{FilePrefix}{day.ToString(DayFormat, CultureInfo.InvariantCulture)}{FileExtension}";

    /// <summary>Full path for a given day inside <paramref name="logsDirectory"/>.</summary>
    public static string PathFor(string logsDirectory, DateOnly day) =>
        Path.Combine(logsDirectory, FileNameFor(day));

    /// <summary>
    /// Reads the day out of a log file name. Returns <see langword="false"/> for anything that does
    /// not match the convention, so a sweep can never delete a file it did not write.
    /// </summary>
    public static bool TryParseDay(string fileName, out DateOnly day)
    {
        day = default;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);
        if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stamp = name[FilePrefix.Length..^FileExtension.Length];
        return DateOnly.TryParseExact(stamp, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>
    /// Lists the daily log files in a folder. Best effort and non-throwing: a missing folder, a
    /// file deleted between the listing and the stat, or a permission failure yields fewer entries
    /// rather than an exception, because every caller is a diagnostics path that must not fail.
    /// </summary>
    public static IReadOnlyList<LogFileEntry> Enumerate(string logsDirectory)
    {
        if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory))
        {
            return [];
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(logsDirectory, SearchPattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }

        var entries = new List<LogFileEntry>(paths.Length);
        foreach (var path in paths)
        {
            if (!TryParseDay(path, out var day))
            {
                continue;
            }

            try
            {
                entries.Add(new LogFileEntry(path, day, new FileInfo(path).Length));
            }
            catch
            {
                // Gone or unreadable between the listing and the stat; nothing to account for.
            }
        }

        return entries;
    }

    /// <summary>
    /// Applies <see cref="LogRetentionPolicy"/> to a folder and returns how many files were
    /// deleted. Non-throwing throughout: a file another process holds open is simply left for the
    /// next sweep.
    /// </summary>
    public static int Prune(
        string logsDirectory,
        DateOnly today,
        int retentionDays = LogRetentionPolicy.DefaultRetentionDays,
        long totalBudgetBytes = LogRetentionPolicy.DefaultTotalBudgetBytes)
    {
        var deleted = 0;
        try
        {
            var doomed = LogRetentionPolicy.SelectForDeletion(
                Enumerate(logsDirectory), today, retentionDays, totalBudgetBytes);

            foreach (var file in doomed)
            {
                try
                {
                    File.Delete(file.Path);
                    deleted++;
                }
                catch
                {
                    // Locked by a viewer or an antivirus scan; retried on the next sweep.
                }
            }
        }
        catch
        {
            // Retention is housekeeping. It must never be the reason the app fails to start.
        }

        return deleted;
    }
}
