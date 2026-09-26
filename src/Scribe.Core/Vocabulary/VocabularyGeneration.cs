using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Vocabulary;

/// <summary>
/// Everything one dictation's vocabulary is made of, built from one committed library snapshot and one read of the
/// personal dictionary (plan 3.8, R6): the compiled local rules (the dictionary merged over
/// <see cref="LibraryVocabulary.Entries"/>), the glossary input (the dictionary composed with
/// <see cref="LibraryVocabulary.AiEntries"/>, the list and order the glossary hint takes too, 9.4) and the AI scope that
/// input was cut by. A dictation takes the current generation when it is admitted and uses that one for its cleanup
/// and its post-processing, so a Save, a reset or an import while it runs changes nothing it writes or sends.
/// </summary>
/// <remarks>
/// Immutable and complete: <see cref="VocabularyPublisher"/> builds one off the dispatcher and publishes it in one
/// write, so no dictation ever sees half of one or a mix of two. Built only by the publisher (and tests); the
/// constructor is internal.
/// </remarks>
public sealed class VocabularyGeneration
{
    internal VocabularyGeneration(
        long number,
        IReadOnlyList<DictionaryEntry> dictionary,
        LibraryVocabulary libraries,
        CompiledDictionaryRules rules)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfNegative(number);

        Number = number;
        Dictionary = [.. dictionary];
        Libraries = libraries;
        Rules = rules;
        GlossaryEntries = CleanupPrompt.ComposeVocabulary(Dictionary, libraries.AiEntries);
        Cleanup = new CleanupVocabulary(GlossaryEntries, libraries.AiScope);
    }

    /// <summary>No vocabulary: what a publisher offers before its first generation is built.</summary>
    public static VocabularyGeneration Empty { get; } = new(0, [], LibraryVocabulary.Empty, CompiledDictionaryRules.Empty);

    /// <summary>The publisher's sequence number for this generation; later generations have larger numbers.</summary>
    public long Number { get; }

    /// <summary>The enabled personal dictionary as it was read for this generation, in the order dictation reads it.</summary>
    public IReadOnlyList<DictionaryEntry> Dictionary { get; }

    /// <summary>The one committed library snapshot this generation was built from.</summary>
    public LibraryVocabulary Libraries { get; }

    /// <summary>The local rules: <see cref="Dictionary"/> merged over <see cref="LibraryVocabulary.Entries"/>, compiled.</summary>
    public CompiledDictionaryRules Rules { get; }

    /// <summary>
    /// The glossary input: <see cref="Dictionary"/> composed with <see cref="LibraryVocabulary.AiEntries"/>
    /// (<see cref="CleanupPrompt.ComposeVocabulary"/>), so a library kept from AI cleanup still applies locally and never
    /// reaches the glossary.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> GlossaryEntries { get; }

    /// <summary>The libraries <see cref="GlossaryEntries"/> may carry to AI cleanup, with the content each was permitted for.</summary>
    public AiVocabularyScope AiScope => Libraries.AiScope;

    /// <summary>What a cleanup request of a dictation admitted with this generation carries and is judged by.</summary>
    public CleanupVocabulary Cleanup { get; }
}
