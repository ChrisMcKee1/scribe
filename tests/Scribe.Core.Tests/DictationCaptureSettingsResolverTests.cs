using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class DictationCaptureSettingsResolverTests
{
    [Fact]
    public void Resolve_StandardTrigger_PreservesAiCleanup()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;

        var captureSettings = DictationCaptureSettingsResolver.Resolve(
            settings, HotkeyTrigger.Standard);

        Assert.True(captureSettings.EnableAiCleanup);
        Assert.NotSame(settings, captureSettings);
    }

    [Fact]
    public void Resolve_DictationOnlyTrigger_DisablesAiCleanupWithoutChangingSavedSettings()
    {
        var settings = AppSettings.CreateDefault();
        settings.EnableAiCleanup = true;

        var captureSettings = DictationCaptureSettingsResolver.Resolve(
            settings, HotkeyTrigger.DictationOnly);

        Assert.False(captureSettings.EnableAiCleanup);
        Assert.True(settings.EnableAiCleanup);
    }
}
