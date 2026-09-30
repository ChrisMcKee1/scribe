using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Scribe.App.Infrastructure;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Scribe.App.Settings;

public partial class DictionaryWordWindow : FluentWindow
{
    private readonly Func<string, IReadOnlyList<string>, DictionaryWordEditor.Result> _build;
    private readonly List<FormEditor> _forms = [];
    private readonly string _originalWritten;
    private readonly string _displayedWritten;
    private DictionaryWordEditor.Result? _choice;
    private bool _imeComposing;

    private DictionaryWordWindow(
        Window owner,
        DictionaryEntryBuilder.Row? edited,
        Func<string, IReadOnlyList<string>, DictionaryWordEditor.Result> build)
    {
        _build = build;
        Owner = owner;
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        var fit = WindowFit.Compute(640, 680, 380, 360,
            new WorkArea(0, 0,
                owner.ActualWidth > 0 ? owner.ActualWidth : workArea.Width,
                owner.ActualHeight > 0 ? owner.ActualHeight : workArea.Height),
            textScale: TextScaleService.CurrentFactor);
        Width = fit.Width;
        Height = fit.Height;
        MinWidth = fit.MinWidth;
        MinHeight = fit.MinHeight;

        _originalWritten = edited?.Replacement ?? string.Empty;
        WrittenBox.Text = _originalWritten;
        _displayedWritten = WrittenBox.Text;
        AddForm(edited?.Pattern ?? string.Empty);
        RuleStatusText.Text = edited is { } row
            ? row.Enabled
                ? row.WholeWord ? "This way is on and matches whole words only." : "This way is on and can match inside words."
                : row.WholeWord ? "This way is off and matches whole words only." : "This way is off and can match inside words."
            : string.Empty;
        if (edited.HasValue)
        {
            Title = DialogTitleBar.Title = "Edit word";
            AcceptButton.Content = "Done";
            AcceptButton.ToolTip = "Keep these changes in the settings draft (Ctrl+Enter).";
        }

        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _imeComposing = false);
        LostKeyboardFocus += (_, e) =>
        {
            if (e.OldFocus is TextBox)
            {
                _imeComposing = false;
            }
        };
        Loaded += (_, _) => FocusEditor(WrittenBox);
    }

    public static DictionaryWordEditor.Result? Show(
        Window owner,
        DictionaryEntryBuilder.Row? edited,
        Func<string, IReadOnlyList<string>, DictionaryWordEditor.Result> build)
    {
        var window = new DictionaryWordWindow(owner, edited, build);
        window.ShowDialog();
        return window._choice;
    }

    private void AddForm(string original)
    {
        var container = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.ColumnDefinitions.Add(new ColumnDefinition());
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(StyleProperty, "FieldHelp");
        Grid.SetColumnSpan(title, 2);
        container.Children.Add(title);
        var editor = new TextBox { Text = original };
        editor.SetResourceReference(StyleProperty, "WordTextBox");
        AutomationProperties.SetLabeledBy(editor, title);
        Grid.SetRow(editor, 1);
        container.Children.Add(editor);
        var hint = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        hint.SetResourceReference(StyleProperty, "FieldHelp");
        Grid.SetRow(hint, 2);
        Grid.SetColumnSpan(hint, 2);
        container.Children.Add(hint);
        var form = new FormEditor(container, title, editor, hint, original, editor.Text);
        if (_forms.Count > 0)
        {
            var remove = new Wpf.Ui.Controls.Button
            {
                Content = "Remove",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            remove.Click += (_, _) => RemoveForm(form);
            Grid.SetColumn(remove, 1);
            Grid.SetRow(remove, 1);
            container.Children.Add(remove);
            form.RemoveButton = remove;
        }

        editor.TextChanged += (_, _) =>
        {
            UpdateFormNames();
            ErrorText.Visibility = Visibility.Collapsed;
        };
        _forms.Add(form);
        FormsPanel.Children.Add(container);
        UpdateFormNames();
    }

    private void UpdateFormNames()
    {
        for (var i = 0; i < _forms.Count; i++)
        {
            var form = _forms[i];
            form.Title.Text = $"Way {i + 1} of {_forms.Count}";
            AutomationProperties.SetName(form.Editor, $"Scribe hears, way {i + 1} of {_forms.Count}");
            if (form.RemoveButton is { } remove)
            {
                AutomationProperties.SetName(remove, string.IsNullOrWhiteSpace(form.Editor.Text)
                    ? $"Remove empty way {i + 1}"
                    : $"Remove {form.Editor.Text}");
            }

            var multiline = form.Editor.Text.IndexOfAny(['\r', '\n']) >= 0;
            form.Hint.Text = multiline ? "This way includes a line break, so it is one phrase." : string.Empty;
            form.Hint.Visibility = multiline ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AddWayButton_Click(object sender, RoutedEventArgs e)
    {
        AddForm(string.Empty);
        FocusEditor(_forms[^1].Editor);
    }

    private void RemoveForm(FormEditor form)
    {
        var index = _forms.IndexOf(form);
        _forms.RemoveAt(index);
        FormsPanel.Children.Remove(form.Container);
        UpdateFormNames();
        ErrorText.Visibility = Visibility.Collapsed;
        FocusEditor(_forms[Math.Min(index, _forms.Count - 1)].Editor);
    }

    private static void FocusEditor(TextBox editor)
    {
        editor.BringIntoView();
        editor.Focus();
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        var written = DictionaryWordEditor.PreserveUnchangedText(_originalWritten, _displayedWritten, WrittenBox.Text);
        var forms = _forms.Select(form =>
            DictionaryWordEditor.PreserveUnchangedText(form.Original, form.DisplayedOriginal, form.Editor.Text)).ToArray();
        var result = _build(written, forms);
        if (!result.Succeeded)
        {
            ErrorText.Text = result.Error;
            ErrorText.Visibility = Visibility.Visible;
            FocusEditor(_forms[Math.Clamp(result.ErrorFormIndex, 0, _forms.Count - 1)].Editor);
            UIElementAutomationPeer.FromElement(ErrorText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            return;
        }

        _choice = result;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_imeComposing)
        {
            // Give the first Escape to the composition, but never keep an abandoned composition latched.
            if (e.Key == Key.Escape ||
                (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape) ||
                (e.Key == Key.DeadCharProcessed && e.DeadCharProcessedKey == Key.Escape))
            {
                _imeComposing = false;
            }

            return;
        }

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Accept();
        }
    }

    private sealed record FormEditor(
        Grid Container, TextBlock Title, TextBox Editor, TextBlock Hint, string Original, string DisplayedOriginal)
    {
        public Wpf.Ui.Controls.Button? RemoveButton { get; set; }
    }
}
