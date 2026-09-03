using System.Diagnostics;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Whether the GitHub Copilot CLI this provider runs on is present, and where.
/// </summary>
/// <param name="Found">True when an executable was located.</param>
/// <param name="Path">Full path to the executable, or null when not found.</param>
/// <param name="Version">Reported version, or null when it could not be read.</param>
public readonly record struct GitHubCopilotCliStatus(bool Found, string? Path, string? Version)
{
    /// <summary>Not installed, or not on PATH.</summary>
    public static GitHubCopilotCliStatus Missing { get; } = new(false, null, null);
}

/// <summary>
/// Locates the GitHub Copilot CLI.
/// </summary>
/// <remarks>
/// The Copilot provider is the one cleanup backend with a dependency the app cannot install for
/// itself: Agent Framework's <c>CopilotClient</c> drives an authenticated Copilot runtime, and on
/// Windows that is the CLI. Without this check the provider would look identical to a working one in
/// Settings and fail at the first dictation, which is the failure mode the whole probe design exists
/// to avoid. Detecting up front lets Settings offer an install button instead.
///
/// Deliberately not a package reference or a P/Invoke: this asks the same two questions a user would
/// ask at a prompt, in the same order the SDK resolves them.
/// </remarks>
public static class GitHubCopilotCli
{
    /// <summary>
    /// The SDK's own override. Documented for the Copilot integration as the path to the executable,
    /// so a user who installed somewhere unusual has already told us where.
    /// </summary>
    public const string PathVariable = "GITHUB_COPILOT_CLI_PATH";

    /// <summary>
    /// The SDK's model override. The Copilot backend exposes whatever models the signed-in account
    /// is licensed for, and it takes the choice through this variable rather than through an API,
    /// which is why the model is a free-text field in Settings rather than a discovered list.
    /// </summary>
    public const string ModelVariable = "GITHUB_COPILOT_MODEL";

    /// <summary>Executable name to look for on PATH when the override is unset.</summary>
    private const string ExecutableName = "copilot";

    /// <summary>How long to wait for `copilot --version`. Generous; it is a process start, not a call.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Finds the CLI, and reads its version when it can. Never throws: a detection failure is
    /// reported as "not found" so Settings can offer the install path rather than an error.
    /// </summary>
    public static GitHubCopilotCliStatus Detect()
    {
        try
        {
            var path = ResolvePath();
            if (path is null)
            {
                return GitHubCopilotCliStatus.Missing;
            }

            return new GitHubCopilotCliStatus(true, path, ReadVersion(path));
        }
        catch (Exception)
        {
            // Detection is best effort by contract. Anything thrown here (a permission error walking
            // PATH, a malformed environment variable) means we could not prove it is installed, and
            // "offer to install it" is the right answer to that.
            return GitHubCopilotCliStatus.Missing;
        }
    }

    /// <summary>The override first, then PATH, matching how the SDK resolves the runtime.</summary>
    private static string? ResolvePath()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim().Trim('"');
            return File.Exists(trimmed) ? trimmed : null;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        // PATHEXT rather than a hardcoded ".exe": on Windows the CLI is commonly installed by npm,
        // which writes a ".cmd" shim and no ".exe" at all, so an .exe-only search reports a working
        // install as missing.
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidateDirectory;
            try
            {
                candidateDirectory = directory.Trim().Trim('"');
                if (candidateDirectory.Length == 0)
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(candidateDirectory, ExecutableName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // A bare, extensionless executable, which is what a non-Windows install looks like.
            var bare = Path.Combine(candidateDirectory, ExecutableName);
            if (File.Exists(bare))
            {
                return bare;
            }
        }

        return null;
    }

    /// <summary>Reads `copilot --version`, or null when it does not answer.</summary>
    private static string? ReadVersion(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)VersionTimeout.TotalMilliseconds))
            {
                // A CLI that never answers is not one we can report a version for, and leaving the
                // process behind would leak a handle per Settings open.
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* best effort */ }
                return null;
            }

            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
