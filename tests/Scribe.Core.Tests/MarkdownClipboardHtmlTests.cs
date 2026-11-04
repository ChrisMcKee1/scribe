using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.TextInjection;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The HTML "Markdown with formatting" adds to the clipboard: rendered only for text with Markdown structure, never
/// changing a character the text keeps literally, and encoded as CF_HTML with byte offsets that point at the fragment.
/// </summary>
public sealed class MarkdownClipboardHtmlTests
{
    [Theory]
    [InlineData("Just a sentence.")]
    [InlineData("Two lines\nof plain prose.")]
    [InlineData("Paragraph one.\n\nParagraph two.")]
    [InlineData("See https://example.com for details.")]
    [InlineData("Five * three * two is thirty.")]
    [InlineData("snake_case_name and file_name.txt")]
    [InlineData("The path C:\\Users\\me\\.copilot\\config.json is fine.")]
    [InlineData("AT&T and &copy; stay as typed.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_without_markdown_structure_gets_no_html(string text) =>
        Assert.Null(MarkdownClipboardHtml.TryRender(text));

    [Theory]
    [InlineData("- one\n- two", "<ul>", "<li>one</li>")]
    [InlineData("1. first\n2. second", "<ol>", "<li>first</li>")]
    [InlineData("**Bold** words", "<strong>Bold</strong>", null)]
    [InlineData("Some *italic* words", "<em>italic</em>", null)]
    [InlineData("Some ~~gone~~ words", "<del>gone</del>", null)]
    [InlineData("Run `dotnet test` now", "<code>dotnet test</code>", null)]
    [InlineData("# Heading", "<h1", "Heading</h1>")]
    [InlineData("> A quoted line", "<blockquote>", "A quoted line")]
    [InlineData("A [link](https://example.com) here", "<a href=\"https://example.com\">link</a>", null)]
    [InlineData("```python\nprint(\"hello\")\n```", "<pre><code class=\"language-python\">", "print(&quot;hello&quot;)")]
    [InlineData("| Name | Value |\n| --- | --- |\n| a | 1 |", "<table>", "<td>a</td>")]
    [InlineData("- [ ] Open task\n- [x] Done task", "type=\"checkbox\"", "Done task")]
    public void Markdown_structure_renders_as_html(string text, string expected, string? alsoExpected)
    {
        var html = MarkdownClipboardHtml.TryRender(text);

        Assert.NotNull(html);
        Assert.Contains(expected, html, StringComparison.Ordinal);
        if (alsoExpected is not null)
        {
            Assert.Contains(alsoExpected, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nested_bullets_render_as_a_nested_list()
    {
        var html = MarkdownClipboardHtml.TryRender("- First\n- Second\n  - Nested")!;

        Assert.Matches(new Regex("<li>Second\\s*<ul>\\s*<li>Nested</li>", RegexOptions.CultureInvariant), html);
    }

    [Fact]
    public void Every_line_break_stays_a_line_break()
    {
        // CommonMark renders a single newline inside a paragraph as a space; a dictation's lines must not run together.
        var html = MarkdownClipboardHtml.TryRender("- item\n\nLine one\nLine two")!;

        Assert.Matches(new Regex("Line one<br\\s*/?>\\s*Line two", RegexOptions.CultureInvariant), html);
    }

    [Fact]
    public void A_windows_path_keeps_every_backslash()
    {
        // CommonMark's escapes would turn "\." into "." (github/app#2784 reported exactly that for pasted paths).
        var html = MarkdownClipboardHtml.TryRender("- Open C:\\Users\\me\\.copilot\\config.json and C:\\temp\\_draft")!;

        Assert.Contains("C:\\Users\\me\\.copilot\\config.json", html, StringComparison.Ordinal);
        Assert.Contains("C:\\temp\\_draft", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Entities_stay_as_typed()
    {
        var html = MarkdownClipboardHtml.TryRender("- AT&T and &copy; and &#169;")!;

        Assert.Contains("AT&amp;T", html, StringComparison.Ordinal);
        Assert.Contains("&amp;copy;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;#169;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\u00A9", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Dictated_markup_is_escaped_text_never_markup()
    {
        var html = MarkdownClipboardHtml.TryRender("- <script>alert(1)</script>\n- <b>bold?</b>\n\n<div>block</div>")!;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;div&gt;block&lt;/div&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_web_address_is_linked_inside_structured_text()
    {
        var html = MarkdownClipboardHtml.TryRender("- See https://example.com/path for details")!;

        Assert.Contains("<a href=\"https://example.com/path\">https://example.com/path</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_with_a_link_reference_definition_gets_no_html()
    {
        // The definition renders as nothing, so the HTML would drop a dictated line.
        Assert.Null(MarkdownClipboardHtml.TryRender("- See [the notes][1]\n\n[1]: https://example.com"));
    }

    [Fact]
    public void Text_outside_markup_reads_the_same_in_the_html()
    {
        const string text = "Intro line\n- First item with AT&T\n- Second item, 5 < 6\n\nClosing line";
        var html = MarkdownClipboardHtml.TryRender(text)!;

        var visible = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        foreach (var word in new[] { "Intro line", "First item with AT&T", "Second item, 5 < 6", "Closing line" })
        {
            Assert.Contains(word, visible, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_pipeline_parses_no_backslash_escapes_entities_or_raw_html()
    {
        // Pinned so a Markdig bump that brings a parser back, or renames one, fails here first.
        var parsers = MarkdownClipboardHtml.InlineParserNames;

        Assert.DoesNotContain("EscapeInlineParser", parsers);
        Assert.DoesNotContain("HtmlEntityParser", parsers);
        Assert.Contains("EmphasisInlineParser", parsers);
        Assert.Contains("CodeInlineParser", parsers);
        Assert.Contains("LinkInlineParser", parsers);
        Assert.Contains("LineBreakInlineParser", parsers);
    }

    [Fact]
    public void Deep_nesting_is_bounded_and_never_throws()
    {
        var text = string.Concat(Enumerable.Repeat("> ", 500)) + "deep";

        var exception = Record.Exception(() => MarkdownClipboardHtml.TryRender(text));

        Assert.Null(exception);
    }

    // --- CF_HTML ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<p>plain ascii</p>")]
    [InlineData("<p>caf\u00E9 and na\u00EFve</p>")]
    [InlineData("<ul><li>emoji \uD83D\uDE00 and CJK \u4E2D\u6587</li></ul>")]
    [InlineData("")]
    public void Cf_html_offsets_point_at_the_fragment_in_utf8_bytes(string fragment)
    {
        var data = ClipboardHtmlFormat.Encode(fragment)!;

        var header = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 200));
        int Offset(string name) =>
            int.Parse(Regex.Match(header, name + ":(\\d{10})", RegexOptions.CultureInvariant).Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.StartsWith("Version:0.9\r\n", header, StringComparison.Ordinal);
        int startHtml = Offset("StartHTML");
        int endHtml = Offset("EndHTML");
        int startFragment = Offset("StartFragment");
        int endFragment = Offset("EndFragment");

        Assert.Equal(fragment, Encoding.UTF8.GetString(data, startFragment, endFragment - startFragment));
        var html = Encoding.UTF8.GetString(data, startHtml, endHtml - startHtml);
        Assert.StartsWith("<html>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>", html, StringComparison.Ordinal);
        Assert.Contains("<!--StartFragment-->" + fragment + "<!--EndFragment-->", html, StringComparison.Ordinal);
        Assert.Equal(data.Length - 1, endHtml);
        Assert.Equal(0, data[^1]);
    }

    [Fact]
    public void A_fragment_that_is_not_valid_unicode_gets_no_cf_html()
    {
        var loneSurrogate = "<p>" + new string('\uD83D', 1) + "</p>";

        Assert.Null(ClipboardHtmlFormat.Encode(loneSurrogate));
    }
}
