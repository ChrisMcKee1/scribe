using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Infrastructure;

/// <summary>
/// A minimal, dependency-free file logger that appends one line per entry to a daily log file
/// under <c>%LOCALAPPDATA%\ScribeData\logs</c>. A tray app has no console, so a file sink is the
/// primary way to diagnose the end-to-end dictation loop.
/// <para>
/// The folder is bounded on two axes (<see cref="LogRetentionPolicy"/>): old days are swept at
/// startup and at every midnight rollover, and a single day that runs away is degraded to
/// warnings and errors once it passes its budget. Without either, a machine that hits a looping
/// fault accumulates log files for as long as the app is installed, and nobody notices until the
/// disk does.
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Re-read the file size this often, so the overlay's writes are accounted for too.</summary>
    private const int SizeRecheckInterval = 256;

    private readonly string _logsDirectory;
    private readonly long _dailyBudgetBytes;
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();
    // Empty means "logging is dead"; initialized here so the constructor's failure paths cannot
    // leave it null (CS8618) when both TryOpenIn attempts fail.
    private string _filePath = string.Empty;
    private DateOnly _fileDay;

    // Best-effort running size of today's file. Seeded from disk and refreshed periodically rather
    // than tracked exactly: the overlay process appends to the same file, so our own byte count is
    // always a lower bound. It only has to be close enough to catch a runaway.
    private long _dayBytes;
    private int _writesSinceSizeCheck;
    private bool _dayBudgetAnnounced;

    /// <summary>Why logging is dead, when it is. Empty while logging is working.</summary>
    private string _failureReason = string.Empty;

    public FileLoggerProvider(
        string logsDirectory,
        LogLevel minimumLevel = LogLevel.Debug,
        long dailyBudgetBytes = LogRetentionPolicy.DefaultDailyBudgetBytes)
    {
        _logsDirectory = logsDirectory ?? string.Empty;
        _minimumLevel = minimumLevel;
        _dailyBudgetBytes = dailyBudgetBytes;
        _fileDay = DateOnly.FromDateTime(DateTime.Now);
        // A shipped Store build was found writing NOTHING for a whole session while happily writing
        // its database to the same ScribeData root, and the reason was unknowable: this constructor
        // used to swallow every exception into an empty path, which the rest of the class treats as
        // "logging is dead", with no message, no fallback and no way for anyone to find out. That
        // silence is why a reproducible bug survived multiple debugging sessions with no evidence to
        // read. The failure is now recorded, a second location is tried, and CurrentStatus reports
        // the outcome so Settings can show the truth instead of a path the app is not writing to.
        if (!TryOpenIn(_logsDirectory))
        {
            var fallback = FallbackDirectory();
            if (!string.IsNullOrEmpty(fallback) && TryOpenIn(fallback))
            {
                _failureReason = $"primary log folder unusable ({_failureReason}); using {fallback}";
            }
            else
            {
                _filePath = string.Empty;
            }
        }
    }

    /// <summary>
    /// Prepares <paramref name="directory"/> for writing, capturing the reason on failure.
    /// </summary>
    private bool TryOpenIn(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = ScribeLogFiles.PathFor(directory, _fileDay);

            // Prove the directory is actually writable rather than assuming it: CreateDirectory
            // succeeding says nothing about whether this process may add a file to it, which is
            // exactly the distinction a packaged app with redirected storage can fall foul of.
            using (var probe = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                // Opened and closed; the append leaves the file untouched when nothing is written.
            }

            _filePath = path;
            _dayBytes = CurrentFileLength();

            // Sweep before the first line rather than on a timer: the app may run for weeks without
            // ever reaching a scheduled sweep, and startup is the one moment guaranteed to happen.
            ScribeLogFiles.Prune(directory, _fileDay);
            _failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _failureReason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
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

    /// <summary>
    /// Where logging is actually going, and why it is not going anywhere when that is the case.
    /// Surfaced in Settings so "the log folder" never again names a folder nothing is written to.
    /// </summary>
    public (bool Healthy, string Path, string Reason) CurrentStatus()
    {
        lock (_gate)
        {
            return (_filePath.Length > 0, _filePath, _failureReason);
        }
    }

    /// <summary>The daily file this provider is currently writing to; empty when logging is dead.</summary>
    public string CurrentFilePath
    {
        get { lock (_gate) { return _filePath; } }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    private long CurrentFileLength()
    {
        try
        {
            var info = new FileInfo(_filePath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void Append(string line, LogLevel level)
    {
        var payload = line + Environment.NewLine;
        var payloadBytes = Encoding.UTF8.GetByteCount(payload);

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
                    if (string.IsNullOrWhiteSpace(_filePath))
                    {
                        return;
                    }

                    // The tray app runs for days, so "daily" must rotate per write, not per launch:
                    // a launch-day file pinned at construction diverges from the overlay's properly
                    // rotated file at midnight and splits the shared timeline the logs exist for.
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (today != _fileDay)
                    {
                        _fileDay = today;
                        _filePath = ScribeLogFiles.PathFor(_logsDirectory, today);
                        _dayBytes = CurrentFileLength();
                        _writesSinceSizeCheck = 0;
                        _dayBudgetAnnounced = false;

                        // Midnight is the only sweep opportunity a long-running tray session gets.
                        ScribeLogFiles.Prune(_logsDirectory, today);
                    }
                    else if (++_writesSinceSizeCheck >= SizeRecheckInterval)
                    {
                        _writesSinceSizeCheck = 0;
                        _dayBytes = Math.Max(_dayBytes, CurrentFileLength());
                    }

                    // The notice is written whenever the budget is first crossed, and the line that
                    // crossed it is dropped only if it is below Warning. Folding the two decisions
                    // together would lose the notice entirely when the crossing line is an error,
                    // which is the case a reader most needs it explained for.
                    var drop = ShouldDropForDailyBudget(level, out var notice);
                    var text = notice is null
                        ? (drop ? null : payload)
                        : notice + Environment.NewLine + (drop ? string.Empty : payload);
                    if (text is null)
                    {
                        return;
                    }

                    using var stream = new FileStream(
                        _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.Write(text);
                    _dayBytes += drop ? 0 : payloadBytes;
                    if (notice is not null)
                    {
                        _dayBytes += Encoding.UTF8.GetByteCount(notice) + Environment.NewLine.Length;
                    }
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

    /// <summary>
    /// True when this line must be dropped because today's file is over budget. Warnings and errors
    /// always survive: past the cap the interesting lines are exactly the ones that would otherwise
    /// be crowded out by whatever is looping. Caller holds <see cref="_gate"/>.
    /// </summary>
    private bool ShouldDropForDailyBudget(LogLevel level, out string? notice)
    {
        notice = null;
        if (_dailyBudgetBytes <= 0 || _dayBytes <= _dailyBudgetBytes)
        {
            return false;
        }

        if (!_dayBudgetAnnounced)
        {
            _dayBudgetAnnounced = true;

            // Emitted once, attached to the next line that gets through, so a reader is never left
            // wondering why the detail stops partway through the day.
            notice = $"{DateTime.Now:HH:mm:ss.fff} [Warning] FileLoggerProvider: " +
                $"Today's log passed its {_dailyBudgetBytes / (1024 * 1024)} MB budget. " +
                "Only warnings and errors are recorded for the rest of the day.";
        }

        return level < LogLevel.Warning;
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
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
                var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {shortCategory}: {formatter(state, exception)}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                provider.Append(line, logLevel);
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
