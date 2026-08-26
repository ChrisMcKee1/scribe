using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Scribe.StyleEval.Markup;

/// <summary>One element as it actually appeared in the answer.</summary>
/// <param name="Name">Lower-cased tag name.</param>
/// <param name="Attributes">Attribute name (lower-cased) to value, in source order.</param>
internal sealed record HtmlElement(string Name, IReadOnlyList<KeyValuePair<string, string>> Attributes);

/// <summary>
/// A parsed HTML fragment: every element, every attribute, every text node, and whether the whole
/// thing was well formed.
/// </summary>
internal sealed record HtmlFragmentFacts(
    bool WellFormed,
    string? ParseError,
    IReadOnlyList<HtmlElement> Elements,
    IReadOnlyList<string> TextNodes,
    IReadOnlyList<string> CodeTexts,
    IReadOnlyList<BoldSpan> Bold,
    IReadOnlyList<ListFact> Lists,
    IReadOnlyList<string> Headings,
    IReadOnlyList<TableFact> Tables,
    IReadOnlyList<string> Links,
    string PlainText)
{
    /// <summary>Facts as the shared structural checkers want them.</summary>
    public MarkupFacts ToMarkupFacts() => WellFormed
        ? new MarkupFacts(
            Bold, Lists, Headings, CodeTexts, [], Tables, Links, PlainText)
        : MarkupFacts.Unreadable(ParseError ?? "not well formed");
}

