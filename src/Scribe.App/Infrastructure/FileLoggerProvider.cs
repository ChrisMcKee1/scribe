using System.IO;
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
        lock (_gate)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
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

            var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Append(line);
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
