namespace Scribe.Core.Cleanup;

/// <summary>
/// A conservative estimate of how many tokens a model on this PC reads for some text, used to fit a request into its
/// context (<see cref="ContextBudget"/>). Scribe has no tokenizer for the models it talks to, and the apps that run them
/// count a prompt only once it is sent, so the estimate errs high: a prompt that runs past the context loses its start
/// (Ollama keeps only the end of a prompt that does not fit, which once dropped every instruction), while an estimate a
/// little high only leaves a few words of vocabulary out.
/// </summary>
/// <remarks>
/// Measured on Ollama 0.35.0 with the tokenizers of Gemma 4 E4B, Qwen3 4B and Granite 4 3B: Scribe's short instructions
/// with its writing style took 4.37 to 4.39 characters a token, the 25 benchmark dictations 4.18 to 4.28 on average and
/// 2.99 at worst (a short dictation, where the transcript tags weigh most), and a vocabulary of 1,367 terms 2.87 (Gemma)
/// to 3.29. So prose counts at <see cref="ProseCharsPerToken"/> and vocabulary at <see cref="VocabularyCharsPerToken"/>,
/// both below every measurement. A character outside ASCII counts as a token of its own, whatever the tokenizer makes of
/// it, so text in a script these tokenizers split finely is never undercounted.
/// </remarks>
public static class TokenEstimate
{
    /// <summary>Characters a token for instructions and dictated text: below the 4.18 to 4.39 measured.</summary>
    public const double ProseCharsPerToken = 3.6;

    /// <summary>Characters a token for vocabulary lines, which are mostly names and acronyms: below the 2.87 measured.</summary>
    public const double VocabularyCharsPerToken = 2.6;

    /// <summary>
    /// Tokens added to a dictation for the transcript tags and line breaks around it, which tokenize finely: with its tags,
    /// the benchmark dictation that tokenized worst took 2.99 characters a token.
    /// </summary>
    public const int ShortTextAllowance = 12;

    /// <summary>The estimated tokens of instructions or dictated text.</summary>
    public static int Prose(ReadOnlySpan<char> text) => Estimate(text, ProseCharsPerToken);

    /// <summary>The estimated tokens of a dictation as its request carries it, between the transcript tags.</summary>
    public static int Transcript(ReadOnlySpan<char> text) => Estimate(text, ProseCharsPerToken) + ShortTextAllowance;

    /// <summary>The estimated tokens of vocabulary text: one glossary line, or the glossary's header.</summary>
    public static int Vocabulary(ReadOnlySpan<char> text) => Estimate(text, VocabularyCharsPerToken);

    private static int Estimate(ReadOnlySpan<char> text, double charsPerToken)
    {
        var ascii = 0;
        var other = 0;
        foreach (var ch in text)
        {
            if (ch < 0x80)
            {
                ascii++;
            }
            else
            {
                other++;
            }
        }

        return (int)Math.Ceiling(ascii / charsPerToken) + other;
    }
}
