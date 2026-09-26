using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private bool ValidateSnippetAndProfileDraft()
    {
        ClearSnippetValidation();
        ClearProfileValidation();

        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            AppSettings.CreateDefault(),
            SnippetRows: SnippetDraftRows(),
            ProfileRows: ProfileDraftRows()));
        var issue = issues.FirstOrDefault(issue => issue.Severity == ValidationSeverity.Blocking);
        if (issue is null)
        {
            return true;
        }

        if (issue.Page == SettingsPage.VoiceSnippets)
        {
            ShowPage(SettingsPage.VoiceSnippets);
            if (issue.RowKey is not null && _snippetRows.FirstOrDefault(row => row.RowKey == issue.RowKey) is { } snippet)
            {
                SnippetList.SelectedItem = snippet;
                SnippetList.ScrollIntoView(snippet);
            }

            var target = issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateBox : SnippetPhraseBox;
            var panel = issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidation : SnippetPhraseValidation;
            var text = issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidationText : SnippetPhraseValidationText;
            var icon = issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidationIcon : SnippetPhraseValidationIcon;
            ShowValidation(panel, text, icon, target, issue.Message);
            target.Focus();
            return false;
        }

        if (issue.Page == SettingsPage.AppProfiles)
        {
            ShowPage(SettingsPage.AppProfiles);
            if (issue.RowKey is not null && _profileRows.FirstOrDefault(row => row.RowKey == issue.RowKey) is { } profile)
            {
                ProfileList.SelectedItem = profile;
                ProfileList.ScrollIntoView(profile);
            }

            var appsIssue = issue.Code == ValidationCode.ProfileAppsEmpty;
            var target = appsIssue ? (Control)ProfileAddAppButton : ProfileNameBox;
            var panel = appsIssue ? ProfileAppsValidation : ProfileNameValidation;
            var text = appsIssue ? ProfileAppsValidationText : ProfileNameValidationText;
            var icon = appsIssue ? ProfileAppsValidationIcon : ProfileNameValidationIcon;
            ShowValidation(panel, text, icon, target, issue.Message);
            target.Focus();
            return false;
        }

        return true;
    }

    private void ShowValidation(FrameworkElement panel, TextBlock text, Wpf.Ui.Controls.SymbolIcon icon, Control field, string message)
    {
        text.Text = message;
        icon.Foreground = TryFindResource("SystemFillColorCriticalBrush") as Brush ?? Brushes.Red;
        panel.Visibility = Visibility.Visible;
        AutomationProperties.SetHelpText(field, message);
        if (field.IsKeyboardFocusWithin)
        {
            AnnounceFrom(field, message);
        }
    }

    private static void HideValidation(FrameworkElement panel, TextBlock text, Control field)
    {
        text.Text = string.Empty;
        panel.Visibility = Visibility.Collapsed;
        AutomationProperties.SetHelpText(field, string.Empty);
    }

    private void RefreshTextChangesNotice()
    {
        var state = TextChangesNotice.Describe(PostCheck?.IsChecked == true, AiCleanupCheck?.IsChecked == true);
        if (SnippetTextChangesNotice is not null)
        {
            SnippetTextChangesNotice.Visibility = state.Show ? Visibility.Visible : Visibility.Collapsed;
            SnippetTextChangesInfoBar.Message = state.Message;
            SnippetTextChangesActionButton.Content = state.ActionText;
        }
    }

    private void TextChangesNoticeButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage.Advanced, nameof(PostCheck));
}
