namespace Scribe.Core.Persistence;

/// <summary>
/// What the tray says after a startup repair of a damaged database, from what the repair actually brought back
/// (<see cref="ScribeDatabase.SettingsLostInRepair"/>, <see cref="ScribeDatabase.DictionaryLostInRepair"/>), so it
/// never claims settings or a dictionary came back when they did not.
/// </summary>
public static class DatabaseRepairNotice
{
    public const string Title = "Scribe repaired its data";

    private const string KeptCopy = "Scribe kept a copy of the damaged file.";

    /// <summary>The notice, and whether it reports a loss the user has to act on.</summary>
    public static (string Text, bool IsError) Compose(bool settingsLost, bool dictionaryLost) =>
        (settingsLost, dictionaryLost) switch
        {
            (false, false) => (
                "Your settings and dictionary were recovered. Some older dictations may be missing.",
                false),
            (true, false) => (
                "Your settings couldn't be recovered, so Scribe is using defaults. Open Settings to set them again. " + KeptCopy,
                true),
            (false, true) => (
                "Your settings were recovered, but your dictionary couldn't be. " + KeptCopy,
                true),
            (true, true) => (
                "Your settings and dictionary couldn't be recovered, so Scribe is using defaults. " +
                KeptCopy,
                true),
        };
}
