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
            // Read by ONNX Runtime when a model's CUDA kernels are built, so it is set before the runtime exists.
            if (FoundryRuntimeEnvironment.PreferPortableAttention(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable))
            {
                logger.LogDebug("Foundry Local: cuDNN attention is off for CUDA builds, as ONNX Runtime allows.");
            }

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

/// <summary>
/// Process settings the ONNX Runtime inside Foundry Local reads, applied before the runtime exists.
/// </summary>
/// <remarks>
/// On a graphics card of compute capability 9.0 or later (an RTX 50 series card, for example) ONNX Runtime prefers
/// cuDNN's attention kernel for GroupQueryAttention unless a kernel is chosen. With Foundry Local 2.1.0 on an RTX 5080
/// (0.5.2) that kernel failed every request to Qwen3 4B's CUDA build ("Non-zero status code returned while running
/// GroupQueryAttention node ... cudnn_flash_attention.cc"), and Gemma 4 E2B's first request took 16 s against 1.3 s
/// without it, and later ones 0.57 s against 0.5 s. ONNX Runtime documents ORT_ENABLE_CUDNN_FLASH_ATTENTION=0 as turning
/// that kernel off, preference included, so its own Flash Attention and XQA kernels run instead
/// (onnxruntime docs/contrib_ops/cuda/gqa.md, "Selecting a Kernel"). A card older than 9.0 never prefers cuDNN, so it
/// changes nothing there, and a value the user set is left alone.
/// </remarks>
internal static class FoundryRuntimeEnvironment
{
    public const string CudnnAttentionVariable = "ORT_ENABLE_CUDNN_FLASH_ATTENTION";

    /// <summary>Turns cuDNN attention off unless the variable is already set. True when it set it.</summary>
    public static bool PreferPortableAttention(Func<string, string?> read, Action<string, string?> write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        if (read(CudnnAttentionVariable) is not null)
        {
            return false;
        }

        write(CudnnAttentionVariable, "0");
        return true;
    }
}