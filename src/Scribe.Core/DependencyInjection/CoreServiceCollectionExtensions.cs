using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Hotkeys;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
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
        services.AddSingleton<ITranscriptionModelInstaller, TranscriptionModelInstaller>();

        // Decode thread count is user-configurable; pull it from persisted settings when the
        // recognizer first resolves its options (0 keeps the service's auto heuristic).
        services.AddOptions<TranscriptionOptions>()
            .Configure<ISettingsRepository>((options, settings) =>
            {
                var loaded = settings.Load();
                options.ModelId = loaded.TranscriptionModelId;
                options.NumThreads = loaded.DecodeThreads;
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

        // History commits in the background, in order, through the one concrete repository. IHistoryRepository is
        // that same repository behind a barrier that makes reads and maintenance wait for writes accepted before them.
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton(sp => new HistoryWriter(
            sp.GetRequiredService<HistoryRepository>(), sp.GetRequiredService<ILogger<HistoryWriter>>()));
        services.AddSingleton<IHistoryWriter>(sp => sp.GetRequiredService<HistoryWriter>());
        services.AddSingleton<IHistoryRepository>(sp => new OrderedHistoryRepository(
            sp.GetRequiredService<HistoryRepository>(),
            sp.GetRequiredService<HistoryWriter>(),
            sp.GetRequiredService<ILogger<OrderedHistoryRepository>>()));
        services.AddSingleton<ICleanupFailureLog, CleanupFailureLog>();

        // Retention runs against the concrete repository, never through an IHistoryRepository
        // wrapper: the database write gate is what serializes it with history writes. The library storage's retention
        // is one of its light steps, run with maintenance's own clock.
        services.AddSingleton<IHistoryMaintenance>(sp => sp.GetRequiredService<HistoryRepository>());
        services.AddSingleton(sp => new StorageMaintenance(
            sp.GetRequiredService<ScribeDatabase>(),
            sp.GetRequiredService<IHistoryMaintenance>(),
            sp.GetRequiredService<ICleanupFailureLog>(),
            sp.GetRequiredService<ILogger<StorageMaintenance>>(),
            TimeProvider.System,
            StorageMaintenanceOptions.Default,
            sp.GetRequiredService<DictionaryLibraryService>().Janitor));
        services.AddSingleton<LastTranscriptStore>();

        services.AddSingleton<ITextPostProcessor, TextPostProcessor>();

        // Dictionary libraries: the built-in embedded set plus any custom CSVs the user imports, and the journal that
        // stores their changes. The three pure parts (the library CSV codec, the built-in overlay, composition and
        // policy) are singletons the Libraries page shares with the service; one service stands behind all three of its
        // interfaces, and the database says whether a repair ran at this start, which the library state reads a missing
        // row by. The service reads for itself whether the stored document can be used (a session on defaults).
        services.AddSingleton<ILibraryCsvCodec>(LibraryCsvCodec.Instance);
        services.AddSingleton<IBuiltInLibraryOverlay>(BuiltInLibraryOverlay.Instance);
        services.AddSingleton<ILibraryComposer>(LibraryComposer.Instance);
        services.AddSingleton(sp =>
        {
            var database = sp.GetRequiredService<ScribeDatabase>();
            return new DictionaryLibraryService(
                sp.GetRequiredService<AppPaths>(),
                sp.GetRequiredService<ISettingsRepository>(),
                sp.GetRequiredService<ILogger<DictionaryLibraryService>>(),
                LibraryServiceParts.Default with
                {
                    Codec = sp.GetRequiredService<ILibraryCsvCodec>(),
                    Overlay = sp.GetRequiredService<IBuiltInLibraryOverlay>(),
                    Composer = sp.GetRequiredService<ILibraryComposer>(),
                    Context = () => new LibraryStateContext(
                        RunningOnDefaults: false, DatabaseRepaired: database.RepairedAtStartup, GenerationStored: false),
                });
        });
        services.AddSingleton<IDictionaryLibraryService>(sp => sp.GetRequiredService<DictionaryLibraryService>());
        services.AddSingleton<ILibraryCatalogStore>(sp => sp.GetRequiredService<DictionaryLibraryService>());
        services.AddSingleton<ILibraryVocabularySource>(sp => sp.GetRequiredService<DictionaryLibraryService>());

        // Optional AI cleanup (Foundry Local on-device, or a Microsoft Foundry deployment via the
        // user's Azure sign-in). Registered unconditionally; it stays inert until enabled in
        // settings, and degrades to raw text whenever it is not ready.
        services.AddSingleton<ITextCleanupService, TextCleanupService>();
        services.AddSingleton<IAzureFoundryDiscovery, AzureFoundryDiscovery>();

        return services;
    }
}
