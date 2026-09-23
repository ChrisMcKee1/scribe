using System.Text;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

public class DailyLogFileTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 21);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "scribe-dailylog-test-" + Guid.NewGuid().ToString("N"));

    // The clock the file under test reads. Tests move it; nothing sleeps or waits for midnight.
    private DateTime _now = Day.ToDateTime(new TimeOnly(9, 0));

    public DailyLogFileTests() => Directory.CreateDirectory(_root);

    private string LogsDir => Path.Combine(_root, "logs");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static LogRecord At(DateOnly day, int hour, int minute, string text, LogLevel level = LogLevel.Information) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), level, text);

    private DailyLogFile Open(
        long dailyBudgetBytes = LogRetentionPolicy.DefaultDailyBudgetBytes,
        Action<LogDayChange>? dayChanged = null,
        string? preferred = null,
        string? fallback = null) =>
        DailyLogFile.Open(
            preferred ?? LogsDir, fallback, dailyBudgetBytes, dayChanged, retryDelay: TimeSpan.Zero, clock: () => _now);

    private string PathFor(DateOnly day, string? directory = null) => ScribeLogFiles.PathFor(directory ?? LogsDir, day);

    private string[] Lines(DateOnly day, string? directory = null) => File.ReadAllLines(PathFor(day, directory));

    [Fact]
    public void Opening_creates_todays_file_and_reports_it()
    {
        var file = Open();

        Assert.True(file.Status.Healthy);
        Assert.Equal(PathFor(Day), file.Status.Path);
        Assert.Equal(string.Empty, file.Status.Reason);
        Assert.Equal(Day, file.Day);
        Assert.True(File.Exists(PathFor(Day)));
    }

    [Fact]
    public void Midnight_starts_a_new_file_and_reports_it()
    {
        var changes = new List<LogDayChange>();
        var file = Open(dayChanged: changes.Add);

        _now = Day.ToDateTime(new TimeOnly(23, 59));
        file.Write([At(Day, 23, 59, "late")]);
        _now = Day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        file.Write([At(Day.AddDays(1), 0, 0, "early")]);

        Assert.Equal(["late"], Lines(Day));
        Assert.Equal(["early"], Lines(Day.AddDays(1)));
        Assert.Equal([new LogDayChange(LogsDir, Day, Day.AddDays(1))], changes);
        Assert.Equal(PathFor(Day.AddDays(1)), file.Status.Path);
        Assert.Equal(Day.AddDays(1), file.Day);
    }

    [Fact]
    public void At_midnight_the_scrub_may_not_touch_the_new_day_or_the_day_just_left()
    {
        var changes = new List<LogDayChange>();
        var file = Open(dayChanged: changes.Add);

        _now = Day.AddDays(1).ToDateTime(new TimeOnly(0, 0, 5));
        file.Write([At(Day.AddDays(1), 0, 0, "after midnight")]);

        // The first day NOT to touch is the one just left: the overlay may still be finishing a line in it.
        Assert.Equal(Day, Assert.Single(changes).ScrubBefore);
    }

    [Fact]
    public void When_the_clock_moves_back_the_scrub_still_spares_both_files()
    {
        var changes = new List<LogDayChange>();
        var file = Open(dayChanged: changes.Add);

        _now = Day.AddDays(-1).ToDateTime(new TimeOnly(23, 30));
        file.Write([At(Day.AddDays(-1), 23, 30, "after the clock moved back")]);

        var change = Assert.Single(changes);
        Assert.Equal(new LogDayChange(LogsDir, Day, Day.AddDays(-1)), change);
        Assert.Equal(Day.AddDays(-1), change.ScrubBefore);
    }

    [Fact]
    public void A_line_logged_just_before_midnight_and_written_after_it_joins_the_new_day()
    {
        // The day is chosen when the line is written, which is how the overlay chooses its file too.
        var file = Open();

        _now = Day.AddDays(1).ToDateTime(new TimeOnly(0, 0, 1));
        file.Write([At(Day, 23, 59, "logged before midnight")]);

        Assert.Equal(["logged before midnight"], Lines(Day.AddDays(1)));
        Assert.Empty(Lines(Day));
    }

    [Fact]
    public void The_file_follows_the_clock_back_the_way_the_overlay_does()
    {
        // A time zone change or a corrected clock can move local time back across midnight. The overlay
        // picks its file from the clock on every write, so the app must as well, or their lines split
        // across two files for as long as the clock stays behind.
        var changes = new List<LogDayChange>();
        var file = Open(dayChanged: changes.Add);

        _now = Day.AddDays(1).ToDateTime(new TimeOnly(0, 5));
        file.Write([At(Day.AddDays(1), 0, 5, "after midnight")]);
        _now = Day.ToDateTime(new TimeOnly(23, 10));
        file.Write([At(Day, 23, 10, "after the clock moved back")]);

        Assert.Equal(["after midnight"], Lines(Day.AddDays(1)));
        Assert.Equal(["after the clock moved back"], Lines(Day));
        Assert.Equal([Day.AddDays(1), Day], changes.Select(c => c.Day));
        Assert.Equal(Day, file.Day);
    }

    [Fact]
    public void Rolling_to_a_new_day_sweeps_files_that_have_aged_out()
    {
        var file = Open();

        // Inside the window when the file was opened, outside it once the day rolls.
        var aging = PathFor(Day.AddDays(-6));
        File.WriteAllText(aging, "old");
        var kept = PathFor(Day.AddDays(-5));
        File.WriteAllText(kept, "recent");

        _now = Day.AddDays(1).ToDateTime(new TimeOnly(0, 1));
        file.Write([At(Day.AddDays(1), 0, 1, "first line of the new day")]);

        Assert.False(File.Exists(aging));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void Past_its_daily_budget_only_warnings_get_through_and_the_notice_is_written_once()
    {
        var file = Open(dailyBudgetBytes: 100);
        var filler = new string('x', 60);

        file.Write([
            At(Day, 9, 0, filler),
            At(Day, 9, 1, filler),
            At(Day, 9, 2, "dropped info"),
            At(Day, 9, 3, "kept warning", LogLevel.Warning),
            At(Day, 9, 4, "dropped too"),
            At(Day, 9, 5, "kept error", LogLevel.Error),
        ]);

        var lines = Lines(Day);
        Assert.Equal(filler, lines[0]);
        Assert.Equal(filler, lines[1]);
        Assert.Contains("[Warning] FileLoggerProvider: Today's log passed its", lines[2]);
        Assert.Equal("kept warning", lines[3]);
        Assert.Equal("kept error", lines[4]);
        Assert.Equal(5, lines.Length);
        Assert.Single(lines, l => l.Contains("passed its", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_process_can_hold_the_file_open_and_append_between_batches()
    {
        // The overlay process appends to the same file, opening it with FileShare.ReadWrite exactly as
        // this writer does. Neither may lock the other out, and neither may write over the other.
        var file = Open();
        var path = PathFor(Day);

        using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            file.Write([At(Day, 10, 0, "app line while the overlay holds the file")]);
        }

        AppendLikeTheOverlay(path, "overlay line");
        file.Write([At(Day, 10, 1, "app line after the overlay")]);
        AppendLikeTheOverlay(path, "second overlay line");

        Assert.Equal(
            ["app line while the overlay holds the file", "overlay line", "app line after the overlay", "second overlay line"],
            Lines(Day));
    }

    [Fact]
    public void A_file_locked_against_writers_is_retried_then_skipped_without_throwing()
    {
        var file = Open();
        var path = PathFor(Day);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            file.Write([At(Day, 11, 0, "lost to the lock")]);
        }

        file.Write([At(Day, 11, 1, "written once the lock is gone")]);

        Assert.Equal(["written once the lock is gone"], Lines(Day));
    }

    [Fact]
    public void An_unusable_preferred_folder_falls_back_and_says_why()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "a file where the log folder should be");
        var fallback = Path.Combine(_root, "fallback", "logs");

        var file = Open(preferred: Path.Combine(blocker, "logs"), fallback: fallback);

        Assert.True(file.Status.Healthy);
        Assert.StartsWith(fallback, file.Status.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primary log folder unusable", file.Status.Reason);
        Assert.Equal(fallback, file.LogsDirectory);
    }

    [Fact]
    public void A_fallback_session_keeps_writing_to_the_fallback_folder_after_midnight()
    {
        // Rolling into the preferred folder that had already failed would silently end logging at the
        // first midnight of a long session.
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var fallback = Path.Combine(_root, "fallback", "logs");
        var file = Open(preferred: Path.Combine(blocker, "logs"), fallback: fallback);

        _now = Day.AddDays(1).ToDateTime(new TimeOnly(0, 5));
        file.Write([At(Day.AddDays(1), 0, 5, "after midnight")]);

        Assert.Equal(["after midnight"], Lines(Day.AddDays(1), fallback));
        Assert.StartsWith(fallback, file.Status.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_log_folder_deleted_under_a_running_session_is_recreated_and_writing_resumes()
    {
        var file = Open();
        file.Write([At(Day, 9, 0, "before the folder was deleted")]);

        Directory.Delete(LogsDir, recursive: true);
        file.Write([At(Day, 9, 1, "after the folder was deleted")]);

        Assert.Equal(["after the folder was deleted"], Lines(Day));
        Assert.True(file.Status.Healthy);
    }

    [Fact]
    public void With_no_usable_folder_the_status_says_so_and_writing_is_a_harmless_no_op()
    {
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");

        var file = Open(preferred: Path.Combine(blocker, "logs"), fallback: Path.Combine(blocker, "fallback"));

        Assert.False(file.Status.Healthy);
        Assert.Equal(string.Empty, file.Status.Path);
        Assert.Contains("fallback log folder unusable", file.Status.Reason);
        file.Write([At(Day, 12, 0, "nowhere to go")]);
    }

    [Fact]
    public void A_known_sensitive_format_is_redacted_before_it_reaches_the_disk()
    {
        var file = Open();
        var line = "14:05:09.123 [Debug] TranscriptionService: Decoded 4520 ms of audio in 210 ms (RTF 0.05): " +
            "\"send the merger memo to Dana\"";

        file.Write([At(Day, 14, 5, line, LogLevel.Debug)]);

        var written = Lines(Day).Single();
        Assert.DoesNotContain("merger", written);
        Assert.EndsWith("(RTF 0.05): \"[transcript redacted, 28 chars]\"", written);
    }

    [Fact]
    public void An_entry_with_an_exception_loses_the_exception_messages_and_keeps_its_line_breaks()
    {
        var file = Open();
        var entry = "14:05:09.123 [Warning] TextPostProcessor: Skipping invalid snippet 5 ('merger memo')." +
            Environment.NewLine + "System.ArgumentException: Invalid pattern 'merger memo'." +
            Environment.NewLine + "   at System.Text.RegularExpressions.Regex..ctor(String pattern)";

        file.Write([At(Day, 14, 5, entry, LogLevel.Warning)]);

        Assert.Equal(
            [
                $"14:05:09.123 [Warning] TextPostProcessor: Skipping invalid snippet 5 ('{HistoricalLogRedaction.SnippetPlaceholder}').",
                $"System.ArgumentException: {HistoricalLogRedaction.MessagePlaceholder}",
                "   at System.Text.RegularExpressions.Regex..ctor(String pattern)",
            ],
            Lines(Day));
    }

    [Fact]
    public void A_settings_provider_failure_loses_its_exception_messages_before_it_reaches_the_disk()
    {
        // The entry SettingsWindow.TryLog wrote until this build, formatted the way the file logger formats an
        // attached exception, with the "(host:port)" .NET 10 puts in a connection failure.
        const string Host = "contoso-ai.openai.azure.com";
        Exception failure;
        try
        {
            throw new System.Net.Http.HttpRequestException(
                System.Net.Http.HttpRequestError.NameResolutionError,
                $"No such host is known. ({Host}:443)",
                new System.Net.Sockets.SocketException(11001));
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            failure = ex;
        }

        var time = Day.ToDateTime(new TimeOnly(14, 5));
        var entry = LogLineFormat.Format(
            time, LogLevel.Warning, "SettingsWindow", "Could not reach the Azure API-key endpoint.", failure);
        var file = Open();

        file.Write([new LogRecord(time, LogLevel.Warning, entry)]);

        var lines = Lines(Day);
        Assert.Equal(LogLineFormat.Format(time, LogLevel.Warning, "SettingsWindow", "Could not reach the Azure API-key endpoint."), lines[0]);
        Assert.Equal($"System.Net.Http.HttpRequestException: {HistoricalLogRedaction.MessagePlaceholder}", lines[1]);
        Assert.StartsWith(" ---> System.Net.Sockets.SocketException (11001): ", lines[2]);
        Assert.Contains(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(Host, StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_are_written_as_utf8_without_a_byte_order_mark()
    {
        var file = Open();

        file.Write([At(Day, 13, 0, "caf\u00e9 \u2026")]);

        var bytes = File.ReadAllBytes(PathFor(Day));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("caf\u00e9 \u2026" + Environment.NewLine, Encoding.UTF8.GetString(bytes));
    }

    private static void AppendLikeTheOverlay(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
    }
}
