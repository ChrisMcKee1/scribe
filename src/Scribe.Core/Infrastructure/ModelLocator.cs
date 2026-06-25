namespace Scribe.Core.Infrastructure;

/// <summary>
/// Locates the model directory across the layouts Scribe supports, in priority order:
/// <list type="number">
///   <item>the <c>SCRIBE_MODELS_DIR</c> environment variable, if set;</item>
///   <item>a <c>models</c> folder next to the executable (installed / published layout);</item>
///   <item>a <c>models</c> folder in an ancestor of the executable (developer layout, where
///         the repo-root <c>models</c> sits above <c>bin\…\net10.0-windows</c>);</item>
///   <item>the per-user fallback <c>%LOCALAPPDATA%\Scribe\models</c>.</item>
/// </list>
/// The first candidate whose ASR files are all present wins. When none is complete, the
/// per-user fallback is returned so callers can surface a clear "models missing" message
/// and know where to place them.
/// </summary>
public sealed class ModelLocator(AppPaths paths)
{
    private const int MaxAncestorLevels = 6;

    public IReadOnlyList<string> CandidateDirectories()
    {
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("SCRIBE_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "models"));

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < MaxAncestorLevels && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            candidates.Add(Path.Combine(dir.FullName, "models"));
        }

        candidates.Add(paths.ModelsDir);

        return candidates
            .Select(p => Path.GetFullPath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Returns the first complete model set, or the per-user fallback if none exists.</summary>
    public ModelSet Resolve()
    {
        foreach (var candidate in CandidateDirectories())
        {
            var set = ModelSet.ForDirectory(candidate);
            if (set.AsrComplete)
                return set;
        }

        return ModelSet.ForDirectory(paths.ModelsDir);
    }

    /// <summary>Resolves and verifies, throwing a descriptive error when files are missing.</summary>
    public ModelSet ResolveOrThrow()
    {
        var set = Resolve();
        if (!set.AsrComplete)
        {
            var missing = string.Join(", ", set.MissingAsrFiles());
            throw new FileNotFoundException(
                $"Speech model files were not found. Missing: {missing}. " +
                $"Run scripts/Download-Models.ps1 or place the model under '{set.Directory}'.");
        }

        return set;
    }
}
