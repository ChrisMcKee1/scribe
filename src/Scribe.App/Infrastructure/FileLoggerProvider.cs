using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Scribe.App.Infrastructure;

/// <summary>
/// A minimal, dependency-free file logger that appends one line per entry to a daily log file
/// under <c>%LOCALAPPDATA%\ScribeData\logs</c>. A tray app has no console, so a file sink is the
/// primary way to diagnose the end-to-end dictation loop.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly object _gate = new();
    private string _filePath;
    private int _fileDay;

    public FileLoggerProvider(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _logsDirectory = logsDirectory;
        var now = DateTime.Now;
        _fileDay = now.DayOfYear;
        _filePath = DailyPath(now);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    private string DailyPath(DateTime now) =>
        Path.Combine(_logsDirectory, $"scribe-{now:yyyyMMdd}.log");

    private void Append(string line)
    {
        var payload = line + Environment.NewLine;

        // The out-of-process overlay (OverlayLog) appends to this SAME daily file with
        // FileShare.ReadWrite, so this writer must share-and-retry to match it. Critically, logging
        // must NEVER throw back into the caller: a transient sharing collision here once propagated
        // through Microsoft.Extensions.Logging and tore down the recording overlay. Collisions are
        // retried briefly and then swallowed.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                lock (_gate)
                {
                    // The tray app runs for days, so "daily" must rotate per write, not per launch:
                    // a launch-day file pinned at construction diverges from the overlay's properly
                    // rotated file at midnight and splits the shared timeline the logs exist for.
                    var now = DateTime.Now;
                    if (now.DayOfYear != _fileDay)
                    {
                        _fileDay = now.DayOfYear;
                        _filePath = DailyPath(now);
                    }

                    using var stream = new FileStream(
                        _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.Write(payload);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(15);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(15);
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Logging must NEVER throw into the caller. Formatting, file I/O, or even a thread
            // interrupt here once propagated through Microsoft.Extensions.Logging and tore down the
            // recording overlay (a transient log-file lock was misread as an overlay launch failure).
            // Any failure to record a line is swallowed — diagnostics are strictly best-effort.
            try
            {
                var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {shortCategory}: {formatter(state, exception)}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                provider.Append(line);
            }
            catch
            {
                // Never let diagnostics disrupt the application.
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
