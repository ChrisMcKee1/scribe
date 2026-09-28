using System.Diagnostics;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// The one way the app and the overlay helper it launches append to the shared daily log (DATA-O-02,
/// <see cref="PerfFlags.AppendOnlyLog"/>).
/// </summary>
/// <remarks>
/// A pair must never run two ways: a <see cref="FileMode.Append"/> writer can still write over the other's append-only
/// lines. So the app decides, for each helper it launches, and the helper follows the launch argument
/// (<see cref="LaunchArgument"/>), never its own environment: append-only only when the flag is on and the helper is this
/// app's own build (both informational versions readable and equal). Otherwise both keep <see cref="FileMode.Append"/>.
/// The decision is applied to the app's writer before the helper starts; until the first launch there is no other writer,
/// so the app's writer starts the old way.
/// </remarks>
public sealed class AppendOnlyLogMode
{
    /// <summary>The overlay launch argument the pair's mode travels in.</summary>
    public const string LaunchArgument = AppendOnlyFile.LaunchArgument;

    private const string OverlayAssemblyFileName = "Scribe.Overlay.dll";

    private volatile bool _appendOnly;

    /// <param name="requested">Whether <see cref="PerfFlags.AppendOnlyLog"/> is on.</param>
    /// <param name="appVersion">The app's own informational version (<see cref="ReadVersion"/>).</param>
    public AppendOnlyLogMode(bool requested, string? appVersion)
    {
        Requested = requested;
        AppVersion = appVersion;
    }

    /// <summary>Whether <see cref="PerfFlags.AppendOnlyLog"/> asked for append-only writes.</summary>
    public bool Requested { get; }

    /// <summary>The app's own informational version, compared with each helper's.</summary>
    public string? AppVersion { get; }

    /// <summary>Whether the app's writer appends only, now. The log writer reads it before each write.</summary>
    public bool AppendOnly => _appendOnly;

    /// <summary>A mode that never changes, for tests and tools that write without an overlay.</summary>
    public static AppendOnlyLogMode Fixed(bool appendOnly) => new(appendOnly, null) { _appendOnly = appendOnly };

    /// <summary>
    /// Decides the pair's mode for a launch of the helper at <paramref name="overlayExecutable"/>, applies it to this app's
    /// writer, and returns whether the launch passes <see cref="LaunchArgument"/>. Called before the helper starts.
    /// </summary>
    public bool DecideForLaunch(string overlayExecutable)
    {
        var appendOnly = Decide(Requested, AppVersion, ReadOverlayVersion(overlayExecutable));
        _appendOnly = appendOnly;
        return appendOnly;
    }

    /// <summary>
    /// The rule: append-only only when requested and both builds' informational versions are known and equal. An
    /// unreadable version is a different build, so the pair keeps today's way.
    /// </summary>
    public static bool Decide(bool requested, string? appVersion, string? overlayVersion) =>
        requested &&
        !string.IsNullOrWhiteSpace(appVersion) &&
        string.Equals(appVersion, overlayVersion, StringComparison.Ordinal);

    /// <summary>The informational version of the overlay build beside <paramref name="overlayExecutable"/>, or null.</summary>
    public static string? ReadOverlayVersion(string? overlayExecutable)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(overlayExecutable) ? null : Path.GetDirectoryName(overlayExecutable);
            return directory is null ? null : ReadVersion(Path.Combine(directory, OverlayAssemblyFileName));
        }
        catch (Exception)
        {
            // A path that cannot be read is a build that cannot be compared: the pair keeps today's way.
            return null;
        }
    }

    /// <summary>The informational (product) version of the assembly file at <paramref name="assemblyPath"/>, or null.</summary>
    public static string? ReadVersion(string? assemblyPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
