using System.Runtime.InteropServices;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Chooses which <c>Scribe.Overlay.exe</c> build output to launch when several are present.
/// </summary>
/// <remarks>
/// The overlay is a separate process built per architecture (WinUI has no AnyCPU story), so a
/// developer who has built both <c>bin\x64\...</c> and <c>bin\ARM64\...</c> ends up with two
/// candidates on disk. Picking the first match alphabetically lands on ARM64 and Windows refuses
/// it with "The specified executable is not a valid application for this OS platform" (Win32 216),
/// which surfaces as a "Machine Type Mismatch" dialog and a pill that never appears. Selection is
/// therefore architecture-first: a mismatched binary is never returned, because launching it can
/// only fail.
/// </remarks>
public static class OverlayExecutableSelector
{
    /// <summary>
    /// Picks the best overlay executable for <paramref name="processArchitecture"/>, preferring one
    /// built in <paramref name="buildConfiguration"/>. Returns <c>null</c> when every candidate is
    /// for a different architecture.
    /// </summary>
    public static string? Select(
        IEnumerable<string> candidates,
        string buildConfiguration,
        Architecture processArchitecture)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Ordered so the choice is stable regardless of how the file system enumerated the tree.
        var ordered = candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matching = new List<string>();
        var unknown = new List<string>();

        foreach (var path in ordered)
        {
            var architecture = DetectArchitecture(path);
            if (architecture is null)
            {
                unknown.Add(path);
            }
            else if (architecture == processArchitecture)
            {
                matching.Add(path);
            }
        }

        // An architecture-less layout is only a guess, so it is the last resort after a real match.
        return PreferConfiguration(matching, buildConfiguration)
               ?? PreferConfiguration(unknown, buildConfiguration);
    }

    /// <summary>
    /// Infers the target architecture from the path, recognising both the MSBuild platform folder
    /// (<c>bin\ARM64\</c>) and the runtime identifier folder (<c>win-arm64\</c>). Returns
    /// <c>null</c> when no segment identifies one.
    /// </summary>
    public static Architecture? DetectArchitecture(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        // Walk from the executable outwards: the RID folder sits closest to the binary and is the
        // most authoritative when a platform folder higher up disagrees.
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var architecture = MapSegment(segments[i]);
            if (architecture is not null)
            {
                return architecture;
            }
        }

        return null;
    }

    private static Architecture? MapSegment(string segment) => segment.ToLowerInvariant() switch
    {
        "x64" or "win-x64" or "amd64" => Architecture.X64,
        "arm64" or "win-arm64" => Architecture.Arm64,
        "x86" or "win-x86" => Architecture.X86,
        _ => null,
    };

    private static string? PreferConfiguration(List<string> paths, string buildConfiguration)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(buildConfiguration))
        {
            var match = paths.Find(path => HasSegment(path, buildConfiguration));
            if (match is not null)
            {
                return match;
            }
        }

        return paths[0];
    }

    private static bool HasSegment(string path, string segment)
    {
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return Array.Exists(segments, s => string.Equals(s, segment, StringComparison.OrdinalIgnoreCase));
    }
}
