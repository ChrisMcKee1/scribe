using System.Collections;
using System.Reflection;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Tests.CleanupLogging;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests.Vocabulary;

public sealed class GitHubCopilotSessionLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "please ask about the harbour lanterns today";
    private const string Instructions = "Clean up the dictation with kestrelmoor vocabulary.";
    private const string ProviderSecret = "private quota for harbourlantern account";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" cleanup-model ")]
    public async Task Each_run_deletes_its_own_session_and_never_owns_the_shared_client(string? model)
    {
        await using var runtime = new FakeCopilotRuntime { Answer = _ => Dictated };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var config = GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, model);
        Assert.Null(config.Tools);
        Assert.Null(config.OnPermissionRequest);
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, model, "ScribeCleanup");

        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);
        Assert.Empty(RegisteredSessions(client));
        // Reusing both the agent and client must work without another connect or a reused session id.
        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);
        Assert.Empty(RegisteredSessions(client));
        Assert.Single(runtime.Requests, request => request.Method == "connect");
        Assert.Equal(2, runtime.Creates.Count);
        Assert.NotEqual(runtime.Creates[0].SessionId, runtime.Creates[1].SessionId);
        foreach (var create in runtime.Creates)
        {
            var calls = CallsFor(runtime, create.SessionId!);
            Assert.Equal(["session.create", "session.send", "session.detach", "session.delete"], calls.Select(call => call.Method));
            Assert.Equal(Instructions, create.SystemMessage);
            if (string.IsNullOrWhiteSpace(model))
            {
                Assert.False(create.Params.TryGetProperty("model", out var chosen) && chosen.ValueKind != System.Text.Json.JsonValueKind.Null);
            }
            else
            {
                Assert.Equal(model.Trim(), create.Params.GetProperty("model").GetString());
            }
        }
    }

    [Theory]
    [InlineData("session.create")]
    [InlineData("session.send")]
    public async Task A_failed_request_still_deletes_the_known_session_id(string failedMethod)
    {
        await using var runtime = new FakeCopilotRuntime
        {
            RequestError = request => request.Method == failedMethod ? ProviderSecret : null,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, null, "ScribeCleanup");

        var failure = await Record.ExceptionAsync(() => RunAsync(agent).WaitAsync(Bound));

        Assert.NotNull(failure);
        var creation = Assert.Single(runtime.Creates);
        var calls = CallsFor(runtime, creation.SessionId!);
        Assert.Equal(
            failedMethod == "session.create"
                ? ["session.create", "session.delete"]
                : new[] { "session.create", "session.send", "session.detach", "session.delete" },
            calls.Select(call => call.Method));
        Assert.Empty(RegisteredSessions(client));
    }

    [Theory]
    [InlineData("session.create", false)]
    [InlineData("session.send", false)]
    [InlineData("session.create", true)]
    [InlineData("session.send", true)]
    public async Task A_cancelled_caller_still_detaches_and_deletes_with_an_independent_token(string heldMethod, bool deletionFails)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new FakeCopilotRuntime
        {
            BeforeAnswer = (request, token) => request.Method == heldMethod ? held.Task.WaitAsync(token) : Task.CompletedTask,
            DeletionError = deletionFails ? ProviderSecret : null,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, null, "ScribeCleanup");
        var arrived = runtime.Next(request => request.Method == heldMethod);

        var running = RunAsync(agent, cancellation.Token);
        await arrived.WaitAsync(Bound);
        var deletion = runtime.Next(request => request.Method == "session.delete");
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bound));

        var creation = Assert.Single(runtime.Creates);
        Assert.Equal(creation.SessionId, (await deletion.WaitAsync(Bound)).SessionId);
        Assert.Equal(
            heldMethod == "session.create" ? 0 : 1,
            CallsFor(runtime, creation.SessionId!).Count(request => request.Method == "session.detach"));
        Assert.Empty(RegisteredSessions(client));
        held.TrySetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deletion_failure_never_fails_the_answer_and_only_its_shape_is_logged(bool rpcError)
    {
        await using var runtime = new FakeCopilotRuntime
        {
            Answer = _ => Dictated,
            DeletionError = rpcError ? null : ProviderSecret,
            RequestError = request => rpcError && request.Method == "session.delete" ? ProviderSecret : null,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, null, "ScribeCleanup", log);

        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("cleanup delete failed", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(ProviderSecret, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Dictated, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Instructions, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(runtime.Creates).SessionId!, log.AllText, StringComparison.Ordinal);
        Assert.Empty(RegisteredSessions(client));
        var calls = runtime.Requests.Count;
        await client.StopAsync().WaitAsync(Bound);
        Assert.Equal(calls, runtime.Requests.Count);
    }

    [Fact]
    public async Task An_error_the_sdk_swallows_during_detach_still_deletes_and_releases_the_session()
    {
        await using var runtime = new FakeCopilotRuntime
        {
            Answer = _ => Dictated,
            RequestError = request => request.Method == "session.detach" ? ProviderSecret : null,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, null, "ScribeCleanup", log);

        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);
        Assert.Single(runtime.Requests, request => request.Method == "session.detach");
        Assert.Single(runtime.Requests, request => request.Method == "session.delete");
        Assert.Empty(RegisteredSessions(client));
        Assert.DoesNotContain(ProviderSecret, log.AllText, StringComparison.Ordinal);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData("session.create")]
    [InlineData("session.send")]
    public async Task A_deletion_failure_never_replaces_the_runs_original_failure(string failedMethod)
    {
        await using var runtime = new FakeCopilotRuntime
        {
            RequestError = request => request.Method == failedMethod ? ProviderSecret : null,
            DeletionError = "deletion denied",
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, null, "ScribeCleanup", log);

        var failure = await Record.ExceptionAsync(() => RunAsync(agent).WaitAsync(Bound));

        Assert.NotNull(failure);
        Assert.Contains(ProviderSecret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("deletion denied", failure.Message, StringComparison.Ordinal);
        Assert.Empty(RegisteredSessions(client));
        Assert.DoesNotContain(ProviderSecret, log.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session.detach")]
    [InlineData("session.delete")]
    public async Task A_stalled_cleanup_step_is_bounded_without_failing_the_answer(string heldMethod)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new FakeCopilotRuntime
        {
            Answer = _ => Dictated,
            BeforeAnswer = (request, token) => request.Method == heldMethod ? held.Task.WaitAsync(token) : Task.CompletedTask,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var clock = new CleanupClock();
        var agent = new GitHubCopilotCleanupAgent(
            client, new GitHubCopilotClientLifetime(client),
            GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null), "ScribeCleanup", log, clock);
        var arrived = runtime.Next(request => request.Method == heldMethod);

        var running = RunAsync(agent);
        await arrived.WaitAsync(Bound);
        Assert.Equal(GitHubCopilotCleanupAgent.CleanupStepTimeout, clock.Timers[^1].DueTime);
        clock.Timers[^1].Fire();

        Assert.Equal(Dictated, (await running.WaitAsync(Bound)).Text);
        Assert.Single(runtime.Requests, request => request.Method == "session.delete");
        Assert.Empty(RegisteredSessions(client));
        Assert.Equal(2, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Null(Assert.Single(log.Entries).Exception);
        held.TrySetResult();
    }

    [Fact]
    public async Task If_both_cleanup_steps_stall_the_caller_is_bounded_and_the_owner_still_can_disconnect()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new FakeCopilotRuntime
        {
            Answer = _ => Dictated,
            BeforeAnswer = (request, token) =>
                request.Method is "session.detach" or "session.delete" ? held.Task.WaitAsync(token) : Task.CompletedTask,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var clock = new CleanupClock();
        var agent = new GitHubCopilotCleanupAgent(
            client, new GitHubCopilotClientLifetime(client),
            GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null), "ScribeCleanup", log, clock);
        var detached = runtime.Next(request => request.Method == "session.detach");
        var deleted = runtime.Next(request => request.Method == "session.delete");

        var running = RunAsync(agent);
        await detached.WaitAsync(Bound);
        clock.Timers[^1].Fire();
        await deleted.WaitAsync(Bound);
        clock.Timers[^1].Fire();

        Assert.Equal(Dictated, (await running.WaitAsync(Bound)).Text);
        Assert.Equal(2, log.Entries.Count);
        // The SDK cannot unregister a still-pending detach after an unsuccessful deletion without its owner stopping
        // the connection. The agent must not stop the shared client to close that gap.
        Assert.Single(RegisteredSessions(client));
        await client.ForceStopAsync().WaitAsync(Bound);
        Assert.Empty(RegisteredSessions(client));
        held.TrySetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_logger_that_throws_during_cleanup_cannot_fail_the_answer(bool throwsWhenChecking)
    {
        await using var runtime = new FakeCopilotRuntime
        {
            Answer = _ => Dictated,
            DeletionError = ProviderSecret,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new FailingLogger(throwsWhenChecking);
        var agent = GitHubCopilotAgentFactory.Create(
            client, new GitHubCopilotClientLifetime(client), Instructions, null, "ScribeCleanup", log);

        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);
        Assert.Equal(1, log.Failures);
        Assert.Empty(RegisteredSessions(client));
    }

    private static CopilotClient ClientFor(FakeCopilotRuntime runtime) =>
        new(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(runtime.Url) });

    private static async Task<AgentResponse> RunAsync(AIAgent agent, CancellationToken cancellationToken = default)
    {
        using (new CleanupAdmission(CleanupRequestKind.Dictation, AiVocabularyScope.None, null).Enter())
        {
            return await agent.RunAsync(Dictated, cancellationToken: cancellationToken);
        }
    }

    private static IReadOnlyList<CopilotRuntimeRequest> CallsFor(FakeCopilotRuntime runtime, string sessionId) =>
        [.. runtime.Requests.Where(request => request.SessionId == sessionId)];

    // Pin the SDK's strong session registry, not just its RPCs: a successful detach or delete must free that reference.
    private static IReadOnlyList<object> RegisteredSessions(CopilotClient client)
    {
        var field = typeof(CopilotClient).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var sessions = Assert.IsAssignableFrom<IDictionary>(field.GetValue(client));
        return [.. sessions.Values.Cast<object>()];
    }

    private sealed class CleanupClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public IReadOnlyList<ManualTimer> Timers
        {
            get
            {
                lock (_timers)
                {
                    return [.. _timers];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timer.Change(dueTime, period);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }
    }

    private sealed class FailingLogger(bool throwsWhenChecking) : ILogger
    {
        public int Failures { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (throwsWhenChecking)
            {
                Failures++;
                throw new IOException(ProviderSecret);
            }

            return true;
        }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Failures++;
                throw new IOException(ProviderSecret);
            }
        }
    }
}
