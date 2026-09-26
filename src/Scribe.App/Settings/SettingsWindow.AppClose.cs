using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    public bool HasAppCloseChanges(CloseTrigger trigger)
    {
        CommitPendingGridEdits();
        RefreshFooterNow();
        return ShouldAskBeforeClose(trigger).Ask;
    }

    public async Task<bool> RequestAppCloseAsync(CloseTrigger trigger)
    {
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();
        await RequestCloseAsync(trigger);
        return _closed || _closeAccepted;
    }
}
