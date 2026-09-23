using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Diagnostics;

/// <summary>Where the file log is writing, and why it is not when it is not.</summary>
/// <param name="Healthy">True while entries have somewhere to go.</param>
/// <param name="Path">The daily file currently written to; empty when logging is dead.</param>
/// <param name="Reason">Empty while the preferred folder works; otherwise what went wrong.</param>
public readonly record struct LogFileStatus(bool Healthy, string Path, string Reason);

/// <summary>Writing moved from one day's file to another's, normally at midnight.</summary>
/// <param name="Directory">The log folder in use.</param>
/// <param name="PreviousDay">The day whose file was being written until now.</param>
/// <param name="Day">The day whose file is written from now on.</param>
public readonly record struct LogDayChange(string Directory, DateOnly PreviousDay, DateOnly Day)
{
    /// <summary>
    /// First day the historical scrub must not touch. Neither the file now being written nor the one just
    /// left may be replaced: the overlay can still be finishing a line in the file it was writing a moment
    /// ago, and a line appended between the scrub's read and its replace would be lost. This holds whichever
    /// way the clock moved.
    /// </summary>
    public DateOnly ScrubBefore => PreviousDay < Day ? PreviousDay : Day;
}

/// <summary>
/// The shared daily log file under <c>%LOCALAPPDATA%\ScribeData\logs</c>: one file per day, named by
/// <see cref="ScribeLogFiles"/>, appended to by this process and by the out-of-process overlay.
/// <para>
/// The folder is bounded on two axes (<see cref="LogRetentionPolicy"/>): old days are swept when the
/// file is opened and whenever writing moves to another day (normally at midnight), and a single day
/// that runs away is degraded to warnings and errors once it passes its budget. Both budgets are soft;
/// see the policy for why.
/// </para>
/// <para>
/// Not thread-safe for writing: <see cref="Write"/> is called by one thread at a time (the
/// <see cref="BackgroundLogWriter"/>). <see cref="Status"/>, <see cref="LogsDirectory"/> and
/// <see cref="Day"/> may be read from any thread.
/// </para>
/// </summary>
public sealed class DailyLogFile : ILogRecordSink
{
    /// <summary>Re-read the file size this often, so the overlay's writes are accounted for too.</summary>
    public const int SizeRecheckInterval = 256;

    private const int WriteAttempts = 12;
    private const int InitialBufferBytes = 4096;
    private const int RetainedBufferBytes = 256 * 1024;

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(15);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly long _dailyBudgetBytes;
    private readonly TimeSpan _retryDelay;
    private readonly Action<LogDayChange>? _dayChanged;
    private readonly Func<DateTime> _clock;
    private readonly object _statusGate = new();
    private ArrayBufferWriter<byte> _pending = new(InitialBufferBytes);

    // Written under _statusGate so Status and Day can be read from any thread.
    private string _directory = string.Empty;
    private string _filePath = string.Empty;
    private string _failureReason = string.Empty;
    private DateOnly _fileDay;

    // Best-effort running size of today's file. Seeded from disk and refreshed periodically rather
    // than tracked exactly: the overlay process appends to the same file, so our own byte count is
    // always a lower bound. It only has to be close enough to catch a runaway.
    private long _dayBytes;
    private int _recordsSinceSizeCheck;
    private bool _dayBudgetAnnounced;

    private DailyLogFile(
        long dailyBudgetBytes, TimeSpan retryDelay, Action<LogDayChange>? dayChanged, Func<DateTime> clock)
    {
        _dailyBudgetBytes = dailyBudgetBytes;
        _retryDelay = retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;
        _dayChanged = dayChanged;
        _clock = clock;
    }

    /// <summary>
    /// Opens today's file in <paramref name="preferredDirectory"/>, or in
    /// <paramref name="fallbackDirectory"/> when the preferred one cannot be written. Never throws: when
    /// neither folder works the result reports it through <see cref="Status"/> and writes nothing.
    /// </summary>
    /// <param name="dayChanged">
    /// Called on the writing thread whenever writing moves to another day's file (normally at midnight).
    /// </param>
    /// <param name="retryDelay">Pause between attempts on a sharing collision. Tests pass zero.</param>
    /// <param name="clock">Local time source that decides the day. Defaults to <see cref="DateTime.Now"/>.</param>
    public static DailyLogFile Open(
        string preferredDirectory,
        string? fallbackDirectory,
        long dailyBudgetBytes = LogRetentionPolicy.DefaultDailyBudgetBytes,
        Action<LogDayChange>? dayChanged = null,
        TimeSpan? retryDelay = null,
        Func<DateTime>? clock = null)
    {
        var file = new DailyLogFile(
            dailyBudgetBytes, retryDelay ?? DefaultRetryDelay, dayChanged, clock ?? (static () => DateTime.Now));
        file.OpenFirstUsable(preferredDirectory ?? string.Empty, fallbackDirectory, file.Today());
        return file;
    }

