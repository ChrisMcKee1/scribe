using Scribe.Core.PostProcessing;

namespace Scribe.Core.Vocabulary;

/// <summary>
/// The dictionary pass of the dictation being processed, over the vocabulary generation that dictation was admitted
/// with: <see cref="Use"/> names the generation, and <see cref="ProcessDetailed"/> applies its compiled rules (with the
/// post-processor's snippets), never the newest ones. A Save that publishes a newer generation while a dictation
/// processes therefore changes nothing that dictation writes (plan 3.8, R6).
/// </summary>
/// <remarks>
/// <para>
/// The dictation controller owns one, processes one dictation at a time, and calls <see cref="Use"/> on the processing
/// thread immediately before <see cref="ProcessDetailed"/>, so the pair belongs to one dictation. It exists so the
/// controller's post-processing call keeps the shape <c>_postProcessor.ProcessDetailed(recognized, result.Text)</c>
/// that DictationInsertionTests anchors the pipeline's order on; what does the work is
/// <see cref="ITextPostProcessor.ProcessDetailed(string, string?, CompiledDictionaryRules)"/>.
/// </para>
/// </remarks>
public sealed class DictationPostProcessor
{
    private readonly ITextPostProcessor _processor;
    private VocabularyGeneration? _generation;

    public DictationPostProcessor(ITextPostProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processor = processor;
    }

    /// <summary>The generation the next <see cref="ProcessDetailed"/> applies, or null before any dictation named one.</summary>
    public VocabularyGeneration? Generation => Volatile.Read(ref _generation);

    /// <summary>Makes <paramref name="generation"/>, the dictation's own, the one the next dictionary pass applies.</summary>
    public void Use(VocabularyGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        Volatile.Write(ref _generation, generation);
    }

    /// <summary>
    /// Post-processes with the rules of the generation <see cref="Use"/> named. Throws when none was named: applying
    /// anything else would be a vocabulary the dictation was never admitted with.
    /// </summary>
    public TextPostProcessingResult ProcessDetailed(string text, string? sourceText = null)
    {
        var generation = Generation ?? throw new InvalidOperationException(
            "No vocabulary generation was named for this dictation's post-processing.");
        return _processor.ProcessDetailed(text, sourceText, generation.Rules);
    }

    /// <summary>Rebuilds the post-processor's snippet rules; the dictionary rules come from generations.</summary>
    public void ReloadSnippets() => _processor.ReloadSnippets();
}
