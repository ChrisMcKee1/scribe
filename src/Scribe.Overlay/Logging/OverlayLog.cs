using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Scribe.Overlay.Logging;

/// <summary>
/// Cross-process file logger that appends to the SAME daily Scribe log the WPF app writes
/// (<c>%LOCALAPPDATA%\ScribeData\logs\scribe-yyyyMMdd.log</c>), in the SAME line format
/// (<c>HH:mm:ss.fff [Level] Overlay: message</c>) so the overlay's full lifecycle interleaves with
/// the dictation pipeline in one timeline. Two processes share the file, so writes retry briefly on
/// the inevitable sharing collisions.
/// </summary>
public static class OverlayLog
{
    private const int MaxQueuedLines = 4096;
    private const int MaxBatchLines = 128;
    private const int WriteAttempts = 12;
    private const int PromptWriteTimeoutMs = 250;
    private const int UnhandledFlushTimeoutMs = 1000;
    private const int FlushTimeoutMs = 1000;
    private const int MaxExceptionFrames = 32;
    private const int MaxExceptionFrameChars = 512;

    private static readonly object StartGate = new();
    private static readonly object PathGate = new();
    private static readonly object WriteGate = new();
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedLines)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly List<string> Batch = new(MaxBatchLines);
    private static readonly StringBuilder Pending = new(4096);

    private static string? _path;
    private static DateOnly _pathDate;
    private static Task? _writer;
    private static int _started;
    private static int _dropped;
    private static int _writing;

    static OverlayLog()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(TimeSpan.FromMilliseconds(FlushTimeoutMs));
    }

    private static string Path
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_path is not null && _pathDate == today)
            {
                return _path;
            }

            lock (PathGate)
            {
                if (_path is null || _pathDate != today)
                {
                    var root = Environment.GetEnvironmentVariable("SCRIBE_DATA_DIR");
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        root = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "ScribeData");
                    }

                    var dir = System.IO.Path.Combine(root, "logs");
                    Directory.CreateDirectory(dir);
                    _path = System.IO.Path.Combine(dir, $"scribe-{today:yyyyMMdd}.log");
                    _pathDate = today;
                }
            }

            return _path;
        }
    }

    public static void Write(string message, string level = "Information")
    {
        // Diagnostics are best-effort and must NEVER throw into the overlay's UI/IPC code, including
        // from Path resolution, directory creation or an unexpected writer failure below.
        try
        {
            EnsureStarted();
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] Overlay: {message}";
            if (RequiresPromptWrite(level))
            {
                var timeout = level == "Error"
                    ? TimeSpan.FromMilliseconds(UnhandledFlushTimeoutMs)
                    : TimeSpan.FromMilliseconds(PromptWriteTimeoutMs);
                WritePromptLine(line, timeout);
                return;
            }

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0 && !Queue.Writer.TryWrite(DropNotice(dropped)))
            {
                Interlocked.Add(ref _dropped, dropped);
            }

            if (!Queue.Writer.TryWrite(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch
        {
            // Never let logging disrupt the overlay.
        }
    }

    public static void Warn(string message) => Write(message, "Warning");

    public static void Error(string message, Exception? ex = null)
    {
        try
        {
            Write(ex is null ? message : message + FormatExceptionShape(ex), "Error");
        }
        catch
        {
            try
            {
                Write(message + " (" + SafeExceptionType(ex) + ")", "Error");
            }
            catch
            {
                // Never let diagnostics replace the original failure.
            }
        }
    }

    private static bool RequiresPromptWrite(string level)
    {
        return level is "Warning" or "Error" or "Critical";
    }

    private static string FormatExceptionShape(Exception ex)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(" (")
                .Append(SafeExceptionType(ex))
                .Append(" 0x")
                .Append(ex.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .Append(')');
            AppendExceptionFrames(builder, ex, MaxExceptionFrames);
            return builder.ToString();
        }
        catch
        {
            return " (" + SafeExceptionType(ex) + ")";
        }
    }

    private static string SafeExceptionType(Exception? ex)
    {
        try
        {
            return ex?.GetType().Name ?? "Exception";
        }
        catch
        {
            return "Exception";
        }
    }

    private static int AppendExceptionFrames(StringBuilder builder, Exception? exception, int remaining)
    {
        if (exception is null || remaining <= 0)
        {
            return remaining;
        }

        try
        {
            remaining = AppendStackFrames(builder, exception.StackTrace, remaining);
            if (remaining <= 0)
            {
                return 0;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    remaining = AppendExceptionFrames(builder, inner, remaining);
                    if (remaining <= 0)
                    {
                        return 0;
                    }
                }

                return remaining;
            }

            return AppendExceptionFrames(builder, exception.InnerException, remaining);
        }
        catch
        {
            return remaining;
        }
    }

    private static int AppendStackFrames(StringBuilder builder, string? stackTrace, int remaining)
    {
        if (string.IsNullOrWhiteSpace(stackTrace) || remaining <= 0)
        {
            return remaining;
        }

        foreach (var rawLine in stackTrace.Split('\n'))
        {
            if (remaining <= 0)
            {
                return 0;
            }

            var line = rawLine.TrimEnd('\r');
            var trimmed = line.AsSpan().TrimStart();
            if (!trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            var frame = trimmed.Length > MaxExceptionFrameChars
                ? trimmed[..MaxExceptionFrameChars]
                : trimmed;
            builder.AppendLine().Append(frame);
            remaining--;
        }

        return remaining;
    }

    private static void WritePromptLine(string line, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + ToWaitMilliseconds(timeout);
        Flush(timeout);
        var remaining = RemainingMilliseconds(deadline);
        if (remaining <= 0 || !Monitor.TryEnter(WriteGate, remaining))
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        try
        {
            if (!AppendWithRetry(Path, line + Environment.NewLine, deadline))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        finally
        {
            Monitor.Exit(WriteGate);
        }
    }

    internal static bool Flush(TimeSpan timeout)
    {
        try
        {
            EnsureStarted();
            var deadline = Environment.TickCount64 + ToWaitMilliseconds(timeout);
            while (Queue.Reader.Count > 0 || Volatile.Read(ref _writing) != 0)
            {
                var remaining = RemainingMilliseconds(deadline);
                if (remaining <= 0)
                {
                    return false;
                }

                Thread.Sleep(Math.Min(10, remaining));
            }

            WritePendingDropNotice(deadline);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureStarted()
    {
        if (Volatile.Read(ref _started) != 0)
        {
            return;
        }

        lock (StartGate)
        {
            if (_started != 0)
            {
                return;
            }

            try
            {
                _writer = Task.Factory.StartNew(
                    RunWriter,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }
            catch
            {
                // Leave _started set so logging calls do not repeatedly pay for failed task creation.
            }
            finally
            {
                Volatile.Write(ref _started, 1);
            }
        }
    }

    private static async Task RunWriter()
    {
        try
        {
            while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                Volatile.Write(ref _writing, 1);
                try
                {
                    Batch.Clear();
                    while (Batch.Count < MaxBatchLines && Queue.Reader.TryRead(out var line))
                    {
                        Batch.Add(line);
                    }

                    WriteBatch(Batch);
                    if (Queue.Reader.Count == 0)
                    {
                        WritePendingDropNotice(Environment.TickCount64 + FlushTimeoutMs);
                    }
                }
                finally
                {
                    Volatile.Write(ref _writing, 0);
                }
            }
        }
        catch
        {
            // A dead writer must never take the overlay down. Later lines may be dropped by the full queue.
        }
    }

    private static string DropNotice(int dropped) => string.Create(
        CultureInfo.InvariantCulture,
        $"{DateTime.Now:HH:mm:ss.fff} [Warning] Overlay: {dropped} overlay log line(s) dropped: the overlay log writer fell behind and its queue was full.");

    private static void WriteBatch(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            Pending.Clear();
            foreach (var line in lines)
            {
                Pending.AppendLine(line);
            }

            lock (WriteGate)
            {
                AppendWithRetry(Path, Pending.ToString(), Environment.TickCount64 + FlushTimeoutMs);
            }
        }
        catch
        {
            // Logging remains best-effort.
        }
    }

    private static void WritePendingDropNotice(long deadline)
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped <= 0)
        {
            return;
        }

        var notice = DropNotice(dropped) + Environment.NewLine;
        var remaining = RemainingMilliseconds(deadline);
        if (remaining <= 0 || !Monitor.TryEnter(WriteGate, remaining))
        {
            Interlocked.Add(ref _dropped, dropped);
            return;
        }

        try
        {
            if (!AppendWithRetry(Path, notice, deadline))
            {
                Interlocked.Add(ref _dropped, dropped);
            }
        }
        finally
        {
            Monitor.Exit(WriteGate);
        }
    }

    private static bool AppendWithRetry(string path, string text, long deadline)
    {
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            if (RemainingMilliseconds(deadline) <= 0)
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                writer.Write(text);
                return true;
            }
            catch (IOException)
            {
                SleepWithin(deadline);
            }
            catch (UnauthorizedAccessException)
            {
                SleepWithin(deadline);
            }
        }

        return false;
    }

    private static int ToWaitMilliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

    private static int RemainingMilliseconds(long deadline) =>
        (int)Math.Min(Math.Max(deadline - Environment.TickCount64, 0), int.MaxValue);

    private static void SleepWithin(long deadline)
    {
        var remaining = RemainingMilliseconds(deadline);
        if (remaining > 0)
        {
            Thread.Sleep(Math.Min(15, remaining));
        }
    }

}
