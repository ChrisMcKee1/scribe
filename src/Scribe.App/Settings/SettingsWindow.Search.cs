using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Scribe.Core.Settings;
using UiAutoSuggestBox = Wpf.Ui.Controls.AutoSuggestBox;

using UiAutoSuggestBoxQuerySubmittedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxQuerySubmittedEventArgs;

using UiAutoSuggestBoxSuggestionChosenEventArgs = Wpf.Ui.Controls.AutoSuggestBoxSuggestionChosenEventArgs;

using UiAutoSuggestBoxTextChangedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxTextChangedEventArgs;


namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private readonly ObservableCollection<SettingsSearchResult> _settingsSearchResults = new();
    private TextBlock? _settingsSearchHiddenHint;
    private Panel? _settingsSearchHiddenHintPanel;
    private FrameworkElement? _settingsSearchHiddenHintParent;
    private ToggleButton? _settingsSearchHiddenToggleParent;

    private void InitializeSettingsSearch()
    {
        SettingsSearchBox.ItemsSource = _settingsSearchResults;
    }

    private void SettingsSearchBox_TextChanged(UiAutoSuggestBox sender, UiAutoSuggestBoxTextChangedEventArgs args)
    {
        var results = SettingsSearchIndex.Search(args.Text);
        _settingsSearchResults.Clear();
        foreach (var result in results)
        {
            _settingsSearchResults.Add(result);
        }

        sender.IsSuggestionListOpen = _settingsSearchResults.Count > 0;
    }

    private void SettingsSearchBox_SuggestionChosen(UiAutoSuggestBox sender, UiAutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SettingsSearchResult result)
        {
            OpenSettingsSearchResult(result);
            args.Handled = true;
        }
    }

    private void SettingsSearchBox_QuerySubmitted(UiAutoSuggestBox sender, UiAutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var result = _settingsSearchResults.FirstOrDefault();
        if (result is not null)
        {
            OpenSettingsSearchResult(result);
            args.Handled = true;
        }
    }

    private void OpenSettingsSearchResult(SettingsSearchResult result)
    {
        SettingsSearchBox.IsSuggestionListOpen = false;
        ShowPage(result.Page);
        SelectDictionarySearchTab(result);
        ExpandSearchContainers(result.ControlName);
        UpdateLayout();

        if (FindName(result.ControlName) is FrameworkElement target && target.IsVisible)
        {
            target.BringIntoView();
            _ = target.Focus();
            return;
        }

        if (result.ParentControlName is { Length: > 0 } parentName &&
            result.ParentLabel is { Length: > 0 } parentLabel &&
            FindName(parentName) is FrameworkElement parent)
        {
            parent.BringIntoView();
            _ = parent.Focus();
            ShowSettingsSearchHiddenHint(parent, parentLabel);
        }
    }

    private void SelectDictionarySearchTab(SettingsSearchResult result)
    {
        if (result.Page != SettingsPage.Dictionary)
        {
            return;
        }

        DictionaryTabs.SelectedItem = string.Equals(result.Label, "Word packs", StringComparison.Ordinal)
            ? DictionaryTabs.Items.OfType<TabItem>().FirstOrDefault(item => string.Equals(item.Header?.ToString(), "Word packs", StringComparison.Ordinal))
            : YourWordsTab;
    }

    private void ExpandSearchContainers(string controlName)
    {
        switch (controlName)
        {
            case "AiWritingStyleBox":
                AiWritingStyleExpander.IsExpanded = true;
                break;
            case "AiPromptStyleCombo":
            case "AiFrontierPromptBox":
            case "AiLocalPromptBox":
                AiAdvancedCard.IsExpanded = true;
                break;
            case "ProfileProcessesBox":
                ProfileProgramNamesExpander.IsExpanded = true;
                break;
            case "HistoryRetentionCombo":
            case "HistoryRetentionCustomBox":
            case "StoreAudioCheck":
                HistorySettingsCard.IsExpanded = true;
                break;
        }
    }

    private void ShowSettingsSearchHiddenHint(FrameworkElement parent, string parentLabel)
    {
        ClearSettingsSearchHiddenHint();
        if (parent.Parent is not Panel panel)
        {
            return;
        }

        _settingsSearchHiddenHint = new TextBlock
        {
            Text = $"Turn on {parentLabel} to see this setting.",
            Margin = new Thickness(parent is CheckBox or Wpf.Ui.Controls.ToggleSwitch ? 28 : 0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Style = TryFindResource("CardDescription") as Style,
        };
        _settingsSearchHiddenHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetLiveSetting(_settingsSearchHiddenHint, AutomationLiveSetting.Polite);

        var index = panel.Children.IndexOf(parent);
        panel.Children.Insert(index < 0 ? panel.Children.Count : index + 1, _settingsSearchHiddenHint);
        _settingsSearchHiddenHintPanel = panel;
        _settingsSearchHiddenHintParent = parent;

        if (parent is ToggleButton toggle)
        {
            _settingsSearchHiddenToggleParent = toggle;
            toggle.Checked += SettingsSearchHiddenParent_Changed;
            toggle.Unchecked += SettingsSearchHiddenParent_Changed;
        }
    }

    private void SettingsSearchHiddenParent_Changed(object sender, RoutedEventArgs e) => ClearSettingsSearchHiddenHint();

    private void ClearSettingsSearchHiddenHint()
    {
        if (_settingsSearchHiddenToggleParent is not null)
        {
            _settingsSearchHiddenToggleParent.Checked -= SettingsSearchHiddenParent_Changed;
            _settingsSearchHiddenToggleParent.Unchecked -= SettingsSearchHiddenParent_Changed;
            _settingsSearchHiddenToggleParent = null;
        }

        if (_settingsSearchHiddenHintPanel is not null && _settingsSearchHiddenHint is not null)
        {
            _settingsSearchHiddenHintPanel.Children.Remove(_settingsSearchHiddenHint);
        }

        _settingsSearchHiddenHint = null;
        _settingsSearchHiddenHintPanel = null;
        _settingsSearchHiddenHintParent = null;
    }
}


