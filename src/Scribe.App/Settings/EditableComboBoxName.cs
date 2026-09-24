using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Scribe.App.Settings;

/// <summary>
/// Gives an editable ComboBox's text box the ComboBox's own UI Automation label.
/// </summary>
/// <remarks>
/// WPF-UI 4.3.0 makes that text box the only Tab stop of an editable combo box (ComboBox.xaml takes the combo
/// box itself out of the Tab order) and gives it an explicit style without a name, and WPF exposes it to UI
/// Automation as the combo box's first child (ComboBoxAutomationPeer.GetChildrenCore). With no name, a text box
/// is announced by the text it holds (TextBox.GetPlainText), so focus landed on "phi-4, edit" rather than on the
/// model picker. An implicit style cannot reach it past the explicit one, hence code.
/// </remarks>
internal static class EditableComboBoxName
{
    private const string EditableTextBoxPart = "PART_EditableTextBox";

    public static void ShareWithTextBox(ComboBox comboBox)
    {
        comboBox.ApplyTemplate();
        if (comboBox.Template?.FindName(EditableTextBoxPart, comboBox) is not TextBox textBox)
        {
            return;
        }

        // Bound rather than copied, so a label set or changed later still reaches the text box.
        BindingOperations.SetBinding(textBox, AutomationProperties.NameProperty, new Binding
        {
            Source = comboBox,
            Path = new PropertyPath(AutomationProperties.NameProperty),
        });
        BindingOperations.SetBinding(textBox, AutomationProperties.LabeledByProperty, new Binding
        {
            Source = comboBox,
            Path = new PropertyPath(AutomationProperties.LabeledByProperty),
        });
    }
}
