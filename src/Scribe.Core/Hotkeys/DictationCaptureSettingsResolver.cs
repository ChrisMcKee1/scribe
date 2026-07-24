using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>Builds the effective settings snapshot for one hotkey-triggered capture.</summary>
public static class DictationCaptureSettingsResolver
{
    public static AppSettings Resolve(AppSettings settings, HotkeyTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var captureSettings = settings.Clone();
        if (trigger == HotkeyTrigger.DictationOnly)
        {
            captureSettings.EnableAiCleanup = false;
        }

        return captureSettings;
    }
}
