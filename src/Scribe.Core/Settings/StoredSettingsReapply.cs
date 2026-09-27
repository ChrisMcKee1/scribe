using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Settings;

/// <summary>What <see cref="StoredSettingsReapply.Reapply"/> put into effect.</summary>
public enum StoredSettingsReapplied
{
    /// <summary>The settings as stored, which rebuild the post-processor and the AI cleanup glossary as they apply.</summary>
    StoredSettings,

    /// <summary>
    /// No settings, because none stored can be used: only the vocabulary was reloaded, on the settings dictation already
    /// runs on.
    /// </summary>
    VocabularyOnly,
}

/// <summary>
/// What <see cref="StoredSettingsReapply.Reapply"/> put into effect, and when dictation can use the vocabulary it stored.
/// </summary>
/// <param name="Reapplied">The settings as stored, or only the vocabulary.</param>
/// <param name="Vocabulary">
/// The answer of the vocabulary generation the application asked for: it completes once a generation built from the
/// dictionary as stored is what the next dictation is admitted with, or says it could not be built. A caller reports the
/// change as in effect only after awaiting it, and never waits on it synchronously.
/// </param>
public sealed record StoredSettingsReapplyResult(StoredSettingsReapplied Reapplied, Task<VocabularyRefresh> Vocabulary);

/// <summary>
/// Puts into effect a change stored on its own, outside a settings save, such as the entry the Usage page's Add writes
/// to the dictionary. What goes live is the settings as stored, never the settings window's editing document. That
/// document holds every edit made since the last successful save, and a Save that fails leaves them all in it, a picked
/// AI cleanup provider among them: applying it would move later dictations, with the text and the vocabulary each one
/// sends, to that provider with no save behind the choice.
/// </summary>
public static class StoredSettingsReapply
{
    /// <summary>
    /// Reads the stored settings and applies them with <paramref name="applySettings"/>, which also asks for a new
    /// vocabulary generation from what reads the dictionary: the post-processor and the AI cleanup glossary. When the
    /// stored settings cannot be used, because the document is unreadable or a repair lost it
    /// (<see cref="ISettingsRepository.LastLoadFailed"/>), no settings are applied at all: the defaults standing in for them
    /// are no more the user's choice than the editing document is, and a session running on the user's own settings would
    /// lose them. Only <paramref name="reloadVocabulary"/> runs then, so the change still takes effect on the settings
    /// dictation already runs on. Either callback returns the answer of the generation it asked for, which the result
    /// carries. The read and the application run on the caller's thread before this returns; a read that throws applies
    /// nothing, and the exception propagates.
    /// </summary>
    public static StoredSettingsReapplyResult Reapply(
        ISettingsRepository repository,
        Func<AppSettings, Task<VocabularyRefresh>> applySettings,
        Func<Task<VocabularyRefresh>> reloadVocabulary)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(applySettings);
        ArgumentNullException.ThrowIfNull(reloadVocabulary);

        var stored = repository.Load();
        if (repository.LastLoadFailed)
        {
            return new StoredSettingsReapplyResult(
                StoredSettingsReapplied.VocabularyOnly,
                reloadVocabulary() ?? throw new InvalidOperationException("The vocabulary reload returned no answer."));
        }

        return new StoredSettingsReapplyResult(
            StoredSettingsReapplied.StoredSettings,
            applySettings(stored) ?? throw new InvalidOperationException("Applying the stored settings returned no answer."));
    }
}
