using System.Text.RegularExpressions;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Mines recent dictation history for recurring jargon worth adding to the user dictionary: the
/// pragmatic version of "auto-learning": Scribe never sees the user's manual corrections after
/// injection, but it can spot the technical terms they keep saying and offer to lock their
/// spelling in. Deliberately high-precision patterns only (acronyms, CamelCase, digit-words like
/// K8s), because a noisy suggestion list is worse than none.
/// </summary>
public static partial class DictionarySuggestionMiner
{
    /// <summary>A recurring term and how many distinct dictations it appeared in.</summary>
    public sealed record Suggestion(string Term, int Dictations);

    // Boring capitalized tokens that clear the acronym bar but aren't vocabulary.
    private static readonly HashSet<string> Stoplist = new(StringComparer.Ordinal)
    {
        "OK", "AM", "PM", "TODO", "FYI", "ASAP", "LOL",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> StoplistSpans =
        Stoplist.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Returns suggested dictionary entries: terms matching a jargon pattern that occur in at least
    /// <paramref name="minDictations"/> distinct dictations and aren't already covered by the
    /// dictionary. Ordered by frequency, capped at <paramref name="maxSuggestions"/>.
    /// </summary>
    public static IReadOnlyList<Suggestion> Mine(
        IEnumerable<HistoryEntry> entries,
        IEnumerable<DictionaryEntry> existing,
        int minDictations = 3,
        int maxSuggestions = 12)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(existing);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existing)
        {
            known.Add(entry.Pattern.Trim());
            if (!string.IsNullOrWhiteSpace(entry.Replacement))
            {
                known.Add(entry.Replacement.Trim());
            }
        }

        // term (case-insensitive) -> (most frequent surface form, per-form counts, dictation count)
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var dictations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var history in entries)
        {
            var seenInThisDictation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in history.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = TrimPunctuation(raw);
                if (token.Length < 2 || known.Contains(token) || !IsCandidate(token))
                {
                    continue;
                }

                if (!counts.TryGetValue(token, out var forms))
                {
                    forms = new Dictionary<string, int>(StringComparer.Ordinal);
                    counts[token] = forms;
                }

                forms[token] = forms.GetValueOrDefault(token) + 1;

                if (seenInThisDictation.Add(token))
                {
                    dictations[token] = dictations.GetValueOrDefault(token) + 1;
                }
            }
        }

        return dictations
            .Where(kv => kv.Value >= minDictations)
            .Select(kv => new Suggestion(
                // Suggest the surface form the user's text uses most often.
                counts[kv.Key].OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).First().Key,
                kv.Value))
            .OrderByDescending(s => s.Dictations)
            .ThenBy(s => s.Term, StringComparer.OrdinalIgnoreCase)
            .Take(maxSuggestions)
            .ToList();
    }

    /// <summary>
    /// Whether a token is worth suggesting: jargon-shaped and not one of the stoplist's everyday abbreviations. Usage's
    /// "Words you could add" and Learn from history both ask this, so "PM" from "2 PM" is offered by neither.
    /// </summary>
    internal static bool IsCandidate(string token)
    {
        // Regex.IsMatch(string) threw for a null token, naming its parameter; the span overload would not.
        ArgumentNullException.ThrowIfNull(token, "input");
        return IsCandidate(token.AsSpan());
    }

    /// <summary>
    /// <see cref="IsCandidate(string)"/> for a token that is still part of a longer text, so the caller need not copy it out.
    /// </summary>
    internal static bool IsCandidate(ReadOnlySpan<char> token) => !StoplistSpans.Contains(token) && IsJargonShaped(token);

    // High-precision "this is jargon" shapes; ordinary prose words match none of them.
    internal static bool IsJargonShaped(string token) =>
        Acronym().IsMatch(token) || CamelHump().IsMatch(token) || LetterDigit().IsMatch(token);

    private static bool IsJargonShaped(ReadOnlySpan<char> token) =>
        Acronym().IsMatch(token) || CamelHump().IsMatch(token) || LetterDigit().IsMatch(token);

    private static string TrimPunctuation(string token)
    {
        // Trailing sentence punctuation always goes; leading quotes/brackets go but a leading dot
        // survives so ".NET" stays intact.
        return token.TrimEnd(',', '.', '!', '?', ';', ':', ')', ']', '}', '"', '\'')
                    .TrimStart('(', '[', '{', '"', '\'');
    }

    [GeneratedRegex(@"^\.?[A-Z]{2,8}$")]
    private static partial Regex Acronym();

    // A lowercase→uppercase transition inside the word: ReBAC, GitHub, JavaScript, sherpa-Onnx.
    [GeneratedRegex(@"^\.?[A-Za-z]*[a-z][A-Z][A-Za-z]*$")]
    private static partial Regex CamelHump();

    // Letters and digits mixed in one token, starting with a letter: K8s, S3, net10, GPT4. One digit, not a run of them: a run
    // followed by letters or digits accepts exactly the same tokens, and its two overlapping loops made the regex backtrack
    // (and allocate while unoptimized, DictionarySuggestionMinerSpanTests.Allocations).
    [GeneratedRegex(@"^[A-Za-z]+[0-9][A-Za-z0-9]*$")]
    private static partial Regex LetterDigit();
}
