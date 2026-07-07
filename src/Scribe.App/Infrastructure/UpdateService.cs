using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Checks GitHub Releases for a newer Scribe build and, if found, downloads it in the background and
/// stages it to apply when the user next quits. No-ops entirely for non-packaged (dev) builds, so it
/// is safe to call unconditionally on startup. Update application is never forced mid-session — a
/// dictation tray app must not restart out from under the user — but the settings UI can check on
/// demand and apply immediately via <see cref="ApplyNowAndRestart"/>.
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/ChrisMcKee1/scribe";

    private readonly ILogger<UpdateService> _log;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService(ILogger<UpdateService> log) => _log = log;

    /// <summary>Raised (once) with a user-facing message when an update has been staged.</summary>
    public event Action<string>? UpdateReady;

    /// <summary>The version of the binaries actually running (assembly version, no build metadata).</summary>
    public static string RunningVersion
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                return "unknown";
            }

            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    /// <summary>Version of the downloaded-and-staged update, when one is waiting; else null.</summary>
    public string? PendingVersion { get; private set; }

    /// <summary>
    /// Performs a best-effort update check. Any failure (offline, rate-limited, not packaged) is
    /// logged at debug and swallowed so it can never disrupt startup.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await CheckAndDownloadAsync(ct).ConfigureAwait(false);
            _log.LogInformation("Startup update check: {Status}", status);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Update check failed; continuing on the current version.");
        }
    }

    /// <summary>
    /// Checks for, and downloads, any newer release. Returns a user-facing status string, so the
    /// settings UI can call this directly from a "Check for updates" button. Never throws for the
    /// expected failure modes (not packaged, offline) — those come back as status text.
    /// </summary>
    public async Task<string> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        try
        {
            var manager = _manager ?? new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                // Plain dev/portable build — there is nothing to update against.
                return $"Running {RunningVersion} (dev build — updates apply to installed builds only).";
            }

            _manager = manager;

            // An update staged in a PREVIOUS session (or by a manual Update.exe run) is still
            // waiting for a restart. Without this, only a download from the current session ever
            // got applied on quit, and a staged update could sit ignored forever.
            var staged = manager.UpdatePendingRestart;
            if (staged is not null)
            {
                PendingVersion = staged.Version.ToString();
                var stagedMessage = $"Scribe {PendingVersion} is downloaded and ready — restart to install.";
                UpdateReady?.Invoke(stagedMessage);
                return stagedMessage;
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                // Binaries vs manifest mismatch is exactly the state a half-applied update leaves
                // behind; log it loudly because the checker will otherwise claim "up to date".
                var manifest = manager.CurrentVersion?.ToString() ?? "unknown";
                if (!string.Equals(manifest, RunningVersion, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning(
                        "Version mismatch: running binaries are {Running} but the install manifest says {Manifest}. " +
                        "A previous update may not have fully applied; the next release will supersede it.",
                        RunningVersion, manifest);
                }

                return $"Scribe {RunningVersion} is up to date.";
            }

            var target = update.TargetFullRelease.Version.ToString();
            _log.LogInformation("Downloading Scribe update {Version}…", target);
            await manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);

            _pending = update;
            PendingVersion = target;
            var message = $"Scribe {target} is downloaded and ready — restart to install.";
            UpdateReady?.Invoke(message);
            return message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed.");
            return "Couldn't check for updates — check your connection and try again.";
        }
    }

    /// <summary>
    /// Applies the staged update immediately: exits this process, swaps in the new version, and
    /// relaunches Scribe. Returns false when there is nothing staged (the caller should check
    /// first). Only returns at all on failure — success never comes back.
    /// </summary>
    public bool ApplyNowAndRestart()
    {
        var asset = _pending?.TargetFullRelease ?? _manager?.UpdatePendingRestart;
        if (_manager is null || asset is null)
        {
            return false;
        }

        try
        {
            _log.LogInformation("Applying Scribe update {Version} and restarting.", asset.Version);
            _manager.ApplyUpdatesAndRestart(asset);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not apply the update immediately.");
            return false;
        }
    }

    /// <summary>
    /// Applies a previously-downloaded update by launching the updater, which waits for this process
    /// to exit and then swaps in the new version. Called from the app's shutdown path. Also honors
    /// an update staged in an earlier session, which the pre-existing code silently dropped.
    /// </summary>
    public void ApplyPendingOnExit()
    {
        var asset = _pending?.TargetFullRelease ?? _manager?.UpdatePendingRestart;
        if (_manager is null || asset is null)
        {
            return;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(asset);
            _log.LogInformation("Staged Scribe update {Version} to apply after exit.", asset.Version);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not stage the update for apply-on-exit.");
        }
    }
}
