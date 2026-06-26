using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>True if the <c>az</c> command resolves on the current PATH.</summary>
    public bool IsInstalled()
    {
        try
        {
            var (exit, _, _) = RunAsync("where", "az", CancellationToken.None).GetAwaiter().GetResult();
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Installs the Azure CLI if absent, or upgrades it if present. Returns a user-facing message and
    /// whether the operation succeeded. Never throws — winget/elevation problems degrade to guidance.
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallOrUpdateAsync(CancellationToken ct = default)
    {
        var alreadyInstalled = IsInstalled();
        var verb = alreadyInstalled ? "upgrade" : "install";
        var args =
            $"{verb} --exact --id {PackageId} --silent --accept-package-agreements --accept-source-agreements";

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
        catch
        {
            return (false, $"winget isn't available. Install the Azure CLI manually from {ManualUrl}.");
        }

        // winget returns 0 on success. The "no applicable upgrade" code is success-equivalent here.
        const int NoApplicableUpgrade = unchecked((int)0x8A15002B);
        if (exit == 0)
        {
            return (true, alreadyInstalled ? "Azure CLI updated." : "Azure CLI installed. Run 'az login' to sign in.");
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

    private static async Task<(int Exit, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

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
}
