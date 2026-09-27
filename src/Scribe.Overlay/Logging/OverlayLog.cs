using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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
    private const int WriteAttempts = 12;
    private const int MaxExceptionFrames = 32;
    private const int MaxExceptionFrameChars = 512;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _path;
    private static DateOnly _pathDate;

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

    private static void AppendLine(string line)
    {
        var text = line + Environment.NewLine;
        var path = Path;

        // Both the WPF host and this process append to the same file; tolerate brief lock contention.
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            try
            {
                lock (Gate)
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Utf8NoBom);
                    writer.Write(text);
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
}
