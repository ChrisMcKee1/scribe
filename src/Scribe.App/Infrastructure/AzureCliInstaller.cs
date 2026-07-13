using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<AzureCliInstaller> _log;

    public AzureCliInstaller(ILogger<AzureCliInstaller> log) => _log = log;

    /// <summary>True if the <c>az</c> command resolves on the current PATH.</summary>
    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            var (exit, _, _) = await RunAsync("where", ["az"], ct).ConfigureAwait(false);
            return exit == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not determine whether Azure CLI is installed.");
            return false;
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
    /// Opens Azure CLI's browser sign-in and selects the first subscription returned for the chosen
    /// account. The CLI's terminal subscription selector is disabled so sign-in needs no console.
    /// </summary>
    public async Task<(bool Ok, string Message)> LoginAsync(
        string? tenantId = null, CancellationToken ct = default)
    {
        try
        {
            var loginArguments = new List<string> { "login", "--output", "none" };
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                loginArguments.Add("--tenant");
                loginArguments.Add(tenantId.Trim());
            }

            var (exit, stdout, stderr) = await RunAsync(
                "az",
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

            var (listExit, subscriptionOutput, listError) = await RunAsync(
                "az",
                ["account", "list", "--query", "[0].id", "--output", "tsv"],
                ct).ConfigureAwait(false);
            if (listExit != 0)
            {
                var detail = string.IsNullOrWhiteSpace(listError)
                    ? $"Azure CLI exited with code {listExit}"
                    : listError.Trim();
                return (false, $"Signed in, but couldn't list subscriptions ({Truncate(detail, 160)}).");
            }

            var subscriptionId = subscriptionOutput.Trim();
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                // Azure permits identities with tenant access but no subscriptions. Discovery will
                // report an empty deployment list, but the browser sign-in itself still succeeded.
                return (true, "Signed in to Azure.");
            }

            var (setExit, _, setError) = await RunAsync(
                "az",
                ["account", "set", "--subscription", subscriptionId],
                ct).ConfigureAwait(false);
            if (setExit != 0)
            {
                var detail = string.IsNullOrWhiteSpace(setError)
                    ? $"Azure CLI exited with code {setExit}"
                    : setError.Trim();
                return (false, $"Signed in, but couldn't select a subscription ({Truncate(detail, 160)}).");
            }

            return (true, "Signed in to Azure and selected the first available subscription.");
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
