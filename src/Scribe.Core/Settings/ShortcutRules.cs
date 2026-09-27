using Scribe.Core.Models;

namespace Scribe.Core.Settings;

public enum SilenceStopApplies
{
    None,
    Primary,
    Secondary,
    Both,
}

public sealed record SilenceStopState(bool IsEnabled, SilenceStopApplies Applies, string Description);

public static class ShortcutRules
{
    public const string SetShortcutFirst = "Set a shortcut first.";
    public const string SilenceStopDisabled = "Available when a shortcut is set to Press to start and stop.";
    public const string SilenceStopPrimary = "Scribe stops after a few seconds of silence. A noisy room can stop it early.";
    public const string SilenceStopSecondary = "When you use the shortcut without AI cleanup, Scribe stops after a few seconds of silence. A noisy room can stop it early.";

    public static SilenceStopState SilenceStop(HotkeyMode primary, HotkeyMode? secondary)
    {
        var primaryToggle = primary == HotkeyMode.Toggle;
        var secondaryToggle = secondary == HotkeyMode.Toggle;
        var applies = (primaryToggle, secondaryToggle) switch
        {
            (true, true) => SilenceStopApplies.Both,
            (true, false) => SilenceStopApplies.Primary,
            (false, true) => SilenceStopApplies.Secondary,
            _ => SilenceStopApplies.None,
        };

        return new SilenceStopState(
            applies != SilenceStopApplies.None,
            applies,
            applies == SilenceStopApplies.Secondary ? SilenceStopSecondary :
            applies == SilenceStopApplies.None ? SilenceStopDisabled : SilenceStopPrimary);
    }

    public static string SecondShortcutModeDescription(bool hasShortcut) =>
        hasShortcut ? string.Empty : SetShortcutFirst;
}
