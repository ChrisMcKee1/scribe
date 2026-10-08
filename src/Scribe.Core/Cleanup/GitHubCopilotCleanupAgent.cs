using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Cleanup;

/// <summary>
/// The GitHub Copilot provider's cleanup agent and its admission point (contract 2.10). A run creates a Copilot session,
/// whose system message carries the instructions and so the glossary, sends it the transcript, collects the answer and
/// detaches and best-effort deletes the session. It preserves Agent Framework's <see cref="SessionConfig"/> and answer
/// mapping, except that the session's creation and its send are each started only through
/// <see cref="CleanupAdmission.TryHandOff"/>, and deletion follows detach.
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
/// best-effort deleted.
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
/// <para>
/// Each cleanup step waits at most two seconds, four in total, independently of the cancelled caller. Its owner keeps
/// the actual SDK work leased after a wait expires and refuses any step once shutdown closes admission. A refused
/// deletion leaves disk cleanup best-effort; it must never reconnect a client the owner already stopped.
/// </para>
/// </remarks>
internal sealed class GitHubCopilotCleanupAgent : AIAgent
{
    private readonly CopilotClient _client;
    private readonly GitHubCopilotClientLifetime _owner;
    private readonly SessionConfig _sessionConfig;
    private readonly string _name;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    internal static readonly TimeSpan CleanupStepTimeout = TimeSpan.FromSeconds(2);

    /// <param name="client">The service's client; the service owns it and releases it, never the agent.</param>
    /// <param name="owner">The same owner the service releases, which closes admission before stopping its client.</param>
    /// <param name="sessionConfig">This agent's configuration, copied for each run because creating a session edits it.</param>
    /// <param name="name">The agent's name.</param>
    /// <param name="logger">Receives cleanup failures by shape only, behind a non-throwing boundary.</param>
    /// <param name="timeProvider">The clock for the cleanup bounds.</param>
    internal GitHubCopilotCleanupAgent(
        CopilotClient client,
        GitHubCopilotClientLifetime owner,
        SessionConfig sessionConfig,
        string name,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(sessionConfig);
        _client = client;
        _owner = owner;
        _sessionConfig = sessionConfig;
        _name = name;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            throw new VocabularyHandOffRefusedException(HandOffRefusal.NoAdmission);
        }

        // Everything a request needs is ready before it is handed over, so each hand-off only starts its request.
        var prompt = string.Join("\n", messages.Select(message => message.Text));
        var config = _sessionConfig.Clone();
        // Known before the RPC, so a failed or cancelled creation can still have its disk state deleted.
        var sessionId = Guid.NewGuid().ToString();
        config.SessionId = sessionId;
        config.Streaming = _sessionConfig.Streaming ?? true;
        var streaming = config.Streaming.Value;

        // The client was started when the provider was set up, so this carries nothing and returns at once; it keeps the
        // start of the runtime, should it ever be needed here, out of the permission gate.
        cancellationToken.ThrowIfCancellationRequested();
        await _owner.RunAsync(() => _client.StartAsync(cancellationToken)).ConfigureAwait(false);

        Task<CopilotSession>? creating = null;
        CopilotSession? copilotSession = null;
        try
        {
            if (!admission.TryHandOff(
                () => creating = Start(() => _owner.RunAsync(() => _client.CreateSessionAsync(config, cancellationToken))), out var refusal))
            {
                throw new VocabularyHandOffRefusedException(refusal);
            }

            copilotSession = await creating!.ConfigureAwait(false);

            // Subscribed before the send, so no event of the answer can be missed.
            var updates = Channel.CreateUnbounded<AgentResponseUpdate>();
            using var subscription = copilotSession.On<SessionEvent>(sessionEvent => Deliver(sessionEvent, streaming, updates.Writer));

            // Judged again, on its own: a revocation, or for a completion a change of recipient, since the creation holds
            // the send back.
            Task<string>? sending = null;
            var send = new MessageOptions { Prompt = prompt };
            if (!admission.TryHandOff(
                () => sending = Start(() => _owner.RunAsync(() => copilotSession.SendAsync(send, cancellationToken))), out refusal))
            {
                throw new VocabularyHandOffRefusedException(refusal);
            }

            await sending!.ConfigureAwait(false);
            var received = new List<AgentResponseUpdate>();
            await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                received.Add(update);
            }

            return received.ToAgentResponse();
        }
        finally
        {
            if (creating is not null)
            {
                if (copilotSession is not null)
                {
                    // SDK 1.0.14 closes the event channel before awaiting detach, then unregisters in its finally.
                    // Disposal preserves disk state; deletion is still attempted if detach fails or exceeds its bound.
                    await CleanupStepAsync(_ => copilotSession.DisposeAsync().AsTask(), "detach").ConfigureAwait(false);
                }

                await CleanupStepAsync(
                    token => _client.DeleteSessionAsync(sessionId, token), "delete").ConfigureAwait(false);
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
        "A cleanup run is stateless: each run creates its own Copilot session and attempts to delete it.";

    internal async Task CleanupStepAsync(Func<CancellationToken, Task> cleanup, string step)
    {
        Task? work = null;
        // Never linked to the caller: a cancelled dictation still owes cleanup. WaitAsync also bounds a stalled SDK
        // operation, notably DisposeAsync, which has no cancellation parameter. A pending detach stays with the SDK
        // until a reply or the owner disconnects; successful deletion removes its client registration in the meantime.
        try
        {
            using var bound = new CancellationTokenSource(CleanupStepTimeout, _timeProvider);
            var token = bound.Token;
            // Admission is taken on the worker immediately before invoking the SDK. It covers the actual task,
            // including a synchronous prefix and any tail that outlives this caller's bound.
            work = Task.Run(() => _owner.RunAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return cleanup(token);
            }));
            await work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (work is not null)
            {
                _ = work.ContinueWith(
                    completed => { _ = completed.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                _logger?.LogWarning(
                    "Copilot session cleanup {Step} failed: {FailureShape}. Local session data may remain.",
                    step, FailureShape.Describe(ex));
            }
            catch (Exception)
            {
                // Cleanup and its diagnostics never change an answer, a cancellation or a held-back send.
            }
        }
    }

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

    // The events Agent Framework's agent maps, mapped to the same content, roles and message ids, which is what
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
