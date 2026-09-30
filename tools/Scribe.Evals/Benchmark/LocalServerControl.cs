using System.Net.Http.Json;

namespace Scribe.Evals.Benchmark;

/// <summary>
/// Frees a local server's memory between benchmark models, so each model is measured alone on the
/// GPU and its load time is a real load rather than a model the server still held. Only the two
/// servers Settings names are recognized, by their default ports: Ollama's native
/// <c>/api/generate</c> with <c>keep_alive: 0</c> unloads at once, and LM Studio's
/// <c>/api/v1/models/unload</c> drops a JIT-loaded instance. Best effort: a failure is reported and
/// the run continues.
/// </summary>
internal static class LocalServerControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task UnloadAsync(BenchModel model, CancellationToken ct)
    {
        if (model.Provider != Scribe.Core.Cleanup.CleanupProvider.OpenAiCompatible ||
            !Uri.TryCreate(model.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return;
        }

        var root = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
        try
        {
            using var response = endpoint.Port switch
            {
                11434 => await Http.PostAsJsonAsync(
                    new Uri(root, "/api/generate"), new { model = model.Target, keep_alive = 0 }, ct).ConfigureAwait(false),
                1234 => await Http.PostAsJsonAsync(
                    new Uri(root, "/api/v1/models/unload"), new { instance_id = model.Target }, ct).ConfigureAwait(false),
                _ => null,
            };

            if (response is { IsSuccessStatusCode: false })
            {
                Console.WriteLine($"      (unload of {model.Target} answered {(int)response.StatusCode})");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"      (unload of {model.Target} failed: {ex.GetType().Name})");
        }
    }
}
