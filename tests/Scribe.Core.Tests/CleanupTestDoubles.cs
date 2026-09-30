using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using OpenAI;
using Scribe.Core.Cleanup;
using Scribe.Core.Tests.CleanupLogging;
using FoundryConfiguration = Microsoft.AI.Foundry.Local.Configuration;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Scribe.Core.Tests;

/// <summary>An HTTP handler whose every response or failure is decided by the test.</summary>
internal sealed class ScriptedHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        return respond(request, cancellationToken);
    }

    /// <summary>A minimal, valid Chat Completions answer.</summary>
    public static HttpResponseMessage ChatCompletion(string content) => Json(HttpStatusCode.OK,
        "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"test\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}," +
        "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Puts <paramref name="handler"/> under every OpenAI client the service builds, with no retries.</summary>
    public static Action<OpenAIClientOptions> Install(HttpMessageHandler handler) => options =>
    {
        options.Transport = new HttpClientPipelineTransport(new HttpClient(handler));
        options.RetryPolicy = new ClientRetryPolicy(0);
    };
}

/// <summary>Shared cached/loaded state for a fake Foundry Local catalog, by variant id.</summary>
internal sealed class FakeFoundryState
{
    private readonly object _sync = new();
    private readonly HashSet<string> _cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _events = new();

    /// <summary>Where a fake model's files live, so reclaim can measure them. Null for no files.</summary>
    public string? ModelRoot { get; set; }

    public IReadOnlyList<string> Events => _events.ToArray();

    public void Record(string evt) => _events.Enqueue(evt);

    public bool IsCached(string id) { lock (_sync) { return _cached.Contains(id); } }

    public bool IsLoaded(string id) { lock (_sync) { return _loaded.Contains(id); } }

    public void SetCached(string id, bool cached) { lock (_sync) { _ = cached ? _cached.Add(id) : _cached.Remove(id); } }

    public void SetLoaded(string id, bool loaded) { lock (_sync) { _ = loaded ? _loaded.Add(id) : _loaded.Remove(id); } }

    public string[] CachedIds() { lock (_sync) { return [.. _cached]; } }

    public string[] LoadedIds() { lock (_sync) { return [.. _loaded]; } }
}

/// <summary>
/// A fake Foundry Local model: a family (alias) whose variants share the alias, the way the SDK
/// reports them. A variant answers to its own id.
/// </summary>
internal sealed class FakeFoundryModel : IModel
{
    private readonly FakeFoundryState _state;
    private readonly List<FakeFoundryModel> _variants = [];
    private readonly ModelInfo _info;
    private FakeFoundryModel _selected;

    // Whether Info is the selected variant's, as the SDK's Model reports it; set for FamilyOn only, so every other family
    // keeps the first variant's.
    private bool _infoFollowsSelection;

    private FakeFoundryModel(FakeFoundryState state, string id, string alias, string executionProvider)
    {
        _state = state;
        _info = new ModelInfo
        {
            Id = id,
            Name = id,
            Alias = alias,
            ProviderType = "test",
            Uri = "test://model",
            ModelType = "ONNX",
            Runtime = new Runtime { DeviceType = DeviceType.CPU, ExecutionProvider = executionProvider },
        };
        _selected = this;
    }

    public static FakeFoundryModel Family(FakeFoundryState state, string alias, params string[] variantIds)
    {
        var family = new FakeFoundryModel(state, variantIds[0], alias, "CPUExecutionProvider");
        foreach (var id in variantIds)
        {
            family._variants.Add(new FakeFoundryModel(state, id, alias, "CPUExecutionProvider"));
        }

        family._selected = family._variants[0];
        return family;
    }

    /// <summary>
    /// A family whose variants name their execution providers, in the order Foundry Local ranks them; the first is the
    /// SDK's choice when none is downloaded. Like the SDK's Model, its <see cref="Info"/> is the selected variant's.
    /// </summary>
    public static FakeFoundryModel FamilyOn(FakeFoundryState state, string alias, params (string Id, string Provider)[] variants)
    {
        var family = new FakeFoundryModel(state, variants[0].Id, alias, variants[0].Provider);
        foreach (var (id, provider) in variants)
        {
            family._variants.Add(new FakeFoundryModel(state, id, alias, provider));
        }

        family._selected = family._variants[0];
        family._infoFollowsSelection = true;
        return family;
    }