    /// <summary>Where entries are going, safe to read from any thread.</summary>
    public LogFileStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return new LogFileStatus(_filePath.Length > 0, _filePath, _failureReason);
            }
        }
    }

    /// <summary>The folder actually in use (the preferred one or the fallback); empty when none is.</summary>
    public string LogsDirectory
    {
        get { lock (_statusGate) { return _directory; } }
    }

    /// <summary>
    /// The day whose file is being written. Anything that rewrites past files must stay before it: this
    /// file, and the overlay's lines in it, are still being appended.
    /// </summary>
    public DateOnly Day
    {
        get { lock (_statusGate) { return _fileDay; } }
    }

    /// <summary>Appends the batch in order. Failures are retried briefly and then swallowed.</summary>
    public void Write(IReadOnlyList<LogRecord> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        string path;
        DateOnly fileDay;
        lock (_statusGate)
        {
            path = _filePath;
            fileDay = _fileDay;
        }

        if (path.Length == 0)
        {
            return;
        }

        try
        {
            // The tray app runs for days, so "daily" must rotate per write, not per launch: a launch-day
            // file pinned at construction diverges from the overlay's properly rotated file at midnight
            // and splits the shared timeline the logs exist for. The day comes from the clock at write
            // time, which is exactly how the overlay picks its file, so the two processes agree even when
            // the clock or the time zone moves backwards. A line logged a moment before midnight and
            // written just after it therefore joins the new day, as it always has.
            var day = Today();
            if (day != fileDay)
            {
                path = StartDay(day);
            }

            foreach (var record in batch)
            {
                if (record.Text is null)
                {
                    continue;
                }

                if (++_recordsSinceSizeCheck >= SizeRecheckInterval)
                {
                    _recordsSinceSizeCheck = 0;
                    _dayBytes = Math.Max(_dayBytes, LengthOf(path));
                }

                // The notice is written whenever the budget is first crossed, and the line that
                // crossed it is dropped only if it is below Warning. Folding the two decisions
                // together would lose the notice entirely when the crossing line is an error,
                // which is the case a reader most needs it explained for.
                var drop = ShouldDropForDailyBudget(record, out var notice);
                if (notice is not null)
                {
                    Append(notice);
                }

                if (!drop)
                {
                    // Defense in depth for the formats in which earlier builds leaked sensitive values: the
                    // sources no longer produce them, and if one ever reappears it is redacted here, along
                    // with the exception text attached to it, rather than persisted.
                    Append(HistoricalLogRedaction.RedactEntry(record.Text));
                }
            }
        }
        finally
        {
            FlushPending(path);
        }
    }

    private DateOnly Today()
    {
        try
        {
            return DateOnly.FromDateTime(_clock());
        }
        catch (Exception)
        {
            return DateOnly.FromDateTime(DateTime.Now);
        }
    }

    private void OpenFirstUsable(string preferred, string? fallback, DateOnly today)
    {
        // A shipped Store build was found writing NOTHING for a whole session while happily writing
        // its database to the same ScribeData root, and the reason was unknowable: the old provider
        // swallowed every exception into an empty path, which the rest of the sink treats as
        // "logging is dead", with no message, no fallback and no way for anyone to find out. That
        // silence is why a reproducible bug survived multiple debugging sessions with no evidence to
        // read. The failure is now recorded, a second location is tried, and Status reports the
        // outcome so Settings can show the truth instead of a path the app is not writing to.
        if (TryOpenIn(preferred, today, out var primaryFailure))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(fallback))
        {
            SetUnusable(primaryFailure);
            return;
        }

        if (TryOpenIn(fallback, today, out var fallbackFailure))
        {
            lock (_statusGate)
            {
                _failureReason = $"primary log folder unusable ({primaryFailure}); using {fallback}";
            }

            return;
        }

        SetUnusable($"{primaryFailure}; fallback log folder unusable ({fallbackFailure})");
    }

    private void SetUnusable(string reason)
    {
        lock (_statusGate)
        {
            _directory = string.Empty;
            _filePath = string.Empty;
            _failureReason = reason;
        }
    }

    /// <summary>Prepares <paramref name="directory"/> for writing, capturing the reason on failure.</summary>
    private bool TryOpenIn(string directory, DateOnly today, out string failure)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = ScribeLogFiles.PathFor(directory, today);

            // Prove the directory is actually writable rather than assuming it: CreateDirectory
            // succeeding says nothing about whether this process may add a file to it, which is
            // exactly the distinction a packaged app with redirected storage can fall foul of.
            using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                // Opened and closed; the append leaves the file untouched when nothing is written.
            }

            lock (_statusGate)
            {
                _directory = directory;
                _filePath = path;
                _failureReason = string.Empty;
                _fileDay = today;
            }

            _dayBytes = LengthOf(path);

            // Sweep before the first line rather than on a timer: the app may run for weeks without
            // ever reaching a scheduled sweep, and startup is the one moment guaranteed to happen.
            ScribeLogFiles.Prune(directory, today);
            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // Returns the new day's path. The directory is the one that was opened, which for a session
    // running on the fallback folder is the fallback: rolling into the preferred folder that already
    // failed would silently stop logging at the first midnight.
    private string StartDay(DateOnly day)
    {
        string directory;
        string path;
        DateOnly previousDay;
        lock (_statusGate)
        {
            directory = _directory;
            path = ScribeLogFiles.PathFor(directory, day);
            previousDay = _fileDay;
            _filePath = path;
            _fileDay = day;
        }

        _dayBytes = LengthOf(path);
        _recordsSinceSizeCheck = 0;
        _dayBudgetAnnounced = false;

        // Midnight is the only sweep opportunity a long-running tray session gets.
        ScribeLogFiles.Prune(directory, day);

        try
        {
            _dayChanged?.Invoke(new LogDayChange(directory, previousDay, day));
        }
        catch (Exception)
        {
            // Housekeeping hooks never cost a log line.
        }

        return path;
    }

    /// <summary>
    /// True when this entry must be dropped because today's file is over budget. Warnings and errors
    /// always survive: past the cap the interesting lines are exactly the ones that would otherwise be
    /// crowded out by whatever is looping.
    /// </summary>
    private bool ShouldDropForDailyBudget(LogRecord record, out string? notice)
    {
        notice = null;
        if (_dailyBudgetBytes <= 0 || _dayBytes <= _dailyBudgetBytes)
        {
            return false;
        }

        if (!_dayBudgetAnnounced)
        {
            _dayBudgetAnnounced = true;

            // Emitted once, ahead of the next line that gets through, so a reader is never left
            // wondering why the detail stops partway through the day.
            notice = LogLineFormat.Format(
                record.Timestamp,
                LogLevel.Warning,
                LogLineFormat.WriterCategory,
                $"Today's log passed its {_dailyBudgetBytes / (1024 * 1024)} MB budget. " +
                "Only warnings and errors are recorded for the rest of the day.");
        }

        return record.Level < LogLevel.Warning;
    }

    private void Append(string text)
    {
        var before = _pending.WrittenCount;
        Utf8.GetBytes(text.AsSpan(), _pending);
        Utf8.GetBytes(Environment.NewLine.AsSpan(), _pending);
        _dayBytes += _pending.WrittenCount - before;
    }

    private void FlushPending(string path)
    {
        if (_pending.WrittenCount == 0)
        {
            return;
        }

        try
        {
            AppendWithRetry(path, _pending.WrittenSpan);
        }
        finally
        {
            // One burst must not pin a large buffer for the rest of a session that lasts for days.
            if (_pending.Capacity > RetainedBufferBytes)
            {
                _pending = new ArrayBufferWriter<byte>(InitialBufferBytes);
            }
            else
            {
                _pending.ResetWrittenCount();
            }
        }
    }

    private void AppendWithRetry(string path, ReadOnlySpan<byte> bytes)
    {
        // The out-of-process overlay (OverlayLog) appends to this SAME daily file with
        // FileShare.ReadWrite, so this writer must share-and-retry to match it. A transient sharing
        // collision once propagated through Microsoft.Extensions.Logging and tore down the recording
        // overlay; collisions are retried briefly and then swallowed.
        var recreated = false;
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            try
            {
                // Opened, appended once and closed for every batch. A handle kept open would be cheaper,
                // but FileStream writes at its own tracked offset, so it would overwrite whatever the
                // overlay appended in between. Unbuffered, so the whole batch is a single write.
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
                stream.Write(bytes);
                return;
            }
            catch (DirectoryNotFoundException) when (!recreated)
            {
                // The folder was deleted under a running session (the privacy policy tells users they
                // may delete their logs at any time). Recreate it once rather than lose every line
                // until the next start; retrying without it would only burn the attempts.
                recreated = true;
                TryCreateDirectoryFor(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_retryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(_retryDelay);
                }
            }
            catch (Exception)
            {
                // Not a transient collision; retrying will not help.
                return;
            }
        }
    }

    private static void TryCreateDirectoryFor(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception)
        {
            // The next attempt fails the ordinary way and is retried like any other failure.
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
