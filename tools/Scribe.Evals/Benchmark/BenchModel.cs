using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

internal enum BenchGroup
{
    Cloud,
    Local,
}

/// <summary>
/// One model under test in the benchmark. For cloud models <see cref="Target"/> is the Azure
/// deployment name and <see cref="Endpoint"/> is its account host. For a Foundry Local model
/// <see cref="Target"/> is the catalog alias and <see cref="Endpoint"/> is null; for a local server
/// reached through the OpenAI-compatible provider (Ollama, LM Studio) <see cref="Target"/> is the
/// model name that server lists and <see cref="Endpoint"/> is its <c>/v1</c> address.
/// </summary>
internal sealed record BenchModel(
    BenchGroup Group,
    string Id,
    CleanupProvider Provider,
    string? Endpoint,
    string Target,
    string? ModelName,
    string? Note)
{
    /// <summary>Stable key used to de-dupe and to resume a partially completed run.</summary>
    public string Key => $"{Group}/{Id}";
}
