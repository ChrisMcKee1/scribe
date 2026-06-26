using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

internal enum BenchGroup
{
    Cloud,
    Local,
}

/// <summary>
/// One model under test in the benchmark. For cloud models <see cref="Target"/> is the Azure
/// deployment name and <see cref="Endpoint"/> is its account host; for local models
/// <see cref="Target"/> is the Foundry Local alias and <see cref="Endpoint"/> is null.
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
