using System.Diagnostics;
using System.Text;
using Scribe.Core.Infrastructure;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

public sealed class ProcessExitTests
{
    // Only hang guards: no check here waits on a clock to decide anything. The output is held by a sort that cmd starts,
    // which inherits the redirected pipes and holds them until the test closes its standard input (Run.ReleaseOutput), so
    // the output ends when the test says so; the output limit runs on a clock only the test moves, except in the first
    // test, which keeps the production path end to end. Each line is handed over by System.Diagnostics.Process's reader on
    // the thread pool, so a check that needs a line waits for it (stream TR, round 2, A1).
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private const string HoldOutput = "start \"\" /b sort >nul";

    // What an az invocation looks like when something it started keeps its output open, through production's clock and
    // limit: the wait returns with the output still held, and what cmd wrote before it exited is kept.
    [Fact]
    public async Task Something_left_holding_the_output_does_not_hold_the_wait_and_the_output_is_kept()
    {
        using var run = Run.Start($"echo written to stdout & echo written to stderr 1>&2 & {HoldOutput} & exit /b 3");
        try
        {
            await ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(2), CancellationToken.None).WaitAsync(Generous);

            // The wait it replaces, WaitForExitAsync, is still waiting: the holder has the output, so the wait above did not
            // wait for its end.
            var outputEnd = run.Process.WaitForExitAsync();
            Assert.False(outputEnd.IsCompleted, "The wait lasted until the end of the output.");
            Assert.Equal(3, run.Process.ExitCode);

            // What cmd wrote before it exited is kept, however late the reader hands it over.
            Assert.True(
                SpinWait.SpinUntil(() => run.StandardOutput.Contains("written to stdout", StringComparison.Ordinal), Generous),
                $"stdout: \"{run.StandardOutput}\"");
            Assert.True(
                SpinWait.SpinUntil(() => run.StandardError.Contains("written to stderr", StringComparison.Ordinal), Generous),
                $"stderr: \"{run.StandardError}\"");

            // Held until the test lets go, and then the output ends.
            Assert.False(outputEnd.IsCompleted, "The output ended before the test let it go.");
            run.ReleaseOutput();
            await outputEnd.WaitAsync(Generous);
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    // With nothing holding the output its end comes with the exit, and the wait reads it to the end. Production's 2 s is
    // the limit, on the test's clock, so the reader's hand-over of the last line, which comes only with that end, never
    // races it: the wait can end only because the output did.
    [Fact]
    public async Task With_nothing_holding_the_output_it_is_read_to_the_end()
    {
        using var run = Run.Start("echo first & echo second & <nul set /p =last line without a break& exit /b 0");
        var clock = new ManualTimeProvider();

        await ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(2), clock, CancellationToken.None).WaitAsync(Generous);

        Assert.Equal(0, run.Process.ExitCode);
        Assert.Equal(["first ", "second ", "last line without a break"], run.StandardOutputLines);
    }

    // The end of the output is still awaited after the exit, within the limit: the holder keeps the output open after cmd
    // has exited, the wait goes on while it does, and once the test lets go the wait ends with the output, the last line,
    // which has no line break, delivered only then (AsyncStreamReader in System.Diagnostics.Process).
    [Fact]
    public async Task An_output_that_ends_within_the_limit_is_waited_for_and_read_to_the_end()
    {
        using var run = Run.Start($"echo first & {HoldOutput} & <nul set /p =last line without a break& exit /b 0");
        var (clock, limitArmed) = ClockThatReportsTheLimit(run);
        try
        {
            var waiting = ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(20), clock, CancellationToken.None);
            var limit = await limitArmed.Task.WaitAsync(Generous);
            Assert.True(limit.ExitedWhenArmed, "The limit was armed before the exit.");
            Assert.Equal(TimeSpan.FromSeconds(20), limit.Timer.DueTime);
            Assert.False(waiting.IsCompleted, "The wait ended while the output was still held and its limit had not passed.");

            run.ReleaseOutput();
            await waiting.WaitAsync(Generous);

            Assert.Equal(["first ", "last line without a break"], run.StandardOutputLines);
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    // What ends a wait on a held output is the limit its caller passes, counted from the exit (review round 2, A1): nothing
    // is armed while the process runs, the limit is armed with exactly that value once the exit is seen, and when it passes
    // the wait returns with the output still held. Any limit, not just production's, so a constant in its place fails.
    [Theory]
    [InlineData(2_000)]
    [InlineData(7_000)]
    public async Task The_limit_the_caller_passes_decides_when_a_held_output_stops_being_awaited(int limitMs)
    {
        // cmd waits for a line before it goes on to start the holder and exit, so the test decides when it exits.
        using var run = Run.Start($"set /p go=& echo written & {HoldOutput} & exit /b 0");
        var (clock, limitArmed) = ClockThatReportsTheLimit(run);
        try
        {
            var waiting = ProcessExit.WaitAsync(run.Process, TimeSpan.FromMilliseconds(limitMs), clock, CancellationToken.None);
            Assert.Empty(clock.Timers);

            run.Send("go");
            var limit = await limitArmed.Task.WaitAsync(Generous);
            Assert.True(limit.ExitedWhenArmed, "The limit was armed before the exit.");
            Assert.Equal(TimeSpan.FromMilliseconds(limitMs), limit.Timer.DueTime);
            Assert.False(waiting.IsCompleted, "The wait ended before its limit passed.");

            limit.Timer.Fire();
            await waiting.WaitAsync(Generous);
            Assert.False(run.Process.WaitForExitAsync().IsCompleted, "The output ended before the limit did.");
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    [Fact]
    public async Task Cancelling_ends_the_wait_for_the_exit()
    {
        // A sort in the foreground: cmd runs until the test closes its input.
        using var run = Run.Start("sort >nul");
        try
        {
            using var cancel = new CancellationTokenSource();
            var waiting = ProcessExit.WaitAsync(run.Process, TimeSpan.FromSeconds(2), cancel.Token);
            Assert.False(waiting.IsCompleted);

            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Generous));
            Assert.False(run.Process.HasExited);
        }
        finally
        {
            run.EndLeftovers();
        }
    }

    // A clock for the output limit, and what it saw when the wait armed it: the timer, and whether the process had exited.
    private static (ManualTimeProvider Clock, TaskCompletionSource<(ManualTimer Timer, bool ExitedWhenArmed)> Armed)
        ClockThatReportsTheLimit(Run run)
    {
        var clock = new ManualTimeProvider();
        var armed = new TaskCompletionSource<(ManualTimer Timer, bool ExitedWhenArmed)>(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Created = timer => armed.TrySetResult((timer, run.Process.HasExited));
        return (clock, armed);
    }
    /// <summary>A cmd.exe with its output redirected and read as RunAsync reads it, and its input a pipe the test holds.</summary>
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
                    RedirectStandardInput = true,
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

        // A line for cmd's set /p, which waits for one.
        public void Send(string line)
        {
            Process.StandardInput.WriteLine(line);
            Process.StandardInput.Flush();
        }

        // Ends the holder's input: it exits, and the output it held ends. Closing the pipe ends it even if nothing wrote a
        // line, and the pipe also closes if the test process dies, so a holder never outlives the test.
        public void ReleaseOutput()
        {
            try
            {
                Process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        // Lets the holder go, then ends the cmd.exe and what it started, even after cmd has exited: KillTree follows a
        // child only if it names the process as its parent and started after it (IsParentOf in Process.Win32.cs), so
        // nothing else is taken.
        public void EndLeftovers()
        {
            ReleaseOutput();
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
