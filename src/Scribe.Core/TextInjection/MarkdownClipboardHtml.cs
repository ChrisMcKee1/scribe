using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Renders a dictation's Markdown to the HTML fragment that "Markdown with formatting" puts on the clipboard beside the
/// Markdown text. Apps that read HTML but leave pasted Markdown as typed (Teams, Microsoft Copilot, Codex; measured
/// 2026-11-04) then show real lists, emphasis, links, code and tables, while an app that reads only text gets the same
/// Markdown as before.
/// </summary>
/// <remarks>
/// <para>
/// The rendering never changes a character the text keeps literally. Backslash escapes and HTML entities are not
/// parsed, so a dictated <c>C:\Users\me\.config</c> or <c>&amp;copy;</c> reads exactly as dictated (CommonMark would turn
/// <c>\.</c> into <c>.</c>, the backslash loss github/app#2784 reported for pasted paths); raw HTML is not parsed either,
/// so <c>&lt;script&gt;</c> in a dictation is escaped text, never markup. Every line break stays a line break: a single
/// newline renders as <c>&lt;br&gt;</c> rather than CommonMark's space.
/// </para>
/// <para>
/// Text without Markdown structure (plain sentences, with or without a bare web address) gets no HTML at all, so plain
/// prose pastes exactly as it did before this format existed. A link reference definition (<c>[1]: https://...</c>)
/// renders as nothing, which would drop a dictated line from the HTML, so text holding one gets no HTML either.
/// </para>
/// </remarks>
internal static class MarkdownClipboardHtml
{
    // Deep enough for any list a person dictates, and a bound on a pathological input's work.
    private const int MaximumNestingDepth = 32;

    private static readonly (MarkdownPipeline Pipeline, string[] InlineParsers) Built = BuildPipeline();

    private static MarkdownPipeline Pipeline => Built.Pipeline;

    /// <summary>The inline parsers the pipeline runs, by type name, for the test that pins them across a Markdig bump.</summary>
    internal static IReadOnlyList<string> InlineParserNames => Built.InlineParsers;

    /// <summary>
    /// The HTML fragment for <paramref name="markdown"/>, or null when it has no Markdown structure worth rendering or
    /// would lose a line in the rendering.
    /// </summary>
    public static string? TryRender(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            if (!HasStructure(document))
            {
                return null;
            }

            var html = Markdown.ToHtml(document, Pipeline).TrimEnd();
            return html.Length == 0 ? null : html;
        }
        catch (Exception)
        {
            // The HTML is an addition to the Markdown text, which pastes on its own. A parser fault must not cost the
            // dictation its paste, so it costs only the formatting.
            return null;
        }
    }

    private static (MarkdownPipeline Pipeline, string[] InlineParsers) BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder
        {
            MaximumNestingDepth = MaximumNestingDepth,
        };

        builder
            .DisableHtml()
            .UseSoftlineBreakAsHardlineBreak()
            .UsePipeTables()
            .UseTaskLists()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseAutoLinks();

        // Literal characters stay literal (see the class remarks).
        builder.InlineParsers.TryRemove<EscapeInlineParser>();
        builder.InlineParsers.TryRemove<HtmlEntityParser>();
        var pipeline = builder.Build();

        // Read after Build, which is when the extensions add their parsers.
        return (pipeline, [.. builder.InlineParsers.Select(parser => parser.GetType().Name)]);
    }

    private static bool HasStructure(MarkdownDocument document)
    {
        bool structured = false;
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case LinkReferenceDefinitionGroup or LinkReferenceDefinition:
                    return false;
                case MarkdownDocument or ParagraphBlock or LiteralInline or LineBreakInline:
                    break;
                case ContainerInline and not LinkInline and not EmphasisInline:
                    // The paragraph's own inline container.
                    break;
                case LinkInline { IsAutoLink: true }:
                    // A bare web address is linked inside rendered HTML, but alone it is no reason to add HTML: apps link
                    // pasted addresses themselves.
                    break;
                default:
                    structured = true;
                    break;
            }
        }

        return structured;
    }
}
