namespace Scribe.StyleEval.Runner;

/// <summary>
/// The generation results file: one <see cref="CellResult"/> per line, appended as each cell lands.
/// </summary>
/// <remarks>
/// A full run is roughly ten thousand model calls. Buffering results until the end would mean a
/// transport failure at cell nine thousand throws away hours of paid work, so every cell is flushed
/// the moment it completes and the file is re-read on the next start to skip what is already there.
/// The mechanics live in <see cref="JsonlStore{T}"/>, which the judge pass uses as well; this type
/// exists to name the row type and the resume key.
/// </remarks>
internal sealed class ResultStore(string path) : JsonlStore<CellResult>(path)
{
    /// <summary>
    /// Cell keys already present in <paramref name="path"/> that carry a real answer. A truncated
    /// final line, which is what a crash mid-write leaves behind, is ignored rather than fatal.
    /// </summary>
    /// <remarks>
    /// A cell that recorded a transport or authentication error is deliberately NOT treated as
    /// complete, so a resume retries it. Counting a failure as done is how a run that lost its Azure
    /// token for a minute ends up with a permanent hole in the matrix that no later resume can fill,
    /// and the resulting pass rates would silently exclude those cells rather than reporting them.
    /// </remarks>
    public static IReadOnlySet<string> LoadCompletedKeys(string path, out int malformedLines) =>
        LoadKeys(
            path,
            r => string.IsNullOrEmpty(r.ScenarioId) || !string.IsNullOrEmpty(r.Error) ? null : r.Key,
            out malformedLines);

    /// <summary>Reads every well-formed result back, for the summary and the report.</summary>
    public static IEnumerable<CellResult> ReadAll(string path) => ReadRows(path);
}
