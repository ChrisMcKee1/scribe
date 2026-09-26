using Scribe.Core.Settings;
using Scribe.App.Infrastructure;
using Wpf.Ui.Controls;

namespace Scribe.App.Settings;

public enum SettingsUpdateRestartGuardResult
{
    Canceled,
    ProceedKeepOpen,
    ProceedCloseAfterAction,
}

public partial class SettingsWindow
{
    public bool HasAppCloseSaveInProgress => _saveInProgress;

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

    public async Task<SettingsUpdateRestartGuardResult> RequestUpdateRestartWithoutClosingAsync()
    {
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();
        while (_saveInProgress)
        {
            FooterStatusText.Text = "Saving...";
            await Task.Delay(100);
            if (_closed)
            {
                return SettingsUpdateRestartGuardResult.ProceedKeepOpen;
            }
        }

        CommitPendingGridEdits();
        RefreshFooterNow();
        var decision = ShouldAskBeforeClose(CloseTrigger.UpdateRestart);
        if (!decision.Ask)
        {
            return SettingsUpdateRestartGuardResult.ProceedKeepOpen;
        }

        if (_closePromptShowing)
        {
            return SettingsUpdateRestartGuardResult.Canceled;
        }

        _closePromptShowing = true;
        try
        {
            var prompt = SettingsClosePrompt.For(CloseTrigger.UpdateRestart);
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = prompt.Title,
                Content = prompt.Body,
                PrimaryButtonText = prompt.PrimaryButton,
                SecondaryButtonText = prompt.DiscardButton,
                CloseButtonText = prompt.CancelButton,
                PrimaryButtonAppearance = ControlAppearance.Primary,
                SecondaryButtonAppearance = ControlAppearance.Secondary,
                CloseButtonAppearance = ControlAppearance.Secondary,
                Owner = this,
            };
            dialog.Loaded += (_, _) =>
            {
                if (MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Close) is { } keepEditing)
                {
                    if (MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Primary) is { } primary)
                    {
                        primary.IsDefault = false;
                    }

                    keepEditing.IsDefault = true;
                    keepEditing.Focus();
                }
            };

            var result = await dialog.ShowDialogAsync();
            if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                _saveInProgress = true;
                FooterStatusText.Text = "Saving...";
                try
                {
                    return await TrySaveWithConfirmationsAsync()
                        ? SettingsUpdateRestartGuardResult.ProceedKeepOpen
                        : SettingsUpdateRestartGuardResult.Canceled;
                }
                finally
                {
                    _saveInProgress = false;
                    ScheduleFooterRefresh();
                }
            }

            return result == Wpf.Ui.Controls.MessageBoxResult.Secondary
                ? SettingsUpdateRestartGuardResult.ProceedCloseAfterAction
                : SettingsUpdateRestartGuardResult.Canceled;
        }
        finally
        {
            _closePromptShowing = false;
        }
    }

    public void CloseAfterAppUpdateDiscard()
    {
        CloseAfterPromptAccepted();
    }
}
