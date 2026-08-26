using Scribe.StyleEval.Runner;

namespace Scribe.StyleEval.Judge;

/// <summary>
/// The judge results file: one <see cref="JudgeCell"/> per line, appended as each verdict lands.
/// </summary>
/// <remarks>
/// Separate from the generation results file and resumable in exactly the same way. The judge pass
/// is opt-in and costs about as much as the run it grades, so losing it to a crash at cell nine
/// thousand would be the same expensive mistake twice.
/// </remarks>
internal sealed class JudgeStore(string path) : JsonlStore<JudgeCell>(path)
{
    /// <summary>Judged cell keys already in the file, so a restart pays for nothing twice.</summary>
    public static IReadOnlySet<string> LoadJudgedKeys(string path, out int malformedLines) =>
        LoadKeys(path, r => string.IsNullOrEmpty(r.ScenarioId) ? null : r.Key, out malformedLines);

    /// <summary>Reads every well-formed verdict back, for the cache and the report.</summary>
    public static IEnumerable<JudgeCell> ReadAll(string path) => ReadRows(path);

    /// <summary>
    /// The default path for the judge file, derived from the generation results path so the two
    /// always sit next to each other and a <c>--out</c> override carries through.
    /// </summary>
    public static string DefaultPathFor(string resultsPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(resultsPath)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(resultsPath);
        return Path.Combine(directory, name + ".judge.jsonl");
    }
}
