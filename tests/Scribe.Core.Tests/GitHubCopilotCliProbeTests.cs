using System.Collections;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// The Copilot CLI version probe runs a child process from cleanup initialization and from the
/// settings window. The old probe read standard output to the end before its wait and never drained
/// standard error, so a child that held a pipe open or filled standard error blocked detection
/// forever. These run the real probe logic against scripted child processes; nothing is spawned.
/// </summary>
public sealed class GitHubCopilotCliProbeTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_well_behaved_cli_reports_its_first_line()
    {
        var process = new ScriptedProcess(new StringReader("\r\n  1.0.34 (build abc)  \r\nsecond line\r\n"), new StringReader(""));
        process.Exit();

        var version = await GitHubCopilotCli.ReadVersionAsync(() => process, Generous, Generous, CancellationToken.None);

        Assert.Equal("1.0.34 (build abc)", version);
        Assert.Equal(0, process.Kills);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task A_child_that_holds_stdout_open_is_killed_at_the_deadline()
    {
        // The exact hang of the old probe: standard output never reaches end of file and the
        // process never exits. The pipe read cannot be cancelled, only ended by killing the child.
        var stdout = new BlockingReader();
        var process = new ScriptedProcess(stdout, new BlockingReader()) { OnKill = p => p.ExitAndClose() };

        var version = await GitHubCopilotCli.ReadVersionAsync(() => process, Short, Generous, CancellationToken.None)
            .WaitAsync(Generous);

        Assert.Null(version);
        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task A_child_that_floods_stderr_cannot_deadlock_the_probe()
    {
        // The child only exits once its error output has been read, which is how a real child
        // blocked on a full stderr pipe behaves. Reading stdout to the end first would hang here.
        var stderr = new FloodReader(totalChars: 1_000_000);
        var process = new ScriptedProcess(new StringReader("1.2.3\n"), stderr);
        stderr.Drained += process.Exit;

        var version = await GitHubCopilotCli.ReadVersionAsync(() => process, Generous, Generous, CancellationToken.None)
            .WaitAsync(Generous);

        Assert.Equal("1.2.3", version);
        Assert.Equal(1_000_000, stderr.CharsRead);
        Assert.Equal(0, process.Kills);
    }

    [Fact]
    public async Task A_child_that_never_stops_writing_is_killed_at_the_deadline()
    {
        var stdout = new FloodReader(totalChars: long.MaxValue);
        var process = new ScriptedProcess(stdout, new StringReader(""))
        {
            OnKill = p =>
            {
                stdout.Close();
                p.Exit();
            },
        };

        var version = await GitHubCopilotCli.ReadVersionAsync(() => process, Short, Generous, CancellationToken.None)
            .WaitAsync(Generous);

        // Killed at the deadline, however much it was still writing.
        Assert.Null(version);
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task A_child_that_survives_kill_is_abandoned_after_the_bound()
    {
        // Kill is asynchronous and a descendant can outlive it holding the pipe; detection must still
        // return rather than wait on it.
        var process = new ScriptedProcess(new BlockingReader(), new BlockingReader());

        var version = await GitHubCopilotCli.ReadVersionAsync(() => process, Short, Short, CancellationToken.None)
            .WaitAsync(Generous);

        Assert.Null(version);
        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task A_kill_that_throws_still_ends_detection_quietly()
    {
        var process = new ScriptedProcess(new BlockingReader(), new BlockingReader())
        {
            OnKill = _ => throw new AggregateException(new InvalidOperationException("descendant")),
        };

        var version = await GitHubCopilotCli.ReadVersionAsync(() => process, Short, Short, CancellationToken.None)
            .WaitAsync(Generous);

        Assert.Null(version);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Cancellation_kills_the_child_instead_of_waiting_out_the_deadline()
    {
        var process = new ScriptedProcess(new BlockingReader(), new BlockingReader()) { OnKill = p => p.ExitAndClose() };
        using var cancel = new CancellationTokenSource();
        var probe = GitHubCopilotCli.ReadVersionAsync(() => process, TimeSpan.FromHours(1), Generous, cancel.Token);

        await process.Started.Task.WaitAsync(Generous);
        cancel.Cancel();

        Assert.Null(await probe.WaitAsync(Generous));
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task A_file_that_is_not_a_program_reports_no_version_and_never_throws()
    {
        Assert.Null(await GitHubCopilotCli.ReadVersionAsync(
            () => throw new System.ComponentModel.Win32Exception("not a valid application"),
            Generous, Generous, CancellationToken.None));
        Assert.Null(await GitHubCopilotCli.ReadVersionAsync(() => null, Generous, Generous, CancellationToken.None));
    }

    [Fact]
    public async Task Draining_keeps_only_what_the_parse_needs_and_still_reads_to_the_end()
    {
        var reader = new FloodReader(totalChars: 100_000);

        var retained = await GitHubCopilotCli.DrainAsync(reader, retainChars: 10, CancellationToken.None);

        Assert.Equal(10, retained.Length);
        Assert.Equal(100_000, reader.CharsRead);
        Assert.Equal(string.Empty, await GitHubCopilotCli.DrainAsync(new StringReader("discarded"), 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \r\n \n", null)]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("\n\n  v2  \nnext", "v2")]
    public void Parsing_takes_the_first_non_blank_line(string? output, string? expected)
    {
        Assert.Equal(expected, GitHubCopilotCli.ParseVersion(output));
    }

    [Fact]
    public void A_line_too_long_to_be_a_version_is_bounded()
    {
        var parsed = GitHubCopilotCli.ParseVersion(new string('x', 5000));

        Assert.Equal(GitHubCopilotCli.MaxVersionChars, parsed!.Length);
    }

    /// <summary>A scripted child process. Exit and pipe closure are driven by the test.</summary>
    private sealed class ScriptedProcess(TextReader stdout, TextReader stderr) : GitHubCopilotCli.IVersionProbeProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _kills;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<ScriptedProcess>? OnKill { get; init; }

        public int Kills => Volatile.Read(ref _kills);

        public bool Disposed { get; private set; }

        public TextReader StandardOutput => stdout;

        public TextReader StandardError => stderr;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _exited.Task.WaitAsync(cancellationToken);
        }

        public void KillProcessTree()
        {
            Interlocked.Increment(ref _kills);
            OnKill?.Invoke(this);
        }

        public void Exit() => _exited.TrySetResult();

        public void ExitAndClose()
        {
            (stdout as BlockingReader)?.Close();
            (stderr as BlockingReader)?.Close();
            Exit();
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A pipe that never yields data and ignores cancellation, the way a synchronous redirected pipe
    /// read behaves: only closing it (the child dying) ends the read.
    /// </summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            new(_closed.Task);

        public override void Close() => _closed.TrySetResult(0);
    }

    /// <summary>A pipe that yields a fixed amount of text in chunks, then end of file.</summary>
    private sealed class FloodReader(long totalChars) : TextReader
    {
        private long _read;
        private volatile bool _closed;

        public event Action? Drained;

        public long CharsRead => Interlocked.Read(ref _read);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var remaining = totalChars - Interlocked.Read(ref _read);
            if (_closed || remaining <= 0)
            {
                Drained?.Invoke();
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..count].Fill('e');
            Interlocked.Add(ref _read, count);
            return count;
        }

        public override void Close() => _closed = true;
    }
}

/// <summary>
/// The model is chosen through the typed SDK surface, and the Copilot runtime's child environment is
/// built explicitly instead of mutating this process's environment around client startup.
/// </summary>
public sealed class GitHubCopilotConfigurationTests
{
    [Fact]
    public void The_child_environment_carries_everything_but_an_inherited_model()
    {
        var inherited = new Hashtable
        {
            ["PATH"] = @"C:\tools;C:\Windows",
            ["GH_TOKEN"] = "inherited-auth",
            ["APPDATA"] = @"C:\Users\someone\AppData\Roaming",
            ["github_copilot_model"] = "ambient-model-the-user-never-chose",
            [42] = "not a string key",
        };

        var environment = GitHubCopilotCli.BuildRuntimeEnvironment(inherited, model: null);

        Assert.Equal(@"C:\tools;C:\Windows", environment["Path"]);
        Assert.Equal("inherited-auth", environment["GH_TOKEN"]);
        Assert.Equal(@"C:\Users\someone\AppData\Roaming", environment["APPDATA"]);
        Assert.False(environment.ContainsKey(GitHubCopilotCli.ModelVariable));
        Assert.Equal(3, environment.Count);
    }

    [Theory]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("  claude-sonnet-4  ", "claude-sonnet-4")]
    public void A_chosen_model_is_published_to_the_child_only(string model, string expected)
    {
        var inherited = new Hashtable { ["PATH"] = @"C:\bin", [GitHubCopilotCli.ModelVariable] = "stale" };

        var environment = GitHubCopilotCli.BuildRuntimeEnvironment(inherited, model);

        Assert.Equal(expected, environment[GitHubCopilotCli.ModelVariable]);
        Assert.Equal(@"C:\bin", environment["PATH"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_model_leaves_the_variable_unset_rather_than_empty(string? model)
    {
        var environment = GitHubCopilotCli.BuildRuntimeEnvironment(new Hashtable { ["PATH"] = "x" }, model);

        Assert.False(environment.ContainsKey(GitHubCopilotCli.ModelVariable));
    }

    [Fact]
    public void Environment_names_are_case_insensitive_like_windows()
    {
        var environment = GitHubCopilotCli.BuildRuntimeEnvironment(new Hashtable { ["Path"] = "only" }, "m");

        Assert.Equal("only", environment["PATH"]);
    }

    [Fact]
    public void The_session_config_matches_the_convenience_overload_plus_the_model()
    {
        // Agent Framework dotnet-1.20.0 GitHubCopilotAgent.GetSessionConfig: an appended system
        // message, no tools, no permission handler. The only addition is Model.
        var config = GitHubCopilotAgentFactory.BuildSessionConfig("Clean up this dictation.", " gpt-5 ");

        Assert.Equal("gpt-5", config.Model);
        Assert.NotNull(config.SystemMessage);
        Assert.Equal(GitHub.Copilot.SystemMessageMode.Append, config.SystemMessage.Mode);
        Assert.Equal("Clean up this dictation.", config.SystemMessage.Content);
        Assert.Null(config.Tools);
        Assert.Null(config.OnPermissionRequest);
        Assert.Null(config.AvailableTools);
        Assert.Null(config.ExcludedTools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_model_leaves_the_choice_to_the_cli(string? model)
    {
        Assert.Null(GitHubCopilotAgentFactory.BuildSessionConfig("style", model).Model);
    }

    [Fact]
    public void Every_agent_gets_its_own_configuration()
    {
        var first = GitHubCopilotAgentFactory.BuildSessionConfig("style", "gpt-5");
        var second = GitHubCopilotAgentFactory.BuildSessionConfig("style", "gpt-5");

        Assert.NotSame(first, second);
        Assert.NotSame(first.SystemMessage, second.SystemMessage);
    }
}

/// <summary>
/// The probe against a real child process: Windows' own <c>cmd.exe</c> printing a version, which is
/// the only way to see the real <see cref="System.Diagnostics.Process"/> stream ownership at work.
/// </summary>
public sealed class GitHubCopilotCliRealProcessTests
{
    [Fact]
    public async Task A_real_probe_reads_the_version_and_leaves_no_redirected_reader_open()
    {
        GitHubCopilotCli.IVersionProbeProcess? probe = null;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // /d skips any AutoRun command the machine has configured, so only the echo runs.
        var version = await GitHubCopilotCli.ReadVersionAsync(
            () => probe = GitHubCopilotCli.StartProbeProcessForTesting(cmd, "/d /c echo 1.2.3"),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal("1.2.3", version);
        Assert.NotNull(probe);

        // Process.Dispose does not close readers the caller has read from; the probe must.
        Assert.Throws<ObjectDisposedException>(() => probe!.StandardOutput.Peek());
        Assert.Throws<ObjectDisposedException>(() => probe!.StandardError.Peek());
    }
}
