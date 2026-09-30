using System.Windows.Controls;
using Scribe.Core.Diagnostics;

namespace Scribe.App.Settings;

// StopInactiveProgress: the window's busy indicators animate only while they are shown (see BusyRingAnimation and
// BusyBarAnimation). Off, they keep WPF-UI's and WPF's own behaviour exactly: nothing here attaches.
public partial class SettingsWindow
{
    private readonly List<BusyBarAnimation> _busyBars = [];
    private bool _busyAnimationsAttached;

    // Every status row on the AI cleanup page; each has a busy spinner.
    private SettingsStatusRow[] BusyStatusRows =>
        [FoundryStatusRow, AzureSignInStatusRow, AzureVerifyStatusRow, CustomStatusRow, CopilotStatusRow, LocalAppStatusRow];

    private void InitializeBusyAnimations()
    {
        if (!_perfFlags.IsOn(PerfFlags.StopInactiveProgress))
        {
            return;
        }

        foreach (var row in BusyStatusRows)
        {
            row.UseBusyAnimationLifecycle();
        }

        var style = BusyBarAnimation.LoadStyle();
        _busyBars.Add(new BusyBarAnimation(BusyBarIn(DictionarySuggestBusy), style));
        _busyBars.Add(new BusyBarAnimation(BusyBarIn(DictionaryCleanupBusy), style));
        _busyAnimationsAttached = true;
    }

    private static ProgressBar BusyBarIn(Panel busyPanel) => busyPanel.Children.OfType<ProgressBar>().Single();

    private void CloseBusyAnimations()
    {
        if (!_busyAnimationsAttached)
        {
            return;
        }

        _busyAnimationsAttached = false;
        foreach (var row in BusyStatusRows)
        {
            row.CloseBusyAnimation();
        }

        foreach (var bar in _busyBars)
        {
            bar.Close();
        }

        _busyBars.Clear();
    }
}
