using System.Collections;
using System.Diagnostics;
using System.Text;

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
    /// The model variable earlier versions of Scribe published on their own process around client
    /// startup. The model now travels as <c>SessionConfig.Model</c>, the typed SDK surface; this name
    /// survives only to scope the runtime's child environment (see <see cref="BuildRuntimeEnvironment(string?)"/>)
    /// so the CLI sees exactly what it saw before, and never an ambient value the user did not choose.
    /// Neither the pinned GitHub Copilot SDK nor Agent Framework reads it themselves.
    /// </summary>
    public const string ModelVariable = "GITHUB_COPILOT_MODEL";

    /// <summary>Executable name to look for on PATH when the override is unset.</summary>
    private const string ExecutableName = "copilot";

    /// <summary>
    /// One deadline for `copilot --version`: both output streams and the exit. Generous; it is a
    /// process start, not a call.
    /// </summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for a killed probe (and anything it started) to go away. Kill is
    /// asynchronous, so without a bound a descendant that survives it would keep detection waiting.
    /// </summary>
    private static readonly TimeSpan KillObservationTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Enough of standard output for the first line, which is all the version parse reads.</summary>
    internal const int MaxRetainedOutputChars = 1024;

    /// <summary>A version line longer than this is not a version; it is truncated for display.</summary>
    internal const int MaxVersionChars = 128;

    /// <summary>
    /// Finds the CLI, and reads its version when it can. Never throws: a detection failure is
    /// reported as "not found" so Settings can offer the install path rather than an error.
    /// </summary>
    /// <remarks>
    /// Blocking wrapper for callers that are already on a background thread (Settings runs it through
    /// <c>Task.Run</c>). Every await inside uses <c>ConfigureAwait(false)</c>, so the wait cannot
    /// deadlock on a UI context.
    /// </remarks>
    public static GitHubCopilotCliStatus Detect() => DetectAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Finds the CLI, and reads its version when it can. Never throws, including on cancellation:
    /// a found executable whose version could not be read (timed out, cancelled, or not a program at
    /// all) is still reported as found, with a null version.
    /// </summary>
    public static async Task<GitHubCopilotCliStatus> DetectAsync(CancellationToken cancellationToken = default)
    {
        string? path;
        try
        {
            path = ResolvePath();
        }
        catch (Exception)
        {
            // Detection is best effort by contract. Anything thrown here (a permission error walking
            // PATH, a malformed environment variable) means we could not prove it is installed, and
            // "offer to install it" is the right answer to that.
            return GitHubCopilotCliStatus.Missing;
        }

        if (path is null)
        {
            return GitHubCopilotCliStatus.Missing;
        }

        var version = await ReadVersionAsync(
                () => SystemVersionProbeProcess.Start(path),
                VersionTimeout,
                KillObservationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return new GitHubCopilotCliStatus(true, path, version);
    }

    /// <summary>
    /// The environment for the Copilot runtime's child process: this process's environment, minus
    /// <see cref="ModelVariable"/>, plus the selected model when there is one.
    /// </summary>
    /// <remarks>
    /// The SDK's <c>CopilotClientOptions.Environment</c> replaces the child's environment wholesale
    /// (it clears <c>ProcessStartInfo.Environment</c> and copies this in), so everything the CLI needs
    /// from the parent, PATH and any authentication variables included, has to be carried over. What
    /// this buys is that the parent process is never mutated: the previous approach set and restored
    /// a process-wide variable around startup, which any concurrent reader could observe and which a
    /// cancelled startup skipped restoring.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> BuildRuntimeEnvironment(string? model) =>
        BuildRuntimeEnvironment(Environment.GetEnvironmentVariables(), model);

    internal static IReadOnlyDictionary<string, string> BuildRuntimeEnvironment(IDictionary inherited, string? model)
    {
        ArgumentNullException.ThrowIfNull(inherited);

        // Windows environment names are case-insensitive; a case-sensitive map could carry both
        // "Path" and "PATH" and let the wrong one win in the child.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in inherited)
        {
            if (entry.Key is string key && key.Length > 0 && entry.Value is string value &&
                !string.Equals(key, ModelVariable, StringComparison.OrdinalIgnoreCase))
            {
                environment[key] = value;
            }
        }

        // A blank model means the account default, which is why the variable is removed rather than
        // written empty: an empty value is not the same question as an unset one.
        if (!string.IsNullOrWhiteSpace(model))
        {
            environment[ModelVariable] = model.Trim();
        }

        return environment;
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

    /// <summary>
    /// Runs one version probe to completion or to its deadline. Never throws.
    /// </summary>
    /// <remarks>
    /// Both output streams are drained concurrently, because the documented deadlock is exactly
    /// the old shape here: reading standard output to the end while nothing reads standard error
    /// lets a child that fills the error pipe block forever, and the exit wait never starts. One
    /// deadline covers the reads and the exit together. On the deadline or on cancellation the
    /// probe owns the process, so it kills the whole tree (cancelling <c>WaitForExitAsync</c> does
    /// not stop anything) and then waits for it to go away, but only for a bounded time.
    /// </remarks>
    internal static async Task<string?> ReadVersionAsync(
        Func<IVersionProbeProcess?> start,
        TimeSpan timeout,
        TimeSpan killObservationTimeout,
        CancellationToken cancellationToken)
    {
        IVersionProbeProcess? process = null;
        try
        {
            process = start();
            if (process is null)
            {
                return null;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            var output = DrainAsync(process.StandardOutput, MaxRetainedOutputChars, deadline.Token);
            var error = DrainAsync(process.StandardError, retainChars: 0, deadline.Token);
            var exit = process.WaitForExitAsync(deadline.Token);
            var completion = Task.WhenAll(output, error, exit);

            try
            {
                // WaitAsync rather than awaiting WhenAll directly: a pipe read that ignores the
                // token would otherwise hold this await open long after the deadline.
                await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Observe(completion);
                await StopAsync(process, output, error, killObservationTimeout).ConfigureAwait(false);
                return null;
            }

            return ParseVersion(await output.ConfigureAwait(false));
        }
        catch (Exception)
        {
            // Not a program (a stand-in file, a broken shim), or it could not be started at all. The
            // executable was still found, and that is what the caller reports.
            return null;
        }
        finally
        {
            try
            {
                process?.Dispose();
            }
            catch (Exception)
            {
                // Disposal of a probe must never turn into a detection failure.
            }
        }
    }

    /// <summary>
    /// Reads a stream to its end, keeping at most <paramref name="retainChars"/> characters and
    /// discarding the rest, so a child that writes without limit costs a fixed buffer rather than
    /// memory proportional to its output. Stops at cancellation and never throws.
    /// </summary>
    internal static async Task<string> DrainAsync(TextReader reader, int retainChars, CancellationToken cancellationToken)
    {
        var retained = new StringBuilder(Math.Clamp(retainChars, 0, 256));
        var buffer = new char[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var room = retainChars - retained.Length;
                if (room > 0)
                {
                    retained.Append(buffer, 0, Math.Min(room, read));
                }
            }
        }
        catch (Exception)
        {
            // Cancelled, or the pipe broke because the process was killed. What was kept is all the
            // version parse could have used anyway.
        }

        return retained.ToString();
    }

    /// <summary>The first non-blank line of the output, trimmed and bounded, or null.</summary>
    internal static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate => candidate.Length > 0);
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        return line.Length <= MaxVersionChars ? line : line[..MaxVersionChars];
    }

    private static async Task StopAsync(
        IVersionProbeProcess process, Task<string> output, Task<string> error, TimeSpan killObservationTimeout)
    {
        try
        {
            process.KillProcessTree();
        }
        catch (Exception)
        {
            // Already exited, exiting, or a descendant could not be terminated. The bounded wait
            // below still applies, and a probe that cannot be killed is simply abandoned.
        }

        Task exited;
        try
        {
            exited = process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            exited = Task.CompletedTask;
        }

        var stopped = Task.WhenAll(output, error, exited);
        try
        {
            await stopped.WaitAsync(killObservationTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out or faulted. Nothing more can be done for a process that outlives Kill, and
            // detection must not wait on it.
            Observe(stopped);
        }
    }

    // A task that may be abandoned must never surface later as an unobserved exception.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>The probe's view of a child process, so its deadline and kill path can be tested.</summary>
    internal interface IVersionProbeProcess : IDisposable
    {
        TextReader StandardOutput { get; }

        TextReader StandardError { get; }

        Task WaitForExitAsync(CancellationToken cancellationToken);

        /// <summary>Terminates the process and every descendant it started.</summary>
        void KillProcessTree();
    }

    private sealed class SystemVersionProbeProcess : IVersionProbeProcess
    {
        private readonly Process _process;
        private readonly TextReader _output;
        private readonly TextReader _error;

        private SystemVersionProbeProcess(Process process)
        {
            _process = process;
            _output = process.StandardOutput;
            _error = process.StandardError;
        }

        public static IVersionProbeProcess? Start(string path) => Start(path, "--version");

        // The arguments are the probe's own; this overload lets a test run a real child that prints
        // a version, without the Copilot CLI.
        internal static IVersionProbeProcess? Start(string path, string arguments)
        {
            var process = Process.Start(new ProcessStartInfo(path, arguments)
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

            try
            {
                return new SystemVersionProbeProcess(process);
            }
            catch (Exception)
            {
                process.Dispose();
                throw;
            }
        }

        public TextReader StandardOutput => _output;

        public TextReader StandardError => _error;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void KillProcessTree() => _process.Kill(entireProcessTree: true);

        // Process.Dispose leaves redirected readers the caller has touched to the caller ("If they are
        // referenced it is the user's responsibility to dispose of them", Process.Close), and this
        // probe always reads both, so each would otherwise hold a pipe handle until finalization.
        public void Dispose()
        {
            DisposeQuietly(_output);
            DisposeQuietly(_error);
            _process.Dispose();
        }

        private static void DisposeQuietly(IDisposable resource)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception)
            {
                // A reader over a pipe the killed child already broke; the handle is released either way.
            }
        }
    }

    /// <summary>Test-only: a real probe process over any executable and arguments.</summary>
    internal static IVersionProbeProcess? StartProbeProcessForTesting(string path, string arguments) =>
        SystemVersionProbeProcess.Start(path, arguments);
}
