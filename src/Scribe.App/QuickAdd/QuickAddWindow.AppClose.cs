using System.Windows;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;

namespace Scribe.App.QuickAdd;

public partial class QuickAddWindow
{
    public bool HasAppCloseCorrection() => HasUnsavedSavableCorrection();

    public async Task<bool> RequestAppCloseAsync()
    {
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();
        if (!HasUnsavedSavableCorrection())
        {
            _allowClose = true;
            Close();
            return true;
        }

        if (_closePromptActive)
        {
            return false;
        }

        _closePromptActive = true;
        try
        {
            var prompt = QuickAddClosePrompt.ForUnsavedWord();
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = prompt.Title,
                Content = prompt.Body,
                PrimaryButtonText = prompt.PrimaryButton,
                SecondaryButtonText = prompt.DiscardButton,
                CloseButtonText = prompt.CancelButton,
                PrimaryButtonAppearance = ControlAppearance.Primary,
                CloseButtonAppearance = ControlAppearance.Secondary,
                Owner = this,
            };
            dialog.Loaded += (_, _) => MakeKeepEditingDefault(dialog);

            var choice = await dialog.ShowDialogAsync();
            if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                Save(closeAfterSaving: true);
                return _allowClose;
            }

            if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                _allowClose = true;
                Close();
                return true;
            }

            return false;
        }
        finally
        {
            _closePromptActive = false;
        }
    }
}
