using Scribe.Core.Models;
using Scribe.Core.Persistence;

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
/// Puts into effect a change stored on its own, outside a settings save, such as the entry the Usage page's Add writes
/// to the dictionary. What goes live is the settings as stored, never the settings window's editing document. That
/// document holds every edit made since the last successful save, and a Save that fails leaves them all in it, a picked
/// AI cleanup provider among them: applying it would move later dictations, with the text and the vocabulary each one
/// sends, to that provider with no save behind the choice.
/// </summary>
public static class StoredSettingsReapply
{
    /// <summary>
    /// Reads the stored settings and applies them with <paramref name="applySettings"/>, which also rebuilds what reads
    /// the dictionary: the post-processor and the AI cleanup glossary. When the stored settings cannot be used, because
    /// the document is unreadable or a repair lost it (<see cref="ISettingsRepository.LastLoadFailed"/>), no settings
    /// are applied at all: the defaults standing in for them are no more the user's choice than the editing document is,
    /// and a session running on the user's own settings would lose them. Only <paramref name="reloadVocabulary"/> runs
    /// then, so the change still takes effect on the settings dictation already runs on. A read that throws applies
    /// nothing, and the exception propagates.
    /// </summary>
    public static StoredSettingsReapplied Reapply(
        ISettingsRepository repository, Action<AppSettings> applySettings, Action reloadVocabulary)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(applySettings);
        ArgumentNullException.ThrowIfNull(reloadVocabulary);

        var stored = repository.Load();
        if (repository.LastLoadFailed)
        {
            reloadVocabulary();
            return StoredSettingsReapplied.VocabularyOnly;
        }

        applySettings(stored);
        return StoredSettingsReapplied.StoredSettings;
    }
}
