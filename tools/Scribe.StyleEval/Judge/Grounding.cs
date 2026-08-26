using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Scribe.StyleEval.Judge;

/// <summary>How well a quoted span could be located in the text it was supposed to come from.</summary>
internal enum GroundingMode
{
    /// <summary>The span is not in the text at all. The finding is not evidence of anything.</summary>
    Missing,

    /// <summary>
    /// Every content word of the span is present, but not as one run. A quote that drifted on
    /// punctuation, an ellipsis, or a word of markup.
    /// </summary>
    Loose,

    /// <summary>The span is present as a contiguous run once markup and whitespace are normalised.</summary>
    Exact,
}

/// <summary>
/// Checks that a judge's quoted span really came from the text it was attributed to.
/// </summary>
/// <remarks>
/// <para>
/// This is the honesty control on the whole judge half. An LLM asked "what structure is missing"
/// will always find something, and the only cheap way to separate a real observation from an
/// invented one is to insist it quotes the text and then verify the quote. A finding whose span
/// cannot be located is recorded and reported but never counted toward a missed-opportunity rate,
/// so a hallucinating judge inflates its own error rate rather than the model's.
/// </para>
/// <para>
/// Normalisation is deliberately generous about markup and strict about words. The judge quotes the
/// input, which is plain text, and the output, which may be Markdown, an HTML fragment or JSON, so a
/// span such as <c>--dry-run</c> legitimately appears in the output as <c>`--dry-run`</c> or as
/// <c>&lt;code&gt;--dry-run&lt;/code&gt;</c>. Stripping emphasis characters, tags and entities before
/// comparing is what stops those from reading as fabrications. Nothing here invents a match: every
/// content word still has to be present.
/// </para>
/// </remarks>
internal static partial class Grounding
{
    /// <summary>Shortest span the loose arm will accept, in normalised characters.</summary>
    private const int MinimumLooseLength = 12;

    /// <summary>Locates <paramref name="span"/> in <paramref name="text"/>.</summary>
    public static GroundingMode Locate(string? span, string? text)
    {
        if (string.IsNullOrWhiteSpace(span) || string.IsNullOrWhiteSpace(text))
        {
            return GroundingMode.Missing;
        }

        var needle = Normalize(span);
        var haystack = Normalize(text);

        if (needle.Length == 0 || haystack.Length == 0)
        {
            return GroundingMode.Missing;
        }

        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            return GroundingMode.Exact;
        }

        if (needle.Length < MinimumLooseLength)
        {
            return GroundingMode.Missing;
        }

        // Every word of the quote has to be somewhere in the text. A judge that invented a fact
        // invents words with it, so this arm cannot rescue a fabrication; it only rescues a quote
        // that lost a comma, a bullet character or a line break on its way out of the model.
        var words = needle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToList();

        if (words.Count < 2)
        {
            return GroundingMode.Missing;
        }

        return words.All(w => haystack.Contains(w, StringComparison.Ordinal))
            ? GroundingMode.Loose
            : GroundingMode.Missing;
    }

    /// <summary>True when the span was found, either way.</summary>
    public static bool IsGrounded(string? span, string? text) => Locate(span, text) != GroundingMode.Missing;

    /// <summary>
    /// Lower-cases, removes markup that a destination may legitimately have wrapped the span in, and
    /// collapses everything else to single spaces.
    /// </summary>
    private static string Normalize(string value)
    {
        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal);

        // An HTML fragment carries the author's words inside tags and entities; JSON carries them
        // inside escapes. Both are the same words once the wrapper is taken off.
        text = Tag.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\\n", " ", StringComparison.Ordinal)
                   .Replace("\\\"", "\"", StringComparison.Ordinal)
                   .Replace("\\\\", "\\", StringComparison.Ordinal);

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            // Emphasis, code, quote, bullet and heading marks are how the destination spells the
            // span rather than part of it. Sentence punctuation goes too: a judge quoting a clause
            // routinely drops or adds the trailing comma.
            if (c is '*' or '_' or '`' or '#' or '>' or '|' or '"' or '\'' or '‘' or '’'
                or '“' or '”' or ',' or ';' or ':' or '.' or '!' or '?' or '(' or ')'
                or '[' or ']' or '{' or '}')
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

        return sb.ToString().Trim();
    }

    [GeneratedRegex("<[^>]{1,200}>")]
    private static partial Regex Tag { get; }
}
