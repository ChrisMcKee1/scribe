using Microsoft.Extensions.Logging;
using Scribe.Core.Persistence;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Retires seed dictionary entries that earlier versions installed and should not have.
/// <para>
/// <see cref="IDictionaryRepository.SeedIfEmpty"/> only ever runs on an empty dictionary, so an
/// existing install keeps whatever it was first given. Without this, those users would go on
/// force-capitalizing ordinary words like "azure" and "parakeet" forever while new users received
/// the corrected seed.
/// </para>
/// <para>
/// Entries are <b>disabled, never deleted</b>, and only when the pattern and replacement still match
/// exactly what Scribe inserted, so an entry the user edited is left alone and anything retired can
/// be switched back on in the dictionary editor. The run-once flag is what stops a deliberate
/// re-enable being undone on the next launch.
/// </para>
/// </summary>
public static class SeedVocabularyRetirement
{
    /// <summary>Number of entries disabled, or 0 when the cleanup was already applied or skipped.</summary>
    public static int Apply(
        ISettingsRepository settings,
        IDictionaryRepository dictionary,
        IReadOnlyList<Models.DictionaryEntry> retired,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(retired);

        try
        {
            // A failed settings load reports every flag as unset, so proceeding would re-run the
            // cleanup on every launch and keep undoing the user's choice.
            if (settings.LastLoadFailed)
            {
                return 0;
            }

            var current = settings.Load();
            if (current.HasRetiredSeedVocabulary)
            {
                return 0;
            }

            var disabled = dictionary.DisableUnmodifiedEntries(retired);
            current.HasRetiredSeedVocabulary = true;
            settings.Save(current);

            if (disabled > 0)
            {
                log?.LogInformation(
                    "Disabled {Count} seed dictionary entries that replaced ordinary words. Re-enable any of them in Settings, Dictionary.",
                    disabled);
            }

            return disabled;
        }
        catch (Exception ex)
        {
            // Tidying the dictionary is never worth failing startup over. Leaving the flag unset
            // means the next launch retries, which is the right behaviour for a failed migration.
            log?.LogWarning(ex, "Could not retire the outdated seed dictionary entries.");
            return 0;
        }
    }
}
