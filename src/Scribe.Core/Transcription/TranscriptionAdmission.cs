using Scribe.Core.Models;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Transcription;

/// <summary>
/// What one recording was admitted with that speech recognition needs beyond its audio: the settings the recording runs
/// on (which speech model, and for cloud speech the resource address and sign-in) and the vocabulary generation it was
/// admitted with (the words that may travel with the audio, and the library scope that permitted them). Both are taken
/// when the recording is admitted, by the rule cleanup and the dictionary pass already follow
/// (<see cref="VocabularyGeneration"/>), so a Save, a reset or a word pack's AI permission changing while the dictation
/// is processed moves neither where its audio may go nor which of the user's words may go with it.
/// </summary>
public sealed class TranscriptionAdmission
{
    public TranscriptionAdmission(AppSettings settings, VocabularyGeneration vocabulary)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(vocabulary);
        Settings = settings;
        Vocabulary = vocabulary;
    }

    /// <summary>The settings the recording was admitted with, with its trigger's overrides applied.</summary>
    public AppSettings Settings { get; }

    /// <summary>The vocabulary generation the recording was admitted with.</summary>
    public VocabularyGeneration Vocabulary { get; }
}
