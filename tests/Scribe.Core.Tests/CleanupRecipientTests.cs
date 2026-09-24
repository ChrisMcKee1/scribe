using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// AI dictionary suggestions send up to 6,000 characters of history, and the user is asked first. The
/// question is about one recipient, captured from the service before it is asked, and CompleteAsync sends
/// only while the service still serves exactly that one. Before this, the window decided whether to ask
/// from settings a failed Save could leave pointing at another provider, and nothing stopped a Save during
/// the history read from sending the sample somewhere the user never agreed to. Each case drives the real
/// service through fake provider factories; nothing leaves the process.
/// </summary>
public sealed class CleanupRecipientTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Sample = "history sample canary quillmoor";

    [Fact]
    public async Task A_provider_saved_during_the_history_read_receives_nothing()
    {
        await using var harness = new CleanupHarness();
        var providers = new Providers();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = providers.Connect;
        svc.Configure(Azure("deployment-a"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        // The window captures the recipient, asks, and then reads history off the UI thread.
        var consented = svc.Recipient!;
        Assert.Equal(CleanupProvider.AzureFoundry, consented.Provider);

        // Meanwhile a Save switches cleanup to GitHub Copilot, which comes up ready.
        svc.Configure(Copilot());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var completion = await svc.CompleteAsync("system", Sample, consented).WaitAsync(Bound);

        Assert.Equal(CompletionOutcome.RecipientChanged, completion.Outcome);
        Assert.Null(completion.Text);
        Assert.True(completion.NothingSent);
        Assert.DoesNotContain(providers.AllMessages, message => message.Contains(Sample, StringComparison.Ordinal));

        // Asked again about the provider now in use, the request goes there.
        var now = svc.Recipient!;
        Assert.Equal(CleanupProvider.GitHubCopilot, now.Provider);
        Assert.Equal(CompletionOutcome.Completed, (await svc.CompleteAsync("system", Sample, now).WaitAsync(Bound)).Outcome);
        Assert.Contains(providers.Copilot.Messages, message => message.Contains(Sample, StringComparison.Ordinal));
        Assert.DoesNotContain(providers.AzureA.Messages, message => message.Contains(Sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_deployment_of_the_same_provider_is_another_recipient()
    {
        await using var harness = new CleanupHarness();
        var providers = new Providers();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = providers.Connect;
        svc.Configure(Azure("deployment-a"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var consented = svc.Recipient!;

        svc.Configure(Azure("deployment-b"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        Assert.Equal(CompletionOutcome.RecipientChanged, (await svc.CompleteAsync("system", Sample, consented).WaitAsync(Bound)).Outcome);
        Assert.DoesNotContain(providers.AllMessages, message => message.Contains(Sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_cleanup_restarts_on_another_provider_nothing_is_sent_or_offered()
    {
        await using var harness = new CleanupHarness();
        var providers = new Providers { CopilotHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = providers.Connect;
        svc.Configure(Azure("deployment-a"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var consented = svc.Recipient!;

        svc.Configure(Copilot());
        Assert.NotEqual(CleanupStatus.Ready, svc.Status);

        Assert.Null(svc.Recipient);
        Assert.Equal(CompletionOutcome.NotReady, (await svc.CompleteAsync("system", Sample, consented).WaitAsync(Bound)).Outcome);

        providers.CopilotHandshake.SetResult();
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        Assert.Equal(CompletionOutcome.RecipientChanged, (await svc.CompleteAsync("system", Sample, consented).WaitAsync(Bound)).Outcome);
        Assert.DoesNotContain(providers.AllMessages, message => message.Contains(Sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_vocabulary_or_writing_style_change_keeps_the_recipient()
    {
        // A prompt-only change rebuilds the agent in place, against the same client: the request still
        // goes where the user agreed, so the question need not be asked again.
        await using var harness = new CleanupHarness();
        var providers = new Providers();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = providers.Connect;
        svc.Configure(Azure("deployment-a") with { Glossary = "Terms: Contoso." });
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var consented = svc.Recipient!;

        svc.Configure(Azure("deployment-a") with { Glossary = "Terms: Fabrikam.", WritingStyle = "Brief." });

        Assert.Equal(CompletionOutcome.Completed, (await svc.CompleteAsync("system", Sample, consented).WaitAsync(Bound)).Outcome);
        Assert.Contains(providers.AzureA.Messages, message => message.Contains(Sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_already_built_finishes_on_its_recipient_whatever_is_saved_next()
    {
        // The check and the agent are taken together under the service's lock, so a Save that lands while
        // the request is in flight changes later requests, never this one.
        await using var harness = new CleanupHarness();
        var providers = new Providers();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = providers.Connect;
        svc.Configure(Azure("deployment-a"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var consented = svc.Recipient!;

        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providers.AzureA.BeforeAnswer = async message =>
        {
            if (message.Contains(Sample, StringComparison.Ordinal))
            {
                inFlight.TrySetResult();
                await release.Task;
            }
        };

        var completion = svc.CompleteAsync("system", Sample, consented);
        await inFlight.Task.WaitAsync(Bound);
        svc.Configure(Copilot());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        release.SetResult();

        Assert.Equal(CompletionOutcome.Completed, (await completion.WaitAsync(Bound)).Outcome);
        Assert.Contains(providers.AzureA.Messages, message => message.Contains(Sample, StringComparison.Ordinal));
        Assert.DoesNotContain(providers.Copilot.Messages, message => message.Contains(Sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_save_that_picked_foundry_local_still_asks_and_sends_only_to_the_provider_served()
    {
        // The failed Save left the window's settings on Foundry Local while cleanup kept serving Azure. The
        // question follows the provider the request would reach, whatever the window believes is saved.
        await using var harness = new CleanupHarness();
        var providers = new Providers();
        var svc = harness.Service;
        svc.ProviderFactoryForTesting = providers.Connect;
        svc.Configure(Azure("deployment-a"));
        await harness.WaitForStatusAsync(CleanupStatus.Ready);

        var recipient = svc.Recipient!;
        Assert.True(AiRequestConsent.IsNeeded(recipient, savedProvider: CleanupProvider.FoundryLocal));
        Assert.True(AiRequestConsent.IsNeeded(recipient, savedProvider: CleanupProvider.AzureFoundry));

        Assert.Equal(CompletionOutcome.Completed, (await svc.CompleteAsync("system", Sample, recipient).WaitAsync(Bound)).Outcome);
        Assert.Contains(providers.AzureA.Messages, message => message.Contains(Sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_an_on_device_recipient_with_an_on_device_saved_provider_needs_no_question()
    {
        await using var harness = new CleanupHarness();
        var svc = harness.Service;
        svc.Configure(CleanupHarness.FoundryOn());
        await harness.WaitForStatusAsync(CleanupStatus.Ready);
        var local = svc.Recipient!;

        Assert.True(local.IsOnDevice);
        Assert.False(AiRequestConsent.IsNeeded(local, CleanupProvider.FoundryLocal));
        Assert.True(AiRequestConsent.IsNeeded(local, CleanupProvider.AzureFoundry));
        Assert.True(AiRequestConsent.IsNeeded(local, CleanupProvider.OpenAiCompatible));
        Assert.True(AiRequestConsent.IsNeeded(local, CleanupProvider.GitHubCopilot));
    }

    private static CleanupOptions Azure(string deployment) => new(
        true, CleanupProvider.AzureFoundry, CleanupModelCatalog.DefaultAlias, "https://recipient-canary.example.invalid/", deployment);

    private static CleanupOptions Copilot() => new(
        true, CleanupProvider.GitHubCopilot, CleanupModelCatalog.DefaultAlias, null, null, CopilotModel: "cleanup-model");

    /// <summary>Stands in for each provider's connection: one recording client per destination.</summary>
    private sealed class Providers
    {
        public RecordingChatClient AzureA { get; } = new();

        public RecordingChatClient AzureB { get; } = new();

        public RecordingChatClient Copilot { get; } = new();

        /// <summary>When set, the Copilot handshake waits for it, honouring cancellation.</summary>
        public TaskCompletionSource? CopilotHandshake { get; init; }

        public IEnumerable<string> AllMessages => AzureA.Messages.Concat(AzureB.Messages).Concat(Copilot.Messages);

        public async Task<Func<string, AIAgent>> Connect(CleanupOptions options, CancellationToken cancellationToken)
        {
            var client = options.Provider switch
            {
                CleanupProvider.GitHubCopilot => Copilot,
                _ when options.AzureDeployment == "deployment-b" => AzureB,
                _ => AzureA,
            };

            if (options.Provider == CleanupProvider.GitHubCopilot && CopilotHandshake is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return instructions => new ChatClientAgent(client, instructions: instructions, name: "ScribeCleanup");
        }
    }

    /// <summary>Answers every call and records the user message each call carried.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public Func<string, Task>? BeforeAnswer { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var user = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            _messages.Enqueue(user);
            if (BeforeAnswer is { } before)
            {
                await before(user);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello there."));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
