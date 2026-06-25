using Scribe.Core.Infrastructure;
using Scribe.Core.Transcription;

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

        return services;
    }
}
