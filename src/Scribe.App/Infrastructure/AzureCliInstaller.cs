using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Scribe.Core.Cleanup;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Best-effort installer/updater for the Azure CLI via winget. The Azure ("Microsoft Foundry via your
/// Azure sign-in") cleanup path relies on <c>az login</c>, so this lets a user provision the CLI
/// without leaving Scribe. winget elevates via UAC when a machine-scope MSI needs it; if winget is
/// unavailable we fall back to pointing the user at the official MSI.
/// </summary>
public sealed class AzureCliInstaller
{
    private const string PackageId = "Microsoft.AzureCLI";
    private const string ManualUrl = "https://aka.ms/installazurecliwindows";
    private static readonly object PathSync = new();
    private static string? _resolvedAzureCliPath;
    private readonly ILogger<AzureCliInstaller> _log;

    public AzureCliInstaller(ILogger<AzureCliInstaller> log) => _log = log;

    /// <summary>True if Azure CLI resolves from PATH or a standard Windows install location.</summary>
    public Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var path = ResolveAzureCliPath();
            if (path is null)
            {
                return Task.FromResult(false);
            }

            EnsureAzureCliOnProcessPath(path);
            return Task.FromResult(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not determine whether Azure CLI is installed.");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Installs the Azure CLI if absent, or upgrades it if present. Returns a user-facing message and
    /// whether the operation succeeded. Never throws — winget/elevation problems degrade to guidance.
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallOrUpdateAsync(CancellationToken ct = default)
    {
        var alreadyInstalled = await IsInstalledAsync(ct).ConfigureAwait(false);
        var verb = alreadyInstalled ? "upgrade" : "install";
        string[] args =
        [
            verb,
            "--exact",
            "--id",
            PackageId,
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
        ];

        int exit;
        string stdout;
        string stderr;
        try
        {
            (exit, stdout, stderr) = await RunAsync("winget", args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not start winget for Azure CLI installation or update.");
            return (false, $"winget isn't available. Install the Azure CLI manually from {ManualUrl}.");
        }

        // winget returns 0 on success. The "no applicable upgrade" code is success-equivalent here.
        const int NoApplicableUpgrade = unchecked((int)0x8A15002B);
        if (exit == 0)
        {
            lock (PathSync)
            {
                _resolvedAzureCliPath = null;
            }

            if (ResolveAzureCliPath() is { } path)
            {
                EnsureAzureCliOnProcessPath(path);
            }

            return (true, alreadyInstalled
                ? "Azure CLI updated."
                : "Azure CLI installed. Choose Sign in & find models to continue.");
        }

        if (exit == NoApplicableUpgrade)
        {
            return (true, "Azure CLI is already up to date.");
        }

        var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
            : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
            : $"winget exited with code 0x{exit:X8}";
        return (false, $"Couldn't {verb} the Azure CLI ({Truncate(detail, 160)}). Try {ManualUrl}.");
    }

    /// <summary>
    /// Opens Azure CLI's browser sign-in. The CLI's terminal subscription selector is disabled so
    /// sign-in needs no console; Scribe scopes later operations without changing the CLI default.
    /// </summary>
    public async Task<(bool Ok, string Message)> LoginAsync(
        string? tenantId = null, CancellationToken ct = default)
    {
        try
        {
            var azureCliPath = ResolveAzureCliPath();
            if (azureCliPath is null)
            {
                return (false, "Azure CLI couldn't be found. Install it below, then try again.");
            }

            EnsureAzureCliOnProcessPath(azureCliPath);
            var loginArguments = new List<string> { "login", "--output", "none" };
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                if (!IsValidTenantIdentifier(tenantId))
                {
                    return (false, "Tenant ID must be a GUID or verified domain name.");
                }

                loginArguments.Add("--tenant");
                loginArguments.Add(tenantId.Trim());
            }

            var (exit, stdout, stderr) = await RunAsync(
                azureCliPath,
                loginArguments,
                ct,
                disableLoginSubscriptionSelector: true).ConfigureAwait(false);

            if (exit != 0)
            {
                var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                    : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                    : $"Azure CLI exited with code {exit}";
                return (false, $"Azure sign-in did not complete ({Truncate(detail, 160)}). Please try again.");
            }

            return (true, "Signed in to Azure.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryLog(ex, "Azure CLI sign-in failed.");
            return (false, "Couldn't start Azure sign-in. Make sure Azure CLI is installed, then try again.");
        }
    }

    /// <summary>Lists every enabled subscription cached by Azure CLI, including other tenants.</summary>
    public async Task<(bool Ok, IReadOnlyList<AzureSubscription> Subscriptions, string Message)>
        ListSubscriptionsAsync(CancellationToken ct = default)
    {
        try
        {
            var azureCliPath = ResolveAzureCliPath();
            if (azureCliPath is null)
            {
                return (false, Array.Empty<AzureSubscription>(), "Azure CLI couldn't be found.");
            }

            EnsureAzureCliOnProcessPath(azureCliPath);
            var (exit, stdout, stderr) = await RunAsync(
                azureCliPath,
                ["account", "list", "--all", "--output", "json"],
                ct).ConfigureAwait(false);
            if (exit != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"Azure CLI exited with code {exit}"
                    : stderr.Trim();
                return (false, Array.Empty<AzureSubscription>(),
                    $"Couldn't list Azure subscriptions ({Truncate(detail, 160)}).");
            }

            return (true, AzureCliAccountParser.ParseSubscriptions(stdout), string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not list Azure CLI subscriptions.");
            return (false, Array.Empty<AzureSubscription>(), "Couldn't list Azure subscriptions.");
        }
    }

    private static string? ResolveAzureCliPath()
    {
        lock (PathSync)
        {
            if (!string.IsNullOrWhiteSpace(_resolvedAzureCliPath) && File.Exists(_resolvedAzureCliPath))
            {
                return _resolvedAzureCliPath;
            }

            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPathDirectories(directories, Environment.GetEnvironmentVariable("PATH"));
            AddPathDirectories(directories, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPathDirectories(directories, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            AddDirectory(directories, Environment.GetEnvironmentVariable("ProgramFiles"),
                "Microsoft SDKs", "Azure", "CLI2", "wbin");
            AddDirectory(directories, Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                "Microsoft SDKs", "Azure", "CLI2", "wbin");
            AddDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links");

            foreach (var directory in directories)
            {
                foreach (var executableName in new[] { "az.cmd", "az.exe", "az.bat" })
                {
                    var candidate = Path.Combine(directory, executableName);
                    if (File.Exists(candidate))
                    {
                        _resolvedAzureCliPath = candidate;
                        return candidate;
                    }
                }
            }

            return null;
        }
    }

    private static void EnsureAzureCliOnProcessPath(string azureCliPath)
    {
        var directory = Path.GetDirectoryName(azureCliPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        lock (PathSync)
        {
            var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var entries = current.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!entries.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
            }
        }
    }

    private static void AddPathDirectories(ISet<string> directories, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                directories.Add(Environment.ExpandEnvironmentVariables(directory.Trim('"')));
            }
        }
    }

    private static void AddDirectory(ISet<string> directories, string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var segments = new string[parts.Length + 1];
        segments[0] = root;
        Array.Copy(parts, 0, segments, 1, parts.Length);
        directories.Add(Path.Combine(segments));
    }

    private static bool IsValidTenantIdentifier(string tenantId)
    {
        var value = tenantId.Trim();
        if (Guid.TryParse(value, out _))
        {
            return true;
        }

        return value.Length is > 0 and <= 253 &&
               value.Contains('.', StringComparison.Ordinal) &&
               value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
    }

    private static async Task<(int Exit, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        bool disableLoginSubscriptionSelector = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (disableLoginSubscriptionSelector)
        {
            psi.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off";
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's timeout (or an explicit cancel) fired. winget often spawns a separate
            // installer child, so terminate the whole tree — otherwise the install keeps running in
            // the background after we've reported a timeout, and a retry would overlap a live install.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    // Bounded reap so the OS releases the handles before we surface the cancellation.
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Already exited or the kill raced its shutdown — nothing left to clean up.
            }

            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private void TryLog(Exception ex, string message)
    {
        try
        {
            _log.LogWarning(ex, message);
        }
        catch
        {
            // Diagnostics must never disrupt Azure CLI setup or sign-in.
        }
    }
}
