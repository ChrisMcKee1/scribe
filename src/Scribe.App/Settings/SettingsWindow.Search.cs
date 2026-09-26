using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Scribe.Core.Settings;
using UiAutoSuggestBox = Wpf.Ui.Controls.AutoSuggestBox;
using UiAutoSuggestBoxQuerySubmittedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxQuerySubmittedEventArgs;
using UiAutoSuggestBoxSuggestionChosenEventArgs = Wpf.Ui.Controls.AutoSuggestBoxSuggestionChosenEventArgs;
using UiAutoSuggestBoxTextChangedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxTextChangedEventArgs;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private sealed record SettingsSearchSuggestion(string DisplayText, SettingsSearchResult? Result, bool IsSelectable = true);

    private readonly ObservableCollection<SettingsSearchSuggestion> _settingsSearchSuggestions = new();
    private SettingsSearchResult? _highlightedSettingsSearchResult;
    private TextBlock? _settingsSearchHiddenHint;
    private Panel? _settingsSearchHiddenHintPanel;
    private ToggleButton? _settingsSearchHiddenToggleParent;

    private void InitializeSettingsSearch()
    {
        SettingsSearchBox.ItemsSource = _settingsSearchSuggestions;
        SettingsSearchBox.Loaded += (_, _) =>
        {
            SettingsSearchBox.ApplyTemplate();
            if (SettingsSearchBox.Template.FindName("PART_TextBox", SettingsSearchBox) is UIElement textBox)
            {
                textBox.PreviewKeyDown += SettingsSearchInnerTextBox_PreviewKeyDown;
            }
        };
    }

    private void SettingsSearchBox_TextChanged(UiAutoSuggestBox sender, UiAutoSuggestBoxTextChangedEventArgs args)
    {
        args.Handled = true;
        _highlightedSettingsSearchResult = null;
        var text = args.Text ?? string.Empty;
        var results = SettingsSearchIndex.Search(text);
        _settingsSearchSuggestions.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            QueueSettingsSearchPopupState(sender, text, open: false);
            return;
        }

        if (results.Count == 0)
        {
            _settingsSearchSuggestions.Add(new SettingsSearchSuggestion("No settings found", null, IsSelectable: false));
        }
        else
        {
            foreach (var result in results)
            {
                _settingsSearchSuggestions.Add(new SettingsSearchSuggestion(result.DisplayText, result));
            }
        }

        QueueSettingsSearchPopupState(sender, text, open: true);
    }

    private void SettingsSearchBox_SuggestionChosen(UiAutoSuggestBox sender, UiAutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not SettingsSearchSuggestion { Result: { } result } suggestion || !suggestion.IsSelectable)
        {
            args.Handled = true;
            return;
        }

        if (sender.IsSuggestionListOpen && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            _highlightedSettingsSearchResult = result;
            args.Handled = true;
            return;
        }

        OpenSettingsSearchResult(result);
        args.Handled = true;
    }

    private void SettingsSearchBox_QuerySubmitted(UiAutoSuggestBox sender, UiAutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var result = _highlightedSettingsSearchResult is { } highlighted &&
            _settingsSearchSuggestions.Any(suggestion => ReferenceEquals(suggestion.Result, highlighted))
                ? highlighted
                : _settingsSearchSuggestions.FirstOrDefault(suggestion => suggestion.IsSelectable)?.Result;
        if (result is not null)
        {
            OpenSettingsSearchResult(result);
            args.Handled = true;
        }
    }

    private void SettingsSearchInnerTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SettingsSearchBox.IsSuggestionListOpen || string.IsNullOrEmpty(SettingsSearchBox.Text))
        {
            return;
        }

        SettingsSearchBox.Text = string.Empty;
        SettingsSearchBox.IsSuggestionListOpen = false;
        e.Handled = true;
    }

    private void QueueSettingsSearchPopupState(UiAutoSuggestBox sender, string text, bool open) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (string.Equals(sender.Text, text, StringComparison.Ordinal))
                {
                    sender.IsSuggestionListOpen = open;
                }
            });

    private void OpenSettingsSearchResult(SettingsSearchResult result)
    {
        SettingsSearchBox.IsSuggestionListOpen = false;
        ShowPage(result.Page);
        SelectDictionarySearchTab(result);
        ExpandSearchContainers(result.ControlName);
        UpdateLayout();

        if (FocusFirstUnsatisfiedRequirement(result))
        {
            return;
        }

        if (FindName(result.ControlName) is FrameworkElement target && target.IsVisible)
        {
            target.BringIntoView();
            _ = target.Focus();
        }
    }

    private bool FocusFirstUnsatisfiedRequirement(SettingsSearchResult result)
    {
        foreach (var requirement in result.Requirements)
        {
            if (FindName(requirement.ControlName) is not FrameworkElement target || RequirementSatisfied(target))
            {
                continue;
            }

            target.BringIntoView();
            _ = target.Focus();
            ShowSettingsSearchHiddenHint(target, requirement);
            return true;
        }

        return false;
    }

    private static bool RequirementSatisfied(FrameworkElement target) =>
        target is ToggleButton { IsChecked: true };

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
            case "HistoryRetentionCombo":
            case "StoreAudioCheck":
                HistorySettingsCard.IsExpanded = true;
                break;
        }
    }

    private void ShowSettingsSearchHiddenHint(FrameworkElement parent, SettingsSearchRequirement requirement)
    {
        ClearSettingsSearchHiddenHint();
        if (parent.Parent is not Panel panel)
        {
            return;
        }

        var verb = requirement.Kind == SettingsSearchRequirementKind.Radio ? "Choose" : "Turn on";
        var label = StripRecommendedSuffix(requirement.Label);
        _settingsSearchHiddenHint = new TextBlock
        {
            Text = $"{verb} \"{label}\" to see this setting.",
            Margin = new Thickness(parent is ToggleButton ? 28 : 0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Style = TryFindResource("CardDescription") as Style,
        };
        _settingsSearchHiddenHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetLiveSetting(_settingsSearchHiddenHint, AutomationLiveSetting.Polite);

        var index = panel.Children.IndexOf(parent);
        panel.Children.Insert(index < 0 ? panel.Children.Count : index + 1, _settingsSearchHiddenHint);
        _settingsSearchHiddenHintPanel = panel;

        if (parent is ToggleButton toggle)
        {
            _settingsSearchHiddenToggleParent = toggle;
            toggle.Checked += SettingsSearchHiddenParent_Changed;
            toggle.Unchecked += SettingsSearchHiddenParent_Changed;
        }
    }

    private static string StripRecommendedSuffix(string label) =>
        label.EndsWith(" (recommended)", StringComparison.Ordinal)
            ? label[..^" (recommended)".Length]
            : label;

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
    }
}
