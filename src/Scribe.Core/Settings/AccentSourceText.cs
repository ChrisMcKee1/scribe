namespace Scribe.Core.Settings;

/// <summary>
/// The Advanced page's Appearance setting, "Use my Windows accent color" (plan 8.9 row 10), as the page shows it. The
/// setting colors every Scribe window (Settings, Add to dictionary, Welcome, the dictionary cleanup review) and the tray
/// menu; the recording indicator and the tray icon keep Scribe's own colors, which in a contrast theme are the system's.
/// </summary>
public static class AccentSourceText
{
    public const string Title = "Use my Windows accent color";

    public const string ContrastThemeSentence = "While a contrast theme is on, Windows chooses every color.";

    private const string BaseDescription =
        "Scribe's windows and the tray menu use your Windows accent color instead of Scribe blue. " +
        "The recording indicator and the tray icon keep Scribe's colors.";

    /// <summary>The description under the check box, with the contrast theme sentence while one is on.</summary>
    public static string Description(bool contrastTheme) =>
        contrastTheme ? BaseDescription + " " + ContrastThemeSentence : BaseDescription;
}
