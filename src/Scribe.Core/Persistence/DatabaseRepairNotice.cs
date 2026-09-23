namespace Scribe.Core.Persistence;

/// <summary>
/// What the tray says after a startup repair of a damaged database, from what the repair actually brought back
/// (<see cref="ScribeDatabase.SettingsLostInRepair"/>, <see cref="ScribeDatabase.DictionaryLostInRepair"/>), so it
/// never claims settings or a dictionary came back when they did not.
/// </summary>
public static class DatabaseRepairNotice
{
    private const string KeptCopy = "The damaged file was kept next to the database for manual recovery.";

    /// <summary>The notice, and whether it reports a loss the user has to act on.</summary>
    public static (string Text, bool IsError) Compose(bool settingsLost, bool dictionaryLost) =>
        (settingsLost, dictionaryLost) switch
        {
            (false, false) => (
                "Scribe repaired its database. Settings and dictionary were recovered; some history may be missing.",
                false),
            (true, false) => (
                "Scribe repaired its database, but your settings could not be recovered and were reset. " + KeptCopy,
                true),
            (false, true) => (
                "Scribe repaired its database. Your settings were recovered, but your dictionary could not be. " + KeptCopy,
                true),
            (true, true) => (
                "Scribe repaired its database, but your settings and dictionary could not be recovered and were reset. " +
                KeptCopy,
                true),
        };
}
