using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Scribe.StyleEval.Markup;

/// <summary>
/// Reads a Markdown or Teams answer into <see cref="MarkupFacts"/> using a real CommonMark parser.
/// </summary>
/// <remarks>
/// A regex would have been quicker and wrong. "at most one bold phrase per paragraph" needs to know
/// what a paragraph is; "any list has three or more items" needs to know that four consecutive
/// "- " lines with no blank line between them are one list and not four; and an asterisk inside a
/// code span is not emphasis at all. Only a parser gets those right, so the checkers grade an AST.
/// </remarks>
internal static class MarkdownReader
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static MarkupFacts Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return MarkupFacts.Unreadable("empty answer");
        }

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(text, Pipeline);
        }
        catch (Exception ex)
        {
            return MarkupFacts.Unreadable($"CommonMark parse threw: {ex.GetType().Name}: {ex.Message}");
        }

        var state = new ReaderState();
        WalkBlocks(document, state, depth: 0);

        return new MarkupFacts(
            state.Bold,
            state.Lists,
            state.Headings,
            state.InlineCode,
            state.Fences,
            state.Tables,
            state.Links,
            state.PlainText.ToString().Trim(),
            state.ContainsRawHtml);
    }

    private sealed class ReaderState
    {
        public List<BoldSpan> Bold { get; } = [];
        public List<ListFact> Lists { get; } = [];
        public List<string> Headings { get; } = [];
        public List<string> InlineCode { get; } = [];
        public List<FenceFact> Fences { get; } = [];
        public List<TableFact> Tables { get; } = [];
        public List<string> Links { get; } = [];
        public StringBuilder PlainText { get; } = new();
        public bool ContainsRawHtml { get; set; }
        public int BlockIndex { get; set; }
    }

    private static void WalkBlocks(ContainerBlock container, ReaderState state, int depth)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    {
                        var text = Flatten(heading.Inline);
                        state.Headings.Add(text);
                        state.PlainText.Append(text).Append('\n');
                        CollectInlines(heading.Inline, state, text);
                        state.BlockIndex++;
                        break;
                    }

                case ParagraphBlock paragraph:
                    {
                        var text = Flatten(paragraph.Inline);
                        state.PlainText.Append(text).Append('\n');

                        var firstSpan = state.Bold.Count;
                        CollectInlines(paragraph.Inline, state, text);

                        // A Teams message carrying a table writes one line per row with a bold label
                        // at the front of each line, and Markdig models those hard-wrapped lines as
                        // ONE paragraph. Counting bold per paragraph would report four correct
                        // labels as four violations of "one bold phrase per paragraph", so each line
                        // of a paragraph becomes its own block for emphasis purposes.
                        SplitParagraphIntoLines(state, firstSpan, text);
                        state.BlockIndex++;
                        break;
                    }

                case ListBlock list:
                    {
                        state.Lists.Add(new ListFact(list.Count, list.IsOrdered, depth));
                        foreach (var item in list)
                        {
                            if (item is ContainerBlock itemBlock)
                            {
                                WalkBlocks(itemBlock, state, depth + 1);
                            }
                        }

                        break;
                    }

                case QuoteBlock quote:
                    WalkBlocks(quote, state, depth);
                    break;

                case Table table:
                    {
                        var rows = table.Count;
                        var columns = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
                        state.Tables.Add(new TableFact(rows, columns));
                        foreach (var row in table.OfType<TableRow>())
                        {
                            foreach (var cell in row.OfType<TableCell>())
                            {
                                WalkBlocks(cell, state, depth + 1);
                            }
                        }

                        break;
                    }

                case FencedCodeBlock fenced:
                    {
                        var content = fenced.Lines.ToString();
                        state.Fences.Add(new FenceFact(fenced.Info ?? string.Empty, content));
                        state.PlainText.Append(content).Append('\n');
                        state.BlockIndex++;
                        break;
                    }

                case CodeBlock code:
                    {
                        var content = code.Lines.ToString();
                        state.Fences.Add(new FenceFact(string.Empty, content));
                        state.PlainText.Append(content).Append('\n');
                        state.BlockIndex++;
                        break;
                    }

                case HtmlBlock:
                    state.ContainsRawHtml = true;
                    state.BlockIndex++;
                    break;

                case ContainerBlock nested:
                    WalkBlocks(nested, state, depth);
                    break;

                default:
                    state.BlockIndex++;
                    break;
            }
        }
    }

    private static void CollectInlines(ContainerInline? container, ReaderState state, string blockText)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case EmphasisInline emphasis:
                    {
                        var text = Flatten(emphasis);

                        // Two asterisks or two underscores is bold; one is italic. Three is both, and
                        // Markdig models it as nested emphasis, so the outer run still counts.
                        if (emphasis.DelimiterCount >= 2)
                        {
                            var start = blockText.IndexOf(text, StringComparison.Ordinal);
                            state.Bold.Add(new BoldSpan(text, state.BlockIndex, blockText, start));
                        }

                        CollectInlines(emphasis, state, blockText);
                        break;
                    }

                case CodeInline code:
                    state.InlineCode.Add(code.Content);
                    break;

                case LinkInline link:
                    if (!string.IsNullOrEmpty(link.Url))
                    {
                        state.Links.Add(link.Url);
                    }

                    CollectInlines(link, state, blockText);
                    break;

                case HtmlInline:
                    // A character entity is not a tag, so HtmlEntityInline deliberately does not
                    // count: "&amp;" in a Markdown answer is escaping, not HTML markup.
                    state.ContainsRawHtml = true;
                    break;

                case ContainerInline nested:
                    CollectInlines(nested, state, blockText);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-homes the bold runs found in one paragraph onto the LINE each of them sits on, so
    /// "at most one bold phrase per paragraph" is counted the way a reader would count it.
    /// </summary>
    private static void SplitParagraphIntoLines(ReaderState state, int firstSpan, string paragraphText)
    {
        if (firstSpan >= state.Bold.Count || !paragraphText.Contains('\n'))
        {
            return;
        }

        var lines = paragraphText.Split('\n');
        for (var i = firstSpan; i < state.Bold.Count; i++)
        {
            var span = state.Bold[i];
            var lineIndex = Array.FindIndex(lines, l => l.Contains(span.Text, StringComparison.Ordinal));
            if (lineIndex < 0)
            {
                continue;
            }

            var line = lines[lineIndex];
            state.Bold[i] = span with
            {
                // Reserve a thousand line slots per block, which no real answer approaches, so the
                // composed key stays unique across blocks without a second field.
                BlockIndex = (span.BlockIndex * 1000) + lineIndex,
                BlockText = line,
                StartInBlock = line.IndexOf(span.Text, StringComparison.Ordinal),
            };
        }
    }

    /// <summary>Renders an inline tree to plain text, dropping the markup.</summary>
    internal static string Flatten(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        Append(container, sb);
        return sb.ToString();

        static void Append(ContainerInline node, StringBuilder sb)
        {
            foreach (var inline in node)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        sb.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case LineBreakInline:
                        // A newline rather than a space, so a hard-wrapped paragraph can be split
                        // back into the lines the author actually wrote.
                        sb.Append('\n');
                        break;
                    case AutolinkInline autolink:
                        sb.Append(autolink.Url);
                        break;
                    case ContainerInline nested:
                        Append(nested, sb);
                        break;
                }
            }
        }
    }
}
