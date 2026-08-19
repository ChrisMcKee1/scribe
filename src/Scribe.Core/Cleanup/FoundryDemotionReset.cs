using Microsoft.Extensions.Logging;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;

namespace Scribe.Core.Cleanup;

/// <summary>
/// Clears Foundry Local GPU demotion markers once, on the first launch after the runnable-variant
/// fix shipped.
/// <para>
/// A marker records "the GPU build of this model failed, use the CPU build instead", and it is
/// applied on every later launch. The trouble is that the code writing it demoted on ANY load
/// failure, and the most common load failure was not a broken GPU at all: Foundry Local would
/// auto-select a variant needing an execution provider the machine did not have (a CUDA build on a
/// PC with no CUDA), which fails deterministically. Scribe now picks a variant the PC can actually
/// run, so those markers describe a problem that no longer exists while still forcing cleanup onto
/// the CPU, invisibly and permanently.
/// </para>
/// <para>
/// Clearing is safe because the demotion is self-healing: a model whose GPU build genuinely does
/// fail is demoted again by the existing probe path, costing one slower startup rather than a
/// permanent loss of acceleration. It runs once, guarded by a flag, so a user whose GPU really is
/// broken is not re-probed on every launch.
/// </para>
/// </summary>
public static class FoundryDemotionReset
{
    internal const string FileName = "foundry-local-demotions.json";

    /// <summary>True when markers were actually cleared on this launch.</summary>
    public static bool Apply(ISettingsRepository settings, AppPaths paths, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            // A failed settings load reports every flag as unset, so proceeding would clear the
            // markers on every launch and keep undoing demotions the probe path had just relearned.
            if (settings.LastLoadFailed)
            {
                return false;
            }

            var current = settings.Load();
            if (current.HasResetFoundryDemotions)
            {
                return false;
            }

            var path = Path.Combine(paths.RootDir, FileName);
            var existed = File.Exists(path);
            if (existed)
            {
                File.Delete(path);
            }

            // Recorded even when no file existed, so a demotion the probe path writes later is not
            // wiped by a reset that had simply never had anything to do.
            current.HasResetFoundryDemotions = true;
            settings.Save(current);

            if (existed)
            {
                log?.LogInformation(
                    "Cleared saved Foundry Local GPU demotions so hardware acceleration is re-evaluated once.");
            }

            return existed;
        }
        catch (Exception ex)
        {
            // Never block startup for a best-effort cleanup. Leaving the flag unset simply retries
            // on the next launch.
            log?.LogDebug(ex, "Could not clear the Foundry Local demotion markers.");
            return false;
        }
    }
}
