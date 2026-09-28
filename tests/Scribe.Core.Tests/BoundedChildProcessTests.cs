using System.Diagnostics;
using Scribe.Benchmarks;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-IMPL-A-06: the startup probe runs its child under a real deadline, with both streams drained while it runs. The
/// fixtures are Scribe.LogAppendChild's headless modes (no window, no input), started from the tests' own build.
/// </summary>
public sealed class BoundedChildProcessTests
{
    [Fact]
    public void A_child_that_holds_its_streams_open_is_ended_at_the_deadline_and_nothing_else_is()
    {
        // The same image, started beside it: not the run's child, so not the run's to end.
        using var sibling = Process.Start(Fixture("hold-stdout", redirect: true))
            ?? throw new InvalidOperationException("The sibling fixture did not start.");
        try
        {
            var clock = Stopwatch.StartNew();
            var run = BoundedChildProcess.Run(Fixture("hold-stdout", redirect: false), TimeSpan.FromSeconds(3));
            clock.Stop();

            Assert.True(run.TimedOut);
            Assert.True(run.Exited);
            Assert.True(run.Drained);
            Assert.Contains("ready", run.Output, StringComparison.Ordinal);

            // The fixture would hold its streams for a minute; the deadline, not its exit, ended the run.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The run took " + clock.Elapsed);
            Assert.NotEqual(sibling.Id, run.ProcessId);
            Assert.False(sibling.HasExited);
        }
        finally
        {
            sibling.Kill(entireProcessTree: true);
            sibling.WaitForExit(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void A_child_that_floods_standard_error_before_it_writes_standard_output_finishes()
    {
        const int Bound = 64 * 1024;

        var run = BoundedChildProcess.Run(Fixture("flood-stderr", redirect: false), TimeSpan.FromSeconds(30), captureChars: Bound);

        Assert.False(run.TimedOut);
        Assert.True(run.Exited);
        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Drained);
        Assert.Equal("done", run.Output.Trim());
        Assert.False(run.OutputTruncated);

        // Four megabytes were read to their end and the first 64 K characters kept.
        Assert.True(run.ErrorsTruncated);
        Assert.Equal(Bound, run.Errors.Length);
        Assert.True(run.Errors.AsSpan().Trim('e').IsEmpty);
    }

    private static ProcessStartInfo Fixture(string name, bool redirect)
    {
        var info = new ProcessStartInfo(AppendOnlyLogTests.ChildExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        info.ArgumentList.Add("fixture");
        info.ArgumentList.Add(name);
        return info;
    }
}
