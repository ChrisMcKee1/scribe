using System.Globalization;
using System.IO.Compression;

namespace Scribe.Core.Diagnostics;

/// <summary>Outcome of writing a diagnostics bundle.</summary>
/// <param name="Path">Where the zip was written.</param>
/// <param name="LogFileCount">How many daily log files it contains.</param>
/// <param name="Bytes">Size of the finished zip.</param>
public sealed record DiagnosticsBundleResult(string Path, int LogFileCount, long Bytes);

/// <summary>
/// Packs the diagnostic logs into a single zip the user can attach to a bug report.
/// <para>
/// This is the answer to the support conversation that motivated the whole area: a user was asked
/// to open <c>%LOCALAPPDATA%\ScribeData\logs</c>, the folder was not there, and the thread ended
/// with no diagnostics at all. Asking a person to navigate a hidden folder is fragile for reasons
/// that have nothing to do with them; handing them one file, saved wherever they chose, is not.
/// </para>
/// <para>
/// <b>The database is never included.</b> <c>scribe.db</c> holds every dictation the user has ever
/// made and their saved API keys. A bundle is meant to be attached to a public issue, so the only
/// things in it are the log files and a plain-text report the user can read before they send it.
/// </para>
/// </summary>
public static class DiagnosticsBundle
{
    /// <summary>Days of logs to include. Matches what retention keeps, so it is always everything.</summary>
    public const int DefaultDays = LogRetentionPolicy.DefaultRetentionDays;

    /// <summary>Suggested file name, timestamped so repeated exports do not overwrite each other.</summary>
    public static string SuggestedFileName(DateTimeOffset now) =>
        $"scribe-diagnostics-{now:yyyyMMdd-HHmmss}.zip";

    /// <summary>
    /// Writes the bundle. Throws only on a failure to create the destination file, which the caller
    /// surfaces to the user: unlike the logging path, this runs because a person pressed a button
    /// and a silent no-op would be worse than an error message.
    /// </summary>
    /// <param name="logsDirectory">Folder holding the daily log files.</param>
    /// <param name="destinationPath">Full path of the zip to create; overwritten if it exists.</param>
    /// <param name="report">Plain-text environment report, written as <c>report.txt</c>.</param>
    /// <param name="today">Current day, so the day window is testable.</param>
    /// <param name="days">How many days back to include, counting today.</param>
    public static DiagnosticsBundleResult Create(
        string logsDirectory,
        string destinationPath,
        string report,
        DateOnly today,
        int days = DefaultDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var oldest = today.AddDays(-(Math.Max(1, days) - 1));
        var included = ScribeLogFiles.Enumerate(logsDirectory)
            .Where(f => f.Day >= oldest)
            .OrderBy(f => f.Day)
            .ToList();

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var copied = 0;
        using (var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, "report.txt", report ?? string.Empty);

            foreach (var file in included)
            {
                // Both Scribe processes hold this file open for append in bursts, so the copy has to
                // share write access. File.Copy does not, and would fail exactly when the app is
                // busy, which is when a user is most likely to be exporting.
                try
                {
                    var entry = archive.CreateEntry($"logs/{Path.GetFileName(file.Path)}", CompressionLevel.Optimal);
                    using var source = new FileStream(
                        file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var target = entry.Open();
                    source.CopyTo(target);
                    copied++;
                }
                catch (Exception ex)
                {
                    // One unreadable day must not cost the user the other six.
                    WriteTextEntry(
                        archive,
                        $"logs/{Path.GetFileNameWithoutExtension(file.Path)}.unreadable.txt",
                        $"This log file could not be read when the bundle was created.{Environment.NewLine}{ex}");
                }
            }
        }

        var bytes = new FileInfo(destinationPath).Length;
        return new DiagnosticsBundleResult(destinationPath, copied, bytes);
    }

    /// <summary>
    /// Builds the inventory section appended to the report: what is in the folder, how big, and how
    /// far back it goes. A user who reports a problem from last week needs to know at a glance
    /// whether the day in question is still there.
    /// </summary>
    public static string DescribeLogInventory(string logsDirectory, DateOnly today)
    {
        var files = ScribeLogFiles.Enumerate(logsDirectory).OrderByDescending(f => f.Day).ToList();
        if (files.Count == 0)
        {
            return $"No log files found in {logsDirectory}.";
        }

        var total = files.Sum(f => f.Bytes);
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"{files.Count} log file(s), {total / 1024.0 / 1024:F2} MB total, in {logsDirectory}"),
        };

        foreach (var file in files)
        {
            var age = today.DayNumber - file.Day.DayNumber;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  {Path.GetFileName(file.Path),-24} {file.Bytes / 1024.0,10:F1} KB  {age} day(s) ago"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
