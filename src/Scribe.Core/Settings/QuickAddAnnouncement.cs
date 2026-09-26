namespace Scribe.Core.Settings;

public enum QuickAddAnnouncementTiming
{
    None,
    Delayed,
    Immediate,
}

public static class QuickAddAnnouncement
{
    public static readonly TimeSpan AnnouncementDelay = TimeSpan.FromMilliseconds(700);

    public static QuickAddAnnouncementTiming ShouldAnnounce(QuickDictionaryAdd.Plan? previous, QuickDictionaryAdd.Plan next)
    {
        if (next.Kind is QuickDictionaryAdd.PlanKind.Saved or QuickDictionaryAdd.PlanKind.SaveFailed or QuickDictionaryAdd.PlanKind.CopySucceeded or QuickDictionaryAdd.PlanKind.CopyFailed)
        {
            return QuickAddAnnouncementTiming.Immediate;
        }

        if (previous is null) return string.IsNullOrWhiteSpace(next.Message) ? QuickAddAnnouncementTiming.None : QuickAddAnnouncementTiming.Delayed;
        return previous.Value.Kind != next.Kind || previous.Value.Severity != next.Severity || previous.Value.Action != next.Action
            ? QuickAddAnnouncementTiming.Delayed
            : QuickAddAnnouncementTiming.None;
    }
}
