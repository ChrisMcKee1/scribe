using Scribe.Core.Models;

namespace Scribe.Core.Tray;

public enum TrayState
{
    Ready,
    Recording,
    Processing,
    Paused,
}

public enum TrayCondition
{
    None,
    DefaultSettings,
    NoSpeechModel,
    SpeechModelFailed,
    UpdateReady,
}

public static class TrayToolTip
{
    public const int MaxLength = 127;

    public static string Compose(TrayState state, string? shortcutName, HotkeyMode mode, TrayCondition condition = TrayCondition.None, string? version = null)
    {
        var shortcut = Shorten(string.IsNullOrWhiteSpace(shortcutName) ? "your shortcut" : shortcutName.Trim(), 40);
        var first = state switch
        {
            TrayState.Ready when mode == HotkeyMode.Toggle => $"Scribe: ready. Press {shortcut} to start and stop.",
            TrayState.Ready => $"Scribe: ready. Hold {shortcut} and speak.",
            TrayState.Recording => "Scribe: listening...",
            TrayState.Processing => "Scribe: writing your text...",
            TrayState.Paused => "Scribe: paused. Your shortcuts work as usual in other apps until you resume.",
            _ => "Scribe: ready.",
        };
        var line = ConditionLine(condition, version);
        return Shorten(line is null ? first : first + Environment.NewLine + line, MaxLength);
    }

    private static string? ConditionLine(TrayCondition condition, string? version) => condition switch
    {
        TrayCondition.DefaultSettings => "Using default settings. Open Settings to review them.",
        TrayCondition.NoSpeechModel => "No speech model is installed. Choose one in Settings.",
        TrayCondition.SpeechModelFailed => "The speech model didn't load. Scribe tries again when you dictate.",
        TrayCondition.UpdateReady => $"Scribe {version ?? string.Empty} is ready. Right-click for Restart to update.",
        _ => null,
    };

    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
