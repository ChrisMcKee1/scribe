using System.Text.Json;
using System.Text.RegularExpressions;
using Scribe.StyleEval.Markup;

namespace Scribe.StyleEval.Checks;

/// <summary>
/// The output contracts each format destination promises. One checker per destination; every other
/// destination gets NotApplicable rather than a free pass.
/// </summary>
internal static partial class DestinationChecks
{
    private const CheckPolarity Neg = CheckPolarity.Negative;

    [GeneratedRegex(@"^\s*```")]
    private static partial Regex OpensWithFence { get; }

    [GeneratedRegex(@"^\s*(#{1,6}\s|>\s|[-*+]\s|\d+[.)]\s|\||```)")]
    private static partial Regex BlockStart { get; }

    [GeneratedRegex(@"<\s*/?\s*(p|div|span|br|hr|b|i|u|strong|em|ul|ol|li|table|tr|td|th|h[1-6]|a|img|code|pre|blockquote)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag { get; }

    [GeneratedRegex(@"(?<![*\w])\*(?!\*|\s)([^*\n]+?)(?<!\s)\*(?!\*)")]
    private static partial Regex SingleAsteriskEmphasis { get; }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s", RegexOptions.Multiline)]
    private static partial Regex AtxHeadingLine { get; }

    [GeneratedRegex(@"^\s{0,3}>\s", RegexOptions.Multiline)]
    private static partial Regex BlockQuoteLine { get; }

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex LowerCamelCase { get; }

    /// <summary>
    /// A string value that IS a date, anchored on purpose.
    /// </summary>
    /// <remarks>
    /// The ISO rule governs a date the author gave as a value. It does not reach a date sitting
    /// inside a sentence, because the same instruction says to keep the author's own sentences
    /// verbatim inside string values, so rewriting "we agreed on March 3, 2026" would break the rule
    /// it was trying to enforce.
    /// </remarks>
    [GeneratedRegex(@"^\s*(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}(st|nd|rd|th)?,?\s+\d{4}\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WrittenDateValue { get; }

    /// <summary>
    /// A numeric date value. The dotted form insists on a four digit year, because "3.0.13" is
    /// OpenSSL and not the third of October, and the same instruction says a version number stays a
    /// string exactly as written.
    /// </summary>
    [GeneratedRegex(@"^\s*(\d{1,2}/\d{1,2}/\d{2,4}|\d{1,2}\.\d{1,2}\.\d{4})\s*$")]
    private static partial Regex NumericDateValue { get; }

    /// <summary>Wrapper keys the instruction names and forbids when the text does not justify one.</summary>
    private static readonly IReadOnlySet<string> WrapperKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "data", "result", "results", "root", "items", "payload", "response", "output", "content", "object",
    };

    /// <summary>Format as Markdown: CommonMark, tagged fences, spaced blocks, no HTML, no wrapper fence.</summary>
    public static CheckResult Markdown(CheckContext c, string rawResponse)
    {
        const string Name = "markdown-contract";

        if (c.Action.Id != "format-markdown")
        {
            return CheckResult.Skip(Name, Neg, "not the Markdown destination");
        }

        if (c.Markup.ParseError is not null)
        {
            return CheckResult.Fail(Name, Neg, $"did not parse as CommonMark: {c.Markup.ParseError}");
        }

        var problems = new List<string>();

        // The whole answer wrapped in a fence. Checked on the RAW response as well as the sanitized
        // text, because TextActionSanitizer strips exactly this wrapper and would otherwise hide it.
        var wrapped = WholeAnswerIsWrapperFence(rawResponse);
        if (wrapped is not null)
        {
            problems.Add(wrapped);
        }

        var untagged = c.Markup.Fences.Where(f => string.IsNullOrWhiteSpace(f.Language)).ToList();
        if (untagged.Count > 0)
        {
            problems.Add($"{untagged.Count} fenced block(s) carry no language tag");
        }

        // The parser, not a regex. A selection about HTML legitimately contains tags, and a correct
        // Markdown answer puts them in code spans, where Markdig reports them as CodeInline rather
        // than HtmlInline. A regex over the raw text fails that answer for doing the right thing.
        if (c.Markup.ContainsRawHtml)
        {
            problems.Add("the answer contains raw HTML, which the Markdown destination forbids");
        }

        var crowded = MissingBlankLines(c.Output);
        if (crowded.Count > 0)
        {
            problems.Add($"no blank line before {crowded.Count} block(s), e.g. line {crowded[0].Line}: '{TextTools.Clip(crowded[0].Text, 50)}'");
        }

        // A stray delimiter is the cheapest signal that emphasis or code did not close properly.
        // Graded on the prose only: MarkdownReader appends fence and code-span CONTENT to PlainText,
        // and a shell glob, a Windows wildcard and a C pointer declaration all carry the delimiter,
        // so testing PlainText itself reports "rm -rf ./build/**/*.tmp" inside a tagged bash fence
        // as an emphasis run that never closed.
        var strayBold = ProseOnly(c.Markup).Contains("**", StringComparison.Ordinal);
        if (strayBold)
        {
            problems.Add("a literal '**' survived into the rendered text, so an emphasis run did not close");
        }

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg,
                $"CommonMark, {c.Markup.Fences.Count} fence(s) all tagged, no HTML, blocks spaced")
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems));
    }

