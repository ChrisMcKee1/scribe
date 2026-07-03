using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
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

        // Decode thread count is user-configurable; pull it from persisted settings when the
        // recognizer first resolves its options (0 keeps the service's auto heuristic).
        services.AddOptions<TranscriptionOptions>()
            .Configure<ISettingsRepository>((options, settings) =>
            {
                var loaded = settings.Load();
                options.NumThreads = loaded.DecodeThreads;
                options.DecodingMethod = loaded.UseHighAccuracyDecoding
                    ? "modified_beam_search"
                    : "greedy_search";
            });
        services.AddSingleton<ITranscriptionService, TranscriptionService>();

        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<ITextInjector, TextInjector>();
        services.AddSingleton<IVadService, VadService>();

        services.AddSingleton<ScribeDatabase>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IDictionaryRepository, DictionaryRepository>();
        services.AddSingleton<ISnippetRepository, SnippetRepository>();
        services.AddSingleton<IHistoryRepository, HistoryRepository>();
        services.AddSingleton<ICleanupFailureLog, CleanupFailureLog>();

        services.AddSingleton<ITextPostProcessor, TextPostProcessor>();

        // Optional AI cleanup (Foundry Local on-device, or a Microsoft Foundry deployment via the
        // user's Azure sign-in). Registered unconditionally; it stays inert until enabled in
        // settings, and degrades to raw text whenever it is not ready.
        services.AddSingleton<ITextCleanupService, TextCleanupService>();
        services.AddSingleton<IAzureFoundryDiscovery, AzureFoundryDiscovery>();

        return services;
    }
}
