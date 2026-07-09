using System;
using System.IO;
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
    private static readonly object Gate = new();
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
        // Diagnostics are best-effort and must NEVER throw into the overlay's UI/IPC code — including
        // from Path resolution (directory creation) or an unexpected writer failure below.
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] Overlay: {message}";
            var path = Path;

            // Both the WPF host and this process append to the same file; tolerate brief lock contention.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    lock (Gate)
                    {
                        using var stream = new FileStream(
                            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(stream);
                        writer.WriteLine(line);
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
        catch
        {
            // Never let logging disrupt the overlay.
        }
    }

    public static void Warn(string message) => Write(message, "Warning");

    public static void Error(string message, Exception? ex = null) =>
        Write(ex is null ? message : $"{message}: {ex}", "Error");
}
