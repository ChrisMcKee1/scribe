using Scribe.Core.Infrastructure;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Scribe.Core services into the host's DI container.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the core foundation services (paths + model resolution). Audio, transcription,
    /// VAD, hotkey, injection, persistence and post-processing services are layered on by
    /// their respective registration helpers as they are introduced.
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

        return services;
    }
}
