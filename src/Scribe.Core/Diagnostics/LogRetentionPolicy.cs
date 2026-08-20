namespace Scribe.Core.Diagnostics;

/// <summary>
/// One log file the retention sweep can see.
/// </summary>
/// <param name="Path">Full path, used only to delete the file.</param>
/// <param name="Day">The day the file records, parsed from its name.</param>
/// <param name="Bytes">Size on disk.</param>
public sealed record LogFileEntry(string Path, DateOnly Day, long Bytes);

/// <summary>
/// Decides which diagnostic log files to delete. Pure and deterministic so the rules can be tested
/// without touching a disk, and so a user's log folder can never quietly grow without bound.
/// <para>
/// Two independent limits, because either one alone fails a real case. <b>Age</b> alone lets a
/// single pathological day (a driver fault looping every millisecond) fill a disk inside the
/// window. <b>Size</b> alone would silently throw away the week of context a user needs when they
/// take three days to get back to us with a bug report. Both together bound the folder while
/// keeping the most recent days intact.
/// </para>
/// </summary>
public static class LogRetentionPolicy
{
    /// <summary>
    /// Days of history kept, counting today. A week is the deliberate figure: users routinely take
    /// a few days to answer a request for logs, and a bug reported on Monday is usually one that
    /// happened over the weekend.
    /// </summary>
    public const int DefaultRetentionDays = 7;

    /// <summary>
    /// Ceiling for the whole log folder. Files are dropped oldest-first until the folder fits.
    /// Sized from real usage: an ordinary heavy day is well under a megabyte, so this is a runaway
    /// backstop rather than a limit normal use will ever reach.
    /// </summary>
    public const long DefaultTotalBudgetBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Ceiling for a single day's file. Past this the writer keeps only warnings and errors for the
    /// rest of that day (see the file logger), so one runaway loop cannot bury the whole week.
    /// </summary>
    public const long DefaultDailyBudgetBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Selects the files to delete. Today's file is never selected, whatever the totals say: it is
    /// the one file a user reporting a problem right now actually needs.
    /// </summary>
    /// <param name="files">Every log file found in the folder.</param>
    /// <param name="today">The current day, so the caller controls the clock.</param>
    /// <param name="retentionDays">Days to keep, counting today. Values below 1 are treated as 1.</param>
    /// <param name="totalBudgetBytes">Folder ceiling; non-positive disables the size sweep.</param>
    public static IReadOnlyList<LogFileEntry> SelectForDeletion(
        IEnumerable<LogFileEntry> files,
        DateOnly today,
        int retentionDays = DefaultRetentionDays,
        long totalBudgetBytes = DefaultTotalBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(files);

        var keepDays = Math.Max(1, retentionDays);
        var oldestKeptDay = today.AddDays(-(keepDays - 1));

        // Newest first. Both sweeps below walk from the end, which is the oldest file, so the day a
        // user is most likely to need is the last thing either sweep will consider dropping.
        var ordered = files.OrderByDescending(f => f.Day).ToList();

        var doomed = new List<LogFileEntry>();
        var survivors = new List<LogFileEntry>();
        foreach (var file in ordered)
        {
            // A file dated in the future (clock skew, a timezone change, a restored backup) is kept
            // rather than deleted: it is newer than the window, not older than it.
            if (file.Day < oldestKeptDay)
            {
                doomed.Add(file);
            }
            else
            {
                survivors.Add(file);
            }
        }

        if (totalBudgetBytes <= 0)
        {
            return doomed;
        }

        var total = survivors.Sum(f => f.Bytes);
        for (var i = survivors.Count - 1; i >= 0 && total > totalBudgetBytes; i--)
        {
            var candidate = survivors[i];
            if (candidate.Day >= today)
            {
                continue; // today's (or a future-dated) file is never sacrificed to the budget
            }

            doomed.Add(candidate);
            total -= candidate.Bytes;
        }

        return doomed;
    }
}
