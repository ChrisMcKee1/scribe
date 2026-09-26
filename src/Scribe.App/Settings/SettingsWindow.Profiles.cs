using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

public partial class SettingsWindow
{
    private readonly VisibleAppService _visibleApps = new();
    private readonly ObservableCollection<AppPickerRow> _appPickerRows = new();
    private int _appPickerRequest;
    private ProfileRow? _appPickerOwner;

    private void LoadProfiles()
    {
        ProfileNewlineCombo.DisplayMemberPath = nameof(ProfileNewlineChoice.Label);
        ProfileNewlineCombo.ItemsSource = new[]
        {
            new ProfileNewlineChoice(null, "Use the Advanced setting"),
            new ProfileNewlineChoice(NewlineInjectionMode.SmartFlatten, "Automatic: one line in command windows"),
            new ProfileNewlineChoice(NewlineInjectionMode.AlwaysFlatten, "Always one line"),
            new ProfileNewlineChoice(NewlineInjectionMode.KeepNewlines, "Keep line breaks"),
        };

        foreach (var profile in _settings.Profiles)
        {
            _profileRows.Add(new ProfileRow
            {
                Name = profile.Name,
                Processes = string.Join(", ", ProgramNames.Normalize(profile.ProcessNames)),
                WritingStyle = profile.WritingStyle ?? string.Empty,
                NewlineHandling = profile.NewlineHandling,
                Origin = DraftRowOrigin.Saved,
                LoadedName = profile.Name,
                LoadedProcesses = string.Join(", ", ProgramNames.Normalize(profile.ProcessNames)),
                LoadedWritingStyle = profile.WritingStyle ?? string.Empty,
                LoadedNewlineHandling = profile.NewlineHandling,
            });
        }

        _loadedProfileRows = LoadedProfileDraftRowsFromRows();
        ProfileList.ItemsSource = _profileRows;
        AppPickerList.ItemsSource = _appPickerRows;
        RefreshProfileEmptyState();
        RefreshProfileRules();
    }

    private ProfileRow? SelectedProfile => ProfileList.SelectedItem as ProfileRow;

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearProfileValidation();
        InvalidateAppPicker();
        var row = SelectedProfile;
        ProfileEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshProfileEmptyState();
        RefreshProfileCommands();
        if (row is null)
        {
            ProfileAppsChips.ItemsSource = null;
            return;
        }

        _loadingProfile = true;
        try
        {
            ProfileNameBox.Text = row.Name;
            ProfileProcessesBox.Text = row.Processes;
            ProfileStyleBox.Text = row.WritingStyle;
            ProfileAppsChips.ItemsSource = ProfileAppChips.FromProgramNames(row.Processes);
            var choices = (ProfileNewlineChoice[])ProfileNewlineCombo.ItemsSource;
            ProfileNewlineCombo.SelectedItem =
                choices.FirstOrDefault(c => c.Mode == row.NewlineHandling) ?? choices[0];
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    private void ProfileAddButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ProfileAddButton,
            Placement = PlacementMode.Top,
        };

        var blank = new MenuItem { Header = ProfilePresetHeader("Blank profile", "Start with an empty app profile.") };
        AutomationProperties.SetName(blank, "Blank profile");
        AutomationProperties.SetHelpText(blank, "Start with an empty app profile.");
        blank.Click += (_, _) => AddBlankProfile();
        menu.Items.Add(blank);
        menu.Items.Add(new Separator());

        foreach (var preset in ProfilePresets.All)
        {
            var existing = FindProfileRow(preset.Profile.Name);
            var item = new MenuItem
            {
                Header = ProfilePresetHeader(preset.Profile.Name, preset.Description),
                IsEnabled = existing is null,
            };
            AutomationProperties.SetName(item, preset.Profile.Name);
            AutomationProperties.SetHelpText(item, preset.Description);

            if (existing is null)
            {
                item.Click += (_, _) => AddPresetProfile(preset);
            }

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static StackPanel ProfilePresetHeader(string title, string description) => new()
    {
        Children =
        {
            new TextBlock { Text = title },
            new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = System.Windows.SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            },
        },
    };

    private void AddBlankProfile()
    {
        var row = new ProfileRow { Name = "New profile", Origin = DraftRowOrigin.New, Touched = true };
        _profileRows.Add(row);
        ProfileList.SelectedItem = row;
        ProfileList.ScrollIntoView(row);
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
        RefreshProfileEmptyState();
        RefreshProfileRules();
    }

    private void AddPresetProfile(ProfilePresets.Preset preset)
    {
        var profile = ProfilePresets.Instantiate(preset);
        var row = new ProfileRow
        {
            Name = profile.Name,
            Processes = string.Join(", ", ProgramNames.Normalize(profile.ProcessNames)),
            WritingStyle = profile.WritingStyle ?? string.Empty,
            NewlineHandling = profile.NewlineHandling,
            Origin = DraftRowOrigin.New,
            Touched = true,
        };

        _profileRows.Add(row);
        ProfileList.SelectedItem = row;
        ProfileList.ScrollIntoView(row);
        RefreshProfileEmptyState();
        RefreshProfileRules();
    }

