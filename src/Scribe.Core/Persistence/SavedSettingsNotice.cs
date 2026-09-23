namespace Scribe.Core.Persistence;

/// <summary>
/// What Scribe says wherever it refuses a change because the settings in use are defaults standing in for the saved
/// ones (<see cref="ISettingsRepository.LastLoadFailed"/>): the stored document could not be read, or a repair lost
/// it. One wording, true in both cases, so the tray and the Settings window say the same thing.
/// </summary>
public static class SavedSettingsNotice
{
    // Follows "Scribe " in the Settings window and "Scribe: " in the tray, which prefixes every message it shows.
    private const string Problem = "couldn't use your saved settings.";

    /// <summary>The tray's note at startup.</summary>
    public const string AtStartup = Problem + " Open Settings, review them and save.";

    /// <summary>For the tray: why a change made there is refused.</summary>
    /// <param name="change">What the user tried to change, as the tray names it.</param>
    public static string FromTray(string change) => $"{Problem} Open Settings, review them and save before changing {change}.";

    /// <summary>For the Settings window, where the user already is: why a control will not change yet.</summary>
    /// <param name="change">What the user tried to change, as the window names it.</param>
    public static string InSettings(string change) => $"Scribe {Problem} Review them and save before changing {change}.";
}