/// <summary>
/// Parses an HTML fragment STRICTLY, as XML, after normalising the two void elements the shipping
/// instruction permits.
/// </summary>
/// <remarks>
/// <para>
/// A forgiving HTML parser is exactly the wrong tool here. HtmlAgilityPack and AngleSharp both
/// repair what they read: an unescaped ampersand becomes text, an unclosed tag gets closed, and the
/// checker then reports a clean bill of health for output that would break the page it was pasted
/// into. The shipping instruction promises a fragment whose ampersands and angle brackets are
/// escaped and whose elements are balanced, so the check has to be intolerant to mean anything.
/// </para>
/// <para>
/// Parsing as XML buys three of the required checks for free: unbalanced or mis-nested elements
/// fail, a bare <c>&amp;</c> fails as a bad entity reference, and a bare <c>&lt;</c> in author
/// content fails as a malformed tag. Named HTML entities such as <c>&amp;nbsp;</c> also fail, which
/// is correct: the instruction lists the escapes it wants and that is not one of them.
/// </para>
/// </remarks>
internal static partial class HtmlFragment
{
    /// <summary>Elements the action's instruction permits.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "h2", "h3", "h4", "ul", "ol", "li", "dl", "dt", "dd", "strong", "em", "code",
        "pre", "blockquote", "a", "table", "thead", "tbody", "tr", "th", "td", "br", "hr",
    };

    /// <summary>Elements the instruction forbids by name, checked on the raw text as well.</summary>
    public static readonly IReadOnlyList<string> Forbidden =
        ["script", "style", "iframe", "object", "embed", "form", "input", "svg", "template"];

    /// <summary>The only attributes permitted anywhere, and where.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedAttributes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["href"] = ["a"],
            ["colspan"] = ["th", "td"],
            ["rowspan"] = ["th", "td"],
        };

    private const string RootName = "styleEvalFragmentRoot";

    [GeneratedRegex(@"<\s*(br|hr)(\s[^<>]*?)?\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex VoidElement { get; }

    [GeneratedRegex(@"<!DOCTYPE", RegexOptions.IgnoreCase)]
    public static partial Regex Doctype { get; }

    [GeneratedRegex(@"<\s*/?\s*(html|head|body)\b", RegexOptions.IgnoreCase)]
    public static partial Regex DocumentShell { get; }

    [GeneratedRegex(@"<!--")]
    public static partial Regex Comment { get; }

    [GeneratedRegex(@"<\s*/?\s*(script|style|iframe|object|embed|form|input|svg|template)\b", RegexOptions.IgnoreCase)]
    public static partial Regex ForbiddenElement { get; }

    /// <summary>
    /// An "on" attribute inside an actual tag.
    /// </summary>
    /// <remarks>
    /// The tag context is what makes this usable. A selection about web security legitimately quotes
    /// <c>onerror="..."</c> as CONTENT, and the correct fragment shows it inside a code element or
    /// with its angle brackets escaped, where it is text and not an attribute at all. A bare
    /// <c>\son[a-zA-Z]+\s*=</c> matches that correct answer, and matches ordinary prose such as
    /// "one = 1" as well. Restricting it to characters that follow a tag name and precede the
    /// closing bracket keeps the hostile case caught and the quoted case clean.
    /// </remarks>
    [GeneratedRegex(@"<\s*[a-zA-Z][a-zA-Z0-9]*[^<>]*\son[a-zA-Z]+\s*=", RegexOptions.IgnoreCase)]
    public static partial Regex EventHandlerAttribute { get; }

    public static HtmlFragmentFacts Parse(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return Failed("empty answer");
        }

        // <br> and <hr> are legal HTML and illegal XML. Normalise only those two: anything else that
        // arrives unclosed is a real defect and must be allowed to fail the parse.
        var normalized = VoidElement.Replace(fragment, m => $"<{m.Groups[1].Value.ToLowerInvariant()}/>");
        var wrapped = $"<{RootName}>{normalized}</{RootName}>";

        var elements = new List<HtmlElement>();
        var textNodes = new List<string>();
        var codeTexts = new List<string>();
        var bold = new List<BoldSpan>();
        var lists = new List<ListFact>();
        var headings = new List<string>();
        var tables = new List<TableFact>();
        var links = new List<string>();
        var plain = new StringBuilder();

        // Block bookkeeping so "one bold per paragraph" is answerable. A block is a p, li, h*, dd,
        // blockquote or table cell; text accumulates into the open block.
        var blockIndex = -1;
        var blockText = new StringBuilder();
        var openBold = new Stack<(int Block, StringBuilder Text)>();
        var listStack = new Stack<(bool Ordered, int Items, int Depth)>();
        var tableStack = new Stack<(int Rows, int Columns, int CellsInRow)>();
        var codeDepth = 0;
        var codeText = new StringBuilder();
        var pendingBold = new List<(string Text, int Block)>();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            ConformanceLevel = ConformanceLevel.Document,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false,
        };

        try
        {
            using var stringReader = new StringReader(wrapped);
            using var reader = XmlReader.Create(stringReader, settings);

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        {
                            var name = reader.Name.ToLowerInvariant();
                            if (string.Equals(name, RootName, StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

                            var attributes = new List<KeyValuePair<string, string>>();
                            if (reader.HasAttributes)
                            {
                                while (reader.MoveToNextAttribute())
                                {
                                    attributes.Add(new KeyValuePair<string, string>(
                                        reader.Name.ToLowerInvariant(), reader.Value));
                                }

                                reader.MoveToElement();
                            }

                            elements.Add(new HtmlElement(name, attributes));

                            if (name == "a")
                            {
                                var href = attributes.FirstOrDefault(a => a.Key == "href").Value;
                                if (!string.IsNullOrEmpty(href))
                                {
                                    links.Add(href);
                                }
                            }

                            var empty = reader.IsEmptyElement;

                            if (IsBlock(name))
                            {
                                FlushBlock(pendingBold, bold, blockIndex, blockText);
                                blockIndex++;
                                blockText.Clear();
                                if (name is "h2" or "h3" or "h4")
                                {
                                    headings.Add(string.Empty);
                                }
                            }

                            if (name is "ul" or "ol" && !empty)
                            {
                                listStack.Push((name == "ol", 0, listStack.Count));
                            }

                            if (name == "li" && listStack.Count > 0)
                            {
                                var top = listStack.Pop();
                                listStack.Push((top.Ordered, top.Items + 1, top.Depth));
                            }

                            if (name == "table" && !empty)
                            {
                                tableStack.Push((0, 0, 0));
                            }

                            if (name == "tr" && tableStack.Count > 0)
                            {
                                var top = tableStack.Pop();
                                tableStack.Push((top.Rows + 1, top.Columns, 0));
                            }

                            if (name is "th" or "td" && tableStack.Count > 0)
                            {
                                var top = tableStack.Pop();
                                var cells = top.CellsInRow + 1;
                                tableStack.Push((top.Rows, Math.Max(top.Columns, cells), cells));
                            }

                            if (name is "code" or "pre")
                            {
                                if (codeDepth == 0)
                                {
                                    codeText.Clear();
                                }

                                codeDepth++;
                            }

                            if (name == "strong" && !empty)
                            {
                                openBold.Push((blockIndex, new StringBuilder()));
                            }

                            if (empty)
                            {
                                CloseElement(name, listStack, lists, tableStack, tables, ref codeDepth, codeText, codeTexts, openBold, pendingBold);
                            }

                            break;
                        }

                    case XmlNodeType.EndElement:
                        {
                            var name = reader.Name.ToLowerInvariant();
                            if (string.Equals(name, RootName, StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

                            CloseElement(name, listStack, lists, tableStack, tables, ref codeDepth, codeText, codeTexts, openBold, pendingBold);

                            if (name is "h2" or "h3" or "h4" && headings.Count > 0)
                            {
                                headings[^1] = blockText.ToString().Trim();
                            }

                            if (IsBlock(name))
                            {
                                FlushBlock(pendingBold, bold, blockIndex, blockText);
                                plain.Append(blockText).Append('\n');
                                blockText.Clear();
                            }

                            break;
                        }

                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.CDATA:
                        {
                            var value = reader.Value;
                            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                            {
                                textNodes.Add(value);
                            }

                            blockText.Append(value);
                            if (codeDepth > 0)
                            {
                                codeText.Append(value);
                            }

                            foreach (var (_, sb) in openBold)
                            {
                                sb.Append(value);
                            }

                            break;
                        }

                    case XmlNodeType.Comment:
                        return Failed("the fragment contains an HTML comment, which the instruction forbids");
                }
            }
        }
        catch (XmlException ex)
        {
            return Failed($"not well formed as an escaped fragment: {ex.Message.Trim()}");
        }

        FlushBlock(pendingBold, bold, blockIndex, blockText);
        plain.Append(blockText);

        // Attach each bold run to the finished text of the block it closed in.
        var resolved = bold
            .Select(b => b with { StartInBlock = b.BlockText.IndexOf(b.Text, StringComparison.Ordinal) })
            .ToList();

        return new HtmlFragmentFacts(
            WellFormed: true,
            ParseError: null,
            Elements: elements,
            TextNodes: textNodes,
            CodeTexts: codeTexts,
            Bold: resolved,
            Lists: lists,
            Headings: headings,
            Tables: tables,
            Links: links,
            PlainText: plain.ToString().Trim());
    }

    private static void CloseElement(
        string name,
        Stack<(bool Ordered, int Items, int Depth)> listStack,
        List<ListFact> lists,
        Stack<(int Rows, int Columns, int CellsInRow)> tableStack,
        List<TableFact> tables,
        ref int codeDepth,
        StringBuilder codeText,
        List<string> codeTexts,
        Stack<(int Block, StringBuilder Text)> openBold,
        List<(string Text, int Block)> pendingBold)
    {
        if (name is "ul" or "ol" && listStack.Count > 0)
        {
            var top = listStack.Pop();
            lists.Add(new ListFact(top.Items, top.Ordered, top.Depth));
        }

        if (name == "table" && tableStack.Count > 0)
        {
            var top = tableStack.Pop();
            tables.Add(new TableFact(top.Rows, top.Columns));
        }

        if (name is "code" or "pre" && codeDepth > 0)
        {
            codeDepth--;
            if (codeDepth == 0)
            {
                codeTexts.Add(codeText.ToString());
                codeText.Clear();
            }
        }

        if (name == "strong" && openBold.Count > 0)
        {
            var (block, text) = openBold.Pop();
            pendingBold.Add((text.ToString(), block));
        }
    }

    private static void FlushBlock(
        List<(string Text, int Block)> pending,
        List<BoldSpan> bold,
        int blockIndex,
        StringBuilder blockText)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var text = blockText.ToString();
        foreach (var (spanText, block) in pending)
        {
            bold.Add(new BoldSpan(
                spanText,
                block < 0 ? blockIndex : block,
                text,
                text.IndexOf(spanText, StringComparison.Ordinal)));
        }

        pending.Clear();
    }

    private static bool IsBlock(string name) =>
        name is "p" or "li" or "h2" or "h3" or "h4" or "dt" or "dd" or "blockquote" or "th" or "td";

    private static HtmlFragmentFacts Failed(string error) =>
        new(false, error, [], [], [], [], [], [], [], [], string.Empty);
}
