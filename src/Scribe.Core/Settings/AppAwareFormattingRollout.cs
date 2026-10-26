using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Persistence;

namespace Scribe.Core.Settings;

/// <summary>
/// Turns app-aware formatting on, once, for an install whose saved settings have it off. Builds before this one shipped
/// it off and saved that value in every document, so off cannot be told apart from a choice; the maintainer decided it is
/// on for everyone, because without it Markdown apps double the list markers of a dictation typed into them. The gate
/// (<see cref="Models.AppSettings.HasTurnedOnAppAwareFormatting"/>) keeps it to one start, so turning it off afterwards
/// stays off.
/// </summary>
public static class AppAwareFormattingRollout
{
    /// <summary>True when this start turned app-aware formatting on.</summary>
    public static bool Apply(ISettingsRepository settings, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            // Checked after this load, as FoundryDemotionReset does: defaults standing in for settings that could not be
            // used report the gate unset, and saving them would pass them off as the user's.
            var current = settings.Load();
            if (settings.LastLoadFailed || current.HasTurnedOnAppAwareFormatting)
            {
                return false;
            }

            var turnedOn = false;
            settings.Update(stored =>
            {
                if (stored.HasTurnedOnAppAwareFormatting)
                {
                    return;
                }

                turnedOn = !stored.AppAwareFormattingEnabled;
                stored.AppAwareFormattingEnabled = true;
                stored.HasTurnedOnAppAwareFormatting = true;
            });

            if (turnedOn)
            {
                log?.LogInformation("Turned app-aware formatting on, once, as the default for every install.");
            }

            return turnedOn;
        }
        catch (Exception ex)
        {
            // Never block startup for it. Leaving the gate unset tries again at the next start.
            log?.LogDebug("Could not turn app-aware formatting on ({Failure}).", FailureShape.Describe(ex));
            return false;
        }
    }
}
