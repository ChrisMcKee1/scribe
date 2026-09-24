namespace Scribe.App.Infrastructure;

/// <summary>
/// The two-button Fluent confirmation ("do it" or Cancel) that Scribe asks with before acting.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI 4.3.0's MessageBox always makes its primary button the default (IsDefault in MessageBox.xaml) and
/// focuses nothing, so Enter confirms whatever the dialog asks. Microsoft's confirmation guidance makes the
/// default depend on the kind of question: proceed for a routine confirmation, but don't proceed for a risky
/// action that can't be easily undone, and for one with security consequences
/// (https://learn.microsoft.com/windows/win32/uxguide/mess-confirm#default-values).
/// </para>
/// <para>
/// For a risky action Cancel becomes the default in every sense Windows gives a dialog's default button: the
/// accent treatment, the Enter key, and focus when the dialog opens
/// (https://learn.microsoft.com/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs, DefaultButton).
/// </para>
/// </remarks>
internal static class ThemedConfirmation
{
    /// <param name="cancelIsDefault">
    /// The action is risky: it deletes data or sends dictation text off this PC, and cannot be taken back.
    /// </param>
    public static Wpf.Ui.Controls.MessageBox Create(string title, string content, string confirmText, bool cancelIsDefault)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
        };

        if (cancelIsDefault)
        {
            dialog.PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            dialog.CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary;

            // Loaded comes after the dialog is activated, so Focus takes keyboard focus rather than only
            // recording it for later.
            dialog.Loaded += (_, _) => MakeCancelTheDefault(dialog);
        }

        return dialog;
    }

    private static void MakeCancelTheDefault(Wpf.Ui.Controls.MessageBox dialog)
    {
        var primary = MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Primary);
        var cancel = MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Close);
        if (primary is null || cancel is null)
        {
            // A template without these buttons still answers Enter with WPF-UI's own default, so the accent
            // goes back to that button instead of pointing at one Enter does not press.
            dialog.PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            dialog.CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            return;
        }

        // Exactly one default: with two, Enter has two access key targets, and WPF then only moves focus
        // between them instead of clicking either.
        primary.IsDefault = false;
        cancel.IsDefault = true;
        cancel.Focus();
    }
}
