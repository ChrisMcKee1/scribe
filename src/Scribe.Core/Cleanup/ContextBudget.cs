namespace Scribe.Core.Cleanup;

/// <summary>
/// How much of a request to a model on this PC its vocabulary may take: the model's context, less the instructions, the
/// dictated text, room for the answer and a margin. The dictated text always comes first, so a long dictation leaves
/// less room for vocabulary and a short one more, and a request never runs past the context: Ollama keeps only the end
/// of a prompt that does not fit, which once dropped every instruction (see <see cref="LocalAiServer"/>).
/// </summary>
/// <remarks>
/// Every count here is an estimate (<see cref="TokenEstimate"/>), high on purpose. The context is what the app on this
/// PC says it loaded the model with, the size Scribe asked for, or, before either is known,
/// <see cref="AssumedContextTokens"/>.
/// </remarks>
public static class ContextBudget
{
    /// <summary>
    /// The context Scribe assumes before the app has said what it loaded the model with: Ollama's default on a graphics
    /// card under 24 GB and on the CPU (it gives 32,768 from 24 GB and 262,144 from 48 GB), and LM Studio's own default.
    /// </summary>
    public const int AssumedContextTokens = 4096;

    /// <summary>The context sizes Settings offers for Ollama and LM Studio, in tokens.</summary>
    public static IReadOnlyList<int> OfferedSizes { get; } = [8192, 16384, 32768, 65536, 131072];

    /// <summary>The smallest context size Scribe asks an app for, and the largest.</summary>
    public const int MinimumSize = 2048;

    /// <inheritdoc cref="MinimumSize"/>
    public const int MaximumSize = 1_048_576;

    /// <summary>
    /// The chat template's own tokens around one system and one user message: measured at 12 to 13 for Gemma 4, Qwen3 and
    /// Granite 4, counted at more.
    /// </summary>
    internal const int ChatTemplateTokens = 32;

    /// <summary>
    /// The dictated text a readying request leaves room for. It is sent as a recording starts, before anything is
    /// dictated, so it leaves room for a dictation of about 1,800 characters and its answer; a longer one leaves less
    /// room for vocabulary in its own request, which then shares the start of the readied vocabulary.
    /// </summary>
    internal const int ReadyingTranscriptTokens = 512;

    /// <summary>
    /// The answer a readying request leaves room for: about what a dictation of <see cref="ReadyingTranscriptTokens"/>
    /// declares (its output ceiling, about 2.5 tokens a word plus 128).
    /// </summary>
    internal const int ReadyingOutputTokens = 1152;

    /// <summary>A context size Scribe can ask for: zero (the app's own setting) or a size within the bounds.</summary>
    public static int Sanitize(int contextTokens) =>
        contextTokens <= 0 ? 0 : Math.Clamp(contextTokens, MinimumSize, MaximumSize);

    /// <summary>
    /// The tokens left for vocabulary, its header included, in a request carrying <paramref name="transcript"/> under
    /// <paramref name="instructions"/> (the system prompt without vocabulary) that lets the model answer with up to
    /// <paramref name="outputCeiling"/> tokens: the request and its longest allowed answer fit the context together.
    /// Negative when even the text and its answer do not fit.
    /// </summary>
    /// <param name="outputCeiling">The most the request lets the model answer with (its max_tokens).</param>
    public static int VocabularyTokens(int contextTokens, string instructions, string transcript, int outputCeiling)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(transcript);
        return VocabularyTokensFor(contextTokens, instructions, RequestTextCost(transcript, outputCeiling));
    }

    /// <summary>
    /// The tokens left for vocabulary beside the request whose text and answer cost the most of all a dictation sends
    /// (<see cref="RequestTextCost"/>): every one of its requests carries the same vocabulary, so that one decides.
    /// </summary>
    internal static int VocabularyTokensFor(int contextTokens, string instructions, long worstRequestTextCost) =>
        (int)Math.Clamp(
            (long)contextTokens - ChatTemplateTokens - TokenEstimate.Prose(instructions) - worstRequestTextCost -
                Margin(contextTokens),
            int.MinValue,
            int.MaxValue);

    /// <summary>What one request's dictated text and its longest allowed answer take of the context.</summary>
    internal static long RequestTextCost(string transcript, int outputCeiling) =>
        (long)TokenEstimate.Transcript(transcript) + Math.Max(0, outputCeiling);

    /// <summary>The tokens a readying request leaves for vocabulary (see <see cref="ReadyingTranscriptTokens"/>).</summary>
    public static int ReadyingVocabularyTokens(int contextTokens, string instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return VocabularyTokensFor(contextTokens, instructions, ReadyingTranscriptTokens + ReadyingOutputTokens);
    }

    /// <summary>True when <paramref name="transcript"/> and its answer fit with the instructions and no vocabulary.</summary>
    public static bool TextFits(int contextTokens, string instructions, string transcript, int outputCeiling) =>
        VocabularyTokens(contextTokens, instructions, transcript, outputCeiling) >= 0;

    /// <summary>
    /// The tokens of vocabulary, its header included, a model on this PC reading <paramref name="contextTokens"/> has room
    /// for beside a typical dictation (<see cref="ReadyingTranscriptTokens"/>) under <paramref name="options"/>' instructions:
    /// what Settings compares the whole vocabulary with (<c>GlossaryHint.WholeVocabularyTokens</c>, header included too).
    /// Negative when not even the instructions and a dictation fit.
    /// </summary>
    public static int VocabularyRoom(int contextTokens, CleanupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ReadyingVocabularyTokens(contextTokens, TextCleanupService.BuildProbeSystemPrompt(options));
    }

    /// <summary>The room left beyond what is counted, for the estimate's own error: 3% of the context, at least 64 tokens.</summary>
    internal static int Margin(int contextTokens) => Math.Max(64, contextTokens * 3 / 100);
}
