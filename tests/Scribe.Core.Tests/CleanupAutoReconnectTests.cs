using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

public sealed class CleanupAutoReconnectTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Canary = "reconnect-private-canary";

    [Theory]
    [InlineData(CleanupProvider.AzureFoundry)]
    [InlineData(CleanupProvider.OpenAiCompatible)]
    public async Task Only_the_next_recording_after_the_backoff_reconnects(CleanupProvider provider)
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? ConnectionFailure() : null);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options(provider));
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Equal(TimeSpan.FromSeconds(30), TextCleanupService.AutomaticReconnectBackoff);
        Assert.Empty(clock.Timers);

        clock.Advance(TimeSpan.FromSeconds(29));
        svc.Admit(CleanupVocabulary.None).Prewarm();
        Assert.Equal(1, fake.Connections);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fake.Connections);
        Assert.Empty(clock.Timers);
        var statuses = new CleanupStatusRecorder(svc);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Equal(2, fake.Connections);
        Assert.Contains(statuses.Snapshot(), status =>
            status.Status == CleanupStatus.Initializing && status.Detail == "Reconnecting AI cleanup...");
    }

    [Fact]
    public async Task Passing_the_backoff_without_a_recording_never_starts_work()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);

        clock.Advance(TimeSpan.FromDays(10));
        var result = await svc.CleanAsync("hello there").WaitAsync(Bound);

        Assert.Equal(1, fake.Connections);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
        Assert.Equal(CleanupOutcome.Failed, result.Outcome);
        Assert.Equal("hello there", result.Text);
        Assert.Empty(clock.Timers);
    }

    [Fact]
    public async Task A_second_automatic_failure_exhausts_the_budget_across_generations()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Equal(2, fake.Connections);

        clock.Advance(TimeSpan.FromDays(10));
        for (var recording = 0; recording < 10; recording++)
        {
            svc.Admit(CleanupVocabulary.None).Prewarm();
        }

        Assert.Equal(2, fake.Connections);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_save_resets_the_budget_even_for_the_same_configuration(bool newConfiguration)
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var svc = SetUp(harness, clock, fake);
        var options = Options();
        svc.Configure(options);
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Unavailable);

        svc.Configure(newConfiguration ? options with { CustomModel = "another-model" } : options);
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Equal(3, fake.Connections);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        Assert.Equal(3, fake.Connections);

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Equal(4, fake.Connections);
    }

    [Fact]
    public async Task An_explicit_same_save_during_the_retry_resets_its_budget_without_restarting_live_work()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var retry = fake.Hold(2);
        var svc = SetUp(harness, clock, fake);
        var options = Options();
        svc.Configure(options);
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        try
        {
            await retry.Started.Task.WaitAsync(Bound);
            svc.Configure(options);
            Assert.Equal(2, fake.Connections);
        }
        finally
        {
            retry.Release.TrySetResult();
        }

        await FinishedAsync(svc, CleanupStatus.Unavailable);
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Equal(3, fake.Connections);
    }

    [Fact]
    public async Task Concurrent_recording_starts_reserve_only_one_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? ConnectionFailure() : null);
        var retry = fake.Hold(2);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        var statuses = new CleanupStatusRecorder(svc);

        try
        {
            Parallel.For(0, 32, _ => svc.Admit(CleanupVocabulary.None).Prewarm());
            await retry.Started.Task.WaitAsync(Bound);
            Assert.Equal(2, fake.Connections);
            Assert.Single(statuses.Snapshot(), status => status.Detail == "Reconnecting AI cleanup...");
        }
        finally
        {
            retry.Release.TrySetResult();
        }

        await FinishedAsync(svc, CleanupStatus.Ready);
        Assert.Equal(2, fake.Connections);
    }

    [Fact]
    public async Task A_save_during_the_retry_owns_the_status_even_when_the_cancelled_retry_fails_late()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call <= 2 ? ConnectionFailure() : null);
        var retry = fake.Hold(2);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        var successor = Options() with { CustomModel = "new-model" };

        try
        {
            await retry.Started.Task.WaitAsync(Bound);
            svc.Configure(successor);
            Assert.Equal(CleanupStatus.Initializing, svc.Status);
        }
        finally
        {
            retry.Release.TrySetResult();
        }

        await FinishedAsync(svc, CleanupStatus.Ready);
        clock.Advance(TimeSpan.FromDays(1));
        svc.Admit(CleanupVocabulary.None).Prewarm();

        Assert.Equal(3, fake.Connections);
        Assert.Equal(successor.CustomModel, fake.LastOptions?.CustomModel);
        Assert.Equal(CleanupStatus.Ready, svc.Status);
    }

    [Fact]
    public async Task A_save_from_the_reconnect_notification_supersedes_the_reservation_before_it_starts()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? ConnectionFailure() : null);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        var successor = Options() with { CustomModel = "new-model" };
        svc.StatusChanged += () =>
        {
            if (svc.StatusDetail == "Reconnecting AI cleanup...")
            {
                svc.Configure(successor);
            }
        };

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Equal(2, fake.Connections);
        Assert.Equal(successor.CustomModel, fake.LastOptions?.CustomModel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(599)]
    public async Task Only_connection_throttle_and_generic_server_failures_can_reconnect(int status)
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? HttpFailure(status) : null);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Equal(2, fake.Connections);
    }

    [Theory]
    [InlineData(CleanupProvider.AzureFoundry)]
    [InlineData(CleanupProvider.OpenAiCompatible)]
    public async Task A_transient_readiness_probe_failure_is_remembered_for_the_next_recording(CleanupProvider provider)
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => null);
        fake.Client.Failure = HttpFailure(503);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options(provider));
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        fake.Client.Failure = null;

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(CleanupVocabulary.None).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Equal(2, fake.Connections);
        Assert.Equal(2, fake.Client.Instructions.Count);
    }

    [Fact]
    public async Task A_readiness_probe_timeout_does_not_arm_a_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => null);
        fake.Client.Failure = new OperationCanceledException(Canary);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Contains("validation timed out", svc.StatusDetail, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromDays(1));
        svc.Admit(CleanupVocabulary.None).Prewarm();

        Assert.Equal(1, fake.Connections);
        Assert.Single(fake.Client.Instructions);
    }

    [Fact]
    public async Task Instructions_that_do_not_fit_the_context_do_not_arm_a_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => null);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(CleanupHarness.Custom(LocalAiServer.OllamaAddress) with
        {
            LocalContextTokens = ContextBudget.MinimumSize,
            WritingStyle = string.Join(" ", Enumerable.Repeat("A writing instruction.", 1000)),
        });
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        Assert.Contains("don't fit", svc.StatusDetail, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromDays(1));
        svc.Admit(CleanupVocabulary.None).Prewarm();

        Assert.Equal(1, fake.Connections);
        Assert.Empty(fake.Client.Instructions);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(408)]
    [InlineData(422)]
    [InlineData(600)]
    public async Task Permanent_http_failures_do_not_reconnect(int status)
    {
        await AssertDoesNotReconnectAsync(HttpFailure(status));
    }

    [Theory]
    [InlineData(PermanentFailure.Cancellation)]
    [InlineData(PermanentFailure.Timeout)]
    [InlineData(PermanentFailure.SignIn)]
    [InlineData(PermanentFailure.NoCredential)]
    [InlineData(PermanentFailure.InvalidConfiguration)]
    [InlineData(PermanentFailure.MissingModel)]
    [InlineData(PermanentFailure.ExecutionProvider)]
    [InlineData(PermanentFailure.Shader)]
    [InlineData(PermanentFailure.ModelBuild)]
    [InlineData(PermanentFailure.Load)]
    [InlineData(PermanentFailure.ContextInitialization)]
    [InlineData(PermanentFailure.RunnerExit)]
    [InlineData(PermanentFailure.Unclassified)]
    public async Task Cancellation_validation_and_known_load_failures_do_not_reconnect(PermanentFailure kind)
    {
        Exception failure = kind switch
        {
            PermanentFailure.Cancellation => new HttpRequestException(Canary, new OperationCanceledException(Canary),
                HttpStatusCode.ServiceUnavailable),
            PermanentFailure.Timeout => new HttpRequestException(Canary, new TimeoutException(Canary),
                HttpStatusCode.ServiceUnavailable),
            PermanentFailure.SignIn => new AuthenticationFailedException(Canary, ConnectionFailure()),
            PermanentFailure.NoCredential => new CredentialUnavailableException(Canary),
            PermanentFailure.InvalidConfiguration => new HttpRequestException(Canary, new ArgumentException(Canary),
                HttpStatusCode.ServiceUnavailable),
            PermanentFailure.MissingModel => HttpFailure(500, "Model is not loaded."),
            PermanentFailure.ExecutionProvider => HttpFailure(500,
                "Cannot load model with CUDA execution provider, which is not available."),
            PermanentFailure.Shader => HttpFailure(500, "Failed to create a WebGPU compute pipeline."),
            PermanentFailure.ModelBuild => HttpFailure(500, "failed to create generator"),
            PermanentFailure.Load => HttpFailure(500, "failed to load model"),
            PermanentFailure.ContextInitialization => HttpFailure(500, "failed to initialize the context"),
            PermanentFailure.RunnerExit => HttpFailure(500, "runner process exited"),
            _ => new InvalidOperationException(Canary),
        };
        await AssertDoesNotReconnectAsync(failure, CleanupHarness.Custom(LocalAiServer.OllamaAddress));
    }

    [Theory]
    [InlineData(CleanupProvider.FoundryLocal)]
    [InlineData(CleanupProvider.GitHubCopilot)]
    public async Task Foundry_local_and_copilot_never_automatically_reconnect(CleanupProvider provider)
    {
        await AssertDoesNotReconnectAsync(ConnectionFailure(), Options(provider));
    }

    [Fact]
    public async Task Switching_off_refuses_a_previously_admitted_recordings_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        var admitted = svc.Admit(CleanupVocabulary.None);

        svc.Configure(Options() with { Enabled = false });
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        admitted.Prewarm();

        Assert.Equal(1, fake.Connections);
        Assert.Equal(CleanupStatus.Disabled, svc.Status);
    }

    [Fact]
    public async Task A_configuration_without_a_model_refuses_a_previously_admitted_recordings_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        var admitted = svc.Admit(CleanupVocabulary.None);

        svc.Configure(Options() with { CustomModel = null });
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        admitted.Prewarm();

        Assert.Equal(1, fake.Connections);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
    }

    [Fact]
    public async Task Disposal_refuses_a_previously_admitted_recordings_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => ConnectionFailure());
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        var admitted = svc.Admit(CleanupVocabulary.None);

        await svc.DisposeAsync();
        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        var failure = Record.Exception(() => admitted.Prewarm());

        Assert.Null(failure);
        Assert.Equal(1, fake.Connections);
    }

    [Fact]
    public async Task The_reconnected_readiness_probe_still_carries_no_vocabulary()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? ConnectionFailure() : null);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options() with { Glossary = Canary });
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        var vocabulary = new CleanupVocabulary(
            [DictionaryEntry.New("canary", Canary)], AiVocabularyScope.None);

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        svc.Admit(vocabulary).Prewarm();
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Single(fake.Client.Instructions);
        Assert.DoesNotContain(Canary, fake.Client.Instructions.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Message.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_status_subscriber_cannot_stop_the_reserved_reconnect()
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? ConnectionFailure() : null);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        svc.StatusChanged += () => throw new InvalidOperationException(Canary);

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        var failure = Record.Exception(() => svc.Admit(CleanupVocabulary.None).Prewarm());
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Null(failure);
        Assert.Equal(2, fake.Connections);
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Message.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_logger_cannot_stop_a_recording_or_the_reserved_reconnect()
    {
        await using var harness = new CleanupHarness();
        var log = new SwitchableLogger();
        await using var svc = new TextCleanupService(log, harness.Paths, harness.Host, foundryStorage: null,
            localServers: harness.LocalServers);
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(call => call == 1 ? ConnectionFailure() : null);
        svc.AutomaticReconnectClock = clock;
        svc.ProviderFactoryForTesting = fake.Connect;
        svc.Configure(Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);
        log.ThrowOnReconnect = true;

        clock.Advance(TextCleanupService.AutomaticReconnectBackoff);
        var failure = Record.Exception(() => svc.Admit(CleanupVocabulary.None).Prewarm());
        await FinishedAsync(svc, CleanupStatus.Ready);

        Assert.Null(failure);
        Assert.Equal(2, fake.Connections);
        Assert.Equal(CleanupStatus.Ready, svc.Status);
        Assert.Equal(1, log.ReconnectFailures);
    }

    public enum PermanentFailure
    {
        Cancellation,
        Timeout,
        SignIn,
        NoCredential,
        InvalidConfiguration,
        MissingModel,
        ExecutionProvider,
        Shader,
        ModelBuild,
        Load,
        ContextInitialization,
        RunnerExit,
        Unclassified,
    }

    private static TextCleanupService SetUp(CleanupHarness harness, ManualTimeProvider clock, ScriptedProvider fake)
    {
        harness.Service.AutomaticReconnectClock = clock;
        harness.Service.ProviderFactoryForTesting = fake.Connect;
        return harness.Service;
    }

    private static CleanupOptions Options(CleanupProvider provider = CleanupProvider.OpenAiCompatible) =>
        CleanupHarness.Custom("https://cleanup.example.invalid/v1") with
        {
            Provider = provider,
            AzureEndpoint = "https://cleanup.example.invalid/",
            AzureDeployment = "model",
        };

    private static Exception ConnectionFailure() =>
        new HttpRequestException(Canary, new SocketException((int)SocketError.ConnectionRefused));

    private static Exception HttpFailure(int status, string message = Canary) =>
        status == 0 ? ConnectionFailure() : new HttpRequestException(message, null, (HttpStatusCode)status);

    private static async Task AssertDoesNotReconnectAsync(Exception failure, CleanupOptions? options = null)
    {
        await using var harness = new CleanupHarness();
        var clock = new ManualTimeProvider();
        var fake = new ScriptedProvider(_ => failure);
        var svc = SetUp(harness, clock, fake);
        svc.Configure(options ?? Options());
        await FinishedAsync(svc, CleanupStatus.Unavailable);

        clock.Advance(TimeSpan.FromDays(10));
        svc.Admit(CleanupVocabulary.None).Prewarm();

        Assert.Equal(1, fake.Connections);
        Assert.Equal(CleanupStatus.Unavailable, svc.Status);
    }

    private static async Task FinishedAsync(TextCleanupService svc, CleanupStatus status)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (svc.Status == status)
            {
                reached.TrySetResult();
            }
        }

        svc.StatusChanged += Check;
        try
        {
            Check();
            await reached.Task.WaitAsync(Bound);
        }
        finally
        {
            svc.StatusChanged -= Check;
        }

        // Terminal status is published before the initializer's finally makes its phase Idle. Taking its existing
        // semaphore waits for that finalization without a sleep or an additional production scheduling seam.
        var field = typeof(TextCleanupService).GetField("_initLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var initLock = Assert.IsType<SemaphoreSlim>(field.GetValue(svc));
        Assert.True(await initLock.WaitAsync(Bound), "The owned initialization must finish.");
        initLock.Release();
    }

    private sealed class ScriptedProvider(Func<int, Exception?> failure)
    {
        private readonly ConcurrentDictionary<int, HeldConnection> _held = new();
        private int _connections;
        private CleanupOptions? _lastOptions;

        public int Connections => Volatile.Read(ref _connections);
        public CleanupOptions? LastOptions => Volatile.Read(ref _lastOptions);
        public RecordingClient Client { get; } = new();

        public HeldConnection Hold(int connection) => _held[connection] = new HeldConnection();

        public async Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _connections);
            Volatile.Write(ref _lastOptions, options);
            if (_held.TryGetValue(call, out var held))
            {
                held.Started.TrySetResult();
                await held.Release.Task;
            }

            if (failure(call) is { } error)
            {
                throw error;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return instructions => new ChatClientAgent(Client, instructions: instructions, name: "ScribeCleanup");
        }
    }

    private sealed class HeldConnection
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingClient : IChatClient
    {
        public ConcurrentQueue<string> Instructions { get; } = new();
        public Exception? Failure { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Instructions.Enqueue(string.Join("\n", messages.Where(message => message.Role == ChatRole.System)
                .Select(message => message.Text).Prepend(options?.Instructions ?? string.Empty)));
            if (Failure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SwitchableLogger : ILogger<TextCleanupService>
    {
        public bool ThrowOnReconnect { get; set; }
        public int ReconnectFailures { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnReconnect && formatter(state, exception).Contains("automatic reconnect reserved", StringComparison.Ordinal))
            {
                ReconnectFailures++;
                throw new InvalidOperationException(Canary);
            }
        }
    }
}
