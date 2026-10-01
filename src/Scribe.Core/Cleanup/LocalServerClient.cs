using System.Net.Http.Json;
using System.Text.Json;

namespace Scribe.Core.Cleanup;

/// <summary>Whether an app on this PC answered.</summary>
public enum LocalServerReach
{
    /// <summary>It answered, and <see cref="LocalServerState.Models"/> is what it has.</summary>
    Reached,

    /// <summary>Nothing listens at its address: the app is not open.</summary>
    NotRunning,

    /// <summary>Something answered, but not as the app does, or not in time.</summary>
    Failed,

    /// <summary>It answered that it needs an API key Scribe was not given (LM Studio can require one).</summary>
    NeedsKey,
}

/// <summary>A model an app on this PC has downloaded and can chat with.</summary>
/// <param name="Id">The name requests use: Ollama's <c>name</c>, LM Studio's <c>key</c>.</param>
/// <param name="DisplayName">What the app itself calls it.</param>
/// <param name="SizeBytes">Its size on disk, 0 when the app does not say.</param>
public sealed record LocalServerModel(string Id, string DisplayName, long SizeBytes)
{
    /// <summary>The largest context the app says it can load the model with, in tokens (LM Studio's <c>max_context_length</c>); 0 when it does not say.</summary>
    public int MaxContextTokens { get; init; }
}

/// <summary>A model an app on this PC holds in memory now.</summary>
/// <param name="Id">The name requests use.</param>
/// <param name="MemoryBytes">
/// What it takes: Ollama's own figure; for LM Studio, which reports none per model, the model's size, which is close.
/// </param>
public sealed record LocalServerLoadedModel(string Id, long MemoryBytes)
{
    /// <summary>
    /// The context the app loaded it with, in tokens: Ollama's <c>context_length</c>, LM Studio's instance
    /// <c>config.context_length</c>; 0 when it does not say.
    /// </summary>
    public int ContextTokens { get; init; }

    /// <summary>LM Studio's id for this loaded instance, the name it unloads by; null for Ollama.</summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// LM Studio: the seconds left before it unloads the instance on its own (<c>remaining_ttl_seconds</c>); null when it
    /// says none, as for a model loaded by hand, which stays until it is unloaded.
    /// </summary>
    public long? RemainingTtlSeconds { get; init; }
}

/// <summary>What an app on this PC has downloaded and what it holds in memory, read at one moment.</summary>
public sealed record LocalServerState(
    LocalServerReach Reach,
    IReadOnlyList<LocalServerModel> Models,
    IReadOnlyList<LocalServerLoadedModel> Loaded)
{
    public static LocalServerState NotRunning { get; } = new(LocalServerReach.NotRunning, [], []);

    public static LocalServerState Failed { get; } = new(LocalServerReach.Failed, [], []);

    public static LocalServerState NeedsKey { get; } = new(LocalServerReach.NeedsKey, [], []);

    /// <summary>The loaded model that <paramref name="modelId"/> names, if the app holds it now.</summary>
    public LocalServerLoadedModel? LoadedFor(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId)
            ? null
            : Loaded.FirstOrDefault(loaded => LocalServerClient.SameModel(loaded.Id, modelId));
}

