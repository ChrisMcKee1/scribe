using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

public sealed class DatabaseRepairNoticeTests
{
    [Fact]
    public void A_repair_that_brought_both_back_says_so_quietly()
    {
        var (text, isError) = DatabaseRepairNotice.Compose(settingsLost: false, dictionaryLost: false);

        Assert.Equal("Scribe repaired its database. Settings and dictionary were recovered; some history may be missing.", text);
        Assert.False(isError);
    }

    [Theory]
    [InlineData(true, false, "your settings could not be recovered and were reset")]
    [InlineData(false, true, "Your settings were recovered, but your dictionary could not be.")]
    [InlineData(true, true, "your settings and dictionary could not be recovered and were reset")]
    public void A_repair_that_lost_something_never_claims_it_came_back(bool settingsLost, bool dictionaryLost, string expected)
    {
        var (text, isError) = DatabaseRepairNotice.Compose(settingsLost, dictionaryLost);

        Assert.True(isError);
        Assert.Contains(expected, text);
        Assert.Contains("The damaged file was kept next to the database for manual recovery.", text);
        Assert.DoesNotContain("Settings and dictionary were recovered", text);
        if (settingsLost)
        {
            Assert.DoesNotContain("settings were recovered", text, StringComparison.OrdinalIgnoreCase);
        }

        if (dictionaryLost)
        {
            Assert.DoesNotContain("dictionary were recovered", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_notice_has_a_dash()
    {
        foreach (var settingsLost in new[] { false, true })
        {
            foreach (var dictionaryLost in new[] { false, true })
            {
                var (text, _) = DatabaseRepairNotice.Compose(settingsLost, dictionaryLost);
                Assert.DoesNotContain('\u2013', text);
                Assert.DoesNotContain('\u2014', text);
            }
        }
    }
}