    public string Id => _variants.Count == 0 ? Info.Id : _selected.Info.Id;

    public string Alias => Info.Alias;

    public ModelInfo Info => _infoFollowsSelection ? _selected._info : _info;

    public IReadOnlyList<IModel> Variants => _variants.Count == 0 ? [this] : _variants;

    public IReadOnlyList<FakeFoundryModel> VariantModels => _variants.Count == 0 ? [this] : _variants;

    /// <summary>When set, <see cref="LoadAsync"/> waits for it, honouring cancellation unless told not to.</summary>
    public TaskCompletionSource? LoadGate { get; set; }

    /// <summary>When true, the gated load ignores cancellation, like an uncooperative native call.</summary>
    public bool LoadIgnoresCancellation { get; set; }

    /// <summary>Signalled when a load starts. Replace it to wait for a later load.</summary>
    public TaskCompletionSource LoadStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _loadCalls;

    /// <summary>How many loads were started, including ones that were cancelled or failed.</summary>
    public int LoadCalls => Volatile.Read(ref _loadCalls);

    public Task<bool> IsCachedAsync(CancellationToken? ct = null) => Task.FromResult(_state.IsCached(Id));

    public Task<bool> IsLoadedAsync(CancellationToken? ct = null) => Task.FromResult(_state.IsLoaded(Id));

    public async Task DownloadAsync(Action<float>? downloadProgress = null, CancellationToken? ct = null)
    {
        _state.Record("download:" + Id);
        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(ct ?? CancellationToken.None).ConfigureAwait(false);
        }

        if (DownloadFailure is { } failure)
        {
            throw failure;
        }

