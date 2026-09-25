namespace Scribe.Core.Settings;

/// <summary>
/// How the Word packs tab (the libraries page) is composed. Exactly one of <see cref="SideBySide"/> and
/// <see cref="Stacked"/>, and <see cref="Short"/> or not.
/// </summary>
[Flags]
public enum LibraryLayoutComposition
{
    /// <summary>The word pack list and the selected word pack's card side by side.</summary>
    SideBySide = 1,

    /// <summary>The list on its own page; Enter or a click on a name opens the card at full width, with Back to word packs.</summary>
    Stacked = 2,

    /// <summary>
    /// For a short work area (UX NEW-01): the subtitle behind an information button, the description in Details, Term
    /// details as a subpage, at least four grid rows, the footer pinned.
    /// </summary>
    Short = 4,
}

/// <summary>What the Word packs tab measures: the Settings window it is shown in, and the text size.</summary>
/// <param name="Width">The window's width in DIPs.</param>
/// <param name="Height">The window's height in DIPs.</param>
/// <param name="TextScale">Windows' text size setting as a factor, 1 to 2.25 (values below 1 count as 1).</param>
/// <param name="NoticeVisible">An InfoBar notice is shown below the card header.</param>
/// <param name="TermDetailsOpen">Term details is open under the grid.</param>
public sealed record LibraryLayoutInput(
    double Width, double Height, double TextScale = 1, bool NoticeVisible = false, bool TermDetailsOpen = false);

/// <summary>The Word packs tab's layout for one <see cref="LibraryLayoutInput"/>.</summary>
/// <param name="Composition">How the tab is composed.</param>
/// <param name="ListWidth">The word pack list's width: its pane beside the card, or the whole tab when stacked.</param>
/// <param name="UseColumnWidth">The terms grid's Use check box column.</param>
/// <param name="SpokenColumnWidth">The Spoken column.</param>
/// <param name="WrittenColumnWidth">The Written column.</param>
/// <param name="ActionColumnWidth">The row action column.</param>
/// <param name="VisibleTermRows">How many term rows the grid shows without scrolling; at least four.</param>
/// <param name="HorizontalOverflow">
/// Even stacked, the text columns cannot keep their minimum width, so the grid keeps it and scrolls sideways: the real
/// overflow fallback (review finding R14).
/// </param>
/// <param name="VerticalOverflow">
/// Even the short composition cannot show four term rows, so the grid keeps four and the rest of the page scrolls.
/// </param>
public sealed record LibraryLayout(
    LibraryLayoutComposition Composition,
    double ListWidth,
    double UseColumnWidth,
    double SpokenColumnWidth,
    double WrittenColumnWidth,
    double ActionColumnWidth,
    int VisibleTermRows,
    bool HorizontalOverflow,
    bool VerticalOverflow)
{
    /// <summary>Whether the list and the card are side by side.</summary>
    public bool SideBySide => Composition.HasFlag(LibraryLayoutComposition.SideBySide);

    /// <summary>Whether the short composition applies.</summary>
    public bool Short => Composition.HasFlag(LibraryLayoutComposition.Short);
}

/// <summary>
/// Decides the Word packs tab's composition from the measured window and text size (plan 3.11, disagreement 2): side by
/// side while both text columns keep 150 DIPs at the current text size, stacked below that; short when the normal
/// composition would show fewer than six term rows, keeping at least four.
/// </summary>
/// <remarks>
/// <para>
/// The fixed geometry is the Settings window's (the 232 DIP navigation rail, the content margins of 12 and 24 DIPs, the
/// 32 DIP title bar and the footer) and the tab's planned one (a 220 DIP list pane, 12 DIPs between panes, 16 inside the
/// card, a 40 DIP Use and action column). Word packs is the second tab of the Dictionary page (Your words, Word packs),
/// so a 40 DIP tab strip sits above the card, under the page title, subtitle and commands, at every window size and text
/// size. Everything that holds text grows with the text size, the tab strip included; the minimum a text column must
/// keep does too, so large text gets the stacked fallback. At 940 x 660 and 100% text each text column gets 164 DIPs, so
/// the minimum window stays side by side, and the tab strip leaves the normal composition five rows there, so it takes
/// the short one, which shows seven. The window clamps its minimum size of 940 x 660 to the monitor's work area, which is
/// where the short composition matters most: 1920 x 1080 at 175% (about 1097 x 569 DIPs), 1366 x 768 at 125%
/// (1092 x 566) and 1920 x 1080 at 200% (960 x 492).
/// </para>
/// <para>
/// No threshold here comes from a guideline number; the shell measures, this decides, and the offscreen renders check
/// the extents it promises: the shell draws the tab strip at 40 DIPs times the text scale, as this assumes. Pure.
/// </para>
/// </remarks>
public static class LibraryLayoutPlanner
{
    /// <summary>The width both text columns must keep, in DIPs at 100% text, for the panes to stay side by side.</summary>
    public const double MinimumTextColumn = 150;

    /// <summary>The fewest term rows the normal composition may show before the short one is used.</summary>
    public const int MinimumNormalRows = 6;

