namespace Scribe.Core.Settings;

/// <summary>The close guard surface that should run next before Scribe exits or restarts.</summary>
public enum AppExitCloseStep
{
    /// <summary>No more close guard work remains.</summary>
    Proceed,

    /// <summary>Ask the Settings window first.</summary>
    Settings,

    /// <summary>Ask Add to dictionary after Settings has either closed or did not need a guard.</summary>
    QuickAdd,

    /// <summary>The exit or restart is canceled.</summary>
    Canceled,
}

/// <summary>The outcome of a guard prompt.</summary>
public enum AppExitCloseStepResult
{
    /// <summary>The user saved successfully.</summary>
    Saved,

    /// <summary>The user chose to discard the draft.</summary>
    Discarded,

    /// <summary>The user chose Keep editing.</summary>
    KeepEditing,

    /// <summary>The save failed or was not confirmed.</summary>
    SaveFailed,
}

/// <summary>Whether the two app-owned draft windows need a close guard.</summary>
public readonly record struct AppExitCloseState(bool SettingsUnsaved, bool QuickAddUnsaved);

/// <summary>
/// Orders the app-level close guards. Settings runs before Add to dictionary so a dictionary draft in Settings is
/// settled before the quick-add popup checks conflicts against it.
/// </summary>
public static class AppExitCloseGuard
{
    /// <summary>Choose the first guard step from the current app state.</summary>
    public static AppExitCloseStep First(AppExitCloseState state) =>
        state.SettingsUnsaved ? AppExitCloseStep.Settings
        : state.QuickAddUnsaved ? AppExitCloseStep.QuickAdd
        : AppExitCloseStep.Proceed;

    /// <summary>Choose the next guard step after one prompt has completed.</summary>
    public static AppExitCloseStep Next(AppExitCloseStep completed, AppExitCloseStepResult result, AppExitCloseState state)
    {
        if (result is AppExitCloseStepResult.KeepEditing or AppExitCloseStepResult.SaveFailed)
        {
            return AppExitCloseStep.Canceled;
        }

        return completed == AppExitCloseStep.Settings && state.QuickAddUnsaved
            ? AppExitCloseStep.QuickAdd
            : AppExitCloseStep.Proceed;
    }
}
