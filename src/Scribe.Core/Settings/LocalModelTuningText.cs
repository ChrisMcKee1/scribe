using System.Globalization;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Settings;

/// <summary>
/// What the AI cleanup page says in each app's settings on this PC (<see cref="LocalModelTuning"/>): Ollama's and LM
/// Studio's under their own expander, Foundry Local's under Model settings. Kept in Core beside the behaviour it describes,
/// so a test holds the words to what Scribe does.
/// </summary>
public static class LocalModelTuningText
{
    /// <summary>The line under each app's settings expander.</summary>
    public const string TuningSummary =
        "How much the model reads at once, and how much of your vocabulary it gets. Most people never need to change these.";

    /// <summary>The whole vocabulary switch, the same for every app.</summary>
    public const string WholeVocabularyTitle = "Send your whole vocabulary when it fits";

    private const string WholeVocabularyWhat =
        "Sends every word from your dictionary and the word packs you let AI cleanup use, not just the ones a dictation " +
        "seems to mention, when they fit with your dictation. A long list can confuse a small model, ";

    /// <summary>Under the switch for Ollama and LM Studio, which read the words once each time the model loads.</summary>
    public const string AppWholeVocabularyHint =
        WholeVocabularyWhat +
        "and the model reads it again each time it loads: a few seconds with a graphics card, a minute or more without one.";

    /// <summary>Under the switch for Foundry Local, which reads them again for every dictation.</summary>
    public const string FoundryWholeVocabularyHint =
        WholeVocabularyWhat +
        "and Foundry Local reads it again for every dictation, which can add many seconds to each one without a graphics card.";

    /// <summary>The context size choice, the same for Ollama and LM Studio.</summary>
    public const string ContextSizeTitle = "Context size";

    /// <summary>
    /// Under Ollama's context size. A size is asked of Ollama with every request (through its own chat API, the only one
    /// that takes one), and Ollama reloads a model when a request asks for a size other than the one it holds.
    /// </summary>
    public const string OllamaContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. A larger size fits more of your vocabulary " +
        "and can use more memory. With a size chosen, Ollama reloads the model whenever you switch between Scribe and " +
        "another app that uses it at a different size. Ollama's setting leaves it to Context length in Ollama's settings.";

    /// <summary>Under LM Studio's context size.</summary>
    public const string LmStudioContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. With a size chosen, Scribe loads the model " +
        "at it and frees it after the idle time. A model you loaded yourself in LM Studio keeps its own size.";

    /// <summary>
    /// The context size choices: the app's own setting (0), then <see cref="ContextBudget.OfferedSizes"/>. A stored size
    /// the list does not offer (one an older or newer build saved) is listed too, so it is shown as it is.
    /// </summary>
    public static IReadOnlyList<(int Tokens, string Label)> ContextSizes(string appName, int stored = 0)
    {
        var sizes = new List<(int Tokens, string Label)> { (0, $"{appName}'s setting") };
        var offered = ContextBudget.OfferedSizes.ToList();
        if (stored > 0 && !offered.Contains(stored))
        {
            offered.Add(stored);
            offered.Sort();
        }

        sizes.AddRange(offered.Select(tokens => (tokens, SizeLabel(tokens))));
        return sizes;
    }

    /// <summary>A size as the list names it: "32K (32,768 tokens)".</summary>
    public static string SizeLabel(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K ({Count(tokens)} tokens)"
            : $"{Count(tokens)} tokens";

    /// <summary>
    /// The status line under an app's settings: the context the model reads, as far as Scribe knows it, how much the whole
    /// vocabulary needs, and whether it fits at that size.
    /// </summary>
    /// <param name="appName">Ollama, LM Studio or Foundry Local.</param>
    /// <param name="inUse">What the app said it loaded the model with; 0 when Scribe does not know.</param>
    /// <param name="asked">The size Scribe asks for; 0 when it leaves the size to the app.</param>
    /// <param name="vocabularyTokens">The whole vocabulary's estimated tokens; 0 when there is none.</param>
    /// <param name="vocabularyRoom">
    /// The tokens of vocabulary that fit beside a typical dictation at the size in use, or asked for
    /// (<see cref="ContextBudget.VocabularyRoom"/>); null when neither is known.
    /// </param>
    public static string ContextStatus(string appName, int inUse, int asked, long vocabularyTokens, int? vocabularyRoom = null)
    {
        var context = inUse > 0
            ? $"The model is reading up to {Count(inUse)} tokens."
            : asked > 0
                ? $"Scribe asks for {Count(asked)} tokens when the model loads."
                : $"{appName} sets the size when it loads the model.";
        if (vocabularyTokens <= 0)
        {
            return context + " AI cleanup has no vocabulary to send.";
        }

        var needs = $"{context} Your whole vocabulary needs about {Count(vocabularyTokens)} tokens";
        return vocabularyRoom switch
        {
            null => needs + ".",
            var room when room >= vocabularyTokens => needs + ", which fits at this size.",
            > 0 and var room => $"{needs}; at this size, about {Count(room)} of them fit beside a dictation.",
            _ => needs + "; at this size, none of it fits beside a dictation.",
        };
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