        downloadProgress?.Invoke(100f);
        _state.SetCached(Id, true);
    }

    /// <summary>When set, a download throws this and caches nothing, as one does while offline.</summary>
    public Exception? DownloadFailure { get; set; }

    /// <summary>When set, a download waits for it, honouring cancellation.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public Task<string> GetPathAsync(CancellationToken? ct = null) =>
        Task.FromResult(_state.ModelRoot is { } root ? Path.Combine(root, SafeName(Id)) : string.Empty);

    public async Task LoadAsync(CancellationToken? ct = null)
    {
        Interlocked.Increment(ref _loadCalls);
        LoadStarted.TrySetResult();
        if (LoadGate is { } gate)
        {
            await (LoadIgnoresCancellation ? gate.Task : gate.Task.WaitAsync(ct ?? CancellationToken.None))
                .ConfigureAwait(false);
        }

        if (LoadFailure is { } failure)
        {
            throw failure;
        }

        if (LoadFailureById?.Invoke(Id) is { } variantFailure)
        {
            throw variantFailure;
        }

        _state.Record("load:" + Id);
        _state.SetLoaded(Id, true);
    }

    /// <summary>When set, a load throws this.</summary>
    public Exception? LoadFailure { get; set; }

    /// <summary>When set, a load of the variant it names throws what it returns, so one build can fail where another loads.</summary>
    public Func<string, Exception?>? LoadFailureById { get; set; }

    public Task RemoveFromCacheAsync(CancellationToken? ct = null) => RemoveCoreAsync(ct ?? CancellationToken.None);

    /// <summary>When set, removal waits for it, honouring cancellation.</summary>
    public TaskCompletionSource? RemoveGate { get; set; }

    /// <summary>Signalled when a removal starts.</summary>
    public TaskCompletionSource RemoveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RemoveCoreAsync(CancellationToken ct)
    {
        RemoveStarted.TrySetResult();
        if (RemoveGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        _state.Record("remove:" + Id);
        _state.SetCached(Id, false);
        if (_state.ModelRoot is { } root)
        {
            var directory = Path.Combine(root, SafeName(Id));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public Task UnloadAsync(CancellationToken? ct = null)
    {
        _state.Record("unload:" + Id);
        _state.SetLoaded(Id, false);
        return Task.CompletedTask;
    }

    // The SDK's in-process OpenAI-style clients (obsolete since 2.0); Scribe talks to the local web service instead.
#pragma warning disable CS0618
    public Task<OpenAIChatClient> GetChatClientAsync(CancellationToken? ct = null) => throw new NotSupportedException();

    public Task<OpenAIAudioClient> GetAudioClientAsync(CancellationToken? ct = null) => throw new NotSupportedException();

    public Task<OpenAIEmbeddingClient> GetEmbeddingClientAsync(CancellationToken? ct = null) => throw new NotSupportedException();
#pragma warning restore CS0618

    public string? GetStringProperty(string key) => null;

    public long GetIntProperty(string key, long defaultValue) => defaultValue;

    public void SelectVariant(IModel variant) =>
        _selected = _variants.First(v => string.Equals(v.Info.Id, variant.Id, StringComparison.OrdinalIgnoreCase));

    public static string SafeName(string id) => id.Replace(':', '-');
}

/// <summary>A fake catalog over a fixed set of families.</summary>
internal sealed class FakeFoundryCatalog(FakeFoundryState state, IReadOnlyList<FakeFoundryModel> families) : ICatalog
{
    public string Name => "fake";

    private IEnumerable<FakeFoundryModel> AllVariants => families.SelectMany(f => f.VariantModels);

    public Task<List<IModel>> ListModelsAsync(CancellationToken? ct = null) =>
        Task.FromResult(families.Cast<IModel>().ToList());

    public Task<IModel?> GetModelAsync(string modelAlias, CancellationToken? ct = null) =>
        Task.FromResult<IModel?>(families.FirstOrDefault(f =>
            string.Equals(f.Alias, modelAlias, StringComparison.OrdinalIgnoreCase)));

    public Task<IModel?> GetModelVariantAsync(string modelId, CancellationToken? ct = null) =>
        Task.FromResult<IModel?>(AllVariants.FirstOrDefault(v =>
            string.Equals(v.Id, modelId, StringComparison.OrdinalIgnoreCase)));

    public Task<List<IModel>> GetCachedModelsAsync(CancellationToken? ct = null) =>
        Task.FromResult(AllVariants.Where(v => state.IsCached(v.Id)).Cast<IModel>().ToList());

    public Task<List<IModel>> GetLoadedModelsAsync(CancellationToken? ct = null) =>
        Task.FromResult(AllVariants.Where(v => state.IsLoaded(v.Id)).Cast<IModel>().ToList());

    public Task<IModel> GetLatestVersionAsync(IModel model, CancellationToken? ct = null) => Task.FromResult(model);

    public Task<List<IModel>> GetModelVersionsAsync(string modelAlias, string? variant = null, int limit = 0, CancellationToken? ct = null) =>
        throw new NotSupportedException();

    public Task<IModel> RegisterModelAsync(string path, string modelId, ModelInfoBuilder metadata, CancellationToken? ct = null) =>
        throw new NotSupportedException();

    public Task UnregisterModelAsync(string modelId, CancellationToken? ct = null) => throw new NotSupportedException();
}

/// <summary>
/// A fake Foundry Local runtime. Every call is recorded in <see cref="FakeFoundryState.Events"/> so a
/// test can assert ordering (execution providers before the first catalog read) and counts.
/// </summary>
internal sealed class FakeFoundryRuntime(FakeFoundryState state, ICatalog catalog) : IFoundryLocalRuntime
{
    private int _epRegistrations;
    private int _catalogReads;
    private int _disposals;
    private int _inFlight;
    private volatile bool _disposedWhileInUse;

    public string[]? Urls { get; private set; }

    /// <summary>The address the fake web service "binds". A distinctive port lets a test search the log for it.</summary>
    public string Url { get; set; } = "http://127.0.0.1:5999";

    /// <summary>When set, execution-provider registration throws this.</summary>
    public Exception? EpFailure { get; set; }

    /// <summary>When set, starting the web service throws this and binds nothing.</summary>
    public Exception? WebStartFailure { get; set; }

    /// <summary>When set, execution-provider registration waits for it, honouring cancellation.</summary>
    public TaskCompletionSource? EpGate { get; set; }

    /// <summary>When true, the gated registration ignores cancellation, like an uncooperative native call.</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>Signalled when execution-provider registration starts.</summary>
    public TaskCompletionSource EpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int EpRegistrations => Volatile.Read(ref _epRegistrations);

    public int CatalogReads => Volatile.Read(ref _catalogReads);

    public int Disposals => Volatile.Read(ref _disposals);

    /// <summary>True if <see cref="Dispose"/> ever ran while one of this runtime's calls was still active.</summary>
    public bool DisposedWhileInUse => _disposedWhileInUse;

    /// <summary>Execution providers this fake PC registers besides the CPU, as Foundry Local names them.</summary>
    public string[] ExtraEps { get; set; } = [];

    /// <summary>Execution providers whose download or registration fails, as one does while offline at a first start.</summary>
    public string[] FailedEps { get; set; } = [];

    public EpInfo[] DiscoverEps() =>
    [
        new EpInfo { Name = "CPUExecutionProvider", IsRegistered = true },
        .. ExtraEps.Select(name => new EpInfo { Name = name, IsRegistered = true }),
    ];

    public async Task<EpDownloadResult> DownloadAndRegisterEpsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            Interlocked.Increment(ref _epRegistrations);
            state.Record("register-eps");
            EpStarted.TrySetResult();
            if (EpGate is { } gate)
            {
                await (IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken)).ConfigureAwait(false);
            }

            if (EpFailure is { } failure)
            {
                throw failure;
            }

            return new EpDownloadResult
            {
                Success = FailedEps.Length == 0,
                Status = FailedEps.Length == 0 ? "Completed" : "Failed",
                RegisteredEps = [],
                FailedEps = FailedEps,
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Task<ICatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _catalogReads);
        state.Record("catalog");
        return Task.FromResult(catalog);
    }

    public Task StartWebServiceAsync(CancellationToken cancellationToken)
    {
        state.Record("start-web");
        if (WebStartFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        Urls = [Url];
        return Task.CompletedTask;
    }

    public Task StopWebServiceAsync(CancellationToken cancellationToken)
    {
        state.Record("stop-web");
        Urls = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _inFlight) > 0)
        {
            _disposedWhileInUse = true;
        }

        Interlocked.Increment(ref _disposals);
        state.Record("dispose-runtime");
    }
}

