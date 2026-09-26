using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>One call the Copilot SDK made to the fake runtime: its method and its parameters, as they arrived.</summary>
internal sealed record CopilotRuntimeRequest(int Index, string Method, JsonElement Params)
{
    public string? SessionId => Text(Params, "sessionId");

    /// <summary>A creation's system message: the instructions, and so the glossary.</summary>
    public string? SystemMessage =>
        Params.ValueKind == JsonValueKind.Object && Params.TryGetProperty("systemMessage", out var message)
            ? Text(message, "content")
            : null;

    /// <summary>A send's prompt: the transcript.</summary>
    public string? Prompt => Text(Params, "prompt");

    // Letters and spaces only in every canary, so JSON escaping in the parameters can never hide one.
    public bool Carries(string canary) => Params.GetRawText().Contains(canary, StringComparison.Ordinal);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// A Copilot runtime for the SDK to connect to over loopback (<see cref="RuntimeConnection.ForUri"/>), answering the
/// JSON-RPC calls a cleanup run makes the way the CLI answers them: <c>connect</c>, <c>session.create</c>,
/// <c>session.send</c> (answered, then followed by the events the CLI streams: the answer in two deltas, the message, its
/// usage, and idle) and <c>session.destroy</c>. The events are the SDK's own types serialized by the SDK
/// (<see cref="SessionEvent.ToJson"/>), so the SDK reads what it reads from the CLI. Every call is recorded as it arrives,
/// before it is answered, and a test holds any answer to put a step between two others without a sleep. The listener is
/// in the test process, bound to the loopback address: nothing leaves the machine.
/// </summary>
internal sealed class FakeCopilotRuntime : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<CopilotRuntimeRequest> _requests = new();
    private readonly ConcurrentQueue<Task> _work = new();
    private readonly Task _accepting;
    private int _count;
    private int _messages;

