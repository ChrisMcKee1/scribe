using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Scribe.App.Dictation;
using Scribe.App.Infrastructure;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Transcription;

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
    private const string RepositoryUrl = "https://github.com/ChrisMcKee1/scribe";
    private const string PrivacyPolicyUrl = RepositoryUrl + "/blob/main/PRIVACY.md";
    private const string NewIssueUrl = RepositoryUrl + "/issues/new";

    private readonly ISettingsRepository _settingsRepository;
    private readonly IAudioCaptureService _audio;
    private readonly IDictionaryRepository _dictionary;
    private readonly IDictionaryLibraryService _libraries;
    private readonly ISnippetRepository _snippets;
    private readonly IHistoryRepository _history;
    private readonly ITextCleanupService _cleanup;
    private readonly IAzureFoundryDiscovery _azureDiscovery;
    private readonly AzureCliInstaller _azureCliInstaller;
    private readonly ICleanupFailureLog _failureLog;
    private readonly ITranscriptionModelInstaller _transcriptionModelInstaller;
    private readonly Action<OverlayPosition> _previewOverlay;
    private readonly Action<AppSettings> _applySettings;
    private readonly Action<bool> _setHotkeyCaptureMode;
    private readonly UpdateService? _updates;
    private StoreUpdateService? _storeUpdates;
    private readonly ILogger<SettingsWindow> _log;

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
    private readonly ObservableCollection<HistoryRow> _historyRows = new();
    private readonly ObservableCollection<FailureRow> _failures = new();
    private UsageAnalyzer.Snapshot? _usageSnapshot;
    private bool _usageInsightRunning;
    private int _usageLoadVersion;
    private int _azureDeploymentLoadVersion;
    private readonly Dictionary<string, CleanupModel> _foundryCuratedByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AzureFoundryDeployment> _azureModelMap = new(StringComparer.OrdinalIgnoreCase);
    // Subscription filter for Azure model discovery. The sentinel "All subscriptions" row is not in
    // the map, so a missing lookup means "no filter" by construction.
    private const string AllAzureSubscriptionsLabel = "All subscriptions";
    private readonly Dictionary<string, AzureSubscription> _azureSubscriptionMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingAzureSubscriptions;
    private bool _foundryModelOp;
    private bool _azureAutoListed;
    private int _azureSignInProbeVersion;
    private bool _azureCliInstalled;
    private bool _azureConnectionKnown;
    private bool _azureConnectionBusy;
    private bool _azureManualConfiguration;
    private AzureSignInStatus _azureSignInStatus = new(false, null);
    private AzureFoundryDeployment? _selectedAzureDeployment;
    private bool _transcriptionModelOp;

    private HotkeyBinding _pendingBinding;
    private HotkeyBinding? _pendingDictationOnlyBinding;
    private bool _capturingDictationOnly;
    private readonly List<Key> _capturedKeys = new(2);
    private readonly HashSet<Key> _pressedCaptureKeys = new();
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
        AzureCliInstaller azureCliInstaller,
        ILogger<SettingsWindow> log,
        ICleanupFailureLog failureLog,
        ITranscriptionModelInstaller transcriptionModelInstaller,
        Action<OverlayPosition> previewOverlay,
        Action<AppSettings> applySettings,
        Action<bool>? setHotkeyCaptureMode = null,
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
        _azureCliInstaller = azureCliInstaller;
        _failureLog = failureLog;
        _transcriptionModelInstaller = transcriptionModelInstaller;
        _previewOverlay = previewOverlay;
        _applySettings = applySettings;
        _setHotkeyCaptureMode = setHotkeyCaptureMode ?? (_ => { });
        _updates = updates;
        _log = log;

        _settings = settingsRepository.Load();
        _pendingBinding = _settings.Hotkey;
        _pendingDictationOnlyBinding = _settings.DictationOnlyHotkey;

        // Match the system light/dark theme + accent colour and enable the Mica backdrop.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        InitializeComponent();

        UsagePeriodBox.ItemsSource = UsagePeriodChoice.All;
        UsagePeriodBox.DisplayMemberPath = nameof(UsagePeriodChoice.Label);
        UsagePeriodBox.SelectedIndex = 1;

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
        HistoryGrid.ItemsSource = _historyRows;
        LoadHistory();
        LoadFailures();
        LoadPerformanceStats();

        // Reflect live cleanup-engine state (download progress, ready, errors) in the UI.
        _cleanup.StatusChanged += OnCleanupStatusChanged;
        Closed += OnClosed;
        RefreshAiStatus();
        InitializeUpdateCard();
        AboutVersionText.Text = $"Version {UpdateService.RunningVersion}";
    }

    // --- Updates card (General) --------------------------------------------------------------

    private void InitializeUpdateCard()
    {
        if (_updates?.IsStoreManaged == true)
        {
            // A Store install still gets a working button; it just goes through the Store rather
            // than Velopack. Hiding it entirely left Store users with no way to check at all.
            _storeUpdates ??= new StoreUpdateService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreUpdateService>.Instance);

            if (!StoreUpdateService.IsStoreInstall())
            {
                // Packaged but sideloaded: the Store has no record of this install, so offering a
                // check would only ever fail.
                UpdateStatusText.Text =
                    $"Scribe {UpdateService.RunningVersion} was installed from a package. Updates are managed outside the app.";
                UpdateCheckButton.Visibility = Visibility.Collapsed;
                UpdateApplyButton.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateStatusText.Text =
                $"Scribe {UpdateService.RunningVersion} is installed from Microsoft Store.";
            UpdateCheckButton.Visibility = Visibility.Visible;
            UpdateApplyButton.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateStatusText.Text = _updates?.PendingVersion is { } pending
            ? $"Scribe {UpdateService.RunningVersion} — {pending} is downloaded and ready to install."
            : $"Scribe {UpdateService.RunningVersion} — use Check for updates when you want to connect.";
        UpdateApplyButton.Visibility = _updates?.PendingVersion is null ? Visibility.Collapsed : Visibility.Visible;
        if (_updates is not null)
        {
            _updates.UpdateReady += OnUpdateReady;
        }
    }

    private void OnUpdateReady(string message) => Dispatcher.BeginInvoke(() =>
    {
        UpdateStatusText.Text = message;
        UpdateApplyButton.Visibility = Visibility.Visible;
    });

    public void ReloadExternalSettings()
    {
        if (_capturing)
        {
            return;
        }

        var latest = _settingsRepository.Load();
        _settings.EnableAiCleanup = latest.EnableAiCleanup;
        AiCleanupCheck.IsChecked = latest.EnableAiCleanup;
    }

    public IReadOnlyList<DictionaryEntry> PersistLearnedDictionaryEntries(IReadOnlyList<DictionaryEntry> entries)
    {
        var wasDirty = DictionarySignature() != _dictionarySnapshot;
        var candidates = entries
            .Where(entry => !_rows.Any(row =>
                string.Equals(row.Pattern.Trim(), entry.Pattern, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.Replacement.Trim(), entry.Replacement, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var persisted = _dictionary.AddRange(candidates);
        foreach (var entry in persisted)
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

        if (!wasDirty)
        {
            _dictionarySnapshot = DictionarySignature();
        }

        return persisted;
    }

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updates?.IsStoreManaged == true)
        {
            await CheckStoreUpdatesAsync();
            return;
        }

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

    private async Task CheckStoreUpdatesAsync()
    {
        _storeUpdates ??= new StoreUpdateService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreUpdateService>.Instance);

        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking Microsoft Store for updates…";
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var available = await _storeUpdates.CheckAsync(hwnd);

            // The Store does not document which version StorePackageUpdate carries, so the message
            // deliberately does not name one rather than risk showing the wrong number.
            UpdateStatusText.Text = available
                ? "An update is available from Microsoft Store."
                : $"Scribe {UpdateService.RunningVersion} is up to date.";
            UpdateApplyButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private async void UpdateApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updates?.IsStoreManaged == true)
        {
            await ApplyStoreUpdateAsync();
            return;
        }

        // On success this never returns — the process exits, the update applies, and Scribe
        // relaunches on the new version.
        if (_updates is null || !_updates.ApplyNowAndRestart())
        {
            UpdateStatusText.Text = "Couldn't restart into the update — it will install when you quit Scribe.";
        }
    }

    private async Task ApplyStoreUpdateAsync()
    {
        if (_storeUpdates is null)
        {
            return;
        }

        UpdateApplyButton.IsEnabled = false;
        UpdateStatusText.Text = "Installing the update from Microsoft Store…";
        try
        {
            // Windows shows its own consent and progress dialogs here, and may close Scribe to
            // replace it, so a "Completed" result often never gets rendered.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var outcome = await _storeUpdates.ApplyAsync(hwnd);
            UpdateStatusText.Text = outcome switch
            {
                StoreUpdateOutcome.Completed => "The update is installed. Restart Scribe to run the new version.",
                StoreUpdateOutcome.Canceled => "The update was cancelled.",
                StoreUpdateOutcome.NothingToDo => $"Scribe {UpdateService.RunningVersion} is up to date.",
                _ => "The update could not be installed. Try again from the Microsoft Store app.",
            };
            UpdateApplyButton.Visibility = outcome == StoreUpdateOutcome.Completed
                ? Visibility.Collapsed
                : UpdateApplyButton.Visibility;
        }
        finally
        {
            UpdateApplyButton.IsEnabled = true;
        }
    }

    // --- Playground ------------------------------------------------------------------------

    internal void ShowPlaygroundPipeline(DictationPipelineReport report)
    {
        if (!IsVisible ||
            SectionPlayground.Visibility != Visibility.Visible ||
            new WindowInteropHelper(this).Handle != report.TargetWindow)
        {
            return;
        }

        PlaygroundRecognizedText.Text = report.RawText ?? string.Empty;
        var displayedText = report.FinalText ?? report.PostProcessing?.Text ??
            report.CleanedText ?? report.RawText ?? string.Empty;
        var displayedResult = report.PostProcessing is { } postProcessing &&
            string.Equals(postProcessing.Text, displayedText, StringComparison.Ordinal)
                ? postProcessing
                : new TextPostProcessingResult(displayedText, []);
        RenderPlaygroundResult(displayedResult);

        PlaygroundCaptureDuration.Text = FormatDuration(report.CaptureDuration);
        PlaygroundCaptureDetail.Text = DetailOrFailure(report, "Audio capture", "Audio recorded");

        PlaygroundVadDuration.Text = report.VadEnabled ? FormatDuration(report.VadDuration) : "Skipped";
        PlaygroundVadDetail.Text = report.VadEnabled
            ? report.VadAvailable
                ? DetailOrFailure(
                    report,
                    "Voice activity detection",
                    $"Kept {report.SpeechDuration.TotalSeconds:N1} seconds of speech")
                : "VAD model unavailable, audio passed through"
            : "Off in settings";

        PlaygroundDecodeDuration.Text = report.DecodeDuration > TimeSpan.Zero
            ? FormatDuration(report.DecodeDuration)
            : "Not run";
        PlaygroundDecodeDetail.Text = report.RawText is not null
            ? $"{report.RawText.Length} characters, RTF {report.RealTimeFactor:N2}"
            : DetailOrFailure(report, "Speech recognition", "Not reached");

        PlaygroundAiDuration.Text = report.CleanupEnabled
            ? FormatDuration(report.CleanupDuration)
            : "Skipped";
        PlaygroundAiDetail.Text = report.Cleanup is { } cleanup
            ? DescribeCleanup(cleanup, report.CleanupEnabled)
            : DetailOrFailure(report, "AI cleanup", "Not reached");

        PlaygroundPostDuration.Text = report.PostProcessingEnabled
            ? FormatDuration(report.PostProcessingDuration)
            : "Skipped";
        PlaygroundPostDetail.Text = report.PostProcessing is { } processed
            ? $"{processed.Replacements.Count} dictionary, library, or snippet replacement(s)"
            : DetailOrFailure(report, "Dictionary and snippets", "Not reached");

        PlaygroundInjectionDuration.Text = report.Injection is not null
            ? FormatDuration(report.InjectionDuration)
            : "Not run";
        PlaygroundInjectionDetail.Text = report.Injection is { } injection
            ? injection.Succeeded
                ? $"Inserted using {injection.Method}"
                : $"Failed: {injection.Error}"
            : DetailOrFailure(report, "Text insertion", "Not reached");

        PlaygroundTotalDuration.Text = FormatDuration(report.TotalDuration);
        PlaygroundTotalDetail.Text = report.FailureStage is null
            ? "Full audio pipeline"
            : $"Stopped at {report.FailureStage}: {report.FailureReason}";
    }

    private static string DetailOrFailure(
        DictationPipelineReport report,
        string stage,
        string detail) =>
        string.Equals(report.FailureStage, stage, StringComparison.Ordinal)
            ? $"Failed: {report.FailureReason}"
            : detail;

    private static string DescribeCleanup(CleanupResult cleanup, bool enabled)
    {
        if (!enabled)
        {
            return "Skipped, AI cleanup is off";
        }

        return cleanup.Outcome switch
        {
            CleanupOutcome.Cleaned when cleanup.FailureReason is not null =>
                $"Cleaned with a partial fallback: {cleanup.FailureReason}",
            CleanupOutcome.Cleaned => "Cleaned",
            CleanupOutcome.Unchanged => "Ran, no changes needed",
            CleanupOutcome.Failed => $"Failed, raw text kept: {cleanup.FailureReason}",
            _ => "Skipped, cleanup model is not ready",
        };
    }

    private void RenderPlaygroundResult(TextPostProcessingResult result)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var position = 0;
        foreach (var replacement in result.Replacements)
        {
            AppendPlaygroundText(paragraph, result.Text[position..replacement.Start]);
            var source = replacement.Kind == TextReplacementKind.Snippet
                ? "Snippet"
                : "Dictionary or library";
            AppendPlaygroundText(
                paragraph,
                result.Text.Substring(replacement.Start, replacement.Length),
                $"{source} matched '{replacement.Pattern}'");
            position = replacement.Start + replacement.Length;
        }

        AppendPlaygroundText(paragraph, result.Text[position..]);
        var document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = PlaygroundOutput.FontFamily,
            FontSize = PlaygroundOutput.FontSize,
            Foreground = PlaygroundOutput.Foreground,
        };
        PlaygroundOutput.Document = document;
    }

    private static void AppendPlaygroundText(
        Paragraph paragraph,
        string text,
        string? highlightTooltip = null)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            AppendRun(paragraph, text[start..index], highlightTooltip);
            paragraph.Inlines.Add(new LineBreak());
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        AppendRun(paragraph, text[start..], highlightTooltip);
    }

    private static void AppendRun(Paragraph paragraph, string text, string? highlightTooltip)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text);
        if (highlightTooltip is not null)
        {
            run.Background = new SolidColorBrush(Color.FromArgb(64, 0, 120, 212));
            run.FontWeight = FontWeights.SemiBold;
            run.TextDecorations = TextDecorations.Underline;
            run.ToolTip = highlightTooltip;
        }

        paragraph.Inlines.Add(run);
    }

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1 ? "<1 ms" : $"{elapsed.TotalMilliseconds:N0} ms";

    // --- Navigation rail -------------------------------------------------------------------

    // Nav order must match the ListBoxItem order in XAML.
    private Grid[] SectionPanels =>
    [
        SectionGeneral, SectionDictation, SectionOverlay, SectionAi,
        SectionDictionary, SectionLibraries, SectionSnippets, SectionProfiles, SectionPlayground, SectionHistory,
        SectionUsage, SectionDiagnostics, SectionAbout,
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

        if (panels[selected] == SectionHistory)
        {
            LoadHistory();
        }
        else if (panels[selected] == SectionUsage)
        {
            LoadUsage();
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

        if (!string.IsNullOrWhiteSpace(_settings.InputDeviceId) &&
            choices.All(choice => !string.Equals(choice.Id, _settings.InputDeviceId, StringComparison.Ordinal)))
        {
            choices.Add(new DeviceChoice(
                _settings.InputDeviceId,
                $"Unavailable — {_settings.InputDeviceName ?? "saved microphone"}",
                _settings.InputDeviceName));
        }

        DeviceCombo.ItemsSource = choices;
        DeviceCombo.SelectedItem =
            choices.FirstOrDefault(c => c.Id == _settings.InputDeviceId) ?? choices[0];
    }

    private void PopulateChoices()
    {
        ModeCombo.ItemsSource = new[] { "Hold", "Toggle" };
        DictationOnlyModeCombo.ItemsSource = new[] { "Hold", "Toggle" };
        TranscriptionModelCombo.ItemsSource = TranscriptionModelCatalog.Curated;

        InjectionCombo.DisplayMemberPath = nameof(InjectionChoice.Label);
        InjectionCombo.ItemsSource = new[]
        {
            new InjectionChoice(InjectionMethod.UnicodeType, "Type it in (recommended, works everywhere)"),
            new InjectionChoice(InjectionMethod.ClipboardPaste, "Paste it in (faster for long text)"),
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
            DictationOnlyHotkeyBox.Text = _pendingDictationOnlyBinding is null
                ? string.Empty
                : HotkeyCapture.Describe(_pendingDictationOnlyBinding);
            DictationOnlyModeCombo.SelectedIndex =
                _pendingDictationOnlyBinding?.Mode == HotkeyMode.Toggle ? 1 : 0;

            OverlayCheck.IsChecked = _settings.ShowOverlay;
            LoadOverlayPosition(_settings.OverlayPosition);
            VadCheck.IsChecked = _settings.UseVoiceActivityDetection;
            AutoStopCheck.IsChecked = _settings.AutoStopOnSilence;
            PostCheck.IsChecked = _settings.ApplyPostProcessing;
            LaunchCheck.IsChecked = _settings.LaunchOnLogin;
            StoreAudioCheck.IsChecked = _settings.StoreAudioHistory;
            ShiftEnterCheck.IsChecked = _settings.ShiftEnterLineBreaks;
            BeamSearchCheck.IsChecked = _settings.UseHighAccuracyDecoding;

            var items = (InjectionChoice[])InjectionCombo.ItemsSource;
            InjectionCombo.SelectedItem =
                items.FirstOrDefault(i => i.Method == _settings.InjectionMethod) ?? items[0];

            var newlineItems = (NewlineChoice[])NewlineCombo.ItemsSource;
            NewlineCombo.SelectedItem =
                newlineItems.FirstOrDefault(i => i.Mode == _settings.NewlineHandling) ?? newlineItems[0];

            ThreadsSlider.Value = Math.Clamp(_settings.DecodeThreads, 0, 16);
            UpdateThreadsLabel();
            TranscriptionModelCombo.SelectedItem =
                TranscriptionModelCatalog.Resolve(_settings.TranscriptionModelId);
            UpdateTranscriptionModelUi();

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
        AzureAuthModeBox.SelectedIndex =
            _settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal ? 1 : 0;
        SpTenantBox.Text = _settings.AiCleanupAzureTenantId ?? string.Empty;
        SpClientIdBox.Text = _settings.AiCleanupAzureClientId ?? string.Empty;
        SpClientSecretBox.Password = _settings.AiCleanupAzureClientSecret ?? string.Empty;
        _azureManualConfiguration = !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureApiKey);

        CustomEndpointBox.Text = _settings.AiCleanupCustomEndpoint ?? string.Empty;
        CustomModelBox.Text = _settings.AiCleanupCustomModel ?? string.Empty;
        CustomApiKeyBox.Password = _settings.AiCleanupCustomApiKey ?? string.Empty;

        // Open Advanced automatically when manual auth is configured, so an override isn't hidden away.
        AzureAdvancedExpander.IsExpanded =
            !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureApiKey) ||
            !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureTenantId);

        // Reflect the saved deployment in the Model picker before any sign-in discovery runs.
        SeedAzureModelFromSettings();

        // Same idea for the subscription filter: show the saved choice as a stand-in until sign-in
        // discovery replaces the list with everything the account can see.
        SeedAzureSubscriptionsFromSettings();

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
        ApplyAzureSettingsAccess();
        UpdateAiEnabledState();
        UpdateAiModelHint();
        UpdateAzureDeploymentHint();

        // Best-effort: merge the live on-device catalog + loaded status in without blocking window open.
        if (AiCleanupCheck.IsChecked == true && SelectedProvider == CleanupProvider.FoundryLocal)
        {
            _ = RefreshFoundryModelsAsync();
        }
        else if (AiCleanupCheck.IsChecked == true && SelectedProvider == CleanupProvider.AzureFoundry)
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
        _rows.CollectionChanged += DictionaryRows_CollectionChanged;
        foreach (var row in _rows)
        {
            row.PropertyChanged += DictionaryRow_PropertyChanged;
        }

        DictionaryGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshDictionaryStatus);
        RefreshDictionaryStatus();
        _dictionarySnapshot = DictionarySignature();
    }

    // Rows are watched individually as well as collectively: a checkbox click commits without
    // necessarily raising CellEditEnding, so relying on that alone left the Library badge stale.
    private void DictionaryRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<DictionaryRow>() ?? [])
        {
            row.PropertyChanged -= DictionaryRow_PropertyChanged;
        }

        foreach (var row in e.NewItems?.OfType<DictionaryRow>() ?? [])
        {
            row.PropertyChanged += DictionaryRow_PropertyChanged;
        }

        RefreshDictionaryStatus();
    }

    private void DictionaryRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ignore everything UpdateDictionaryCoverage itself writes. The Coverage setter also raises
        // CoverageLabel, CoverageAppearance and CoverageVisibility, so filtering only Coverage left
        // three unfiltered notifications per row queuing a full recompute each: not an infinite loop
        // (the second pass is a no-op) but a Dispatcher flood proportional to dictionary size.
        if (e.PropertyName is null || e.PropertyName.StartsWith("Coverage", StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.BeginInvoke(RefreshDictionaryStatus);
    }

    /// <summary>Recomputes the glossary hint and the per-row library coverage badges together.</summary>
    private void RefreshDictionaryStatus()
    {
        UpdateDictionaryGlossaryHint();
        UpdateDictionaryCoverage();
    }

    /// <summary>
    /// Tags each row with how it relates to the libraries that are switched on, so the user can see
    /// which entries are redundant and which are deliberate overrides without saving first.
    /// </summary>
    /// <remarks>
    /// Deliberately tolerant: a failure here costs a badge, never an edit. It also runs against the
    /// live library checkboxes rather than saved settings, so toggling a library updates the column
    /// immediately.
    /// </remarks>
    private void UpdateDictionaryCoverage()
    {
        try
        {
            if (_rows.Count == 0)
            {
                return;
            }

            var enabledLibraries = _loadedLibraries
                .Where(l => _libraryRows.Any(r =>
                    r.Enabled && string.Equals(r.Id, l.Id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var covering = new Dictionary<string, (DictionaryEntry Entry, string Library)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var library in enabledLibraries)
            {
                foreach (var entry in library.Entries)
                {
                    if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Pattern))
                    {
                        continue;
                    }

                    covering.TryAdd(entry.Pattern.Trim(), (entry, library.Name));
                }
            }

            foreach (var row in _rows)
            {
                var pattern = (row.Pattern ?? string.Empty).Trim();
                if (pattern.Length == 0 || !covering.TryGetValue(pattern, out var hit))
                {
                    row.Coverage = DictionaryRowCoverage.None;
                    row.CoverageTooltip = string.Empty;
                    continue;
                }

                var mine = (row.Replacement ?? string.Empty).Trim();
                var theirs = (hit.Entry.Replacement ?? string.Empty).Trim();
                var same = string.Equals(mine, theirs, StringComparison.Ordinal)
                           && row.WholeWord == hit.Entry.WholeWord;

                row.Coverage = same ? DictionaryRowCoverage.Duplicate : DictionaryRowCoverage.Override;
                row.CoverageTooltip = same
                    ? $"\"{hit.Library}\" already writes this as \"{theirs}\". Removing this entry " +
                      "changes nothing and frees room in the AI cleanup glossary."
                    : $"\"{hit.Library}\" writes this as \"{theirs}\". Your entry wins. " +
                      "Clear Enabled to fall back to the library.";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not compute dictionary library coverage.");
        }
    }

    /// <summary>
    /// Adds a blank row and puts the cursor in it. The grid's own placeholder row was the only way
    /// to add an entry, which is invisible unless you already know it exists.
    /// </summary>
    private void DictionaryAddButton_Click(object sender, RoutedEventArgs e)
    {
        var row = new DictionaryRow();
        _rows.Add(row);

        DictionaryGrid.ScrollIntoView(row);

        // The row container is generated lazily, and BeginEdit silently does nothing when it does
        // not exist yet. Forcing layout first is what makes the new row actually land in edit mode
        // rather than appearing blank and unfocused.
        DictionaryGrid.UpdateLayout();

        DictionaryGrid.SelectedItem = row;
        DictionaryGrid.CurrentCell = new DataGridCellInfo(row, DictionaryGrid.Columns[0]);
        DictionaryGrid.BeginEdit();
    }

    /// <summary>Removes the row whose delete button was pressed.</summary>
    private void DictionaryDeleteRow_Click(object sender, RoutedEventArgs e)
    {
        // Committing first avoids removing a row the grid still believes it is editing.
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (sender is FrameworkElement { DataContext: DictionaryRow row })
        {
            _rows.Remove(row);
        }
    }

    // The glossary sent to AI cleanup is bounded, but the bound depends on where cleanup runs: a
    // cloud endpoint takes everything, a small on-device model takes a short list so the vocabulary
    // does not crowd out the transcript. Local find-and-replace is never capped either way. This is
    // surfaced as a status line rather than enforced as an input limit, because blocking the 81st
    // entry would break a feature that still works.
    private void UpdateDictionaryGlossaryHint()
    {
        if (DictionaryGlossaryHint is null)
        {
            return;
        }

        var enabled = _rows.Count(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Replacement));
        var total = _rows.Count;

        if (AiCleanupCheck?.IsChecked != true)
        {
            DictionaryGlossaryHint.Text = $"{enabled:N0} of {total:N0} entries enabled.";
            return;
        }

        var style = CleanupPrompt.ResolvePromptStyle(SelectedPromptStyle, SelectedProvider);
        if (style != CleanupPromptStyle.Local)
        {
            DictionaryGlossaryHint.Text =
                $"{enabled:N0} of {total:N0} entries enabled. All of them are replaced locally, and all " +
                "of them are sent to AI cleanup as a glossary.";
            return;
        }

        // The glossary the model actually receives is the merged list, not just this page's rows, and
        // the enabled libraries alone can run to several hundred terms. Counting only personal entries
        // reported "well under the cap" on a stock install that was in fact discarding most of the
        // library. Mirror DictionaryLibraryComposer's de-duplication (trimmed, case-insensitive,
        // personal first) so the number quoted here is the number that gets built.
        var effective = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            if (row.Enabled && !string.IsNullOrWhiteSpace(row.Pattern) &&
                !string.IsNullOrWhiteSpace(row.Replacement))
            {
                effective.Add(row.Pattern.Trim());
            }
        }

        var personalCount = effective.Count;
        foreach (var library in _loadedLibraries)
        {
            if (!_libraryRows.Any(r => r.Enabled &&
                    string.Equals(r.Id, library.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var entry in library.EnabledEntries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Pattern))
                {
                    effective.Add(entry.Pattern.Trim());
                }
            }
        }

        var cap = CleanupPrompt.MaxGlossaryTermsLocal;
        var libraryCount = effective.Count - personalCount;
        var libraryNote = libraryCount > 0 ? $" plus {libraryCount:N0} from enabled libraries" : string.Empty;

        if (effective.Count <= cap)
        {
            DictionaryGlossaryHint.Text = $"{enabled:N0} of {total:N0} entries enabled{libraryNote}.";
            return;
        }

        DictionaryGlossaryHint.Text =
            $"{enabled:N0} of {total:N0} entries enabled{libraryNote}. All of them are replaced locally. " +
            $"On-device cleanup has a small context window, so only the first {cap:N0} of those " +
            $"{effective.Count:N0} terms are sent to it as a glossary; {effective.Count - cap:N0} are not. " +
            "Your own entries come first, so they always make the cut. Local replacement still covers " +
            "the rest, and a cloud provider receives the full list.";
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

        // Switching a library on or off changes which dictionary entries are redundant, so the
        // Library column on the dictionary page has to follow it rather than wait for a save.
        LibraryGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshDictionaryStatus);

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
                "A local snapshot of your current rhythm. Lower latency is faster; " +
                "pace shows how quickly Scribe processes speech compared with its duration.";

            StatWeekDictations.Text = stats.Count.ToString("N0");
            StatWeekSpeech.Text = FormatElapsed(stats.TotalAudio.TotalSeconds);
            StatLongestDictation.Text = FormatElapsed(stats.LongestAudioSeconds);
            StatBestPace.Text = stats.FastestRtf > 0
                ? $"{1.0 / stats.FastestRtf:0.0}x"
                : "n/a";

            if (stats.ParakeetDecodeMs is { } decode)
            {
                DecodeSummaryHint.Text =
                    $"Time inside Parakeet only, over {stats.ParakeetDecodeCount} " +
                    $"run{(stats.ParakeetDecodeCount == 1 ? string.Empty : "s")}. AI cleanup is never counted here. " +
                    $"Typical pace {FormatPace(stats.RtfP50)} realtime; slower runs {FormatPace(stats.RtfP95)}.";
                StatDecodeAverage.Text = FormatLatency(decode.Average);
                StatDecodeMin.Text = FormatLatency(decode.Min);
                StatDecodeMax.Text = FormatLatency(decode.Max);
                StatDecodeP50.Text = FormatLatency(decode.P50);
                StatDecodeP95.Text = FormatLatency(decode.P95);
                DecodeMetricsGrid.Visibility = Visibility.Visible;
                DecodeNoDataText.Visibility = Visibility.Collapsed;
            }
            else
            {
                DecodeSummaryHint.Text = "Waiting for a model-verified Parakeet run.";
                DecodeMetricsGrid.Visibility = Visibility.Collapsed;
                DecodeNoDataText.Visibility = Visibility.Visible;
            }

            if (stats.CleanupMs is { } cleanup)
            {
                CleanupSummaryHint.Text =
                    $"The cleanup model round trip on its own, over {stats.CleanupCount} " +
                    $"run{(stats.CleanupCount == 1 ? string.Empty : "s")}. Recognition time is not included.";
                StatCleanupAverage.Text = FormatLatency(cleanup.Average);
                StatCleanupMin.Text = FormatLatency(cleanup.Min);
                StatCleanupMax.Text = FormatLatency(cleanup.Max);
                StatCleanupP50.Text = FormatLatency(cleanup.P50);
                StatCleanupP95.Text = FormatLatency(cleanup.P95);
                CleanupMetricsGrid.Visibility = Visibility.Visible;
                CleanupNoDataText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CleanupSummaryHint.Text = "No AI cleanup runs in this period yet.";
                CleanupMetricsGrid.Visibility = Visibility.Collapsed;
                CleanupNoDataText.Visibility = Visibility.Visible;
                CleanupSpeedExpander.IsExpanded = false;
            }

            if (stats.CombinedMs is { } combined)
            {
                CombinedSummaryHint.Text =
                    $"Recognition plus the cleanup model round trip over {stats.CombinedCount} " +
                    $"run{(stats.CombinedCount == 1 ? string.Empty : "s")}. This is the wait you actually feel.";
                StatCombinedAverage.Text = FormatLatency(combined.Average);
                StatCombinedMin.Text = FormatLatency(combined.Min);
                StatCombinedMax.Text = FormatLatency(combined.Max);
                StatCombinedP50.Text = FormatLatency(combined.P50);
                StatCombinedP95.Text = FormatLatency(combined.P95);
                CombinedMetricsGrid.Visibility = Visibility.Visible;
                CombinedNoDataText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CombinedSummaryHint.Text = "No cleanup-enabled runs in this period yet.";
                CombinedMetricsGrid.Visibility = Visibility.Collapsed;
                CombinedNoDataText.Visibility = Visibility.Visible;
                CombinedSpeedExpander.IsExpanded = false;
            }

            StatsGrid.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Stats are a nicety; never block the settings window over them.
            System.Diagnostics.Debug.WriteLine($"Performance stats unavailable: {ex.Message}");
        }

        static string FormatLatency(double ms) =>
            ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000.0:0.0} s";

        static string FormatElapsed(double seconds) => seconds switch
        {
            < 60 => $"{seconds:0} sec",
            < 3600 => $"{seconds / 60.0:0.#} min",
            _ => $"{seconds / 3600.0:0.#} hr",
        };

        static string FormatPace(double rtf) =>
            rtf > 0 ? $"{1.0 / rtf:0.0}x" : "n/a";
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
        BeginCapture(dictationOnly: false);
        HotkeyBox.Focus();
    }

    private void DictationOnlyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCapture(dictationOnly: true);
        DictationOnlyHotkeyBox.Focus();
    }

    private void DictationOnlyClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            CancelCapture();
        }
        _pendingDictationOnlyBinding = null;
        DictationOnlyHotkeyBox.Text = string.Empty;
    }

    private void BeginCapture(bool dictationOnly)
    {
        _capturingDictationOnly = dictationOnly;
        _capturing = true;
        _finalized = false;
        _capturedKeys.Clear();
        _pressedCaptureKeys.Clear();

        // Put the global hook into pass-through first: the current push-to-talk key must reach
        // this capture box as an ordinary key instead of being suppressed or starting a recording.
        _setHotkeyCaptureMode(true);
        ActiveHotkeyBox.Text = "Press one or two keys… (dictation is paused)";
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

        if (_pressedCaptureKeys.Add(key) && !_capturedKeys.Contains(key))
        {
            if (_capturedKeys.Count == 2)
            {
                ShowInfo("A dictation hotkey can contain up to two keys.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
            else
            {
                _capturedKeys.Add(key);
                ActiveHotkeyBox.Text = string.Join("+", _capturedKeys.Select(k => k.ToString())) +
                    (_capturedKeys.Count == 1 ? "  (add another key or release)" : "  (release to set)");
            }
        }
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing || _finalized)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _pressedCaptureKeys.Remove(key);
        if (_capturedKeys.Count == 0 || _pressedCaptureKeys.Count > 0)
        {
            return;
        }

        Finalize(HotkeyCapture.FromKeys(_capturedKeys, ActiveSelectedMode));
    }

    private void Finalize(HotkeyBinding binding)
    {
        if (_capturingDictationOnly)
        {
            _pendingDictationOnlyBinding = binding;
        }
        else
        {
            _pendingBinding = binding;
        }
        _finalized = true;
        _capturing = false;
        _capturedKeys.Clear();
        _pressedCaptureKeys.Clear();
        _setHotkeyCaptureMode(false);
        ActiveHotkeyBox.Text = HotkeyCapture.Describe(binding);
        Keyboard.ClearFocus();

        var risk = HotkeyCapture.AccessibilityRisk(binding);
        if (risk is not null)
        {
            ShowInfo(risk + " Consider a two-key chord instead.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
        else if (HotkeyCapture.IsReservedWindowsChord(binding))
        {
            ShowInfo(
                "This chord overrides a Windows shortcut while Scribe is running.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void CancelCapture()
    {
        _capturing = false;
        _capturedKeys.Clear();
        _pressedCaptureKeys.Clear();
        _setHotkeyCaptureMode(false);
        ActiveHotkeyBox.Text = CurrentHotkeyDescription();
        Keyboard.ClearFocus();
    }

    private void SettingsWindow_Deactivated_StopHotkeyCapture(object? sender, EventArgs e)
    {
        if (_capturing)
        {
            CancelCapture();
        }
    }

    private HotkeyMode SelectedMode => ModeCombo.SelectedIndex == 1 ? HotkeyMode.Toggle : HotkeyMode.Hold;

    private HotkeyMode DictationOnlySelectedMode =>
        DictationOnlyModeCombo.SelectedIndex == 1 ? HotkeyMode.Toggle : HotkeyMode.Hold;

    private HotkeyMode ActiveSelectedMode => _capturingDictationOnly
        ? DictationOnlySelectedMode
        : SelectedMode;

    private Wpf.Ui.Controls.TextBox ActiveHotkeyBox => _capturingDictationOnly
        ? DictationOnlyHotkeyBox
        : HotkeyBox;

    private string CurrentHotkeyDescription()
    {
        if (!_capturingDictationOnly)
        {
            return HotkeyCapture.Describe(_pendingBinding);
        }

        return _pendingDictationOnlyBinding is null
            ? string.Empty
            : HotkeyCapture.Describe(_pendingDictationOnlyBinding);
    }

    private static bool SamePhysicalBinding(HotkeyBinding left, HotkeyBinding right) =>
        left.VirtualKey == right.VirtualKey &&
        left.SecondaryVirtualKey == right.SecondaryVirtualKey &&
        left.Modifiers == right.Modifiers;

    // --- Threads -------------------------------------------------------------------------

    private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateThreadsLabel();

    private void TranscriptionModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingUi)
        {
            UpdateTranscriptionModelUi();
        }
    }

    private void UpdateTranscriptionModelUi()
    {
        if (TranscriptionModelCombo.SelectedItem is not TranscriptionModel model)
        {
            return;
        }

        var installed = _transcriptionModelInstaller.IsInstalled(model);
        var size = model.IsBundled ? "Bundled" : $"{model.DownloadSize / 1_000_000} MB download";
        TranscriptionModelHint.Text =
            $"{model.Description} Languages: {model.Languages}. {size}. " +
            (installed ? "Ready." : "Not installed.");
        TranscriptionModelInstallButton.Visibility = model.IsBundled ? Visibility.Collapsed : Visibility.Visible;
        TranscriptionModelInstallButton.IsEnabled = !installed && !_transcriptionModelOp;
        TranscriptionModelInstallButton.Content = installed ? "Installed" : "Install";
    }

    private async void TranscriptionModelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptionModelOp ||
            TranscriptionModelCombo.SelectedItem is not TranscriptionModel model ||
            model.IsBundled)
        {
            return;
        }

        _transcriptionModelOp = true;
        TranscriptionModelInstallButton.IsEnabled = false;
        TranscriptionModelProgress.Value = 0;
        TranscriptionModelProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(value => TranscriptionModelProgress.Value = value * 100);
        try
        {
            await _transcriptionModelInstaller.InstallAsync(model, progress);
            ShowInfo($"{model.DisplayName} is installed. Restart Scribe after saving to use it.");
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Model installation failed", ex.Message);
        }
        finally
        {
            _transcriptionModelOp = false;
            TranscriptionModelProgress.Visibility = Visibility.Collapsed;
            UpdateTranscriptionModelUi();
        }
    }

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
        if (AiCleanupCheck.IsChecked == true && SelectedProvider == CleanupProvider.AzureFoundry)
        {
            _ = ProbeAzureSignInAsync();
        }
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
            _selectedAzureDeployment = deployment;

            // Prefer the Foundry project endpoint (Microsoft's recommended shape, routed natively
            // through AIProjectClient). Its data plane is Entra-only, so a user working with an API
            // key gets the classic account endpoint instead.
            var usingApiKey = !string.IsNullOrWhiteSpace(AzureApiKeyBox.Password);
            AzureEndpointBox.Text = deployment.EndpointFor(usingApiKey);
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
        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            await VerifyServicePrincipalAsync();
            return;
        }

        await RefreshAzureConnectionAsync(
            allowInteractiveLogin: true,
            listModels: true,
            forceListModels: true);
    }

    private void AzureManualButton_Click(object sender, RoutedEventArgs e)
    {
        _azureManualConfiguration = true;
        AzureAdvancedExpander.IsExpanded = true;
        ApplyAzureSettingsAccess();
        AzureStatusText.Text =
            "Manual setup is open. Enter an endpoint, deployment name, and API key.";
        AzureEndpointBox.Focus();
    }

    private void AzureTenantBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        ++_azureSignInProbeVersion;
        ++_azureDeploymentLoadVersion;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        _azureAutoListed = false;
        _azureManualConfiguration = true;
        _selectedAzureDeployment = null;
        _updatingAzureSubscriptions = true;
        try
        {
            AzureSubscriptionBox.SelectedItem = AllAzureSubscriptionsLabel;
        }
        finally
        {
            _updatingAzureSubscriptions = false;
        }

        SetAzureConnectionBusy(false);
        AzureStatusText.Text = "Tenant changed. Verify Azure sign-in before browsing subscriptions and models.";
    }

    // Best-effort and non-blocking; runs when the Azure panel is shown.
    private Task ProbeAzureSignInAsync()
    {
        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            _azureConnectionKnown = true;
            var principal = CurrentServicePrincipal;
            if (principal is null)
            {
                if (AzureStatusText is not null)
                {
                    AzureStatusText.Text = "Enter the service principal details, then verify them.";
                }

                ApplyAzureSettingsAccess();
                return Task.CompletedTask;
            }

            // A saved service principal verifies itself on open, exactly as the Azure CLI path
            // probes its sign-in. Making the user press a button to re-confirm credentials Scribe
            // already has, on every visit, is busywork: the details cannot have changed since they
            // were saved, and cleanup has usually already authenticated with them in the background.
            return VerifyServicePrincipalAsync(automatic: true);
        }

        return RefreshAzureConnectionAsync(allowInteractiveLogin: false, listModels: true);
    }

    private async Task RefreshAzureConnectionAsync(
        bool allowInteractiveLogin,
        bool listModels,
        bool forceListModels = false)
    {
        // Everything below probes Azure CLI specifically. In service principal mode a valid CLI
        // session would otherwise mark the panel signed in, making an unverified app registration
        // look verified and revealing configuration that its identity may not actually reach.
        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            return;
        }

        var operationVersion = ++_azureSignInProbeVersion;
        var shouldListModels = false;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        SetAzureConnectionBusy(true);
        AzureStatusText.Text = "Checking your Azure CLI sign-in…";

        try
        {
            _azureCliInstalled = await _azureCliInstaller.IsInstalledAsync();
            _azureConnectionKnown = true;
            if (!_azureCliInstalled)
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                ApplyAzureSettingsAccess();
                AzureStatusText.Text =
                    "Azure CLI was not found. Install it below, or use an endpoint and API key instead.";
                return;
            }

            await ClearUnavailableSelectedSubscriptionAsync();
            var status = await ProbeCurrentAzureSignInAsync();
            if (!status.IsSignedIn && allowInteractiveLogin)
            {
                AzureStatusText.Text = "Opening Azure sign-in in your browser…";
                using var loginCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var selectedSubscription = SelectedAzureSubscription;
                var tenantId = selectedSubscription is null ||
                               string.IsNullOrWhiteSpace(selectedSubscription.TenantId)
                    ? NullIfBlank(AzureTenantBox.Text)
                    : selectedSubscription.TenantId;
                var (ok, message) = await _azureCliInstaller.LoginAsync(tenantId, loginCts.Token);
                if (!ok)
                {
                    _azureSignInStatus = new AzureSignInStatus(false, null);
                    ApplyAzureSettingsAccess();
                    AzureStatusText.Text = message;
                    return;
                }

                status = await ProbeCurrentAzureSignInAsync();
                if (!status.IsSignedIn && SelectedAzureSubscription is not null)
                {
                    ClearSelectedAzureSubscription();
                    status = await ProbeCurrentAzureSignInAsync();
                }
            }

            if (operationVersion != _azureSignInProbeVersion)
            {
                return;
            }

            _azureSignInStatus = status;
            ApplyAzureSettingsAccess();
            if (!status.IsSignedIn)
            {
                AzureStatusText.Text = allowInteractiveLogin
                    ? "Azure sign-in completed, but Scribe could not verify an Azure token. Check the tenant and try again."
                    : "Not signed in to Azure. Sign in to reveal subscriptions and models.";
                return;
            }

            AzureStatusText.Text = $"{DescribeAzureIdentity(status)} Listing compatible deployments…";
            shouldListModels =
                listModels && (forceListModels || allowInteractiveLogin || !_azureAutoListed);
        }
        catch (OperationCanceledException)
        {
            if (operationVersion == _azureSignInProbeVersion)
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureConnectionKnown = true;
                ApplyAzureSettingsAccess();
                AzureStatusText.Text = "Azure sign-in timed out. Please try again.";
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not verify Azure sign-in.");
            if (operationVersion == _azureSignInProbeVersion)
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureConnectionKnown = true;
                ApplyAzureSettingsAccess();
                AzureStatusText.Text = "Couldn't verify Azure sign-in. Please try again.";
            }
        }
        finally
        {
            if (operationVersion == _azureSignInProbeVersion)
            {
                SetAzureConnectionBusy(false);
            }
        }

        if (shouldListModels && operationVersion == _azureSignInProbeVersion)
        {
            await ListAzureDeploymentsAsync();
        }
    }

    private async Task<AzureSignInStatus> ProbeCurrentAzureSignInAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var selectedSubscription = SelectedAzureSubscription;
        var tenantId = selectedSubscription is null || string.IsNullOrWhiteSpace(selectedSubscription.TenantId)
            ? NullIfBlank(AzureTenantBox.Text)
            : selectedSubscription.TenantId;
        return await _azureDiscovery.GetSignInStatusAsync(
            tenantId,
            selectedSubscription?.Id,
            cts.Token);
    }

    private async Task ClearUnavailableSelectedSubscriptionAsync()
    {
        var selectedSubscription = SelectedAzureSubscription;
        if (selectedSubscription is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (ok, subscriptions, _) = await _azureCliInstaller.ListSubscriptionsAsync(cts.Token);
        if (ok && subscriptions.All(subscription => !string.Equals(
                subscription.Id,
                selectedSubscription.Id,
                StringComparison.OrdinalIgnoreCase)))
        {
            ClearSelectedAzureSubscription();
        }
    }

    private void ClearSelectedAzureSubscription()
    {
        _updatingAzureSubscriptions = true;
        try
        {
            AzureSubscriptionBox.SelectedItem = AllAzureSubscriptionsLabel;
        }
        finally
        {
            _updatingAzureSubscriptions = false;
        }
    }

    private void SetAzureConnectionBusy(bool busy)
    {
        _azureConnectionBusy = busy;
        ApplyAzureSettingsAccess();
    }

    private AzureAuthMode SelectedAzureAuthMode =>
        AzureAuthModeBox?.SelectedIndex == 1 ? AzureAuthMode.ServicePrincipal : AzureAuthMode.AzureCli;

    /// <summary>The app registration currently entered, or null when it is incomplete.</summary>
    private AzureServicePrincipal? CurrentServicePrincipal => AzureServicePrincipal.TryCreate(
        SelectedAzureAuthMode,
        SpTenantBox?.Text,
        SpClientIdBox?.Text,
        SpClientSecretBox?.Password);

    private AzureSettingsAccess.State CurrentAzureSettingsAccess =>
        AzureSettingsAccess.Resolve(
            _azureCliInstalled,
            _azureSignInStatus.IsSignedIn,
            _azureManualConfiguration,
            !string.IsNullOrWhiteSpace(AzureApiKeyBox?.Password),
            SelectedAzureAuthMode,
            CurrentServicePrincipal is not null);

    private void ApplyAzureSettingsAccess()
    {
        if (AzureCliSetupPanel is null ||
            AzureDiscoveryPanel is null ||
            AzureConfigurationPanel is null ||
            AzureManualButton is null ||
            AzureRefreshButton is null)
        {
            return;
        }

        var access = CurrentAzureSettingsAccess;
        var servicePrincipal = access.ShowServicePrincipalFields;
        AzureCliSetupPanel.Visibility =
            _azureConnectionKnown && access.ShowCliSetup ? Visibility.Visible : Visibility.Collapsed;
        AzureDiscoveryPanel.Visibility = access.ShowDiscovery ? Visibility.Visible : Visibility.Collapsed;
        AzureConfigurationPanel.Visibility = access.ShowConfiguration ? Visibility.Visible : Visibility.Collapsed;
        AzureManualButton.Visibility =
            access.ShowManualConfigurationAction ? Visibility.Visible : Visibility.Collapsed;

        if (AzureServicePrincipalPanel is not null)
        {
            AzureServicePrincipalPanel.Visibility = servicePrincipal ? Visibility.Visible : Visibility.Collapsed;
        }

        // The optional CLI tenant box pins the az login account to a tenant. In service principal
        // mode the app registration names its own tenant, so a second tenant field would be two
        // controls claiming the same setting.
        if (AzureCliTenantPanel is not null)
        {
            AzureCliTenantPanel.Visibility = servicePrincipal ? Visibility.Collapsed : Visibility.Visible;
        }

        if (AzureStatusTitle is not null)
        {
            AzureStatusTitle.Text = servicePrincipal ? "Use a service principal" : "Use your Azure sign-in";
        }

        AzureRefreshButton.Content = servicePrincipal
            ? _azureSignInStatus.IsSignedIn ? "Re-verify" : "Verify service principal"
            : _azureSignInStatus.IsSignedIn ? "Refresh models" : "Sign in & find models";

        // Verifying an app registration is a direct Entra call, so unlike the CLI path it does not
        // have to wait on the Azure CLI probe that _azureConnectionKnown tracks.
        AzureRefreshButton.IsEnabled = !_azureConnectionBusy && access.CanStartSignIn &&
            (servicePrincipal || _azureConnectionKnown);

        UpdateServicePrincipalValidation(servicePrincipal);
    }

    // Shows the first unmet requirement while the user is still typing, but stays quiet on an
    // untouched form so an empty panel doesn't open covered in red.
    private void UpdateServicePrincipalValidation(bool servicePrincipalMode)
    {
        if (SpValidationText is null)
        {
            return;
        }

        if (!servicePrincipalMode)
        {
            SpValidationText.Visibility = Visibility.Collapsed;
            return;
        }

        var untouched = string.IsNullOrWhiteSpace(SpTenantBox?.Text)
            && string.IsNullOrWhiteSpace(SpClientIdBox?.Text)
            && string.IsNullOrEmpty(SpClientSecretBox?.Password);
        var issue = AzureServicePrincipalValidator.Validate(
            SpTenantBox?.Text, SpClientIdBox?.Text, SpClientSecretBox?.Password);

        if (untouched || issue == AzureServicePrincipalValidator.Issue.None)
        {
            SpValidationText.Visibility = Visibility.Collapsed;
            return;
        }

        SpValidationText.Text = AzureServicePrincipalValidator.Describe(issue);
        SpValidationText.Visibility = Visibility.Visible;
    }

    private void AzureAuthModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        // Both modes ultimately write one tenant setting, so carry the current value across rather
        // than making the user retype it. Copy unconditionally: only syncing into a blank box would
        // leave the destination holding a stale tenant that Save would then persist over the edit
        // the user actually made.
        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            if (SpTenantBox is not null && AzureTenantBox is not null
                && !string.IsNullOrWhiteSpace(AzureTenantBox.Text))
            {
                SpTenantBox.Text = AzureTenantBox.Text;
            }
        }
        else if (AzureTenantBox is not null && SpTenantBox is not null
                 && !string.IsNullOrWhiteSpace(SpTenantBox.Text))
        {
            AzureTenantBox.Text = SpTenantBox.Text;
        }

        // The previous mode's verification says nothing about this one's identity, so drop it and
        // make the user verify again instead of showing a stale signed-in state. Bumping the
        // versions abandons any probe still in flight from the mode being left; that probe's
        // cleanup is version-guarded and will not run, so busy is released here instead or the
        // action button would stay disabled forever.
        ++_azureSignInProbeVersion;
        ++_azureDeploymentLoadVersion;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        _azureAutoListed = false;
        AzureCredentialInvalidation.Invalidate();
        SetAzureConnectionBusy(false);
        if (AzureStatusText is not null)
        {
            AzureStatusText.Text = SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal
                ? "Enter the service principal details, then verify them."
                : "Checking your Azure CLI sign-in before showing cloud resources.";
        }

        ApplyAzureSettingsAccess();

        // Returning to the CLI needs a fresh probe; nothing else re-runs it on this path.
        if (SelectedAzureAuthMode == AzureAuthMode.AzureCli)
        {
            _ = RefreshAzureConnectionAsync(
                allowInteractiveLogin: false, listModels: true, forceListModels: false);
        }
    }

    private void ServicePrincipalField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        // Editing the identity retires any verification, in flight or already applied. Bumping
        // unconditionally matters: a verification started against the previous details must not be
        // allowed to land on the new ones just because nothing was verified yet.
        ++_azureSignInProbeVersion;
        if (_azureSignInStatus.IsSignedIn && SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            _azureSignInStatus = new AzureSignInStatus(false, null);
            if (AzureStatusText is not null)
            {
                AzureStatusText.Text = "The service principal changed. Verify it again.";
            }
        }

        AzureCredentialInvalidation.Invalidate();
        ApplyAzureSettingsAccess();
    }

    private void Hyperlink_RequestNavigate(
        object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenExternalLink(e.Uri.AbsoluteUri, "Could not open the link.");
        e.Handled = true;
    }

    private void PrivacyPolicyButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink(PrivacyPolicyUrl, "Could not open the privacy policy.");

    private void GitHubStarButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink(RepositoryUrl, "Could not open the Scribe GitHub page.");

    private void GitHubIssueButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink(NewIssueUrl, "Could not open GitHub Issues.");

    private void GitHubSourceButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink(RepositoryUrl, "Could not open the Scribe source code.");

    private void OpenExternalLink(string url, string failureMessage)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TryLog(ex, failureMessage);
            ShowInfo(failureMessage, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Verifies the entered app registration by requesting a real ARM token with it, so the UI
    /// reports whether that identity actually works rather than merely whether it looks well formed.
    /// </summary>
    // Only asks for the endpoint and deployment when they are actually missing. Telling someone to
    // enter values already sitting in the boxes below reads as though the save did not take.
    private string DescribeServicePrincipalReady(AzureSignInStatus status)
    {
        var identity = DescribeAzureIdentity(status);
        var configured = !string.IsNullOrWhiteSpace(AzureEndpointBox?.Text)
            && !string.IsNullOrWhiteSpace(AzureDeploymentBox?.Text);
        return configured
            ? $"{identity} AI cleanup is ready to use."
            : $"{identity} Enter the endpoint and deployment name for your model.";
    }

    private async Task VerifyServicePrincipalAsync(bool automatic = false)
    {
        var principal = CurrentServicePrincipal;
        if (principal is null)
        {
            return;
        }

        // Guard the completion the same way the CLI probe does. Without this, editing a field or
        // switching modes mid-verification would let the old identity's result land on the new one.
        var operationVersion = ++_azureSignInProbeVersion;
        SetAzureConnectionBusy(true);
        AzureStatusText.Text = automatic
            ? "Checking the saved service principal…"
            : "Verifying the service principal…";
        try
        {
            // An explicit press means "try again", so drop the cached credential. An automatic
            // check reuses it, which is the whole point of the cache and keeps opening Settings
            // from costing a token request when cleanup already holds a valid one.
            if (!automatic)
            {
                AzureCredentialInvalidation.Invalidate();
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var status = await _azureDiscovery.GetSignInStatusAsync(
                principal.TenantId, null, cts.Token, principal);
            if (operationVersion != _azureSignInProbeVersion)
            {
                return;
            }

            _azureSignInStatus = status;
            AzureStatusText.Text = status.IsSignedIn
                ? DescribeServicePrincipalReady(status)
                : status.FailureReason ?? AzureSignInDiagnostics.Generic;
        }
        catch (OperationCanceledException)
        {
            if (operationVersion == _azureSignInProbeVersion)
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                AzureStatusText.Text = "Verifying the service principal timed out. Please try again.";
            }
        }
        catch (Exception ex)
        {
            // The exception is logged, never shown: an Entra failure can echo request details, and
            // this path handles a secret.
            TryLog(ex, "Could not verify the Azure service principal.");
            if (operationVersion == _azureSignInProbeVersion)
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                AzureStatusText.Text = "The service principal could not be verified. Check the details and try again.";
            }
        }
        finally
        {
            if (operationVersion == _azureSignInProbeVersion)
            {
                SetAzureConnectionBusy(false);
            }
        }
    }

    private static string DescribeAzureIdentity(AzureSignInStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Account))
        {
            return string.IsNullOrWhiteSpace(status.TenantId)
                ? "Signed in to Azure."
                : $"Signed in to Azure. Tenant: {status.TenantId}.";
        }

        return string.IsNullOrWhiteSpace(status.TenantId)
            ? $"Signed in as {status.Account}."
            : $"Signed in as {status.Account}. Tenant: {status.TenantId}.";
    }

    // Shared by the manual Refresh button, the auto-list-on-sign-in path, and the subscription
    // filter. Deployments are listed from the selected subscription only (or all of them when the
    // filter is on the "All subscriptions" sentinel).
    private async Task ListAzureDeploymentsAsync()
    {
        if (!_azureSignInStatus.IsSignedIn)
        {
            ApplyAzureSettingsAccess();
            return;
        }

        var loadVersion = ++_azureDeploymentLoadVersion;
        AzureRefreshButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var tenantInput = NullIfBlank(AzureTenantBox.Text);
            var tenantOverride = tenantInput is null
                ? null
                : _azureSignInStatus.TenantId ??
                  (Guid.TryParse(tenantInput, out var parsedTenantId)
                      ? parsedTenantId.ToString("D")
                      : null);
            var (subscriptionsOk, subscriptions, subscriptionsMessage) =
                await _azureCliInstaller.ListSubscriptionsAsync(cts.Token);
            if (!subscriptionsOk)
            {
                AzureStatusText.Text = $"{DescribeAzureIdentity(_azureSignInStatus)} {subscriptionsMessage}";
                return;
            }

            if (loadVersion != _azureDeploymentLoadVersion)
            {
                return;
            }

            var preserveAllSelection =
                _azureAutoListed &&
                SelectedAzureSubscription is null &&
                string.Equals(
                    AzureSubscriptionBox.SelectedItem as string,
                    AllAzureSubscriptionsLabel,
                    StringComparison.Ordinal);
            _azureAutoListed = true;
            PopulateAzureSubscriptions(subscriptions, preserveAllSelection);
            var selectedSubscription = SelectedAzureSubscription;
            var discovery = await DiscoverAzureDeploymentsAsync(
                subscriptions, selectedSubscription, tenantOverride, cts.Token);
            if (loadVersion != _azureDeploymentLoadVersion)
            {
                return;
            }

            var deployments = discovery.Deployments;

            _azureModelMap.TryGetValue(AzureModelBox.Text?.Trim() ?? string.Empty, out var previous);
            SetAzureDeployments(
                deployments,
                preferEndpoint: NullIfBlank(AzureEndpointBox.Text) ?? previous?.Endpoint,
                preferDeployment: NullIfBlank(AzureDeploymentBox.Text) ?? previous?.DeploymentName);

            var scope = selectedSubscription is null ? string.Empty : $" in {selectedSubscription.DisplayName}";
            var identity = DescribeAzureIdentity(_azureSignInStatus);
            AzureStatusText.Text = discovery.FailedTenantCount > 0
                ? deployments.Count == 0
                    ? $"{identity} No deployments could be listed. Check access to the selected tenant subscriptions."
                    : $"{identity} Found {deployments.Count} compatible deployment(s){scope}. Some tenants couldn't be checked."
                : deployments.Count == 0
                    ? $"{identity} No compatible deployments were returned{scope}. Check that the subscription contains a Responses-capable text model and that your account can list deployments."
                    : $"{identity} Found {deployments.Count} compatible deployment(s){scope}. Choose one for cleanup.";
        }
        catch (OperationCanceledException)
        {
            if (loadVersion == _azureDeploymentLoadVersion)
            {
                AzureStatusText.Text = "Listing Azure deployments timed out. Please try again.";
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not list Azure deployments.");
            if (loadVersion == _azureDeploymentLoadVersion)
            {
                AzureStatusText.Text =
                    "Couldn't list deployments. Sign in again and make sure you have access to a deployment.";
            }
        }
        finally
        {
            if (loadVersion == _azureDeploymentLoadVersion)
            {
                AzureRefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task<(IReadOnlyList<AzureFoundryDeployment> Deployments, int FailedTenantCount)>
        DiscoverAzureDeploymentsAsync(
            IReadOnlyList<AzureSubscription> subscriptions,
            AzureSubscription? selectedSubscription,
            string? tenantOverride,
            CancellationToken cancellationToken)
    {
        if (selectedSubscription is not null)
        {
            var tenantId = string.IsNullOrWhiteSpace(selectedSubscription.TenantId)
                ? tenantOverride
                : selectedSubscription.TenantId;
            var selectedDeployments = await Task.Run(
                () => _azureDiscovery.DiscoverAsync(tenantId, selectedSubscription.Id, cancellationToken),
                cancellationToken);
            return (selectedDeployments, 0);
        }

        var accountGroups = subscriptions
            .Where(subscription => !string.IsNullOrWhiteSpace(subscription.TenantId))
            .Where(subscription => tenantOverride is null ||
                                   string.Equals(
                                       subscription.TenantId,
                                       tenantOverride,
                                       StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                subscription => $"{subscription.TenantId}\0{subscription.AccountName}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var concurrency = new SemaphoreSlim(3, 3);
        var tasks = accountGroups.Select(async accountGroup =>
        {
            await concurrency.WaitAsync(cancellationToken);
            var representative = accountGroup.First();
            try
            {
                var tenantDeployments = await Task.Run(
                    () => _azureDiscovery.DiscoverAcrossSubscriptionsAsync(
                        representative.TenantId,
                        accountGroup.Select(subscription => subscription.Id).ToList(),
                        representative.Id,
                        cancellationToken),
                    cancellationToken);
                return (Deployments: tenantDeployments, Failed: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                TryLog(ex, "Could not list Azure deployments for an Azure CLI account scope.");
                return (
                    Deployments: (IReadOnlyList<AzureFoundryDeployment>)Array.Empty<AzureFoundryDeployment>(),
                    Failed: true);
            }
            finally
            {
                concurrency.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        var deployments = results
            .SelectMany(result => result.Deployments)
            .DistinctBy(item => (item.Endpoint, item.DeploymentName))
            .OrderBy(item => item.AccountName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (deployments, results.Count(result => result.Failed));
    }

    /// <summary>The subscription the filter dropdown points at, or null for "All subscriptions".</summary>
    private AzureSubscription? SelectedAzureSubscription =>
        AzureSubscriptionBox.SelectedItem is string label &&
        _azureSubscriptionMap.TryGetValue(label, out var subscription)
            ? subscription
            : null;

    // Rebuilds the subscription dropdown from a fresh listing, keeping the current choice selected
    // by id. An empty listing (transient failure) keeps the seeded stand-in so a saved filter is
    // never silently dropped.
    private void PopulateAzureSubscriptions(
        IReadOnlyList<AzureSubscription> subscriptions,
        bool preserveAllSelection)
    {
        if (subscriptions.Count == 0)
        {
            return;
        }

        var current = SelectedAzureSubscription;
        var currentId = current?.Id;
        var candidates = current is null
            ? subscriptions
            : subscriptions.Concat([current]).ToList();
        var preferredId = preserveAllSelection
            ? null
            : AzureSubscriptionSelection.ChooseInitialSubscriptionId(
                candidates,
                currentId,
                _azureSignInStatus.TenantId);
        _updatingAzureSubscriptions = true;
        try
        {
            _azureSubscriptionMap.Clear();
            var items = new List<string>(subscriptions.Count + 1) { AllAzureSubscriptionsLabel };
            string? reselect = null;
            foreach (var subscription in subscriptions)
            {
                var label = subscription.DisplayName;
                // Two subscriptions can share a display name; the id makes the row unambiguous.
                if (label == AllAzureSubscriptionsLabel || _azureSubscriptionMap.ContainsKey(label))
                {
                    label = $"{subscription.DisplayName} ({subscription.Id})";
                }

                _azureSubscriptionMap[label] = subscription;
                items.Add(label);
                if (preferredId is not null &&
                    string.Equals(subscription.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                {
                    reselect = label;
                }
            }

            if (reselect is null &&
                current is not null &&
                preferredId is not null &&
                string.Equals(current.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            {
                var label = current.DisplayName;
                if (label == AllAzureSubscriptionsLabel || _azureSubscriptionMap.ContainsKey(label))
                {
                    label = $"{current.DisplayName} ({current.Id})";
                }

                _azureSubscriptionMap[label] = current;
                items.Add(label);
                reselect = label;
            }
            AzureSubscriptionBox.ItemsSource = items;
            AzureSubscriptionBox.ItemsSource = items;
            AzureSubscriptionBox.SelectedItem = reselect ?? AllAzureSubscriptionsLabel;
        }
        finally
        {
            _updatingAzureSubscriptions = false;
        }
    }

    // Shows the saved subscription filter before any sign-in discovery runs, mirroring
    // SeedAzureModelFromSettings: a stand-in row that the first real listing replaces.
    private void SeedAzureSubscriptionsFromSettings()
    {
        _updatingAzureSubscriptions = true;
        try
        {
            _azureSubscriptionMap.Clear();
            var items = new List<string> { AllAzureSubscriptionsLabel };
            var selected = AllAzureSubscriptionsLabel;

            var savedId = _settings.AiCleanupAzureSubscriptionId?.Trim();
            if (!string.IsNullOrEmpty(savedId))
            {
                var standIn = new AzureSubscription(
                    savedId,
                    _settings.AiCleanupAzureSubscriptionName ?? savedId,
                    _settings.AiCleanupAzureSubscriptionTenantId ?? string.Empty);
                _azureSubscriptionMap[standIn.DisplayName] = standIn;
                items.Add(standIn.DisplayName);
                selected = standIn.DisplayName;
            }

            AzureSubscriptionBox.ItemsSource = items;
            AzureSubscriptionBox.SelectedItem = selected;
        }
        finally
        {
            _updatingAzureSubscriptions = false;
        }
    }

    private async void AzureSubscriptionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Programmatic updates (seeding, post-listing rebuilds) must not re-trigger discovery.
        if (_updatingAzureSubscriptions)
        {
            return;
        }

        // Before the first listing there is nothing to filter; the choice is picked up when the
        // user signs in and the initial listing runs.
        if (!_azureAutoListed)
        {
            return;
        }

        await RefreshAzureConnectionAsync(
            allowInteractiveLogin: false,
            listModels: true,
            forceListModels: true);
    }

    private async void AzureCliButton_Click(object sender, RoutedEventArgs e)
    {
        AzureCliButton.IsEnabled = false;
        AzureCliStatusText.Text = "Installing or updating the Azure CLI via winget… this can take a minute.";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (ok, message) = await _azureCliInstaller.InstallOrUpdateAsync(cts.Token);
            AzureCliStatusText.Text = message;
            if (ok)
            {
                _azureCliInstalled = true;
                _azureConnectionKnown = true;
                ApplyAzureSettingsAccess();
                await RefreshAzureConnectionAsync(
                    allowInteractiveLogin: false,
                    listModels: true,
                    forceListModels: true);
            }
        }
        catch (OperationCanceledException)
        {
            AzureCliStatusText.Text =
                "Azure CLI install timed out. Try again, or install it from https://aka.ms/installazurecliwindows.";
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not install or update Azure CLI.");
            AzureCliStatusText.Text =
                "Couldn't run the installer. Install the Azure CLI from https://aka.ms/installazurecliwindows.";
        }
        finally
        {
            AzureCliButton.IsEnabled = true;
        }
    }

    private void TryLog(Exception ex, string message)
    {
        try
        {
            _log.LogWarning(ex, message);
        }
        catch
        {
            // Diagnostics must never disrupt the settings window.
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
                     string.Equals(d.Endpoint, preferEndpoint, StringComparison.OrdinalIgnoreCase) ||
                     // A saved endpoint may be either form for the same deployment, so a project
                     // endpoint must still re-select its discovered entry on the next open.
                     string.Equals(d.ProjectEndpoint, preferEndpoint, StringComparison.OrdinalIgnoreCase)))
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
            SubscriptionId: _settings.AiCleanupAzureSubscriptionId ?? string.Empty,
            SubscriptionName: _settings.AiCleanupAzureSubscriptionName ?? string.Empty,
            TenantId: _settings.AiCleanupAzureSubscriptionTenantId ?? string.Empty,
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
        _selectedAzureDeployment = current;
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
            : _azureSignInStatus.IsSignedIn
                ? "Choose a discovered deployment, or enter its exact name below."
                : "Sign in before browsing deployments.";
    }

    private void OnCleanupStatusChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        RefreshAiStatus();
        RefreshUsageInsightAvailability();
    }));

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
            AzureCleanupStatusText.Text = detail;
        }
        else
        {
            AiStatusText.Text = detail;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Closing mid-capture must never leave the global hook in pass-through, or the
        // push-to-talk key would stay dead until the app restarts.
        if (_capturing)
        {
            _capturing = false;
            _setHotkeyCaptureMode(false);
        }

        _cleanup.StatusChanged -= OnCleanupStatusChanged;
        if (_updates is not null)
        {
            _updates.UpdateReady -= OnUpdateReady;
        }

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

    // "Save" persists and keeps the window open so the user can move page by page; "Save and close"
    // does the same and then closes. Both first give the user a chance to drop dictionary entries a
    // library already covers, which has to happen here rather than inside TrySave: the prompt is
    // async and TrySave is called on a synchronous path.
    //
    // _saveInProgress guards the await. These are async void handlers, so without it a second click
    // while the confirm dialog is open would start a parallel save and the two could interleave
    // against the same rows.
    private bool _saveInProgress;

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saveInProgress)
        {
            return;
        }

        _saveInProgress = true;
        try
        {
            if (await ConfirmDictionaryOverlapAsync() && TrySave())
            {
                ShowInfo("Settings saved.");
            }
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    private async void SaveCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saveInProgress)
        {
            return;
        }

        _saveInProgress = true;
        try
        {
            if (await ConfirmDictionaryOverlapAsync() && TrySave())
            {
                Close();
            }
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    /// <summary>
    /// Offers to drop dictionary entries that an enabled library already covers identically. Returns
    /// false only when the user backs out of saving entirely.
    /// </summary>
    /// <remarks>
    /// A personal dictionary quietly accumulates entries that a library later started covering, and
    /// nothing looks wrong because both layers produce the same text. It still costs: personal
    /// entries merge ahead of library entries and consume the AI glossary budget first, so redundant
    /// ones displace terms a model genuinely cannot guess.
    /// <para>
    /// Entries that write the same spoken form <i>differently</i> are never offered for removal.
    /// Those are deliberate overrides ("v s" meaning versus, not Visual Studio) and deleting one
    /// would silently change what the user's dictation says. They are reported, not touched.
    /// </para>
    /// </remarks>
    private async Task<bool> ConfirmDictionaryOverlapAsync()
    {
        try
        {
            DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

            if (DictionarySignature() == _dictionarySnapshot)
            {
                return true; // nothing changed, so nothing new to warn about
            }

            var entries = BuildDictionaryEntries(out var duplicate);
            if (duplicate is not null)
            {
                return true; // TrySave reports the duplicate; don't stack two dialogs
            }

            var libraries = _loadedLibraries
                .Where(l => _libraryRows.Any(r =>
                    r.Enabled && string.Equals(r.Id, l.Id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var libraryEntries = new List<DictionaryEntry>();
            var sourceByPattern = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                foreach (var entry in library.Entries)
                {
                    libraryEntries.Add(entry);
                    if (!string.IsNullOrWhiteSpace(entry.Pattern))
                    {
                        sourceByPattern.TryAdd(entry.Pattern.Trim(), library.Name);
                    }
                }
            }

            var report = DictionaryLibraryOverlapAnalyzer.Analyze(entries, libraryEntries, sourceByPattern);
            if (report.RedundantCount == 0)
            {
                return true; // overrides alone are legitimate; warning about them every save is noise
            }

            var sample = string.Join('\n', report.Redundant
                .Take(6)
                .Select(o => $"    {o.Pattern}  ->  {o.Replacement}" +
                             (string.IsNullOrEmpty(o.LibraryId) ? string.Empty : $"   ({o.LibraryId})")));
            var more = report.RedundantCount > 6 ? $"\n    and {report.RedundantCount - 6:N0} more" : string.Empty;

            var overrideNote = report.OverrideCount == 0
                ? string.Empty
                : $"\n\n{report.OverrideCount:N0} other {(report.OverrideCount == 1 ? "entry writes" : "entries write")} " +
                  "a term differently from the library. Those are kept: your version wins, which is " +
                  "probably why you added them.";

            var count = report.RedundantCount;
            var noun = count == 1 ? "entry is" : "entries are";
            var confirmed = await ConfirmAsync(
                "Some entries are already covered",
                $"{count:N0} dictionary {noun} already handled identically by a library you have " +
                $"turned on:\n\n{sample}{more}\n\n" +
                "Removing them changes nothing about your dictation, and frees room in the glossary " +
                "sent to AI cleanup for terms it cannot guess." + overrideNote,
                count == 1 ? "Remove it" : $"Remove {count:N0}");

            if (confirmed)
            {
                var drop = new HashSet<string>(
                    report.Redundant.Select(o => o.Pattern), StringComparer.OrdinalIgnoreCase);

                foreach (var row in _rows.Where(r =>
                    !string.IsNullOrWhiteSpace(r.Pattern) && drop.Contains(r.Pattern.Trim())).ToList())
                {
                    _rows.Remove(row);
                }
            }

            return true; // "Cancel" declines the cleanup, not the save
        }
        catch (Exception ex)
        {
            // A hygiene prompt must never be the reason a save fails.
            _log.LogWarning(ex, "Could not check the dictionary against enabled libraries.");
            return true;
        }
    }

    // Validates, persists and applies the settings. Returns true on success; on a validation problem
    // or a save error it surfaces its own message, leaves the window open, and returns false.
    private bool TrySave()
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
            return false;
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
            return false;
        }

        var standardBinding = _pendingBinding with { Mode = SelectedMode };
        var dictationOnlyBinding = _pendingDictationOnlyBinding is null
            ? null
            : _pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode };
        if (dictationOnlyBinding is not null && SamePhysicalBinding(standardBinding, dictationOnlyBinding))
        {
            ShowSection(SectionGeneral);
            ShowThemedMessage(
                "Hotkey conflict",
                "The AI-cleanup and dictation-only hotkeys must use different keys.");
            return false;
        }

        var azureValidation = AzureSettingsAccess.ValidateCleanup(
            enabled: AiCleanupCheck.IsChecked == true,
            usesAzureProvider: SelectedProvider == CleanupProvider.AzureFoundry,
            signedIn: _azureSignInStatus.IsSignedIn,
            apiKey: AzureApiKeyBox.Password,
            endpoint: AzureEndpointBox.Text,
            deployment: AzureDeploymentBox.Text,
            authMode: SelectedAzureAuthMode,
            tenantId: SpTenantBox.Text,
            clientId: SpClientIdBox.Text,
            clientSecret: SpClientSecretBox.Password);
        if (azureValidation != AzureSettingsAccess.ValidationIssue.None)
        {
            ShowSection(SectionAi);
            var message = azureValidation switch
            {
                AzureSettingsAccess.ValidationIssue.AuthenticationRequired =>
                    "Sign in to Azure, or use an endpoint and API key, before enabling Microsoft Foundry cleanup.",
                AzureSettingsAccess.ValidationIssue.ServicePrincipalIncomplete =>
                    "Enter the tenant ID, client ID, and client secret for the service principal, then verify them.",
                AzureSettingsAccess.ValidationIssue.EndpointRequired =>
                    "Choose a discovered model or enter the Microsoft Foundry or Azure OpenAI endpoint.",
                AzureSettingsAccess.ValidationIssue.DeploymentRequired =>
                    "Choose a discovered model or enter its exact Azure deployment name.",
                _ => "Complete the Microsoft Foundry configuration before saving.",
            };
            AzureStatusText.Text = message;
            ShowThemedMessage("Microsoft Foundry is not ready", message);
            return false;
        }

        try
        {
            var device = (DeviceChoice?)DeviceCombo.SelectedItem;
            _settings.InputDeviceId = device?.Id;
            _settings.InputDeviceName = device?.Id is null
                ? null
                : device.PersistedName ?? StripDefaultSuffix(device.Name);

            _settings.Hotkey = standardBinding;
            _settings.DictationOnlyHotkey = dictationOnlyBinding;
            _settings.ShowOverlay = OverlayCheck.IsChecked == true;
            _settings.OverlayPosition = SelectedOverlayPosition;
            _settings.UseVoiceActivityDetection = VadCheck.IsChecked == true;
            _settings.AutoStopOnSilence = AutoStopCheck.IsChecked == true;
            _settings.ApplyPostProcessing = PostCheck.IsChecked == true;
            _settings.LaunchOnLogin = LaunchCheck.IsChecked == true;
            _settings.StoreAudioHistory = StoreAudioCheck.IsChecked == true;
            _settings.ShiftEnterLineBreaks = ShiftEnterCheck.IsChecked == true;
            _settings.UseHighAccuracyDecoding = BeamSearchCheck.IsChecked == true;
            _settings.InjectionMethod =
                ((InjectionChoice?)InjectionCombo.SelectedItem)?.Method ?? InjectionMethod.UnicodeType;
            _settings.NewlineHandling =
                ((NewlineChoice?)NewlineCombo.SelectedItem)?.Mode ?? NewlineInjectionMode.SmartFlatten;
            _settings.Profiles = BuildProfiles();
            _settings.EnabledDictionaryLibraryIds = CollectEnabledLibraryIds();
            _settings.DecodeThreads = (int)ThreadsSlider.Value;
            _settings.TranscriptionModelId =
                ((TranscriptionModel?)TranscriptionModelCombo.SelectedItem)?.Id ??
                TranscriptionModelCatalog.DefaultId;

            _settings.EnableAiCleanup = AiCleanupCheck.IsChecked == true;
            _settings.AiCleanupProvider = SelectedProvider;
            _settings.AiCleanupModel =
                NullIfBlank(AiModelBox.Text) ?? CleanupModelCatalog.DefaultAlias;
            _settings.AiCleanupAzureEndpoint = NullIfBlank(AzureEndpointBox.Text);
            _settings.AiCleanupAzureDeployment = NullIfBlank(AzureDeploymentBox.Text);
            _settings.AiCleanupAzureApiKey = NullIfBlank(AzureApiKeyBox.Password);
            _settings.AiCleanupAzureAuthMode = SelectedAzureAuthMode;
            // One tenant setting, edited from whichever box the active mode shows.
            _settings.AiCleanupAzureTenantId = SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal
                ? NullIfBlank(SpTenantBox.Text)
                : NullIfBlank(AzureTenantBox.Text);
            _settings.AiCleanupAzureClientId = NullIfBlank(SpClientIdBox.Text);
            _settings.AiCleanupAzureClientSecret = NullIfBlank(SpClientSecretBox.Password);
            // The credential is cached for token reuse, so a changed identity has to drop it or the
            // next dictation would keep authenticating as the previous one.
            AzureCredentialInvalidation.Invalidate();
            var azureSubscription = AzureSubscriptionSelection.ResolveAuthenticationSubscription(
                _selectedAzureDeployment,
                SelectedAzureSubscription,
                AzureEndpointBox.Text,
                AzureDeploymentBox.Text);
            _settings.AiCleanupAzureSubscriptionId = azureSubscription?.Id;
            _settings.AiCleanupAzureSubscriptionName = azureSubscription?.Name;
            _settings.AiCleanupAzureSubscriptionTenantId = azureSubscription?.TenantId;
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

            var previousLaunchState = StartupRegistration.IsEnabled();
            if (!StartupRegistration.Set(_settings.LaunchOnLogin))
            {
                throw new InvalidOperationException("Windows did not accept the Start with Windows change.");
            }

            try
            {
                _settingsRepository.SaveBundle(_settings, entries, snippets);
            }
            catch
            {
                StartupRegistration.Set(previousLaunchState);
                throw;
            }

            _applySettings(_settings);

            // Refresh the saved-state snapshots so an immediate re-save of an unchanged section is a
            // no-op — important now that Save keeps the window open for page-by-page editing.
            _dictionarySnapshot = DictionarySignature();
            _snippetSnapshot = SnippetSignature();
            return true;
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
            return false;
        }
        catch (Exception ex)
        {
            ShowThemedMessage("Scribe", $"Could not save settings:\n{ex.Message}");
            return false;
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
            if (SelectedProvider != CleanupProvider.FoundryLocal &&
                !await ConfirmAsync(
                    "Send recent dictations to your AI provider?",
                    "To suggest vocabulary, Scribe will send up to 6,000 characters from recent " +
                    "dictation history to the provider endpoint you configured. Audio is never sent.",
                    "Send and continue"))
            {
                return;
            }

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

        DictionarySuggestButton.ToolTip = null;
        DictionarySuggestButton.IsEnabled = false;
        DictionarySuggestBusy.Visibility = Visibility.Visible;
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
            DictionarySuggestBusy.Visibility = Visibility.Collapsed;
            DictionarySuggestButton.IsEnabled = true;
            DictionarySuggestButton.ToolTip =
                "Learn vocabulary from recent dictations. A configured remote AI provider receives " +
                "a bounded text sample only after you confirm; otherwise Scribe scans locally.";
        }
    }

    private void SuggestWithMiner(IReadOnlyList<DictionaryEntry> current, bool aiRanFirst = false)
    {
        IReadOnlyList<DictionaryEntry> suggestions;
        try
        {
            suggestions = DictionaryHistoryLearner.BuildEntries(_history.GetRecent(1000), current);
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

        AddSuggestionRows(suggestions.Select(s => (s.Pattern, s.Replacement)));
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

    // --- History --------------------------------------------------------------------------

    private void LoadHistory()
    {
        _historyRows.Clear();
        foreach (var entry in _history.GetRecent(200))
        {
            _historyRows.Add(HistoryRow.From(entry));
        }

        var hasRows = _historyRows.Count > 0;
        HistoryEmptyHint.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        HistoryClearButton.IsEnabled = hasRows;
        UpdateHistorySelection();
    }

    // --- Usage ----------------------------------------------------------------------------

    private void UsagePeriodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            LoadUsage();
        }
    }

    private void UsageRefreshButton_Click(object sender, RoutedEventArgs e) => LoadUsage();

    private async void LoadUsage()
    {
        const int historyLimit = 5000;
        var loadVersion = Interlocked.Increment(ref _usageLoadVersion);
        var period = UsagePeriodBox.SelectedItem as UsagePeriodChoice ?? UsagePeriodChoice.All[1];
        UsageCoverageText.Text = "Calculating from local history...";

        try
        {
            var result = await Task.Run(() =>
            {
                var now = DateTimeOffset.UtcNow;
                var recent = _history.GetRecent(historyLimit + 1);
                var entries = recent.Take(historyLimit).ToList();
                var since = period.Days is { } days
                    ? now.AddDays(-(days - 1))
                    : entries.Count == 0
                        ? now
                        : entries.Min(entry => entry.TimestampUtc);
                var knownTerms = _dictionary.GetEnabled()
                    .Concat(_libraries.GetEnabledLibraryEntries())
                    .ToList();
                var snapshot = UsageAnalyzer.Compute(entries, knownTerms, since, now);
                var periodCapped = recent.Count > historyLimit &&
                    (period.Days is null || recent[historyLimit].TimestampUtc >= since);
                return (Snapshot: snapshot, PeriodCapped: periodCapped);
            });

            if (loadVersion != Volatile.Read(ref _usageLoadVersion))
            {
                return;
            }

            _usageSnapshot = result.Snapshot;
            var snapshot = _usageSnapshot;

            UsageCoverageText.Text = result.PeriodCapped
                ? $"{period.Label}, based on the latest {historyLimit:N0} retained dictations."
                : $"{period.Label}, {_usageSnapshot.Dictations:N0} retained dictation" +
                  (_usageSnapshot.Dictations == 1 ? "." : "s.");
            UsageDictationsText.Text = snapshot.Dictations.ToString("N0");
            UsageWordsText.Text = snapshot.Words.ToString("N0");
            UsageActiveDaysText.Text = snapshot.ActiveDays.ToString("N0");
            UsageSpeechText.Text = FormatDuration(snapshot.Speech);
            UsageAverageText.Text = snapshot.AverageWords.ToString("0.#");
            UsageAppsGrid.ItemsSource = snapshot.TopApps;

            var weekly = snapshot.Granularity == UsageAnalyzer.TrendGranularity.Weekly;
            var trendRows = UsageTrendNormalizer.Normalize(snapshot.Trend)
                .Select(point => new UsageTrendRow(
                    weekly ? $"Week of {point.Trend.Start:MMM d}" : point.Trend.Start.ToString("MMM d"),
                    point.Trend.Dictations,
                    point.Trend.Words,
                    point.RelativeHeight))
                .ToList();
            UsageTrendChart.ItemsSource = trendRows;
            UsageTrendGrid.ItemsSource = trendRows;

            var covered = snapshot.Terms.Where(term => term.Covered).ToList();
            var novel = snapshot.Terms.Where(term => !term.Covered).ToList();
            UsageKnownTerms.ItemsSource = covered.Count == 0
                ? ["No recognized technologies in this period."]
                : covered.Select(FormatUsageTerm).ToList();
            UsageNovelEmptyHint.Visibility = novel.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UsageNovelTerms.Visibility = novel.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UsageNovelTerms.ItemsSource = novel
                .Select(term => new UsageTermRow(term.Text, term.Dictations))
                .ToList();

            UsageInsightText.Text = snapshot.Dictations == 0
                ? "Record a dictation to make usage insight available."
                : "Generate a short summary. Only totals and dictionary term labels are sent.";
            RefreshUsageInsightAvailability();
        }
        catch (Exception ex)
        {
            if (loadVersion != Volatile.Read(ref _usageLoadVersion))
            {
                return;
            }

            _usageSnapshot = null;
            UsageCoverageText.Text = "Usage is temporarily unavailable.";
            UsageInsightText.Text = ex.Message;
            UsageInsightButton.IsEnabled = false;
        }

        static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
            ? $"{duration.TotalHours:0.#} hr"
            : $"{duration.TotalMinutes:0.#} min";

        static string FormatUsageTerm(UsageAnalyzer.TermUsage term) =>
            $"{term.Text} ({term.Dictations:N0} dictation{(term.Dictations == 1 ? string.Empty : "s")})";
    }

    private async void UsageNovelTermAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button button || button.DataContext is not UsageTermRow term)
        {
            return;
        }

        button.IsEnabled = false;
        await Task.Yield();

        try
        {
            var entry = DictionaryEntry.New(term.Text.ToLowerInvariant(), term.Text);
            var persisted = PersistLearnedDictionaryEntries([entry]);
            if (persisted.Count == 0)
            {
                ShowInfo(
                    $"\"{term.Text}\" is already in the dictionary grid.",
                    Wpf.Ui.Controls.InfoBarSeverity.Informational);
                return;
            }

            // ApplySettings owns the live post-processor reload used by the normal Save path.
            _applySettings(_settings);
            ShowInfo($"Added \"{term.Text}\" to your dictionary.");
            LoadUsage();
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            ShowInfo(
                $"Couldn't add \"{term.Text}\" to your dictionary: {ex.Message}",
                Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void RefreshUsageInsightAvailability()
    {
        if (UsageInsightButton is null)
        {
            return;
        }

        UsageInsightButton.IsEnabled = !_usageInsightRunning &&
            _usageSnapshot is { Dictations: > 0 } &&
            _cleanup.Status == CleanupStatus.Ready;
    }

    private async void UsageInsightButton_Click(object sender, RoutedEventArgs e)
    {
        if (_usageInsightRunning || _usageSnapshot is not { Dictations: > 0 } snapshot)
        {
            return;
        }

        if (_cleanup.Status != CleanupStatus.Ready)
        {
            UsageInsightText.Text = "Configure a ready AI cleanup model to generate an insight.";
            return;
        }

        _usageInsightRunning = true;
        RefreshUsageInsightAvailability();
        UsageInsightText.Text = "Generating insight...";
        try
        {
            var response = await _cleanup.CompleteAsync(
                UsageInsight.SystemPrompt,
                UsageInsight.BuildSummary(snapshot));
            UsageInsightText.Text = UsageInsight.Parse(response)
                ?? "The configured model did not return an insight.";
        }
        catch (Exception ex)
        {
            UsageInsightText.Text = $"Insight failed: {ex.Message}";
        }
        finally
        {
            _usageInsightRunning = false;
            RefreshUsageInsightAvailability();
        }
    }

    private HistoryRow? SelectedHistory => HistoryGrid.SelectedItem as HistoryRow;

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateHistorySelection();

    private void UpdateHistorySelection()
    {
        var hasSelection = SelectedHistory is not null;
        HistoryCopyButton.IsEnabled = hasSelection;
        HistoryDeleteButton.IsEnabled = hasSelection;
    }

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => CopyHistoryText();

    private void HistoryCopyButton_Click(object sender, RoutedEventArgs e) => CopyHistoryText();

    private void CopyHistoryText()
    {
        if (SelectedHistory is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Text);
            ShowInfo("Copied the selected dictation.");
        }
        catch (Exception ex)
        {
            ShowInfo($"Couldn't copy the dictation: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async void HistoryDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory is not { } row)
        {
            return;
        }

        HistoryDeleteButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _history.Delete(row.Id));
            LoadHistory();
            ShowInfo("Deleted the selected history entry.");
        }
        catch (Exception ex)
        {
            ShowInfo($"Couldn't delete the history entry: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
            UpdateHistorySelection();
        }
    }

    private async void HistoryClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRows.Count == 0 ||
            !await ConfirmAsync(
                "Clear history",
                "Delete all dictation history and stored audio? This cannot be undone.",
                "Clear all"))
        {
            return;
        }

        HistoryClearButton.IsEnabled = false;
        try
        {
            await Task.Run(_history.Clear);
            LoadHistory();
            ShowInfo("Cleared dictation history.");
        }
        catch (Exception ex)
        {
            ShowInfo($"Couldn't clear history: {ex.Message}", Wpf.Ui.Controls.InfoBarSeverity.Error);
            HistoryClearButton.IsEnabled = _historyRows.Count > 0;
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

    private sealed record DeviceChoice(string? Id, string Name, string? PersistedName = null);

    private sealed record InjectionChoice(InjectionMethod Method, string Label);

    private sealed record NewlineChoice(NewlineInjectionMode Mode, string Label);

    private sealed record ProviderChoice(CleanupProvider Provider, string Label);
    private sealed record PromptStyleChoice(CleanupPromptStyle Style, string Label);
    private sealed record UsagePeriodChoice(int? Days, string Label)
    {
        public static IReadOnlyList<UsagePeriodChoice> All { get; } =
        [
            new(7, "Last 7 days"),
            new(30, "Last 30 days"),
            new(90, "Last 90 days"),
            new(null, "All retained history"),
        ];
    }

    private sealed record UsageTrendRow(
        string Period,
        int Dictations,
        int Words,
        double RelativeHeight)
    {
        public string ToolTip =>
            $"{Period}: {Dictations:N0} dictation{(Dictations == 1 ? string.Empty : "s")}, " +
            $"{Words:N0} word{(Words == 1 ? string.Empty : "s")}";
    }

    private sealed record UsageTermRow(string Text, int Dictations)
    {
        public string DictationLabel =>
            $"({Dictations:N0} dictation{(Dictations == 1 ? string.Empty : "s")})";
    }

    /// <summary>
    /// Editable dictionary row backing the grid. Raises change notifications so the Library column
    /// can update as the user types, which is the whole point of showing it: an entry that starts
    /// duplicating a library the moment you finish typing it should say so immediately, not after a
    /// save round trip.
    /// </summary>
    public sealed class DictionaryRow : INotifyPropertyChanged
    {
        private string _pattern = string.Empty;
        private string _replacement = string.Empty;
        private bool _wholeWord = true;
        private bool _enabled = true;
        private DictionaryRowCoverage _coverage;
        private string _coverageTooltip = string.Empty;

        public long Id { get; set; }

        public string Pattern
        {
            get => _pattern;
            set => Set(ref _pattern, value ?? string.Empty);
        }

        public string Replacement
        {
            get => _replacement;
            set => Set(ref _replacement, value ?? string.Empty);
        }

        public bool WholeWord
        {
            get => _wholeWord;
            set => Set(ref _wholeWord, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        /// <summary>How this entry relates to the libraries that are currently switched on.</summary>
        public DictionaryRowCoverage Coverage
        {
            get => _coverage;
            set
            {
                if (Set(ref _coverage, value))
                {
                    OnPropertyChanged(nameof(CoverageLabel));
                    OnPropertyChanged(nameof(CoverageAppearance));
                    OnPropertyChanged(nameof(CoverageVisibility));
                }
            }
        }

        public string CoverageTooltip
        {
            get => _coverageTooltip;
            set => Set(ref _coverageTooltip, value);
        }

        public string CoverageLabel => Coverage switch
        {
            DictionaryRowCoverage.Duplicate => "Same as library",
            DictionaryRowCoverage.Override => "Overrides library",
            _ => string.Empty,
        };

        // Caution reads as "you can probably delete this"; Info reads as "this is doing something".
        public Wpf.Ui.Controls.ControlAppearance CoverageAppearance => Coverage switch
        {
            DictionaryRowCoverage.Duplicate => Wpf.Ui.Controls.ControlAppearance.Caution,
            _ => Wpf.Ui.Controls.ControlAppearance.Info,
        };

        public Visibility CoverageVisibility =>
            Coverage == DictionaryRowCoverage.None ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>How a dictionary row relates to the enabled libraries.</summary>
    public enum DictionaryRowCoverage
    {
        /// <summary>No enabled library covers this spoken form. The entry is doing its own work.</summary>
        None = 0,

        /// <summary>A library produces exactly this, so the entry is clutter.</summary>
        Duplicate,

        /// <summary>A library writes this spoken form differently; this entry wins.</summary>
        Override,
    }

    private sealed record HistoryRow(
        long Id, string When, string Text, string App, string Audio, string Decode, string Cleanup)
    {
        public static HistoryRow From(HistoryEntry entry) => new(
            entry.Id,
            entry.TimestampUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
            entry.Text,
            string.IsNullOrWhiteSpace(entry.TargetApp) ? HistoryRowFormat.NotApplicable : entry.TargetApp!,
            HistoryRowFormat.Audio(entry.AudioMilliseconds),
            HistoryRowFormat.Latency(entry.DecodeMilliseconds),
            HistoryRowFormat.Latency(entry.CleanupMilliseconds));
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
