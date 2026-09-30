using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

/// <summary>
/// Ollama and LM Studio as a test scripts them: <see cref="State"/> is what every read answers (not running, unless a test
/// says otherwise), and every unload is recorded and answered true.
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

    public Task<LocalServerState> ReadAsync(string endpoint, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        lock (_unloads)
        {
            Reads++;
            _keys.Add(apiKey);
        }

        return Task.FromResult(State);
    }

    public Task<bool> UnloadAsync(
        string endpoint, string modelId, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        lock (_unloads)
        {
            _unloads.Add((endpoint, modelId));
            _keys.Add(apiKey);
        }

        Unloaded?.Invoke();
        return Task.FromResult(true);
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
}
