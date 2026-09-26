using System.Diagnostics;
using System.Text;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Tests;

public sealed class ProcessExitTests
{
    // Only a hang guard. Each line is handed over by System.Diagnostics.Process's reader on the thread pool, which the
    // rest of a parallel test run can hold up past a 2 s output limit (loaded local full runs have seen stderr still
    // empty when the wait returned), so a test that needs a line waits for it rather than racing the limit.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    // What an az invocation looks like when something it started keeps its output open: cmd's start passes the
    // redirected pipe on to the ping it starts, which runs on for 30 s after cmd has written its output and exited.
    [Fact]
    public async Task Something_left_holding_the_output_does_not_hold_the_wait_and_the_output_is_kept()
    {
        using var run = Run.Start(
            "echo written to stdout & echo written to stderr 1>&2 & start \"\" /b ping -n 30 127.0.0.1 >nul & exit /b 3");
        try
        {
            var clock = Stopwatch.StartNew();
            await ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(2), CancellationToken.None);

            // The wait it replaces, WaitForExitAsync, has not finished: the ping still holds the output, so the wait
            // above did not wait for its end.
            Assert.False(
                run.Process.WaitForExitAsync().IsCompleted,
                $"the wait lasted until the end of the output ({clock.Elapsed.TotalSeconds:0.0} s)");
            Assert.Equal(3, run.Process.ExitCode);

            // What cmd wrote before it exited is kept, however late the reader hands it over.
            Assert.True(
                SpinWait.SpinUntil(() => run.StandardOutput.Contains("written to stdout", StringComparison.Ordinal), Generous),
                $"stdout: \"{run.StandardOutput}\"");
            Assert.True(
                SpinWait.SpinUntil(() => run.StandardError.Contains("written to stderr", StringComparison.Ordinal), Generous),
                $"stderr: \"{run.StandardError}\"");

            // And it goes on waiting for the end of the output, which the ping holds.
            using var old = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Process.WaitForExitAsync(old.Token));
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    // The limit is the hang guard here: with nothing holding the output its end comes at the exit, and a limit as short
    // as production's 2 s would race the reader's hand-over of the last line, which comes only with that end.
    [Fact]
    public async Task With_nothing_holding_the_output_it_is_read_to_the_end()
    {
        using var run = Run.Start("echo first & echo second & <nul set /p =last line without a break& exit /b 0");

        await ProcessExit.WaitAsync(run.Process, Generous, CancellationToken.None);

        Assert.Equal(0, run.Process.ExitCode);
        Assert.Equal(["first ", "second ", "last line without a break"], run.StandardOutputLines);
    }

    // The end of the output is still awaited after the exit, within the limit: here a ping the command started holds
    // the output for about a second after cmd has exited, and the last line, which has no line break, is only
    // delivered at the end (AsyncStreamReader in System.Diagnostics.Process).
    [Fact]
    public async Task An_output_that_ends_within_the_limit_is_waited_for_and_read_to_the_end()
    {
        using var run = Run.Start(
            "echo first & start \"\" /b ping -n 2 127.0.0.1 >nul & <nul set /p =last line without a break& exit /b 0");
        try
        {
            var clock = Stopwatch.StartNew();
            await ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(20), CancellationToken.None);

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the wait lasted {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.Equal(["first ", "last line without a break"], run.StandardOutputLines);
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    [Fact]
    public async Task Cancelling_ends_the_wait_for_the_exit()
    {
        using var run = Run.Start("ping -n 30 127.0.0.1 >nul");
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(2), cancel.Token));
            Assert.False(run.Process.HasExited);
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    /// <summary>A cmd.exe with its output redirected and read as RunAsync reads it.</summary>
    private sealed class Run : IDisposable
    {
        private readonly List<string> _stdout = [];
        private readonly StringBuilder _stderr = new();

        private Run(Process process) => Process = process;

        public Process Process { get; }

        public string StandardOutput
        {
            get
            {
                lock (_stdout)
                {
                    return string.Join('\n', _stdout);
                }
            }
        }

        public IReadOnlyList<string> StandardOutputLines
        {
            get
            {
                lock (_stdout)
                {
                    return [.. _stdout];
                }
            }
        }

        public string StandardError
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.ToString();
                }
            }
        }

        public static Run Start(string command)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = $"/d /c \"{command}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };

            var run = new Run(process);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (run._stdout)
                    {
                        run._stdout.Add(e.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (run._stderr)
                    {
                        run._stderr.AppendLine(e.Data);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return run;
        }

        // Ends the cmd.exe and what it started, even after cmd has exited: KillTree follows a child only if it names
        // the process as its parent and started after it (IsParentOf in Process.Win32.cs), so nothing else is taken.
        // The pings end themselves within 30 s anyway.
        public void EndLeftovers()
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public void Dispose() => Process.Dispose();
    }
}
