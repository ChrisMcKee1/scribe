using System.Text.RegularExpressions;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Mines recent dictation history for recurring jargon worth adding to the user dictionary — the
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
                if (token.Length < 2 || known.Contains(token) || Stoplist.Contains(token))
                {
                    continue;
                }

                if (!IsJargonShaped(token))
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

    // High-precision "this is jargon" shapes; ordinary prose words match none of them.
    internal static bool IsJargonShaped(string token) =>
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

    // Letters and digits mixed in one token, starting with a letter: K8s, S3, net10, GPT4.
    [GeneratedRegex(@"^[A-Za-z]+[0-9]+[A-Za-z0-9]*$")]
    private static partial Regex LetterDigit();
}
