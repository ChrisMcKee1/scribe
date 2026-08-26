using System.Text;
using System.Text.RegularExpressions;

namespace Scribe.StyleEval.Checks;

/// <summary>Shared string measurements the checkers need. Pure, allocation-light, no I/O.</summary>
internal static partial class TextTools
{
    /// <summary>The two dash characters the house style forbids introducing.</summary>
    public static int CountDashes(string value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c is '—' or '–')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Lower-cases, collapses whitespace, and strips edge punctuation.</summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '*', '_', '`', ' ');
    }

    /// <summary>Content words of a phrase, for the fuzzy phrase match the positive checkers use.</summary>
    public static IReadOnlyList<string> ContentWords(string value) =>
        [.. Normalize(value)
            .Split((char[])[' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "are", "was", "were",
        "has", "have", "will", "you", "your", "our", "its", "but", "not",
    };

    /// <summary>
    /// True when <paramref name="phrase"/> is present in <paramref name="candidate"/> closely enough
    /// to count. Exact containment first; otherwise most of the phrase's content words must be there.
    /// </summary>
    /// <remarks>
    /// A model asked to bold "by Friday" may legitimately emit "by Friday," or "by this Friday", and
    /// failing it for the comma would make the positive half unusable. The fuzzy arm is deliberately
    /// strict about content words so "Friday" alone does not satisfy "by Friday the 3rd".
    /// </remarks>
    public static bool PhraseMatches(string candidate, string phrase)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedPhrase = Normalize(phrase);

        if (normalizedPhrase.Length == 0)
        {
            return false;
        }

        if (normalizedCandidate.Contains(normalizedPhrase, StringComparison.Ordinal) ||
            normalizedPhrase.Contains(normalizedCandidate, StringComparison.Ordinal))
        {
            return true;
        }

        var words = ContentWords(phrase);
        if (words.Count == 0)
        {
            return false;
        }

        var hits = words.Count(w => normalizedCandidate.Contains(w, StringComparison.Ordinal));
        return hits * 3 >= words.Count * 2;
    }

    /// <summary>
    /// Sentence count, by terminal punctuation followed by a space or end of text, plus one for a
    /// trailing run that never got a terminator.
    /// </summary>
    /// <remarks>
    /// The trailing run matters. Dictated and chat-register input often ends with no full stop at
    /// all, and counting only terminators makes that text zero sentences, so the proofread supplying
    /// the missing full stop, which is precisely its job, would read as "sentence count moved from
    /// 0 to 1" and fail.
    /// </remarks>
    public static int CountSentences(string value)
    {
        var count = 0;
        var lastTerminator = -1;

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is not ('.' or '!' or '?'))
            {
                continue;
            }

            // Run past "?!" and "..." so one terminator is counted once.
            while (i + 1 < value.Length && value[i + 1] is '.' or '!' or '?')
            {
                i++;
            }

            if (i + 1 >= value.Length || char.IsWhiteSpace(value[i + 1]))
            {
                count++;
                lastTerminator = i;
            }
        }

        return value[(lastTerminator + 1)..].Trim().Length > 0 ? count + 1 : count;
    }

    /// <summary>Paragraph count, by blank-line separation.</summary>
    public static int CountParagraphs(string value) =>
        BlankLine.Split(value.Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .Count(p => p.Trim().Length > 0);

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLine { get; }

    /// <summary>
    /// Levenshtein distance over whitespace-collapsed text, divided by the longer length.
    /// </summary>
    /// <remarks>
    /// Two rolling rows rather than a full matrix: the proofread corpus runs to a few thousand
    /// characters and a full matrix at ten thousand cells is gigabytes of churn for no benefit.
    /// </remarks>
    public static double NormalizedEditDistance(string left, string right)
    {
        var a = CollapseWhitespace(left);
        var b = CollapseWhitespace(right);

        if (a.Length == 0 && b.Length == 0)
        {
            return 0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Collapses runs of whitespace to a single space, preserving case.</summary>
    public static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Truncates for a result-file reason string, so one bad cell cannot bloat the JSONL.</summary>
    public static string Clip(string value, int max = 160)
    {
        var single = CollapseWhitespace(value);
        return single.Length <= max ? single : single[..max] + "...";
    }
}
