using System;
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
/// dictation tray app must not restart out from under the user.
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

    /// <summary>
    /// Performs a best-effort update check. Any failure (offline, rate-limited, not packaged) is
    /// logged at debug and swallowed so it can never disrupt startup.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                // Plain dev/portable build — there is nothing to update against.
                return;
            }

            _manager = manager;
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                _log.LogInformation("Scribe is up to date ({Version}).", manager.CurrentVersion);
                return;
            }

            _log.LogInformation("Downloading Scribe update {Version}…", update.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);

            _pending = update;
            UpdateReady?.Invoke($"Scribe {update.TargetFullRelease.Version} is ready — it will install when you quit.");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Update check failed; continuing on the current version.");
        }
    }

    /// <summary>
    /// Applies a previously-downloaded update by launching the updater, which waits for this process
    /// to exit and then swaps in the new version. Called from the app's shutdown path.
    /// </summary>
    public void ApplyPendingOnExit()
    {
        if (_manager is null || _pending is null)
        {
            return;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_pending);
            _log.LogInformation("Staged Scribe update to apply after exit.");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not stage the update for apply-on-exit.");
        }
    }
}
