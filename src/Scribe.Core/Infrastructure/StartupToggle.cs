using Microsoft.Extensions.Logging;
using Scribe.Core.Persistence;
using Windows.ApplicationModel;

namespace Scribe.Core.Infrastructure;

/// <summary>
/// Decides what Scribe remembers as the user's Start with Windows preference.
/// </summary>
/// <remarks>
/// The saved preference drives the next launch's reconcile (<see cref="StartupRegistration.SyncAsync"/>),
/// so it must never contradict what the user just did: for the Run key a stale "off" removes the
/// entry at the next start, and for the package task a stale "on" re-enables a task the user turned
/// off in Scribe.
/// </remarks>
public static class StartupPreference
{
    /// <summary>
    /// Saves only the Start with Windows preference, as one atomic change of the stored settings,
    /// on a worker thread.
    /// </summary>
    /// <remarks>
    /// Atomic, because the tray's AI cleanup toggle rewrites the same document: a plain load, edit
    /// and save on either side could put back the other's stale copy, and a lost preference is
    /// undone by the next launch's reconcile. On a worker, because the write waits for the
    /// database whenever another write holds it, and that wait must not freeze the window. Only
    /// this field is written, so the window's other unsaved edits never ride along.
    /// </remarks>
    public static Task PersistAsync(ISettingsRepository settings, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Task.Run(() => { settings.Update(stored => stored.LaunchOnLogin = enabled); });
    }

    /// <summary>The preference to keep after the user asked Windows for a change.</summary>
    /// <remarks>
    /// A Windows-side user disable keeps what the user asked for. When they later turn Scribe on
    /// in Windows Settings, a saved "off" would otherwise have the next launch remove the Run entry
    /// they just enabled.
    /// </remarks>
    public static bool AfterRequest(bool requested, StartupRegistrationStatus result, bool saved) =>
        result.State switch
        {
            null => saved,
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => true,
            StartupTaskState.DisabledByUser => requested,
            _ => false,
        };

    /// <summary>The preference to keep after reading Windows without asking it for anything.</summary>
    /// <remarks>
    /// Adopting what Windows reports is what stops an untouched switch from undoing a change made
    /// in Windows Settings at the next launch. A Windows-side user disable and an unreadable state
    /// say nothing new about what the user wants from Scribe, so they keep the saved value.
    /// </remarks>
    public static bool Observed(StartupRegistrationStatus observed, bool saved) =>
        observed.State switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => true,
            StartupTaskState.Disabled or StartupTaskState.DisabledByPolicy => false,
            _ => saved,
        };
}

public enum StartupToggleOutcome
{
    /// <summary>Nothing to do: the switch already matches Windows, or Windows does not allow a change.</summary>
    Unchanged,

    /// <summary>Another change is still being applied; this one was not started.</summary>
    Busy,

    /// <summary>Windows now matches the request and the preference is saved.</summary>
    Applied,

    /// <summary>Windows refused, or its own setting blocks the request. The status says why.</summary>
    Declined,

    /// <summary>
    /// The Windows state could not be read back, so nothing is claimed either way, and the request
    /// stays saved.
    /// </summary>
    /// <remarks>
    /// The next launch's reconcile finishes a direct-download change in either direction and a Store
    /// enable. It never disables a Store startup task, so after a failed Store disable the task can
    /// stay on: the switch then shows that true state, and the next Save adopts it as the preference.
    /// </remarks>
    Unknown,

    /// <summary>The preference could not be saved, so Windows was not asked for anything.</summary>
    SaveFailed,
}

public sealed record StartupToggleResult(
    StartupRegistrationStatus Status,
    bool Preference,
    StartupToggleOutcome Outcome);

/// <summary>
/// Applies a Start with Windows change the moment the switch is flipped, the way Windows' own
/// toggles behave, and saves the preference with it.
/// </summary>
/// <remarks>
/// <para>
/// The switch used to be a pending edit that only a successful Save applied. Closing the window,
/// or a Save stopped by a problem on another page, silently dropped it, so Windows never changed.
/// </para>
/// <para>
/// The request is saved before Windows is asked. The next launch reconciles Windows with the saved
/// preference, so the other order would let a process exit between the two steps reverse a change
/// the user just made; this order lets the next launch finish it instead. The exception is a Store
/// disable: a launch never disables the package task (see <see cref="StartupRegistration.Plan"/>),
/// so an interrupted Store disable leaves the task on, the switch shows that, and the next Save
/// adopts it. A save that fails leaves Windows untouched. When Windows then refuses or blocks the
/// change, the preference is saved again as what Windows reports, so the next launch does not keep
/// retrying something the user was told did not happen.
/// </para>
/// </remarks>
public sealed class StartupToggle
{
    private readonly StartupRegistration _registration;
    private readonly Func<bool, Task> _savePreference;
    private readonly ILogger _log;
    private int _applying;

    public StartupToggle(StartupRegistration registration, Func<bool, Task> savePreference, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(savePreference);
        ArgumentNullException.ThrowIfNull(log);
        _registration = registration;
        _savePreference = savePreference;
        _log = log;
    }

    public bool IsApplying => Volatile.Read(ref _applying) != 0;

    /// <summary>
    /// Asks Windows for <paramref name="requested"/>, given the state the switch was showing and
    /// the preference currently saved. Never throws.
    /// </summary>
    public async Task<StartupToggleResult> ApplyAsync(
        bool requested,
        StartupRegistrationStatus shown,
        bool savedPreference)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
        {
            return new StartupToggleResult(shown, savedPreference, StartupToggleOutcome.Busy);
        }

        var saved = savedPreference;
        try
        {
            if (!shown.CanChange || shown.IsEnabled == requested)
            {
                return new StartupToggleResult(shown, saved, StartupToggleOutcome.Unchanged);
            }

            if (saved != requested)
            {
                try
                {
                    await _savePreference(requested);
                    saved = requested;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.LogWarning(ex, "Could not save the Start with Windows preference; Windows was left unchanged.");
                    return new StartupToggleResult(
                        shown with
                        {
                            Error = "Could not save the Start with Windows setting, so Windows was left as it was. Try again.",
                        },
                        saved,
                        StartupToggleOutcome.SaveFailed);
                }
            }

            var result = await _registration.SetEnabledAsync(requested);
            if (!result.IsKnown)
            {
                // Nothing can be claimed about Windows, so the request stays saved. See Unknown for
                // which directions the next launch can finish.
                return new StartupToggleResult(result, saved, StartupToggleOutcome.Unknown);
            }

            var preference = StartupPreference.AfterRequest(requested, result, saved);
            if (preference != saved)
            {
                try
                {
                    await _savePreference(preference);
                    saved = preference;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The request stays saved, and the status already tells the user what Windows
                    // did. A launch retries a direct-download change or a Store enable, never a
                    // Store disable.
                    _log.LogWarning(ex, "Could not save the Start with Windows preference Windows reported.");
                }
            }

            return new StartupToggleResult(
                result,
                saved,
                result.Matches(requested) ? StartupToggleOutcome.Applied : StartupToggleOutcome.Declined);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Applying the Start with Windows change failed.");
            return new StartupToggleResult(
                new StartupRegistrationStatus(null,
                    "Could not change the Windows startup setting. Reopen Settings to try again."),
                saved,
                StartupToggleOutcome.Unknown);
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }
}
