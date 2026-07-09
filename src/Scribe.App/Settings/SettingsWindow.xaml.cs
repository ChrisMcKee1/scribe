using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Scribe.App.Infrastructure;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.App.Settings;

/// <summary>
/// Modeless settings editor. Loads the persisted <see cref="AppSettings"/> and the user
/// dictionary, lets the user change the microphone, hotkey, behaviour toggles, text-insertion
/// method and decode threads, and edit the dictionary inline. On save it persists everything,
/// reconciles the "launch at logon" registration, and calls back into the dictation controller
/// so the new binding and dictionary take effect without a restart.
/// </summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IAudioCaptureService _audio;
    private readonly IDictionaryRepository _dictionary;
    private readonly IDictionaryLibraryService _libraries;
    private readonly ISnippetRepository _snippets;
    private readonly IHistoryRepository _history;
    private readonly ITextCleanupService _cleanup;
    private readonly IAzureFoundryDiscovery _azureDiscovery;
    private readonly ICleanupFailureLog _failureLog;
    private readonly Action<OverlayPosition> _previewOverlay;
    private readonly Action<AppSettings> _applySettings;
    private readonly UpdateService? _updates;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<DictionaryRow> _rows = new();
    private readonly ObservableCollection<LibraryRow> _libraryRows = new();
    // Cached snapshot of the loaded libraries (built-in + custom) so the preview panel resolves a
    // selected row without re-reading files on every click. Kept in sync on import/remove.
    private readonly List<DictionaryLibrary> _loadedLibraries = new();
    private readonly ObservableCollection<SnippetRow> _snippetRows = new();
    private bool _loadingSnippet;
    private readonly ObservableCollection<ProfileRow> _profileRows = new();
    private bool _loadingProfile;
    private readonly ObservableCollection<FailureRow> _failures = new();
    private readonly Dictionary<string, CleanupModel> _foundryCuratedByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AzureFoundryDeployment> _azureModelMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _foundryModelOp;
    private bool _azureAutoListed;

    private HotkeyBinding _pendingBinding;
    private readonly HashSet<Key> _heldModifiers = new();
    private bool _capturing;
    private bool _finalized;
    private bool _loadingUi;

    public SettingsWindow(
        ISettingsRepository settingsRepository,
        IAudioCaptureService audio,
        IDictionaryRepository dictionary,
        IDictionaryLibraryService libraries,
        ISnippetRepository snippets,
        IHistoryRepository history,
        ITextCleanupService cleanup,
        IAzureFoundryDiscovery azureDiscovery,
        ICleanupFailureLog failureLog,
        Action<OverlayPosition> previewOverlay,
        Action<AppSettings> applySettings,
        UpdateService? updates = null)
    {
        _settingsRepository = settingsRepository;
        _audio = audio;
        _dictionary = dictionary;
        _libraries = libraries;
        _snippets = snippets;
        _history = history;
        _cleanup = cleanup;
        _azureDiscovery = azureDiscovery;
        _failureLog = failureLog;
        _previewOverlay = previewOverlay;
        _applySettings = applySettings;
        _updates = updates;

        _settings = settingsRepository.Load();
        _pendingBinding = _settings.Hotkey;

        // Match the system light/dark theme + accent colour and enable the Mica backdrop.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        InitializeComponent();

        // Type-to-filter behaviour for the model pickers (browse on click, search on type).
        AttachComboFilter(AiModelBox, UpdateAiModelHint);
        AttachComboFilter(AzureModelBox, UpdateAzureDeploymentHint);

        PopulateDevices();
        PopulateChoices();
        LoadFromSettings();
        LoadDictionary();
        LoadLibraries();
        LoadSnippets();
        LoadProfiles();
        LoadFailures();
        LoadPerformanceStats();

        // Reflect live cleanup-engine state (download progress, ready, errors) in the UI.
        _cleanup.StatusChanged += OnCleanupStatusChanged;
        Closed += OnClosed;
        RefreshAiStatus();
        InitializeUpdateCard();
    }

    // --- Updates card (General) --------------------------------------------------------------

    private void InitializeUpdateCard()
    {
        UpdateStatusText.Text = _updates?.PendingVersion is { } pending
            ? $"Scribe {UpdateService.RunningVersion} — {pending} is downloaded and ready to install."
            : $"Scribe {UpdateService.RunningVersion} — updates are checked at startup and installed when you quit.";
        UpdateApplyButton.Visibility = _updates?.PendingVersion is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null)
        {
            UpdateStatusText.Text = $"Scribe {UpdateService.RunningVersion} (dev build — updates apply to installed builds only).";
            return;
        }

        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            UpdateStatusText.Text = await _updates.CheckAndDownloadAsync();
            UpdateApplyButton.Visibility = _updates.PendingVersion is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private void UpdateApplyButton_Click(object sender, RoutedEventArgs e)
    {
        // On success this never returns — the process exits, the update applies, and Scribe
        // relaunches on the new version.
        if (_updates is null || !_updates.ApplyNowAndRestart())
        {
            UpdateStatusText.Text = "Couldn't restart into the update — it will install when you quit Scribe.";
        }
    }

    // --- Navigation rail -------------------------------------------------------------------

    // Nav order must match the ListBoxItem order in XAML.
    private Grid[] SectionPanels =>
    [
        SectionGeneral, SectionDictation, SectionOverlay, SectionAi,
        SectionDictionary, SectionLibraries, SectionSnippets, SectionProfiles, SectionDiagnostics,
    ];

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent (SelectedIndex is set in XAML) before the panels parse.
        if (SectionDiagnostics is null)
        {
            return;
        }

        var panels = SectionPanels;
        var selected = Math.Clamp(NavList.SelectedIndex, 0, panels.Length - 1);
        for (var i = 0; i < panels.Length; i++)
        {
            panels[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Navigates the rail to the given section, e.g. to show where a save error lives.</summary>
    private void ShowSection(Grid section)
    {
        var index = Array.IndexOf(SectionPanels, section);
        if (index >= 0)
        {
            NavList.SelectedIndex = index;
        }
    }

    private void PopulateDevices()
    {
        var choices = new List<DeviceChoice> { new(null, "System default (recommended)") };
        try
        {
            foreach (var device in _audio.GetInputDevices())
            {
                var label = device.IsDefault ? $"{device.Name} — default" : device.Name;
                choices.Add(new DeviceChoice(device.Id, label));
            }
        }
        catch
        {
            // Device enumeration can fail transiently; the default choice is always available.
        }

        DeviceCombo.ItemsSource = choices;
        DeviceCombo.SelectedItem =
            choices.FirstOrDefault(c => c.Id == _settings.InputDeviceId) ?? choices[0];
    }

    private void PopulateChoices()
    {
        ModeCombo.ItemsSource = new[] { "Hold", "Toggle" };

        InjectionCombo.DisplayMemberPath = nameof(InjectionChoice.Label);
        InjectionCombo.ItemsSource = new[]
        {
            new InjectionChoice(InjectionMethod.UnicodeType, "Unicode typing (recommended)"),
            new InjectionChoice(InjectionMethod.ClipboardPaste, "Clipboard paste"),
        };

        NewlineCombo.DisplayMemberPath = nameof(NewlineChoice.Label);
        NewlineCombo.ItemsSource = new[]
        {
            new NewlineChoice(NewlineInjectionMode.SmartFlatten, "Smart — one line in terminals (recommended)"),
            new NewlineChoice(NewlineInjectionMode.AlwaysFlatten, "Always one line — never send Enter"),
            new NewlineChoice(NewlineInjectionMode.KeepNewlines, "Keep line breaks exactly as dictated"),
        };
    }

    private void LoadFromSettings()
    {
        _loadingUi = true;
        try
        {
            HotkeyBox.Text = HotkeyCapture.Describe(_pendingBinding);
            ModeCombo.SelectedIndex = _pendingBinding.Mode == HotkeyMode.Toggle ? 1 : 0;

            OverlayCheck.IsChecked = _settings.ShowOverlay;
            LoadOverlayPosition(_settings.OverlayPosition);
            VadCheck.IsChecked = _settings.UseVoiceActivityDetection;
            AutoStopCheck.IsChecked = _settings.AutoStopOnSilence;
            PostCheck.IsChecked = _settings.ApplyPostProcessing;
            LaunchCheck.IsChecked = _settings.LaunchOnLogin;
            StoreAudioCheck.IsChecked = _settings.StoreAudioHistory;
            BeamSearchCheck.IsChecked = _settings.UseHighAccuracyDecoding;

            var items = (InjectionChoice[])InjectionCombo.ItemsSource;
            InjectionCombo.SelectedItem =
                items.FirstOrDefault(i => i.Method == _settings.InjectionMethod) ?? items[0];

            var newlineItems = (NewlineChoice[])NewlineCombo.ItemsSource;
            NewlineCombo.SelectedItem =
                newlineItems.FirstOrDefault(i => i.Mode == _settings.NewlineHandling) ?? newlineItems[0];

            ThreadsSlider.Value = Math.Clamp(_settings.DecodeThreads, 0, 16);
            UpdateThreadsLabel();

            LoadAiSettings();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void LoadAiSettings()
    {
        AiCleanupCheck.IsChecked = _settings.EnableAiCleanup;

        AiProviderCombo.DisplayMemberPath = nameof(ProviderChoice.Label);
        AiProviderCombo.ItemsSource = new[]
        {
            new ProviderChoice(CleanupProvider.FoundryLocal, "On-device — Foundry Local"),
            new ProviderChoice(CleanupProvider.AzureFoundry, "Microsoft Foundry — your Azure sign-in"),
            new ProviderChoice(CleanupProvider.OpenAiCompatible, "Custom endpoint — Ollama, LM Studio, OpenRouter…"),
        };

        // Foundry model picker: searchable list of curated aliases. The live Foundry Local catalog
        // merges in on demand (panel show / "Check & list models") without blocking the window open.
        _foundryCuratedByAlias.Clear();
        foreach (var curated in CleanupModelCatalog.Curated)
        {
            _foundryCuratedByAlias[curated.Alias] = curated;
        }
        SetComboItems(AiModelBox, CleanupModelCatalog.Curated.Select(m => m.Alias).ToList());

        var providers = (ProviderChoice[])AiProviderCombo.ItemsSource;
        AiProviderCombo.SelectedItem =
            providers.FirstOrDefault(p => p.Provider == _settings.AiCleanupProvider) ?? providers[0];

        var savedModel = CleanupModelCatalog.Curated
            .FirstOrDefault(m => string.Equals(m.Alias, _settings.AiCleanupModel, StringComparison.OrdinalIgnoreCase));
        AiModelBox.Text = savedModel?.Alias
            ?? (string.IsNullOrWhiteSpace(_settings.AiCleanupModel)
                ? CleanupModelCatalog.Curated[0].Alias
                : _settings.AiCleanupModel.Trim());

        // Manual endpoint/deployment/key are the source of truth Save reads; discovery just autofills
        // them. Populate from saved settings (key is decrypted in memory by AppSettings).
        AzureEndpointBox.Text = _settings.AiCleanupAzureEndpoint ?? string.Empty;
        AzureDeploymentBox.Text = _settings.AiCleanupAzureDeployment ?? string.Empty;
        AzureApiKeyBox.Password = _settings.AiCleanupAzureApiKey ?? string.Empty;
        AzureTenantBox.Text = _settings.AiCleanupAzureTenantId ?? string.Empty;

        CustomEndpointBox.Text = _settings.AiCleanupCustomEndpoint ?? string.Empty;
        CustomModelBox.Text = _settings.AiCleanupCustomModel ?? string.Empty;
        CustomApiKeyBox.Password = _settings.AiCleanupCustomApiKey ?? string.Empty;

        // Open Advanced automatically when manual auth is configured, so an override isn't hidden away.
        AzureAdvancedExpander.IsExpanded =
            !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureApiKey) ||
            !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureTenantId);

        // Reflect the saved deployment in the Model picker before any sign-in discovery runs.
        SeedAzureModelFromSettings();

        // Show the effective writing style: the user's saved guidance, or the default when blank so
        // they can see and edit exactly what gets sent to the model.
        AiWritingStyleBox.Text = CleanupPrompt.ResolveWritingStyle(_settings.AiCleanupWritingStyle);

        // Cleanup prompt: the style selector plus the editable frontier/local guardrail prompts. Each box
        // shows the effective prompt (the user's override, or the built-in default) so it is visible and tunable.
        AiPromptStyleCombo.DisplayMemberPath = nameof(PromptStyleChoice.Label);
        AiPromptStyleCombo.ItemsSource = new[]
        {
            new PromptStyleChoice(CleanupPromptStyle.Auto, "Automatic (recommended), by provider"),
            new PromptStyleChoice(CleanupPromptStyle.Frontier, "Frontier, for cloud and capable models"),
            new PromptStyleChoice(CleanupPromptStyle.Local, "Local, for on-device and small models"),
        };
        var promptStyles = (PromptStyleChoice[])AiPromptStyleCombo.ItemsSource;
        AiPromptStyleCombo.SelectedItem =
            promptStyles.FirstOrDefault(s => s.Style == _settings.AiCleanupPromptStyle) ?? promptStyles[0];
        AiFrontierPromptBox.Text = CleanupPrompt.ResolveFrontierPrompt(_settings.AiCleanupFrontierPrompt);
        AiLocalPromptBox.Text = CleanupPrompt.ResolveLocalPrompt(_settings.AiCleanupLocalPrompt);

        UpdateAiProviderPanels();
        UpdateAiEnabledState();
        UpdateAiModelHint();
        UpdateAzureDeploymentHint();

        // Best-effort: merge the live on-device catalog + loaded status in without blocking window open.
        if (SelectedProvider == CleanupProvider.FoundryLocal)
        {
            _ = RefreshFoundryModelsAsync();
        }
        else if (SelectedProvider == CleanupProvider.AzureFoundry)
        {
            // Detect an existing Azure sign-in and auto-list deployments so search works immediately.
            _ = ProbeAzureSignInAsync();
        }
    }

    private void LoadDictionary()
    {
        foreach (var entry in _dictionary.GetAll())
        {
            _rows.Add(new DictionaryRow
            {
                Id = entry.Id,
                Pattern = entry.Pattern,
                Replacement = entry.Replacement,
                WholeWord = entry.WholeWord,
                Enabled = entry.Enabled,
            });
        }

        DictionaryGrid.ItemsSource = _rows;
        _dictionarySnapshot = DictionarySignature();
    }

    // --- Libraries -----------------------------------------------------------------------

    private void LoadLibraries()
    {
        var enabled = new HashSet<string>(_settings.EnabledDictionaryLibraryIds, StringComparer.OrdinalIgnoreCase);
        _loadedLibraries.Clear();
        _loadedLibraries.AddRange(_libraries.GetLibraries());

        _libraryRows.Clear();
        foreach (var library in _loadedLibraries)
        {
            _libraryRows.Add(new LibraryRow
            {
                Id = library.Id,
                Name = library.Name,
                Category = library.Category,
                Terms = library.EnabledEntryCount,
                Source = library.BuiltIn ? "Built-in" : "Custom",
                BuiltIn = library.BuiltIn,
                Enabled = enabled.Contains(library.Id),
            });
        }

        LibraryGrid.ItemsSource = _libraryRows;

        // Preview the first library so the detail panel is never blank when the page opens.
        if (_libraryRows.Count > 0)
        {
            LibraryGrid.SelectedIndex = 0;
        }
        else
        {
            UpdateLibraryDetail(null);
        }
    }

    private void LibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);

    // Drives the right-hand preview panel from the selected library row: header plus a read-only grid
    // of its spoken-to-written terms. Resolving from the cached snapshot keeps clicking through
    // libraries instant (no per-click file reads).
    private void UpdateLibraryDetail(LibraryRow? row)
    {
        var library = row is null
            ? null
            : _loadedLibraries.FirstOrDefault(l => string.Equals(l.Id, row.Id, StringComparison.OrdinalIgnoreCase));

        if (library is null)
        {
            LibraryTermsGrid.ItemsSource = null;
            LibraryTermsGrid.Visibility = Visibility.Collapsed;
            LibraryDetailEmpty.Visibility = Visibility.Visible;
            LibraryDetailName.Text = string.Empty;
            LibraryDetailMeta.Text = string.Empty;
            LibraryDetailDesc.Text = string.Empty;
            LibraryDetailDesc.Visibility = Visibility.Collapsed;
            return;
        }

        var count = library.Entries.Count;
        LibraryDetailName.Text = library.Name;
        LibraryDetailMeta.Text =
            $"{library.Category} \u00b7 {count} {(count == 1 ? "term" : "terms")} \u00b7 {(library.BuiltIn ? "Built-in" : "Custom")}";
        LibraryDetailDesc.Text = library.Description ?? string.Empty;
        LibraryDetailDesc.Visibility =
            string.IsNullOrWhiteSpace(library.Description) ? Visibility.Collapsed : Visibility.Visible;

        LibraryTermsGrid.ItemsSource = library.Entries;
        LibraryTermsGrid.Visibility = Visibility.Visible;
        LibraryDetailEmpty.Visibility = Visibility.Collapsed;
    }

    // The enabled-set persisted in settings: the ids of every ticked library still in the list.
    private List<string> CollectEnabledLibraryIds() =>
        _libraryRows.Where(r => r.Enabled).Select(r => r.Id).ToList();

    private void LibraryImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DictionaryLibrary imported;
        try
        {
            var csv = File.ReadAllText(dialog.FileName);
            var suggestedName = Path.GetFileNameWithoutExtension(dialog.FileName);
            imported = _libraries.Import(csv, suggestedName);
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not import that library:\n{ex.Message}");
            return;
        }

        // Newly imported libraries start switched off, like the built-in ones, so an import never
        // silently changes how dictation is spelled until the user turns it on and saves.
        _loadedLibraries.Add(imported);
        var newRow = new LibraryRow
        {
            Id = imported.Id,
            Name = imported.Name,
            Category = imported.Category,
            Terms = imported.EnabledEntryCount,
            Source = "Custom",
            BuiltIn = false,
            Enabled = false,
        };
        _libraryRows.Add(newRow);
        LibraryGrid.SelectedItem = newRow; // preview it immediately
        LibraryGrid.ScrollIntoView(newRow);

        ShowInfo($"Imported \"{imported.Name}\" with {imported.EnabledEntryCount} " +
                 $"{(imported.EnabledEntryCount == 1 ? "term" : "terms")}. Turn it on, then save to apply.");
    }

    private void LibraryExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLibrary() is not { } library)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = library.Id + ".csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, DictionaryLibraryCsv.Export(library), CsvEncoding);
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not export the library:\n{ex.Message}");
        }
    }

    private async void LibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            ShowInfo("Select a library to remove.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (row.BuiltIn)
        {
            ShowThemedMessage(
                "Built-in library",
                $"\"{row.Name}\" is built in and can't be removed. Turn it off with its checkbox instead.");
            return;
        }

        if (!await ConfirmAsync(
                "Remove library",
                $"Remove the imported library \"{row.Name}\"? This deletes it from Scribe. " +
                "You can import it again later from the original file.",
                "Remove"))
        {
            return;
        }

        try
        {
            _libraries.Remove(row.Id);
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not remove that library:\n{ex.Message}");
            return;
        }

        _loadedLibraries.RemoveAll(l => string.Equals(l.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        _libraryRows.Remove(row);
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);
        ShowInfo($"Removed \"{row.Name}\".");
    }

    // Resolves the grid's selected row back to its loaded library from the cached snapshot,
    // surfacing a friendly hint when nothing is selected or the file has since gone missing.
    private DictionaryLibrary? SelectedLibrary()
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            ShowInfo("Select a library first.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return null;
        }

        var library = _loadedLibraries.FirstOrDefault(l =>
            string.Equals(l.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            ShowInfo("That library is no longer available.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }

        return library;
    }

    // Save skips sections the user never touched, so a pre-existing data problem in one section
    // (e.g. a duplicate dictionary entry loaded from disk) can never block saving a change made in
    // another. The signatures capture everything the section's SaveAll would write.
    private string DictionarySignature() => string.Join(
        "", _rows.Select(r => $"{r.Id}|{r.Pattern}|{r.Replacement}|{r.WholeWord}|{r.Enabled}"));

    private string SnippetSignature() => string.Join(
        "", _snippetRows.Select(r => $"{r.Id}|{r.Phrase}|{r.Template}|{r.Enabled}"));

    private string _dictionarySnapshot = string.Empty;
    private string _snippetSnapshot = string.Empty;

    private void LoadPerformanceStats()
    {
        try
        {
            var entries = _history.GetRecent(1000);
            var stats = Scribe.Core.Diagnostics.DictationStats.Compute(entries, DateTimeOffset.UtcNow.AddDays(-7));
            if (stats is null)
            {
                return; // keep the friendly empty-state text
            }

            StatsSummaryText.Text =
                $"{stats.Count} dictation{(stats.Count == 1 ? string.Empty : "s")}, " +
                $"{stats.TotalAudio.TotalMinutes:0.#} min of speech. Decode only — lower is faster; " +
                "RTF is decode time relative to audio length.";
            StatDecodeP50.Text = FormatSeconds(stats.DecodeP50Ms);
            StatDecodeP95.Text = FormatSeconds(stats.DecodeP95Ms);
            StatRtfP50.Text = $"{stats.RtfP50:0.00}×";
            StatRtfP95.Text = $"{stats.RtfP95:0.00}×";
            StatsGrid.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Stats are a nicety; never block the settings window over them.
            System.Diagnostics.Debug.WriteLine($"Performance stats unavailable: {ex.Message}");
        }

        static string FormatSeconds(double ms) =>
            ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000.0:0.0} s";
    }

    private void LoadFailures()
    {
        _failures.Clear();
        foreach (var failure in _failureLog.GetRecent(50))
        {
            _failures.Add(new FailureRow
            {
                When = failure.TimestampUtc.ToLocalTime().ToString("g"),
                Model = (string.IsNullOrWhiteSpace(failure.Model) ? failure.Provider : failure.Model)
                        ?? string.Empty,
                Reason = failure.Reason,
            });
        }

        FailuresGrid.ItemsSource = _failures;
        NoFailuresText.Visibility = _failures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearFailuresButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _failureLog.Clear();
            _failures.Clear();
            NoFailuresText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not clear the failure log:\n{ex.Message}");
        }
    }

    // --- Hotkey capture ------------------------------------------------------------------

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCapture();
        HotkeyBox.Focus();
    }

    private void BeginCapture()
    {
        _capturing = true;
        _finalized = false;
        _heldModifiers.Clear();
        HotkeyBox.Text = "Press a key…";
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        if (HotkeyCapture.IsModifierKey(key))
        {
            // Defer: a lone modifier is finalized on key-up, but a modifier+key combo is
            // finalized when the non-modifier key arrives below.
            _heldModifiers.Add(key);
            HotkeyBox.Text = HotkeyCapture.Describe(_pendingBinding) + "  (press a key…)";
            return;
        }

        Finalize(HotkeyCapture.FromKeyEvent(e, SelectedMode));
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing || _finalized)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!HotkeyCapture.IsModifierKey(key))
        {
            return;
        }

        _heldModifiers.Remove(key);
        if (_heldModifiers.Count == 0)
        {
            // Released a modifier with nothing else held: bind it as a push-to-talk key.
            Finalize(HotkeyCapture.FromKeyEvent(e, SelectedMode));
        }
    }

    private void Finalize(HotkeyBinding binding)
    {
        _pendingBinding = binding;
        _finalized = true;
        _capturing = false;
        _heldModifiers.Clear();
        HotkeyBox.Text = HotkeyCapture.Describe(binding);
        Keyboard.ClearFocus();
    }

    private void CancelCapture()
    {
        _capturing = false;
        _heldModifiers.Clear();
        HotkeyBox.Text = HotkeyCapture.Describe(_pendingBinding);
        Keyboard.ClearFocus();
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            CancelCapture();
        }
    }

    private HotkeyMode SelectedMode => ModeCombo.SelectedIndex == 1 ? HotkeyMode.Toggle : HotkeyMode.Hold;

    // --- Threads -------------------------------------------------------------------------

    private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateThreadsLabel();

    private void UpdateThreadsLabel()
    {
        if (ThreadsLabel is null)
        {
            return;
        }

        var value = (int)ThreadsSlider.Value;
        ThreadsLabel.Text = value == 0 ? "Auto" : value.ToString();
    }

    // --- AI cleanup ----------------------------------------------------------------------

    private CleanupProvider SelectedProvider =>
        (AiProviderCombo.SelectedItem as ProviderChoice)?.Provider ?? CleanupProvider.FoundryLocal;

    private CleanupPromptStyle SelectedPromptStyle =>
        (AiPromptStyleCombo.SelectedItem as PromptStyleChoice)?.Style ?? CleanupPromptStyle.Auto;

    private void AiCleanupCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        UpdateAiEnabledState();
    }

    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        UpdateAiProviderPanels();
        RefreshAiStatus();

        if (SelectedProvider == CleanupProvider.FoundryLocal)
        {
            _ = RefreshFoundryModelsAsync();
        }
        else if (SelectedProvider == CleanupProvider.AzureFoundry)
        {
            _ = ProbeAzureSignInAsync();
        }
    }

    // --- Filterable model dropdowns --------------------------------------------------------
    // The pickers are editable ComboBoxes doing double duty: click the chevron to browse every
    // discovered model, or type to quick-filter the open list. Users shouldn't need to know a
    // deployment's name up front — browsing is the primary path, search the accelerator.

    private bool _suppressComboFilter;

    private void AttachComboFilter(ComboBox box, Action onTextChanged)
    {
        box.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                if (_suppressComboFilter || _loadingUi)
                {
                    return;
                }

                onTextChanged();

                // Only typing filters; programmatic Text updates and selection commits don't.
                if (!box.IsKeyboardFocusWithin)
                {
                    return;
                }

                var text = box.Text?.Trim() ?? string.Empty;
                box.Items.Filter = text.Length == 0
                    ? null
                    : item => item?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

                if (!box.IsDropDownOpen && box.Items.Count > 0)
                {
                    box.IsDropDownOpen = true;

                    // Opening the dropdown selects the editable text, so the next keystroke would
                    // wipe the query; park the caret at the end instead.
                    if (box.Template.FindName("PART_EditableTextBox", box) is TextBox editor)
                    {
                        editor.SelectionStart = editor.Text.Length;
                        editor.SelectionLength = 0;
                    }
                }
            }));
    }

    private void ModelCombo_DropDownOpened(object sender, EventArgs e)
    {
        // A hand-opened dropdown always shows the full list, not the residue of the last search.
        if (sender is ComboBox box)
        {
            box.Items.Filter = null;
        }
    }

    /// <summary>Replaces a picker's items while preserving the visible (typed or saved) text.</summary>
    private void SetComboItems(ComboBox box, IReadOnlyList<string> items)
    {
        _suppressComboFilter = true;
        try
        {
            var text = box.Text;
            box.ItemsSource = items;
            box.Items.Filter = null;
            box.Text = text;
        }
        finally
        {
            _suppressComboFilter = false;
        }
    }

    private void AiModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        // The editable Text lags SelectionChanged; read it after the combo commits.
        Dispatcher.BeginInvoke(UpdateAiModelHint);
    }

    private void AzureModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        if (AzureModelBox.SelectedItem is string display)
        {
            Dispatcher.BeginInvoke(() => ApplyAzureSelection(display));
        }
    }

    private void AzureModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Covers a deployment name typed in full without picking from the list.
        if (!_loadingUi)
        {
            ApplyAzureSelection(AzureModelBox.Text);
        }
    }

    // A discovered deployment autofills the manual endpoint/deployment fields, which are what Save reads.
    private void ApplyAzureSelection(string? display)
    {
        if (!string.IsNullOrWhiteSpace(display) &&
            _azureModelMap.TryGetValue(display.Trim(), out var deployment))
        {
            AzureEndpointBox.Text = deployment.Endpoint;
            AzureDeploymentBox.Text = deployment.DeploymentName;
        }

        UpdateAzureDeploymentHint();
    }

    private async void AiCheckButton_Click(object sender, RoutedEventArgs e)
    {
        AiCheckButton.IsEnabled = false;
        AiStatusText.Text = "Checking Foundry Local…";
        try
        {
            var available = await Task.Run(() => _cleanup.ProbeAsync());
            AiStatusText.Text = available
                ? "Foundry Local is available. The selected model downloads on first use (about 1–2 GB)."
                : "Foundry Local was not detected. Install it (winget install Microsoft.FoundryLocal), then check again.";

            if (available)
            {
                await RefreshFoundryModelsAsync();
            }
        }
        catch
        {
            AiStatusText.Text = "Couldn't verify Foundry Local. Make sure it's installed and try again.";
        }
        finally
        {
            AiCheckButton.IsEnabled = true;
        }
    }

    // Merges the live Foundry Local catalog into the searchable picker and refreshes the loaded-model
    // status. Best-effort: if Foundry Local isn't installed the curated alias list stays in place.
    private async Task RefreshFoundryModelsAsync()
    {
        try
        {
            var models = await _cleanup.ListFoundryModelsAsync();
            if (models.Count > 0)
            {
                // Keep the currently typed alias selectable even if it isn't in the live catalog.
                var current = AiModelBox.Text?.Trim();
                var aliases = models.Select(m => m.Alias).ToList();
                if (!string.IsNullOrWhiteSpace(current) &&
                    !aliases.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    aliases.Add(current);
                }

                SetComboItems(AiModelBox, aliases);
            }

            UpdateFoundryLoadedText(models.FirstOrDefault(m => m.Loaded)?.Alias);
        }
        catch
        {
            // Leave the curated list and existing status untouched on any failure.
        }
    }

    private void UpdateFoundryLoadedText(string? loadedAlias)
    {
        if (AiLoadedModelText is null)
        {
            return;
        }

        AiLoadedModelText.Text = string.IsNullOrWhiteSpace(loadedAlias)
            ? "No on-device model is loaded yet."
            : $"Loaded: {loadedAlias}";

        if (AiUnloadButton is not null)
        {
            AiUnloadButton.IsEnabled = !_foundryModelOp && !string.IsNullOrWhiteSpace(loadedAlias);
        }
    }

    private async void AiLoadButton_Click(object sender, RoutedEventArgs e)
    {
        var alias = AiModelBox.Text?.Trim();
        if (_foundryModelOp || string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        _foundryModelOp = true;
        AiLoadButton.IsEnabled = false;
        AiUnloadButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(message => AiStatusText.Text = message);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ok = await _cleanup.LoadFoundryModelAsync(alias, progress, cts.Token);
            if (!ok)
            {
                AiStatusText.Text = $"Couldn't load {alias}. Make sure Foundry Local is installed.";
            }
        }
        catch
        {
            AiStatusText.Text = $"Couldn't load {alias}.";
        }
        finally
        {
            _foundryModelOp = false;
            AiLoadButton.IsEnabled = true;
            await RefreshFoundryModelsAsync();
        }
    }

    private async void AiUnloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_foundryModelOp)
        {
            return;
        }

        _foundryModelOp = true;
        AiLoadButton.IsEnabled = false;
        AiUnloadButton.IsEnabled = false;
        AiStatusText.Text = "Unloading the on-device model…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var loaded = await _cleanup.GetLoadedFoundryModelAsync(cts.Token);
            var ok = await _cleanup.UnloadFoundryModelAsync(loaded, cts.Token);
            AiStatusText.Text = ok
                ? "Unloaded. No on-device model is resident."
                : "Nothing was loaded to unload.";
        }
        catch
        {
            AiStatusText.Text = "Couldn't unload the on-device model.";
        }
        finally
        {
            _foundryModelOp = false;
            AiLoadButton.IsEnabled = true;
            await RefreshFoundryModelsAsync();
        }
    }

    private async void AzureRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        AzureStatusText.Text = "Signing in and listing your Azure deployments…";
        await ListAzureDeploymentsAsync();
    }

    // Probes whether the user is already signed in to Azure (reusing az login / DefaultAzureCredential)
    // and reflects it in the UI: if signed in we show the identity, relabel the button to "Refresh
    // models", and list deployments automatically so the search box just works — no forced sign-in click.
    // Best-effort and non-blocking; runs when the Azure panel is shown.
    private async Task ProbeAzureSignInAsync()
    {
        AzureStatusText.Text = "Checking your Azure sign-in…";
        AzureSignInStatus status;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var tenantId = NullIfBlank(AzureTenantBox.Text);
            status = await Task.Run(() => _azureDiscovery.GetSignInStatusAsync(tenantId, cts.Token), cts.Token);
        }
        catch
        {
            status = new AzureSignInStatus(false, null);
        }

        if (status.IsSignedIn)
        {
            AzureRefreshButton.Content = "Refresh models";
            AzureStatusText.Text = string.IsNullOrWhiteSpace(status.Account)
                ? "Signed in to Azure. Listing your deployments…"
                : $"Signed in as {status.Account}. Listing your deployments…";

            // Auto-list once per window so the picker is populated without a manual click; the manual
            // "Refresh models" button re-lists on demand afterwards.
            if (!_azureAutoListed)
            {
                _azureAutoListed = true;
                await ListAzureDeploymentsAsync();
            }
        }
        else
        {
            AzureRefreshButton.Content = "Sign in & find models";
            AzureStatusText.Text =
                "Not signed in to Azure. Run 'az login' (or install the Azure CLI below), then choose Find models.";
        }
    }

    // Shared by the manual Refresh button and the auto-list-on-sign-in path.
    private async Task ListAzureDeploymentsAsync()
    {
        AzureRefreshButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var tenantId = NullIfBlank(AzureTenantBox.Text);
            var deployments = await Task.Run(() => _azureDiscovery.DiscoverAsync(tenantId, cts.Token), cts.Token);
            _azureAutoListed = true;

            _azureModelMap.TryGetValue(AzureModelBox.Text?.Trim() ?? string.Empty, out var previous);
            SetAzureDeployments(
                deployments,
                preferEndpoint: NullIfBlank(AzureEndpointBox.Text) ?? previous?.Endpoint,
                preferDeployment: NullIfBlank(AzureDeploymentBox.Text) ?? previous?.DeploymentName);

            AzureStatusText.Text = deployments.Count == 0
                ? "No text-capable deployments found. Realtime/audio/embedding models can't do cleanup — deploy a chat model (e.g. gpt-4.1-mini) on your Azure resource."
                : $"Found {deployments.Count} compatible deployment(s). Choose one to use for cleanup.";
        }
        catch (OperationCanceledException)
        {
            AzureStatusText.Text = "Listing Azure deployments timed out. Please try again.";
        }
        catch
        {
            AzureStatusText.Text = "Couldn't list deployments. Run 'az login' and make sure you have access to a deployment.";
        }
        finally
        {
            AzureRefreshButton.IsEnabled = true;
        }
    }

    private async void AzureCliButton_Click(object sender, RoutedEventArgs e)
    {
        AzureCliButton.IsEnabled = false;
        AzureCliStatusText.Text = "Installing or updating the Azure CLI via winget… this can take a minute.";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var installer = new AzureCliInstaller();
            var (_, message) = await installer.InstallOrUpdateAsync(cts.Token);
            AzureCliStatusText.Text = message;
        }
        catch (OperationCanceledException)
        {
            AzureCliStatusText.Text =
                "Azure CLI install timed out. Try again, or install it from https://aka.ms/installazurecliwindows.";
        }
        catch
        {
            AzureCliStatusText.Text =
                "Couldn't run the installer. Install the Azure CLI from https://aka.ms/installazurecliwindows.";
        }
        finally
        {
            AzureCliButton.IsEnabled = true;
        }
    }

    private void SetAzureDeployments(
        IReadOnlyList<AzureFoundryDeployment> deployments, string? preferEndpoint, string? preferDeployment)
    {
        var items = BuildAzureModelItems(deployments);
        SetComboItems(AzureModelBox, items);

        string? selected = null;
        if (!string.IsNullOrWhiteSpace(preferDeployment))
        {
            foreach (var item in items)
            {
                var d = _azureModelMap[item];
                if (string.Equals(d.DeploymentName, preferDeployment, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(preferEndpoint) ||
                     string.Equals(d.Endpoint, preferEndpoint, StringComparison.OrdinalIgnoreCase)))
                {
                    selected = item;
                    break;
                }
            }
        }

        if (selected is not null)
        {
            AzureModelBox.Text = selected;
            ApplyAzureSelection(selected);
        }
        else if (string.IsNullOrWhiteSpace(preferEndpoint) && string.IsNullOrWhiteSpace(preferDeployment)
                 && items.Count > 0)
        {
            // Nothing entered yet — pick the first discovered deployment and let it autofill the fields.
            AzureModelBox.Text = items[0];
            ApplyAzureSelection(items[0]);
        }
        else
        {
            // Keep the user's manually entered endpoint/deployment; don't overwrite with an unrelated match.
            UpdateAzureDeploymentHint();
        }
    }

    // Rebuilds the display→deployment map and returns the deduped searchable display strings.
    private List<string> BuildAzureModelItems(IReadOnlyList<AzureFoundryDeployment> deployments)
    {
        _azureModelMap.Clear();
        var items = new List<string>(deployments.Count);
        foreach (var deployment in deployments)
        {
            // Always show which Foundry account/project serves the deployment, so two deployments
            // of the same model in different projects are tellable apart at a glance (and the user
            // knows which endpoint a pick will fill in). The saved-settings stand-in has no account
            // name and renders as the bare deployment.
            var baseLabel = string.IsNullOrWhiteSpace(deployment.AccountName)
                ? deployment.DisplayName
                : $"{deployment.DisplayName}  ({deployment.AccountName})";

            var label = baseLabel;
            if (_azureModelMap.ContainsKey(label))
            {
                // Same deployment name in the same-named account: fall back to the subscription.
                label = $"{baseLabel}  —  {deployment.SubscriptionName}";
                var i = 2;
                while (_azureModelMap.ContainsKey(label))
                {
                    label = $"{baseLabel}  —  {deployment.SubscriptionName} ({i++})";
                }
            }

            _azureModelMap[label] = deployment;
            items.Add(label);
        }

        return items;
    }

    private void SeedAzureModelFromSettings()
    {
        var endpoint = _settings.AiCleanupAzureEndpoint?.Trim();
        var deployment = _settings.AiCleanupAzureDeployment?.Trim();
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(deployment))
        {
            return;
        }

        // A lightweight stand-in so the Model picker shows the saved choice until sign-in discovery
        // replaces it. Empty AccountName makes Detail render the endpoint; ModelName == DeploymentName
        // makes DisplayName render just the deployment.
        var current = new AzureFoundryDeployment(
            SubscriptionName: string.Empty,
            ResourceGroup: string.Empty,
            AccountName: string.Empty,
            Kind: string.Empty,
            Endpoint: endpoint,
            DeploymentName: deployment,
            ModelName: deployment,
            ModelVersion: null,
            Location: string.Empty);

        var items = BuildAzureModelItems(new[] { current });
        SetComboItems(AzureModelBox, items);
        AzureModelBox.Text = items[0];
    }

    private void UpdateAiProviderPanels()
    {
        if (FoundryPanel is null || AzurePanel is null || CustomPanel is null)
        {
            return;
        }

        var provider = SelectedProvider;
        FoundryPanel.Visibility = provider == CleanupProvider.FoundryLocal ? Visibility.Visible : Visibility.Collapsed;
        AzurePanel.Visibility = provider == CleanupProvider.AzureFoundry ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = provider == CleanupProvider.OpenAiCompatible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAiEnabledState()
    {
        var on = AiCleanupCheck.IsChecked == true;
        AiProviderCombo.IsEnabled = on;
        FoundryPanel.IsEnabled = on;
        AzurePanel.IsEnabled = on;
        CustomPanel.IsEnabled = on;
        AiWritingStyleBox.IsEnabled = on;
        ResetWritingStyleButton.IsEnabled = on;
        AiPromptStyleCombo.IsEnabled = on;
        AiFrontierPromptBox.IsEnabled = on;
        ResetFrontierPromptButton.IsEnabled = on;
        AiLocalPromptBox.IsEnabled = on;
        ResetLocalPromptButton.IsEnabled = on;
    }

    private void ResetWritingStyleButton_Click(object sender, RoutedEventArgs e) =>
        AiWritingStyleBox.Text = CleanupPrompt.DefaultWritingStyle;

    // Prompt-style selector has no live side effects; the choice is applied on Save with the other
    // cleanup settings. The handler exists only because the XAML binds SelectionChanged.
    private void AiPromptStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private async void ResetFrontierPromptButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ConfirmAsync("Restore frontier prompt",
                    "Replace the frontier prompt with Scribe's built-in default? Your local prompt is not affected.",
                    "Restore frontier prompt"))
            {
                AiFrontierPromptBox.Text = CleanupPrompt.DefaultFrontierPrompt;
            }
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Restore failed", $"Couldn't restore the frontier prompt: {ex.Message}");
        }
    }

    private async void ResetLocalPromptButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ConfirmAsync("Restore local prompt",
                    "Replace the local prompt with Scribe's built-in default? Your frontier prompt is not affected.",
                    "Restore local prompt"))
            {
                AiLocalPromptBox.Text = CleanupPrompt.DefaultLocalPrompt;
            }
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Restore failed", $"Couldn't restore the local prompt: {ex.Message}");
        }
    }

    // Normalizes a prompt text box for comparison/storage: unify newlines and trim, so an unedited box
    // (WPF returns CRLF line breaks) compares equal to the LF-based default and is stored as blank.
    private static string NormalizePrompt(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    private void UpdateAiModelHint()
    {
        if (AiModelHint is null)
        {
            return;
        }

        var alias = AiModelBox.Text?.Trim() ?? string.Empty;
        if (!_foundryCuratedByAlias.TryGetValue(alias, out var model))
        {
            AiModelHint.Text = string.Empty;
            return;
        }

        // Lead with the benchmark badge when this model is a golden-suite winner so the
        // recommendation is visible the moment it is selected, not just in the panel hint above.
        AiModelHint.Text = string.IsNullOrEmpty(model.Recommendation)
            ? model.Hint
            : $"Recommended, {model.Recommendation}. {model.Hint}";
    }

    private void UpdateAzureDeploymentHint()
    {
        if (AzureDeploymentHint is null)
        {
            return;
        }

        var key = AzureModelBox.Text?.Trim() ?? string.Empty;
        AzureDeploymentHint.Text = _azureModelMap.TryGetValue(key, out var deployment)
            ? deployment.Detail
            : "Sign in to list your models, or enter one under Advanced.";
    }

    private void OnCleanupStatusChanged() => Dispatcher.BeginInvoke(new Action(RefreshAiStatus));

    private void RefreshAiStatus()
    {
        var detail = _cleanup.StatusDetail;
        if (_cleanup.Status == CleanupStatus.Disabled || string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        // Surface the live engine status on whichever provider is actually running.
        if (_settings.AiCleanupProvider == CleanupProvider.AzureFoundry)
        {
            AzureStatusText.Text = detail;
        }
        else
        {
            AiStatusText.Text = detail;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cleanup.StatusChanged -= OnCleanupStatusChanged;
        Closed -= OnClosed;
    }

    // --- Themed dialogs / inline notifications -------------------------------------------

    /// <summary>
    /// Shows a Fluent-themed confirm dialog (two buttons) and returns true only when the user picks the
    /// primary action. Used for the individually-confirmed prompt resets so one restore never touches the other.
    /// </summary>
    private async Task<bool> ConfirmAsync(string title, string content, string confirmText)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            Owner = this,
        };
        return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>
    /// Shows a Fluent-themed message dialog that matches the rest of the window, replacing the
    /// dated Win32 <see cref="System.Windows.MessageBox"/>. Fire-and-forget so existing synchronous
    /// click handlers stay simple; the dialog itself is modal to this window.
    /// </summary>
    private void ShowThemedMessage(string title, string content)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "OK",
            IsSecondaryButtonEnabled = false,
            IsCloseButtonEnabled = false,
            Owner = this,
        };
        _ = dialog.ShowDialogAsync();
    }

    /// <summary>
    /// Raises the shared inline notification at the top of the content area and auto-dismisses it
    /// after a few seconds. Used for non-blocking success and summary messages instead of a modal.
    /// </summary>
    private void ShowInfo(string message, Wpf.Ui.Controls.InfoBarSeverity severity = Wpf.Ui.Controls.InfoBarSeverity.Success)
    {
        InfoNotice.Title = string.Empty;
        InfoNotice.Message = message;
        InfoNotice.Severity = severity;
        InfoNotice.IsOpen = true;

        _infoDismissTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        _infoDismissTimer.Stop();
        _infoDismissTimer.Tick -= DismissInfo;
        _infoDismissTimer.Tick += DismissInfo;
        _infoDismissTimer.Start();
    }

    private void DismissInfo(object? sender, EventArgs e)
    {
        _infoDismissTimer?.Stop();
        InfoNotice.IsOpen = false;
    }

    private System.Windows.Threading.DispatcherTimer? _infoDismissTimer;

    // --- Save / cancel -------------------------------------------------------------------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Commit any in-progress grid edit first so validation sees the latest input.
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        LibraryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // Only validate and save the dictionary/snippets when the user actually changed them.
        // Pre-existing bad data in an untouched section (e.g. a duplicate entry that was loaded
        // from disk) must never block saving a change made on a different page.
        var dictionaryDirty = DictionarySignature() != _dictionarySnapshot;
        var snippetsDirty = SnippetSignature() != _snippetSnapshot;

        // Validate the dictionary before touching anything: a duplicate spoken form would violate
        // the unique index, and the user deserves a pointer to the offending row rather than a
        // database error after half the settings were applied. Jump to the section that owns the
        // problem first — the dialog is meaningless while another page is showing.
        List<DictionaryEntry>? entries = null;
        DictionaryRow? duplicateRow = null;
        if (dictionaryDirty)
        {
            entries = BuildDictionaryEntries(out duplicateRow);
        }

        if (duplicateRow is not null)
        {
            ShowSection(SectionDictionary);
            DictionaryGrid.SelectedItem = duplicateRow;
            DictionaryGrid.ScrollIntoView(duplicateRow);
            ShowThemedMessage(
                "Duplicate dictionary entry",
                $"\"{duplicateRow.Pattern.Trim()}\" appears more than once in your dictionary.\n\n" +
                "Each spoken word or phrase can only have one replacement. Edit or remove the " +
                "highlighted row, then save again.");
            return;
        }

        List<Snippet>? snippets = null;
        SnippetRow? duplicateSnippet = null;
        if (snippetsDirty)
        {
            snippets = BuildSnippets(out duplicateSnippet);
        }

        if (duplicateSnippet is not null)
        {
            ShowSection(SectionSnippets);
            SnippetList.SelectedItem = duplicateSnippet;
            SnippetList.ScrollIntoView(duplicateSnippet);
            ShowThemedMessage(
                "Duplicate snippet trigger",
                $"\"{duplicateSnippet.Phrase.Trim()}\" is used as the trigger for more than one snippet.\n\n" +
                "Each trigger phrase can only expand to one template. Edit or remove the highlighted " +
                "snippet, then save again.");
            return;
        }

        try
        {
            var device = (DeviceChoice?)DeviceCombo.SelectedItem;
            _settings.InputDeviceId = device?.Id;
            _settings.InputDeviceName = device?.Id is null ? null : StripDefaultSuffix(device.Name);

            _settings.Hotkey = _pendingBinding with { Mode = SelectedMode };
            _settings.ShowOverlay = OverlayCheck.IsChecked == true;
            _settings.OverlayPosition = SelectedOverlayPosition;
            _settings.UseVoiceActivityDetection = VadCheck.IsChecked == true;
            _settings.AutoStopOnSilence = AutoStopCheck.IsChecked == true;
            _settings.ApplyPostProcessing = PostCheck.IsChecked == true;
            _settings.LaunchOnLogin = LaunchCheck.IsChecked == true;
            _settings.StoreAudioHistory = StoreAudioCheck.IsChecked == true;
            _settings.UseHighAccuracyDecoding = BeamSearchCheck.IsChecked == true;
            _settings.InjectionMethod =
                ((InjectionChoice?)InjectionCombo.SelectedItem)?.Method ?? InjectionMethod.UnicodeType;
            _settings.NewlineHandling =
                ((NewlineChoice?)NewlineCombo.SelectedItem)?.Mode ?? NewlineInjectionMode.SmartFlatten;
            _settings.Profiles = BuildProfiles();
            _settings.EnabledDictionaryLibraryIds = CollectEnabledLibraryIds();
            _settings.DecodeThreads = (int)ThreadsSlider.Value;

            _settings.EnableAiCleanup = AiCleanupCheck.IsChecked == true;
            _settings.AiCleanupProvider = SelectedProvider;
            _settings.AiCleanupModel =
                NullIfBlank(AiModelBox.Text) ?? CleanupModelCatalog.DefaultAlias;
            _settings.AiCleanupAzureEndpoint = NullIfBlank(AzureEndpointBox.Text);
            _settings.AiCleanupAzureDeployment = NullIfBlank(AzureDeploymentBox.Text);
            _settings.AiCleanupAzureApiKey = NullIfBlank(AzureApiKeyBox.Password);
            _settings.AiCleanupAzureTenantId = NullIfBlank(AzureTenantBox.Text);
            _settings.AiCleanupCustomEndpoint = NullIfBlank(CustomEndpointBox.Text);
            _settings.AiCleanupCustomModel = NullIfBlank(CustomModelBox.Text);
            _settings.AiCleanupCustomApiKey = NullIfBlank(CustomApiKeyBox.Password);

            // Persist the writing style only when it differs from the default; storing blank for the
            // default keeps users tracking future improvements to the built-in guidance.
            var writingStyle = AiWritingStyleBox.Text?.Trim() ?? string.Empty;
            _settings.AiCleanupWritingStyle =
                writingStyle.Length == 0 || writingStyle == CleanupPrompt.DefaultWritingStyle
                    ? string.Empty
                    : writingStyle;

            // Persist the prompt style and, like the writing style, store a prompt override only when it
            // differs from the built-in default so users keep tracking future default improvements.
            _settings.AiCleanupPromptStyle = SelectedPromptStyle;
            var frontierPrompt = NormalizePrompt(AiFrontierPromptBox.Text);
            _settings.AiCleanupFrontierPrompt =
                frontierPrompt.Length == 0 || frontierPrompt == CleanupPrompt.DefaultFrontierPrompt
                    ? string.Empty
                    : frontierPrompt;
            var localPrompt = NormalizePrompt(AiLocalPromptBox.Text);
            _settings.AiCleanupLocalPrompt =
                localPrompt.Length == 0 || localPrompt == CleanupPrompt.DefaultLocalPrompt
                    ? string.Empty
                    : localPrompt;

            if (entries is not null)
            {
                _dictionary.SaveAll(entries);
            }

            if (snippets is not null)
            {
                _snippets.SaveAll(snippets);
            }

            _settingsRepository.Save(_settings);
            StartupRegistration.Set(_settings.LaunchOnLogin);
            _applySettings(_settings);

            Close();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Constraint safety net for anything grid validation didn't anticipate — still phrased
            // for a person, not a stack trace.
            ShowSection(SectionDictionary);
            ShowThemedMessage(
                "Duplicate dictionary entry",
                "Two dictionary entries ended up with the same spoken word or phrase, so the " +
                "dictionary was not changed.\n\nEach spoken form can only be listed once. Remove " +
                "the duplicate and save again.");
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not save settings:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Builds the desired dictionary state from the grid rows, skipping blank placeholder rows.
    /// Reports the first row whose spoken form duplicates an earlier row (case-insensitive, to
    /// match how the post-processor and AI glossary treat patterns) via <paramref name="duplicate"/>.
    /// </summary>
    private List<DictionaryEntry> BuildDictionaryEntries(out DictionaryRow? duplicate)
    {
        var result = DictionaryEntryBuilder.Build(
            _rows.Select(r => new DictionaryEntryBuilder.Row(
                r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled)).ToList());

        duplicate = result.HasDuplicate ? _rows[result.DuplicateIndex] : null;
        return result.Entries.ToList();
    }

    // --- Voice snippets --------------------------------------------------------------------

    private void LoadSnippets()
    {
        foreach (var snippet in _snippets.GetAll())
        {
            _snippetRows.Add(new SnippetRow
            {
                Id = snippet.Id,
                Phrase = snippet.Phrase,
                Template = snippet.Template,
                Enabled = snippet.Enabled,
            });
        }

        SnippetList.ItemsSource = _snippetRows;
        _snippetSnapshot = SnippetSignature();
    }

    private SnippetRow? SelectedSnippet => SnippetList.SelectedItem as SnippetRow;

    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = SelectedSnippet;
        SnippetEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        SnippetEmptyHint.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null)
        {
            return;
        }

        _loadingSnippet = true;
        try
        {
            SnippetPhraseBox.Text = row.Phrase;
            SnippetTemplateBox.Text = row.Template;
            SnippetEnabledCheck.IsChecked = row.Enabled;
        }
        finally
        {
            _loadingSnippet = false;
        }
    }

    private void SnippetAddButton_Click(object sender, RoutedEventArgs e)
    {
        var row = new SnippetRow { Phrase = "new snippet", Template = string.Empty };
        _snippetRows.Add(row);
        SnippetList.SelectedItem = row;
        SnippetList.ScrollIntoView(row);
        SnippetPhraseBox.Focus();
        SnippetPhraseBox.SelectAll();
    }

    private void SnippetDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSnippet is { } row)
        {
            _snippetRows.Remove(row);
        }
    }

    private void SnippetPhraseBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSnippet && SelectedSnippet is { } row)
        {
            row.Phrase = SnippetPhraseBox.Text;
        }
    }

    private void SnippetTemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSnippet && SelectedSnippet is { } row)
        {
            row.Template = SnippetTemplateBox.Text;
        }
    }

    private void SnippetEnabledCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_loadingSnippet && SelectedSnippet is { } row)
        {
            row.Enabled = SnippetEnabledCheck.IsChecked == true;
        }
    }

    /// <summary>
    /// Builds the desired snippet state from the editor rows, skipping rows with a blank phrase or
    /// template. Reports the first duplicate trigger phrase (case-insensitive) like the dictionary.
    /// </summary>
    private List<Snippet> BuildSnippets(out SnippetRow? duplicate)
    {
        var result = SnippetBuilder.Build(
            _snippetRows.Select(r => new SnippetBuilder.Row(
                r.Id, r.Phrase, r.Template, r.Enabled)).ToList());

        duplicate = result.HasDuplicate ? _snippetRows[result.DuplicateIndex] : null;
        return result.Snippets.ToList();
    }

    // --- Per-app profiles ------------------------------------------------------------------

    private void LoadProfiles()
    {
        ProfileNewlineCombo.DisplayMemberPath = nameof(ProfileNewlineChoice.Label);
        ProfileNewlineCombo.ItemsSource = new[]
        {
            new ProfileNewlineChoice(null, "Use the global setting"),
            new ProfileNewlineChoice(NewlineInjectionMode.SmartFlatten, "Smart — one line in terminals"),
            new ProfileNewlineChoice(NewlineInjectionMode.AlwaysFlatten, "Always one line — never send Enter"),
            new ProfileNewlineChoice(NewlineInjectionMode.KeepNewlines, "Keep line breaks exactly as dictated"),
        };

        foreach (var profile in _settings.Profiles)
        {
            _profileRows.Add(new ProfileRow
            {
                Name = profile.Name,
                Processes = string.Join(", ", profile.ProcessNames),
                WritingStyle = profile.WritingStyle ?? string.Empty,
                NewlineHandling = profile.NewlineHandling,
            });
        }

        ProfileList.ItemsSource = _profileRows;
    }

    private ProfileRow? SelectedProfile => ProfileList.SelectedItem as ProfileRow;

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = SelectedProfile;
        ProfileEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        ProfileEmptyHint.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null)
        {
            return;
        }

        _loadingProfile = true;
        try
        {
            ProfileNameBox.Text = row.Name;
            ProfileProcessesBox.Text = row.Processes;
            ProfileStyleBox.Text = row.WritingStyle;
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
        var row = new ProfileRow { Name = "New profile" };
        _profileRows.Add(row);
        ProfileList.SelectedItem = row;
        ProfileList.ScrollIntoView(row);
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void ProfileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is { } row)
        {
            _profileRows.Remove(row);
        }
    }

    private void ProfileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.Name = ProfileNameBox.Text;
        }
    }

    private void ProfileProcessesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.Processes = ProfileProcessesBox.Text;
        }
    }

    private void ProfileStyleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.WritingStyle = ProfileStyleBox.Text;
        }
    }

    private void ProfileNewlineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProfile && SelectedProfile is { } row)
        {
            row.NewlineHandling = (ProfileNewlineCombo.SelectedItem as ProfileNewlineChoice)?.Mode;
        }
    }

    /// <summary>Builds the profile list to persist, skipping rows with no name and no processes.</summary>
    private List<AppProfile> BuildProfiles() =>
        ProfileBuilder.Build(
            _profileRows.Select(r => new ProfileBuilder.Row(
                r.Name, r.Processes, r.WritingStyle, r.NewlineHandling)).ToList());

    private sealed record ProfileNewlineChoice(NewlineInjectionMode? Mode, string Label);

    // --- Overlay position picker -----------------------------------------------------------

    private void LoadOverlayPosition(OverlayPosition position)
    {
        foreach (var child in OverlayPositionGrid.Children)
        {
            if (child is RadioButton zone)
            {
                zone.IsChecked = string.Equals((string)zone.Tag, position.ToString(), StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The position currently picked in the mini-monitor (pending until save).</summary>
    private OverlayPosition SelectedOverlayPosition
    {
        get
        {
            foreach (var child in OverlayPositionGrid.Children)
            {
                if (child is RadioButton { IsChecked: true } zone &&
                    Enum.TryParse<OverlayPosition>((string)zone.Tag, out var position))
                {
                    return position;
                }
            }

            return OverlayPosition.BottomCenter;
        }
    }

    private void OverlayPreviewButton_Click(object sender, RoutedEventArgs e) =>
        _previewOverlay(SelectedOverlayPosition);

    // --- Dictionary suggestions from history -----------------------------------------------

    private async void DictionarySuggestButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // The grid is the live source of truth for "already covered", so terms added but not yet
        // saved are excluded from new suggestions too.
        var current = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new DictionaryEntry(r.Id, r.Pattern.Trim(), (r.Replacement ?? string.Empty).Trim()))
            .ToList();

        // Prefer the user's configured AI model: it can work out how a term is spoken versus how it is
        // written (acronyms, phonetic mishears, casing), not just spot repeated words. Fall back to the
        // offline pattern miner when no model is ready, so the button still helps with no AI configured.
        if (_cleanup.Status == CleanupStatus.Ready)
        {
            await SuggestWithAiAsync(current);
        }
        else
        {
            SuggestWithMiner(current);
        }
    }

    private async Task SuggestWithAiAsync(IReadOnlyList<DictionaryEntry> current)
    {
        List<HistoryEntry> history;
        try
        {
            history = _history.GetRecent(1000).ToList();
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not read your history:\n{ex.Message}");
            return;
        }

        var sample = AiDictionarySuggester.BuildHistorySample(history);
        if (string.IsNullOrWhiteSpace(sample))
        {
            ShowThemedMessage(
                "Nothing to suggest",
                "There are no recent dictations to learn from yet. Keep dictating and try again later.");
            return;
        }

        var originalContent = DictionarySuggestButton.Content;
        DictionarySuggestButton.IsEnabled = false;
        DictionarySuggestButton.Content = "Thinking…";
        ShowInfo(
            "Asking your AI model to learn terms from your recent dictations…",
            Wpf.Ui.Controls.InfoBarSeverity.Informational);
        try
        {
            var response = await _cleanup.CompleteAsync(AiDictionarySuggester.SystemPrompt, sample);
            if (string.IsNullOrWhiteSpace(response))
            {
                // The model was unavailable or returned nothing: fall back to the deterministic miner.
                SuggestWithMiner(current, aiRanFirst: true);
                return;
            }

            var suggestions = AiDictionarySuggester.ParseSuggestions(response, current);
            if (suggestions.Count == 0)
            {
                SuggestWithMiner(current, aiRanFirst: true);
                return;
            }

            AddSuggestionRows(suggestions.Select(s => (s.Pattern, s.Replacement)));
            ShowInfo(
                $"Added {suggestions.Count} suggested {(suggestions.Count == 1 ? "entry" : "entries")} " +
                "your AI model inferred from recent dictations. Review them in the grid, delete any you " +
                "don't want, then save.");
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not get AI suggestions:\n{ex.Message}");
        }
        finally
        {
            DictionarySuggestButton.Content = originalContent;
            DictionarySuggestButton.IsEnabled = true;
        }
    }

    private void SuggestWithMiner(IReadOnlyList<DictionaryEntry> current, bool aiRanFirst = false)
    {
        IReadOnlyList<DictionarySuggestionMiner.Suggestion> suggestions;
        try
        {
            suggestions = DictionarySuggestionMiner.Mine(_history.GetRecent(1000), current);
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not scan your history:\n{ex.Message}");
            return;
        }

        if (suggestions.Count == 0)
        {
            ShowThemedMessage(
                "Nothing to suggest",
                aiRanFirst
                    ? "Your AI model and the history scan didn't find any new terms to add. Keep " +
                      "dictating and try again later."
                    : "No recurring technical terms found in your recent dictations yet.\n\n" +
                      "Suggestions appear once a term shows up in three or more dictations, so keep " +
                      "dictating and try again later.");
            return;
        }

        // Lock the spelling: the lowercase spoken form maps to the surface form seen most often.
        AddSuggestionRows(suggestions.Select(s => (s.Term.ToLowerInvariant(), s.Term)));
        ShowInfo(
            $"Added {suggestions.Count} suggested {(suggestions.Count == 1 ? "entry" : "entries")} " +
            "from your recent dictations. Review them in the grid, delete any you don't want, then save.");
    }

    private void AddSuggestionRows(IEnumerable<(string Pattern, string Replacement)> entries)
    {
        DictionaryRow? first = null;
        foreach (var (pattern, replacement) in entries)
        {
            var row = new DictionaryRow { Pattern = pattern, Replacement = replacement };
            _rows.Add(row);
            first ??= row;
        }

        if (first is not null)
        {
            DictionaryGrid.SelectedItem = first;
            DictionaryGrid.ScrollIntoView(first);
        }
    }

    // --- Dictionary CSV import / export ---------------------------------------------------

    // UTF-8 with BOM so Excel opens accented terms correctly instead of guessing the codepage.
    private static readonly UTF8Encoding CsvEncoding = new(encoderShouldEmitUTF8Identifier: true);

    private void DictionaryTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = "scribe-dictionary-template.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, DictionaryCsv.Template, CsvEncoding);

            // Open it straight away so the user can start filling it in.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not save the template:\n{ex.Message}");
        }
    }

    private void DictionaryExportButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var dialog = new SaveFileDialog
        {
            FileName = "scribe-dictionary.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var entries = _rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r => new DictionaryEntry(
                    r.Id, r.Pattern.Trim(), (r.Replacement ?? string.Empty).Trim(), r.WholeWord, r.Enabled));
            File.WriteAllText(dialog.FileName, DictionaryCsv.Export(entries), CsvEncoding);
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not export the dictionary:\n{ex.Message}");
        }
    }

    private void DictionaryImportButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DictionaryCsvResult parsed;
        try
        {
            parsed = DictionaryCsv.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not read that file:\n{ex.Message}");
            return;
        }

        var (added, updated, unchanged) = MergeImportedEntries(parsed.Entries);
        var summary = new StringBuilder();
        summary.Append($"Imported {added} new {(added == 1 ? "entry" : "entries")}");
        if (updated > 0)
        {
            summary.Append($", updated {updated}");
        }

        if (unchanged > 0)
        {
            summary.Append($", {unchanged} already up to date");
        }

        summary.Append('.');
        if (added + updated > 0)
        {
            summary.Append(" The changes apply when you save.");
        }

        if (parsed.Errors.Count > 0)
        {
            summary.Append("\n\nSome rows couldn't be read:\n")
                   .Append(string.Join('\n', parsed.Errors.Take(8)));
            if (parsed.Errors.Count > 8)
            {
                summary.Append($"\n…and {parsed.Errors.Count - 8} more.");
            }
        }

        ShowInfo(
            summary.ToString(),
            parsed.Errors.Count > 0
                ? Wpf.Ui.Controls.InfoBarSeverity.Warning
                : Wpf.Ui.Controls.InfoBarSeverity.Success);
    }

    /// <summary>
    /// Merges imported entries into the grid (not the database — the save button owns persistence,
    /// so an import can still be cancelled). Matching is by spoken form, case-insensitive, mirroring
    /// the duplicate rule the save validation enforces.
    /// </summary>
    private (int Added, int Updated, int Unchanged) MergeImportedEntries(IReadOnlyList<DictionaryEntry> imported)
    {
        // The pure merge/counting lives in Core; here we apply its plan to the observable grid rows.
        var existing = _rows
            .Select((r, i) => new DictionaryImportMerger.ExistingRow(
                i, r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled))
            .ToList();

        var plan = DictionaryImportMerger.Merge(existing, imported);

        foreach (var op in plan.Operations)
        {
            var entry = op.Entry;
            // Replace/append the row object (rather than mutate it) so the grid, which has no property
            // change notifications on DictionaryRow, refreshes the visible values.
            var row = new DictionaryRow
            {
                Id = entry.Id,
                Pattern = entry.Pattern,
                Replacement = entry.Replacement,
                WholeWord = entry.WholeWord,
                Enabled = entry.Enabled,
            };

            if (op.Kind == DictionaryImportMerger.OperationKind.Update)
            {
                _rows[op.Index] = row;
            }
            else
            {
                _rows.Add(row);
            }
        }

        return (plan.Added, plan.Updated, plan.Unchanged);
    }

    private static string StripDefaultSuffix(string name)
    {
        const string suffix = " — default";
        return name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record DeviceChoice(string? Id, string Name);

    private sealed record InjectionChoice(InjectionMethod Method, string Label);

    private sealed record NewlineChoice(NewlineInjectionMode Mode, string Label);

    private sealed record ProviderChoice(CleanupProvider Provider, string Label);
    private sealed record PromptStyleChoice(CleanupPromptStyle Style, string Label);

    /// <summary>Editable dictionary row backing the grid. Parameterless ctor enables grid add-row.</summary>
    public sealed class DictionaryRow
    {
        public long Id { get; set; }
        public string Pattern { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public bool WholeWord { get; set; } = true;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>Library row backing the libraries grid; only <see cref="Enabled"/> is user-editable.</summary>
    public sealed class LibraryRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Terms { get; set; }
        public string Source { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Editable snippet row behind the master-detail editor. Phrase raises change notifications so
    /// the ListBox label tracks edits made in the detail pane.
    /// </summary>
    public sealed class SnippetRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _phrase = string.Empty;

        public long Id { get; set; }

        public string Phrase
        {
            get => _phrase;
            set
            {
                if (_phrase != value)
                {
                    _phrase = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Phrase)));
                }
            }
        }

        public string Template { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Editable profile row; Name notifies so the ListBox label tracks the detail pane.</summary>
    public sealed class ProfileRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
                }
            }
        }

        public string Processes { get; set; } = string.Empty;
        public string WritingStyle { get; set; } = string.Empty;
        public NewlineInjectionMode? NewlineHandling { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class FailureRow
    {
        public string When { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
