using System.IO.Compression;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class DiagnosticsBundleTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 20);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "scribe-bundle-test-" + Guid.NewGuid().ToString("N"));

    private string LogsDir => Path.Combine(_dir, "logs");

    public DiagnosticsBundleTests() => Directory.CreateDirectory(LogsDir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void WriteLog(DateOnly day, string content = "line") =>
        File.WriteAllText(ScribeLogFiles.PathFor(LogsDir, day), content);

    private string Destination() => Path.Combine(_dir, "bundle.zip");

    private static string[] EntryNames(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
    }

    [Fact]
    public void Bundle_contains_the_report_and_every_retained_log()
    {
        WriteLog(Today);
        WriteLog(Today.AddDays(-1));
        WriteLog(Today.AddDays(-6));

        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "environment report", Today);

        Assert.Equal(3, result.LogFileCount);
        Assert.Equal(
            ["logs/scribe-20260814.log", "logs/scribe-20260819.log", "logs/scribe-20260820.log", "report.txt"],
            EntryNames(result.Path));
    }

    [Fact]
    public void Bundle_never_reaches_outside_the_logs_folder()
    {
        // scribe.db holds every dictation the user has ever made and their saved API keys. A bundle
        // is meant to be attachable to a public issue, so the database must be impossible to sweep
        // in even when it sits one directory up from the logs.
        WriteLog(Today);
        File.WriteAllText(Path.Combine(_dir, "scribe.db"), "secrets");
        File.WriteAllText(Path.Combine(LogsDir, "settings.json"), "more secrets");

        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "report", Today);

        Assert.DoesNotContain(EntryNames(result.Path), n => n.Contains("scribe.db", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(EntryNames(result.Path), n => n.Contains("settings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Days_beyond_the_window_are_left_out()
    {
        WriteLog(Today);
        WriteLog(Today.AddDays(-30));

        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "report", Today);

        Assert.Equal(1, result.LogFileCount);
    }

    [Fact]
    public void A_log_the_app_is_writing_to_is_still_captured()
    {
        // The realistic case: the user exports while Scribe is running, so the current day's file is
        // open for append in this process and in the overlay. File.Copy would fail here.
        WriteLog(Today);
        var live = ScribeLogFiles.PathFor(LogsDir, Today);

        using (new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var result = DiagnosticsBundle.Create(LogsDir, Destination(), "report", Today);
            Assert.Equal(1, result.LogFileCount);
        }
    }

    [Fact]
    public void An_empty_log_folder_still_produces_a_readable_bundle()
    {
        // A user whose logs never got created is precisely the person filing this report. Handing
        // them an error instead of a bundle saying "no logs found" loses the environment report too.
        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "report", Today);

        Assert.Equal(0, result.LogFileCount);
        Assert.Equal(["report.txt"], EntryNames(result.Path));
    }

    [Fact]
    public void Inventory_reports_the_age_of_each_file()
    {
        WriteLog(Today.AddDays(-2), new string('x', 2048));

        var inventory = DiagnosticsBundle.DescribeLogInventory(LogsDir, Today);

        Assert.Contains("scribe-20260818.log", inventory);
        Assert.Contains("2 day(s) ago", inventory);
    }

    [Fact]
    public void Inventory_says_so_when_the_folder_is_empty()
    {
        Assert.Contains("No log files found", DiagnosticsBundle.DescribeLogInventory(LogsDir, Today));
    }

    [Fact]
    public void Suggested_names_are_unique_per_second_and_end_in_zip()
    {
        var name = DiagnosticsBundle.SuggestedFileName(new DateTimeOffset(2026, 8, 20, 14, 5, 9, TimeSpan.Zero));

        Assert.Equal("scribe-diagnostics-20260820-140509.zip", name);
    }
}
