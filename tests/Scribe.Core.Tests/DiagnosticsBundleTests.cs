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

    [Fact]
    public void Known_sensitive_lines_are_redacted_in_the_copy_and_the_report_counts_them()
    {
        // Synthetic lines in the exact shapes earlier builds wrote: a Debug decode line carrying the
        // transcript, a cleanup skip warning naming a custom endpoint's host, an invalid snippet warning
        // with its exception, and a discovery line naming an Azure account.
        var decode = "09:15:02.001 [Debug] TranscriptionService: Decoded 3100 ms of audio in 140 ms (RTF 0.05): " +
            "\"my account number is 4417 1234\"";
        var skip = "09:15:02.200 [Warning] DictationController: AI cleanup was skipped for this dictation: " +
            "AI cleanup is enabled but Initializing (Connecting to llm.contoso.internal\u2026). The raw transcription was used.";
        var snippet = "09:15:03.000 [Warning] TextPostProcessor: Skipping invalid snippet 4 ('merger memo')." +
            Environment.NewLine + "System.ArgumentException: merger memo" + Environment.NewLine + "   at X.Y()";
        var account = "09:15:04.000 [Debug] AzureFoundryDiscovery: Could not list deployments for account fabrikam-ai.";
        WriteLog(Today.AddDays(-1), string.Join(Environment.NewLine, decode, skip, snippet, account) + Environment.NewLine);
        WriteLog(Today, "09:20:00.000 [Information] App: ordinary" + Environment.NewLine);

        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "environment report", Today);

        var yesterday = ReadEntry(result.Path, "logs/scribe-20260819.log");
        Assert.DoesNotContain("4417", yesterday);
        Assert.DoesNotContain("contoso", yesterday);
        Assert.DoesNotContain("merger", yesterday);
        Assert.DoesNotContain("fabrikam", yesterday);
        Assert.Contains("(RTF 0.05): \"[transcript redacted, 30 chars]\"", yesterday);
        Assert.Contains("   at X.Y()", yesterday);
        Assert.Equal(new LogRedactionCounts(1, 1, 1, 2, 0), result.Redactions);

        var report = ReadEntry(result.Path, "report.txt");
        Assert.StartsWith("environment report", report);
        Assert.Contains("--- privacy redaction ---", report);
        Assert.Contains("This is not a general scan", report);
        Assert.Contains("dictation text replaced: 1", report);
        Assert.Contains("custom AI endpoint addresses replaced: 1", report);
        Assert.Contains("Azure deployment, account and subscription names replaced: 1", report);
        Assert.Contains("dictionary, snippet, dictionary library and profile text replaced: 2", report);
        Assert.Contains("AI provider and failure text replaced: 0", report);
        Assert.Contains("  - speech decoding lines from TranscriptionService (Debug), versions 0.3.11 to 0.4.2", report);
        Assert.DoesNotContain("4417", report);
        Assert.DoesNotContain("contoso", report);
    }

    [Fact]
    public void The_report_lists_every_recognized_format_with_its_versions()
    {
        var report = DiagnosticsBundle.WithRedactionSummary("r", default);

        foreach (var format in HistoricalLogRedaction.KnownFormats)
        {
            Assert.Contains($"  - {format.Lines}, versions {format.Versions}", report);
        }
    }

    [Fact]
    public void Settings_warnings_from_earlier_versions_lose_what_their_exceptions_quoted_in_the_copy()
    {
        // The two Settings shapes 0.2.4 to 0.4.2 wrote: an API-key check against an endpoint behind a VPN that was
        // down, whose connection failure named the host, and a report mailto: no mail client would open.
        var verify = "10:01:02.003 [Warning] SettingsWindow: Could not reach the Azure API-key endpoint." + Environment.NewLine +
            "System.Net.Http.HttpRequestException: No such host is known. (contoso-ai.openai.azure.com:443)" + Environment.NewLine +
            " ---> System.Net.Sockets.SocketException (11001): No such host is known." + Environment.NewLine +
            "   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port)";
        var mail = "10:05:00.000 [Warning] SettingsWindow: Could not open a mail client for the AI report." + Environment.NewLine +
            "System.ComponentModel.Win32Exception (1155): An error occurred trying to start process " +
            "'mailto:support@mckeesolutions.ai?subject=x&body=my%20account%20number%20is%204417' with working directory 'C:\\x'." +
            Environment.NewLine + "   at System.Diagnostics.Process.StartWithShellExecuteEx(ProcessStartInfo startInfo)";
        var local = "10:06:00.000 [Warning] SettingsWindow: Could not load the dictionary for Settings." + Environment.NewLine +
            "Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 5: 'database is locked'.";
        WriteLog(Today.AddDays(-1), string.Join(Environment.NewLine, verify, mail, local) + Environment.NewLine);

        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "environment report", Today);

        var copy = ReadEntry(result.Path, "logs/scribe-20260819.log");
        Assert.DoesNotContain("contoso", copy);
        Assert.DoesNotContain("4417", copy);
        Assert.Contains("SettingsWindow: Could not reach the Azure API-key endpoint.", copy);
        Assert.Contains($"System.Net.Http.HttpRequestException: {HistoricalLogRedaction.MessagePlaceholder}", copy);
        Assert.Contains("   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port)", copy);
        Assert.Contains("SQLite Error 5: 'database is locked'.", copy);
        Assert.Equal(new LogRedactionCounts(1, 0, 0, 0, 1), result.Redactions);

        var report = ReadEntry(result.Path, "report.txt");
        Assert.Contains("dictation text replaced: 1", report);
        Assert.Contains("AI provider and failure text replaced: 1", report);
        Assert.Contains("Settings warnings about Azure sign-in", report);
    }

    [Fact]
    public void Logs_without_a_known_format_are_copied_byte_for_byte()
    {
        var content = "09:20:00.000 [Information] App: ordinary\r\n09:20:01.000 [Warning] Overlay: pill\n" +
            "no terminator on the last line";
        WriteLog(Today, content);

        var result = DiagnosticsBundle.Create(LogsDir, Destination(), "report", Today);

        Assert.Equal(content, ReadEntry(result.Path, "logs/scribe-20260820.log"));
        Assert.Equal(default, result.Redactions);
        Assert.Contains("dictation text replaced: 0", ReadEntry(result.Path, "report.txt"));
    }

    private static string ReadEntry(string zipPath, string entryName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open());
        return reader.ReadToEnd();
    }
}
