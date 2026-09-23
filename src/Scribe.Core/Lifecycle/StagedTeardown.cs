namespace Scribe.Core.Lifecycle;

/// <summary>One named, independent step of a shutdown sequence.</summary>
/// <param name="Name">A fixed label for the log. Never user content.</param>
/// <param name="Run">The step itself.</param>
public readonly record struct TeardownStep(string Name, Action Run);

/// <summary>
/// Runs a fixed sequence of independent shutdown steps so that one failure never skips the rest.
/// </summary>
/// <remarks>
/// The app's exit path used to run every step inside a single try block, so the first throw silently skipped tray
/// disposal, the theme watcher, host shutdown and host disposal together. Steps still run strictly in the given order,
/// which callers rely on (the dictation controller must stop using the core services before the host disposes them),
/// and the failure callback is guarded too, because a logger that throws must not stop the teardown it describes.
/// </remarks>
public static class StagedTeardown
{
    /// <summary>
    /// Runs every step in order. A step that throws is reported through <paramref name="onFailure"/> and the next step
    /// runs anyway. Returns how many steps failed. Never throws for a step's failure.
    /// </summary>
    public static int Run(IReadOnlyList<TeardownStep> steps, Action<string, Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var failures = 0;
        foreach (var step in steps)
        {
            try
            {
                step.Run();
            }
            catch (Exception ex)
            {
                failures++;
                try
                {
                    onFailure?.Invoke(step.Name, ex);
                }
                catch
                {
                    // Reporting is best-effort; the remaining steps matter more than the report.
                }
            }
        }

        return failures;
    }
}
