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
    private const int FlushTimeoutMs = 1000;

    private static readonly object StartGate = new();
    private static readonly object PathGate = new();
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

    public static void Error(string message, Exception? ex = null) =>
        Write(ex is null ? message : string.Create(
            CultureInfo.InvariantCulture,
            $"{message} ({ex.GetType().Name} 0x{ex.HResult:X8})"), "Error");

    internal static bool Flush(TimeSpan timeout)
    {
        try
        {
            EnsureStarted();
            var deadline = Environment.TickCount64 + Math.Clamp((long)timeout.TotalMilliseconds, 0, int.MaxValue);
            while (Queue.Reader.Count > 0 || Volatile.Read(ref _writing) != 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                Thread.Sleep((int)Math.Min(10, remaining));
            }

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
                Batch.Clear();
                while (Batch.Count < MaxBatchLines && Queue.Reader.TryRead(out var line))
                {
                    Batch.Add(line);
                }

                Volatile.Write(ref _writing, 1);
                try
                {
                    WriteBatch(Batch);
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

            AppendWithRetry(Path, Pending.ToString());
        }
        catch
        {
            // Logging remains best-effort.
        }
    }

    private static void AppendWithRetry(string path, string text)
    {
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                writer.Write(text);
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
