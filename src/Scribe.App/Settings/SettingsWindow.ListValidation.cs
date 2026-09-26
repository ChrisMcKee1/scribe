using System.Windows;
using System.Windows.Controls;
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
            var validation = issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidation : SnippetPhraseValidation;
            ShowValidation(validation, issue.Message);
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

            var target = issue.Code == ValidationCode.ProfileAppsEmpty ? (Control)ProfileAddAppButton : ProfileNameBox;
            var validation = issue.Code == ValidationCode.ProfileAppsEmpty ? ProfileAppsValidation : ProfileNameValidation;
            ShowValidation(validation, issue.Message);
            target.Focus();
            return false;
        }

        return true;
    }

    private static void ShowValidation(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    private static void HideValidation(TextBlock target)
    {
        target.Text = string.Empty;
        target.Visibility = Visibility.Collapsed;
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

