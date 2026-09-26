using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Scribe.Core.Cleanup;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Tests.CleanupLogging;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// A library vocabulary source that keeps contract 2.10 exactly: <see cref="TryHandOff"/> runs the hand-off under the
/// permission gate when the published scope still covers the admitted one, and a publication takes the same gate, so a
/// request is handed over either before a revocation or not at all. Every hand-off is recorded with its admitted scope.
/// </summary>
internal sealed class TestVocabularySource : ILibraryVocabularySource
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<(AiVocabularyScope Admitted, bool HandedOver)> _handOffs = new();
    private LibraryVocabulary _current;
    private int _currentReads;

    public TestVocabularySource(LibraryVocabulary initial) => _current = initial;

    public LibraryVocabulary Current
    {
        get
        {
            Interlocked.Increment(ref _currentReads);
            ReadingCurrent?.Invoke();
            if (CurrentFailure is { } failure)
            {
                throw failure;
            }

            return Volatile.Read(ref _current);
        }
    }

    /// <summary>Runs on every read of <see cref="Current"/>, on the reading thread; a test blocks a build here.</summary>
    public Action? ReadingCurrent { get; set; }

    /// <summary>When set, <see cref="Current"/> throws this.</summary>
    public Exception? CurrentFailure { get; set; }

    public int CurrentReads => Volatile.Read(ref _currentReads);

    /// <summary>How many handlers <see cref="Changed"/> has now: a disposed subscriber must have removed its own.</summary>
    public int ChangedSubscribers => Changed?.GetInvocationList().Length ?? 0;

    public IReadOnlyList<(AiVocabularyScope Admitted, bool HandedOver)> HandOffs => [.. _handOffs];

    public event Action<long>? Changed;

    /// <summary>Publishes <paramref name="next"/> under the permission gate, as J publishes a narrowing, then raises Changed.</summary>
    public void Publish(LibraryVocabulary next)
    {
        lock (_gate)
        {
            Volatile.Write(ref _current, next);
        }

        ResilientEvent.InvokeAll(Changed, next.Generation);
    }

    public bool TryHandOff(AiVocabularyScope admitted, Action handOff)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(handOff);
        lock (_gate)
        {
            if (!Volatile.Read(ref _current).AiScope.Covers(admitted))
            {
                _handOffs.Enqueue((admitted, false));
                return false;
            }

            handOff();
            _handOffs.Enqueue((admitted, true));
            return true;
        }
    }
}

/// <summary>Builds committed library vocabularies the way composition would: the permitted libraries' entries are the AI entries.</summary>
internal static class TestVocabularies
{
    public static readonly LibraryContentHash H1 = new(new string('1', 64));
    public static readonly LibraryContentHash H2 = new(new string('2', 64));

    /// <summary>One library in a vocabulary: its id, the content its permission covers, its entries, and whether AI cleanup may have them.</summary>
    public sealed record Library(string Id, LibraryContentHash? Content, bool Permitted, params DictionaryEntry[] Entries);

    public static LibraryVocabulary Of(long generation, params Library[] libraries)
    {
        var entries = libraries.SelectMany(library => library.Entries).ToList();
        var aiEntries = libraries.Where(library => library.Permitted).SelectMany(library => library.Entries).ToList();
        var scope = new AiVocabularyScope(
            generation,
            libraries.Where(library => library.Permitted).Select(library => KeyValuePair.Create(library.Id, library.Content)));
        return new LibraryVocabulary(generation, entries, aiEntries, scope);
    }

    public static DictionaryEntry Entry(string spoken, string written) => DictionaryEntry.New(spoken, written);
}

/// <summary>One request that reached the fake network: its path, its body, and when it arrived.</summary>
internal sealed record SentRequest(int Index, string Path, string Body)
{
    // Letters and spaces only in every canary, so JSON escaping in a body can never hide one.
    public bool Carries(string canary) => Body.Contains(canary, StringComparison.Ordinal);

    public bool IsProbe => TranscriptOf() == "ok";

    /// <summary>The transcript inside the user message, whichever surface carried it.</summary>
    public string? TranscriptOf()
    {
        var text = UserText();
        if (text is null)
        {
            return null;
        }

        var open = text.IndexOf(TextCleanupService.TranscriptOpenTag, StringComparison.Ordinal);
        var close = text.IndexOf(TextCleanupService.TranscriptCloseTag, StringComparison.Ordinal);
        return open >= 0 && close > open
            ? text[(open + TextCleanupService.TranscriptOpenTag.Length)..close].Trim()
            : text.Trim();
    }

    // The user turn's text: Chat Completions' messages or Responses' input.
    private string? UserText()
    {
        try
        {
            using var document = JsonDocument.Parse(Body);
            var root = document.RootElement;
            var parts = new List<string>();
            foreach (var name in new[] { "messages", "input" })
            {
                if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var message in list.EnumerateArray())
                {
                    if (message.TryGetProperty("role", out var role) && role.GetString() == "user")
                    {
                        CollectText(message.GetProperty("content"), parts);
                    }
                }
            }

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectText(JsonElement content, List<string> parts)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                parts.Add(content.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                    {
                        parts.Add(text.GetString()!);
                    }
                }

                break;
        }
    }
}

/// <summary>
/// The network under the vocabulary hand-off handler: records every request that reached it and answers it as the test
/// scripts, echoing the transcript back by default so a cleanup is accepted as unchanged. A request can be held at the
/// network (the gate is released by the test) to put something between two steps without a single sleep.
/// </summary>
internal sealed class CanaryNetwork : HttpMessageHandler
{
    private readonly ConcurrentQueue<SentRequest> _sent = new();
    private int _count;

    public IReadOnlyList<SentRequest> Sent => [.. _sent];

