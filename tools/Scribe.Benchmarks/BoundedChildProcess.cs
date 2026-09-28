using System.Diagnostics;
using System.Text;

namespace Scribe.Benchmarks;

/// <summary>
/// Runs a headless child to its end under a real deadline (DATA-IMPL-A-06). Both of its streams are drained as they
/// arrive, each on its own thread, so a child that fills one pipe while the other is being read cannot stall either side,
/// and output past the bound is read and dropped, so the child never blocks on a full pipe. A child still running at the
/// deadline has its own process tree ended, nothing else, and its exit is observed before the run returns.
/// </summary>
internal static class BoundedChildProcess
{
    /// <summary>The characters kept of each stream by default.</summary>
    internal const int DefaultCaptureChars = 1 << 20;

    // How long an ended child may take to exit, and its streams to reach their end once it has.
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Starts <paramref name="info"/> with both streams redirected, calls <paramref name="started"/> with the process (to
    /// set its affinity, say), and returns once it has exited or, at <paramref name="deadline"/>, been ended.
    /// </summary>
    internal static ChildRun Run(
        ProcessStartInfo info, TimeSpan deadline, int captureChars = DefaultCaptureChars, Action<Process>? started = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentOutOfRangeException.ThrowIfNegative(captureChars);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        using var child = Process.Start(info) ?? throw new InvalidOperationException("The child did not start.");
        var output = new BoundedText(captureChars);
        var errors = new BoundedText(captureChars);
        var outputDrain = StartDrain(child.StandardOutput, output);
        var errorDrain = StartDrain(child.StandardError, errors);
        started?.Invoke(child);

        var timedOut = !child.WaitForExit(deadline);
        if (timedOut)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the wait and the kill.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // It could not be ended; whether it exited is observed below.
            }
        }

        var exited = !timedOut || child.WaitForExit(ExitBound);
        var drainEnd = Stopwatch.StartNew();
        var drained = outputDrain.Join(DrainBound) & errorDrain.Join(Remaining(DrainBound, drainEnd));
        return new ChildRun(
            child.Id,
            timedOut,
            exited,
            exited ? child.ExitCode : null,
            output.ToString(),
            output.Truncated,
            errors.ToString(),
            errors.Truncated,
            drained);
    }

    private static TimeSpan Remaining(TimeSpan bound, Stopwatch elapsed)
    {
        var left = bound - elapsed.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static Thread StartDrain(StreamReader reader, BoundedText sink)
    {
        var thread = new Thread(() => Drain(reader, sink)) { IsBackground = true, Name = "Child stream drain" };
        thread.Start();
        return thread;
    }

    private static void Drain(StreamReader reader, BoundedText sink)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                sink.Append(buffer.AsSpan(0, read));
            }
        }
        catch (IOException)
        {
            // The pipe broke: what was read is kept.
        }
        catch (ObjectDisposedException)
        {
            // The run returned before this stream ended and released it.
        }
    }

    // One stream's first characters, up to the bound; the rest is counted as dropped.
    private sealed class BoundedText(int capacity)
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();
        private bool _truncated;

        public bool Truncated
        {
            get
            {
                lock (_gate)
                {
                    return _truncated;
                }
            }
        }

        public void Append(ReadOnlySpan<char> chars)
        {
            lock (_gate)
            {
                var room = capacity - _text.Length;
                if (chars.Length > room)
                {
                    _truncated = true;
                    chars = chars[..room];
                }

                _text.Append(chars);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }
}

/// <summary>How a <see cref="BoundedChildProcess"/> run ended.</summary>
/// <param name="ProcessId">The child's process id.</param>
/// <param name="TimedOut">Whether the deadline passed first, so the child's process tree was ended.</param>
/// <param name="Exited">Whether the child's exit was observed.</param>
/// <param name="ExitCode">Its exit code, when it exited.</param>
/// <param name="Output">The first characters of its standard output.</param>
/// <param name="OutputTruncated">Whether standard output ran past the bound.</param>
/// <param name="Errors">The first characters of its standard error.</param>
/// <param name="ErrorsTruncated">Whether standard error ran past the bound.</param>
/// <param name="Drained">Whether both streams reached their end before the run returned.</param>
internal sealed record ChildRun(
    int ProcessId,
    bool TimedOut,
    bool Exited,
    int? ExitCode,
    string Output,
    bool OutputTruncated,
    string Errors,
    bool ErrorsTruncated,
    bool Drained);
