using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class HistoryRetentionChangeTests
{
    [Theory]
    [InlineData(0, 90)]
    [InlineData(365, 90)]
    public void Shortening_retention_describes_the_save_confirmation(int loadedDays, int draftDays)
    {
        var confirmation = HistoryRetentionChange.Describe(loadedDays, draftDays);

        Assert.NotNull(confirmation);
        Assert.Equal($"Delete dictations older than {HistoryRetentionChange.FormatPeriod(draftDays)}?", confirmation.Title);
        Assert.Equal(
            $"Scribe will delete dictations older than {HistoryRetentionChange.FormatPeriod(draftDays)}, and their recordings, soon after you save. This can't be undone, and Usage totals will change.",
            confirmation.Body);
        Assert.Equal("Keep editing", confirmation.KeepEditingText);
        Assert.Equal("Delete and save", confirmation.DeleteAndSaveText);
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(30, 365)]
    [InlineData(90, 0)]
    [InlineData(0, 0)]
    public void Unchanged_longer_or_until_delete_does_not_confirm(int loadedDays, int draftDays)
    {
        Assert.False(HistoryRetentionChange.RequiresConfirmation(loadedDays, draftDays));
        Assert.Null(HistoryRetentionChange.Describe(loadedDays, draftDays));
    }
}
