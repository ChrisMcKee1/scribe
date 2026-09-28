using System.IO.Compression;
using System.Text.RegularExpressions;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-O-09 (<see cref="PerfFlags.BackgroundDiagnosticsExport"/>): Settings' "Save diagnostics..." writes the same zip on a
/// worker through one owner that refuses a second request, logs every outcome by its shape (so a result that lands after
/// the window closed still reaches the log) and lets quit wait a bounded time. The scheduler is the test's, so nothing here
/// depends on timing; no window is created.
/// </summary>
public sealed class DiagnosticsExportTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 20);
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("scribe-export-");
    private readonly CapturingLogger _log = new();

    public DiagnosticsExportTests()
    {
        Directory.CreateDirectory(LogsDir);
        WriteLog(Today, 400);
        WriteLog(Today.AddDays(-1), 300);
        WriteLog(Today.AddDays(-5), 50);
    }

    public void Dispose()
    {
        try { _folder.Delete(recursive: true); } catch { /* best effort */ }
    }

    private string LogsDir => Path.Combine(_folder.FullName, "logs");

    private string Destination(string name = "bundle.zip") => Path.Combine(_folder.FullName, name);

    [Fact]
    public async Task Both_ways_write_the_same_archive()
    {
        const string Report = "environment report";
        var synchronous = DiagnosticsBundle.Create(LogsDir, Destination("synchronous.zip"), Report, Today);

        var outcome = await new DiagnosticsExport(_log, static work => Task.Run(work), create: null)
            .RunAsync(LogsDir, Destination("worker.zip"), Report, Today);

        Assert.False(outcome.WasRefused);
        Assert.Equal(synchronous.LogFileCount, outcome.Bundle.LogFileCount);
        Assert.Equal(synchronous.Redactions, outcome.Bundle.Redactions);
        Assert.True(synchronous.Redactions.Of(LogRedactionKind.Transcript) > 0, "The logs must hold a line the copy redacts.");

        using var expected = ZipFile.OpenRead(synchronous.Path);
        using var actual = ZipFile.OpenRead(outcome.Bundle.Path);
        Assert.Equal(expected.Entries.Select(e => e.FullName), actual.Entries.Select(e => e.FullName));
        foreach (var (left, right) in expected.Entries.Zip(actual.Entries))
        {
            Assert.Equal(Decompressed(left), Decompressed(right));

            // Deflate at one level over the same bytes gives the same length: the compression level did not change.
            Assert.Equal(left.CompressedLength, right.CompressedLength);
        }
    }

    [Fact]
    public async Task A_request_while_one_is_written_is_refused_writes_nothing_and_the_next_one_runs()
    {
        var scheduler = new HeldScheduler();
        var export = new DiagnosticsExport(_log, scheduler.Schedule, create: null);

        var first = export.RunAsync(LogsDir, Destination("first.zip"), "report", Today);
        Assert.True(export.IsRunning);
        Assert.False(first.IsCompleted);

        var second = await export.RunAsync(LogsDir, Destination("second.zip"), "report", Today);
        Assert.True(second.WasRefused);
        Assert.Same(DiagnosticsExportResult.Refused, second);
        Assert.False(File.Exists(Destination("second.zip")));
        Assert.Equal(1, scheduler.Scheduled);

        scheduler.RunNext();
        var written = await first;
        Assert.False(written.WasRefused);
        Assert.False(export.IsRunning);

        scheduler.AutoRun = true;
        var third = await export.RunAsync(LogsDir, Destination("third.zip"), "report", Today);
        Assert.False(third.WasRefused);
        Assert.True(File.Exists(Destination("third.zip")));
    }

    [Fact]
    public void An_outcome_nobody_awaits_still_reaches_the_log_and_quit_waits_for_it_within_its_bound()
    {
        var scheduler = new HeldScheduler();
        var export = new DiagnosticsExport(_log, scheduler.Schedule, create: null);
        Assert.True(export.WaitForIdle(TimeSpan.Zero));

        _ = export.RunAsync(LogsDir, Destination(), "report", Today);
        Assert.False(export.WaitForIdle(TimeSpan.FromMilliseconds(20)));

        var worker = new Thread(scheduler.RunNext);
        worker.Start();
        Assert.True(export.WaitForIdle(TimeSpan.FromSeconds(30)));
        worker.Join();

        Assert.False(export.IsRunning);
        Assert.Matches(new Regex(@"^Information: Wrote a diagnostics bundle with 3 log file\(s\), \d+ bytes\.$"), _log.Text);
    }

    [Fact]
    public async Task An_existing_destination_is_replaced_as_before()
    {
        File.WriteAllText(Destination(), "an older file");
        var export = new DiagnosticsExport(_log, static work => Task.Run(work), create: null);

        var outcome = await export.RunAsync(LogsDir, Destination(), "report", Today);

        using var archive = ZipFile.OpenRead(outcome.Bundle!.Path);
        Assert.Contains(archive.Entries, e => e.FullName == "report.txt");
    }

    [Fact]
    public async Task A_destination_that_cannot_be_written_fails_as_before_and_is_logged_by_its_shape()
    {
        var destination = Destination();
        using (new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var synchronous = Assert.ThrowsAny<IOException>(() => DiagnosticsBundle.Create(LogsDir, destination, "report", Today));
            var export = new DiagnosticsExport(_log, static work => Task.Run(work), create: null);

            var failure = await Assert.ThrowsAnyAsync<IOException>(() => export.RunAsync(LogsDir, destination, "report", Today));

            Assert.Equal(synchronous.GetType(), failure.GetType());
            Assert.False(export.IsRunning);
        }

        Assert.StartsWith("Warning: Could not write the diagnostics bundle. (", _log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder.FullName, _log.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bundle.zip", _log.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failure_partway_leaves_what_it_leaves_today_and_frees_the_owner()
    {
        var export = new DiagnosticsExport(
            _log,
            static work => Task.Run(work),
            create: (_, destination, _, _) =>
            {
                File.WriteAllText(destination, "part of a zip");
                throw new IOException("The disk is full.");
            });

        var failure = await Assert.ThrowsAsync<IOException>(() => export.RunAsync(LogsDir, Destination(), "report", Today));

        Assert.Equal("The disk is full.", failure.Message);
        Assert.Equal("part of a zip", File.ReadAllText(Destination()));
        Assert.False(export.IsRunning);
        Assert.DoesNotContain("The disk is full", _log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_logger_or_a_scheduler_that_throws_changes_nothing_the_export_reports()
    {
        var export = new DiagnosticsExport(new ThrowingLogger(), static work => Task.Run(work), create: null);
        var outcome = await export.RunAsync(LogsDir, Destination(), "report", Today);
        Assert.False(outcome.WasRefused);
        Assert.False(export.IsRunning);

        var refusing = new DiagnosticsExport(_log, static _ => throw new InvalidOperationException("no worker"), create: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.RunAsync(LogsDir, Destination("b.zip"), "report", Today));
        Assert.False(refusing.IsRunning);
    }

    [Fact]
    public async Task The_caller_sees_the_export_idle_and_its_outcome_logged_when_its_result_arrives()
    {
        var export = new DiagnosticsExport(_log, static work => Task.Run(work), create: null);
        bool? runningWhenSeen = null;
        string? logWhenSeen = null;

        var run = export.RunAsync(LogsDir, Destination(), "report", Today);
        var seen = run.ContinueWith(
            _ =>
            {
                runningWhenSeen = export.IsRunning;
                logWhenSeen = _log.Text;
            },
            TaskScheduler.Default);
        await run;
        await seen;

        Assert.False(runningWhenSeen);
        Assert.Contains("Wrote a diagnostics bundle", logWhenSeen, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_keeps_today_s_handler_when_the_flag_is_off_and_hands_the_zip_to_the_owner_when_on()
    {
        var window = Source("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var handler = Member(window, "private void AboutSaveDiagnostics_Click(");
        var background = Member(window, "private async void SaveDiagnosticsInBackground(");

        string[] order =
        [
            "if (DiagnosticsExport is { } export && _perfFlags.IsOn(PerfFlags.BackgroundDiagnosticsExport))",
            "SaveDiagnosticsInBackground(export);",
            "var dialog = new Microsoft.Win32.SaveFileDialog",
            "var report = _diagnostics?.ComposeReport()",
            "var result = DiagnosticsBundle.Create(",
            "_log.LogInformation(",
        ];
        var positions = order.Select(text => handler.IndexOf(text, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);

        Assert.True(
            background.IndexOf("_diagnostics?.ComposeReport()", StringComparison.Ordinal) <
            background.IndexOf("await export.RunAsync(", StringComparison.Ordinal));
        Assert.DoesNotContain("DiagnosticsBundle.Create(", background, StringComparison.Ordinal);
        Assert.DoesNotContain("_log.Log", background, StringComparison.Ordinal);
        Assert.Contains("if (_closed)", background, StringComparison.Ordinal);
        Assert.Contains("SetSaveDiagnosticsEnabled(false);", background, StringComparison.Ordinal);

        var xaml = Source("src", "Scribe.App", "Settings", "SettingsWindow.xaml");
        Assert.Equal(2, Regex.Matches(xaml, Regex.Escape("Click=\"AboutSaveDiagnostics_Click\"")).Count);
        Assert.Contains("x:Name=\"DiagnosticsSaveBundleButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AboutSaveBundleButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_owns_one_export_hands_it_to_the_window_and_waits_for_it_at_quit_while_the_logger_is_up()
    {
        var app = Source("src", "Scribe.App", "App.xaml.cs");

        Assert.Contains("builder.Services.AddSingleton<DiagnosticsExport>();", app, StringComparison.Ordinal);
        Assert.Contains("DiagnosticsExport = services.GetRequiredService<DiagnosticsExport>(),", app, StringComparison.Ordinal);
        Assert.Contains("perfFlags: services.GetRequiredService<PerfFlags>(),", app, StringComparison.Ordinal);
        Assert.Contains("DiagnosticsExportDrainTimeout = TimeSpan.FromSeconds(10);", app, StringComparison.Ordinal);

        var exportStep = app.IndexOf("new(\"diagnostics export\"", StringComparison.Ordinal);
        Assert.True(exportStep > 0);
        Assert.True(exportStep < app.IndexOf("new(\"host stop\"", StringComparison.Ordinal));
        Assert.True(exportStep < app.IndexOf("new(\"host dispose\"", StringComparison.Ordinal));
    }

    private void WriteLog(DateOnly day, int lines)
    {
        using var writer = new StreamWriter(ScribeLogFiles.PathFor(LogsDir, day));
        for (var i = 0; i < lines; i++)
        {
            writer.WriteLine($"14:05:{i % 60:D2}.123 [Information] DictationController: #{i} finished with outcome Typed");
            if (i % 50 == 0)
            {
                writer.WriteLine(
                    "14:05:09.123 [Debug] TranscriptionService: Decoded 4520 ms of audio in 210 ms (RTF 0.05): " +
                    "\"send the merger memo to Dana\"");
            }
        }
    }

    private static byte[] Decompressed(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Member(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing: " + signature);
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced member: " + signature);
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts])).ReplaceLineEndings("\n");

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    // Holds each export's work until the test runs it, so "while one is being written" is a state, not a race.
    private sealed class HeldScheduler
    {
        private readonly Queue<(Func<DiagnosticsBundleResult> Work, TaskCompletionSource<DiagnosticsBundleResult> Done)> _held = new();

        public int Scheduled { get; private set; }

        public bool AutoRun { get; set; }

        public Task<DiagnosticsBundleResult> Schedule(Func<DiagnosticsBundleResult> work)
        {
            Scheduled++;
            var done = new TaskCompletionSource<DiagnosticsBundleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (AutoRun)
            {
                return Task.Run(work);
            }

            _held.Enqueue((work, done));
            return done.Task;
        }

        public void RunNext()
        {
            var (work, done) = _held.Dequeue();
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => throw new InvalidOperationException("scope");

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("the log is gone");
    }
}
