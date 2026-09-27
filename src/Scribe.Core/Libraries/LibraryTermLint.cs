using System.Collections.Frozen;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Libraries;

/// <summary>What the editor's hints say about a term (plan 3.6). A hint never blocks a Save; the words are W2's.</summary>
[Flags]
public enum TermHints
{
    None = 0,

    /// <summary>
    /// The spoken form is an ordinary word in a language the bundled speech model transcribes (<see cref="LibraryTermLint.CommonWords"/>),
    /// so the rule also fires in everyday sentences of that language.
    /// </summary>
    OrdinaryWord = 1,

    /// <summary>Whole word is off: the rule rewrites the spoken form inside longer words too.</summary>
    WholeWordOff = 2,

    /// <summary>
    /// The written form is the spoken form in lowercase and is not a name that is always lowercase
    /// (<see cref="LibraryTermLint.AlwaysLowercaseNames"/>): matching ignores case and nothing capitalizes afterwards, so
    /// the rule lowercases the word wherever it appears, at the start of a sentence too.
    /// </summary>
    ForcesLowercase = 4,

    /// <summary>
    /// The written form is longer than the AI cleanup glossary's per-term cap, so the glossary leaves the term out (it is
    /// not shortened); dictation on this PC still applies it.
    /// </summary>
    LongForGlossary = 8,

    /// <summary>
    /// The written form spans lines, so the AI cleanup glossary leaves the term out as a template; dictation on this PC
    /// still applies it.
    /// </summary>
    MultiLine = 16,

    /// <summary>
    /// The spoken form is not in the form the editor commits (<see cref="LibraryTermKey.IsInCommitForm"/>): white space at
    /// its edges, or a double space, a tab or a no-break space inside it. Only an older file can hold one, and such a
    /// pattern never matches dictated text; "Fix spacing" is an ordinary edit that commits <see cref="LibraryTermKey.Normalize"/>.
    /// </summary>
    IrregularSpacing = 32,
}

/// <summary>
/// The editor's hints about a term (plan 3.6): pure checks that never block a Save. The word lists are the ones the
/// shipped libraries are held to (<c>BuiltInLibraryDataTests</c> reads them from here), and the two glossary hints are
/// judged by the predicate the glossary itself applies, <see cref="CleanupPrompt.IsVocabularyReplacement"/>, so a hint
/// can never say a term is left out that AI cleanup receives, or the other way round.
/// </summary>
public static class LibraryTermLint
{
    /// <summary>
    /// Ordinary words in languages the bundled Parakeet model transcribes, compared without case. A whole-word rule on any
    /// of these fires mid-sentence for a speaker of that language: "il" (French and Italian) and "di" (Italian) were both
    /// shipped once as bare acronym rules.
    /// </summary>
    public static IReadOnlySet<string> CommonWords { get; } = new[]
    {
        // French, Italian, Spanish, Portuguese and German function words.
        "il", "di", "la", "le", "les", "de", "du", "des", "un", "une", "et", "en", "au", "ce",
        "se", "si", "su", "da", "del", "che", "non", "per", "con", "una", "el", "los", "las",
        "es", "als", "das", "der", "die", "den", "und", "ist", "im", "am", "an", "zu", "so",
        "no", "na", "os", "as", "em", "ao", "ou", "je", "tu", "me", "te", "ne", "on", "ma",

        // English words short enough to be mistaken for an acronym.
        "a", "i", "an", "as", "at", "be", "by", "do", "go", "he", "if", "in", "is", "it", "me",
        "my", "no", "of", "on", "or", "so", "to", "up", "us", "we",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names that really are always lowercase, compared exactly, so a rule forcing them down is intended. Every other rule
    /// that maps a word to its own lowercase form is a bug: "Distillation reduces size" once came out as "distillation
    /// reduces size".
    /// </summary>
    public static IReadOnlySet<string> AlwaysLowercaseNames { get; } = new[]
    {
        "npm", "pnpm", "kubectl", "webpack", "pandas", "conda", "dbt", "htmx",
        "statsmodels", "torchvision", "torchaudio",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The hints for <paramref name="values"/>, whether the row is on or off. The glossary hints depend on the written
    /// form alone: a written form that is not blank is left out of the glossary exactly when it is
    /// <see cref="TermHints.LongForGlossary"/> or <see cref="TermHints.MultiLine"/>.
    /// </summary>
    public static TermHints Check(TermValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var spoken = values.Spoken ?? string.Empty;
        var written = values.Written ?? string.Empty;
        var hints = TermHints.None;

        if (CommonWords.Contains(spoken.Trim()))
        {
            hints |= TermHints.OrdinaryWord;
        }

        if (!values.WholeWord)
        {
            hints |= TermHints.WholeWordOff;
        }

        if (ForcesLowercase(spoken.Trim(), written.Trim()))
        {
            hints |= TermHints.ForcesLowercase;
        }

        // The glossary's own verdict first: a written form it accepts is neither long nor multi-line. Only one it leaves
        // out (or a blank one, which it leaves out as a removal rule) needs the reasons, and both are the predicate's.
        if (!CleanupPrompt.IsVocabularyReplacement(written))
        {
            if (written.Length > CleanupPrompt.MaxGlossaryTermChars)
            {
                hints |= TermHints.LongForGlossary;
            }

            if (SpansLines(written, CleanupPrompt.IsVocabularyReplacement))
            {
                hints |= TermHints.MultiLine;
            }
        }

        if (!LibraryTermKey.IsInCommitForm(spoken))
        {
            hints |= TermHints.IrregularSpacing;
        }

        return hints;
    }

    // Only casing differs, the written form is all lowercase, it has a letter that could have been uppercase, and it is
    // not a name that is always lowercase.
    private static bool ForcesLowercase(string spoken, string written)
    {
        if (written.Length == 0 ||
            !string.Equals(spoken, written, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(written, written.ToLowerInvariant(), StringComparison.Ordinal) ||
            AlwaysLowercaseNames.Contains(written))
        {
            return false;
        }

        foreach (var ch in written)
        {
            if (char.ToUpperInvariant(ch) != ch)
            {
                return true;
            }
        }

        return false;
    }

    // CleanupPrompt keeps its set of line breaks to itself, so ask its predicate: a window of the text with a
    // non-blank character in front and no more characters than the glossary's cap fails it only for a line break. A
    // window never ends between the two halves of a surrogate pair, so the probe is well-formed whenever the value is,
    // and a pair cut in two can never be what fails it, whatever the predicate one day says about surrogates. The
    // predicate is a parameter only so a test can hold the probe to that.
    internal static bool SpansLines(string written, Func<string, bool> isVocabularyReplacement)
    {
        var window = CleanupPrompt.MaxGlossaryTermChars - 1;
        var length = 0;
        for (var start = 0; start < written.Length; start += length)
        {
            length = Math.Min(window, written.Length - start);
            if (length == window && char.IsHighSurrogate(written[start + length - 1]))
            {
                length--;
            }

            if (!isVocabularyReplacement(string.Concat("x", written.AsSpan(start, length))))
            {
                return true;
            }
        }

        return false;
    }
}