    /// <summary>Answers a request; by default Chat Completions or Responses, echoing the transcript.</summary>
    public Func<SentRequest, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (request, _) => Task.FromResult(Echo(request));

    /// <summary>Signalled with each request as it arrives, before it is answered.</summary>
    public event Action<SentRequest>? Arrived;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new SentRequest(Interlocked.Increment(ref _count), request.RequestUri!.AbsolutePath, body);
        _sent.Enqueue(sent);
        Arrived?.Invoke(sent);
        return await Respond(sent, cancellationToken);
    }

    /// <summary>A task that completes with the first request, from now on, that <paramref name="match"/> accepts.</summary>
    public Task<SentRequest> Next(Func<SentRequest, bool> match)
    {
        var arrived = new TaskCompletionSource<SentRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(SentRequest request)
        {
            if (match(request) && arrived.TrySetResult(request))
            {
                Arrived -= Check;
            }
        }

        Arrived += Check;
        return arrived.Task;
    }

    public static HttpResponseMessage Echo(SentRequest request)
    {
        var text = request.TranscriptOf() ?? "ok";
        return request.Path.EndsWith("/responses", StringComparison.Ordinal) ? Responses(text) : Chat(text);
    }

    public static HttpResponseMessage Chat(string content) => Json(HttpStatusCode.OK,
        "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"test\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content) + "}," +
        "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}");

    public static HttpResponseMessage Responses(string content) => Json(HttpStatusCode.OK,
        "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,\"status\":\"completed\"," +
        "\"model\":\"test\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\"," +
        "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":" + JsonSerializer.Serialize(content) +
        ",\"annotations\":[]}]}],\"parallel_tool_calls\":false,\"tool_choice\":\"auto\",\"tools\":[]," +
        "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// The OpenAI client's own retry policy, with each wait replaced by a gate the test releases: a retry happens exactly
/// when the test says, after whatever it put in between.
/// </summary>
internal sealed class GatedRetryPolicy(int maxRetries) : ClientRetryPolicy(maxRetries)
{
    private readonly ConcurrentQueue<TaskCompletionSource> _waits = new();

    /// <summary>Signalled when a retry is waiting; completing the released source lets it go.</summary>
    public TaskCompletionSource<TaskCompletionSource> Waiting { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Positive, or the policy skips WaitAsync altogether; the wait itself is the gate, never this long.
    protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.FromTicks(1);

    protected override async Task WaitAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waits.Enqueue(release);
        var waiting = Waiting;
        Waiting = new TaskCompletionSource<TaskCompletionSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.TrySetResult(release);
        await release.Task.WaitAsync(cancellationToken);
    }

    protected override void Wait(TimeSpan time, CancellationToken cancellationToken) =>
        WaitAsync(time, cancellationToken).GetAwaiter().GetResult();
}

/// <summary>
/// A cleanup service built the way the app builds it, with the library vocabulary's admission point, over fakes: the
/// production transport, hand-off handler included, sends to a <see cref="CanaryNetwork"/>, and Foundry Local runs on
/// the fake runtime. Nothing leaves the process.
/// </summary>
internal sealed class VocabularyCleanupHarness : IAsyncDisposable
{
    public VocabularyCleanupHarness(ILibraryVocabularySource? source, CanaryNetwork? network = null)
    {
        Temp = new TempDirectory();
        State = new FakeFoundryState();
        Qwen = FakeFoundryModel.Family(State, CleanupHarness.FoundryAlias, CleanupHarness.FoundryVariant);
        Catalog = new FakeFoundryCatalog(State, [Qwen]);
        Runtime = new FakeFoundryRuntime(State, Catalog);
        Host = new FakeFoundryHost(() => Runtime);
        Network = network ?? new CanaryNetwork();
        Service = new TextCleanupService(Log, new AppPaths(Temp.Combine("data")), Host, null, source)
        {
            InnerHttpHandlerForTesting = Network,

            // No retries unless a test asks for them, as the other cleanup harness has it; the transport is left alone.
            OpenAIClientOptionsOverride = options => options.RetryPolicy = RetryPolicy ?? new ClientRetryPolicy(0),
        };
    }

    public TempDirectory Temp { get; }

    public FakeFoundryState State { get; }

    public FakeFoundryModel Qwen { get; }

    public FakeFoundryCatalog Catalog { get; }

    public FakeFoundryRuntime Runtime { get; }

    public FakeFoundryHost Host { get; }

    public CanaryNetwork Network { get; }

    /// <summary>The retry policy clients built from now on use; none (no retries) when null.</summary>
    public ClientRetryPolicy? RetryPolicy { get; set; }

    public CapturingLogger<TextCleanupService> Log { get; } = new();

    public TextCleanupService Service { get; }

    public async Task ConfigureAndWaitAsync(CleanupOptions options)
    {
        Service.Configure(options);
        await WaitForStatusAsync(CleanupStatus.Ready);
    }

    public async Task WaitForStatusAsync(CleanupStatus status)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (Service.Status == status)
            {
                reached.TrySetResult();
            }
        }

        Service.StatusChanged += Check;
        try
        {
            Check();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            Service.StatusChanged -= Check;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Temp.Dispose();
    }

    public static CleanupOptions Custom(CleanupPromptStyle style = CleanupPromptStyle.Frontier) =>
        CleanupHarness.Custom("https://vocabulary-canary.example.invalid/v1") with { PromptStyle = style };

    // API-key authentication, so the real Azure initializer builds its clients over the fake network without asking
    // any credential for a token.
    public static CleanupOptions Azure() => new(
        true,
        CleanupProvider.AzureFoundry,
        CleanupModelCatalog.DefaultAlias,
        "https://vocabulary-canary.example.invalid/",
        "cleanup-deployment",
        AzureApiKey: "not-a-real-key");
}
