using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Tests.CleanupLogging;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests.Vocabulary;

public sealed class GitHubCopilotClientLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Dictated = "please ask about the harbour lanterns today";
    private const string Instructions = "Clean up the dictation with kestrelmoor vocabulary.";
    private const string ProviderSecret = "private quota for harbourlantern account";

    [Fact]
    public async Task Agents_keep_using_the_shared_client_until_its_idempotent_owner_releases_it()
    {
        await using var runtime = new FakeCopilotRuntime { Answer = _ => Dictated };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var calls = new ConcurrentQueue<string>();
        var clock = new ShutdownClock();
        var owner = OwnerFor(client, calls, clock);
        var agent = GitHubCopilotAgentFactory.Create(client, owner, Instructions, null, "ScribeCleanup");

        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);
        Assert.Equal(Dictated, (await RunAsync(agent).WaitAsync(Bound)).Text);
        Assert.False(IsDisposed(client));
        Assert.Single(runtime.Requests, request => request.Method == "connect");

        var first = owner.DisposeAsync().AsTask();
        var second = owner.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await first.WaitAsync(Bound);
        await owner.DisposeAsync();

        Assert.Equal(["stop", "dispose"], calls);
        Assert.True(IsDisposed(client));
        Assert.Null(ConnectionTask(client));
        Assert.Empty(RegisteredSessions(client));
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }

    [Fact]
    public async Task A_stuck_sdk_detach_gets_the_supported_force_stop_after_the_grace()
    {
        await using var runtime = new FakeCopilotRuntime
        {
            BeforeAnswer = (request, token) =>
                request.Method == "session.detach" ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        await client.CreateSessionAsync(GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null));
        var calls = new ConcurrentQueue<string>();
        var clock = new ShutdownClock();
        var log = new CapturingLogger<GitHubCopilotClientLifetime>();
        var owner = OwnerFor(client, calls, clock, log);
        var detach = runtime.Next(request => request.Method == "session.detach");

        var shuttingDown = owner.DisposeAsync().AsTask();
        await detach.WaitAsync(Bound);
        Assert.NotNull(ConnectionTask(client));
        Assert.False(shuttingDown.IsCompleted);
        clock.FireCurrentStep();
        await shuttingDown.WaitAsync(Bound);

        Assert.Equal(["stop", "force", "dispose"], calls);
        Assert.True(IsDisposed(client));
        Assert.Null(ConnectionTask(client));
        Assert.Empty(RegisteredSessions(client));
        Assert.Single(runtime.Requests, request => request.Method == "session.detach");
        Assert.Contains(log.Entries, entry => entry.Message.StartsWith("Copilot client shutdown stop did not finish", StringComparison.Ordinal));
        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
        Assert.Equal(3, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
    }

    [Fact]
    public async Task A_failed_sdk_shutdown_still_tries_force_and_disposal_and_logs_only_its_shape()
    {
        await using var runtime = new FakeCopilotRuntime
        {
            RequestError = request => request.Method == "session.detach" ? ProviderSecret : null,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var session = await client.CreateSessionAsync(GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null));
        var calls = new ConcurrentQueue<string>();
        var log = new CapturingLogger<GitHubCopilotClientLifetime>();
        var owner = OwnerFor(client, calls, new ShutdownClock(), log, failStop: true);

        await owner.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(["stop", "force", "dispose"], calls);
        Assert.True(IsDisposed(client));
        Assert.Null(ConnectionTask(client));
        Assert.Empty(RegisteredSessions(client));
        Assert.Single(log.Entries);
        Assert.DoesNotContain(ProviderSecret, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Instructions, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(session.SessionId, log.AllText, StringComparison.Ordinal);
        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task A_delivered_answer_is_unchanged_when_deletion_fails_and_its_owner_closes()
    {
        await using var runtime = new FakeCopilotRuntime { Answer = _ => Dictated, DeletionError = ProviderSecret };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var log = new CapturingLogger<GitHubCopilotClientLifetime>();
        var owner = new GitHubCopilotClientLifetime(client, log, new ShutdownClock());
        var agent = GitHubCopilotAgentFactory.Create(client, owner, Instructions, null, "ScribeCleanup", log);

        var result = await RunAsync(agent).WaitAsync(Bound);
        await owner.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(Dictated, result.Text);
        Assert.Equal(12, result.Usage?.TotalTokenCount);
        Assert.True(IsDisposed(client));
        Assert.Empty(RegisteredSessions(client));
        Assert.Single(runtime.Requests, request => request.Method == "session.delete");
        Assert.DoesNotContain(ProviderSecret, log.AllText, StringComparison.Ordinal);
        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task A_cancelled_dictation_returns_before_owner_shutdown_and_keeps_its_cancellation_after_failed_cleanup()
    {
        await using var runtime = new FakeCopilotRuntime
        {
            BeforeAnswer = (request, token) =>
                request.Method == "session.send" ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask,
            DeletionError = ProviderSecret,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var owner = new GitHubCopilotClientLifetime(client, timeProvider: new ShutdownClock());
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var agentClock = new ShutdownClock();
        var agent = new GitHubCopilotCleanupAgent(
            client, owner, GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null), "ScribeCleanup", log, agentClock);
        using var cancellation = new CancellationTokenSource();
        var sent = runtime.Next(request => request.Method == "session.send");

        var running = RunAsync(agent, cancellation.Token);
        await sent.WaitAsync(Bound);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bound));
        Assert.False(IsDisposed(client));

        // The failed deletion's actual SDK work has drained, so the owner can now disconnect safely.
        await owner.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.True(running.IsCanceled);
        Assert.True(IsDisposed(client));
        Assert.Null(ConnectionTask(client));
        Assert.Empty(RegisteredSessions(client));
        Assert.Single(runtime.Requests, request => request.Method == "session.delete");
        Assert.DoesNotContain(ProviderSecret, log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_shutdown_phase_is_bounded_and_pending_sdk_work_can_finish_after_the_owner_returns()
    {
        await using var runtime = new FakeCopilotRuntime();
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var stopRelease = NewSignal();
        var forceRelease = NewSignal();
        var disposeRelease = NewSignal();
        var stopStarted = NewSignal();
        var forceStarted = NewSignal();
        var disposeStarted = NewSignal();
        Task? stopping = null;
        Task? forcing = null;
        Task? disposing = null;
        var clock = new ShutdownClock();
        var log = new CapturingLogger<GitHubCopilotClientLifetime>();
        async Task StopAsync()
        {
            stopStarted.TrySetResult();
            await stopRelease.Task;
            await client.StopAsync();
        }
        async Task ForceAsync()
        {
            forceStarted.TrySetResult();
            await forceRelease.Task;
            await client.ForceStopAsync();
        }
        async Task DisposeAsync()
        {
            disposeStarted.TrySetResult();
            await disposeRelease.Task;
            await client.DisposeAsync();
        }
        var owner = new GitHubCopilotClientLifetime(
            () => stopping = StopAsync(),
            () => forcing = ForceAsync(),
            () => new ValueTask(disposing = DisposeAsync()),
            log, clock);

        var shutdown = owner.DisposeAsync().AsTask();
        await stopStarted.Task.WaitAsync(Bound);
        clock.FireCurrentStep();
        await forceStarted.Task.WaitAsync(Bound);
        clock.FireCurrentStep();
        await disposeStarted.Task.WaitAsync(Bound);
        clock.FireCurrentStep();
        await shutdown.WaitAsync(Bound);

        Assert.Equal(TimeSpan.FromSeconds(8), GitHubCopilotClientLifetime.ShutdownWaitBound);
        Assert.Equal(GitHubCopilotClientLifetime.StepTimeout * 3, clock.Elapsed);
        Assert.Equal(3, log.Entries.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));

        // Settle the deliberately delayed SDK calls in ownership order, without leaving work behind this test.
        forceRelease.TrySetResult();
        await forcing!.WaitAsync(Bound);
        stopRelease.TrySetResult();
        await stopping!.WaitAsync(Bound);
        disposeRelease.TrySetResult();
        await disposing!.WaitAsync(Bound);
        Assert.True(IsDisposed(client));
        Assert.Null(ConnectionTask(client));
    }

    [Fact]
    public async Task A_fault_from_a_timed_out_shutdown_task_is_observed_later_without_exposing_its_text()
    {
        await using var runtime = new FakeCopilotRuntime();
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var started = NewSignal();
        var lateFailure = NewSignal();
        var clock = new ShutdownClock();
        var log = new ShutdownLogger();
        var owner = new GitHubCopilotClientLifetime(
            () =>
            {
                started.TrySetResult();
                return lateFailure.Task;
            },
            client.ForceStopAsync,
            client.DisposeAsync,
            log, clock);

        var shutdown = owner.DisposeAsync().AsTask();
        await started.Task.WaitAsync(Bound);
        clock.FireCurrentStep();
        await shutdown.WaitAsync(Bound);
        lateFailure.TrySetException(new IOException(ProviderSecret));
        await log.LateFailureLogged.Task.WaitAsync(Bound);

        Assert.True(IsDisposed(client));
        Assert.DoesNotContain(ProviderSecret, log.Capture.AllText, StringComparison.Ordinal);
        Assert.All(log.Capture.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Throwing_shutdown_diagnostics_cannot_skip_force_or_final_disposal(bool throwsWhenChecking)
    {
        await using var runtime = new FakeCopilotRuntime
        {
            RequestError = request => request.Method == "session.detach" ? ProviderSecret : null,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        await client.CreateSessionAsync(GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null));
        var log = new ShutdownLogger(throwsWhenChecking, throwsWhenWriting: true);
        var calls = new ConcurrentQueue<string>();
        var owner = OwnerFor(client, calls, new ShutdownClock(), log, failStop: true);

        await owner.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(["stop", "force", "dispose"], calls);
        Assert.Equal(1, log.Failures);
        Assert.True(IsDisposed(client));
        Assert.Empty(RegisteredSessions(client));
    }

    [Fact]
    public async Task Shutdown_before_a_held_answer_cannot_reconnect_when_the_cancelled_run_finally_cleans_up()
    {
        var awaitingAnswer = NewSignal();
        await using var runtime = new FakeCopilotRuntime
        {
            BeforeEvents = (_, token) =>
            {
                awaitingAnswer.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var owner = new GitHubCopilotClientLifetime(client, timeProvider: new ShutdownClock());
        var log = new CapturingLogger<GitHubCopilotCleanupAgent>();
        var agent = GitHubCopilotAgentFactory.Create(client, owner, Instructions, null, "ScribeCleanup", log);
        using var cancellation = new CancellationTokenSource();

        var running = RunAsync(agent, cancellation.Token);
        await awaitingAnswer.Task.WaitAsync(Bound);
        var closing = owner.DisposeAsync().AsTask();
        Assert.True(owner.IsClosing);
        await closing.WaitAsync(Bound);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bound));

        Assert.True(IsDisposed(client));
        Assert.Null(ConnectionTask(client));
        Assert.Single(runtime.Requests, request => request.Method == "connect");
        Assert.DoesNotContain(runtime.Requests, request => request.Method == "session.delete");
        Assert.All(log.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(Dictated, log.AllText, StringComparison.Ordinal);

        // A stale agent's next run is refused before even StartAsync, not merely before its send.
        var before = runtime.Requests.Count;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => RunAsync(agent).WaitAsync(Bound));
        Assert.Equal(before, runtime.Requests.Count);
    }

    [Fact]
    public async Task An_admitted_deletion_drains_before_stop_and_its_failure_never_changes_the_answer()
    {
        var held = NewSignal();
        await using var runtime = new FakeCopilotRuntime
        {
            Answer = _ => Dictated,
            DeletionError = ProviderSecret,
            BeforeAnswer = (request, token) =>
                request.Method == "session.delete" ? held.Task.WaitAsync(token) : Task.CompletedTask,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var calls = new ConcurrentQueue<string>();
        var owner = OwnerFor(client, calls, new ShutdownClock());
        var agent = new GitHubCopilotCleanupAgent(
            client, owner, GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null),
            "ScribeCleanup", timeProvider: new ShutdownClock());
        var deleting = runtime.Next(request => request.Method == "session.delete");

        var running = RunAsync(agent);
        await deleting.WaitAsync(Bound);
        Assert.Equal(1, owner.ActiveOperations);
        var closing = owner.DisposeAsync().AsTask();
        Assert.True(owner.IsClosing);
        Assert.Empty(calls);
        held.TrySetResult();

        var answer = await running.WaitAsync(Bound);
        await closing.WaitAsync(Bound);
        Assert.Equal(Dictated, answer.Text);
        Assert.Equal(["stop", "dispose"], calls);
        Assert.True(IsDisposed(client));
        Assert.Single(runtime.Requests, request => request.Method == "connect");
    }

    [Fact]
    public async Task A_synchronous_cleanup_prefix_is_off_the_caller_and_its_lease_outlives_the_wait_bound()
    {
        await using var runtime = new FakeCopilotRuntime();
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var calls = new ConcurrentQueue<string>();
        var ownerClock = new ShutdownClock();
        var owner = OwnerFor(client, calls, ownerClock);
        var agentClock = new ShutdownClock();
        var agent = new GitHubCopilotCleanupAgent(
            client, owner, GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null),
            "ScribeCleanup", timeProvider: agentClock);
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        try
        {
            var step = agent.CleanupStepAsync(_ =>
            {
                entered.TrySetResult();
                if (!release.Wait(Bound))
                {
                    throw new TimeoutException("The test did not release its synchronous prefix.");
                }

                return Task.CompletedTask;
            }, "delete");
            await entered.Task.WaitAsync(Bound);
            Assert.Equal(1, owner.ActiveOperations);
            agentClock.FireCurrentStep();
            await step.WaitAsync(Bound);
            Assert.Equal(1, owner.ActiveOperations);

            var closing = owner.DisposeAsync().AsTask();
            Assert.True(owner.IsClosing);
            await ownerClock.TimerCreated.Task.WaitAsync(Bound);
            ownerClock.FireCurrentStep();
            await closing.WaitAsync(Bound);

            // Stopping here would let the prefix restart the client after it resumes. Keep the old resources instead.
            Assert.Empty(calls);
            Assert.False(IsDisposed(client));
            Assert.NotNull(ConnectionTask(client));
        }
        finally
        {
            release.Set();
            var tracker = Assert.IsType<CleanupOperationTracker>(
                typeof(GitHubCopilotClientLifetime).GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
            await tracker.Close().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Cleanup_after_close_never_invokes_even_a_synchronous_reconnecting_prefix()
    {
        await using var runtime = new FakeCopilotRuntime();
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var owner = new GitHubCopilotClientLifetime(client, timeProvider: new ShutdownClock());
        var agent = new GitHubCopilotCleanupAgent(
            client, owner, GitHubCopilotAgentFactory.BuildSessionConfig(Instructions, null), "ScribeCleanup");
        await owner.DisposeAsync().AsTask().WaitAsync(Bound);
        var invoked = 0;

        await agent.CleanupStepAsync(token =>
        {
            Interlocked.Increment(ref invoked);
            return client.DeleteSessionAsync("synthetic-session", token);
        }, "delete").WaitAsync(Bound);

        Assert.Equal(0, invoked);
        Assert.Single(runtime.Requests, request => request.Method == "connect");
        Assert.Null(ConnectionTask(client));
    }

    [Fact]
    public async Task An_old_agent_run_queued_before_shutdown_never_starts_creates_or_sends_after_shutdown()
    {
        await using var runtime = new FakeCopilotRuntime();
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var owner = new GitHubCopilotClientLifetime(client, timeProvider: new ShutdownClock());
        var agent = GitHubCopilotAgentFactory.Create(client, owner, Instructions, null, "ScribeCleanup");
        var queued = NewSignal();
        var release = NewSignal();
        var running = Task.Run(async () =>
        {
            queued.TrySetResult();
            await release.Task;
            return await RunAsync(agent);
        });

        await queued.Task.WaitAsync(Bound);
        await owner.DisposeAsync().AsTask().WaitAsync(Bound);
        release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => running.WaitAsync(Bound));

        Assert.Single(runtime.Requests, request => request.Method == "connect");
        Assert.Empty(runtime.Creates);
        Assert.Empty(runtime.Sends);
        Assert.DoesNotContain(runtime.Requests, request => request.Method == "session.delete");
        Assert.Null(ConnectionTask(client));
        Assert.Equal(0, owner.ActiveOperations);
    }

    [Fact]
    public async Task An_owner_closed_during_creation_refuses_the_send_even_before_its_sdk_stop_begins()
    {
        var creationRelease = NewSignal();
        var stopEntered = NewSignal();
        var stopRelease = NewSignal();
        await using var runtime = new FakeCopilotRuntime
        {
            BeforeAnswer = (request, token) =>
                request.Method == "session.create" ? creationRelease.Task.WaitAsync(token) : Task.CompletedTask,
        };
        await using var client = ClientFor(runtime);
        await client.StartAsync();
        var owner = new GitHubCopilotClientLifetime(
            async () =>
            {
                stopEntered.TrySetResult();
                await stopRelease.Task;
                await client.StopAsync();
            },
            client.ForceStopAsync, client.DisposeAsync, timeProvider: new ShutdownClock());
        var agent = GitHubCopilotAgentFactory.Create(client, owner, Instructions, null, "ScribeCleanup");
        var creation = runtime.Next(request => request.Method == "session.create");

        var running = RunAsync(agent);
        await creation.WaitAsync(Bound);
        var closing = owner.DisposeAsync().AsTask();
        Assert.True(owner.IsClosing);
        creationRelease.TrySetResult();
        await stopEntered.Task.WaitAsync(Bound);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => running.WaitAsync(Bound));

        Assert.Empty(runtime.Sends);
        Assert.DoesNotContain(runtime.Requests, request => request.Method == "session.delete");
        Assert.Single(runtime.Requests, request => request.Method == "connect");
        stopRelease.TrySetResult();
        await closing.WaitAsync(Bound);
        Assert.Null(ConnectionTask(client));
    }

    [Fact]
    public void The_service_handle_and_method_signatures_stay_free_of_copilot_sdk_types()
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Assert.All(typeof(TextCleanupService).GetFields(Flags), field => Assert.False(NamesCopilot(field.FieldType)));
        foreach (var method in typeof(TextCleanupService).GetMethods(Flags))
        {
            Assert.False(NamesCopilot(method.ReturnType));
            Assert.All(method.GetParameters(), parameter => Assert.False(NamesCopilot(parameter.ParameterType)));
        }
    }

    private static bool NamesCopilot(Type type) =>
        type.Assembly.GetName().Name == "GitHub.Copilot.SDK" ||
        (type.IsGenericType && type.GetGenericArguments().Any(NamesCopilot)) ||
        (type.HasElementType && NamesCopilot(type.GetElementType()!));

    private static CopilotClient ClientFor(FakeCopilotRuntime runtime) =>
        new(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(runtime.Url) });

    private static GitHubCopilotClientLifetime OwnerFor(
        CopilotClient client, ConcurrentQueue<string> calls, TimeProvider clock, ILogger? log = null, bool failStop = false) =>
        new(
            () =>
            {
                calls.Enqueue("stop");
                return failStop ? Task.FromException(new IOException(ProviderSecret)) : client.StopAsync();
            },
            () => { calls.Enqueue("force"); return client.ForceStopAsync(); },
            () => { calls.Enqueue("dispose"); return client.DisposeAsync(); },
            log, clock);

    private static async Task<AgentResponse> RunAsync(AIAgent agent, CancellationToken cancellationToken = default)
    {
        using (new CleanupAdmission(CleanupRequestKind.Dictation, AiVocabularyScope.None, null).Enter())
        {
            return await agent.RunAsync(Dictated, cancellationToken: cancellationToken);
        }
    }

    private static object? FieldOf(CopilotClient client, string name) =>
        typeof(CopilotClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client);

    private static bool IsDisposed(CopilotClient client) => Assert.IsType<bool>(FieldOf(client, "_disposed"));

    private static object? ConnectionTask(CopilotClient client) => FieldOf(client, "_connectionTask");

    private static IReadOnlyList<object> RegisteredSessions(CopilotClient client) =>
        [.. Assert.IsAssignableFrom<IDictionary>(FieldOf(client, "_sessions")).Values.Cast<object>()];

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ShutdownClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public TimeSpan Elapsed { get; private set; }
        public TaskCompletionSource TimerCreated { get; } = NewSignal();

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

        public void FireCurrentStep()
        {
            var timer = Timers[^1];
            Assert.False(timer.Disposed);
            Assert.Equal(GitHubCopilotClientLifetime.StepTimeout, timer.DueTime);
            Elapsed += timer.DueTime!.Value;
            timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timer.Change(dueTime, period);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            TimerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class ShutdownLogger(bool throwsWhenChecking = false, bool throwsWhenWriting = false) : ILogger
    {
        public CapturingLogger<GitHubCopilotClientLifetime> Capture { get; } = new();
        public TaskCompletionSource LateFailureLogged { get; } = NewSignal();
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
            if (!IsEnabled(logLevel))
            {
                return;
            }

            if (throwsWhenWriting)
            {
                Failures++;
                throw new IOException(ProviderSecret);
            }

            Capture.Log(logLevel, eventId, state, exception, formatter);
            if (formatter(state, exception).Contains("completed late", StringComparison.Ordinal))
            {
                LateFailureLogged.TrySetResult();
            }
        }
    }
}
