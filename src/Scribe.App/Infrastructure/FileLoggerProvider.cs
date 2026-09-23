using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Infrastructure;

/// <summary>
/// The app's file log, a thin <see cref="ILoggerProvider"/> over two Core types:
/// <see cref="DailyLogFile"/> (one file per day under <c>%LOCALAPPDATA%\ScribeData\logs</c>, shared with
/// the overlay process, retention sweeps, the daily budget) and <see cref="BackgroundLogWriter"/> (a
/// bounded queue with a single writer thread). A tray app has no console, so this file is the primary
/// way to diagnose the end-to-end dictation loop.
/// <para>
/// A logging call formats its line on the caller's thread, queues it and returns; the disk write
/// happens on the writer thread. It used to open, append and close the file inside every call, and the
/// callers include the hotkey transition thread and the UI thread. Warnings and worse, and the session
/// start and end markers, still wait briefly until they are on disk, and the queue is drained when the
/// host disposes this provider, at process exit and on an unhandled exception, each with a short bound.
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Bound for a flush the app asks for itself, such as right after the session banner.</summary>
    internal static readonly TimeSpan PromptFlushTimeout = TimeSpan.FromMilliseconds(500);

    // The process is going away in both cases; each gets one short, bounded chance to drain.
    private static readonly TimeSpan ProcessExitFlushTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CrashFlushTimeout = TimeSpan.FromSeconds(1);

    private readonly LogLevel _minimumLevel;
    private readonly DailyLogFile _file;
    private readonly BackgroundLogWriter _writer;

    // One scrub at a time: a start and a midnight can overlap, and both would rewrite the same files and
    // the same ledger. It only ever guards that background housekeeping, never a logging call.
    private readonly object _scrubGate = new();
    private int _redactionStarted;
    private int _disposed;

    public FileLoggerProvider(
        string logsDirectory,
        LogLevel minimumLevel = LogLevel.Debug,
        long dailyBudgetBytes = LogRetentionPolicy.DefaultDailyBudgetBytes)
    {
        _minimumLevel = minimumLevel;
        _file = DailyLogFile.Open(
            logsDirectory ?? string.Empty, FallbackDirectory(), dailyBudgetBytes, OnDayChanged);
        _writer = new BackgroundLogWriter(_file);

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// Where logging is actually going, and why it is not going anywhere when that is the case.
    /// Surfaced in Settings so "the log folder" never again names a folder nothing is written to.
    /// </summary>
    public (bool Healthy, string Path, string Reason) CurrentStatus()
    {
        var status = _file.Status;
        return (status.Healthy, status.Path, status.Reason);
    }

    /// <summary>The daily file this provider is currently writing to; empty when logging is dead.</summary>
    public string CurrentFilePath => _file.Status.Path;

    /// <summary>
    /// Waits, up to <paramref name="timeout"/>, until every line logged so far is on disk. Returns false
    /// if the bound ran out first. Never throws.
    /// </summary>
    public bool Flush(TimeSpan timeout) => _writer.Flush(timeout);

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(LogLineFormat.ShortCategory(categoryName), this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }
        catch (Exception)
        {
            // Unhooking is tidiness; a late hook only flushes an empty queue.
        }

        // Bounded: drains the queue and stops the writer thread. Teardown carries on after the host is
        // gone, so any line logged later is written on its caller's thread rather than lost.
        _writer.Dispose();
    }

    /// <summary>
    /// Second place to try when the preferred folder cannot be written. Deliberately the per-user
    /// temp folder, which a packaged app can always write to even when its AppData writes are
    /// redirected or denied.
    /// </summary>
    private static string FallbackDirectory()
    {
        try
        {
            return Path.Combine(Path.GetTempPath(), "ScribeData", "logs");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => _writer.Flush(ProcessExitFlushTimeout);

    // Raised on the faulting thread before the runtime terminates the process, and the writer thread is
    // still running at that point, so this is the last chance for everything queued before the crash.
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) =>
        _writer.Flush(CrashFlushTimeout);

    /// <summary>
    /// Starts the one-time pass that removes, from past days' files only, the sensitive values earlier
    /// builds wrote in known formats (<see cref="HistoricalLogRedaction"/>). Called once the session banner
    /// is on disk, so the log opens with the banner rather than with this pass's summary. Later passes
    /// follow each day change. Idempotent and never throws.
    /// </summary>
    public void StartHistoricalRedaction()
    {
        if (Interlocked.Exchange(ref _redactionStarted, 1) != 0 || !_file.Status.Healthy)
        {
            return;
        }

        // The file's own day, not a second clock read: a start straddling midnight must never put the
        // file being written inside the range the scrub rewrites.
        ScheduleHistoricalScrub(_file.LogsDirectory, before: _file.Day);
    }

    // Called on the writer thread whenever writing moves to another day's file, normally at midnight.
    // Nothing runs before the first pass has been started, which keeps the banner first in the log.
    private void OnDayChanged(LogDayChange change)
    {
        if (Volatile.Read(ref _redactionStarted) != 0)
        {
            ScheduleHistoricalScrub(change.Directory, change.ScrubBefore);
        }
    }

    /// <summary>
    /// Runs the scrub on its own low-priority thread, because a week of retained logs may be read and none
    /// of that belongs on the startup path or the writer thread.
    /// </summary>
    private void ScheduleHistoricalScrub(string directory, DateOnly before)
    {
        try
        {
            var thread = new Thread(() => ScrubHistoricalLeaks(directory, before))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Scribe log redaction",
            };
            thread.Start();
        }
        catch (Exception)
        {
            // Housekeeping; the next start tries again, and the diagnostics export redacts regardless.
        }
    }

    private void ScrubHistoricalLeaks(string directory, DateOnly before)
    {
        try
        {
            LogScrubResult result;
            lock (_scrubGate)
            {
                result = HistoricalLogRedaction.ScrubRetainedFiles(directory, before);
            }

            if (result.FilesRewritten == 0 && result.FilesSkipped == 0)
            {
                return;
            }

            // Counts only: the whole point of the pass is that the redacted text never appears again.
            var counts = result.Redactions;
            WriteOwnLine(LogLevel.Information, string.Format(
                CultureInfo.InvariantCulture,
                "Redacted {0} sensitive value(s) that an earlier version wrote into {1} past log file(s) " +
                "(dictation text {2}, endpoint addresses {3}, Azure names {4}, dictionary and similar text {5}, " +
                "provider text {6}); {7} file(s) could not be examined or rewritten and will be retried later.",
                counts.Total,
                result.FilesRewritten,
                counts.Transcripts,
                counts.EndpointAddresses,
                counts.AzureResourceNames,
                counts.UserContent,
                counts.ProviderText,
                result.FilesSkipped));
        }
        catch (Exception)
        {
            // Never let housekeeping disturb the application.
        }
    }

    private void WriteOwnLine(LogLevel level, string message)
    {
        var now = DateTime.Now;
        _writer.Write(new LogRecord(
            now, level, LogLineFormat.Format(now, level, LogLineFormat.WriterCategory, message)));
    }

    private sealed class FileLogger(string shortCategory, FileLoggerProvider provider) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

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
            // Any failure to record a line is swallowed; diagnostics are strictly best-effort.
            try
            {
                // Everything the queue keeps is settled here, on the caller's thread: the timestamp and
                // one finished string. The state object, scopes and the exception object itself are
                // never retained, so nothing the caller still owns waits in the queue.
                var now = DateTime.Now;
                var message = formatter(state, exception);
                var text = LogLineFormat.Format(now, logLevel, shortCategory, message, exception);
                provider._writer.Write(
                    new LogRecord(now, logLevel, text),
                    BackgroundLogWriter.RequiresPromptWrite(logLevel, message));
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