    /// <summary>The fewest term rows the grid ever shows.</summary>
    public const int MinimumRows = 4;

    private const double RailWidth = 232;
    private const double ContentHorizontalMargin = 12 + 24;
    private const double ListPaneWidth = 220;
    private const double MaxListPaneWidth = 320;
    private const double PaneGap = 12;
    private const double CardPadding = 16;
    private const double UseColumn = 40;
    private const double ActionColumn = 40;

    // The Dictionary page's tab strip (Your words, Word packs), above the card; it grows with the text size.
    private const double TabStrip = 40;

    private const double TitleBar = 32;
    private const double ContentVerticalMargin = 4 + 16;
    private const double ButtonHeight = 32;
    private const double FooterGap = 16;
    private const double PageTitle = 28;
    private const double PageHeaderMargins = 8 + 14;
    private const double SubtitleLines = 2;
    private const double TextLine = 20;
    private const double CommandsGap = 10;
    private const double CardHeading = 28;
    private const double CardMeta = 16;
    private const double CardGaps = 8 + 8;
    private const double NoticeHeight = 48;
    private const double NoticeGap = 8;
    private const double ToolbarGap = 8;
    private const double GridHeader = 32;
    private const double RowPadding = 12;
    private const double TermDetailsHeight = 180;

    /// <summary>The layout for <paramref name="input"/>, with the Dictionary page's tab strip above the card.</summary>
    public static LibraryLayout Plan(LibraryLayoutInput input) => Plan(input, TabStrip);

    // For tests: the layout under a tab strip of `tabStrip` DIPs at 100% text (0 for none), so a test can compare the
    // page with and without it.
    internal static LibraryLayout Plan(LibraryLayoutInput input, double tabStrip)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Height);
        var scale = Scale(input);

        var content = Math.Max(0, input.Width - RailWidth - ContentHorizontalMargin);
        var use = UseColumn * scale;
        var action = ActionColumn * scale;
        var minimumText = MinimumTextColumn * scale;
        var list = Math.Min(ListPaneWidth * scale, MaxListPaneWidth);

        var sideBySideText = TextColumn(content - list - PaneGap, use, action);
        var sideBySide = sideBySideText >= minimumText;
        var text = sideBySide ? sideBySideText : TextColumn(content, use, action);
        var horizontalOverflow = !sideBySide && text < minimumText;
        if (horizontalOverflow)
        {
            text = minimumText;
        }

        var row = TextLine * scale + RowPadding;
        var normalRows = Rows(input, scale, row, tabStrip, isShort: false);
        var isShort = normalRows < MinimumNormalRows;
        var rows = isShort ? Rows(input, scale, row, tabStrip, isShort: true) : normalRows;
        var verticalOverflow = rows < MinimumRows;

        var composition = (sideBySide ? LibraryLayoutComposition.SideBySide : LibraryLayoutComposition.Stacked)
            | (isShort ? LibraryLayoutComposition.Short : 0);
        return new LibraryLayout(
            composition,
            sideBySide ? list : content,
            use,
            text,
            text,
            action,
            Math.Max(rows, MinimumRows),
            horizontalOverflow,
            verticalOverflow);
    }

    // For tests: the height the card gets in the given composition, in DIPs (see CardHeight below).
    internal static double CardHeight(LibraryLayoutInput input, bool isShort, double tabStrip = TabStrip)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CardHeight(input, Scale(input), isShort, tabStrip);
    }

    private static double Scale(LibraryLayoutInput input) =>
        double.IsFinite(input.TextScale) ? Math.Clamp(input.TextScale, 1, 2.25) : 1;

    private static double TextColumn(double pane, double use, double action) =>
        Math.Max(0, pane - 2 * CardPadding - use - action) / 2;

    // The height the card gets: the window's height less everything above the card (the title bar, the content margins,
    // the page title and subtitle, the page's commands and the tab strip) and below it (the footer). The short
    // composition drops the subtitle.
    private static double CardHeight(LibraryLayoutInput input, double scale, bool isShort, double tabStrip) =>
        input.Height
        - TitleBar
        - ContentVerticalMargin
        - FooterGap - ButtonHeight * scale
        - PageHeaderMargins - PageTitle * scale - (isShort ? 0 : SubtitleLines * TextLine * scale)
        - ButtonHeight * scale - CommandsGap
        - tabStrip * scale;

    // Term rows that fit in the card under everything in it above the grid. The short composition drops the description
    // line and moves Term details to a subpage; the notice stays in both, because it is actionable.
    private static int Rows(LibraryLayoutInput input, double scale, double row, double tabStrip, bool isShort)
    {
        var inCard = 2 * CardPadding
            + CardGaps + (CardHeading + CardMeta + ButtonHeight) * scale + (isShort ? 0 : TextLine * scale)
            + (input.NoticeVisible ? NoticeHeight * scale + NoticeGap : 0)
            + ButtonHeight * scale + ToolbarGap
            + GridHeader * scale
            + (input.TermDetailsOpen && !isShort ? TermDetailsHeight * scale : 0);
        return (int)Math.Floor(Math.Max(0, CardHeight(input, scale, isShort, tabStrip) - inCard) / row);
    }
}
