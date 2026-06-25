using Scribe.Core.Audio;
using Scribe.Core.Hotkeys;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;
using Scribe.Core.Vad;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Scribe.Core services into the host's DI container.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the core foundation services (paths + model resolution) and the offline
    /// transcription engine. Audio, VAD, hotkey, injection, persistence and post-processing
    /// services are layered on as they are introduced.
    /// </summary>
    public static IServiceCollection AddScribeCore(this IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var paths = new AppPaths();
            paths.EnsureCreated();
            return paths;
        });

        services.AddSingleton<ModelLocator>();

        services.AddOptions<TranscriptionOptions>();
        services.AddSingleton<ITranscriptionService, TranscriptionService>();

        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<ITextInjector, TextInjector>();
        services.AddSingleton<IVadService, VadService>();

        services.AddSingleton<ScribeDatabase>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IDictionaryRepository, DictionaryRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();

        services.AddSingleton<ITextPostProcessor, TextPostProcessor>();

        return services;
    }
}
