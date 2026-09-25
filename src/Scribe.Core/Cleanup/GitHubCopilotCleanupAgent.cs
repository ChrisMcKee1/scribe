using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The GitHub Copilot provider's cleanup agent and its admission point (contract 2.10). A run creates a Copilot session,
/// whose system message carries the instructions and so the glossary, sends it the transcript, collects the answer and
/// destroys the session, exactly as Agent Framework 1.20.0's <c>GitHubCopilotAgent</c> runs the same
/// <see cref="SessionConfig"/>, except that the session's creation and its send are each started only through
/// <see cref="CleanupAdmission.TryHandOff"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scribe runs the session itself because <c>GitHubCopilotAgent</c> creates the session and sends to it inside one run,
/// with no step between the two a caller can reach. Gating that whole run held the gate only while the creation started:
/// the send followed once the runtime had answered the creation, long after the gate was released, so a revocation in
/// between could not hold it back, and the send is what makes the runtime call the model with the system message. Here
/// the send is handed over on its own, as the contract names it, and so is the creation, because its system message
/// hands the glossary to a runtime Scribe does not control. A request already handed over cannot be recalled: a
/// revocation between the two leaves a session that received the instructions and never a transcript, and it is
/// destroyed.
/// </para>
/// <para>
/// What the runtime receives is what the Agent Framework agent sent: the same session configuration
/// (<see cref="GitHubCopilotAgentFactory.BuildSessionConfig"/>, with streaming turned on as that agent turns it on), and
/// the messages' text joined by line feeds as the prompt; the run options are ignored, as that agent ignores them. The
/// answer is mapped from the same events into the same response content, so <see cref="AgentResponse.Text"/> and
/// <see cref="AgentResponse.Usage"/> come out the same (<c>GitHubCopilotCleanupAgentTests</c> runs both agents
/// against one fake runtime). One thing is left out: the agent's Agent Framework feature-usage mark, a process-wide bit
/// that Agent Framework can add to the User-Agent of its HTTP requests, so a later request to another provider no longer
/// says that Copilot was used.
/// </para>
/// <para>
/// Referenced only from <see cref="GitHubCopilotAgentFactory"/>, so the Copilot assemblies still load only for a user who
/// selects the provider.
/// </para>
/// </remarks>
internal sealed class GitHubCopilotCleanupAgent : AIAgent
{
    private readonly CopilotClient _client;
    private readonly SessionConfig _sessionConfig;
    private readonly string _name;

