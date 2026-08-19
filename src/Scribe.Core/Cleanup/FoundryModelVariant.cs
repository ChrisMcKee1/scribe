namespace Scribe.Core.Cleanup;

public enum FoundryModelExecutionBuild
{
    Unknown = 0,
    Cpu = 1,
    Gpu = 2,
}

internal readonly record struct FoundryModelVariantCandidate(string Alias, string? ExecutionProvider);

/// <summary>
/// Alias-shape helpers for the GPU to CPU demotion.
/// <para>
/// Foundry Local reports a model's execution provider directly, and that is what the settings UI
/// reads (see <see cref="FoundryExecutionProviders"/>). These helpers exist for the one case where
/// that information is not trustworthy: the variant has just failed to load or failed its first
/// inference, so Scribe has to work out a CPU counterpart from the alias it was given. Prefer the
/// SDK's provider anywhere the model actually loaded.
/// </para>
/// </summary>
internal static class FoundryModelVariant
{
    private const string GenericGpuSuffix = "-generic-gpu";
    private const string GenericCpuSuffix = "-generic-cpu";
    private const string CudaGpuSuffix = "-cuda-gpu";
    private const string GpuSuffix = "-gpu";
    private const string CpuSuffix = "-cpu";
    private const string CpuExecutionProvider = "CPUExecutionProvider";

    public static FoundryModelExecutionBuild Classify(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return FoundryModelExecutionBuild.Unknown;
        }

        var trimmed = StripVersionSuffix(alias.Trim());
        if (trimmed.EndsWith(GpuSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return FoundryModelExecutionBuild.Gpu;
        }

        if (trimmed.EndsWith(CpuSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return FoundryModelExecutionBuild.Cpu;
        }

        return FoundryModelExecutionBuild.Unknown;
    }

    public static bool IsGpuAlias(string? alias) => Classify(alias) == FoundryModelExecutionBuild.Gpu;

    public static string? ResolveCpuCounterpartAlias(string? gpuAlias, IEnumerable<string> catalogAliases)
    {
        return ResolveCpuCounterpartAlias(
            gpuAlias,
            catalogAliases.Select(alias => new FoundryModelVariantCandidate(alias, null)));
    }

    public static string? ResolveCpuCounterpartAlias(
        string? gpuAlias,
        IEnumerable<FoundryModelVariantCandidate> catalogCandidates)
    {
        if (!IsGpuAlias(gpuAlias))
        {
            return null;
        }

        var trimmed = StripVersionSuffix(gpuAlias!.Trim());
        var catalog = catalogCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Alias))
            .GroupBy(candidate => StripVersionSuffix(candidate.Alias.Trim()), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CandidateCpuAliases(trimmed))
        {
            if (catalog.TryGetValue(candidate, out var matches))
            {
                return PreferCpuExecutionProvider(matches);
            }
        }

        return null;
    }

    public static bool IsCpuExecutionProvider(string? executionProvider) =>
        string.Equals(executionProvider?.Trim(), CpuExecutionProvider, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CandidateCpuAliases(string gpuAlias)
    {
        if (gpuAlias.EndsWith(GenericGpuSuffix, StringComparison.OrdinalIgnoreCase))
        {
            yield return gpuAlias[..^GenericGpuSuffix.Length] + GenericCpuSuffix;
        }

        if (gpuAlias.EndsWith(CudaGpuSuffix, StringComparison.OrdinalIgnoreCase))
        {
            yield return gpuAlias[..^CudaGpuSuffix.Length] + GenericCpuSuffix;
        }

        if (gpuAlias.EndsWith(GpuSuffix, StringComparison.OrdinalIgnoreCase))
        {
            yield return gpuAlias[..^GpuSuffix.Length] + CpuSuffix;
        }
    }

    private static string PreferCpuExecutionProvider(IReadOnlyList<FoundryModelVariantCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (IsCpuExecutionProvider(candidate.ExecutionProvider))
            {
                return candidate.Alias.Trim();
            }
        }

        return candidates[0].Alias.Trim();
    }

    private static string StripVersionSuffix(string alias)
    {
        var colon = alias.LastIndexOf(':');
        if (colon > 0 && colon < alias.Length - 1 && alias[(colon + 1)..].All(char.IsDigit))
        {
            return alias[..colon];
        }

        return alias;
    }
}