    /// <summary>
    /// The rendered text with every code run blanked out, so a delimiter that is CONTENT is not read
    /// as broken markup.
    /// </summary>
    /// <remarks>
    /// Each run is replaced by a space rather than removed, because removing it can push the text on
    /// either side of it together and manufacture the very delimiter this is meant to stop finding.
    /// </remarks>
    private static string ProseOnly(MarkupFacts markup)
    {
        var text = markup.PlainText;
        foreach (var code in markup.AllCode)
        {
            if (code.Length > 0)
            {
                text = text.Replace(code, " ", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static string? WholeAnswerIsWrapperFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!OpensWithFence.IsMatch(trimmed) || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return null;
        }

        var newline = trimmed.IndexOf('\n');
        if (newline < 0)
        {
            return null;
        }

        var info = trimmed[3..newline].Trim();
        var body = trimmed[(newline + 1)..^3];
        if (body.Contains("```", StringComparison.Ordinal))
        {
            return null;
        }

        return info.Length == 0 || info.Equals("markdown", StringComparison.OrdinalIgnoreCase) || info.Equals("md", StringComparison.OrdinalIgnoreCase)
            ? $"the whole answer is wrapped in a '{(info.Length == 0 ? "untagged" : info)}' fence"
            : null;
    }

    private static List<(int Line, string Text)> MissingBlankLines(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var offenders = new List<(int, string)>();
        var inFence = false;
        var previousWasListItem = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isFence = line.TrimStart().StartsWith("```", StringComparison.Ordinal);
            var isClosingFence = isFence && inFence;

            if (isFence)
            {
                inFence = !inFence;
            }

            // Inside a fence nothing is a block, and a CLOSING fence needs no blank line before it:
            // the blank line belongs after it, and demanding one before would ask for a blank final
            // line inside every code block.
            if ((inFence && !isFence) || isClosingFence)
            {
                continue;
            }

            if (i == 0 || !BlockStart.IsMatch(line))
            {
                previousWasListItem = BlockStart.IsMatch(line) && IsListItem(line);
                continue;
            }

            var previous = lines[i - 1];
            var thisIsListItem = IsListItem(line);

            // Consecutive items of one list, and consecutive rows of one table, need no blank line.
            if (previous.Trim().Length == 0 ||
                (thisIsListItem && previousWasListItem) ||
                (line.TrimStart().StartsWith('|') && previous.TrimStart().StartsWith('|')))
            {
                previousWasListItem = thisIsListItem;
                continue;
            }

            offenders.Add((i + 1, line));
            previousWasListItem = thisIsListItem;
        }

        return offenders;
    }

    private static bool IsListItem(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("- ", StringComparison.Ordinal) ||
               t.StartsWith("* ", StringComparison.Ordinal) ||
               t.StartsWith("+ ", StringComparison.Ordinal) ||
               (t.Length > 2 && char.IsAsciiDigit(t[0]) && (t.Contains(". ", StringComparison.Ordinal) || t.Contains(") ", StringComparison.Ordinal)));
    }

    /// <summary>Format as HTML: a well-formed, allowlisted, attribute-starved, escaped fragment.</summary>
    public static CheckResult Html(CheckContext c)
    {
        const string Name = "html-contract";

        if (c.Action.Id != "format-html")
        {
            return CheckResult.Skip(Name, Neg, "not the HTML destination");
        }

        var problems = new List<string>();
        var raw = c.Output;

        // Hostile constructs are checked on the RAW text first, so they are reported even when the
        // fragment is too broken to parse. Author content that MENTIONS a script tag arrives escaped
        // as &lt;script&gt; and correctly does not match.
        if (HtmlFragment.Doctype.IsMatch(raw))
        {
            problems.Add("emitted a doctype");
        }

        if (HtmlFragment.DocumentShell.IsMatch(raw))
        {
            problems.Add("emitted an html, head or body element");
        }

        if (HtmlFragment.Comment.IsMatch(raw))
        {
            problems.Add("emitted an HTML comment");
        }

        var forbidden = HtmlFragment.ForbiddenElement.Matches(raw)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (forbidden.Count > 0)
        {
            problems.Add($"emitted forbidden element(s): {string.Join(", ", forbidden)}");
        }

        if (HtmlFragment.EventHandlerAttribute.IsMatch(raw))
        {
            problems.Add("emitted an attribute whose name begins with 'on'");
        }

        var facts = c.Html;
        if (!facts.WellFormed)
        {
            problems.Add(facts.ParseError ?? "not well formed");
            return CheckResult.Fail(Name, Neg, string.Join("; ", problems));
        }

        var disallowedElements = facts.Elements
            .Select(e => e.Name)
            .Where(n => !HtmlFragment.Allowed.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (disallowedElements.Count > 0)
        {
            problems.Add($"element(s) outside the allowlist: {string.Join(", ", disallowedElements)}");
        }

        if (facts.Elements.Any(e => e.Name == "h1"))
        {
            problems.Add("emitted h1, which breaks the host page outline");
        }

        foreach (var element in facts.Elements)
        {
            foreach (var (attribute, value) in element.Attributes)
            {
                if (attribute.StartsWith("on", StringComparison.Ordinal))
                {
                    problems.Add($"'{attribute}' on <{element.Name}>");
                    continue;
                }

                if (!DestinationChecksAllowedAttribute(element.Name, attribute))
                {
                    problems.Add($"attribute '{attribute}' on <{element.Name}> is not permitted");
                    continue;
                }

                if (attribute == "href" && !IsAllowedHref(value))
                {
                    problems.Add($"href scheme not in http/https/mailto: '{TextTools.Clip(value, 50)}'");
                }
            }
        }

        var escaping = EscapingProblems(raw, facts);
        problems.AddRange(escaping);

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg,
                $"well-formed fragment, {facts.Elements.Count} element(s) all allowlisted, attributes and escaping clean")
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems.Distinct(StringComparer.Ordinal)));
    }

    private static bool DestinationChecksAllowedAttribute(string element, string attribute) =>
        HtmlFragment.AllowedAttributes.TryGetValue(attribute, out var hosts) &&
        hosts.Contains(element, StringComparer.Ordinal);

    private static bool IsAllowedHref(string value)
    {
        var v = value.Trim();
        return v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every angle bracket and ampersand that reached a text node must have been written as an
    /// entity in the source, or the fragment would break the page it is pasted into.
    /// </summary>
    /// <remarks>
    /// A bare <c>&amp;</c> or <c>&lt;</c> already failed the parse, so this arm exists for the one
    /// character XML tolerates in text: a bare <c>&gt;</c>.
    /// </remarks>
    private static IEnumerable<string> EscapingProblems(string raw, HtmlFragmentFacts facts)
    {
        var decoded = string.Concat(facts.TextNodes);
        var needed = decoded.Count(ch => ch == '>');
        var written = CountOccurrences(raw, "&gt;");

        if (needed > written)
        {
            yield return $"{needed - written} greater-than sign(s) in the author's content were not written as &gt;";
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Format as JSON: parses, camelCase keys, no invented wrapper, ISO dates, strings stay strings.</summary>
    public static CheckResult Json(CheckContext c)
    {
        const string Name = "json-contract";

        if (c.Action.Id != "format-json")
        {
            return CheckResult.Skip(Name, Neg, "not the JSON destination");
        }

        if (c.Json is null)
        {
            return CheckResult.Fail(Name, Neg, "the answer does not parse as JSON");
        }

        var root = c.Json.RootElement;
        var problems = new List<string>();

        var badKeys = JsonWalker.Keys(root)
            .Where(k => !LowerCamelCase.IsMatch(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (badKeys.Count > 0)
        {
            problems.Add($"key(s) not lowerCamelCase: {string.Join(", ", badKeys.Take(5).Select(k => $"'{k}'"))}");
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            var properties = root.EnumerateObject().ToList();
            if (properties.Count == 1 && WrapperKeys.Contains(properties[0].Name))
            {
                problems.Add($"wrapped everything in an unjustified '{properties[0].Name}' key");
            }
        }

        var writtenDates = JsonWalker.Strings(root)
            .Where(v => WrittenDateValue.IsMatch(v) || NumericDateValue.IsMatch(v))
            .Where(v => !c.Scenario.ProtectedTokens.Contains(v.Trim(), StringComparer.Ordinal))
            .ToList();
        if (writtenDates.Count > 0)
        {
            problems.Add($"absolute date(s) not written as ISO 8601: {string.Join(", ", writtenDates.Take(3).Select(v => $"'{TextTools.Clip(v, 40)}'"))}");
        }

        // "Keep as strings any value whose written form carries meaning." A protected token that
        // arrived as a bare number lost its written form.
        var strings = JsonWalker.Strings(root).ToList();
        var numbers = JsonWalker.NumberTexts(root).ToList();
        var demoted = c.Scenario.ProtectedTokens
            .Where(t => !strings.Any(s => s.Contains(t, StringComparison.Ordinal)))
            .Where(t => numbers.Any(n => n == t || n == t.TrimStart('v', 'V')))
            .ToList();
        if (demoted.Count > 0)
        {
            problems.Add($"string-typed value(s) became JSON numbers: {string.Join(", ", demoted.Select(d => $"'{d}'"))}");
        }

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg, $"parses, {badKeys.Count} bad keys, no wrapper, dates and value types clean")
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems));
    }

    /// <summary>Format for Teams: the compose box subset, and nothing the compose box cannot render.</summary>
    public static CheckResult Teams(CheckContext c)
    {
        const string Name = "teams-contract";

        if (c.Action.Id != "format-for-teams")
        {
            return CheckResult.Skip(Name, Neg, "not the Teams destination");
        }

        var problems = new List<string>();

        // The Teams instruction explicitly offers "three backticks alone on a line to open and close
        // a code block", and what goes inside one is the author's material shown verbatim. A shell
        // comment opening with "# ", a transcript line opening with "> ", a pipeline full of pipe
        // characters and a glob are all content there, not compose-box markup, so the line-level
        // arms below grade the message AROUND the code. The parser-derived facts already ignore it.
        var output = StripCode(c.Output);

        // Same reasoning as the Markdown destination: a message ABOUT html tags is allowed to
        // mention them, and the Teams instruction's own answer for that is a code span. Only raw
        // HTML the parser sees as markup counts, and only when the selection did not already have it.
        if (c.Markup.ContainsRawHtml && !HtmlTag.IsMatch(c.Scenario.Text))
        {
            problems.Add("contains an HTML tag; Teams shows it as literal characters");
        }

        var singles = SingleAsteriskEmphasis.Matches(output)
            .Select(m => m.Groups[1].Value)
            .ToList();
        if (singles.Count > 0)
        {
            problems.Add(
                $"single-asterisk emphasis, which inverts to italic when the message is quoted elsewhere: " +
                string.Join(", ", singles.Take(3).Select(s => $"'{TextTools.Clip(s, 30)}'")));
        }

        if (AtxHeadingLine.IsMatch(output) || c.Markup.Headings.Count > 0)
        {
            problems.Add("a chat message never gets a heading");
        }

        if (BlockQuoteLine.IsMatch(output))
        {
            problems.Add("a chat message never gets a block quote");
        }

        if (c.Markup.Tables.Count > 0 || LooksLikePipeTable(output))
        {
            problems.Add("Teams has no table; a table wants one line per row with a bold label");
        }

        return problems.Count == 0
            ? CheckResult.Pass(Name, Neg, "compose-box subset only: no HTML, no heading, no quote, no table, bold uses two asterisks")
            : CheckResult.Fail(Name, Neg, string.Join("; ", problems));
    }

    private static bool LooksLikePipeTable(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Count(line => line.Count(ch => ch == '|') >= 2) >= 2;

    /// <summary>
    /// Blanks out fenced code blocks and inline code spans, keeping the line structure, so the
    /// line-level Teams arms grade the message rather than the code it is showing.
    /// </summary>
    private static string StripCode(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        var inFence = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                kept.Add(string.Empty);
                continue;
            }

            kept.Add(inFence ? string.Empty : InlineCode.Replace(line, " "));
        }

        return string.Join('\n', kept);
    }

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode { get; }
}
