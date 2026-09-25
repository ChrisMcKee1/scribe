using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddAnnouncementTests
{
    [Fact]
    public void Kind_change_announces_after_700_ms()
    {
        var prior = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Pending, null, "pending");
        var next = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Create, null, "create");

        Assert.Equal(QuickAddAnnouncementTiming.Delayed, QuickAddAnnouncement.ShouldAnnounce(prior, next));
        Assert.Equal(TimeSpan.FromMilliseconds(700), QuickAddAnnouncement.AnnouncementDelay);
    }

    [Fact]
    public void Same_kind_keystroke_is_silent()
    {
        var prior = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Pending, null, "a");
        var next = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Pending, null, "ab");

        Assert.Equal(QuickAddAnnouncementTiming.None, QuickAddAnnouncement.ShouldAnnounce(prior, next));
    }

    [Theory]
    [InlineData(QuickDictionaryAdd.PlanKind.Saved)]
    [InlineData(QuickDictionaryAdd.PlanKind.SaveFailed)]
    [InlineData(QuickDictionaryAdd.PlanKind.CopySucceeded)]
    [InlineData(QuickDictionaryAdd.PlanKind.CopyFailed)]
    public void Results_and_errors_announce_at_once(QuickDictionaryAdd.PlanKind kind)
    {
        var prior = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Pending, null, "pending");
        var next = new QuickDictionaryAdd.Plan(kind, null, "result");

        Assert.Equal(QuickAddAnnouncementTiming.Immediate, QuickAddAnnouncement.ShouldAnnounce(prior, next));
    }
}