    private ProfileRow? FindProfileRow(string name) =>
        _profileRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private async void ProfileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        var confirmed = await ConfirmAsync(
            "Delete this profile?",
            "Nothing changes until you save.",
            "Delete profile");
        if (!confirmed)
        {
            return;
        }

        var index = ProfileList.SelectedIndex;
        _profileRows.Remove(row);
        if (_profileRows.Count > 0)
        {
            ProfileList.SelectedIndex = Math.Min(index, _profileRows.Count - 1);
        }

        RefreshProfileEmptyState();
        RefreshProfileRules();
    }

    private void ProfileMoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelectedProfile(-1);

    private void ProfileMoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelectedProfile(1);

    private void MoveSelectedProfile(int direction)
    {
        var index = ProfileList.SelectedIndex;
        var next = index + direction;
        if (index < 0 || next < 0 || next >= _profileRows.Count)
        {
            return;
        }

        _profileRows.Move(index, next);
        ProfileList.SelectedIndex = next;
        ProfileList.ScrollIntoView(_profileRows[next]);
        RefreshProfileCommands();
    }

    private void ProfileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.Name = ProfileNameBox.Text;
            row.Touched = true;
            HideValidation(ProfileNameValidation, ProfileNameValidationText, ProfileNameBox);
        }
    }

    private void ProfileProcessesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            ApplyProfileProcesses(row, ProfileProcessesBox.Text, markTouched: true);
            HideValidation(ProfileAppsValidation, ProfileAppsValidationText, ProfileAddAppButton);
        }
    }

    private void ProfileStyleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.WritingStyle = ProfileStyleBox.Text;
            row.Touched = true;
        }
    }

    private void ProfileNewlineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.NewlineHandling = (ProfileNewlineCombo.SelectedItem as ProfileNewlineChoice)?.Mode;
            row.Touched = true;
        }
    }

    private async void ProfileAddAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        var request = ++_appPickerRequest;
        _appPickerOwner = row;
        _appPickerRows.Clear();
        AppPickerEmptyText.Visibility = Visibility.Collapsed;
        AppPickerPanel.Visibility = Visibility.Visible;

        IReadOnlyList<AppPickerOption> options;
        try
        {
            var runningTask = Task.Run(() => _visibleApps.GetVisibleApps());
            var recentTask = Task.Run(() => RecentApps.From(_history.GetRecent(1000)));
            await Task.WhenAll(runningTask, recentTask);
            options = AppPickerOptions.Build(runningTask.Result, recentTask.Result, ProgramNames.Normalize(SplitProfileProcesses(row.Processes)));
        }
        catch (Exception)
        {
            options = [];
        }

        if (request != _appPickerRequest || !ReferenceEquals(_appPickerOwner, row) || !ReferenceEquals(SelectedProfile, row))
        {
            return;
        }

        foreach (var option in options)
        {
            _appPickerRows.Add(new AppPickerRow(option));
        }

        AppPickerEmptyText.Visibility = _appPickerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_appPickerRows.Count > 0)
        {
            AppPickerList.Focus();
            AppPickerList.SelectedIndex = 0;
        }
    }

    private void AppPickerAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } row || !ReferenceEquals(_appPickerOwner, row))
        {
            InvalidateAppPicker();
            return;
        }

        var apps = ProgramNames.Normalize(SplitProfileProcesses(row.Processes)).ToList();
        apps.AddRange(_appPickerRows.Where(option => option.IsSelected).Select(option => option.ProcessName));
        ApplyProfileProcesses(row, string.Join(", ", ProgramNames.Normalize(apps)), markTouched: true);
        ProfileProcessesBox.Text = row.Processes;
        InvalidateAppPicker();
        HideValidation(ProfileAppsValidation, ProfileAppsValidationText, ProfileAddAppButton);
    }

    private void AppPickerCancelButton_Click(object sender, RoutedEventArgs e) => InvalidateAppPicker();

    private void InvalidateAppPicker()
    {
        _appPickerRequest++;
        _appPickerOwner = null;
        _appPickerRows.Clear();
        AppPickerPanel.Visibility = Visibility.Collapsed;
    }

    private void ProfileAppChipRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } row || (sender as FrameworkElement)?.DataContext is not ProfileAppChip chip)
        {
            return;
        }

        var apps = SplitProfileProcesses(ProfileAppChips.RemoveGroup(row.Processes, chip.GroupKey));
        ApplyProfileProcesses(row, string.Join(", ", apps), markTouched: true);
        ProfileProcessesBox.Text = row.Processes;
        HideValidation(ProfileAppsValidation, ProfileAppsValidationText, ProfileAddAppButton);
    }

    private void ApplyProfileProcesses(ProfileRow row, string processes, bool markTouched)
    {
        row.Processes = string.Join(", ", ProgramNames.Normalize(SplitProfileProcesses(processes)));
        if (markTouched)
        {
            row.Touched = true;
        }

        if (SelectedProfile == row)
        {
            ProfileAppsChips.ItemsSource = ProfileAppChips.FromProgramNames(row.Processes);
        }
    }

    private static IEnumerable<string> SplitProfileProcesses(string? processes) =>
        (processes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void RefreshProfileEmptyState()
    {
        var hasSelection = SelectedProfile is not null;
        ProfileEditor.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        ProfileEmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        if (hasSelection)
        {
            return;
        }

        ProfileEmptyHint.Text = _profileRows.Count == 0
            ? "No app profiles yet. Add one to change how Scribe writes in a specific app."
            : "Select a profile to edit it.";
    }

    private void RefreshProfileCommands()
    {
        var index = ProfileList.SelectedIndex;
        ProfileDeleteButton.IsEnabled = index >= 0;
        ProfileMoveUpButton.IsEnabled = index > 0;
        ProfileMoveDownButton.IsEnabled = index >= 0 && index < _profileRows.Count - 1;
    }

    private void RefreshProfileRules()
    {
        var state = ProfileRules.Describe(AiCleanupCheck.IsChecked == true, _profileRows.Count);
        ProfileAiCleanupNotice.Visibility = state.ShowAiCleanupNotice ? Visibility.Visible : Visibility.Collapsed;
        ProfileAiCleanupInfoBar.Message = state.NoticeText ?? string.Empty;
        ProfileAiCleanupActionButton.Content = state.ActionText ?? ProfileRules.AiCleanupAction;
        ProfileOrderHint.Visibility = state.ShowFirstMatchHint ? Visibility.Visible : Visibility.Collapsed;
        RefreshProfileCommands();
    }

    private void ProfileAiCleanupButton_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage.AiCleanup);

    /// <summary>Builds the profile list to persist. The order is the ListBox order: first match wins.</summary>
    private List<AppProfile> BuildProfiles() =>
        ProfileBuilder.Build(
            _profileRows.Select(r => new ProfileBuilder.Row(
                r.Name, r.Processes, r.WritingStyle, r.NewlineHandling)).ToList());

    // What a Save stored for each profile row, read in the same moment as the rows BuildProfiles turned into the stored
    // profiles. A word pack Save awaits its preparation between reading the rows and committing, so an edit made meanwhile
    // isn't in what was stored and must stay unsaved (review of 7b722fe), as the snippets' submission does.
    private sealed record ProfileSubmission(
        ProfileRow Row, string? Name, string? Processes, string? WritingStyle, NewlineInjectionMode? NewlineHandling);

    private IReadOnlyList<ProfileSubmission> CaptureProfileSubmission() =>
        CaptureProfileSubmission(_profileRows.ToList());

    private static IReadOnlyList<ProfileSubmission> CaptureProfileSubmission(IReadOnlyList<ProfileRow> rows) =>
        [.. rows.Select(row => new ProfileSubmission(row, row.Name, row.Processes, row.WritingStyle, row.NewlineHandling))];

    // Each submitted row still in the list takes what was submitted as its saved baseline, in memory. A row edited since it
    // was submitted keeps its edit, now unsaved against that baseline, and stays touched.
    private void MarkProfileRowsSaved(IReadOnlyList<ProfileSubmission> submission)
    {
        foreach (var submitted in submission)
        {
            var row = submitted.Row;
            if (!_profileRows.Contains(row))
            {
                continue;
            }

            row.Origin = DraftRowOrigin.Saved;
            row.LoadedName = submitted.Name;
            row.LoadedProcesses = submitted.Processes;
            row.LoadedWritingStyle = submitted.WritingStyle;
            row.LoadedNewlineHandling = submitted.NewlineHandling;
            if (string.Equals(row.Name, submitted.Name, StringComparison.Ordinal) &&
                string.Equals(row.Processes, submitted.Processes, StringComparison.Ordinal) &&
                string.Equals(row.WritingStyle, submitted.WritingStyle, StringComparison.Ordinal) &&
                row.NewlineHandling == submitted.NewlineHandling)
            {
                row.Touched = false;
            }
        }

        // The whole submission is what is stored now, a profile deleted while the Save waited included.
        _loadedProfileRows = [.. submission.Select(submitted => new LoadedProfileDraftRow(
            submitted.Row.RowKey, submitted.Name, submitted.Processes, submitted.WritingStyle, submitted.NewlineHandling))];
    }

    private IReadOnlyList<ProfileDraftRow> ProfileDraftRows() =>
        _profileRows.Select(row => new ProfileDraftRow(
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
            LoadedNewlineHandling: row.LoadedNewlineHandling)).ToList();

    private void ClearProfileValidation()
    {
        HideValidation(ProfileNameValidation, ProfileNameValidationText, ProfileNameBox);
        HideValidation(ProfileAppsValidation, ProfileAppsValidationText, ProfileAddAppButton);
    }

    private sealed record ProfileNewlineChoice(NewlineInjectionMode? Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class AppPickerRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public AppPickerRow(AppPickerOption option)
        {
            ProcessName = option.ProcessName;
            Label = option.Label;
        }

        public string ProcessName { get; }
        public string Label { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