    public FakeCopilotRuntime()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        _accepting = AcceptAsync();
    }

    /// <summary>What the SDK connects to: <c>RuntimeConnection.ForUri(Url)</c>.</summary>
    public string Url { get; }

    public IReadOnlyList<CopilotRuntimeRequest> Requests => [.. _requests];

    public IReadOnlyList<CopilotRuntimeRequest> Creates => [.. _requests.Where(request => request.Method == "session.create")];

    public IReadOnlyList<CopilotRuntimeRequest> Sends => [.. _requests.Where(request => request.Method == "session.send")];

    /// <summary>The answer to a send's prompt: by default its transcript, echoed back so a cleanup is accepted as unchanged.</summary>
    public Func<string, string> Answer { get; set; } = prompt => TranscriptOf(prompt) ?? "ok";

    /// <summary>When set, a send is followed by a <c>session.error</c> with this message instead of an answer.</summary>
    public string? SessionError { get; set; }

    /// <summary>Runs before a call is answered; a test holds an answer by returning a task it completes later.</summary>
    public Func<CopilotRuntimeRequest, CancellationToken, Task> BeforeAnswer { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Signalled with each call as it arrives, before it is answered.</summary>
    public event Action<CopilotRuntimeRequest>? Arrived;

    /// <summary>A task that completes with the first call, from now on, that <paramref name="match"/> accepts.</summary>
    public Task<CopilotRuntimeRequest> Next(Func<CopilotRuntimeRequest, bool> match)
    {
        var arrived = new TaskCompletionSource<CopilotRuntimeRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(CopilotRuntimeRequest request)
        {
            if (match(request) && arrived.TrySetResult(request))
            {
                Arrived -= Check;
            }
        }

        Arrived += Check;
        return arrived.Task;
    }

    /// <summary>The transcript inside a prompt's delimiters, or null when it has none.</summary>
    public static string? TranscriptOf(string prompt)
    {
        var open = prompt.IndexOf(TextCleanupService.TranscriptOpenTag, StringComparison.Ordinal);
        var close = prompt.IndexOf(TextCleanupService.TranscriptCloseTag, StringComparison.Ordinal);
        return open >= 0 && close > open
            ? prompt[(open + TextCleanupService.TranscriptOpenTag.Length)..close].Trim()
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;

        // A fault that is not a closed connection is the fake failing, which a passing test must not hide.
        while (_work.TryDequeue(out var work))
        {
            await work;
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _work.Enqueue(ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var owned = client;
        var stream = client.GetStream();
        var writes = new SemaphoreSlim(1, 1);
        try
        {
            while (await ReadFrameAsync(stream, _stop.Token) is { } body)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                // A notification (the SDK's cancellation of a request) or a response: nothing to answer.
                if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("id", out var id))
                {
                    continue;
                }

                // The SDK sends a single request object as the params, and a positional array only for several.
                var parameters = root.TryGetProperty("params", out var given) ? given.Clone() : default;
                if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() == 1)
                {
                    parameters = parameters[0].Clone();
                }

                var request = new CopilotRuntimeRequest(Interlocked.Increment(ref _count), method.GetString()!, parameters);
                _requests.Enqueue(request);
                Arrived?.Invoke(request);

                // Answered on its own task, so a held answer never stops the calls behind it from being read.
                _work.Enqueue(AnswerAsync(stream, writes, id.Clone(), request));
            }
        }
        catch (Exception ex) when (IsDisconnection(ex))
        {
            // The SDK closed the connection, or the fake is being disposed.
        }
    }

    private async Task AnswerAsync(NetworkStream stream, SemaphoreSlim writes, JsonElement id, CopilotRuntimeRequest request)
    {
        try
        {
            await BeforeAnswer(request, _stop.Token);
            switch (request.Method)
            {
                case "connect":
                    await WriteAsync(stream, writes, Result(id, "{\"ok\":true,\"protocolVersion\":3,\"version\":\"fake\"}"));
                    break;

                // The SDK names the session it creates and checks that the answer echoes it.
                case "session.create":
                    await WriteAsync(
                        stream,
                        writes,
                        Result(id, "{\"sessionId\":" + JsonSerializer.Serialize(request.SessionId) + ",\"workspacePath\":null}"));
                    break;

                case "session.send":
                    var messageId = "message-" + Interlocked.Increment(ref _messages).ToString(CultureInfo.InvariantCulture);
                    await WriteAsync(stream, writes, Result(id, "{\"messageId\":\"" + messageId + "\"}"));
                    foreach (var sessionEvent in EventsFor(request, messageId))
                    {
                        await WriteAsync(
                            stream,
                            writes,
                            "{\"jsonrpc\":\"2.0\",\"method\":\"session.event\",\"params\":{\"sessionId\":" +
                            JsonSerializer.Serialize(request.SessionId) + ",\"event\":" + sessionEvent + "}}");
                    }

                    break;

                case "session.destroy":
                    await WriteAsync(stream, writes, Result(id, "null"));
                    break;

                default:
                    await WriteAsync(
                        stream,
                        writes,
                        "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}");
                    break;
            }
        }
        catch (Exception ex) when (IsDisconnection(ex))
        {
            // The connection closed before the answer could be written, or the fake is being disposed.
        }
    }

    private IEnumerable<string> EventsFor(CopilotRuntimeRequest send, string messageId)
    {
        var now = DateTimeOffset.UtcNow;
        if (SessionError is { } error)
        {
            yield return new SessionErrorEvent
            {
                Id = Guid.NewGuid(),
                Timestamp = now,
                Data = new SessionErrorData { ErrorType = "model", Message = error },
            }.ToJson();
            yield break;
        }

        var answer = Answer(send.Prompt ?? string.Empty);
        var half = answer.Length / 2;
        foreach (var part in new[] { answer[..half], answer[half..] })
        {
            yield return new AssistantMessageDeltaEvent
            {
                Id = Guid.NewGuid(),
                Timestamp = now,
                Data = new AssistantMessageDeltaData { DeltaContent = part, MessageId = messageId },
            }.ToJson();
        }

        yield return new AssistantMessageEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = now,
            Data = new AssistantMessageData { Content = answer, MessageId = messageId },
        }.ToJson();

        yield return new AssistantUsageEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = now,
            Data = new AssistantUsageData
            {
                Model = "fake-model",
                InputTokens = 5,
                OutputTokens = 7,
                CacheReadTokens = 2,
                CacheWriteTokens = 1,
#pragma warning disable GHCP001 // Evaluation-only in the SDK; set so the parity test compares every count.
                Cost = 3,
#pragma warning restore GHCP001
                Duration = TimeSpan.FromMilliseconds(40),
            },
        }.ToJson();

        yield return new SessionIdleEvent { Id = Guid.NewGuid(), Timestamp = now, Data = new SessionIdleData() }.ToJson();
    }

    private static string Result(JsonElement id, string result) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}";

    private async Task WriteAsync(Stream stream, SemaphoreSlim writes, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
        await writes.WaitAsync(_stop.Token);
        try
        {
            await stream.WriteAsync(header, _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        finally
        {
            writes.Release();
        }
    }

    // The framing the CLI and the SDK share: "Content-Length: N", a blank line, then N bytes of JSON.
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(64);
        var one = new byte[1];
        while (header.Count < 4 || !header.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                return header.Count == 0 ? null : throw new EndOfStreamException("The SDK closed the connection inside a frame header.");
            }

            header.Add(one[0]);
        }

        const string Prefix = "Content-Length:";
        var line = Encoding.ASCII.GetString([.. header])
            .Split("\r\n")
            .Single(candidate => candidate.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
        var body = new byte[int.Parse(line[Prefix.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture)];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return body;
    }

    private static bool IsDisconnection(Exception exception) =>
        exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException;
}
