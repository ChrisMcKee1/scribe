using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Scribe.Core.Diagnostics;

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
    private const int MaxExceptionFrames = 32;
    private const int MaxExceptionFrameChars = 512;

    private static readonly object Gate = new();
    private static string? _path;
    private static DateOnly _pathDate;

    // A StreamWriter given only a stream encodes as UTF-8 without a byte order mark and throws on text that is not well
    // formed; it buffers 1,024 characters and encodes a line that fits them in one piece when it is disposed. Such a line is
    // written the same way here without the writer's and the stream's buffers; a longer one keeps the writer, which writes
    // it in pieces.
    private static readonly UTF8Encoding LineEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] NewLineBytes = LineEncoding.GetBytes(Environment.NewLine);
    private const int WriterBufferChars = 1024;

    // DATA-O-02: the app passes this at launch only when its own writer appends the same way (PerfFlags.AppendOnlyLog, and
    // this helper declares that it follows the argument), so the pair never runs two ways. Never read from the environment.
    private static readonly bool AppendOnly = HasArgument(Environment.GetCommandLineArgs(), AppendOnlyFile.LaunchArgument);
    private static int _modeAnnounced;

    private static string Path
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_path is not null && _pathDate == today)
            {
                return _path;
            }

            lock (Gate)
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
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] Overlay: {message}";
            AppendLine(line);
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

    // Appends one line to path with the retries two processes sharing a file need; whatever throws past them is caught by
    // Write. The overload below hands it the day's file and this helper's way.
    private static void AppendLine(string path, string line, bool appendOnly)
    {
        // Both the WPF host and this process append to the same file; tolerate brief lock contention.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                lock (Gate)
                {
                    if (appendOnly)
                    {
                        // The whole record in one write, however long (DATA-O-02), encoded before the file is opened: a
                        // handle that may only append puts it after whatever the app wrote, never over it or through it.
                        var record = new byte[LineEncoding.GetByteCount(line) + NewLineBytes.Length];
                        NewLineBytes.CopyTo(record, LineEncoding.GetBytes(line, 0, line.Length, record, 0));
                        using var appendOnlyStream = AppendOnlyFile.Open(path);
                        appendOnlyStream.Write(record, 0, record.Length);
                    }
                    else if (line.Length + Environment.NewLine.Length <= WriterBufferChars)
                    {
                        using var stream = new FileStream(
                            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
                        var bytes = new byte[LineEncoding.GetByteCount(line) + NewLineBytes.Length];
                        NewLineBytes.CopyTo(bytes, LineEncoding.GetBytes(line, 0, line.Length, bytes, 0));
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    else
                    {
                        using var stream = new FileStream(
                            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(stream);
                        writer.WriteLine(line);
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

    private static void AppendLine(string line)
    {
        var path = Path;
        if (AppendOnly && Interlocked.Exchange(ref _modeAnnounced, 1) == 0)
        {
            // Once per process, so the log shows which way this helper appended; the old way adds no line.
            AppendLine(path, $"{DateTime.Now:HH:mm:ss.fff} [Information] Overlay: shared log append-only (launch argument).", true);
        }

        AppendLine(path, line, AppendOnly);
    }

    /// <summary>
    /// Test seam (AppendOnlyLogTests and its child writer): the retry loop above for a test's own file and way. Unused in the
    /// overlay.
    /// </summary>
    internal static void AppendLineForTests(string path, string line, bool appendOnly) => AppendLine(path, line, appendOnly);

    private static bool HasArgument(string[] arguments, string name)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
