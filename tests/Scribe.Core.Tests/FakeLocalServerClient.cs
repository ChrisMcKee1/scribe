using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Ollama and LM Studio as a test scripts them: <see cref="State"/> is what every read answers (not running, unless a test
/// says otherwise), and every unload is recorded and answered true for a key the app takes (<see cref="KeyAccepted"/>). An
/// LM Studio model freed by name loses every copy, as LocalServerClient unloads each instance LM Studio lists.
/// </summary>
internal sealed class FakeLocalServerClient : ILocalServerClient
{
    private readonly List<(string Endpoint, string Model)> _unloads = [];
    private readonly List<string?> _keys = [];

    public LocalServerState State { get; set; } = LocalServerState.NotRunning;

    public int Reads { get; private set; }

    public IReadOnlyList<(string Endpoint, string Model)> Unloads
    {
        get
        {
            lock (_unloads)
            {
                return [.. _unloads];
            }
        }
    }

    /// <summary>The API key each read and unload was asked with, in order.</summary>
    public IReadOnlyList<string?> Keys
    {
        get
        {
            lock (_unloads)
            {
                return [.. _keys];
            }
        }
    }

    /// <summary>Completes each time an unload is asked for.</summary>
    public event Action? Unloaded;

    /// <summary>When set, each unload is recorded and then held until this completes, as a slow app would hold it.</summary>
    public TaskCompletionSource? UnloadGate { get; set; }

    /// <summary>When set, each read is counted and then held until this completes.</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    /// <summary>Completes each time a read is asked for.</summary>
    public event Action? ReadStarted;

    /// <summary>
    /// Which API keys the app takes; every key by default. A read with another answers <see cref="LocalServerState.NeedsKey"/>,
    /// and a load or an unload of an instance with another is refused.
    /// </summary>
    public Func<string?, bool> KeyAccepted { get; set; } = _ => true;

    public async Task<LocalServerState> ReadAsync(string endpoint, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        lock (_unloads)
        {
            Reads++;
            _keys.Add(apiKey);
        }

        ReadStarted?.Invoke();
        if (ReadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return KeyAccepted(apiKey) ? State : LocalServerState.NeedsKey;
    }

    public async Task<bool> UnloadAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        lock (_unloads)
        {
            _unloads.Add((endpoint, modelId));
            _operations.Add($"unload-model {modelId}");
            _keys.Add(apiKey);
        }

        Unloaded?.Invoke();
        if (UnloadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!KeyAccepted(apiKey))
        {
            return false;
        }

        // LM Studio frees every copy of the model (LocalServerClient unloads each instance it lists).
        if (LocalAiServer.AppAt(endpoint) == LocalServerApp.LmStudio)
        {
            lock (_unloads)
            {
                State = State with
                {
                    Loaded = [.. State.Loaded.Where(loaded => !LocalServerClient.SameModel(loaded.Id, modelId))],
                };
            }
        }

        return true;
    }

    /// <summary>Waits until at least <paramref name="count"/> unloads were asked for.</summary>
    public async Task WaitForUnloadsAsync(int count, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (Unloads.Count < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} unload(s), saw {Unloads.Count}.");
            }

            await Task.Delay(10);
        }
    }

    private readonly List<(string Endpoint, string Model, int ContextTokens)> _loads = [];
    private readonly List<(string Endpoint, string InstanceId)> _instanceUnloads = [];
    private readonly List<string> _operations = [];

    /// <summary>Every load and unload asked for, in order: "load {model} {tokens}", "unload {instance}", "unload-model {model}".</summary>
    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (_unloads)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>Every load at a context size asked for, in order.</summary>
    public IReadOnlyList<(string Endpoint, string Model, int ContextTokens)> Loads
    {
        get
        {
            lock (_unloads)
            {
                return [.. _loads];
            }
        }
    }

    /// <summary>Every unload of one instance asked for, in order.</summary>
    public IReadOnlyList<(string Endpoint, string InstanceId)> InstanceUnloads
    {
        get
        {
            lock (_unloads)
            {
                return [.. _instanceUnloads];
            }
        }
    }

    /// <summary>
    /// What a load at a context size answers: the instance id it loaded, by default the model's own name, or null for a
    /// load that failed. When <see cref="StateAfterLoad"/> is set, a successful load makes it the state every read answers.
    /// </summary>
    public Func<string, int, string?> LoadAnswer { get; set; } = (model, _) => model;

    /// <summary>The state a successful load leaves, read after it; null leaves <see cref="State"/> as it is.</summary>
    public Func<string, int, LocalServerState>? StateAfterLoad { get; set; }

    /// <summary>When set, each load is recorded and then held until this completes, as a slow load would be.</summary>
    public TaskCompletionSource? LoadGate { get; set; }

    /// <summary>Completes each time a load at a context size is asked for.</summary>
    public event Action? LoadStarted;

    /// <summary>
    /// Whether LM Studio takes the unload of an instance; true by default. A taken unload removes that instance from
    /// <see cref="State"/>, as LM Studio stops listing it.
    /// </summary>
    public Func<string, bool> InstanceUnloadAnswer { get; set; } = _ => true;

    /// <summary>When set, each unload of an instance is recorded and then held until this completes.</summary>
    public TaskCompletionSource? InstanceUnloadGate { get; set; }

    /// <summary>What <see cref="ReadMaxContextAsync"/> answers; 0, the app not saying, by default.</summary>
    public int MaxContextTokens { get; set; }

    public async Task<string?> LoadWithContextAsync(
        string endpoint, string modelId, int contextTokens, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        lock (_unloads)
        {
            _loads.Add((endpoint, modelId, contextTokens));
            _operations.Add($"load {modelId} {contextTokens}");
            _keys.Add(apiKey);
        }

        LoadStarted?.Invoke();
        if (LoadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_unloads)
        {
            var instance = KeyAccepted(apiKey) ? LoadAnswer(modelId, contextTokens) : null;
            if (instance is not null && StateAfterLoad is { } after)
            {
                State = after(modelId, contextTokens);
            }

            return instance;
        }
    }

    public async Task<bool> UnloadInstanceAsync(
        string endpoint, string instanceId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        lock (_unloads)
        {
            _instanceUnloads.Add((endpoint, instanceId));
            _operations.Add($"unload {instanceId}");
            _keys.Add(apiKey);
        }

        Unloaded?.Invoke();
        if (InstanceUnloadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_unloads)
        {
            if (!KeyAccepted(apiKey) || !InstanceUnloadAnswer(instanceId))
            {
                return false;
            }

            State = State with
            {
                Loaded = [.. State.Loaded.Where(loaded => !string.Equals(loaded.InstanceId, instanceId, StringComparison.Ordinal))],
            };
            return true;
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> unloads of an instance were asked for.</summary>
    public async Task WaitForInstanceUnloadsAsync(int count, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (InstanceUnloads.Count < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} instance unload(s), saw {InstanceUnloads.Count}.");
            }

            await Task.Delay(10);
        }
    }

    public Task<int> ReadMaxContextAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(MaxContextTokens);
}