    /// <param name="client">The service's client; the service owns it and releases it, never the agent.</param>
    /// <param name="sessionConfig">This agent's configuration, copied for each run because creating a session edits it.</param>
    /// <param name="name">The agent's name.</param>
    internal GitHubCopilotCleanupAgent(CopilotClient client, SessionConfig sessionConfig, string name)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(sessionConfig);
        _client = client;
        _sessionConfig = sessionConfig;
        _name = name;
    }

    public override string Name => _name;

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (session is not null)
        {
            throw new NotSupportedException(StatelessMessage);
        }

        if (CleanupAdmission.Current is not { } admission)
        {
            throw new VocabularyHandOffRefusedException(admitted: false);
        }

        // Everything a request needs is ready before it is handed over, so each hand-off only starts its request.
        var prompt = string.Join("\n", messages.Select(message => message.Text));
        var config = _sessionConfig.Clone();
        config.Streaming = _sessionConfig.Streaming ?? true;
        var streaming = config.Streaming.Value;

        // The client was started when the provider was set up, so this carries nothing and returns at once; it keeps the
        // start of the runtime, should it ever be needed here, out of the permission gate.
        await _client.StartAsync(cancellationToken).ConfigureAwait(false);

        Task<CopilotSession>? creating = null;
        if (!admission.TryHandOff(() => creating = Start(() => _client.CreateSessionAsync(config, cancellationToken))))
        {
            throw new VocabularyHandOffRefusedException(admitted: true);
        }

        var copilotSession = await creating!.ConfigureAwait(false);
        var answered = false;
        try
        {
            // Subscribed before the send, so no event of the answer can be missed.
            var updates = Channel.CreateUnbounded<AgentResponseUpdate>();
            using var subscription = copilotSession.On<SessionEvent>(sessionEvent => Deliver(sessionEvent, streaming, updates.Writer));

            Task<string>? sending = null;
            var send = new MessageOptions { Prompt = prompt };
            if (!admission.TryHandOff(() => sending = Start(() => copilotSession.SendAsync(send, cancellationToken))))
            {
                throw new VocabularyHandOffRefusedException(admitted: true);
            }

            await sending!.ConfigureAwait(false);
            var received = new List<AgentResponseUpdate>();
            await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                received.Add(update);
            }

            answered = true;
            return received.ToAgentResponse();
        }
        finally
        {
            try
            {
                await copilotSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!answered)
            {
                // The run's own outcome is what the caller must see, a held-back send above all: a failed teardown after
                // it never turns a permission change into a failure. After an answer, a failed teardown fails the run, as
                // it did with the Agent Framework agent.
            }
        }
    }

    // Scribe never streams cleanup, and a streamed run would start sending only when it is first read, so it is refused.
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A streamed Copilot run cannot be handed over through the vocabulary admission point.");

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(StatelessMessage);

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(StatelessMessage);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedState,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(StatelessMessage);

    private const string StatelessMessage =
        "A cleanup run is stateless: each run creates its own Copilot session and destroys it.";

    // Runs inside the permission gate, so it never throws there: a request that fails to start fails its own task.
    private static Task<T> Start<T>(Func<Task<T>> start)
    {
        try
        {
            return start();
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    // The events Agent Framework 1.20.0's agent maps, mapped to the same content, roles and message ids, which is what
    // decides the response's text and usage. Tool arguments are not parsed: Scribe registers no tools and reads neither.
    private void Deliver(SessionEvent sessionEvent, bool streaming, ChannelWriter<AgentResponseUpdate> updates)
    {
        switch (sessionEvent)
        {
            case AssistantMessageDeltaEvent delta:
                updates.TryWrite(new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [new TextContent(delta.Data?.DeltaContent ?? string.Empty) { RawRepresentation = delta }])
                {
                    AgentId = Id,
                    MessageId = delta.Data?.MessageId,
                    CreatedAt = delta.Timestamp,
                });
                break;

            // Streamed, the text already arrived in the deltas; otherwise this is the only place it arrives.
            case AssistantMessageEvent message:
                AIContent content = streaming
                    ? new AIContent { RawRepresentation = message }
                    : new TextContent(message.Data?.Content ?? string.Empty) { RawRepresentation = message };
                updates.TryWrite(new AgentResponseUpdate(ChatRole.Assistant, [content])
                {
                    AgentId = Id,
                    ResponseId = message.Data?.MessageId,
                    MessageId = message.Data?.MessageId,
                    CreatedAt = message.Timestamp,
                });
                break;

            case ToolExecutionStartEvent toolStart:
                updates.TryWrite(new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(toolStart.Data?.ToolCallId ?? string.Empty, toolStart.Data?.ToolName ?? string.Empty)
                    {
                        RawRepresentation = toolStart,
                    }])
                {
                    AgentId = Id,
                    CreatedAt = toolStart.Timestamp,
                });
                break;

            case ToolExecutionCompleteEvent toolComplete:
                object? result = toolComplete.Data?.Success == true
                    ? toolComplete.Data?.Result?.Content
                    : toolComplete.Data?.Error?.Message ?? "Tool execution failed";
                updates.TryWrite(new AgentResponseUpdate(
                    ChatRole.Tool,
                    [new FunctionResultContent(toolComplete.Data?.ToolCallId ?? string.Empty, result) { RawRepresentation = toolComplete }])
                {
                    AgentId = Id,
                    CreatedAt = toolComplete.Timestamp,
                });
                break;

            case AssistantUsageEvent usage:
                updates.TryWrite(new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [new UsageContent(UsageOf(usage.Data)) { RawRepresentation = usage }])
                {
                    AgentId = Id,
                    CreatedAt = usage.Timestamp,
                });
                break;

            case SessionIdleEvent idle:
                updates.TryWrite(Raw(idle));
                updates.TryComplete();
                break;

            // The runtime's message goes into the exception, as the agent put it there; Scribe logs a failure by its shape.
            case SessionErrorEvent error:
                updates.TryWrite(Raw(error));
                updates.TryComplete(new InvalidOperationException($"Session error: {error.Data?.Message ?? "Unknown error"}"));
                break;

            default:
                updates.TryWrite(Raw(sessionEvent));
                break;
        }
    }

    private AgentResponseUpdate Raw(SessionEvent sessionEvent) =>
        new(ChatRole.Assistant, [new AIContent { RawRepresentation = sessionEvent }])
        {
            AgentId = Id,
            CreatedAt = sessionEvent.Timestamp,
        };

    private static UsageDetails UsageOf(AssistantUsageData? data)
    {
        AdditionalPropertiesDictionary<long>? additional = null;
        if (data?.CacheWriteTokens is long cacheWriteTokens)
        {
            additional ??= [];
            additional[nameof(AssistantUsageData.CacheWriteTokens)] = cacheWriteTokens;
        }

        // Evaluation-only in the SDK; read because the agent this replaces reported it, and the parity test pins that.
#pragma warning disable GHCP001
        if (data?.Cost is double cost)
        {
            additional ??= [];
            additional[nameof(AssistantUsageData.Cost)] = (long)cost;
        }
#pragma warning restore GHCP001

        if (data?.Duration is TimeSpan duration)
        {
            additional ??= [];
            additional[nameof(AssistantUsageData.Duration)] = (long)duration.TotalMilliseconds;
        }

        return new UsageDetails
        {
            InputTokenCount = data?.InputTokens,
            OutputTokenCount = data?.OutputTokens,
            TotalTokenCount = (data?.InputTokens ?? 0) + (data?.OutputTokens ?? 0),
            CachedInputTokenCount = data?.CacheReadTokens,
            AdditionalCounts = additional,
        };
    }
}
