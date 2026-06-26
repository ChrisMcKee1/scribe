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
    private readonly string _filePath;
    private readonly object _gate = new();

    public FileLoggerProvider(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _filePath = Path.Combine(logsDirectory, $"scribe-{DateTime.Now:yyyyMMdd}.log");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

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
