using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using FoundryConfiguration = Microsoft.AI.Foundry.Local.Configuration;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Creates, or attaches to, the process-wide Foundry Local manager.
/// </summary>
/// <remarks>
/// A pass-through over <see cref="FoundryLocalManager"/>, a process-wide singleton with a private
/// constructor. It adds no behaviour of its own; it exists so the lifetime rules around the
/// manager (execution providers registered before the first catalog read, one creator at a time,
/// no use after disposal, storage reclaimed only when nothing is loaded) can be tested without
/// loading the native Foundry Local runtime. <see cref="ICatalog"/> and <see cref="IModel"/> are
/// already interfaces in the SDK and are used directly.
/// </remarks>
internal interface IFoundryLocalHost
{
    /// <summary>
    /// True once a manager exists in this process. The SDK never clears its singleton, so this stays
    /// true for the rest of the process, including after the manager is disposed.
    /// </summary>
    bool IsManagerCreated { get; }

    /// <summary>Creates the manager, or attaches to the one that already exists.</summary>
    Task<IFoundryLocalRuntime> CreateOrAttachAsync(
        FoundryConfiguration configuration, ILogger logger, CancellationToken cancellationToken);
}

/// <summary>The manager operations Scribe uses, one to one with <see cref="FoundryLocalManager"/>.</summary>
internal interface IFoundryLocalRuntime : IDisposable
{
    /// <summary>Bound URLs once the web service is running, otherwise null.</summary>
    string[]? Urls { get; }

    EpInfo[] DiscoverEps();

    Task<EpDownloadResult> DownloadAndRegisterEpsAsync(CancellationToken cancellationToken);

    Task<ICatalog> GetCatalogAsync(CancellationToken cancellationToken);

    Task StartWebServiceAsync(CancellationToken cancellationToken);

    Task StopWebServiceAsync(CancellationToken cancellationToken);
}

/// <summary>The production host: the real Foundry Local SDK singleton.</summary>
internal sealed class FoundryLocalSdkHost : IFoundryLocalHost
{
    public bool IsManagerCreated => FoundryLocalManager.IsInitialized;

    public async Task<IFoundryLocalRuntime> CreateOrAttachAsync(
        FoundryConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        if (!FoundryLocalManager.IsInitialized)
        {
            try
            {
                await FoundryLocalManager.CreateAsync(configuration, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (FoundryLocalException) when (FoundryLocalManager.IsInitialized)
            {
                // Created by another caller in this process between the check and the call. The SDK
                // reports that as FoundryLocalException("already been created"), not as
                // InvalidOperationException, so a filter on the latter never matched it.
            }
        }

        return new SdkRuntime(FoundryLocalManager.Instance);
    }

    private sealed class SdkRuntime(FoundryLocalManager manager) : IFoundryLocalRuntime
    {
        public string[]? Urls => manager.Urls;

        public EpInfo[] DiscoverEps() => manager.DiscoverEps();

        public Task<EpDownloadResult> DownloadAndRegisterEpsAsync(CancellationToken cancellationToken) =>
            manager.DownloadAndRegisterEpsAsync(cancellationToken);

        public Task<ICatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
            manager.GetCatalogAsync(cancellationToken);

        public Task StartWebServiceAsync(CancellationToken cancellationToken) =>
            manager.StartWebServiceAsync(cancellationToken);

        public Task StopWebServiceAsync(CancellationToken cancellationToken) =>
            manager.StopWebServiceAsync(cancellationToken);

        public void Dispose() => manager.Dispose();
    }
}
