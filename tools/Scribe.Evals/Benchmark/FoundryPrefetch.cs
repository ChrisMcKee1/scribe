using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;

namespace Scribe.Evals.Benchmark;

/// <summary>
/// Downloads Foundry Local models without loading them, into the same folder the cleanup service reads
/// (<see cref="TextCleanupService.CreateFoundryConfiguration"/>), so a benchmark's load time measures a load and not a
/// multi-gigabyte download, and the GPU stays free for whatever is being timed meanwhile. A roster entry may be a
/// family alias (the variant Foundry Local picks for this PC) or an exact variant id, such as a CPU build.
/// </summary>
internal static class FoundryPrefetch
{
    public static async Task<int> RunAsync(IReadOnlyList<string> models, CancellationToken ct)
    {
        await using var service = new TextCleanupService(NullLogger<TextCleanupService>.Instance);
        var host = new FoundryLocalSdkHost();
        using var runtime = await host.CreateOrAttachAsync(
            service.CreateFoundryConfiguration(), NullLogger.Instance, ct).ConfigureAwait(false);

        // Registered before the first catalog read, as the service does, or the catalog lists CPU variants only.
        var providers = await runtime.DownloadAndRegisterEpsAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"Execution providers: {string.Join(", ", providers.RegisteredEps ?? [])}");
        var catalog = await runtime.GetCatalogAsync(ct).ConfigureAwait(false);
        var listed = await catalog.ListModelsAsync(ct).ConfigureAwait(false);

        var failures = 0;
        foreach (var spec in models)
        {
            var name = spec.StartsWith("foundry:", StringComparison.OrdinalIgnoreCase) ? spec["foundry:".Length..] : spec;
            try
            {
                var model = await catalog.GetModelAsync(name, ct).ConfigureAwait(false);
                if (model is null || string.IsNullOrWhiteSpace(model.Id))
                {
                    model = null;
                    foreach (var parent in listed)
                    {
                        var variant = parent.Variants.FirstOrDefault(v => string.Equals(v.Id, name, StringComparison.OrdinalIgnoreCase));
                        if (variant is not null)
                        {
                            parent.SelectVariant(variant);
                            model = parent;
                            break;
                        }
                    }
                }

                if (model is null)
                {
                    Console.WriteLine($"  {name}: not in the catalog.");
                    failures++;
                    continue;
                }

                if (await model.IsCachedAsync(ct).ConfigureAwait(false))
                {
                    Console.WriteLine($"  {name}: {model.Id} already downloaded.");
                    continue;
                }

                var started = DateTime.UtcNow;
                var last = -10;
                await model.DownloadAsync(progress =>
                {
                    var pct = (int)progress;
                    if (pct >= last + 10)
                    {
                        last = pct;
                        Console.WriteLine($"  {name}: {pct}%");
                    }
                }, ct).ConfigureAwait(false);
                Console.WriteLine($"  {name}: {model.Id} downloaded in {(DateTime.UtcNow - started).TotalSeconds:F0} s.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.WriteLine($"  {name}: failed ({ex.GetType().Name}: {ex.Message})");
                failures++;
            }
        }

        return failures;
    }
}
