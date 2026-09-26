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
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private bool _closeAccepted;
    private bool _closePromptShowing;
    private Task? _closeOperation;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeAccepted || _closed)
        {
            base.OnClosing(e);
            return;
        }

        CommitPendingGridEdits();
        RefreshFooterNow();
        var decision = ShouldAskBeforeClose(CloseTrigger.CloseButton);
        if (!decision.Ask)
        {
            _closeAccepted = true;
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(async () => await RequestCloseAsync(CloseTrigger.CloseButton)));
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

    private async Task<SavePreflightInput?> PrepareSavePreflightAsync()
    {
        CommitPendingEditorValues();
        var settings = CaptureDraftSettings();
        var dictionarySignature = DictionarySignature();
        var snippetSignature = SnippetSignature();
        var dictionaryDirty = _dictionaryLoad.HasChanges(dictionarySignature);
        var snippetsDirty = _snippetLoad.HasChanges(snippetSignature);
        var dictionaryRows = _dictionaryLoad.IsLoaded ? _rows.ToList() : new List<DictionaryRow>();
        var snippetRows = _snippetLoad.IsLoaded ? _snippetRows.ToList() : new List<SnippetRow>();
        var profileRows = _profileRows.ToList();
        var durationFields = DurationDraftFields();
        var issues = SettingsDraftValidator.Validate(new SettingsDraft(
            settings,
            _dictionaryLoad.IsLoaded ? [.. dictionaryRows.Select(ToDictionaryDraftRow)] : [],
            _snippetLoad.IsLoaded ? [.. snippetRows.Select(row => ToDraftRow(row))] : [],
            [.. profileRows.Select(ToProfileDraftRow)],
            durationFields));
        ClearAllInlineValidation();
        foreach (var warning in issues.Where(issue => issue.Severity == ValidationSeverity.Warning))
        {
            ShowValidationIssue(warning);
        }

        if (issues.FirstOrDefault(issue => issue.Severity == ValidationSeverity.Blocking) is { } blocking)
        {
            ShowValidationIssue(blocking);
            return null;
        }

        IReadOnlyList<DictionarySubmission>? dictionarySubmission = null;
        var entries = dictionaryDirty ? BuildDictionaryEntries(dictionaryRows, out dictionarySubmission) : null;
        var snippets = default(List<Snippet>);
        IReadOnlyList<SnippetSubmission>? snippetSubmission = null;
        if (snippetsDirty)
        {
            snippets = BuildSnippets(snippetRows, out snippetSubmission);
        }

        var profileSubmission = CaptureProfileSubmission(profileRows);
        var intents = new ExternalIntents(_externalAiCleanup.NewestRevision, _externalMicrophone.NewestRevision);
        var draftSections = BuildSaveDraftSections();
        var draftCapture = draftSections.CaptureNow();
        var draftSignature = SaveDraftSignature(draftSections, draftCapture);
        var preflight = new SavePreflightInput(settings, entries, dictionarySubmission, snippets, snippetSubmission, profileSubmission, intents, dictionarySignature, snippetSignature, draftSignature, draftCapture);
        if (!await ConfirmNewRemovalRulesAsync(dictionaryRows))
        {
            return null;
        }

        if (!await ConfirmRetentionChangeAsync(settings))
        {
            return null;
        }

        return preflight;
    }

    private async Task RequestCloseAsync(CloseTrigger trigger)
    {
        if (_closeOperation is { } running)
        {
            await running;
            return;
        }

        var operation = RunCloseAsync(trigger);
        _closeOperation = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_closeOperation, operation))
            {
                _closeOperation = null;
            }
        }
    }

    private async Task RunCloseAsync(CloseTrigger trigger)
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

        while (_saveInProgress)
        {
            FooterStatusText.Text = "Saving...";
            await Task.Delay(100);
            if (_closed)
            {
                return;
            }
        }

        CommitPendingGridEdits();
        RefreshFooterNow();
        var decision = ShouldAskBeforeClose(trigger);
        if (!decision.Ask)
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
                if (_saveInProgress)
                {
                    return;
                }

                dialog.PrimaryButtonText = "Saving...";
                _saveInProgress = true;
                FooterStatusText.Text = "Saving...";
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
                while (_saveInProgress)
                {
                    await Task.Delay(100);
                }

                CloseAfterPromptAccepted();
            }
        }
        finally
        {
            _closePromptShowing = false;
        }
    }

    private CloseDecision ShouldAskBeforeClose(CloseTrigger trigger)
    {
        var guard = SettingsCloseGuard.Decide(CurrentUnsavedSections(), trigger);
        if (trigger is CloseTrigger.UpdateRestart or CloseTrigger.SignOut or CloseTrigger.Shutdown)
        {
            return guard;
        }

        return _currentChanges.IsDirty || _settingsRecovered || _wordPackWorkspace?.HasUnsavedChanges == true
            ? new CloseDecision(true, guard.Prompt, guard.Choices.Count == 0 ? [CloseChoice.Save, CloseChoice.DiscardChanges, CloseChoice.KeepEditing] : guard.Choices, CloseChoice.KeepEditing)
            : guard;
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
            ImeComposing: _imeComposing,
            IsEditingCell(),
            IsAnyMenuOpen(this) || FocusedComboBox() is { IsDropDownOpen: true },
            FocusedSearchBox() is not null,
            FocusedSearchBox() is { Text.Length: > 0 }));
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
                if (FocusedComboBox() is { } combo)
                {
                    combo.IsDropDownOpen = false;
                }

                break;
            case EscapeAction.ClearSearch:
                if (FocusedSearchBox() is { } search)
                {
                    search.Clear();
                    search.Focus();
                }
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
            DurationDraftFields()));
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

    private IReadOnlyList<DurationDraftField> DurationDraftFields()
    {
        var fields = new List<DurationDraftField>();
        AddIfCustom(MaxDictationCombo, MaxDictationCustomBox, DurationChoiceKind.MaxDictation, SettingsPage.Advanced, nameof(MaxDictationCustomBox), 1, 120);
        AddIfCustom(IdleReleaseCombo, IdleReleaseCustomBox, DurationChoiceKind.IdleRelease, SettingsPage.Advanced, nameof(IdleReleaseCustomBox), 1, 1440);
        AddIfCustom(HistoryRetentionCombo, HistoryRetentionCustomBox, DurationChoiceKind.HistoryRetention, SettingsPage.History, nameof(HistoryRetentionCustomBox), 1, 3650);
        return fields;

        void AddIfCustom(ComboBox combo, Wpf.Ui.Controls.NumberBox customBox, DurationChoiceKind kind, SettingsPage page, string controlName, int min, int max)
        {
            if (combo.SelectedItem is not DurationChoice { IsCustom: true })
            {
                return;
            }

            var typed = customBox.Value;
            var value = typed is double number && !double.IsNaN(number) && !double.IsInfinity(number) && Math.Abs(number - Math.Round(number)) < 1e-9
                ? (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue)
                : int.MinValue;
            fields.Add(new DurationDraftField(page, controlName, value, min, max));
        }
    }

    private void ShowValidationIssue(ValidationIssue issue)
    {
        ShowPage(issue.Page, issue.ControlName);
        switch (issue.Code)
        {
            case ValidationCode.DictionarySpokenEmpty:
            case ValidationCode.DictionaryDuplicate:
                DictionaryTabs.SelectedItem = YourWordsTab;
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
                ShowPage(SettingsPage.VoiceSnippets);
                if (issue.RowKey is not null && _snippetRows.FirstOrDefault(row => row.RowKey == issue.RowKey) is { } snippet)
                {
                    SnippetList.SelectedItem = snippet;
                    SnippetList.ScrollIntoView(snippet);
                }

                var snippetTarget = issue.Code == ValidationCode.SnippetTextEmpty ? (Control)SnippetTemplateBox : SnippetPhraseBox;
                ShowValidation(
                    issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidation : SnippetPhraseValidation,
                    issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidationText : SnippetPhraseValidationText,
                    issue.Code == ValidationCode.SnippetTextEmpty ? SnippetTemplateValidationIcon : SnippetPhraseValidationIcon,
                    snippetTarget,
                    issue.Message);
                snippetTarget.Focus();
                break;
            case ValidationCode.ProfileNameEmpty:
            case ValidationCode.ProfileAppsEmpty:
                ShowPage(SettingsPage.AppProfiles);
                if (issue.RowKey is not null && _profileRows.FirstOrDefault(row => row.RowKey == issue.RowKey) is { } profile)
                {
                    ProfileList.SelectedItem = profile;
                    ProfileList.ScrollIntoView(profile);
                }

                var appsIssue = issue.Code == ValidationCode.ProfileAppsEmpty;
                var profileTarget = appsIssue ? (Control)ProfileAddAppButton : ProfileNameBox;
                ShowValidation(
                    appsIssue ? ProfileAppsValidation : ProfileNameValidation,
                    appsIssue ? ProfileAppsValidationText : ProfileNameValidationText,
                    appsIssue ? ProfileAppsValidationIcon : ProfileNameValidationIcon,
                    profileTarget,
                    issue.Message);
                profileTarget.Focus();
                break;
            case ValidationCode.ShortcutsIdentical:
                ShowValidation(ShortcutValidation, ShortcutValidationText, ShortcutValidationIcon, DictationOnlyHotkeyBox, issue.Message);
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
        HideValidation(ShortcutValidation, ShortcutValidationText, DictationOnlyHotkeyBox);
    }



    private static DictionaryDraftRow ToDictionaryDraftRow(DictionaryRow row) => new(
        RowKey: row.RowKey,
        Origin: row.Origin,
        Touched: row.Touched,
        Pattern: row.Pattern,
        Replacement: row.Replacement,
        LoadedPattern: row.LoadedPattern,
        LoadedReplacement: row.LoadedReplacement,
        WholeWord: row.WholeWord,
        Enabled: row.Enabled,
        LoadedWholeWord: row.LoadedWholeWord,
        LoadedEnabled: row.LoadedEnabled);

    private static ProfileDraftRow ToProfileDraftRow(ProfileRow row) => new(
        RowKey: row.RowKey,
        Origin: row.Origin,
        Touched: row.Touched,
        Name: row.Name,
        Apps: row.Processes,
        LoadedName: row.LoadedName,
        LoadedApps: row.LoadedProcesses,
        WritingStyle: row.WritingStyle,
        LoadedWritingStyle: row.LoadedWritingStyle,
        NewlineHandling: row.NewlineHandling,
        LoadedNewlineHandling: row.LoadedNewlineHandling);

    private Wpf.Ui.Controls.TextBox? FocusedSearchBox() =>
        Keyboard.FocusedElement is Wpf.Ui.Controls.TextBox box &&
        box.Name.Contains("Search", StringComparison.OrdinalIgnoreCase)
            ? box
            : null;

    private ComboBox? FocusedComboBox() =>
        FindVisualParent<ComboBox>(Keyboard.FocusedElement as DependencyObject);

    private void CommitPendingEditorValues()
    {
        CommitPendingGridEdits();
        if (Keyboard.FocusedElement is UIElement focused)
        {
            focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            focused.Focus();
        }
    }

    private async Task<bool> ConfirmNewRemovalRulesAsync(IReadOnlyList<DictionaryRow> rows)
    {
        var removals = rows.Select(ToDictionaryDraftRow)
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

    private async Task<bool> ConfirmRetentionChangeAsync(AppSettings settings)
    {
        if (_settingsRecovered)
        {
            return true;
        }

        var draftDays = settings.HistoryRetentionDays;
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
    private sealed record SavePreflightInput(
        AppSettings Settings,
        IReadOnlyList<DictionaryEntry>? Entries,
        IReadOnlyList<DictionarySubmission>? DictionarySubmission,
        IReadOnlyList<Snippet>? Snippets,
        IReadOnlyList<SnippetSubmission>? SnippetSubmission,
        IReadOnlyList<ProfileSubmission> ProfileSubmission,
        ExternalIntents Intents,
        string DictionarySignature,
        string SnippetSignature,
        string DraftSignature,
        SaveDraftSections.Capture DraftCapture);
}


