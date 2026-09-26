using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.Core.Settings;

public enum AdvancedSection
{
    SpeechRecognition,
    Recording,
    TypingIntoApps,
    TextChanges,
}

public static class AdvancedDefaults
{
    public static int ChangedCount(AdvancedSection section, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = AppSettings.CreateDefault();
        return section switch
        {
            AdvancedSection.SpeechRecognition =>
                Count(settings.TranscriptionModelId != TranscriptionModelCatalog.DefaultId) +
                Count(settings.DecodeThreads != defaults.DecodeThreads) +
                Count(settings.ReleaseModelsAfterIdleMinutes != defaults.ReleaseModelsAfterIdleMinutes),
            AdvancedSection.Recording =>
                Count(settings.UseVoiceActivityDetection != defaults.UseVoiceActivityDetection) +
                Count(settings.MaxDictationMinutes != defaults.MaxDictationMinutes),
            AdvancedSection.TypingIntoApps =>
                Count(settings.InjectionMethod != defaults.InjectionMethod) +
                Count(settings.NewlineHandling != defaults.NewlineHandling) +
                Count(settings.ShiftEnterLineBreaks != defaults.ShiftEnterLineBreaks),
            AdvancedSection.TextChanges => Count(settings.ApplyPostProcessing != defaults.ApplyPostProcessing),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
        };
    }

    public static AppSettings ApplyTo(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var defaults = AppSettings.CreateDefault();
        target.TranscriptionModelId = defaults.TranscriptionModelId;
        target.DecodeThreads = defaults.DecodeThreads;
        target.ReleaseModelsAfterIdleMinutes = defaults.ReleaseModelsAfterIdleMinutes;
        target.UseVoiceActivityDetection = defaults.UseVoiceActivityDetection;
        target.MaxDictationMinutes = defaults.MaxDictationMinutes;
        target.InjectionMethod = defaults.InjectionMethod;
        target.NewlineHandling = defaults.NewlineHandling;
        target.ShiftEnterLineBreaks = defaults.ShiftEnterLineBreaks;
        target.ApplyPostProcessing = defaults.ApplyPostProcessing;
        return target;
    }

    public static string SectionHeader(AdvancedSection section, AppSettings settings)
    {
        var count = ChangedCount(section, settings);
        return count == 0 ? string.Empty : $"{count} changed";
    }

    private static int Count(bool changed) => changed ? 1 : 0;
}
