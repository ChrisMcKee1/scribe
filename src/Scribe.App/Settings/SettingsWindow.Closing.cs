using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scribe.App.Infrastructure;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private bool _closeAccepted;
    private bool _closePromptShowing;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeAccepted || _closed)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = RequestCloseAsync(CloseTrigger.CloseButton);
    }

    private async Task SaveFromAcceleratorAsync()
    {
        if (_saveInProgress)
        {
            return;
        }

        _saveInProgress = true;
        try
        {
            if (await TrySaveWithConfirmationsAsync())
            {
                ShowInfo("Changes saved.");
            }
        }
        finally
        {
            _saveInProgress = false;
            ScheduleFooterRefresh();
        }
    }

    private async Task<bool> TrySaveWithConfirmationsAsync() =>
        await ConfirmDictionaryOverlapAsync() && await TrySaveAsync();

    private async Task RequestCloseAsync(CloseTrigger trigger)
    {
        if (_closePromptShowing)
        {
            return;
        }

        if (_capturing)
        {
            CancelCapture();
            return;
        }

        CommitPendingGridEdits();
        RefreshFooterNow();
        var decision = SettingsCloseGuard.Decide(CurrentUnsavedSections(), trigger);
        if (!decision.Ask && !_currentChanges.IsDirty)
        {
            CloseAfterPromptAccepted();
            return;
        }

        if (!decision.Ask && _currentChanges.IsDirty)
        {
            CloseAfterPromptAccepted();
            return;
        }

        _closePromptShowing = true;
        try
        {
            var prompt = SettingsClosePrompt.For(trigger);
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = prompt.Title,
                Content = prompt.Body,
                PrimaryButtonText = prompt.PrimaryButton,
                SecondaryButtonText = prompt.DiscardButton,
                CloseButtonText = prompt.CancelButton,
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                SecondaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                Owner = this,
            };
            dialog.Loaded += (_, _) =>
            {
                if (MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Close) is { } keepEditing)
                {
                    if (MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Primary) is { } primary)
                    {
                        primary.IsDefault = false;
                    }

                    keepEditing.IsDefault = true;
                    keepEditing.Focus();
                }
            };

            var result = await dialog.ShowDialogAsync();
            if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                dialog.PrimaryButtonText = "Saving...";
                _saveInProgress = true;
                try
                {
                    if (await TrySaveWithConfirmationsAsync())
                    {
                        CloseAfterPromptAccepted();
                    }
                }
                finally
                {
                    _saveInProgress = false;
                    ScheduleFooterRefresh();
                }
            }
            else if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                CloseAfterPromptAccepted();
            }
        }
        finally
        {
            _closePromptShowing = false;
        }
    }

    private void CloseAfterPromptAccepted()
    {
        _closeAccepted = true;
        Close();
    }

    private UnsavedSections CurrentUnsavedSections()
    {
        var sections = UnsavedSections.None;
        if (_wordPackWorkspace?.HasUnsavedChanges == true)
        {
            sections |= UnsavedSections.Libraries;
        }

        if (_dictionaryLoad.IsLoaded && _dictionaryLoad.HasChanges(DictionarySignature()))
        {
            sections |= UnsavedSections.Dictionary;
        }

        if (_snippetLoad.IsLoaded && _snippetLoad.HasChanges(SnippetSignature()))
        {
            sections |= UnsavedSections.Snippets;
        }

        return sections;
    }

    private async Task HandleEscapeAsync()
    {
        var action = SettingsCloseGuard.NextEscape(new EscapeState(
            _capturing,
            ImeComposing: false,
            IsEditingCell(),
            IsAnyMenuOpen(this),
            ReferenceEquals(Keyboard.FocusedElement, LibrarySearchBox),
            !string.IsNullOrEmpty(LibrarySearchBox.Text)));
        switch (action)
        {
            case EscapeAction.CancelHotkeyCapture:
                CancelCapture();
                break;
            case EscapeAction.CancelEdit:
                CancelPendingGridEdits();
                break;
            case EscapeAction.CloseMenu:
                CloseOpenMenus(this);
                break;
            case EscapeAction.ClearSearch:
                LibrarySearchBox.Clear();
                LibrarySearchBox.Focus();
                break;
            default:
                await RequestCloseAsync(CloseTrigger.Escape);
                break;
        }
    }

    private void CommitPendingGridEdits()
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        LibraryGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        LibraryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        LibraryTermsGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        LibraryTermsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    private void CancelPendingGridEdits()
    {
        DictionaryGrid.CancelEdit(DataGridEditingUnit.Cell);
        DictionaryGrid.CancelEdit(DataGridEditingUnit.Row);
        LibraryGrid.CancelEdit(DataGridEditingUnit.Cell);
        LibraryGrid.CancelEdit(DataGridEditingUnit.Row);
        LibraryTermsGrid.CancelEdit(DataGridEditingUnit.Cell);
        LibraryTermsGrid.CancelEdit(DataGridEditingUnit.Row);
    }

    private bool IsEditingCell() =>
        FindVisualParent<DataGridCell>(Keyboard.FocusedElement as DependencyObject)?.IsEditing == true;

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static bool IsAnyMenuOpen(DependencyObject root)
    {
        if (root is ContextMenu { IsOpen: true } or MenuItem { IsSubmenuOpen: true })
        {
            return true;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (IsAnyMenuOpen(VisualTreeHelper.GetChild(root, i)))
            {
                return true;
            }
        }

        return false;
    }

    private static void CloseOpenMenus(DependencyObject root)
    {
        if (root is ContextMenu menu)
        {
            menu.IsOpen = false;
        }

        if (root is MenuItem item)
        {
            item.IsSubmenuOpen = false;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CloseOpenMenus(VisualTreeHelper.GetChild(root, i));
        }
    }

    private bool ValidateDraftBeforeSave()
    {
        ClearAllInlineValidation();
        var settings = CaptureDraftSettings();
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            settings,
            _dictionaryLoad.IsLoaded ? DictionaryDraftRows() : [],
            _snippetLoad.IsLoaded ? SnippetDraftRows() : [],
            ProfileDraftRows(),
            DurationDraftFields(settings)));
        foreach (var warning in issues.Where(issue => issue.Severity == ValidationSeverity.Warning))
        {
            ShowValidationIssue(warning);
        }

        if (issues.FirstOrDefault(issue => issue.Severity == ValidationSeverity.Blocking) is { } blocking)
        {
            ShowValidationIssue(blocking);
            return false;
        }

        return true;
    }

    private IReadOnlyList<DurationDraftField> DurationDraftFields(AppSettings settings)
    {
        var fields = new List<DurationDraftField>();
        AddIfCustom(MaxDictationCombo, SettingsPage.Advanced, nameof(MaxDictationCustomBox), settings.MaxDictationMinutes, 1, 120);
        AddIfCustom(IdleReleaseCombo, SettingsPage.Advanced, nameof(IdleReleaseCustomBox), settings.ReleaseModelsAfterIdleMinutes, 1, 1440);
        AddIfCustom(HistoryRetentionCombo, SettingsPage.History, nameof(HistoryRetentionCustomBox), settings.HistoryRetentionDays, 1, 3650);
        return fields;

        void AddIfCustom(ComboBox combo, SettingsPage page, string controlName, int value, int min, int max)
        {
            if (combo.SelectedItem is DurationChoice { IsCustom: true })
            {
                fields.Add(new DurationDraftField(page, controlName, value, min, max));
            }
        }
    }

    private void ShowValidationIssue(ValidationIssue issue)
    {
        ShowPage(issue.Page, issue.ControlName);
        switch (issue.Code)
        {
            case ValidationCode.DictionarySpokenEmpty:
            case ValidationCode.DictionaryDuplicate:
                if (issue.RowKey is not null && _rows.FirstOrDefault(row => row.RowKey == issue.RowKey) is { } row)
                {
                    DictionaryGrid.SelectedItem = row;
                    DictionaryGrid.ScrollIntoView(row);
                }

                ShowValidation(DictionaryValidation, DictionaryValidationText, DictionaryValidationIcon, DictionaryGrid, issue.Message);
                DictionaryGrid.Focus();
                break;
            case ValidationCode.SnippetTriggerEmpty:
            case ValidationCode.SnippetTextEmpty:
            case ValidationCode.SnippetDuplicate:
                ValidateSnippetAndProfileDraft();
                break;
            case ValidationCode.ProfileNameEmpty:
            case ValidationCode.ProfileAppsEmpty:
                ValidateSnippetAndProfileDraft();
                break;
            case ValidationCode.ShortcutsIdentical:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                DictationOnlyHotkeyBox.Focus();
                break;
            case ValidationCode.FoundryEndpointInvalid:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                AzureEndpointBox.Focus();
                break;
            case ValidationCode.CustomEndpointInvalid:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                CustomEndpointBox.Focus();
                break;
            case ValidationCode.CustomModelEmpty:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                CustomModelBox.Focus();
                break;
            case ValidationCode.DeploymentEmpty:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                AzureDeploymentBox.Focus();
                break;
            case ValidationCode.TenantEmpty:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                SpTenantBox.Focus();
                break;
            case ValidationCode.ClientIdEmpty:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                SpClientIdBox.Focus();
                break;
            case ValidationCode.ClientSecretEmpty:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                SpClientSecretBox.Focus();
                break;
            case ValidationCode.DurationOutOfRange:
                ShowInfo(issue.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
                FocusControl(issue.ControlName);
                break;
        }
    }

    private void FocusControl(string controlName)
    {
        if (FindName(controlName) is Control control)
        {
            control.Focus();
        }
    }

    private void ClearAllInlineValidation()
    {
        HideValidation(DictionaryValidation, DictionaryValidationText, DictionaryGrid);
        ClearSnippetValidation();
        ClearProfileValidation();
    }

    private async Task<bool> ConfirmNewRemovalRulesAsync()
    {
        var removals = DictionaryDraftRows()
            .Where(SettingsDraftValidator.NeedsDictionaryRemovalConfirmation)
            .Select(row => (row.Pattern ?? string.Empty).Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (removals.Count == 0)
        {
            return true;
        }

        var list = string.Join(", ", removals.Select(text => $"\"{text}\""));
        return await ConfirmRiskyAsync(
            "Remove these phrases from your dictations?",
            $"Scribe will remove these phrases instead of replacing them: {list}.",
            "Remove them",
            "Keep editing");
    }

    private async Task<bool> ConfirmRetentionChangeAsync()
    {
        if (_settingsRecovered)
        {
            return true;
        }

        var draftDays = SelectedDurationValue(HistoryRetentionCombo, HistoryRetentionCustomBox, _committedSettings.HistoryRetentionDays);
        var confirmation = HistoryRetentionChange.Describe(_committedSettings.HistoryRetentionDays, draftDays);
        if (confirmation is null)
        {
            return true;
        }

        return await ConfirmRiskyAsync(
            confirmation.Title,
            confirmation.Body,
            confirmation.DeleteAndSaveText,
            confirmation.KeepEditingText);
    }
}
