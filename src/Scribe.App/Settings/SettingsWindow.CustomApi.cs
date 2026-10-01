using System.Windows.Controls;
using Scribe.Core.Cleanup;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

/*
 * Another AI service's API: Chat Completions or Responses (AppSettings.AiCleanupCustomApiStyle). An address that ends in an
 * API's path names it, and Ollama and LM Studio at their own addresses take Chat Completions (CustomServiceAddress.Effective):
 * the box then shows that API and cannot be changed, and the choice made in it is kept, so taking the path off the address
 * brings it back. Save stores the API the boxes reach the service with (CustomServiceFields.ForSave).
 */
public partial class SettingsWindow
{
    // The API chosen in the box, whatever the address says now.
    private CustomApiStyle _chosenCustomApiStyle;

    // True while the window sets the box itself, so its own change is not taken as a choice.
    private bool _showingCustomApiStyle;

    private CustomApiStyle ChosenCustomApiStyle => _chosenCustomApiStyle;

    // Shows the API the boxes would reach the service with, and lets it be chosen only when the address leaves it open.
    private void ShowCustomApiStyle()
    {
        if (CustomApiStyleCombo is null || CustomApiStyleHint is null)
        {
            return;
        }

        if (CustomApiStyleCombo.Items.Count == 0)
        {
            foreach (var style in CustomApiStyleText.Choices)
            {
                CustomApiStyleCombo.Items.Add(new ComboBoxItem { Content = CustomApiStyleText.NameOf(style), Tag = style });
            }
        }

        var address = CustomEndpointBox?.Text?.Trim();
        var shown = CustomServiceAddress.Effective(CleanupProvider.OpenAiCompatible, address, _chosenCustomApiStyle);
        _showingCustomApiStyle = true;
        try
        {
            CustomApiStyleCombo.SelectedIndex = CustomApiStyleText.Choices.ToList().IndexOf(shown);
        }
        finally
        {
            _showingCustomApiStyle = false;
        }

        CustomApiStyleCombo.IsEnabled = CustomApiStyleText.CanChoose(address);
        CustomApiStyleHint.Text = CustomApiStyleText.Hint(address);
    }

    private void CustomApiStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingCustomApiStyle || _loadingUi || CustomApiStyleCombo is not { IsEnabled: true } combo ||
            (combo.SelectedItem as ComboBoxItem)?.Tag is not CustomApiStyle chosen)
        {
            return;
        }

        _chosenCustomApiStyle = chosen;
        CustomConnectionField_Changed(sender, e);
    }
}
