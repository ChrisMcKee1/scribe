using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class OllamaServiceControllerTests
{
    [Fact]
    public void The_child_serves_loopback_with_cloud_off_without_changing_the_parent_environment()
    {
        var parentHost = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        var parentCloud = Environment.GetEnvironmentVariable("OLLAMA_NO_CLOUD");
        var info = OllamaServiceController.StartInfo("C:\\installed\\ollama.exe");
        Assert.Equal(["serve"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal("127.0.0.1:11434", info.Environment["OLLAMA_HOST"]);
        Assert.Equal("1", info.Environment["OLLAMA_NO_CLOUD"]);
        Assert.Equal(parentHost, Environment.GetEnvironmentVariable("OLLAMA_HOST"));
        Assert.Equal(parentCloud, Environment.GetEnvironmentVariable("OLLAMA_NO_CLOUD"));
    }

    [Fact]
    public async Task A_server_already_running_outside_Scribe_is_never_started_or_stopped()
    {
        var starts = 0;
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(true),
            () => { starts++; throw new InvalidOperationException(); });

        var state = await controller.StartAsync();
        Assert.True(state.Running);
        Assert.False(state.Owned);
        Assert.False(state.CanAct);
        Assert.Equal("Stop Ollama", state.ButtonText);
        Assert.Equal(0, starts);
        Assert.Equal(state, await controller.StopAsync());
    }

    [Fact]
    public async Task The_button_changes_and_only_the_owned_process_is_stopped()
    {
        var running = false;
        var process = new FakeProcess(() => running = false);
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(running),
            () => { running = true; return process; });

        Assert.Equal("Start Ollama", (await controller.ReadAsync()).ButtonText);
        var started = await controller.StartAsync();
        Assert.True(started.Owned);
        Assert.True(started.CanAct);
        Assert.Equal("Stop Ollama", started.ButtonText);
        Assert.Equal(0, process.Kills);

        var stopped = await controller.StopAsync();
        Assert.False(stopped.Running);
        Assert.Equal("Start Ollama", stopped.ButtonText);
        Assert.Equal(1, process.Kills);
        await controller.StopAsync();
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task Scribe_shutdown_stops_only_the_process_it_started()
    {
        var running = false;
        var process = new FakeProcess(() => running = false);
        var controller = new OllamaServiceController(
            _ => Task.FromResult(running),
            () => { running = true; return process; });
        await controller.StartAsync();

        controller.Dispose();
        controller.Dispose();

        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
        Assert.False(running);
    }

    [Fact]
    public async Task A_failed_start_is_reported_without_quoting_the_executable_path()
    {
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(false),
            () => throw new FileNotFoundException("private executable path"));

        var state = await controller.StartAsync();

        Assert.False(state.Running);
        Assert.NotNull(state.Error);
        Assert.DoesNotContain("private", state.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_that_times_out_retires_the_process_it_created()
    {
        var process = new FakeProcess();
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(false),
            () => process,
            startBound: TimeSpan.Zero);

        var state = await controller.StartAsync();

        Assert.NotNull(state.Error);
        Assert.False(state.Running);
        Assert.Equal(1, process.Kills);
    }

    [Fact]
    public async Task Closing_Scribe_during_startup_cancels_the_wait_and_retires_its_process()
    {
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess();
        var reads = 0;
        var controller = new OllamaServiceController(
            async ct =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    return false;
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return false;
            },
            () => { launched.SetResult(); return process; });
        var starting = controller.StartAsync();
        await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, process.Kills);
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_Settings_ends_observation_not_an_initial_start_or_an_unhealthy_replacement(bool replacing)
    {
        var running = false;
        var pending = false;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = new FakeProcess(() => running = false);
        var process = new FakeProcess(() => running = false);
        var starts = 0;
        using var controller = new OllamaServiceController(
            async ct =>
            {
                if (!pending)
                {
                    return running;
                }

                running = await ready.Task.WaitAsync(ct);
                pending = false;
                return running;
            },
            () =>
            {
                pending = true;
                starts++;
                launched.TrySetResult();
                return replacing && starts == 1 ? previous : process;
            });
        if (replacing)
        {
            ready.SetResult(true);
            await controller.StartAsync();
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running = false;
        }

        using var window = new CancellationTokenSource();
        var start = controller.StartAsync();
        var observation = start.WaitAsync(window.Token);
        try
        {
            await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, process.Kills);
            Assert.False(process.HasExited);
            ready.TrySetResult(true);
            var state = await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(state.Running);
            Assert.True(state.Owned);
            if (replacing)
            {
                Assert.Equal(1, previous.Kills);
                Assert.True(previous.Disposed);
            }
        }
        finally
        {
            ready.TrySetResult(true);
            controller.Dispose();
            try { await start.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task A_stop_that_did_not_observe_exit_keeps_ownership_for_a_retry()
    {
        var running = false;
        var process = new FakeProcess { ExitsOnKill = false };
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(running),
            () => { running = true; return process; });
        await controller.StartAsync();

        var state = await controller.StopAsync();

        Assert.True(state.Running);
        Assert.True(state.Owned);
        Assert.NotNull(state.Error);
        Assert.False(process.Disposed);
        process.ExitsOnKill = true;
        await controller.StopAsync();
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Closing_Settings_while_stop_waits_for_a_read_does_not_cancel_the_admitted_stop()
    {
        var running = false;
        var waitForRead = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(() => running = false);
        using var controller = new OllamaServiceController(
            async ct =>
            {
                if (waitForRead)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }

                return running;
            },
            () => { running = true; return process; });
        await controller.StartAsync();
        waitForRead = true;
        var read = controller.ReadAsync();
        using var window = new CancellationTokenSource();
        var stop = controller.StopAsync();
        var observation = stop.WaitAsync(window.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, process.Kills);
            waitForRead = false;
            release.TrySetResult();

            var state = await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(state.Running);
            Assert.False(state.Owned);
            Assert.Equal(1, process.Kills);
            Assert.True(process.Disposed);
        }
        finally
        {
            release.TrySetResult();
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unhealthy_owned_server_is_replaced_only_after_its_exit_is_observed(bool refused)
    {
        var running = false;
        var previous = new FakeProcess(() => running = false) { ExitsOnKill = !refused };
        var replacement = new FakeProcess(() => running = false);
        var starts = 0;
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(running),
            () =>
            {
                running = true;
                return ++starts == 1 ? previous : replacement;
            });
        await controller.StartAsync();
        running = false;

        var state = await controller.StartAsync();

        Assert.True(state.Owned);
        Assert.Equal(1, previous.Kills);
        Assert.Equal(!refused, previous.Disposed);
        Assert.Equal(refused ? 1 : 2, starts);
        if (refused)
        {
            Assert.NotNull(state.Error);
            previous.ExitsOnKill = true;
            state = await controller.StartAsync();
            Assert.True(state.Owned);
            Assert.Null(state.Error);
            Assert.Equal(2, starts);
            Assert.True(previous.Disposed);
        }
        else
        {
            Assert.Null(state.Error);
        }
    }

    [Fact]
    public async Task A_self_exited_process_is_disposed_and_forgotten_without_claiming_an_external_replacement()
    {
        var running = false;
        var process = new FakeProcess(() => running = false);
        using var controller = new OllamaServiceController(
            _ => Task.FromResult(running),
            () => { running = true; return process; });
        await controller.StartAsync();
        process.Exit();
        running = true; // A different process now serves that address, not this controller's child.

        var state = await controller.ReadAsync();
        var stopped = await controller.StopAsync();

        Assert.True(process.Disposed);
        Assert.Equal(0, process.Kills);
        Assert.True(state.Running);
        Assert.False(state.Owned);
        Assert.Equal(state, stopped);
    }

    [Fact]
    public async Task A_throwing_logger_cannot_discard_ownership_after_a_refused_stop_or_prevent_shutdown()
    {
        var running = false;
        var process = new FakeProcess(() => running = false) { KillFailure = new Win32Exception(5, "synthetic private path") };
        var controller = new OllamaServiceController(
            _ => Task.FromResult(running),
            () => { running = true; return process; },
            log: new ThrowingLogger());
        try
        {
            await controller.StartAsync();
            var state = await controller.StopAsync();
            Assert.True(state.Owned);
            Assert.NotNull(state.Error);
            Assert.False(process.Disposed);
            process.KillFailure = null;

            controller.Dispose();

            Assert.True(process.Disposed);
            Assert.False(running);
        }
        finally
        {
            process.KillFailure = null;
            controller.Dispose();
        }
    }

    private sealed class FakeProcess(Action? stopped = null) : IOllamaOwnedProcess
    {
        public bool HasExited { get; private set; }
        public bool ExitsOnKill { get; set; } = true;
        public int Kills { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? KillFailure { get; set; }
        public void Kill()
        {
            Kills++;
            if (KillFailure is not null)
            {
                throw KillFailure;
            }

            if (ExitsOnKill)
            {
                Exit();
            }
        }

        public void Exit()
        {
            HasExited = true;
            stopped?.Invoke();
        }

        public bool WaitForExit(int milliseconds) => HasExited;
        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => throw new InvalidOperationException("logger unavailable");
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger unavailable");
    }
}
