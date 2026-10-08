using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scribe.Core.Settings;
using UiAutoSuggestBox = Wpf.Ui.Controls.AutoSuggestBox;
using UiAutoSuggestBoxQuerySubmittedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxQuerySubmittedEventArgs;
using UiAutoSuggestBoxSuggestionChosenEventArgs = Wpf.Ui.Controls.AutoSuggestBoxSuggestionChosenEventArgs;
using UiAutoSuggestBoxTextChangedEventArgs = Wpf.Ui.Controls.AutoSuggestBoxTextChangedEventArgs;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private sealed record SettingsSearchSuggestion(string DisplayText, SettingsSearchResult? Result, bool IsSelectable = true)
    {
        public override string ToString() => DisplayText;
    }

    private readonly ObservableCollection<SettingsSearchSuggestion> _settingsSearchSuggestions = new();
    private SettingsSearchResult? _highlightedSettingsSearchResult;
    private TextBlock? _settingsSearchHiddenHint;
    private Panel? _settingsSearchHiddenHintPanel;
    private ToggleButton? _settingsSearchHiddenToggleParent;
    private ListView? _settingsSearchSuggestionsList;
    private int _settingsSearchGeneration;
    private bool _settingsSearchClosed;

    private void InitializeSettingsSearch()
    {
        SettingsSearchBox.ItemsSource = _settingsSearchSuggestions;
        SettingsSearchBox.LostKeyboardFocus += SettingsSearchFocus_LostKeyboardFocus;
        SettingsSearchBox.Loaded += (_, _) => AttachSettingsSearchTemplateParts();
        Closed += (_, _) =>
        {
            _settingsSearchClosed = true;
            _settingsSearchGeneration++;
        };
    }

    private void AttachSettingsSearchTemplateParts()
    {
        SettingsSearchBox.ApplyTemplate();
        if (SettingsSearchBox.Template.FindName("PART_TextBox", SettingsSearchBox) is UIElement textBox)
        {
            AutomationProperties.SetName(textBox, "Find a setting");
        }

        if (SettingsSearchBox.Template.FindName("PART_SuggestionsList", SettingsSearchBox) is ListView list)
        {
            _settingsSearchSuggestionsList = list;
            list.PreviewMouseLeftButtonUp -= SettingsSearchSuggestionsList_PreviewMouseLeftButtonUp;
            list.PreviewMouseLeftButtonUp += SettingsSearchSuggestionsList_PreviewMouseLeftButtonUp;
            list.PreviewKeyDown -= SettingsSearchSuggestionsList_PreviewKeyDown;
            list.PreviewKeyDown += SettingsSearchSuggestionsList_PreviewKeyDown;
            list.LostKeyboardFocus -= SettingsSearchFocus_LostKeyboardFocus;
            list.LostKeyboardFocus += SettingsSearchFocus_LostKeyboardFocus;
        }
    }

    private void SettingsSearchBox_TextChanged(UiAutoSuggestBox sender, UiAutoSuggestBoxTextChangedEventArgs args)
    {
        args.Handled = true;
        var generation = ++_settingsSearchGeneration;
        _highlightedSettingsSearchResult = null;
        var text = args.Text ?? string.Empty;
        var results = SettingsSearchIndex.Search(text);
        _settingsSearchSuggestions.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            QueueSettingsSearchPopupState(sender, text, open: false, generation);
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

        QueueSettingsSearchPopupState(sender, text, open: true, generation);
    }

    private void SettingsSearchBox_SuggestionChosen(UiAutoSuggestBox sender, UiAutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SettingsSearchSuggestion { Result: { } result } suggestion && IsCurrentSuggestion(suggestion))
        {
            _highlightedSettingsSearchResult = result;
        }

        args.Handled = true;
    }

    private void SettingsSearchBox_QuerySubmitted(UiAutoSuggestBox sender, UiAutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var result = CurrentHighlightedResult() ?? FirstCurrentResult();
        if (result is not null)
        {
            OpenSettingsSearchResult(result);
            args.Handled = true;
        }
    }

    private void SettingsSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TryConsumeSettingsSearchEscape())
        {
            e.Handled = true;
        }
    }

    // The window's Escape (OnPreviewKeyDown) runs before the box's own key handlers, so it asks here first: an open list
    // closes with focus kept in the box, and a box holding text is cleared. An empty box's Escape goes on to the window's
    // Escape order and the close guard.
    private bool TryConsumeSettingsSearchEscape()
    {
        // A hotkey capture owns every key and an IME composition owns Escape, wherever the focus is: both go on to the
        // window's Escape order, which cancels the capture or leaves the key to the IME.
        if (_capturing || _imeComposing)
        {
            return false;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        if (!IsWithin(SettingsSearchBox, focused) && !IsWithin(_settingsSearchSuggestionsList, focused))
        {
            return false;
        }

        if (SettingsSearchBox.IsSuggestionListOpen)
        {
            DismissSettingsSearchSuggestions(clearText: false);

            // Escape from inside the list would otherwise leave focus on the window, and the next Escape would close
            // Settings instead of clearing the text.
            SettingsSearchBox.Focus();
            return true;
        }

        if (!string.IsNullOrEmpty(SettingsSearchBox.Text))
        {
            SettingsSearchBox.Text = string.Empty;
            DismissSettingsSearchSuggestions(clearText: false);
            return true;
        }

        return false;
    }

    private void SettingsSearchSuggestionsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: SettingsSearchSuggestion suggestion } ||
            !TryGetCurrentResult(suggestion, out var result))
        {
            return;
        }

        OpenSettingsSearchResult(result);
        e.Handled = true;
    }

    private void SettingsSearchSuggestionsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _settingsSearchSuggestionsList?.SelectedItem is not SettingsSearchSuggestion suggestion ||
            !TryGetCurrentResult(suggestion, out var result))
        {
            return;
        }

        OpenSettingsSearchResult(result);
        e.Handled = true;
    }

    private void SettingsSearchFocus_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var generation = _settingsSearchGeneration;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (generation != _settingsSearchGeneration)
                {
                    return;
                }

                var focused = Keyboard.FocusedElement as DependencyObject;
                if (!IsWithin(SettingsSearchBox, focused) && !IsWithin(_settingsSearchSuggestionsList, focused))
                {
                    DismissSettingsSearchSuggestions(clearText: false);
                }
            });
    }

    private void QueueSettingsSearchPopupState(UiAutoSuggestBox sender, string text, bool open, int generation) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (generation == _settingsSearchGeneration &&
                    string.Equals(sender.Text, text, StringComparison.Ordinal) &&
                    (!open || sender.IsKeyboardFocusWithin))
                {
                    sender.IsSuggestionListOpen = open;
                }
            });

    private void DismissSettingsSearchSuggestions(bool clearText)
    {
        _settingsSearchGeneration++;
        SettingsSearchBox.IsSuggestionListOpen = false;
        _highlightedSettingsSearchResult = null;
        if (clearText)
        {
            SettingsSearchBox.Text = string.Empty;
        }
    }

    private void OpenSettingsSearchResult(SettingsSearchResult result)
    {
        var generation = ++_settingsSearchGeneration;
        SettingsSearchBox.IsSuggestionListOpen = false;
        _highlightedSettingsSearchResult = null;

        // Closing the list from inside it moves focus to the window, and the lost-focus dismissal would then cancel the
        // wait for a target inside an expander that is still opening. Focus stays in the box until the target takes it.
        SettingsSearchBox.Focus();
        ShowPage(result.Page);
        SelectDictionarySearchTab(result);
        ExpandSearchContainers(result.ControlName);
        UpdateLayout();

        if (FocusFirstUnsatisfiedRequirement(result))
        {
            return;
        }

        _ = FocusSearchTargetWhenVisibleAsync(result, generation);
    }

    private async Task FocusSearchTargetWhenVisibleAsync(SettingsSearchResult result, int generation)
    {
        var page = result.Page;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (!_settingsSearchClosed && generation == _settingsSearchGeneration && CurrentNavigationPage() == page)
        {
            if (FindName(result.ControlName) is FrameworkElement { IsVisible: true } target)
            {
                target.BringIntoView();
                _ = target.Focus();
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Task.Delay(50);
        }
    }

    private bool FocusFirstUnsatisfiedRequirement(SettingsSearchResult result)
    {
        foreach (var requirement in result.Requirements)
        {
            if (RequirementSatisfied(requirement))
            {
                continue;
            }

            if (requirement.Kind == SettingsSearchRequirementKind.View)
            {
                if (TrySatisfyViewRequirement(requirement))
                {
                    UpdateLayout();
                    continue;
                }

                // Signed out with the Azure CLI, the manual details can't open either: signing in is the way there.
                FocusAzureSignInRequirement(requirement with { Kind = SettingsSearchRequirementKind.Action });
                return true;
            }

            if (requirement.Kind == SettingsSearchRequirementKind.Action)
            {
                FocusAzureSignInRequirement(requirement);
                return true;
            }

            if (FindName(requirement.ControlName) is FrameworkElement target)
            {
                target.BringIntoView();
                _ = target.Focus();
                ShowSettingsSearchHiddenHint(target, requirement);
                return true;
            }
        }

        return false;
    }

    private bool RequirementSatisfied(SettingsSearchRequirement requirement)
    {
        if (requirement.Kind == SettingsSearchRequirementKind.View)
        {
            return !string.Equals(requirement.ControlName, "AzureManualToggleButton", StringComparison.Ordinal) ||
                CurrentAzureSettingsAccess.ShowEndpointPanel;
        }

        if (requirement.Kind == SettingsSearchRequirementKind.Action)
        {
            return CurrentAzureSettingsAccess.ShowDiscovery;
        }

        return FindName(requirement.ControlName) is ToggleButton { IsChecked: true };
    }

    private bool NeedsAzureManualDetails(SettingsSearchRequirement requirement) =>
        string.Equals(requirement.ControlName, "AzureManualToggleButton", StringComparison.Ordinal) &&
        CurrentAzureSettingsAccess.ShowManualToggleButton &&
        !CurrentAzureSettingsAccess.ShowEndpointPanel;

    private bool TrySatisfyViewRequirement(SettingsSearchRequirement requirement)
    {
        if (!NeedsAzureManualDetails(requirement))
        {
            return false;
        }

        _azureManualConfiguration = true;
        ApplyAzureSettingsAccess();
        return true;
    }

    private void FocusAzureSignInRequirement(SettingsSearchRequirement requirement)
    {
        var target = AzureSignInStatusRow.FocusPrimaryButton()
            ? (FrameworkElement)AzureSignInStatusRow
            : AzureSignInStatusRow;
        target.BringIntoView();
        ShowSettingsSearchHiddenHint(AzureSignInStatusRow, requirement);
    }

    private SettingsSearchResult? CurrentHighlightedResult() =>
        _highlightedSettingsSearchResult is { } highlighted &&
        _settingsSearchSuggestions.Any(suggestion => ReferenceEquals(suggestion.Result, highlighted))
            ? highlighted
            : null;

    private SettingsSearchResult? FirstCurrentResult() =>
        _settingsSearchSuggestions.FirstOrDefault(suggestion => suggestion.IsSelectable)?.Result;

    private bool TryGetCurrentResult(SettingsSearchSuggestion suggestion, out SettingsSearchResult result)
    {
        result = null!;
        if (!IsCurrentSuggestion(suggestion) || suggestion.Result is null)
        {
            return false;
        }

        result = suggestion.Result;
        return true;
    }

    private bool IsCurrentSuggestion(SettingsSearchSuggestion suggestion) =>
        suggestion.IsSelectable && _settingsSearchSuggestions.Any(current => ReferenceEquals(current, suggestion));

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
            case "OllamaDownloadModelBox":
                OllamaDownloadPanel.IsExpanded = true;
                break;
            case "OllamaContextSizeCombo":
            case "OllamaWholeVocabularyCheck":
                OllamaTuningExpander.IsExpanded = true;
                break;
            case "LmStudioContextSizeCombo":
            case "LmStudioWholeVocabularyCheck":
                LmStudioTuningExpander.IsExpanded = true;
                break;
            case "FoundryWholeVocabularyCheck":
                FoundryTuningExpander.IsExpanded = true;
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

        _settingsSearchHiddenHint = new TextBlock
        {
            Text = RequirementHint(requirement),
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

    private static string RequirementHint(SettingsSearchRequirement requirement) => requirement.Kind switch
    {
        SettingsSearchRequirementKind.Radio => $"Choose \"{StripRecommendedSuffix(requirement.Label)}\" to see this setting.",
        SettingsSearchRequirementKind.Action => "Sign in to Azure to see this setting.",
        _ => $"Turn on \"{requirement.Label}\" to see this setting.",
    };

    private static string StripRecommendedSuffix(string label) =>
        label.EndsWith(" (recommended)", StringComparison.Ordinal)
            ? label[..^" (recommended)".Length]
            : label;

    private void SettingsSearchHiddenParent_Changed(object sender, RoutedEventArgs e) => ClearSettingsSearchHiddenHint();

    // Called when the Azure settings view is republished: once signing in shows the discovery settings, the sign-in hint
    // no longer applies.
    private void RetireAzureSignInHintIfSignedIn()
    {
        if (_settingsSearchHiddenHint is not null &&
            ReferenceEquals(_settingsSearchHiddenHintPanel, AzureSignInStatusRow.Parent) &&
            CurrentAzureSettingsAccess.ShowDiscovery)
        {
            ClearSettingsSearchHiddenHint();
        }
    }

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

    private static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsWithin(DependencyObject? root, DependencyObject? value)
    {
        // A Hyperlink, like any content element, has no visual parent: VisualTreeHelper throws for it.
        for (var current = value; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }
}