/// <summary>A fake host: creation can be gated, and every created runtime is kept for inspection.</summary>
internal sealed class FakeFoundryHost(Func<FakeFoundryRuntime> createRuntime) : IFoundryLocalHost
{
    private int _creations;

    public bool IsManagerCreated => Volatile.Read(ref _creations) > 0;

    public int Creations => Volatile.Read(ref _creations);

    /// <summary>When set, creation waits for it. Deliberately ignores cancellation when requested.</summary>
    public TaskCompletionSource? CreateGate { get; set; }

    public bool IgnoreCancellation { get; set; }

    public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<FakeFoundryRuntime> Created { get; } = [];

    /// <summary>Every configuration the service handed over, in order.</summary>
    public List<FoundryConfiguration> Configurations { get; } = [];

    /// <summary>The logger the service handed the SDK, which is what the real SDK logs through.</summary>
    public ILogger? SdkLogger { get; private set; }

    public async Task<IFoundryLocalRuntime> CreateOrAttachAsync(
        FoundryConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        lock (Configurations)
        {
            Configurations.Add(configuration);
            SdkLogger = logger;
        }

        CreateStarted.TrySetResult();
        if (CreateGate is { } gate)
        {
            await (IgnoreCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken)).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _creations);
        var runtime = createRuntime();
        lock (Created)
        {
            Created.Add(runtime);
        }

        return runtime;
    }
}

/// <summary>A self-deleting temporary directory.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scribe-s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, int bytes)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                // Links are skipped, so clearing attributes can never reach through a junction a test made.
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var file in Directory.EnumerateFiles(Path, "*", options))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { }
                }

                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort: a temp directory left behind is harmless.
        }
    }
}

/// <summary>One status as the cleanup service published it.</summary>
internal readonly record struct PublishedStatus(CleanupStatus Status, string? Detail);

/// <summary>
/// Every status a cleanup service publishes, read inside the event on the publishing thread. A publisher
/// that only starts after an earlier publication returns can never be recorded out of order.
/// </summary>
internal sealed class CleanupStatusRecorder
{
    private readonly ConcurrentQueue<PublishedStatus> _seen = new();

    public CleanupStatusRecorder(TextCleanupService service) =>
        service.StatusChanged += () => _seen.Enqueue(new PublishedStatus(service.Status, service.StatusDetail));

    public IReadOnlyList<PublishedStatus> Snapshot() => _seen.ToArray();
}

/// <summary>
/// Runs each report on the reporting thread at the moment of the report. Progress&lt;T&gt; would post it
/// elsewhere, and the orderings these tests force would be lost.
/// </summary>
internal sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
