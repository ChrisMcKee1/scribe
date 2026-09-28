namespace Scribe.Core.Lifecycle;

/// <summary>
/// Shows one dictation state change on its two independent views, the tray icon and the recording pill, in the order
/// PillBeforeTray picks: the tray first (as every release before it) or the pill's commands first. Each view receives the
/// change exactly once, and a view that throws never keeps the other from receiving it: when the first view throws, the
/// second still runs before that exception goes on; an exception from the second goes on after both ran.
/// </summary>
/// <remarks>
/// It only calls the two views, on the caller's thread, in order: nothing here posts, waits or blocks. The tray view the
/// shell passes catches and logs its own failures; the pill's calls do not, so their exception still reaches the
/// presentation relay, which logs it, and the caller skips whatever follows, as it did before.
/// </remarks>
public static class DictationStateViews
{
    /// <summary>Shows a state on both views, the pill first when <paramref name="pillFirst"/>.</summary>
    public static void Show(bool pillFirst, Action tray, Action pill)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(pill);
        var first = pillFirst ? pill : tray;
        var second = pillFirst ? tray : pill;
        try
        {
            first();
        }
        finally
        {
            second();
        }
    }
}
