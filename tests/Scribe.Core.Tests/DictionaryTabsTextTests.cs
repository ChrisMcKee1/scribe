using System.Globalization;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The Dictionary page's tabs: each says how much it holds, and each tab's line explains it and names the other, so a
/// person on either tab learns the other exists and how the two differ.
/// </summary>
public sealed class DictionaryTabsTextTests
{
    [Theory]
    [InlineData(0, "0 words")]
    [InlineData(1, "1 word")]
    [InlineData(31, "31 words")]
    [InlineData(1_549, "1,549 words")]
    public void Your_words_counts_the_words(int words, string expected)
    {
        // The count is formatted in the current culture, like the word packs' own counts; pin one for the separator.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal(expected, DictionaryTabsText.YourWordsSummary(words));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Nothing_is_counted_before_the_words_have_loaded()
    {
        Assert.Equal(string.Empty, DictionaryTabsText.YourWordsSummary(null));
        Assert.Equal(string.Empty, DictionaryTabsText.WordPacksSummary(null, 11));
    }

    [Theory]
    [InlineData(11, 11, "11 of 11 on")]
    [InlineData(3, 11, "3 of 11 on")]
    [InlineData(0, 11, "0 of 11 on")]
    [InlineData(1, 1, "1 of 1 on")]
    [InlineData(0, 0, "No word packs")]
    [InlineData(14, 11, "11 of 11 on")] // never more on than there are
    [InlineData(-2, 11, "0 of 11 on")]
    public void Word_packs_count_how_many_are_on_of_how_many(int on, int total, string expected)
    {
        Assert.Equal(expected, DictionaryTabsText.WordPacksSummary(on, total));
    }

    [Fact]
    public void Each_tab_s_line_names_the_other_tab_and_says_your_own_words_win()
    {
        Assert.Contains("word pack", DictionaryTabsText.YourWordsGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yours wins", DictionaryTabsText.YourWordsGuide, StringComparison.Ordinal);
        Assert.Contains("word packs", DictionaryTabsText.YourWordsLink, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Your own words always win", DictionaryTabsText.WordPacksGuide, StringComparison.Ordinal);
        Assert.Contains("Your words", DictionaryTabsText.WordPacksLink, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_reader_hears_the_summary_then_the_line()
    {
        Assert.Equal(
            "31 words. " + DictionaryTabsText.YourWordsGuide,
            DictionaryTabsText.HelpText(DictionaryTabsText.YourWordsSummary(31), DictionaryTabsText.YourWordsGuide));
        Assert.Equal(DictionaryTabsText.WordPacksGuide, DictionaryTabsText.HelpText(string.Empty, DictionaryTabsText.WordPacksGuide));
    }
}
