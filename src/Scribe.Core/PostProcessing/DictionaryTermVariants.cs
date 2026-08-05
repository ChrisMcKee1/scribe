using System.Text.RegularExpressions;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Derives the dictionary patterns that a recognizer plausibly produces for a known-good term.
/// </summary>
/// <remarks>
/// This exists because dictation history only ever records what the recognizer got <em>right</em>:
/// it is written after the dictionary has already run, so it can never show the misrecognition a
/// rule is supposed to repair. Mining it and emitting <c>lowercase(term) -> term</c> invents a
/// left-hand side that was never observed, which is how the dictionary filled up with rules that
/// provably do nothing (re-decoding retained audio found "AI" 22 times in raw recognizer output and
/// "ai" zero times, so <c>ai -> AI</c> had never once fired).
/// <para>
/// So we only generate the two shapes that re-decoding showed Parakeet genuinely produces:
/// spelling an unknown acronym out letter by letter ("C L I", "M C P"), and splitting a closed
/// compound ("co pilot", "power platform", "second brain"). Both are recoverable because the
/// pattern differs from the term only in case and spacing, which also makes them safe: neither
/// "c s u" nor "co pilot" occurs in ordinary prose, so a false positive cannot corrupt normal text.
/// Anything needing a real phonetic guess ("Stew" -> "STU", "Get Up" -> "GitHub") is not derivable
/// and must come from the user correcting a dictation.
/// </para>
/// </remarks>
public static partial class DictionaryTermVariants
{
    // Two-letter acronyms are excluded on purpose: the spelled form of one is a pair of single
    // letters ("a i"), which collides with the article "a" and the pronoun "I" in ordinary prose.
    private const int MinAcronymLetters = 3;

    // Splitting a compound must not produce a pattern made of ordinary words, or the rule stops
    // being a rendering fix and starts rewriting prose.
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "do", "for", "from", "go", "had",
        "has", "have", "he", "her", "his", "i", "if", "in", "is", "it", "its", "me", "my", "no",
        "not", "of", "on", "or", "our", "out", "she", "so", "that", "the", "their", "them", "then",
        "there", "they", "this", "to", "up", "us", "was", "we", "were", "what", "when", "which",
        "who", "will", "with", "would", "you", "your",
    };

    /// <summary>
    /// The patterns worth adding for <paramref name="term"/>, or an empty list when none is
    /// recoverable. Every returned pattern differs from the term only in case and spacing.
    /// </summary>
    public static IReadOnlyList<string> For(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var trimmed = term.Trim();
        var variants = new List<string>();

        if (TrySpellOutAcronym(trimmed, out var spelled))
        {
            variants.Add(spelled);
        }

        if (TrySplitCompound(trimmed, out var split))
        {
            variants.Add(split);
        }

        return variants;
    }

    /// <summary>"CSU" -> "c s u": the recognizer spells out acronyms it has no token for.</summary>
    private static bool TrySpellOutAcronym(string term, out string pattern)
    {
        pattern = string.Empty;
        if (!Acronym().IsMatch(term))
        {
            return false;
        }

        var letters = term.TrimStart('.');
        if (letters.Length < MinAcronymLetters)
        {
            return false;
        }

        pattern = string.Join(' ', letters.Select(char.ToLowerInvariant));
        return true;
    }

    /// <summary>"WebIQ" -> "web iq": the recognizer hears a closed compound as separate words.</summary>
    private static bool TrySplitCompound(string term, out string pattern)
    {
        pattern = string.Empty;
        if (!CamelHump().IsMatch(term))
        {
            return false;
        }

        var words = SplitOnHumps(term.TrimStart('.'));
        if (words.Count < 2 || words.Any(w => w.Length < 2))
        {
            return false;
        }

        // "AndThen" -> "and then" would rewrite ordinary prose, so a compound made of everyday
        // words is not a safe rendering fix even though it is shaped like one.
        if (words.Any(CommonWords.Contains))
        {
            return false;
        }

        pattern = string.Join(' ', words.Select(w => w.ToLowerInvariant()));
        return !string.Equals(pattern, term, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitOnHumps(string term)
    {
        var words = new List<string>();
        var start = 0;

        for (var i = 1; i < term.Length; i++)
        {
            if (!char.IsUpper(term[i]) || !char.IsLower(term[i - 1]))
            {
                continue;
            }

            words.Add(term[start..i]);
            start = i;
        }

        words.Add(term[start..]);
        return words;
    }

    [GeneratedRegex(@"^\.?[A-Z0-9]{2,8}$")]
    private static partial Regex Acronym();

    [GeneratedRegex(@"^\.?[A-Za-z]*[a-z][A-Z][A-Za-z]*$")]
    private static partial Regex CamelHump();
}
