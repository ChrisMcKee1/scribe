using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace Scribe.App.Infrastructure;

/// <summary>
/// Checks for and installs updates for a Microsoft Store install, so a Store user gets the same
/// "check for updates" affordance a direct-download user already has.
/// </summary>
/// <remarks>
/// Velopack owns updates for direct downloads and must stay out of the way here: two updaters
/// replacing the same files is how an install gets corrupted. This type is the Store-side
/// counterpart, and it is deliberately gated on the package being <em>Store signed</em> rather than
/// merely packaged. A sideloaded MSIX carries our own signature, and the Store APIs report it as
/// unknown (0x803F6107) or throw, so asking about it would only produce noise.
/// </remarks>
public sealed class StoreUpdateService
{
    private readonly ILogger<StoreUpdateService> _log;
    private StoreContext? _context;
    private IReadOnlyList<StorePackageUpdate>? _pending;

    public StoreUpdateService(ILogger<StoreUpdateService> log) => _log = log;

    /// <summary>True when this install came from the Store and can be updated through it.</summary>
    public static bool IsStoreInstall()
    {
        try
        {
            return Package.Current.SignatureKind == PackageSignatureKind.Store;
        }
        catch (Exception)
        {
            // No package identity at all (a direct-download install), which is not an error here.
            return false;
        }
    }

    /// <summary>True once <see cref="CheckAsync"/> has found at least one update.</summary>
    public bool HasPendingUpdate => _pending is { Count: > 0 };

    /// <summary>
    /// Asks the Store whether an update is waiting. Shows no UI, so the caller can render its own
    /// status before the user commits to anything. Never throws.
    /// </summary>
    /// <param name="ownerWindow">
    /// Handle of the window the Store should parent any later dialog to. Required: the Store APIs
    /// are single-window aware and fail with 0x80070578 ("invalid window handle") without it.
    /// </param>
    public async Task<bool> CheckAsync(nint ownerWindow)
    {
        var context = TryGetContext(ownerWindow);
        if (context is null)
        {
            return false;
        }

        try
        {
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            _pending = updates;
            if (updates.Count > 0)
            {
                _log.LogInformation("Microsoft Store reports {Count} package update(s) available.", updates.Count);
            }

            return updates.Count > 0;
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex))
        {
            // Sideloaded, offline, or the Store service is unavailable. Any of these mean "nothing
            // to offer", never a reason to fault the settings window.
            _log.LogDebug(ex, "Store update check was unavailable.");
            _pending = null;
            return false;
        }
    }

    /// <summary>
    /// Downloads and installs the updates found by <see cref="CheckAsync"/>. The Store shows its own
    /// consent and progress dialogs, and Windows may close Scribe to replace it. Never throws.
    /// </summary>
    public async Task<StoreUpdateOutcome> ApplyAsync(nint ownerWindow)
    {
        var updates = _pending;
        if (updates is null || updates.Count == 0)
        {
            return StoreUpdateOutcome.NothingToDo;
        }

        var context = TryGetContext(ownerWindow);
        if (context is null)
        {
            return StoreUpdateOutcome.Failed;
        }

        try
        {
            _log.LogInformation("Installing {Count} Microsoft Store package update(s).", updates.Count);
            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            return result.OverallState switch
            {
                StorePackageUpdateState.Completed => StoreUpdateOutcome.Completed,
                StorePackageUpdateState.Canceled => StoreUpdateOutcome.Canceled,
                _ => StoreUpdateOutcome.Failed,
            };
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex))
        {
            _log.LogWarning(ex, "Could not install the Microsoft Store update.");
            return StoreUpdateOutcome.Failed;
        }
    }

    // The Store context is per-window: it must be told which HWND owns the dialogs it raises.
    private StoreContext? TryGetContext(nint ownerWindow)
    {
        if (!IsStoreInstall())
        {
            return null;
        }

        try
        {
            var context = _context ??= StoreContext.GetDefault();
            if (ownerWindow != 0)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindow);
            }

            return context;
        }
        catch (Exception ex) when (IsExpectedStoreFailure(ex))
        {
            _log.LogDebug(ex, "Microsoft Store context was unavailable.");
            _context = null;
            return null;
        }
    }

    // Microsoft does not document what these APIs do for a packaged app the Store has no record of.
    // Reported behaviour in the wild spans an empty result, a COMException, and a
    // FileNotFoundException, and the Store service itself can simply be unreachable. Since every one
    // of those means the same thing to a user ("there is no Store update to offer"), the filter is
    // deliberately broad and only lets genuinely fatal conditions escape.
    private static bool IsExpectedStoreFailure(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException;
}

public enum StoreUpdateOutcome
{
    NothingToDo,
    Completed,
    Canceled,
    Failed,
}
