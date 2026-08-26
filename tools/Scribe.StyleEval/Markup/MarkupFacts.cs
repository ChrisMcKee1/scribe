namespace Scribe.StyleEval.Markup;

/// <summary>One bold (or strong) run, and which block it sits in.</summary>
/// <param name="Text">The emphasised text with markup removed.</param>
/// <param name="BlockIndex">Index of the enclosing paragraph or list item.</param>
/// <param name="BlockText">Full plain text of that block, so "is this the whole line" is decidable.</param>
/// <param name="StartInBlock">Character offset of the run inside <paramref name="BlockText"/>, or -1.</param>
internal sealed record BoldSpan(string Text, int BlockIndex, string BlockText, int StartInBlock)
{
    /// <summary>Words in the run, by whitespace.</summary>
    public int WordCount => Text.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>True when the run swallowed the entire block it lives in.</summary>
    public bool IsWholeBlock =>
        string.Equals(Text.Trim(), BlockText.Trim(), StringComparison.Ordinal) ||
        (BlockText.Trim().Length > 0 && Text.Trim().Length >= BlockText.Trim().Length - 1);

    /// <summary>True when the run reads as a complete sentence rather than a marked phrase.</summary>
    public bool LooksLikeSentence
    {
        get
        {
            var t = Text.Trim();
            return t.Length > 0 && (t[^1] is '.' or '?' or '!') && WordCount > 4;
        }
    }
}

/// <summary>One list, flattened. Nested lists are reported separately with their own depth.</summary>
internal sealed record ListFact(int ItemCount, bool Ordered, int Depth);

/// <summary>One table.</summary>
internal sealed record TableFact(int RowCount, int ColumnCount);

/// <summary>One fenced code block.</summary>
internal sealed record FenceFact(string Language, string Content);

/// <summary>
/// The structural shape of a model answer, normalised across destinations so one set of checkers can
/// grade Markdown, Teams and HTML without knowing which it is looking at.
/// </summary>
/// <param name="Bold">Emphasised runs, in document order.</param>
/// <param name="Lists">Every list, with its item count.</param>
/// <param name="Headings">Heading text, in document order.</param>
/// <param name="InlineCode">Inline code runs.</param>
/// <param name="Fences">Fenced or preformatted blocks.</param>
/// <param name="Tables">Tables.</param>
/// <param name="Links">Link targets.</param>
/// <param name="PlainText">The answer with markup removed.</param>
/// <param name="ContainsRawHtml">True when raw HTML appears where it should not.</param>
/// <param name="ParseError">Non-null when the answer could not be parsed at all.</param>
internal sealed record MarkupFacts(
    IReadOnlyList<BoldSpan> Bold,
    IReadOnlyList<ListFact> Lists,
    IReadOnlyList<string> Headings,
    IReadOnlyList<string> InlineCode,
    IReadOnlyList<FenceFact> Fences,
    IReadOnlyList<TableFact> Tables,
    IReadOnlyList<string> Links,
    string PlainText,
    bool ContainsRawHtml = false,
    string? ParseError = null)
{
    /// <summary>An answer nothing could be read out of. Every structural checker abstains.</summary>
    public static MarkupFacts Unreadable(string error) =>
        new([], [], [], [], [], [], [], string.Empty, false, error);

    /// <summary>Every string that counts as "in code formatting" for this answer.</summary>
    public IEnumerable<string> AllCode => InlineCode.Concat(Fences.Select(f => f.Content));
}