/// <summary>
/// Asks Ollama or LM Studio, at its own address on this PC (<see cref="LocalAiServer.AppAt"/>), which models it has and
/// which it holds in memory, and asks it to free one. What Settings shows under "On this PC", and how Scribe gives the
/// memory back when AI cleanup no longer needs the model.
/// </summary>
public interface ILocalServerClient
{
    /// <summary>
    /// Reads what the app at <paramref name="endpoint"/> has and holds, with the API key saved for that address when there
    /// is one. Never throws.
    /// </summary>
    Task<LocalServerState> ReadAsync(string endpoint, string? apiKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the app at <paramref name="endpoint"/> to free <paramref name="modelId"/>'s memory, with the API key saved for
    /// that address when there is one. True when the app took the request, or held nothing of the model to free. Never
    /// throws.
    /// </summary>
    Task<bool> UnloadAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// LM Studio only: loads <paramref name="modelId"/> with a context of <paramref name="contextTokens"/> through LM
    /// Studio's own chat API, which its OpenAI-compatible one cannot ask for. Returns the id of the instance it loaded, or
    /// null when it did not. Never throws.
    /// </summary>
    Task<string?> LoadWithContextAsync(
        string endpoint, string modelId, int contextTokens, string? apiKey = null, CancellationToken cancellationToken = default);

    /// <summary>LM Studio only: unloads one loaded instance, by its id. True when the app took the request. Never throws.</summary>
    Task<bool> UnloadInstanceAsync(
        string endpoint, string instanceId, string? apiKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ollama only: the largest context <paramref name="modelId"/> was made for, in tokens (its <c>context_length</c> in
    /// <c>/api/show</c>); 0 when the app does not say. Never throws.
    /// </summary>
    Task<int> ReadMaxContextAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// The requests behind <see cref="ILocalServerClient"/>. Only ever sent to an address <see cref="LocalAiServer.AppAt"/>
/// recognizes, so only to this PC, and never with anything the user said; the only credential is the API key the user
/// saved for that same address.
/// </summary>
/// <remarks>
/// Ollama: <c>GET /api/tags</c> lists the downloaded models (a model whose capabilities leave out "completion", such as
/// an embedding model, is left out), <c>GET /api/ps</c> the loaded ones with their memory and context, <c>POST
/// /api/show</c> one model's details, among them the largest context it was made for, and <c>POST /api/generate</c>
/// with <c>keep_alive: 0</c> unloads one at once. LM Studio: <c>GET /api/v1/models</c> lists every model with its
/// <c>type</c> (only <c>llm</c> can chat), its <c>max_context_length</c> and its <c>loaded_instances</c>, each with
/// its <c>config.context_length</c>; <c>POST /api/v1/models/unload</c> with an instance's id unloads it; and
/// <c>POST /api/v1/chat</c> with a <c>context_length</c> loads a model at that size, which its OpenAI-compatible chat
/// cannot ask for (measured on 0.4.25: the OpenAI-compatible request loaded the model at LM Studio's own size whatever
/// it asked, and LM Studio's own load request refuses a <c>ttl</c>, so a model loaded that way would stay until it is
/// unloaded, where one LM Studio's chat loads is unloaded after LM Studio's own idle time). With "Require
/// Authentication" on (LM Studio 0.4 and later), LM Studio refuses a request without one of its API tokens, so the key
/// saved for that address goes with every request as a bearer token, as it does with a chat request, and a refusal
/// (401 or 403) reads as <see cref="LocalServerReach.NeedsKey"/>. Measured on Ollama 0.34.4 and 0.35.0 and LM Studio
/// 0.4.25.
/// </remarks>
public sealed class LocalServerClient : ILocalServerClient, IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(15);

    // A model load reads gigabytes from disk: 5 to 7 s for Gemma 4 E2B on this machine, longer for a larger model or a slow
    // disk.
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;

    public LocalServerClient()
        : this(CreateHandler())
    {
    }

    /// <summary>For tests: the requests go through <paramref name="handler"/>.</summary>
    internal LocalServerClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // No proxy, and no redirects: every request goes to the app's own address on this PC and nowhere else. A redirect
    // would carry the saved key, as a bearer token, to wherever the answer pointed (the chat requests' transport refuses
    // redirects for the same reason, see VocabularyHandOff).
    internal static SocketsHttpHandler CreateHandler() =>
        new() { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) };

    public void Dispose() => _http.Dispose();

    public async Task<LocalServerState> ReadAsync(
        string endpoint, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (Root(endpoint) is not { } root)
        {
            return LocalServerState.Failed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            return root.App switch
            {
                LocalServerApp.Ollama => await ReadOllamaAsync(root.Uri, apiKey, timeout.Token).ConfigureAwait(false),
                _ => await ReadLmStudioAsync(root.Uri, apiKey, timeout.Token).ConfigureAwait(false),
            };
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return LocalServerState.NotRunning;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalServerState.Failed;
        }
        catch (Exception)
        {
            // Not the app we expected, an answer we could not read, or no answer in time.
            return LocalServerState.Failed;
        }
    }

    public async Task<bool> UnloadAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (Root(endpoint) is not { } root || string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UnloadTimeout);
        try
        {
            if (root.App == LocalServerApp.Ollama)
            {
                // keep_alive 0 on a request with no prompt unloads the model at once (Ollama's FAQ, "How do I keep a
                // model loaded in memory or make it unload immediately?"). A model it does not hold is left as it is.
                using var unload = Request(HttpMethod.Post, new Uri(root.Uri, "/api/generate"), apiKey,
                    new { model = modelId.Trim(), keep_alive = 0 });
                using var response = await _http.SendAsync(unload, timeout.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }

            // LM Studio unloads by instance, and one model can have several; unload every instance of this one.
            using var read = Request(HttpMethod.Get, new Uri(root.Uri, "/api/v1/models"), apiKey);
            using var list = await _http.SendAsync(read, timeout.Token).ConfigureAwait(false);
            if (!list.IsSuccessStatusCode)
            {
                return false;
            }

            var instances = new List<string>();
            using (var document = await ReadJsonAsync(list, timeout.Token).ConfigureAwait(false))
            {
                foreach (var model in Array(document.RootElement, "models"))
                {
                    var key = Text(model, "key");
                    foreach (var instance in Array(model, "loaded_instances"))
                    {
                        if (Text(instance, "id") is { } id && (SameModel(key, modelId) || SameModel(id, modelId)))
                        {
                            instances.Add(id);
                        }
                    }
                }
            }

            var all = true;
            foreach (var id in instances)
            {
                using var unload = Request(
                    HttpMethod.Post, new Uri(root.Uri, "/api/v1/models/unload"), apiKey, new { instance_id = id });
                using var response = await _http.SendAsync(unload, timeout.Token).ConfigureAwait(false);
                all &= response.IsSuccessStatusCode;
            }

            return all;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<string?> LoadWithContextAsync(
        string endpoint, string modelId, int contextTokens, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (Root(endpoint) is not { App: LocalServerApp.LmStudio } root || string.IsNullOrWhiteSpace(modelId) ||
            contextTokens <= 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoadTimeout);
        try
        {
            // One word and a one-token answer, which LM Studio does not keep (store: false): the request exists to load the
            // model at this size, and the dictation's own requests then reach the instance it loaded.
            using var load = Request(HttpMethod.Post, new Uri(root.Uri, "/api/v1/chat"), apiKey, new
            {
                model = modelId.Trim(),
                input = "ok",
                max_output_tokens = 1,
                temperature = 0,
                context_length = contextTokens,
                store = false,
            });
            using var response = await _http.SendAsync(load, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            return Text(document.RootElement, "model_instance_id") ?? modelId.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<bool> UnloadInstanceAsync(
        string endpoint, string instanceId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (Root(endpoint) is not { App: LocalServerApp.LmStudio } root || string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UnloadTimeout);
        try
        {
            using var unload = Request(
                HttpMethod.Post, new Uri(root.Uri, "/api/v1/models/unload"), apiKey, new { instance_id = instanceId.Trim() });
            using var response = await _http.SendAsync(unload, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<int> ReadMaxContextAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (Root(endpoint) is not { App: LocalServerApp.Ollama } root || string.IsNullOrWhiteSpace(modelId))
        {
            return 0;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            using var show = Request(HttpMethod.Post, new Uri(root.Uri, "/api/show"), apiKey, new { model = modelId.Trim() });
            using var response = await _http.SendAsync(show, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            // The model's own figure, named for its architecture: "gemma4.context_length", "qwen3.context_length".
            using var document = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("model_info", out var info) || info.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (var property in info.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out var length) && length > 0)
                {
                    return (int)Math.Min(length, int.MaxValue);
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    /// <summary>
    /// True when two names are the same model to its app: equal ignoring case, or equal once Ollama's implicit
    /// <c>:latest</c> tag is added to a name without a tag.
    /// </summary>
    public static bool SameModel(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        static string Tagged(string name)
        {
            var trimmed = name.Trim();
            var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            return lastSegment.Contains(':', StringComparison.Ordinal) ? trimmed : trimmed + ":latest";
        }

        return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Tagged(first), Tagged(second), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LocalServerState> ReadOllamaAsync(Uri root, string? apiKey, CancellationToken ct)
    {
        var models = new List<LocalServerModel>();
        using (var tagsRequest = Request(HttpMethod.Get, new Uri(root, "/api/tags"), apiKey))
        using (var tags = await _http.SendAsync(tagsRequest, ct).ConfigureAwait(false))
        {
            if (Refused(tags))
            {
                return LocalServerState.NeedsKey;
            }

            if (!tags.IsSuccessStatusCode)
            {
                return LocalServerState.Failed;
            }

            using var document = await ReadJsonAsync(tags, ct).ConfigureAwait(false);
            foreach (var model in Array(document.RootElement, "models"))
            {
                if (Text(model, "name") is not { } name || !CanChat(model))
                {
                    continue;
                }

                models.Add(new LocalServerModel(name, name, Number(model, "size")));
            }
        }

        var loaded = new List<LocalServerLoadedModel>();
        using (var psRequest = Request(HttpMethod.Get, new Uri(root, "/api/ps"), apiKey))
        using (var ps = await _http.SendAsync(psRequest, ct).ConfigureAwait(false))
        {
            if (ps.IsSuccessStatusCode)
            {
                using var document = await ReadJsonAsync(ps, ct).ConfigureAwait(false);
                foreach (var model in Array(document.RootElement, "models"))
                {
                    if ((Text(model, "name") ?? Text(model, "model")) is { } name)
                    {
                        loaded.Add(new LocalServerLoadedModel(name, Number(model, "size"))
                        {
                            ContextTokens = Int(model, "context_length"),
                        });
                    }
                }
            }
        }

        return new LocalServerState(LocalServerReach.Reached, Sorted(models), loaded);
    }

    private async Task<LocalServerState> ReadLmStudioAsync(Uri root, string? apiKey, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, new Uri(root, "/api/v1/models"), apiKey);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (Refused(response))
        {
            return LocalServerState.NeedsKey;
        }

        if (!response.IsSuccessStatusCode)
        {
            return LocalServerState.Failed;
        }

        using var document = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var models = new List<LocalServerModel>();
        var loaded = new List<LocalServerLoadedModel>();
        foreach (var model in Array(document.RootElement, "models"))
        {
            if (!string.Equals(Text(model, "type"), "llm", StringComparison.OrdinalIgnoreCase) ||
                Text(model, "key") is not { } key)
            {
                continue;
            }

            var size = Number(model, "size_bytes");
            models.Add(new LocalServerModel(key, Text(model, "display_name") ?? key, size)
            {
                MaxContextTokens = Int(model, "max_context_length"),
            });
            var instances = Array(model, "loaded_instances").ToList();
            if (instances.Count > 0)
            {
                // Requests by the model's key reach its first instance.
                loaded.Add(Instance(key, size, instances[0]));
            }

            // An instance loaded under a name of its own (lms load --identifier) answers to that name as well.
            foreach (var instance in instances)
            {
                if (Text(instance, "id") is { } id && !SameModel(id, key))
                {
                    loaded.Add(Instance(id, size, instance));
                }
            }
        }

        return new LocalServerState(LocalServerReach.Reached, Sorted(models), loaded);
    }

    // One LM Studio instance as a loaded model: its context, its id and the time LM Studio still keeps it.
    private static LocalServerLoadedModel Instance(string name, long size, JsonElement instance) =>
        new(name, size)
        {
            ContextTokens = instance.ValueKind == JsonValueKind.Object && instance.TryGetProperty("config", out var config)
                ? Int(config, "context_length")
                : 0,
            InstanceId = Text(instance, "id"),
            RemainingTtlSeconds = instance.ValueKind == JsonValueKind.Object &&
                instance.TryGetProperty("remaining_ttl_seconds", out var ttl) &&
                ttl.ValueKind == JsonValueKind.Number && ttl.TryGetInt64(out var seconds)
                    ? seconds
                    : null,
        };

    // The key saved for this address, when there is one, as the bearer token a chat request carries.
    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string? apiKey, object? json = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        return request;
    }

    // The app asks for an API key it was not given, or refuses the one it was.
    private static bool Refused(HttpResponseMessage response) =>
        response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;

    private static IReadOnlyList<LocalServerModel> Sorted(List<LocalServerModel> models) =>
        [.. models.OrderBy(model => model.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.Ordinal)];

    // Ollama lists what each model can do; one without "completion" (an embedding model) cannot clean text. An older
    // Ollama that lists no capabilities keeps every model.
    private static bool CanChat(JsonElement model)
    {
        if (!model.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return capabilities.EnumerateArray().Any(capability =>
            capability.ValueKind == JsonValueKind.String &&
            string.Equals(capability.GetString(), "completion", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) &&
        number > 0
            ? number
            : 0;

    private static int Int(JsonElement element, string name) => (int)Math.Min(Number(element, name), int.MaxValue);

    private static (LocalServerApp App, Uri Uri)? Root(string endpoint)
    {
        var app = LocalAiServer.AppAt(endpoint);
        if (app == LocalServerApp.None || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return (app, new Uri(uri.GetLeftPart(UriPartial.Authority)));
    }
}
