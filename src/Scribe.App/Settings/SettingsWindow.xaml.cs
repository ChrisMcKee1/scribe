using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Scribe.Core.Feedback;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Scribe.App.Dictation;
using Scribe.App.Infrastructure;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Transcription;

namespace Scribe.App.Settings;

/// <summary>
/// Modeless settings editor. Loads the persisted <see cref="AppSettings"/>, then reads the
/// dictionary, snippets, libraries, history and diagnostics off the UI thread so the window opens
/// at once; each of those sections stays read-only until its rows arrive. Lets the user change the
/// microphone, hotkey, behaviour toggles, text-insertion method and decode threads, and edit the
/// dictionary inline. On save it persists everything and calls back into the dictation controller
/// so the new binding and dictionary take effect without a restart. Start with Windows is the
/// exception: like Windows' own toggles it applies the moment it is flipped, without Save.
/// </summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string RepositoryUrl = ScribeLinks.Repository;
    private const string PrivacyPolicyUrl = ScribeLinks.PrivacyPolicy;
    private const string NewIssueUrl = ScribeLinks.NewIssue;

    private readonly ISettingsRepository _settingsRepository;
    private readonly IAudioCaptureService _audio;
    private readonly IDictionaryRepository _dictionary;
    private readonly IDictionaryLibraryService _libraries;
    private readonly ISnippetRepository _snippets;
    private readonly IHistoryRepository _history;
    private readonly ITextCleanupService _cleanup;
    private readonly ILibraryCatalogStore _libraryStore;
    private readonly IAzureFoundryDiscovery _azureDiscovery;
    private readonly AzureCliInstaller _azureCliInstaller;
    private readonly ICleanupFailureLog _failureLog;
    private readonly ITranscriptionModelInstaller _transcriptionModelInstaller;
    private readonly AppPaths _paths;
    private readonly StartupRegistration _startup;
    private readonly StartupToggle _startupToggle;
    private readonly StartupSwitchState _startupSwitch = new();
    private bool _showingStartupStatus;
    private Task? _startupApply;
    // The settings document could not be parsed and the window holds defaults. Only an explicit
    // Save may replace the stored copy, so Start with Windows must not write around it.
    private bool _settingsRecovered;
    private readonly SessionDiagnostics? _diagnostics;
    private readonly Action<OverlayPosition> _previewOverlay;

    // Both return the answer of the vocabulary generation the application asked for, which the window awaits before it
    // says a stored change is in effect.
    private readonly Func<AppSettings, Task<Scribe.Core.Vocabulary.VocabularyRefresh>> _applySettings;
    private readonly Func<Task<Scribe.Core.Vocabulary.VocabularyRefresh>> _reloadVocabulary;

    // The committed library vocabulary dictation uses, which the usage report's library selection comes from: never the
    // window's own library switches (after a failed Save _settings holds unsaved ones), and never a fresh read of the
    // stored document, which may have turned unreadable.
    private readonly ILibraryVocabularySource _libraryVocabulary;
    private readonly Action<bool> _setHotkeyCaptureMode;
    private readonly UpdateService? _updates;
    private StoreUpdateService? _storeUpdates;
    private readonly ILogger<SettingsWindow> _log;
    private readonly TranscriptionOptions _runningTranscription;

    private readonly AppSettings _settings;
    private AppSettings _committedSettings;
    private readonly ObservableCollection<DictionaryRow> _rows = new();
    private readonly ObservableCollection<LibraryRow> _libraryRows = new();
    private LibraryWorkspace? _wordPackWorkspace;
    private LibraryCatalog? _wordPackCatalog;
    private bool _updatingLibraryRows;
    private bool _updatingLibraryTerms;
    private LibraryOrdering? _libraryOrdering;
    private readonly LibrarySearch _librarySearch = LibrarySearch.ForCurrentCulture();
    private readonly LibraryTermSort _libraryTermSort = LibraryTermSort.ForCurrentCulture();
    private readonly ObservableCollection<LibraryTermRow> _libraryTermRows = new();
    private DispatcherTimer? _librarySearchTimer;
    private LibrarySearchResult? _librarySearchResult;
    private LibraryTermSortOrder _libraryTermSortOrder = LibraryTermSortOrder.SavedOrder;
    private string? _selectedLibraryId;
    private readonly WordPackSaveProtocol _wordPackSaveProtocol;
    private readonly ObservableCollection<SnippetRow> _snippetRows = new();
    private bool _loadingSnippet;
    private readonly ObservableCollection<ProfileRow> _profileRows = new();
    private bool _loadingProfile;
    private readonly ObservableCollection<HistoryRow> _historyRows = new();
    private readonly ObservableCollection<FailureRow> _failures = new();

    // Sections read off the UI thread. Until one has loaded, Save treats it as untouched.
    private readonly SettingsSectionLoad _dictionaryLoad = new();
    private readonly SettingsSectionLoad _libraryLoad = new();
    private readonly SettingsSectionLoad _snippetLoad = new();
    private readonly SettingsSectionLoad _historyLoad = new();
    private readonly SettingsSectionLoad _failureLoad = new();
    private readonly SettingsSectionLoad _statsLoad = new();
    private readonly PendingRowUpdates<long, AiRating> _ratingWrites = new();

    // Hint texts as written in the XAML, restored once a section's loading message is no longer needed.
    private string _historyEmptyText = string.Empty;
    private string _noFailuresText = string.Empty;
    private string _snippetEmptyText = string.Empty;
    private string _libraryDetailEmptyText = string.Empty;
    private string _statsSummaryEmptyText = string.Empty;

    private UsageAnalyzer.Snapshot? _usageSnapshot;

    // The library scope of the usage report the shown snapshot came from (UsageReport.Result.LibraryScope), set and
    // cleared with the snapshot: the usage insight is handed over only under it, so its library labels never go once
    // their library's AI permission narrowed or its content changed after the report was built (contract 3.3.6).
    private AiVocabularyScope _usageLibraryScope = AiVocabularyScope.None;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var accelerator =
            Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F ? SettingsAccelerator.Find :
            Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N ? SettingsAccelerator.AddTerm :
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N ? SettingsAccelerator.NewLibrary :
            (SettingsAccelerator?)null;
        if (accelerator is null)
        {
            return;
        }

        var onWordPacks = DictionaryTabs.SelectedItem == WordPacksTab;
        if (!SettingsCloseGuard.CanRunAccelerator(accelerator.Value, new AcceleratorState(_capturing, ImeComposing: false, onWordPacks)))
        {
            return;
        }

        e.Handled = true;
        switch (accelerator.Value)
        {
            case SettingsAccelerator.Find:
                LibrarySearchBox.Focus();
                LibrarySearchBox.SelectAll();
                break;
            case SettingsAccelerator.AddTerm:
                AddWordPackTerm();
                break;
            case SettingsAccelerator.NewLibrary:
                LibraryNewButton_Click(this, new RoutedEventArgs());
                break;
        }
    }
    private bool _usageInsightRunning;
    private readonly LatestRequestCoalescer<UsagePeriodChoice> _usageLoads = new();
    private int _azureDeploymentLoadVersion;
    private readonly Dictionary<string, CleanupModel> _foundryCuratedByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FoundryModelOption> _foundryExecutionBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AzureFoundryDeployment> _azureModelMap = new(StringComparer.OrdinalIgnoreCase);
    // Subscription filter for Azure model discovery. The sentinel "All subscriptions" row is not in
    // the map, so a missing lookup means "no filter" by construction.
    private const string AllAzureSubscriptionsLabel = "All subscriptions";
    private readonly Dictionary<string, AzureSubscription> _azureSubscriptionMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingAzureSubscriptions;
    private bool _foundryModelOp;
    private FoundryLocalSetupDescription? _foundryOperationStatus;
    private string? _foundryOperationAlias;
    private bool _azureAutoListed;
    private readonly AzureSignInAttempts _azureSignInAttempts = new();
    private bool _azureCliInstalled;
    private bool _azureConnectionKnown;
    private bool _azureManualConfiguration;
    private string? _azureStatusMessage;
    private AzureVerificationOutcome _azureApiKeyOutcome = AzureVerificationOutcome.NotRun;
    private AzureVerificationOutcome _servicePrincipalOutcome = AzureVerificationOutcome.NotRun;
    private AzureSignInStatus _azureSignInStatus = new(false, null);
    private AzureFoundryDeployment? _selectedAzureDeployment;
    private bool _azureApiKeyVerified;
    private bool _transcriptionModelOp;

    private HotkeyBinding _pendingBinding;
    private HotkeyBinding? _pendingDictationOnlyBinding;

    // The bindings the stored settings hold, as loaded or last saved, which Restore's notice compares with to say whether
    // Save is still needed. Not _settings itself: a Save fills that in before it stores anything, so after a Save that
    // failed it would hold keys that were never stored.
    private HotkeyBinding _savedBinding;
    private HotkeyBinding? _savedDictationOnlyBinding;

    // The AI cleanup provider the stored settings hold, as loaded or last saved, for the same reason: after a
    // Save that failed, _settings names a provider that cleanup never switched to.
    private CleanupProvider _savedAiProvider;
    private bool _capturingDictationOnly;

    // The keys and mouse buttons recorded since Set was pressed; what they mean is decided in Core.
    private HotkeyCaptureSession? _capture;
    private bool _capturing;
    private bool _finalized;
    private bool _loadingUi;

    // The tray's AI cleanup switch, held while a hotkey capture keeps the window's switch from showing it.
    private readonly ExternalSwitchSync _externalAiCleanup = new();
    private bool _showingExternalAiCleanup;

    // The microphone chosen from the tray, held while the picker's list is open: rebuilding its items under the user's
    // pointer would close the list in their hand. The devices it offers, and whether a refresh waits for the list too.
    private readonly ExternalChoiceSync<MicrophoneSelection> _externalMicrophone = new();
    private IReadOnlyList<AudioDevice> _inputDevices = [];
    private bool _showingMicrophones;
    private bool _microphonesStale;

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
        AppPaths paths,
        StartupRegistration startup,
        IOptions<TranscriptionOptions> runningTranscription,
        Action<OverlayPosition> previewOverlay,
        Func<AppSettings, Task<Scribe.Core.Vocabulary.VocabularyRefresh>> applySettings,
        Func<Task<Scribe.Core.Vocabulary.VocabularyRefresh>> reloadVocabulary,
        ILibraryVocabularySource libraryVocabulary,
        Action<bool>? setHotkeyCaptureMode = null,
        UpdateService? updates = null,
        SessionDiagnostics? diagnostics = null)
    {
        _settingsRepository = settingsRepository;
        _audio = audio;
        _dictionary = dictionary;
        _libraries = libraries;
        _libraryStore = libraries as ILibraryCatalogStore
            ?? throw new InvalidOperationException("The word pack editor needs the library catalog store.");
        _snippets = snippets;
        _history = history;
        _cleanup = cleanup;
        _azureDiscovery = azureDiscovery;
        _azureCliInstaller = azureCliInstaller;
        _failureLog = failureLog;
        _transcriptionModelInstaller = transcriptionModelInstaller;
        _paths = paths;
        _startup = startup;
        _runningTranscription = runningTranscription.Value;
        _previewOverlay = previewOverlay;
        _applySettings = applySettings;
        _reloadVocabulary = reloadVocabulary;
        _libraryVocabulary = libraryVocabulary;
        _libraryVocabulary.Changed += OnLibraryVocabularyChanged;
        _setHotkeyCaptureMode = setHotkeyCaptureMode ?? (_ => { });
        _updates = updates;
        _diagnostics = diagnostics;
        _log = log;

        _settings = settingsRepository.Load();
        _committedSettings = _settings.Clone();
        _wordPackSaveProtocol = new WordPackSaveProtocol(new WordPackSaveStore(
            _libraryStore,
            payload => _settingsRepository.SaveBundle(
                _settings,
                dictionaryEntries: null,
                snippets: null,
                new ExternalIntents(0, 0),
                payload)));
        _savedAiProvider = _settings.AiCleanupProvider;
        _settingsRecovered = settingsRepository.LastLoadFailed;
        _startupToggle = new StartupToggle(
            startup, enabled => StartupPreference.PersistAsync(settingsRepository, enabled), log);
        _pendingBinding = _settings.Hotkey;
        _pendingDictationOnlyBinding = _settings.DictationOnlyHotkey;
        _savedBinding = _settings.Hotkey;
        _savedDictationOnlyBinding = _settings.DictationOnlyHotkey;

        // Match the system light/dark theme + accent colour and enable the Mica backdrop.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccents: false);

        InitializeComponent();
        InitializeNavigation();

        // Keyboard focus in an editable combo box lands on its text box, which WPF-UI leaves unnamed.
        EditableComboBoxName.ShareWithTextBox(AiModelBox);
        EditableComboBoxName.ShareWithTextBox(AzureModelBox);
        EditableComboBoxName.ShareWithTextBox(CopilotModelCombo);

        UsagePeriodBox.ItemsSource = UsagePeriodChoice.All;
        UsagePeriodBox.DisplayMemberPath = nameof(UsagePeriodChoice.Label);
        UsagePeriodBox.SelectedIndex = 1;

        // Type-to-filter behaviour for the model pickers (browse on click, search on type).
        AttachComboFilter(AiModelBox, UpdateAiModelHint);
        AttachComboFilter(AzureModelBox, UpdateAzureDeploymentHint);

        PopulateDevices();
        PopulateChoices();
        LoadFromSettings();
        InitializeDictionaryGrid();
        InitializeLibraryGrid();
        InitializeSnippetList();
        LoadProfiles();
        InitializeReadOnlySections();

        // Everything read from the database now arrives off the UI thread, so a large history or
        // dictionary no longer holds the window back from appearing.
        LoadDictionaryAsync();
        LoadLibrariesAsync();
        LoadSnippetsAsync();
        LoadHistory();
        LoadFailures();
        LoadPerformanceStats();

        // Reflect live cleanup-engine state (download progress, ready, errors) in the UI.
        _cleanup.StatusChanged += OnCleanupStatusChanged;
        Closed += OnClosed;
        Loaded += RefreshStartupStatus;
        Activated += RefreshStartupStatus;

        // The title bar's mouse buttons reach a hotkey capture only as window messages (CaptureNonClientMouseButtons).
        SourceInitialized += (_, _) =>
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(CaptureNonClientMouseButtons);
        RefreshAiStatus();
        InitializeUpdateCard();
        AboutVersionText.Text = $"Version {UpdateService.RunningVersion}";
        // Effective, not configured. These boxes exist so a user can paste the path into File
        // Explorer or a support thread, and Explorer runs outside the package container: showing
        // the path Scribe itself uses is what sent a Store user hunting for a folder Windows had
        // redirected somewhere else. AppPaths probes for the real location at startup.
        AboutLogsPathBox.Text = _paths.EffectiveLogsDir;

        // The path alone is not the truth. If the sink failed to open, that folder holds nothing,
        // and a user (or a support thread) reading a stale file from it is exactly how a shipped
        // build went a week looking like it was logging when it was not.
        if (App.LogSink?.CurrentStatus() is { } logStatus && !logStatus.Healthy)
        {
            AboutLogsPathBox.Text =
                $"{_paths.EffectiveLogsDir}   [NOT LOGGING: {logStatus.Reason}]";
        }
        else if (App.LogSink?.CurrentStatus() is { Path.Length: > 0 } live &&
                 !live.Path.StartsWith(_paths.LogsDir, StringComparison.OrdinalIgnoreCase))
        {
            // Writing somewhere other than the advertised folder, e.g. the temp fallback.
            AboutLogsPathBox.Text = live.Path;
        }
        AboutDatabasePathBox.Text = _paths.EffectiveDatabasePath;
        AboutDataFileHintText.Text = StorageRetentionPolicy.DataFileHint;
        AboutStoreLinkBox.Text = ScribeLinks.StoreWeb;
        if (_paths.IsFallbackRoot)
        {
            AboutDataPathWarning.Text =
                $"Scribe could not use {_paths.PreferredRootDir}. It is currently using this fallback location. {_paths.CreationFailureMessage}";
            AboutDataPathWarning.Visibility = Visibility.Visible;
        }
        else if (_paths.WritesAreVirtualized)
        {
            // Windows is redirecting this packaged build's writes into the package's private store.
            // The paths below are already the redirected ones, but they look nothing like the
            // documented location, so a user comparing them against a README or an older support
            // thread needs to be told which one is real before they conclude the app is broken.
            AboutDataPathWarning.Text =
                "Windows stores this installation's files inside its app package folder rather than " +
                $"at {_paths.RootDir}. The paths below are the real locations. Copy one of them if " +
                "you have been asked for a log file, or use Save diagnostics.";
            AboutDataPathWarning.Visibility = Visibility.Visible;
        }
        else if (_paths.VirtualizedRootDir is { } stranded && Directory.Exists(stranded))
        {
            // Not virtualized now, but an earlier build of this install was, so its logs are still
            // sitting in the package folder. Those are the logs covering whatever the user is most
            // likely reporting, so say where they are rather than leave them hunting for a path
            // that no longer matches what they were told before.
            AboutDataPathWarning.Text =
                "An earlier version of this installation stored its files, including its logs, at " +
                $"{stranded}. Your settings and history have been carried across to the paths below.";
            AboutDataPathWarning.Visibility = Visibility.Visible;
        }
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
                    "Updates are managed outside the app for this package install.";
                UpdateCheckButton.Visibility = Visibility.Collapsed;
                UpdateApplyButton.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateStatusText.Text =
                "Scribe does not look for updates until you ask.";
            UpdateCheckButton.Visibility = Visibility.Visible;
            UpdateApplyButton.Content = "Install update";
            UpdateApplyButton.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateStatusText.Text = _updates?.PendingVersion is { } pending
            ? $"{pending} is downloaded and ready to install."
            : "Scribe does not look for updates until you ask.";
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

    /// <summary>
    /// Adopts the AI cleanup switch from the tray, so this window's next save carries it instead of putting back its
    /// own older value: when the tray asks for the change, and again when it says how the change ended.
    /// <paramref name="revision"/> is the one the change took when it was made
    /// (<see cref="ExternalSwitchSync.NextRevision"/>), so word of a change older than one this window already has,
    /// such as the user's own later click, changes nothing. Takes the value rather than reading the stored settings
    /// again here, on the UI thread. While a hotkey is being recorded the switch shows the change once the recording
    /// ends, and the document, and any save made before then, carry it at once.
    /// </summary>
    public void AdoptExternalAiCleanup(bool enabled, long revision)
    {
        if (!_externalAiCleanup.TryAdopt(enabled, revision, canShowNow: !_capturing, out var showNow))
        {
            return;
        }

        _settings.EnableAiCleanup = enabled;
        if (showNow)
        {
            ShowExternalAiCleanup(enabled);
        }
    }

    /// <summary>
    /// Takes the settings as stored after a tray change the settings lane confirmed as the baseline Try dictation compares
    /// with and describes, since they are what dictation now runs on. The lane never hands back a document older than one
    /// this window saved and applied before (<see cref="SettingsWriteLane"/>), so this can't move the baseline back. The
    /// optimistic word of a tray change (<see cref="AdoptExternalAiCleanup"/>, <see cref="AdoptExternalMicrophone"/>)
    /// changes only the draft, because that change may still fail.
    /// </summary>
    public void AdoptStoredSettings(AppSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        _committedSettings = stored.Clone();
        try
        {
            UpdateTryDictationPage();
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not refresh Try dictation after a tray change was saved.");
        }
    }

    // A hotkey recording has ended, so a tray change that arrived during it can be shown now.
    private void ShowWaitingExternalAiCleanup()
    {
        if (_externalAiCleanup.Release() is { } waiting)
        {
            ShowExternalAiCleanup(waiting);
        }
    }

    // Flagged, so the switch's own handler does not take the tray's value for the user's.
    private void ShowExternalAiCleanup(bool enabled)
    {
        _showingExternalAiCleanup = true;
        try
        {
            AiCleanupCheck.IsChecked = enabled;
        }
        finally
        {
            _showingExternalAiCleanup = false;
        }
    }

    public IReadOnlyList<DictionaryEntry> PersistLearnedDictionaryEntries(IReadOnlyList<DictionaryEntry> entries)
    {
        if (!_dictionaryLoad.IsLoaded)
        {
            // No rows on screen yet, so there are no unsaved edits to protect and storage is the
            // whole truth. The read still in flight may predate these entries, so it is redone.
            var stored = _dictionary.GetAll();
            var fresh = entries
                .Where(entry => !stored.Any(existing =>
                    string.Equals(existing.Pattern.Trim(), entry.Pattern, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.Replacement.Trim(), entry.Replacement, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var added = _dictionary.AddRange(fresh);
            if (added.Count > 0 && _dictionaryLoad.Invalidate())
            {
                LoadDictionaryAsync();
            }

            return added;
        }

        var wasDirty = _dictionaryLoad.HasChanges(DictionarySignature());
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
            _dictionaryLoad.MarkSaved(DictionarySignature());
        }

        return persisted;
    }

    /// <summary>
    /// Persists one entry from the tray's quick-add popup and mirrors it into this open grid.
    ///
    /// This has to exist because <see cref="IDictionaryRepository.SaveAll"/> deletes stored rows
    /// that are missing from the grid: an entry written straight to the repository while this
    /// window is open would be silently destroyed the next time the user pressed Save here. That is
    /// the same hazard <see cref="PersistLearnedDictionaryEntries"/> guards against.
    ///
    /// Identity is the spoken form rather than the id, because the grid can hold a matching row
    /// that has never been saved (id 0). Keying off the id would insert a second row for a spoken
    /// form that already has one, which the save-time duplicate check then rejects.
    /// </summary>
    public DictionaryEntry ApplyQuickDictionaryEntry(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_dictionaryLoad.IsLoaded)
        {
            // Before the rows arrive there is no grid to merge into: write storage by spoken form,
            // then redo the read, which may have started before this write.
            var stored = _dictionary.GetAll().FirstOrDefault(existing =>
                string.Equals(existing.Pattern.Trim(), entry.Pattern, StringComparison.OrdinalIgnoreCase));
            DictionaryEntry written;
            if (stored is null)
            {
                written = _dictionary.Add(entry with { Id = 0 });
            }
            else
            {
                written = entry with { Id = stored.Id };
                _dictionary.Update(written);
            }

            if (_dictionaryLoad.Invalidate())
            {
                LoadDictionaryAsync();
            }

            return written;
        }

        var wasDirty = _dictionaryLoad.HasChanges(DictionarySignature());

        var row = _rows.FirstOrDefault(r =>
            string.Equals(r.Pattern.Trim(), entry.Pattern, StringComparison.OrdinalIgnoreCase));

        DictionaryEntry persisted;
        if (row is null)
        {
            persisted = _dictionary.Add(entry with { Id = 0 });
            _rows.Add(new DictionaryRow
            {
                Id = persisted.Id,
                Pattern = persisted.Pattern,
                Replacement = persisted.Replacement,
                WholeWord = persisted.WholeWord,
                Enabled = persisted.Enabled,
            });
        }
        else
        {
            // An unsaved grid row has no database identity yet, so it is inserted and the row is
            // given the new id. Leaving it at 0 would make the grid's next save insert it a second
            // time, on top of the row the popup just created.
            persisted = row.Id == 0
                ? _dictionary.Add(entry with { Id = 0 })
                : entry with { Id = row.Id };

            if (row.Id != 0)
            {
                _dictionary.Update(persisted);
            }

            row.Id = persisted.Id;
            row.Pattern = persisted.Pattern;
            row.Replacement = persisted.Replacement;
            row.WholeWord = persisted.WholeWord;
            row.Enabled = persisted.Enabled;
        }

        // A quick add must not turn an otherwise untouched window dirty and start prompting the
        // user to save edits they never made.
        if (!wasDirty)
        {
            _dictionaryLoad.MarkSaved(DictionarySignature());
        }

        return persisted;
    }

    /// <summary>
    /// The dictionary as the user currently sees it, including edits that have not been saved yet.
    /// The quick-add popup checks duplicates against this rather than the database so it agrees
    /// with what is on screen. Before the rows have loaded there is nothing unsaved, so storage is
    /// the answer.
    /// </summary>
    public IReadOnlyList<DictionaryEntry> CurrentDictionaryEntries() => !_dictionaryLoad.IsLoaded
        ? _dictionary.GetAll()
        : _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new DictionaryEntry(
                r.Id, r.Pattern.Trim(), r.Replacement.Trim(), r.WholeWord, r.Enabled))
            .ToList();

    internal IReadOnlyList<string> PendingQuickAddSpokenForms()
    {
        if (!_dictionaryLoad.IsLoaded)
        {
            return [];
        }

        return DictionaryDraftDiff.ChangedSpokenForms(_dictionary.GetAll(), CurrentDictionaryEntries()).ToList();
    }

    internal void ShowDictionaryEntry(string spoken)
    {
        ShowPage(SettingsPage.Dictionary, nameof(DictionaryGrid));
        if (!_dictionaryLoad.IsLoaded || string.IsNullOrWhiteSpace(spoken))
        {
            return;
        }

        var row = _rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Pattern.Trim(), spoken.Trim(), StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        DictionaryGrid.SelectedItem = row;
        DictionaryGrid.ScrollIntoView(row);
        DictionaryGrid.Focus();
    }

    internal void AddDictionaryDraft(string spoken)
    {
        ShowPage(SettingsPage.Dictionary, nameof(DictionaryGrid));
        if (string.IsNullOrWhiteSpace(spoken))
        {
            return;
        }

        var existing = _rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Pattern.Trim(), spoken.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            DictionaryGrid.SelectedItem = existing;
            DictionaryGrid.ScrollIntoView(existing);
            return;
        }

        var row = new DictionaryRow { Pattern = spoken.Trim(), WholeWord = true, Enabled = true };
        _rows.Add(row);
        DictionaryGrid.ScrollIntoView(row);
        DictionaryGrid.UpdateLayout();
        DictionaryGrid.SelectedItem = row;
        DictionaryGrid.CurrentCell = new DataGridCellInfo(row, DictionaryGrid.Columns.Count > 1 ? DictionaryGrid.Columns[1] : DictionaryGrid.Columns[0]);
        DictionaryGrid.Focus();
        DictionaryGrid.BeginEdit();
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
            UpdateStatusText.Text = "Dev build. Updates apply to installed builds only.";
            return;
        }

        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";
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
        UpdateStatusText.Text = "Checking Microsoft Store for updates...";
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var available = await _storeUpdates.CheckAsync(hwnd);

            // The Store does not document which version StorePackageUpdate carries, so the message
            // deliberately does not name one rather than risk showing the wrong number.
            UpdateStatusText.Text = available
                ? "An update is available from Microsoft Store."
                : "Scribe is up to date.";
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

        // On success this never returns; the process exits, the update applies, and Scribe
        // relaunches on the new version.
        if (_updates is null || !_updates.ApplyNowAndRestart())
        {
            UpdateStatusText.Text = "Couldn't restart to update. The update will install when you quit Scribe.";
        }
    }

    private async Task ApplyStoreUpdateAsync()
    {
        if (_storeUpdates is null)
        {
            return;
        }

        UpdateApplyButton.IsEnabled = false;
        UpdateStatusText.Text = "Installing the update from Microsoft Store...";
        try
        {
            // Windows shows its own consent and progress dialogs here, and may close Scribe to
            // replace it, so a "Completed" result often never gets rendered.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var outcome = await _storeUpdates.ApplyAsync(hwnd);
            UpdateStatusText.Text = outcome switch
            {
                StoreUpdateOutcome.Completed => "The update is installed. Restart Scribe to use the new version.",
                StoreUpdateOutcome.Canceled => "The update was canceled.",
                StoreUpdateOutcome.NothingToDo => "Scribe is up to date.",
                _ => "Couldn't install the update. Try again from the Microsoft Store app.",
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

    // --- Try dictation ------------------------------------------------------------------------

    internal void ShowPlaygroundPipeline(DictationPipelineReport report)
    {
        if (!IsVisible ||
            SectionTryDictation.Visibility != Visibility.Visible ||
            new WindowInteropHelper(this).Handle != report.TargetWindow)
        {
            return;
        }

        RenderTryDictationReport(report);
    }

    // --- Navigation rail -------------------------------------------------------------------

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionDiagnostics is null)
        {
            return;
        }

        if (CurrentNavigationPage() is { } page)
        {
            ShowPage(page);
        }
    }

    /// <summary>Navigates the rail to the given page, optionally focusing a named control on it.</summary>
    internal void ShowPage(SettingsPage page, string? focusName = null)
    {
        if (!PagePanels.TryGetValue(page, out var selected))
        {
            return;
        }

        foreach (var panel in AllSectionPanels)
        {
            panel.Visibility = ReferenceEquals(panel, selected) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!Equals(NavList.SelectedValue, page))
        {
            NavList.SelectedValue = page;
        }

        selected.BringIntoView();
        if (page == SettingsPage.History)
        {
            LoadHistory();
        }
        else if (page == SettingsPage.Usage)
        {
            LoadUsage();
        }
        else if (page == SettingsPage.TryDictation)
        {
            UpdateTryDictationPage();
        }

        if (!string.IsNullOrWhiteSpace(focusName) && FindName(focusName) is IInputElement target)
        {
            _ = target.Focus();
        }
    }

    /// <summary>Navigates to a section still addressed by older validation code.</summary>
    private void ShowSection(Grid section)
    {
        // Both callers report a problem in a row of Your words, which the Word packs tab would hide.
        if (ReferenceEquals(section, SectionDictionary))
        {
            DictionaryTabs.SelectedItem = YourWordsTab;
        }

        foreach (var pair in PagePanels)
        {
            if (ReferenceEquals(pair.Value, section))
            {
                ShowPage(pair.Key);
                return;
            }
        }

        foreach (var panel in AllSectionPanels)
        {
            panel.Visibility = ReferenceEquals(panel, section) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PopulateDevices()
    {
        try
        {
            _inputDevices = _audio.GetInputDevices();
        }
        catch (Exception ex)
        {
            // Device enumeration can fail transiently; the Windows default choice is always available.
            TryLog(ex, "Could not list the microphones.");
            _inputDevices = [];
        }

        ShowMicrophones(MicrophoneSelection.From(_settings));
    }

    /// <summary>
    /// The devices Windows offers changed while the window is open (the capture service's watcher, posted to this
    /// thread). The list is rebuilt around whatever the picker shows, saved or not, so a choice the user has made and not
    /// saved yet survives; while the list is open the refresh waits for it to close.
    /// </summary>
    public void ShowInputDevices(IReadOnlyList<AudioDevice> devices)
    {
        _inputDevices = devices;
        if (DeviceCombo.IsDropDownOpen)
        {
            _microphonesStale = true;
            return;
        }

        ShowMicrophones(ShownMicrophone);
    }

    /// <summary>
    /// Adopts the microphone chosen from the tray, so this window's next save carries it instead of putting back its own
    /// older choice: when the tray asks for the change, and again when it says how the change ended. As with
    /// <see cref="AdoptExternalAiCleanup"/>, <paramref name="revision"/> is the one the change took when it was made, so
    /// word of a change older than one this window already has, such as the user's own later choice here, changes
    /// nothing. While the picker's list is open it shows the change once the list closes, and a save made before then
    /// carries it at once.
    /// </summary>
    public void AdoptExternalMicrophone(MicrophoneSelection selection, long revision)
    {
        var chosen = MicrophoneSelection.Normalize(selection.DeviceId, selection.DeviceName);
        if (!_externalMicrophone.TryAdopt(chosen, revision, canShowNow: !DeviceCombo.IsDropDownOpen, out var showNow))
        {
            return;
        }

        chosen.ApplyTo(_settings);
        if (showNow)
        {
            ShowMicrophones(chosen);
        }
    }

    // What the picker shows now, which a save writes unless a tray change is still waiting to be shown.
    private MicrophoneSelection ShownMicrophone =>
        DeviceCombo.SelectedItem is MicrophoneChoice choice ? choice.Selection : MicrophoneSelection.From(_settings);

    // Rebuilds the picker around a choice without that counting as the user's: showing the list, or refreshing it, never
    // changes what is chosen, only what is offered and how the Windows default reads.
    private void ShowMicrophones(MicrophoneSelection selection)
    {
        var menu = MicrophoneChoices.Build(_inputDevices, selection);
        _showingMicrophones = true;
        try
        {
            DeviceCombo.ItemsSource = menu.Choices;
            DeviceCombo.SelectedIndex = menu.SelectedIndex;
        }
        finally
        {
            _showingMicrophones = false;
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingMicrophones || _loadingUi)
        {
            return;
        }

        // The user's own choice, newer than any tray change made before it.
        _externalMicrophone.UserChanged();
    }

    // A tray change, or a refresh of the devices, that arrived while the list was open shows now.
    private void DeviceCombo_DropDownClosed(object? sender, EventArgs e)
    {
        if (_externalMicrophone.Release() is { } waiting)
        {
            _microphonesStale = false;
            ShowMicrophones(waiting);
        }
        else if (_microphonesStale)
        {
            _microphonesStale = false;
            ShowMicrophones(ShownMicrophone);
        }
    }

    private void SoundSettings_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink("ms-settings:sound",
            "Could not open the Windows sound settings. Open Windows Settings > System > Sound.");

    private void PopulateChoices()
    {
        ModeCombo.ItemsSource = new[] { "Press and hold", "Press to start and stop" };
        DictationOnlyModeCombo.ItemsSource = new[] { "Press and hold", "Press to start and stop" };
        TranscriptionModelCombo.DisplayMemberPath = nameof(TranscriptionModelChoice.Label);

        InjectionCombo.DisplayMemberPath = nameof(InjectionChoice.Label);
        InjectionCombo.ItemsSource = new[]
        {
            new InjectionChoice(InjectionMethod.UnicodeType, "Type the text (recommended)"),
            new InjectionChoice(InjectionMethod.ClipboardPaste, "Paste the text"),
        };

        NewlineCombo.DisplayMemberPath = nameof(NewlineChoice.Label);
        NewlineCombo.ItemsSource = new[]
        {
            new NewlineChoice(NewlineInjectionMode.SmartFlatten, "Automatic: one line in command windows (recommended)"),
            new NewlineChoice(NewlineInjectionMode.AlwaysFlatten, "Always one line"),
            new NewlineChoice(NewlineInjectionMode.KeepNewlines, "Keep line breaks"),
        };
    }

    private void LoadFromSettings()
    {
        _loadingUi = true;
        try
        {
            HotkeyBox.Text = HotkeyCapture.Describe(_pendingBinding);
            ModeCombo.SelectedIndex = ModeIndex(_pendingBinding.Mode);
            DictationOnlyHotkeyBox.Text = _pendingDictationOnlyBinding is null
                ? string.Empty
                : HotkeyCapture.Describe(_pendingDictationOnlyBinding);
            DictationOnlyModeCombo.SelectedIndex = ModeIndex(_pendingDictationOnlyBinding?.Mode ?? HotkeyMode.Hold);
            DefaultHotkeysHintText.Text = DefaultHotkeyRestore.Caption;
            HideMouseButtonsHint();
            UpdateShortcutRows();
            UpdateFirstRunHint();

            OverlayCheck.IsChecked = _settings.ShowOverlay;
            LoadOverlayPosition(_settings.OverlayPosition);
            AutoStopCheck.IsChecked = _settings.AutoStopOnSilence;
            StoreAudioCheck.IsChecked = _settings.StoreAudioHistory;
            StoreAudioHintText.Text = StorageRetentionPolicy.StoredAudioHint;
            SpaceAfterDictationCheck.IsChecked = _settings.AddSpaceAfterDictation;
            LoadDurationChoices(HistoryRetentionCombo, HistoryRetentionCustomBox, DurationChoiceKind.HistoryRetention, _settings.HistoryRetentionDays);
            HistoryRetentionHintText.Text = StorageRetentionPolicy.TextRetentionHint;
            LoadAdvancedControls(_settings);
            UpdateAdvancedSectionHeaders(_settings);

            LoadAiSettings();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void UpdateAdvancedSectionHeaders(AppSettings source)
    {
        AdvancedSpeechHeader.Text = SectionHeaderText("Speech recognition", AdvancedSection.SpeechRecognition, source);
        AdvancedRecordingHeader.Text = SectionHeaderText("Recording", AdvancedSection.Recording, source);
        AdvancedTypingHeader.Text = SectionHeaderText("Typing into apps", AdvancedSection.TypingIntoApps, source);
        AdvancedTextChangesHeader.Text = SectionHeaderText("Text changes", AdvancedSection.TextChanges, source);
        AdvancedAppearanceHeader.Text = SectionHeaderText("Appearance", AdvancedSection.Appearance, source);
    }

    private static string SectionHeaderText(string title, AdvancedSection section, AppSettings source)
    {
        var suffix = AdvancedDefaults.SectionHeader(section, source);
        return string.IsNullOrEmpty(suffix) ? title : $"{title}  {suffix}";
    }

    private void UpdateFirstRunHint()
    {
        // Until RefreshFirstRunHint has read the count, and after a failed read, the count is unknown and counts as not
        // empty, so the hint stays hidden.
        var state = FirstRunHint.ShouldShow(
            SettingsLoadState.Loaded,
            _firstRunHistoryCount ?? 1,
            dismissed: false,
            ShortcutInstruction(_pendingBinding with { Mode = SelectedMode }));
        FirstRunHintBar.Title = state.Title;
        FirstRunHintBar.Message = state.Message;
        FirstRunHintBar.IsOpen = state.Show;
        FirstRunHintHost.Visibility = state.Show ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ShortcutInstruction(HotkeyBinding binding)
    {
        var shortcut = HotkeyText.SentenceName(binding);
        return binding.Mode == HotkeyMode.Toggle
            ? $"Click in any text box, press {shortcut} and speak. Press it again to type."
            : $"Click in any text box, hold {shortcut} and speak. Let go to type.";
    }

    private void UpdateShortcutRows()
    {
        HotkeyDescriptionText.Text = _settings.EnableAiCleanup
            ? "Types your words with AI cleanup."
            : "Types your words. AI cleanup is off.";
        SetCaveat(HotkeyCaveatText, ShortcutCaveats.For(_pendingBinding));

        var hasSecondShortcut = _pendingDictationOnlyBinding is not null;
        DictationOnlyClearButton.IsEnabled = hasSecondShortcut;
        DictationOnlyModeCombo.IsEnabled = hasSecondShortcut;
        DictationOnlyDescriptionText.Text = hasSecondShortcut
            ? _settings.EnableAiCleanup
                ? "Optional. Types exactly what Scribe hears, with your dictionary and snippets."
                : "AI cleanup is off, so this works like the dictation shortcut."
            : "Set a shortcut first.";
        SetCaveat(DictationOnlyCaveatText, ShortcutCaveats.For(_pendingDictationOnlyBinding));
        UpdateSilenceStop();
    }

    private static void SetCaveat(TextBlock textBlock, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            textBlock.Text = string.Empty;
            textBlock.Margin = new Thickness(0);
            textBlock.Visibility = Visibility.Collapsed;
            return;
        }

        textBlock.Text = text;
        textBlock.Margin = new Thickness(0, 6, 0, 0);
        textBlock.Visibility = Visibility.Visible;
    }

    private void ShowMouseButtonsHint()
    {
        MouseButtonsHintText.Text = HotkeyCaptureSession.MouseButtonsHint;
        MouseButtonsHintText.Margin = new Thickness(0, 16, 0, 0);
        MouseButtonsHintText.Visibility = Visibility.Visible;
    }

    private void HideMouseButtonsHint()
    {
        MouseButtonsHintText.Text = string.Empty;
        MouseButtonsHintText.Margin = new Thickness(0);
        MouseButtonsHintText.Visibility = Visibility.Collapsed;
    }

    private void LoadTranscriptionModelChoices(string? selectedModelId)
    {
        var installed = TranscriptionModelCatalog.Curated
            .Where(model => _transcriptionModelInstaller.IsInstalled(model))
            .Select(model => model.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = TranscriptionModelChoices.Build(selectedModelId, installed);
        TranscriptionModelCombo.ItemsSource = choices;
        TranscriptionModelCombo.SelectedItem = choices.FirstOrDefault(choice => choice.IsSelected) ?? choices[0];
    }

    // Runs on Loaded and on every activation, so coming back from Windows Settings > Apps > Startup
    // shows what the user changed there.
    private async void RefreshStartupStatus(object? sender, EventArgs e)
    {
        if (_closed || !_startupSwitch.TryBeginRefresh(_saveInProgress, out var ticket))
        {
            return;
        }

        // Only the first read disables the switch. A later one, run because the window was just
        // activated, must leave it usable, or the click that activated the window is lost.
        if (_startupSwitch.DisablesSwitchWhileRefreshing)
        {
            LaunchCheck.IsEnabled = false;
        }

        StartupRegistrationStatus status;
        try
        {
            status = await _startup.GetStatusAsync();
        }
        catch (Exception ex)
        {
            // GetStatusAsync reports its own failures as an unknown state. This only keeps a bug from
            // leaving the switch disabled on "Checking..." with nothing to say why.
            TryLog(ex, "Could not refresh the Windows startup setting.");
            status = new StartupRegistrationStatus(null);
        }

        // A flip or a save that started while this read was running shows its own, newer state.
        if (_startupSwitch.CompleteRefresh(ticket, status))
        {
            ShowStartupStatus(status);
        }
    }

    // Renders a state already recorded in _startupSwitch.
    private void ShowStartupStatus(StartupRegistrationStatus status)
    {
        if (_closed)
        {
            return;
        }

        _showingStartupStatus = true;
        try
        {
            LaunchCheck.IsChecked = status.IsEnabled;
        }
        finally
        {
            _showingStartupStatus = false;
        }

        LaunchCheck.IsEnabled = status.CanChange;
        LaunchStatusText.Text = _startup.IsIsolated
            ? $"{status.Message} {StartupRegistration.IsolatedDataNote}"
            : status.Message;
    }

    /// <summary>
    /// Start with Windows applies as soon as the switch is flipped, as Windows' own toggles do.
    /// </summary>
    /// <remarks>
    /// It used to wait for Save, so closing the window, or a Save stopped by a problem on another
    /// page, dropped the change without a word and Windows' startup setting never moved. Checked
    /// and Unchecked are used rather than Click so keyboard and UI Automation toggles apply too;
    /// the switch's own updates from <see cref="ShowStartupStatus"/> are ignored. A flip while a
    /// read of Windows' state is running is allowed; see <see cref="StartupSwitchState"/>.
    /// </remarks>
    private async void LaunchCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_showingStartupStatus || _closed)
        {
            return;
        }

        var requested = LaunchCheck.IsChecked == true;
        var decision = _startupSwitch.TryBeginApply(requested, _saveInProgress, _settingsRecovered);
        if (decision == StartupFlipDecision.Unchanged)
        {
            return;
        }

        if (decision != StartupFlipDecision.Apply)
        {
            // The switch goes back to what Windows last said rather than claim a state nobody
            // applied.
            TryLogRefusedFlip(decision);
            RevertLaunchSwitch();
            if (decision == StartupFlipDecision.RecoveredSettings)
            {
                ShowInfo(
                    SavedSettingsNotice.InSettings("Start with Windows"),
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }

            return;
        }

        var apply = ApplyStartupChangeAsync(requested, _startupSwitch.Shown!);
        _startupApply = apply;
        await apply;
    }

    private void TryLogRefusedFlip(StartupFlipDecision decision)
    {
        try
        {
            _log.LogDebug("Start with Windows change refused: {Reason}.", decision);
        }
        catch
        {
            // Diagnostics must never disrupt the settings window.
        }
    }

    // Touches only the checked state: a refresh or an apply still running owns the rest of the row.
    private void RevertLaunchSwitch()
    {
        _showingStartupStatus = true;
        try
        {
            LaunchCheck.IsChecked = _startupSwitch.Shown?.IsEnabled == true;
        }
        finally
        {
            _showingStartupStatus = false;
        }
    }

    private async Task ApplyStartupChangeAsync(bool requested, StartupRegistrationStatus shown)
    {
        // Disabling a focused control drops keyboard focus, so a keyboard user who pressed Space
        // would otherwise be left with focus somewhere else once the change lands.
        var hadKeyboardFocus = LaunchCheck.IsKeyboardFocusWithin;
        LaunchCheck.IsEnabled = false;
        LaunchStatusText.Text = requested
            ? "Turning on Start with Windows..."
            : "Turning off Start with Windows...";

        StartupToggleResult result;
        try
        {
            result = await _startupToggle.ApplyAsync(requested, shown, _settings.LaunchOnLogin);
        }
        catch (Exception ex)
        {
            // ApplyAsync does not throw by contract; this keeps a bug from stranding the switch.
            TryLog(ex, "Could not apply the Start with Windows change.");
            result = new StartupToggleResult(
                new StartupRegistrationStatus(null), _settings.LaunchOnLogin, StartupToggleOutcome.Unknown);
        }

        // Keep the window's copy in step: the next Save writes this whole object, and a stale value
        // would hand the next launch's reconcile the old preference.
        _settings.LaunchOnLogin = result.Preference;
        _startupSwitch.CompleteApply(result.Status);
        ShowStartupStatus(result.Status);
        if (_closed)
        {
            return;
        }

        // Only when focus is still where WPF put it on disabling, never away from a control the
        // user moved to while the change was applying.
        if (hadKeyboardFocus && LaunchCheck.IsEnabled && IsActive &&
            (Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, this)))
        {
            LaunchCheck.Focus();
        }

        if (result.Outcome == StartupToggleOutcome.SaveFailed)
        {
            ShowInfo(result.Status.Message, Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void StartupSettings_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink("ms-settings:startupapps",
            "Could not open Windows startup settings. Open Windows Settings > Apps > Startup.");

    private void LoadAiSettings()
    {
        AiCleanupCheck.IsChecked = _settings.EnableAiCleanup;

        SetSelectedProviderRadio(_settings.AiCleanupProvider);

        _foundryCuratedByAlias.Clear();
        foreach (var curated in CleanupModelCatalog.Curated)
        {
            _foundryCuratedByAlias[curated.Alias] = curated;
        }
        var savedModelAlias = string.IsNullOrWhiteSpace(_settings.AiCleanupModel)
            ? CleanupModelCatalog.DefaultAlias
            : _settings.AiCleanupModel.Trim();
        SetFoundryModelItems([], savedModelAlias);

        // Manual endpoint/deployment/key are the source of truth Save reads; discovery just autofills
        // them. Populate from saved settings (key is decrypted in memory by AppSettings).
        AzureEndpointBox.Text = _settings.AiCleanupAzureEndpoint ?? string.Empty;
        AzureDeploymentBox.Text = _settings.AiCleanupAzureDeployment ?? string.Empty;
        AzureApiKeyBox.Password = _settings.AiCleanupAzureApiKey ?? string.Empty;
        AzureTenantBox.Text = _settings.AiCleanupAzureTenantId ?? string.Empty;
        SetSelectedAzureAuthRadio(!string.IsNullOrWhiteSpace(_settings.AiCleanupAzureApiKey)
            ? 2
            : _settings.AiCleanupAzureAuthMode == AzureAuthMode.ServicePrincipal ? 1 : 0);
        SpTenantBox.Text = _settings.AiCleanupAzureTenantId ?? string.Empty;
        SpClientIdBox.Text = _settings.AiCleanupAzureClientId ?? string.Empty;
        SpClientSecretBox.Password = _settings.AiCleanupAzureClientSecret ?? string.Empty;
        _azureManualConfiguration = !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureApiKey);

        CustomEndpointBox.Text = _settings.AiCleanupCustomEndpoint ?? string.Empty;
        CustomModelBox.Text = _settings.AiCleanupCustomModel ?? string.Empty;
        CopilotModelCombo.Text = _settings.AiCleanupCopilotModel ?? string.Empty;
        CustomApiKeyBox.Password = _settings.AiCleanupCustomApiKey ?? string.Empty;

        // Reflect the saved deployment in the Model picker before any sign-in discovery runs.
        SeedAzureModelFromSettings();

        // Same idea for the subscription filter: show the saved choice as a stand-in until sign-in
        // discovery replaces the list with everything the account can see.
        SeedAzureSubscriptionsFromSettings();

        // Show the effective writing style: the user's saved guidance, or the default when blank so
        // they can see and edit exactly what gets sent to the model.
        AiWritingStyleBox.Text = CleanupPrompt.ResolveWritingStyle(_settings.AiCleanupWritingStyle);
        UpdateAiWritingStyleSummary();

        // Cleanup prompt: the style selector plus the editable frontier/local guardrail prompts. Each box
        // shows the effective prompt (the user's override, or the built-in default) so it is visible and tunable.
        AiPromptStyleCombo.DisplayMemberPath = nameof(PromptStyleChoice.Label);
        AiPromptStyleCombo.ItemsSource = new[]
        {
            new PromptStyleChoice(CleanupPromptStyle.Auto, "Automatic (recommended)"),
            new PromptStyleChoice(CleanupPromptStyle.Frontier, "Detailed, for cloud and larger models"),
            new PromptStyleChoice(CleanupPromptStyle.Local, "Short, for small models on this PC"),
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
        UpdateAzureProjectApiKeyHint();

        // Best-effort: merge the live on-device catalog + loaded status in without blocking window open.
        // Only a runtime that is already running is read: opening this page must never be what
        // downloads the hardware runtime or a model. "Set up Foundry Local" is the explicit way to start it.
        if (AiCleanupCheck.IsChecked == true && SelectedProvider == CleanupProvider.FoundryLocal)
        {
            _ = RefreshFoundryModelsAsync(initializeRuntime: false);
        }
        else if (RemoteActivityPolicy.MayContact(_committedSettings, CurrentAiDraftSettings(), RemoteActivityTrigger.WindowOpen) &&
                 SelectedProvider == CleanupProvider.AzureFoundry)
        {
            _ = ProbeAzureSignInAsync();
        }
    }

    private void InitializeDictionaryGrid()
    {
        DictionaryGrid.ItemsSource = _rows;
        DataGridCheckBoxClick.Attach(DictionaryGrid);
        DataGridTypingTab.Attach(DictionaryGrid);
        _rows.CollectionChanged += DictionaryRows_CollectionChanged;
        DictionaryGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshDictionaryStatus);
        SetDictionaryEditable(false);
        RefreshDictionaryStatus();
    }

    // Until the rows arrive there is nothing to edit, and an edit on an empty grid would be saved as
    // if the user had deleted every entry.
    private void SetDictionaryEditable(bool editable)
    {
        DictionaryGrid.IsEnabled = editable;
        DictionaryAddButton.IsEnabled = editable;
        DictionarySuggestButton.IsEnabled = editable;
        DictionaryCleanupButton.IsEnabled = editable;
        DictionaryImportButton.IsEnabled = editable;
        DictionaryExportButton.IsEnabled = editable;
    }

    private async void LoadDictionaryAsync()
    {
        if (!_dictionaryLoad.TryBegin(DictionarySignature(), out var ticket))
        {
            return;
        }

        IReadOnlyList<DictionaryEntry> entries;
        try
        {
            entries = await Task.Run(() => _dictionary.GetAll());
        }
        catch (Exception ex)
        {
            if (_dictionaryLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load the dictionary for Settings.");
                RefreshDictionaryStatus();
            }

            return;
        }

        if (!_dictionaryLoad.CanPublish(ticket))
        {
            return;
        }

        // Rows go in before the grid is bound and with the collection handler detached, as the old
        // synchronous load did, so a large dictionary does not queue one full status refresh per row.
        DictionaryGrid.ItemsSource = null;
        _rows.CollectionChanged -= DictionaryRows_CollectionChanged;
        try
        {
            foreach (var stale in _rows)
            {
                stale.PropertyChanged -= DictionaryRow_PropertyChanged;
            }

            _rows.Clear();
            foreach (var entry in entries)
            {
                var row = new DictionaryRow
                {
                    Id = entry.Id,
                    Pattern = entry.Pattern,
                    Replacement = entry.Replacement,
                    WholeWord = entry.WholeWord,
                    Enabled = entry.Enabled,
                };
                row.PropertyChanged += DictionaryRow_PropertyChanged;
                _rows.Add(row);
            }
        }
        finally
        {
            _rows.CollectionChanged += DictionaryRows_CollectionChanged;
            DictionaryGrid.ItemsSource = _rows;
        }

        _dictionaryLoad.Publish(ticket, DictionarySignature());
        SetDictionaryEditable(true);
        RefreshDictionaryStatus();
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
    /// <remarks>Deliberately tolerant: a failure here costs a badge, never an edit.</remarks>
    private void UpdateDictionaryCoverage()
    {
        try
        {
            if (_rows.Count == 0)
            {
                return;
            }

            // Precedence, not the list's A to Z order: the badge must name the word pack dictation uses.
            var covering = DictionaryLibraryOverlapAnalyzer.Coverage(CurrentWordPackLibraries(), CollectEnabledLibraryIds());

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
                    ? $"\"{hit.LibraryName}\" already writes this as \"{theirs}\". Removing this entry " +
                      "changes nothing and frees room in the AI cleanup glossary."
                    : $"\"{hit.LibraryName}\" writes this as \"{theirs}\". Your entry wins. " +
                      "Clear Enabled to fall back to the word pack.";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not compute dictionary library coverage: {Failure}", FailureShape.DescribeWithStack(ex));
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

    // What the dictionary gives AI cleanup, counted by Core the way dictation builds it (GlossaryHint):
    // this page's rows as typed, the libraries of the committed selection, and the provider, prompt style and
    // switches on screen, so every control it reads refreshes it. Local find-and-replace is never capped. This is a
    // status line rather than an input limit, because blocking the 81st entry would break a feature that
    // still works.
    private void UpdateDictionaryGlossaryHint()
    {
        // Also reached from the AI page's handlers, which can run while InitializeComponent is still
        // creating the controls this reads.
        if (DictionaryGlossaryHint is null || AiPromptStyleCombo is null ||
            AiCleanupCheck is null || PostCheck is null)
        {
            return;
        }

        if (!_dictionaryLoad.IsLoaded)
        {
            DictionaryGlossaryHint.Text = _dictionaryLoad.State == SettingsSectionState.Failed
                ? "Couldn't load your dictionary, so it can't be edited right now. Close Settings and open it again to retry."
                : "Loading your dictionary...";
            return;
        }

        // The entries the selected word packs compose to: precedence, not the list's A to Z order, decides which row
        // survives a shared spoken form. The hint counts them as given and never reorders them.
        var libraryEntries = DictionaryLibraryComposer.ComposeLibraries(
            LibraryPrecedence.Enabled(CurrentWordPackLibraries(), CollectEnabledLibraryIds()));

        DictionaryGlossaryHint.Text = GlossaryHint.Describe(new GlossaryHint.Input(
            _rows.Select(r => new DictionaryEntryBuilder.Row(r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled)).ToList(),
            libraryEntries,
            AiCleanupOn: AiCleanupCheck.IsChecked == true,
            PostProcessingOn: PostCheck.IsChecked == true,
            SelectedProvider,
            SelectedPromptStyle));
    }

    private void PostCheck_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateDictionaryGlossaryHint();
        RefreshTextChangesNotice();
    }

    // --- Word packs -----------------------------------------------------------------------

    private void InitializeLibraryGrid()
    {
        LibraryGrid.ItemsSource = _libraryRows;
        DataGridCheckBoxClick.Attach(LibraryGrid);

        // The Word packs tab stages switches in the workspace. Save commits the workspace's change set with the
        // settings document, then marks exactly that change set saved after the library journal publishes it.
        _libraryRows.CollectionChanged += LibraryRows_CollectionChanged;

        _libraryDetailEmptyText = LibraryDetailEmpty.Text;
        LibraryDetailEmpty.Text = "Loading word packs...";
        LibraryTermsGrid.ItemsSource = _libraryTermRows;
        SetLibrariesEditable(false);
        UpdateLibraryDetail(null);
    }

    private void LibraryRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<LibraryRow>() ?? [])
        {
            row.PropertyChanged -= LibraryRow_PropertyChanged;
        }

        foreach (var row in e.NewItems?.OfType<LibraryRow>() ?? [])
        {
            row.PropertyChanged += LibraryRow_PropertyChanged;
        }
    }

    private void LibraryRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingLibraryRows || sender is not LibraryRow row)
        {
            return;
        }

        if (e.PropertyName == nameof(LibraryRow.Enabled))
        {
            _wordPackWorkspace?.SetEnabled(row.Id, row.Enabled);
            Dispatcher.BeginInvoke(RefreshDictionaryStatus);
        }
        else if (e.PropertyName == nameof(LibraryRow.AiCleanup))
        {
            _wordPackWorkspace?.SetAiPermission(row.Id, row.AiCleanup);
        }
    }

    // The enabled set is saved from these rows, so an unloaded list must not be editable: saving it
    // would switch every library off.
    private void SetLibrariesEditable(bool editable)
    {
        LibraryGrid.IsEnabled = editable;
        LibraryImportButton.IsEnabled = editable;
        LibraryExportButton.IsEnabled = editable;
        LibraryRemoveButton.IsEnabled = editable;
    }

    private async void LoadLibrariesAsync()
    {
        if (!_libraryLoad.TryBegin(LibrarySignature(), out var ticket))
        {
            return;
        }

        LibraryCatalog catalog;
        try
        {
            catalog = await Task.Run(_libraryStore.LoadCatalog);
        }
        catch (Exception ex)
        {
            if (_libraryLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load dictionary libraries for Settings.");
                LibraryDetailEmpty.Text =
                    "Couldn't load word packs.";
            }

            return;
        }

        if (!_libraryLoad.CanPublish(ticket))
        {
            return;
        }

        _wordPackCatalog = catalog;
        _wordPackWorkspace = new LibraryWorkspace(catalog, BuiltInLibraryOverlay.Instance, LibraryDecisions.DefaultAiPermission);

        // Clear raises a reset that names no removed rows, so their handlers are dropped here.
        foreach (var stale in _libraryRows)
        {
            stale.PropertyChanged -= LibraryRow_PropertyChanged;
        }

        // One A to Z list of built-in and custom libraries, each shown as the committed selection has it. The ordering is
        // captured now, so a library imported later is placed by the same rules as the rows already shown.
        _libraryOrdering = LibraryOrdering.ForCurrentCulture();
        _updatingLibraryRows = true;
        _libraryRows.Clear();
        foreach (var library in _libraryOrdering.Sort(catalog.Libraries.Select(l => l.Content), l => l.Name, l => l.Id))
        {
            _libraryRows.Add(NewLibraryRow(library));
        }
        _updatingLibraryRows = false;

        _libraryLoad.Publish(ticket, LibrarySignature());
        LibraryDetailEmpty.Text = _libraryDetailEmptyText;
        SetLibrariesEditable(true);

        // Preview the first library so the detail panel is never blank when the page opens.
        if (_libraryRows.Count > 0)
        {
            LibraryGrid.SelectedIndex = 0;
        }
        else
        {
            UpdateLibraryDetail(null);
        }

        // Coverage badges and the glossary count depend on which libraries are on.
        RefreshDictionaryStatus();
    }

    private void LibraryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _librarySearchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _librarySearchTimer.Stop();
        _librarySearchTimer.Tick -= LibrarySearchTimer_Tick;
        _librarySearchTimer.Tick += LibrarySearchTimer_Tick;
        _librarySearchTimer.Start();
    }

    private void LibrarySearchTimer_Tick(object? sender, EventArgs e)
    {
        _librarySearchTimer?.Stop();
        ApplyLibrarySearch();
    }

    private void ApplyLibrarySearch()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var sources = _wordPackWorkspace.Draft.Libraries
            .Where(library => !library.PendingDelete)
            .Select(library => new LibrarySearchSource(library.Content.Id, _wordPackWorkspace.RowsOf(library.Content.Id)))
            .ToList();
        _librarySearchResult = _librarySearch.Search(sources, LibrarySearchBox.Text);
        foreach (var row in _libraryRows)
        {
            ApplySearchState(row);
        }

        if (_selectedLibraryId is not null)
        {
            RefreshTermRows(_selectedLibraryId);
        }
    }

    private void ApplySearchState(LibraryRow row)
    {
        row.MatchCount = _librarySearchResult?.IsActive == true ? _librarySearchResult.CountIn(row.Id) : null;
    }

    private void LibraryNewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var id = _wordPackWorkspace.CreateLibrary();
        RefreshWordPackList(id);
        RefreshDictionaryStatus();
    }

    private void LibraryMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var menu = new ContextMenu();
        var duplicate = new MenuItem { Header = "Duplicate" };
        duplicate.IsEnabled = LibraryGrid.SelectedItem is LibraryRow;
        duplicate.Click += (_, _) =>
        {
            if (LibraryGrid.SelectedItem is LibraryRow row)
            {
                var id = _wordPackWorkspace.Duplicate(row.Id);
                RefreshWordPackList(id);
            }
        };
        menu.Items.Add(duplicate);

        var deleted = new MenuItem { Header = $"Recently deleted ({_wordPackWorkspace.Draft.RecentlyDeleted.Count})" };
        deleted.Click += (_, _) => ShowRecentlyDeletedDialog();
        menu.Items.Add(deleted);

        menu.PlacementTarget = LibraryMoreButton;
        menu.IsOpen = true;
    }

    private void LibraryTermDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null ||
            sender is not FrameworkElement { DataContext: LibraryTermRow row })
        {
            return;
        }

        if (!_wordPackWorkspace.CanEditContent(_selectedLibraryId))
        {
            ShowInfo("This word pack can't be edited right now.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        try
        {
            _wordPackWorkspace.DeleteTerm(_selectedLibraryId, row.RowId);
            RefreshTermRows(_selectedLibraryId);
            RefreshWordPackList(_selectedLibraryId);
            RefreshDictionaryStatus();
        }
        catch (InvalidOperationException)
        {
            ShowInfo("This term can be turned off or restored, but not deleted.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void AddWordPackTerm()
    {
        if (_selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        if (!_wordPackWorkspace.CanEditContent(_selectedLibraryId))
        {
            ShowInfo("This word pack can't be edited right now.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        var result = _wordPackWorkspace.AddTerm(_selectedLibraryId, new TermValues(string.Empty, string.Empty), removalIntent: false);
        if (!result.Applied)
        {
            ShowInfo(result.Issue is null ? "Couldn't add a term." : LibraryEditor.Message(result.Issue), Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        RefreshTermRows(_selectedLibraryId);
        RefreshWordPackList(_selectedLibraryId);
        if (_libraryTermRows.LastOrDefault() is { } row)
        {
            LibraryTermsGrid.SelectedItem = row;
            LibraryTermsGrid.ScrollIntoView(row);
        }
    }

    private void ShowRecentlyDeletedDialog()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var list = new ListBox
        {
            MinWidth = 420,
            MinHeight = 180,
            ItemsSource = _wordPackWorkspace.Draft.RecentlyDeleted.Select(entry =>
                new RecentlyDeletedRow(entry)).ToList(),
            DisplayMemberPath = nameof(RecentlyDeletedRow.Display),
        };
        var restore = new Button { Content = "Restore word pack", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var purge = new Button { Content = "Delete permanently", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90 };
        var dialog = new Window
        {
            Title = $"Recently deleted ({_wordPackWorkspace.Draft.RecentlyDeleted.Count})",
            Owner = this,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        restore.Click += async (_, _) =>
        {
            if (list.SelectedItem is RecentlyDeletedRow row)
            {
                var content = await Task.Run(() => _libraryStore.ReadRecentlyDeleted(row.Entry));
                if (content is not null)
                {
                    var id = _wordPackWorkspace.RestoreDeleted(content);
                    RefreshWordPackList(id);
                    dialog.DialogResult = true;
                }
            }
        };
        purge.Click += (_, _) =>
        {
            if (list.SelectedItem is RecentlyDeletedRow row)
            {
                _wordPackWorkspace.DeletePermanently(row.Entry);
                dialog.DialogResult = true;
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { restore, purge, close },
                },
            },
        };
        dialog.ShowDialog();
        RefreshWordPackList();
        RefreshDictionaryStatus();
    }

    // Drives the right-hand preview panel from the selected library row: header plus a read-only grid
    // of its spoken-to-written terms. Resolving from the cached snapshot keeps clicking through
    // libraries instant (no per-click file reads).
    private void UpdateLibraryDetail(LibraryRow? row)
    {
        var library = row is null || _wordPackWorkspace is null
            ? null
            : _wordPackWorkspace.Draft.Find(row.Id);

        if (library is null)
        {
            _selectedLibraryId = null;
            _libraryTermRows.Clear();
            LibraryTermsGrid.Visibility = Visibility.Collapsed;
            LibraryDetailEmpty.Visibility = Visibility.Visible;
            LibraryDetailName.Text = string.Empty;
            LibraryDetailMeta.Text = string.Empty;
            LibraryDetailDesc.Text = string.Empty;
            LibraryDetailDesc.Visibility = Visibility.Collapsed;
            LibraryExportButton.IsEnabled = false;
            LibraryRemoveButton.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedLibraryId = library.Content.Id;
        var content = library.Content;
        var count = content.Rows.Count;
        LibraryDetailName.Text = content.Name;
        LibraryDetailMeta.Text =
            $"{LibrarySource(content.BuiltIn)}, {content.Category}, {count} {(count == 1 ? "word" : "words")}";
        LibraryDetailDesc.Text = content.Description ?? string.Empty;
        LibraryDetailDesc.Visibility =
            string.IsNullOrWhiteSpace(content.Description) ? Visibility.Collapsed : Visibility.Visible;

        RefreshTermRows(library.Content.Id);
        LibraryTermsGrid.Visibility = Visibility.Visible;
        LibraryDetailEmpty.Visibility = Visibility.Collapsed;
        LibraryExportButton.IsEnabled = true;
        LibraryRemoveButton.Visibility = content.BuiltIn ? Visibility.Collapsed : Visibility.Visible;
    }

    // The enabled-set persisted in settings: the ids of every ticked library still in the list, in precedence order, so
    // what Save writes never depends on the order the rows are shown in (after a fresh load it is the list 0.4.3 wrote).
    // The rows are read-only in this build, and once the library state is stored the save keeps the stored list anyway.
    private List<string> CollectEnabledLibraryIds() =>
        _wordPackWorkspace?.Draft.LocalState.EnabledIds.ToList()
        ?? LibraryPrecedence.Order(_libraryRows.Where(r => r.Enabled), r => r.Id, r => r.BuiltIn).Select(r => r.Id).ToList();

    // Where a word pack comes from, shown under its name in the list and in the preview's meta line.
    private static string LibrarySource(bool builtIn) => builtIn ? "Built-in" : "Imported";

    private IReadOnlyList<DictionaryLibrary> CurrentWordPackLibraries()
    {
        if (_wordPackWorkspace is null)
        {
            return _libraryRows
                .Select(row => new DictionaryLibrary(
                    row.Id,
                    row.Name,
                    row.Category,
                    null,
                    row.BuiltIn,
                    []))
                .ToList();
        }

        return _wordPackWorkspace.Draft.Libraries
            .Where(library => !library.PendingDelete)
            .Select(library => new DictionaryLibrary(
                library.Content.Id,
                library.Content.Name,
                library.Content.Category,
                library.Content.Description,
                library.Content.BuiltIn,
                library.Content.Rows.Select(row => row.Values.ToEntry()).ToList())
            {
                FileName = library.FileName,
            })
            .ToList();
    }

    private void RefreshTermRows(string libraryId)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        foreach (var row in _libraryTermRows)
        {
            row.PropertyChanged -= LibraryTermRow_PropertyChanged;
        }

        var rows = _wordPackWorkspace.RowsOf(libraryId);
        if (_librarySearchResult?.IsActive == true)
        {
            var matches = _librarySearchResult.MatchesIn(libraryId).ToHashSet();
            rows = rows.Where(row => matches.Contains(row.RowId)).ToList();
        }

        rows = _libraryTermSort.Sort(rows, _libraryTermSortOrder);
        _updatingLibraryTerms = true;
        _libraryTermRows.Clear();
        foreach (var row in rows)
        {
            var view = new LibraryTermRow(row.RowId, row.Row.Values);
            view.PropertyChanged += LibraryTermRow_PropertyChanged;
            _libraryTermRows.Add(view);
        }

        _updatingLibraryTerms = false;
        LibraryDetailEmpty.Visibility = _libraryTermRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryDetailEmpty.Text = SearchNoMatchesText(libraryId);
    }

    private string SearchNoMatchesText(string libraryId)
    {
        if (_librarySearchResult?.IsActive != true || _librarySearchResult.CountIn(libraryId) > 0 || _wordPackWorkspace is null)
        {
            return _libraryDetailEmptyText;
        }

        var selected = _wordPackWorkspace.Draft.Find(libraryId)?.Content.Name ?? "this word pack";
        var elsewhere = _librarySearchResult.FoundElsewhere(libraryId)
            .Select(match => _wordPackWorkspace.Draft.Find(match.LibraryId) is { } pack
                ? $"{pack.Content.Name} ({match.Count})"
                : null)
            .Where(text => text is not null)
            .ToList();
        return elsewhere.Count == 0
            ? $"No matches in {selected}."
            : $"No matches in {selected}. Found in: {string.Join(", ", elsewhere)}";
    }

    private void LibraryTermRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingLibraryTerms || sender is not LibraryTermRow row || _selectedLibraryId is null || _wordPackWorkspace is null)
        {
            return;
        }

        if (!_wordPackWorkspace.CanEditContent(_selectedLibraryId))
        {
            ShowInfo("This word pack can't be edited right now.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        var result = _wordPackWorkspace.EditTerm(
            _selectedLibraryId,
            row.RowId,
            new TermValues(row.Pattern, row.Replacement, row.WholeWord, row.Enabled),
            removalIntent: false);
        if (!result.Applied && result.Issue is { } issue)
        {
            ShowInfo(LibraryEditor.Message(issue, row.Pattern), Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }

        RefreshWordPackList(_selectedLibraryId);
        RefreshDictionaryStatus();
    }

    private void LibraryTermsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var header = e.Column.Header?.ToString();
        _libraryTermSortOrder = header switch
        {
            "Scribe hears" => _libraryTermSortOrder == LibraryTermSortOrder.SpokenAscending
                ? LibraryTermSortOrder.SpokenDescending
                : LibraryTermSortOrder.SpokenAscending,
            "Scribe writes" => _libraryTermSortOrder == LibraryTermSortOrder.WrittenAscending
                ? LibraryTermSortOrder.WrittenDescending
                : LibraryTermSortOrder.WrittenAscending,
            _ => LibraryTermSortOrder.SavedOrder,
        };
        if (_selectedLibraryId is not null)
        {
            RefreshTermRows(_selectedLibraryId);
        }
    }

    private LibraryRow NewLibraryRow(LibraryContent library) => new()
    {
        Id = library.Id,
        Name = library.Name,
        Category = library.Category,
        Terms = library.Rows.Count,
        Source = LibrarySource(library.BuiltIn),
        BuiltIn = library.BuiltIn,
        Enabled = _wordPackWorkspace?.Draft.LocalState.EnabledIds.Contains(library.Id) == true,
        AiCleanup = _wordPackWorkspace?.ShowsAiPermission(library.Id) == true,
    };

    private void RefreshWordPackList(string? selectId = null)
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        selectId ??= (LibraryGrid.SelectedItem as LibraryRow)?.Id;
        foreach (var row in _libraryRows)
        {
            row.PropertyChanged -= LibraryRow_PropertyChanged;
        }

        _updatingLibraryRows = true;
        _libraryRows.Clear();
        var ordering = _libraryOrdering ??= LibraryOrdering.ForCurrentCulture();
        foreach (var library in ordering.Sort(
                     _wordPackWorkspace.Draft.Libraries.Where(l => !l.PendingDelete).Select(l => l.Content),
                     l => l.Name,
                     l => l.Id))
        {
            var row = NewLibraryRow(library);
            ApplySearchState(row);
            _libraryRows.Add(row);
        }

        _updatingLibraryRows = false;
        if (selectId is not null)
        {
            LibraryGrid.SelectedItem = _libraryRows.FirstOrDefault(row => string.Equals(row.Id, selectId, StringComparison.OrdinalIgnoreCase));
        }
    }

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

        try
        {
            if (_wordPackWorkspace is null)
            {
                return;
            }

            var bytes = File.ReadAllBytes(dialog.FileName);
            var document = LibraryCsvCodec.Instance.ReadImport(bytes);
            var plan = LibraryImportPlanner.Plan(
                document,
                new LibraryImportTarget.NewLibrary(Path.GetFileName(dialog.FileName)),
                _wordPackWorkspace.Draft);
            if (ShowImportWordPackDialog(plan) is not { } accepted)
            {
                return;
            }

            var before = _wordPackWorkspace.Draft.Libraries.Select(l => l.Content.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = _wordPackWorkspace.ApplyImport(accepted, ImportConflictChoice.KeepMine);
            if (!result.Applied)
            {
                ShowThemedMessage("Couldn't import the word pack", result.Issue is null
                    ? "Couldn't import the word pack. Your edits are still here."
                    : LibraryEditor.Message(result.Issue));
                return;
            }

            var importedId = _wordPackWorkspace.Draft.Libraries.FirstOrDefault(l => !before.Contains(l.Content.Id))?.Content.Id;
            RefreshWordPackList(importedId);
            RefreshDictionaryStatus();
            ShowInfo("Imported the word pack. Save to apply it.");
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not import a word pack from Settings.");
            ShowThemedMessage("Couldn't import the word pack", "Couldn't import the word pack. Check the file and try again.");
            return;
        }
    }

    private LibraryImportPlan? ShowImportWordPackDialog(LibraryImportPlan plan)
    {
        var nameBox = new TextBox { Text = plan.SuggestedName, MinWidth = 320, Margin = new Thickness(0, 8, 0, 8) };
        var summary = new TextBlock
        {
            Text = $"{plan.Adds:N0} added, {plan.WrittenDifferently:N0} written differently, {plan.AlreadyHere:N0} already here, {plan.Skipped:N0} couldn't be read.",
            TextWrapping = TextWrapping.Wrap,
        };
        var errors = new TextBlock
        {
            Text = plan.SkippedRows.Count == 0
                ? "No row errors."
                : string.Join(Environment.NewLine, plan.SkippedRows.Take(6).Select(row => $"Row {row.Line}: {row.Kind}")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Replacement name" });
        panel.Children.Add(nameBox);
        panel.Children.Add(summary);
        panel.Children.Add(errors);
        var dialog = new Window
        {
            Title = "Import a word pack",
            Owner = this,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    panel,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children =
                        {
                            new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) },
                            new Button { Content = $"Import {plan.Operations.Count:N0} valid terms", IsDefault = true, MinWidth = 150 },
                        },
                    },
                },
            },
        };
        ((Button)((StackPanel)((StackPanel)dialog.Content).Children[1]).Children[1]).Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? plan with { SuggestedName = nameBox.Text } : null;
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
            Title = "Export word pack",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = _wordPackWorkspace?.Draft.Find(library.Id)?.Content;
            if (content is null)
            {
                return;
            }

            File.WriteAllBytes(dialog.FileName, LibraryCsvCodec.Instance.WriteExport(content));
            ShowInfo("Exported the word pack.");
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not export a word pack from Settings.");
            ShowThemedMessage("Couldn't export the word pack", "Couldn't export the word pack. Choose another location or try again.");
        }
    }

    private async void LibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            ShowInfo("Select a word pack to remove.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (row.BuiltIn)
        {
            ShowThemedMessage("Built-in word pack", "Built-in word packs can be turned off, but not removed.");
            return;
        }

        if (!await ConfirmRiskyAsync(
                "Delete word pack",
                $"Delete \"{row.Name}\" and its {row.Terms:N0} {(row.Terms == 1 ? "word" : "words")}? You can restore it from Recently deleted for 30 days. Your dictionary is unchanged.",
                "Delete word pack"))
        {
            return;
        }

        try
        {
            _wordPackWorkspace?.DeleteLibrary(row.Id);
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not stage a word pack deletion from Settings.");
            ShowThemedMessage("Couldn't delete the word pack", "Couldn't delete the word pack. Try again.");
            return;
        }

        RefreshWordPackList();
        UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow);

        RefreshDictionaryStatus();
        ShowInfo("Deleted the word pack. Save to apply it.");
    }

    // Resolves the grid's selected row back to its loaded library from the cached snapshot,
    // surfacing a friendly hint when nothing is selected or the file has since gone missing.
    private DictionaryLibrary? SelectedLibrary()
    {
        if (LibraryGrid.SelectedItem is not LibraryRow row)
        {
            ShowInfo("Select a word pack first.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return null;
        }

        var draft = _wordPackWorkspace?.Draft.Find(row.Id);
        if (draft is null)
        {
            ShowInfo("That word pack is no longer available.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return null;
        }

        return new DictionaryLibrary(
            draft.Content.Id,
            draft.Content.Name,
            draft.Content.Category,
            draft.Content.Description,
            draft.Content.BuiltIn,
            draft.Content.Rows.Select(row => row.Values.ToEntry()).ToList())
        {
            FileName = draft.FileName,
        };
    }

    // Save skips sections the user never touched, so a pre-existing data problem in one section
    // (e.g. a duplicate dictionary entry loaded from disk) can never block saving a change made in
    // another. The signatures capture everything the section's SaveAll would write; the matching
    // snapshots live in the section's SettingsSectionLoad. Every value is framed (DraftSnapshot) and the
    // rows are counted, so no text typed or pasted into a row can make changed rows sign like the saved
    // ones: joined with a '|', a phrase "a" with the template "b|c" signed like "a|b" with "c", and a
    // Save skipped the section, or closed over it while waiting for its vocabulary.
    private string DictionarySignature() => new Scribe.Core.Vocabulary.DraftSnapshot()
        .DictionaryRows([.. _rows.Select(r => new DictionaryEntryBuilder.Row(r.Id, r.Pattern, r.Replacement, r.WholeWord, r.Enabled))])
        .Hash();

    private string SnippetSignature() => new Scribe.Core.Vocabulary.DraftSnapshot()
        .SnippetRows([.. _snippetRows.Select(r => new SnippetBuilder.Row(r.Id, r.Phrase, r.Template, r.Enabled))])
        .Hash();

    /// <summary>
    /// Which libraries are listed, and which are shown on. The section's load tracks it, and the cleanup
    /// treats a change while its scan runs (an import or a removal, in this build) as a reason to scan again.
    /// </summary>
    private string LibrarySignature() => new Scribe.Core.Vocabulary.DraftSnapshot()
        .LibraryRows([.. _libraryRows.Select(r => (r.Id, r.Enabled))])
        .Hash();

    /// <summary>Set once the window has closed, so async continuations know not to touch its controls.</summary>
    private bool _closed;

    private void ShowSystemCapability(ComputeCapabilityReport? report)
    {
        try
        {
            if (report is null)
            {
                SystemCapabilityText.Text = "This PC details aren't available.";
                return;
            }

            SystemCapabilityText.Text = report.Describe();

            if (report.Recommendation is { } advice)
            {
                SystemCapabilityAdviceText.Text = advice;
                SystemCapabilityAdviceText.Visibility = Visibility.Visible;
            }
            else
            {
                SystemCapabilityAdviceText.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            // Hardware detection is descriptive only; never let it break the diagnostics page.
            TryLog(ex, "Compute capability detection failed.");
            SystemCapabilityText.Text = "This PC details aren't available.";
        }
    }

    private async void LoadPerformanceStats()
    {
        if (!_statsLoad.TryBegin(null, out var ticket))
        {
            return;
        }

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        ComputeCapabilityReport? capability;
        Scribe.Core.Diagnostics.DictationStats.Snapshot? stats;
        try
        {
            (capability, stats) = await Task.Run(() => ReadPerformanceData(since));
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not read diagnostics for Settings.");
            (capability, stats) = (null, null);
        }

        if (!_statsLoad.Publish(ticket, string.Empty))
        {
            return;
        }

        ShowSystemCapability(capability);
        ShowPerformanceStats(stats);
    }

    // Runs on a worker thread, so it touches no controls. Hardware detection is descriptive only and
    // the stats are a nicety, so neither failure may take the Diagnostics page down.
    private (ComputeCapabilityReport? Capability, Scribe.Core.Diagnostics.DictationStats.Snapshot? Stats)
        ReadPerformanceData(DateTimeOffset since)
    {
        ComputeCapabilityReport? capability = null;
        try
        {
            capability = ComputeCapabilityReport.Detect();
        }
        catch (Exception ex)
        {
            TryLog(ex, "Compute capability detection failed.");
        }

        Scribe.Core.Diagnostics.DictationStats.Snapshot? stats = null;
        try
        {
            stats = Scribe.Core.Diagnostics.DictationStats.Compute(_history.GetRecent(1000), since);
        }
        catch (Exception ex)
        {
            TryLog(ex, "Performance stats unavailable.");
        }

        return (capability, stats);
    }

    private void ShowPerformanceStats(Scribe.Core.Diagnostics.DictationStats.Snapshot? stats)
    {
        if (stats is null)
        {
            StatsSummaryText.Text = _statsSummaryEmptyText; // the friendly empty-state text
            return;
        }

        try
        {
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
                SpeedDetailsExpander.IsExpanded = false;
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
                SpeedDetailsExpander.IsExpanded = false;
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

    // Grids and hints for the sections that only display stored data. Their rows arrive later.
    private void InitializeReadOnlySections()
    {
        HistoryGrid.ItemsSource = _historyRows;
        _historyEmptyText = HistoryEmptyHint.Text;
        HistoryEmptyHint.Text = "Loading history...";
        HistoryEmptyHint.Visibility = Visibility.Visible;
        HistoryClearButton.IsEnabled = false;

        FailuresGrid.ItemsSource = _failures;
        _noFailuresText = NoFailuresText.Text;
        NoFailuresText.Text = "Loading...";
        NoFailuresText.Visibility = Visibility.Visible;
        ClearFailuresButton.IsEnabled = false;

        _statsSummaryEmptyText = StatsSummaryText.Text;
        StatsSummaryText.Text = "Reading your statistics...";
    }

    private async void LoadFailures()
    {
        if (!_failureLoad.TryBegin(null, out var ticket))
        {
            return;
        }

        IReadOnlyList<CleanupFailure> failures;
        try
        {
            failures = await Task.Run(() => _failureLog.GetRecent(10_000));
        }
        catch (Exception ex)
        {
            if (_failureLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load the AI cleanup failure log for Settings.");
                NoFailuresText.Text = "Couldn't load the list.";
                NoFailuresText.Visibility = Visibility.Visible;
                ClearFailuresButton.IsEnabled = true;
            }

            return;
        }

        if (!_failureLoad.Publish(ticket, string.Empty))
        {
            return;
        }

        _failures.Clear();
        foreach (var failure in failures.Take(20))
        {
            _failures.Add(new FailureRow
            {
                When = failure.TimestampUtc.ToLocalTime().ToString("g"),
                Model = (string.IsNullOrWhiteSpace(failure.Model) ? failure.Provider : failure.Model)
                        ?? string.Empty,
                Reason = failure.Reason,
            });
        }

        NoFailuresText.Text = _noFailuresText;
        NoFailuresText.Visibility = _failures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FailuresCountText.Text = failures.Count > _failures.Count
            ? $"Showing the 20 most recent of {failures.Count:N0} failures."
            : string.Empty;
        ClearFailuresButton.IsEnabled = true;
    }

    private async void ClearFailuresButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmRiskyAsync(
                "Clear the list of AI cleanup problems?",
                "This doesn't change your settings.",
                "Clear list"))
        {
            return;
        }

        ClearFailuresButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _failureLog.Clear());
            if (_closed)
            {
                return;
            }

            _failures.Clear();
            FailuresCountText.Text = string.Empty;
            NoFailuresText.Text = _noFailuresText;
            NoFailuresText.Visibility = Visibility.Visible;

            // A read that started before the clear would bring the old rows back.
            LoadFailures();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                TryLog(ex, "Could not clear the AI cleanup failure log.");
                ShowInfo("Couldn't clear the list. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
                ClearFailuresButton.IsEnabled = true;
            }
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
        UpdateShortcutRows();
    }

    // Stages the shipped hotkeys like any other edit on this page: nothing is stored until Save, and Cancel discards
    // it. No confirmation, because it deletes nothing and both rows show the result at once, where Set changes either
    // key back. The mode boxes are set too, since Save reads each binding's mode from its box. The saved bindings
    // decide whether the notice asks for a Save.
    private void RestoreHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            CancelCapture();
        }

        var restored = DefaultHotkeyRestore.Restore(
            _pendingBinding with { Mode = SelectedMode },
            _pendingDictationOnlyBinding is null ? null : _pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode },
            _savedBinding,
            _savedDictationOnlyBinding);
        _pendingBinding = restored.Dictation;
        _pendingDictationOnlyBinding = restored.DictationOnly;
        HotkeyBox.Text = HotkeyCapture.Describe(restored.Dictation);
        ModeCombo.SelectedIndex = ModeIndex(restored.Dictation.Mode);
        DictationOnlyHotkeyBox.Text = HotkeyCapture.Describe(restored.DictationOnly);
        DictationOnlyModeCombo.SelectedIndex = ModeIndex(restored.DictationOnly.Mode);
        UpdateShortcutRows();
        UpdateFirstRunHint();

        // The default severity, as for the other "done, now Save" notices here: the bar floats over the page title, and
        // WPF-UI fills the informational one almost transparently (#08FFFFFF in the dark theme), so the title would
        // show through the message.
        ShowInfo(restored.Message);
        AnnounceFrom(RestoreHotkeysButton, restored.Message);
    }

    // The notification bar is not read out when it opens, so the outcome is also raised as a UI Automation
    // notification from the control that caused it. A notification does not depend on keyboard focus, which matters
    // here: a key capture in progress is cancelled first, and that clears focus.
    private void AnnounceFrom(UIElement source, string message)
    {
        try
        {
            var peer = UIElementAutomationPeer.FromElement(source) ?? UIElementAutomationPeer.CreatePeerForElement(source);
            peer?.RaiseNotificationEvent(
                System.Windows.Automation.AutomationNotificationKind.ActionCompleted,
                System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "Scribe.SettingsNotice");
        }
        catch (Exception ex)
        {
            // The same words are on screen; a screen reader that cannot be told must not break the page.
            TryLog(ex, "Could not announce a settings notice to assistive technology.");
        }
    }

    private static int ModeIndex(HotkeyMode mode) => mode == HotkeyMode.Toggle ? 1 : 0;

    private void BeginCapture(bool dictationOnly)
    {
        _capturingDictationOnly = dictationOnly;
        _capturing = true;
        _finalized = false;
        _capture = HotkeyCapture.NewSession();
        ShowMouseButtonsHint();

        // Put the global hook into pass-through first: the current push-to-talk key must reach
        // this capture box as an ordinary key instead of being suppressed or starting a recording.
        _setHotkeyCaptureMode(true);
        ActiveHotkeyBox.Text = HotkeyCaptureSession.Prompt;
    }

    // Keys reach the capture wherever focus is in the window, as they always have: these are the window's Preview events.
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing || _capture is null)
        {
            return;
        }

        e.Handled = true;
        ApplyCaptureStep(_capture.Press(HotkeyCapture.VirtualKeyOf(e)));
    }

    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing || _finalized || _capture is null)
        {
            return;
        }

        e.Handled = true;
        ApplyCaptureStep(_capture.Release(HotkeyCapture.VirtualKeyOf(e), ActiveSelectedMode));
    }

    // Mouse buttons reach the capture with the pointer anywhere on this window: these are the window's Preview events, so
    // a press is seen before any control under the pointer, and the title bar's are taken from its window messages
    // (CaptureNonClientMouseButtons). A recorded button's events are marked handled, so no control acts on them and a
    // side button's release never becomes a Back or Forward command. The left and right buttons keep their meaning: a
    // click on Cancel, or anywhere else, still works; one on the capture box itself says why it can't be a hotkey.
    private void HotkeyCapture_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing || _capture is null)
        {
            return;
        }

        var step = _capture.Press(HotkeyCapture.VirtualKeyOf(e.ChangedButton));
        if (step.Handled)
        {
            e.Handled = true;
        }

        if (step.Outcome == HotkeyCaptureOutcome.Refused && !IsWithin(ActiveHotkeyBox, e.OriginalSource))
        {
            return;
        }

        ApplyCaptureStep(step);
    }

    private void HotkeyCapture_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing || _finalized || _capture is null)
        {
            return;
        }

        var step = _capture.Release(HotkeyCapture.VirtualKeyOf(e.ChangedButton), ActiveSelectedMode);
        if (step.Handled)
        {
            e.Handled = true;
        }

        ApplyCaptureStep(step);
    }

    private static bool IsWithin(DependencyObject container, object? source) =>
        source is DependencyObject element &&
        (ReferenceEquals(element, container) || (element is Visual visual && container is Visual root && root.IsAncestorOf(visual)));

    private void ApplyCaptureStep(HotkeyCaptureStep step)
    {
        switch (step.Outcome)
        {
            case HotkeyCaptureOutcome.Cancelled:
                CancelCapture();
                break;
            case HotkeyCaptureOutcome.Refused:
            case HotkeyCaptureOutcome.TooMany:
                ShowInfo(step.Message!, Wpf.Ui.Controls.InfoBarSeverity.Warning);
                break;
            case HotkeyCaptureOutcome.Recorded:
                ActiveHotkeyBox.Text = step.Text!;
                break;
            case HotkeyCaptureOutcome.Completed:
                Finalize(step.Binding!, step.Message);
                break;
        }
    }

    // The title bar is the window's non-client area, where WPF raises no mouse events (WPF-UI answers WM_NCHITTEST with
    // HTCAPTION there, and with the caption buttons' codes over them), so a middle or side button pressed with the
    // pointer on it arrives only as these window messages. While capturing they go to the same capture, and are marked
    // handled, so the side buttons' releases never become Back or Forward commands either.
    private const int WmNcMButtonDown = 0x00A7;
    private const int WmNcMButtonUp = 0x00A8;
    private const int WmNcXButtonDown = 0x00AB;
    private const int WmNcXButtonUp = 0x00AC;

    private IntPtr CaptureNonClientMouseButtons(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_capturing || _capture is null || msg is not (WmNcMButtonDown or WmNcMButtonUp or WmNcXButtonDown or WmNcXButtonUp))
        {
            return IntPtr.Zero;
        }

        // For the X button messages the high word of wParam names the button (XBUTTON1 or XBUTTON2).
        var button = msg is WmNcMButtonDown or WmNcMButtonUp
            ? MouseButtons.Middle
            : (((long)wParam >> 16) & 0xFFFF) switch { 1 => MouseButtons.Back, 2 => MouseButtons.Forward, _ => 0u };
        if (button == 0)
        {
            return IntPtr.Zero;
        }

        var step = msg is WmNcMButtonDown or WmNcXButtonDown
            ? _capture.Press(button)
            : _capture.Release(button, ActiveSelectedMode);
        handled = true;
        ApplyCaptureStep(step);

        // An application that processes WM_NCXBUTTONDOWN or WM_NCXBUTTONUP returns TRUE.
        return msg is WmNcXButtonDown or WmNcXButtonUp ? new IntPtr(1) : IntPtr.Zero;
    }

    private void Finalize(HotkeyBinding binding, string? chordWarning)
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
        _capture = null;
        _setHotkeyCaptureMode(false);
        ShowWaitingExternalAiCleanup();
        ActiveHotkeyBox.Text = HotkeyCapture.Describe(binding);
        HideMouseButtonsHint();
        UpdateShortcutRows();
        UpdateFirstRunHint();
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
        else if (chordWarning is not null)
        {
            ShowInfo(chordWarning, Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    private void CancelCapture()
    {
        _capturing = false;
        _capture = null;
        _setHotkeyCaptureMode(false);
        ShowWaitingExternalAiCleanup();
        ActiveHotkeyBox.Text = CurrentHotkeyDescription();
        HideMouseButtonsHint();
        UpdateShortcutRows();
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

    private void TranscriptionModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingUi)
        {
            UpdateTranscriptionModelUi();
        }
    }

    private void UpdateTranscriptionModelUi()
    {
        if (TranscriptionModelCombo.SelectedItem is not TranscriptionModelChoice choice)
        {
            return;
        }

        TranscriptionModelHint.Text = choice.Hint;
        TranscriptionModelInstallButton.Visibility = choice.ShowInstall ? Visibility.Visible : Visibility.Collapsed;
        TranscriptionModelInstallButton.IsEnabled = choice.ShowInstall && !_transcriptionModelOp;
        TranscriptionModelInstallButton.Content = choice.IsInstalled ? "Downloaded" : "Download";
    }

    private async void TranscriptionModelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptionModelOp ||
            TranscriptionModelCombo.SelectedItem is not TranscriptionModelChoice choice)
        {
            return;
        }

        var model = TranscriptionModelCatalog.Resolve(choice.Id);
        if (model.IsBundled)
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
            ShowInfo($"{model.DisplayName} is downloaded. Save, then restart Scribe to use it.");
            LoadTranscriptionModelChoices(model.Id);
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

    // --- AI cleanup ----------------------------------------------------------------------

    private CleanupProvider SelectedProvider =>
        AiProviderCopilotRadio?.IsChecked == true ? CleanupProvider.GitHubCopilot :
        AiProviderFoundryRadio?.IsChecked == true ? CleanupProvider.AzureFoundry :
        AiProviderCustomRadio?.IsChecked == true ? CleanupProvider.OpenAiCompatible :
        CleanupProvider.FoundryLocal;

    private CleanupPromptStyle SelectedPromptStyle =>
        (AiPromptStyleCombo.SelectedItem as PromptStyleChoice)?.Style ?? CleanupPromptStyle.Auto;

    private void AiCleanupCheck_Toggled(object sender, RoutedEventArgs e)
    {
        // Before the loading guard: dictionary, snippets and profile notices read this switch whatever set it.
        UpdateDictionaryGlossaryHint();
        RefreshTextChangesNotice();
        RefreshProfileRules();
        if (_loadingUi)
        {
            return;
        }

        if (!_showingExternalAiCleanup)
        {
            _externalAiCleanup.UserChanged();
        }

        UpdateAiEnabledState();
    }

    private void AiProviderRadio_Checked(object sender, RoutedEventArgs e) =>
        AiProviderChanged(RemoteActivityTrigger.ProviderChange);

    private void AiProviderChanged(RemoteActivityTrigger trigger)
    {
        UpdateDictionaryGlossaryHint();
        if (_loadingUi)
        {
            return;
        }

        UpdateAiProviderPanels();
        UpdateAiEnabledState();
        RefreshAiStatus();

        if (SelectedProvider == CleanupProvider.FoundryLocal)
        {
            if (_savedAiProvider != CleanupProvider.FoundryLocal)
            {
                FoundryStatusRow.Show(new(AiCleanupStatusKind.Info, FoundryIdleStatus));
            }

            _ = RefreshFoundryModelsAsync(initializeRuntime: false);
        }
        else if (SelectedProvider == CleanupProvider.AzureFoundry &&
                 RemoteActivityPolicy.MayContact(_committedSettings, CurrentAiDraftSettings(), trigger))
        {
            _ = ProbeAzureSignInAsync();
        }
    }

    private void SetSelectedProviderRadio(CleanupProvider provider)
    {
        if (AiProviderLocalRadio is null)
        {
            return;
        }

        AiProviderLocalRadio.IsChecked = provider == CleanupProvider.FoundryLocal;
        AiProviderFoundryRadio.IsChecked = provider == CleanupProvider.AzureFoundry;
        AiProviderCustomRadio.IsChecked = provider == CleanupProvider.OpenAiCompatible;
        AiProviderCopilotRadio.IsChecked = provider == CleanupProvider.GitHubCopilot;
    }

    // The sign-in fields for the method shown, as Save stores them: the draft and the Save read them only through this.
    private AzureSignInFields.Stored ShownAzureSignInFields => AzureSignInFields.For(
        SelectedAzureAuthMode,
        IsAzureApiKeySelected,
        AzureTenantBox?.Text,
        SpTenantBox?.Text,
        SpClientIdBox?.Text,
        SpClientSecretBox?.Password,
        SelectedAzureApiKey);

    private AppSettings CurrentAiDraftSettings()
    {
        var draft = _settings.Clone();
        draft.EnableAiCleanup = AiCleanupCheck?.IsChecked == true;
        draft.AiCleanupProvider = SelectedProvider;
        draft.AiCleanupModel = SelectedFoundryModelAlias;
        draft.AiCleanupAzureAuthMode = SelectedAzureAuthMode;
        draft.AiCleanupAzureEndpoint = AzureEndpointBox?.Text;
        draft.AiCleanupAzureDeployment = AzureDeploymentBox?.Text;
        var signIn = ShownAzureSignInFields;
        draft.AiCleanupAzureTenantId = signIn.TenantId;
        draft.AiCleanupAzureClientId = signIn.ClientId;
        draft.AiCleanupAzureClientSecret = signIn.ClientSecret;
        draft.AiCleanupAzureApiKey = signIn.ApiKey;
        draft.AiCleanupCustomEndpoint = CustomEndpointBox?.Text;
        draft.AiCleanupCustomModel = CustomModelBox?.Text;
        draft.AiCleanupCustomApiKey = CustomApiKeyBox?.Password;
        draft.AiCleanupCopilotModel = CopilotModelCombo?.Text;
        return draft;
    }

    // --- Filterable model dropdowns --------------------------------------------------------
    // The pickers are editable ComboBoxes doing double duty: click the chevron to browse every
    // discovered model, or type to quick-filter the open list. Users shouldn't need to know a
    // deployment's name up front; browsing is the primary path, search the accelerator.

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

    private string SelectedFoundryModelAlias =>
        (AiModelBox.SelectedItem as FoundryModelChoice)?.Alias ??
        AiModelBox.SelectedValue as string ??
        NullIfBlank(AiModelBox.Text) ?? CleanupModelCatalog.DefaultAlias;

    private void SetFoundryModelItems(IReadOnlyList<FoundryModelOption> liveCatalog, string? selectedAlias = null)
    {
        _suppressComboFilter = true;
        try
        {
            var selected = string.IsNullOrWhiteSpace(selectedAlias) ? SelectedFoundryModelAlias : selectedAlias.Trim();
            AiModelBox.DisplayMemberPath = nameof(FoundryModelChoice.Label);
            AiModelBox.SelectedValuePath = nameof(FoundryModelChoice.Alias);
            var choices = FoundryModelChoices.Build(selected, CleanupModelCatalog.Curated, liveCatalog);
            AiModelBox.ItemsSource = choices;
            AiModelBox.Items.Filter = null;
            AiModelBox.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.Alias, selected, StringComparison.OrdinalIgnoreCase));
            if (AiModelBox.SelectedItem is null)
            {
                AiModelBox.Text = selected;
            }

            // The rebuild decides the last operation's outcome once, from the alias it restored. The combo can't be asked
            // while its items are replaced: its selection is cleared then, and its alias reads as the display text. A
            // failure or a running operation for that model keeps its row; anything else is retired, so the service's
            // progress and the catalog's loaded state show.
            if (_foundryOperationStatus is { } outcome &&
                !(FoundryLocalSetup.KeepsThroughPickerRebuild(outcome) &&
                  string.Equals(selected, _foundryOperationAlias, StringComparison.OrdinalIgnoreCase)))
            {
                _foundryOperationStatus = null;
                _foundryOperationAlias = null;
            }
        }
        finally
        {
            _suppressComboFilter = false;
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

        // A real choice of another model retires the last operation's outcome, whatever it was. A rebuild of the picker (a
        // catalog refresh) selects programmatically and decides for itself in SetFoundryModelItems.
        if (!_suppressComboFilter)
        {
            _foundryOperationStatus = null;
            _foundryOperationAlias = null;
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

            // Keep the portal's project URL for Entra and the account URL for API keys.
            // Cleanup normalizes both to account-level inference.
            var usingApiKey = IsAzureApiKeySelected && !string.IsNullOrWhiteSpace(AzureApiKeyBox.Password);
            AzureEndpointBox.Text = deployment.EndpointFor(usingApiKey);
            AzureDeploymentBox.Text = deployment.DeploymentName;
        }

        UpdateAzureDeploymentHint();
    }

    // The Foundry Local panel's resting status line, the same text the XAML starts with.
    private const string FoundryIdleStatus =
        "Nothing downloads until you choose Set up or Load, or save with AI cleanup on. " +
        "Choosing somewhere else for AI cleanup removes what Scribe downloaded for it.";

    // The local status row has one next action chosen by FoundryLocalSetup. The action is the only page path
    // that may start the Foundry Local runtime or download a model.
    private async Task SetupFoundryLocalAsync()
    {
        _foundryOperationAlias = SelectedFoundryModelAlias;
        _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.SettingUp, DisplayNameForFoundryAlias(_foundryOperationAlias), ModelSizeForAlias(_foundryOperationAlias));
        FoundryStatusRow.Show(new(AiCleanupStatusKind.Busy, "Setting up. The first time can take a while."));
        try
        {
            var available = await Task.Run(() => _cleanup.ProbeAsync());
            if (!available)
            {
                _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.Failed, DisplayNameForFoundryAlias(_foundryOperationAlias), ModelSizeForAlias(_foundryOperationAlias));
                FoundryStatusRow.Show(new(AiCleanupStatusKind.Error, "Couldn't set up AI on this PC. Try again, or choose another AI service.", new(AiCleanupActionId.TryAgain, "Try again")));
                return;
            }

            var count = await RefreshFoundryModelsAsync(initializeRuntime: true);
            var loaded = _foundryExecutionBuilds.Values.FirstOrDefault(m => m.Loaded);
            var running = loaded is null
                ? $"Ready to download {DisplayNameForFoundryAlias(SelectedFoundryModelAlias)}."
                : $"{loaded.Alias} is ready.";

            _foundryOperationStatus = new FoundryLocalSetupDescription(AiCleanupStatusKind.Info, count switch
            {
                null => $"Foundry Local is running, but its model list could not be read. {running}",
                0 => "Foundry Local is running but reported no models. Check that it finished starting, then try again.",
                _ => $"Foundry Local is running. {count} models are in the dropdown above. {running}",
            }, "Load", false, FoundryLocalSetupStage.RuntimeReady);
            FoundryStatusRow.Show(new(_foundryOperationStatus.Kind, _foundryOperationStatus.Text, new(AiCleanupActionId.Load, "Load")));
        }
        catch
        {
            _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.Failed, DisplayNameForFoundryAlias(_foundryOperationAlias), ModelSizeForAlias(_foundryOperationAlias));
            FoundryStatusRow.Show(new(AiCleanupStatusKind.Error, "Couldn't set up AI on this PC. Try again, or choose another AI service.", new(AiCleanupActionId.TryAgain, "Try again")));
        }
    }

    // Merges the live Foundry Local catalog into the searchable picker and refreshes the loaded-model
    // status. Best-effort: if Foundry Local isn't installed the curated alias list stays in place.
    // Returns how many models the catalog reported, or null when the catalog could not be read, so
    // the caller can tell "no models" apart from "could not ask". initializeRuntime is true only for
    // an explicit request: starting the runtime registers its execution providers, which can mean a
    // download of several GB, so showing the page or selecting the provider only reads a runtime that
    // is already running.
    private async Task<int?> RefreshFoundryModelsAsync(bool initializeRuntime)
    {
        try
        {
            var models = initializeRuntime
                ? await _cleanup.ListFoundryModelsAsync()
                : await _cleanup.ListFoundryModelsIfInitializedAsync();
            SetFoundryModelItems(models);

            _foundryExecutionBuilds.Clear();
            foreach (var model in models)
            {
                _foundryExecutionBuilds[model.Alias] = model;
            }

            UpdateAiModelHint();
            RefreshAiStatus();
            return models.Count;
        }
        catch
        {
            // Leave the curated list and existing status untouched on any failure.
            return null;
        }
    }

    private void UpdateFoundryLoadedText(string? loadedAlias, string? deviceLabel = null)
    {
        var modelName = DisplayNameForFoundryAlias(string.IsNullOrWhiteSpace(loadedAlias) ? SelectedFoundryModelAlias : loadedAlias);
        var stage = string.IsNullOrWhiteSpace(loadedAlias)
            ? FoundryLocalSetupStage.CachedUnloaded
            : FoundryLocalSetupStage.Loaded;
        var setup = FoundryLocalSetup.Describe(stage, modelName, ModelSizeForAlias(SelectedFoundryModelAlias));
        FoundryStatusRow.Show(new AiCleanupStatusRow(setup.Kind, setup.Text, setup.ActionText is null ? null : new(setup.ActionText == "Load" ? AiCleanupActionId.Load : AiCleanupActionId.Unload, setup.ActionText, setup.ActionText != "Unload" || setup.CanUnload)));
    }

    private async Task LoadFoundryModelAsync()
    {
        var alias = SelectedFoundryModelAlias;
        if (_foundryModelOp || string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        _foundryModelOp = true;
        _foundryOperationAlias = alias;
        _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.DownloadingOrLoading, DisplayNameForFoundryAlias(alias), ModelSizeForAlias(alias), "Loading the model...");
        FoundryStatusRow.Show(new(AiCleanupStatusKind.Busy, "Loading the model..."));
        try
        {
            string? lastMessage = null;
            var progress = new Progress<string>(message =>
            {
                lastMessage = message;
                FoundryStatusRow.Show(new(AiCleanupStatusKind.Busy, message));
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ok = await _cleanup.LoadFoundryModelAsync(alias, progress, cts.Token);
            if (!ok)
            {
                _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.ModelFailed, DisplayNameForFoundryAlias(alias), ModelSizeForAlias(alias));
                FoundryStatusRow.Show(new(AiCleanupStatusKind.Error, string.IsNullOrWhiteSpace(lastMessage)
                    ? _foundryOperationStatus.Text
                    : lastMessage, new(AiCleanupActionId.TryAgain, "Try again")));
            }
        }
        catch
        {
            _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.ModelFailed, DisplayNameForFoundryAlias(alias), ModelSizeForAlias(alias));
            FoundryStatusRow.Show(new(AiCleanupStatusKind.Error, _foundryOperationStatus.Text, new(AiCleanupActionId.TryAgain, "Try again")));
        }
        finally
        {
            _foundryModelOp = false;
            if (_foundryOperationStatus?.Kind == AiCleanupStatusKind.Busy)
            {
                _foundryOperationStatus = null;
                _foundryOperationAlias = null;
            }
            await RefreshFoundryModelsAsync(initializeRuntime: false);
        }
    }

    private async Task UnloadFoundryModelAsync()
    {
        if (_foundryModelOp)
        {
            return;
        }
        _foundryModelOp = true;
        _foundryOperationAlias = SelectedFoundryModelAlias;
        _foundryOperationStatus = FoundryLocalSetup.Describe(FoundryLocalSetupStage.DownloadingOrLoading, DisplayNameForFoundryAlias(_foundryOperationAlias), ModelSizeForAlias(_foundryOperationAlias), "Freeing model memory...");
        FoundryStatusRow.Show(new(AiCleanupStatusKind.Busy, "Freeing model memory..."));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var loaded = await _cleanup.GetLoadedFoundryModelAsync(cts.Token);
            _ = await _cleanup.UnloadFoundryModelAsync(loaded, cts.Token);
            _foundryOperationStatus = null;
            _foundryOperationAlias = null;
            FoundryStatusRow.Show(new(AiCleanupStatusKind.Success, "Model memory freed.", new(AiCleanupActionId.Load, "Load")));
        }
        catch
        {
            _foundryOperationStatus = new FoundryLocalSetupDescription(AiCleanupStatusKind.Error, "Couldn't free model memory. Try again.", "Try again", false, FoundryLocalSetupStage.ModelFailed);
            FoundryStatusRow.Show(new(AiCleanupStatusKind.Error, "Couldn't free model memory. Try again.", new(AiCleanupActionId.TryAgain, "Try again")));
        }
        finally
        {
            _foundryModelOp = false;
            if (_foundryOperationStatus?.Kind == AiCleanupStatusKind.Busy)
            {
                _foundryOperationStatus = null;
                _foundryOperationAlias = null;
            }
            await RefreshFoundryModelsAsync(initializeRuntime: false);
        }
    }

    private async Task RunAzurePrimaryActionAsync(AiCleanupActionId action)
    {
        if (_azureSignInAttempts.IsBusy)
        {
            return;
        }

        if (IsAzureApiKeySelected)
        {
            await VerifyAzureApiKeyAsync();
            return;
        }

        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            await VerifyServicePrincipalAsync();
            return;
        }

        var interactive = action == AiCleanupActionId.SignIn;
        await RefreshAzureConnectionAsync(
            allowInteractiveLogin: interactive,
            listModels: true,
            forceListModels: action == AiCleanupActionId.RefreshModels);
    }

    private void AzureEndpointBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        InvalidateAzureApiKeyVerification("The endpoint changed. Verify the API key again.");
        ApplyAzureSettingsAccess();
        UpdateAzureProjectApiKeyHint();
    }

    private void AzureDeploymentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        InvalidateAzureApiKeyVerification("The deployment changed. Verify the API key again.");
        ApplyAzureSettingsAccess();
    }

    private void AzureApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        _azureManualConfiguration = true;
        InvalidateAzureApiKeyVerification("The API key changed. Verify it again.");
        ApplyAzureSettingsAccess();
        UpdateAzureDeploymentHint();
        UpdateAzureProjectApiKeyHint();
    }

    private void InvalidateAzureApiKeyVerification(string message)
    {
        if (!IsAzureApiKeySelected)
        {
            return;
        }

        // Retiring any verification still running also ends its busy state (AzureSignInAttempts), which that
        // verification no longer can, so editing the endpoint mid-probe leaves the Verify button usable.
        _azureSignInAttempts.Retire();
        _azureApiKeyVerified = false;
        _azureApiKeyOutcome = AzureVerificationOutcome.ChangedSince;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        ApplyAzureSettingsAccess();

        if (CanVerifyAzureApiKey)
        {
            _azureStatusMessage = message;
        }
    }

    private void UpdateAzureProjectApiKeyHint()
    {
        if (AzureProjectApiKeyHint is null)
        {
            return;
        }

        var projectEndpointWithKey = IsAzureApiKeySelected
            && !string.IsNullOrWhiteSpace(AzureEndpointBox?.Text)
            && AzureEndpointBox.Text.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase);
        AzureProjectApiKeyHint.Visibility = projectEndpointWithKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AzureTenantBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        // A different tenant means a different sign-in, so the verified state goes and the page returns
        // to its signed-out form until the user signs in again. It deliberately does not request manual
        // setup (_azureManualConfiguration): that would open the endpoint fields and hide "Use endpoint
        // instead" for anyone who types a tenant before signing in. The box sits with the sign-in method,
        // outside every panel this hides, so it stays put while the user types.
        _azureSignInAttempts.Retire();
        ++_azureDeploymentLoadVersion;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        _azureAutoListed = false;
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

        ApplyAzureSettingsAccess();
        _azureStatusMessage = "Tenant changed. Verify Azure sign-in before browsing subscriptions and models.";
    }

    // Best-effort and non-blocking; runs when the Azure panel is shown.
    private Task ProbeAzureSignInAsync()
    {
        if (IsAzureApiKeySelected)
        {
            _azureStatusMessage = "Fill in the details above, then choose Verify.";

            ApplyAzureSettingsAccess();
            return Task.CompletedTask;
        }

        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            _azureConnectionKnown = true;
            var principal = CurrentServicePrincipal;
            if (principal is null)
            {
                _azureStatusMessage = "Fill in the details above, then choose Verify.";

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
        // Everything below probes Azure CLI specifically. In API-key or service-principal mode a
        // valid CLI session would otherwise make the other identity look verified.
        if (IsAzureApiKeySelected || SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            return;
        }

        var operationVersion = _azureSignInAttempts.Begin();
        var shouldListModels = false;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        ApplyAzureSettingsAccess();
        _azureStatusMessage = "Checking your Azure sign-in...";

        try
        {
            // Editing the tenant or switching the method retires this attempt during any of its waits, and
            // AzureCliSignIn then stops before it changes anything, a browser sign-in included. It also shows the
            // outcome itself (CliSignInSteps.Publish), right after a last ownership check with nothing awaited in
            // between, because an await can resume in a later dispatcher operation.
            var result = await AzureCliSignIn.RunAndPublishAsync(
                new CliSignInSteps(this, allowInteractiveLogin, _azureSignInAttempts.CancellationOf(operationVersion)),
                allowInteractiveLogin,
                () => _azureSignInAttempts.IsCurrent(operationVersion));

            // This continuation is such a later operation too: an attempt retired since must not start a listing.
            if (!_azureSignInAttempts.IsCurrent(operationVersion))
            {
                return;
            }

            shouldListModels = result.Outcome == AzureCliSignIn.Outcome.SignedIn &&
                listModels && (forceListModels || allowInteractiveLogin || !_azureAutoListed);
        }
        catch (OperationCanceledException)
        {
            if (_azureSignInAttempts.IsCurrent(operationVersion))
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureConnectionKnown = true;
                ApplyAzureSettingsAccess();
                _azureStatusMessage = "Azure sign-in timed out. Please try again.";
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not verify Azure sign-in.");
            if (_azureSignInAttempts.IsCurrent(operationVersion))
            {
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureConnectionKnown = true;
                ApplyAzureSettingsAccess();
                _azureStatusMessage = "Couldn't verify Azure sign-in. Please try again.";
            }
        }
        finally
        {
            if (_azureSignInAttempts.Finish(operationVersion))
            {
                ApplyAzureSettingsAccess();
            }
        }

        if (shouldListModels && _azureSignInAttempts.IsCurrent(operationVersion))
        {
            await ListAzureDeploymentsAsync();
        }
    }

    private async Task<AzureSignInStatus> ProbeCurrentAzureSignInAsync(CancellationToken attemptCancellation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var selectedSubscription = SelectedAzureSubscription;
        var tenantId = selectedSubscription is null || string.IsNullOrWhiteSpace(selectedSubscription.TenantId)
            ? NullIfBlank(AzureTenantBox.Text)
            : selectedSubscription.TenantId;
        return await _azureDiscovery.GetSignInStatusAsync(
            tenantId,
            selectedSubscription?.Id,
            cts.Token);
    }

    /// <summary>
    /// The window's steps for <see cref="AzureCliSignIn"/>. Each reads the page when it runs, so a check or a
    /// browser sign-in uses the tenant and subscription shown at that moment.
    /// </summary>
    /// <remarks>
    /// Every Azure CLI wait also ends when the attempt is retired (<paramref name="attemptCancellation"/>). They all
    /// run inside Azure CLI's single gate (AzureCliProcessCoordinator), which every token request waits on, cleanup's
    /// included, so a retired az login left running made every later check time out until its sign-in window was
    /// dismissed or five minutes had passed.
    /// </remarks>
    private sealed class CliSignInSteps(
        SettingsWindow window, bool allowInteractiveLogin, CancellationToken attemptCancellation) : IAzureCliSignInSteps
    {
        public async Task<bool> IsCliInstalledAsync()
        {
            var installed = await window._azureCliInstaller.IsInstalledAsync();

            // A fact about this PC rather than this attempt's result, so it is kept even when the attempt is retired.
            window._azureCliInstalled = installed;
            window._azureConnectionKnown = true;
            return installed;
        }

        public bool HasSelectedSubscription => window.SelectedAzureSubscription is not null;

        public async Task<bool> IsSelectedSubscriptionUnavailableAsync()
        {
            var selectedSubscription = window.SelectedAzureSubscription;
            if (selectedSubscription is null)
            {
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var (ok, subscriptions, _) = await window._azureCliInstaller.ListSubscriptionsAsync(cts.Token);
            return ok && subscriptions.All(subscription => !string.Equals(
                subscription.Id,
                selectedSubscription.Id,
                StringComparison.OrdinalIgnoreCase));
        }

        public void ClearSelectedSubscription() => window.ClearSelectedAzureSubscription();

        public Task<AzureSignInStatus> ProbeAsync() => window.ProbeCurrentAzureSignInAsync(attemptCancellation);

        public void ReportBrowserSignIn() => window._azureStatusMessage = "Finish signing in in your browser.";

        public async Task<(bool Ok, string Message)> LoginAsync()
        {
            // Retiring the attempt cancels this, and AzureCliInstaller.RunAsync then ends az's own processes (cmd.exe and
            // az's python, never a browser az opened: AzureCliProcessTree), so a tenant edit no longer leaves it holding
            // the gate for up to five minutes. A sign-in page or window it opened stays open, but nothing is left to
            // receive its result.
            using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation);
            loginCts.CancelAfter(TimeSpan.FromMinutes(5));
            var selectedSubscription = window.SelectedAzureSubscription;
            var tenantId = selectedSubscription is null || string.IsNullOrWhiteSpace(selectedSubscription.TenantId)
                ? NullIfBlank(window.AzureTenantBox.Text)
                : selectedSubscription.TenantId;
            return await window._azureCliInstaller.LoginAsync(tenantId, loginCts.Token);
        }

        // Only while the attempt owns the page (AzureCliSignIn.RunAndPublishAsync), never for a retired one.
        public void Publish(AzureCliSignIn.Result result)
        {
            var status = result.Status ?? new AzureSignInStatus(false, null);
            window._azureSignInStatus = status;
            window.ApplyAzureSettingsAccess();
            window._azureStatusMessage = result.Outcome switch
            {
                AzureCliSignIn.Outcome.CliMissing =>
                    "Azure CLI isn't installed.",
                AzureCliSignIn.Outcome.LoginFailed => result.Message,
                AzureCliSignIn.Outcome.NotSignedIn when allowInteractiveLogin =>
                    "Azure sign-in completed, but Scribe could not verify an Azure token. Check the tenant and try again.",
                AzureCliSignIn.Outcome.NotSignedIn =>
                    "Not signed in to Azure. Until you sign in, Scribe types what it heard.",
                _ => $"{DescribeAzureIdentity(status)} Listing compatible deployments…",
            };
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

    private bool IsAzureApiKeySelected => AzureApiKeyRadio?.IsChecked == true;

    private string SelectedAzureApiKey => IsAzureApiKeySelected ? AzureApiKeyBox?.Password ?? string.Empty : string.Empty;

    private bool CanVerifyAzureApiKey =>
        IsAzureApiKeySelected &&
        !string.IsNullOrWhiteSpace(AzureEndpointBox?.Text) &&
        !string.IsNullOrWhiteSpace(AzureDeploymentBox?.Text) &&
        !string.IsNullOrWhiteSpace(SelectedAzureApiKey);

    private AzureAuthMode SelectedAzureAuthMode =>
        AzureServicePrincipalRadio?.IsChecked == true
            ? AzureAuthMode.ServicePrincipal
            : AzureAuthMode.AzureCli;

    /// <summary>The app registration currently entered, or null when it is incomplete.</summary>
    private AzureServicePrincipal? CurrentServicePrincipal => AzureServicePrincipal.TryCreate(
        SelectedAzureAuthMode,
        SpTenantBox?.Text,
        SpClientIdBox?.Text,
        SpClientSecretBox?.Password);

    private void SetSelectedAzureAuthRadio(int selectedIndex)
    {
        if (AzureCliRadio is null)
        {
            return;
        }

        AzureCliRadio.IsChecked = selectedIndex == 0;
        AzureServicePrincipalRadio.IsChecked = selectedIndex == 1;
        AzureApiKeyRadio.IsChecked = selectedIndex == 2;
    }

    private void AzureAuthRadio_Checked(object sender, RoutedEventArgs e) =>
        AzureAuthModeChanged();

    private void AzureUseApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        AzureApiKeyRadio.IsChecked = true;
        _azureManualConfiguration = true;
        ApplyAzureSettingsAccess();
        UpdateAzureProjectApiKeyHint();
        AzureEndpointBox.Focus();
    }

    private AzureSettingsAccess.State CurrentAzureSettingsAccess =>
        AzureSettingsAccess.Resolve(
            _azureCliInstalled,
            _azureSignInStatus.IsSignedIn,
            _azureManualConfiguration || IsAzureApiKeySelected,
            !string.IsNullOrWhiteSpace(SelectedAzureApiKey),
            SelectedAzureAuthMode,
            CurrentServicePrincipal is not null,
            IsAzureApiKeySelected);

    private void ApplyAzureSettingsAccess()
    {
        if (AzureDiscoveryPanel is null || AzureEndpointPanel is null)
        {
            return;
        }

        var access = CurrentAzureSettingsAccess;
        var servicePrincipal = access.ShowServicePrincipalFields;
        var apiKeyMode = IsAzureApiKeySelected;
        var cliMode = !apiKeyMode && SelectedAzureAuthMode == AzureAuthMode.AzureCli;
        AzureDiscoveryPanel.Visibility = cliMode && access.ShowDiscovery ? Visibility.Visible : Visibility.Collapsed;
        AzureEndpointPanel.Visibility = access.ShowEndpointPanel ? Visibility.Visible : Visibility.Collapsed;
        AzureManualToggleButton.Visibility = access.ShowManualToggleButton ? Visibility.Visible : Visibility.Collapsed;

        var manualOpen = access.ShowEndpointPanel && cliMode;
        AzureManualToggleText.Text = manualOpen ? "Hide manual details" : "Enter details manually";
        AzureManualToggleButton.SetValue(AutomationProperties.NameProperty, AzureManualToggleText.Text);
        AzureManualToggleIcon.Symbol = manualOpen
            ? Wpf.Ui.Controls.SymbolRegular.ChevronUp24
            : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;

        if (AzureServicePrincipalPanel is not null)
        {
            AzureServicePrincipalPanel.Visibility = servicePrincipal ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AzureApiKeyPanel is not null)
        {
            AzureApiKeyPanel.Visibility = access.ShowApiKeyPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AzureCliTenantPanel is not null)
        {
            AzureCliTenantPanel.Visibility = access.ShowCliTenant ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AzureEndpointHint is not null)
        {
            AzureEndpointHint.Text = cliMode ? "Filled in when you choose a model." : "Copy it from your resource in the Azure portal.";
        }

        UpdateServicePrincipalValidation(servicePrincipal);
        UpdateAzureProjectApiKeyHint();
        RefreshAiStatus();
    }

    private void AzureManualToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _azureManualConfiguration = !AzureEndpointPanel.IsVisible;
        ApplyAzureSettingsAccess();
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

    private void AzureAuthModeChanged()
    {
        if (_loadingUi)
        {
            return;
        }

        if (!IsAzureApiKeySelected)
        {
            // Both Entra modes ultimately write one tenant setting, so carry the current value across
            // rather than making the user retype it.
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
        }

        // The previous mode's verification says nothing about this one's identity, so drop it and
        // make the user verify again instead of showing a stale signed-in state. Retiring the attempt
        // abandons any probe still in flight from the mode being left and ends its busy state, which
        // that probe no longer can (AzureSignInAttempts); bumping the deployment version abandons its
        // listing.
        _azureSignInAttempts.Retire();
        ++_azureDeploymentLoadVersion;
        _azureSignInStatus = new AzureSignInStatus(false, null);
        _azureApiKeyOutcome = AzureVerificationOutcome.NotRun;
        _servicePrincipalOutcome = AzureVerificationOutcome.NotRun;
        _azureAutoListed = false;
        AzureCredentialInvalidation.Invalidate();
        ApplyAzureSettingsAccess();
        _azureStatusMessage = IsAzureApiKeySelected
            ? "Fill in the details above, then choose Verify."
            : SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal
                ? "Fill in the details above, then choose Verify."
                : "Not checked yet.";

        ApplyAzureSettingsAccess();
    }

    private void ServicePrincipalField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        // Editing the identity retires any verification, in flight or already applied. Retiring
        // unconditionally matters: a verification started against the previous details must not be
        // allowed to land on the new ones just because nothing was verified yet. Retiring also ends the
        // busy state, which the retired verification no longer can, so Verify is usable again at once.
        var verifying = _azureSignInAttempts.IsBusy;
        _azureSignInAttempts.Retire();

        // The row reads the typed outcome, which belongs to the details it was reached for.
        if (_servicePrincipalOutcome.Kind is AzureVerificationOutcomeKind.Succeeded or AzureVerificationOutcomeKind.Failed)
        {
            _servicePrincipalOutcome = AzureVerificationOutcome.ChangedSince;
        }

        if ((_azureSignInStatus.IsSignedIn || verifying) && SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            // Also replaces "Verifying the service principal…", which nothing would replace any more.
            _azureSignInStatus = new AzureSignInStatus(false, null);
            _azureStatusMessage = "Changed since the last check. Choose Verify.";
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

    private void AboutOpenStore_Click(object sender, RoutedEventArgs e) =>
        // The protocol form lands in the Store app; OpenExternalLink surfaces the failure if the
        // Store has been removed, in which case the copyable web link beside it still works.
        OpenExternalLink(ScribeLinks.StoreProtocol, "Could not open the Microsoft Store.");

    private void AboutCopyStoreLink_Click(object sender, RoutedEventArgs e) =>
        CopyPathToClipboard(ScribeLinks.StoreWeb, "Store link");

    /// <summary>
    /// The unconditional reporting route required by Store policy 11.16, reachable whether or not
    /// History has anything in it.
    /// </summary>
    private void AboutReportAiContent_Click(object sender, RoutedEventArgs e) =>
        ShowAiReportDialog(string.Empty);

    /// <summary>
    /// Sends the user to the Store's own rating surface.
    /// </summary>
    /// <remarks>
    /// Offered to everyone, always, and never gated on the History thumbs. Routing only satisfied
    /// users here while steering unhappy ones to a private inbox is the pattern Store policy files
    /// under fraudulent activity, so the two must stay unconnected.
    /// </remarks>
    private void AboutRateInStore_Click(object sender, RoutedEventArgs e) =>
        OpenExternalLink(ScribeLinks.StoreReview, "Could not open the Microsoft Store rating page.");

    // --- About: where your data lives --------------------------------------------------------
    // The paths come from AppPaths, the same object every writer uses, so what is shown here can
    // never drift from where the files actually are. A portable profile (SCRIBE_DATA_DIR) or a
    // Store install both resolve correctly for free.

    // Every one of these hands a path to something OUTSIDE this process (the clipboard, Explorer),
    // so they all use the effective path rather than the one Scribe writes through.

    private void AboutCopyLogsPath_Click(object sender, RoutedEventArgs e) =>
        CopyPathToClipboard(_paths.EffectiveLogsDir, "log folder path");

    private void AboutCopyDatabasePath_Click(object sender, RoutedEventArgs e) =>
        CopyPathToClipboard(_paths.EffectiveDatabasePath, "data file path");

    private void AboutOpenLogsFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_paths.EffectiveLogsDir);

    /// <summary>
    /// Writes every retained log file plus an environment report into one zip the user chooses the
    /// location of.
    /// <para>
    /// This is the path that actually gets diagnostics out of a user's machine. Copying a path and
    /// asking somebody to navigate to a hidden folder fails for reasons that are nothing to do with
    /// them: AppData is hidden by default, the folder is empty until the app has run, and on a
    /// packaged build predating the manifest fix it was not at the advertised path at all. A file on
    /// their Desktop that they can read before attaching has none of those failure modes.
    /// </para>
    /// </summary>
    private void AboutSaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Scribe diagnostics",
            Filter = "Zip archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = DiagnosticsBundle.SuggestedFileName(DateTimeOffset.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var report = _diagnostics?.ComposeReport()
                ?? "Environment details were unavailable when this bundle was created.";
            var result = DiagnosticsBundle.Create(
                _paths.LogsDir, dialog.FileName, report, DateOnly.FromDateTime(DateTime.Now));

            _log.LogInformation(
                "Wrote a diagnostics bundle with {Count} log file(s), {Bytes} bytes.",
                result.LogFileCount, result.Bytes);

            ShowInfo(result.LogFileCount == 0
                ? $"Saved {System.IO.Path.GetFileName(result.Path)}, but no log files were found to include."
                : $"Saved {System.IO.Path.GetFileName(result.Path)} with {result.LogFileCount} day(s) of logs " +
                  $"({result.Bytes / 1024.0:F0} KB). Open it and read report.txt before sharing.");
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not write the diagnostics bundle.");
            ShowInfo("Couldn't save diagnostics. Choose another location or try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    // Opens the containing folder rather than selecting scribe.db. Selecting a file invites
    // dragging it straight into an email or issue, and that file holds every dictation the user
    // has ever made plus their saved API keys.
    private void AboutOpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_paths.EffectiveRootDir);

    private void CopyPathToClipboard(string path, string label)
    {
        try
        {
            if (!ScribeClipboard.SetText(path))
            {
                throw new InvalidOperationException("Clipboard busy.");
            }

            ShowInfo($"Copied {label}.");
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; that is not worth a crash.
            TryLog(ex, "Could not copy a path to the clipboard.");
            ShowInfo($"Couldn't copy {label}. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Opens a folder in File Explorer. The folder is never created: these directories are made at
    /// startup by <see cref="AppPaths.EnsureCreated"/>, so if one is genuinely missing that is
    /// worth saying rather than papering over with an empty folder the user would read as "my logs
    /// were deleted".
    /// </summary>
    private void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            ShowInfo("That folder doesn't exist yet.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        try
        {
            // Passed as a single argument rather than a command line, so a path containing spaces,
            // quotes or commas cannot be re-parsed into extra Explorer arguments.
            var startInfo = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(folder);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not open the folder.");
            ShowInfo("Couldn't open the folder. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

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
    /// Verifies the entered API key by making a real Responses API call to the configured deployment.
    /// </summary>
    private async Task VerifyAzureApiKeyAsync()
    {
        if (_azureSignInAttempts.IsBusy)
        {
            return;
        }

        if (!CanVerifyAzureApiKey)
        {
            _azureStatusMessage = "Fill in the details above, then choose Verify.";
            ApplyAzureSettingsAccess();
            return;
        }

        var endpoint = AzureEndpointBox.Text.Trim();
        var deployment = AzureDeploymentBox.Text.Trim();
        var apiKey = SelectedAzureApiKey.Trim();
        var operationVersion = _azureSignInAttempts.Begin();
        ApplyAzureSettingsAccess();
        _azureStatusMessage = "Verifying the API key...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _azureSignInAttempts.CancellationOf(operationVersion));
            var result = await ProbeAzureApiKeyAsync(endpoint, deployment, apiKey, linked.Token);
            if (!_azureSignInAttempts.IsCurrent(operationVersion))
            {
                return;
            }

            _azureApiKeyVerified = result.Success;
            _azureApiKeyOutcome = result.Success
                ? AzureVerificationOutcome.Succeeded(result.Message)
                : AzureVerificationOutcome.Failed(result.Message);
            _azureSignInStatus = result.Success
                ? new AzureSignInStatus(true, null)
                : new AzureSignInStatus(false, result.Message);
            _azureStatusMessage = result.Message;
        }
        catch (OperationCanceledException)
        {
            if (_azureSignInAttempts.IsCurrent(operationVersion))
            {
                _azureApiKeyVerified = false;
                _azureApiKeyOutcome = AzureVerificationOutcome.Failed("Verifying the API key timed out. Check the endpoint host and try again.");
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureStatusMessage = _azureApiKeyOutcome.SafeMessage;
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not verify the Azure API key.");
            if (_azureSignInAttempts.IsCurrent(operationVersion))
            {
                _azureApiKeyVerified = false;
                _azureApiKeyOutcome = AzureVerificationOutcome.Failed("The API key could not be verified. Check the endpoint, deployment name, and key.");
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureStatusMessage = _azureApiKeyOutcome.SafeMessage;
            }
        }
        finally
        {
            if (_azureSignInAttempts.Finish(operationVersion))
            {
                ApplyAzureSettingsAccess();
            }
        }
    }

    private async Task<AzureApiKeyProbeResult> ProbeAzureApiKeyAsync(
        string endpoint,
        string deployment,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return AzureApiKeyProbeResult.Fail("The Azure endpoint is not a valid URL.");
        }

        var accountEndpoint = endpointUri.AbsolutePath.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase)
            ? new Uri($"{endpointUri.Scheme}://{endpointUri.Authority}/")
            : endpointUri;
        var responsesEndpoint = new Uri(
            $"{accountEndpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/openai/v1/responses");
        using var request = new HttpRequestMessage(HttpMethod.Post, responsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = deployment,
                input = "ok",
                max_output_tokens = 16,
                store = false,
            }),
            Encoding.UTF8,
            "application/json");

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode
                ? AzureApiKeyProbeResult.Ok("API key verified. Check Cleanup status below for model availability.")
                : AzureApiKeyProbeResult.Fail(DescribeAzureApiKeyFailure(response.StatusCode, deployment, body));
        }
        catch (HttpRequestException ex)
        {
            TryLog(ex, "Could not reach the Azure API-key endpoint.");
            return AzureApiKeyProbeResult.Fail(
                "Couldn't reach the Azure endpoint. Check the URL and network connection.");
        }
    }

    private static string DescribeAzureApiKeyFailure(HttpStatusCode statusCode, string deployment, string responseBody)
    {
        var status = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Azure rejected the API key (401). Check that the key belongs to this resource.",

            HttpStatusCode.Forbidden =>
                "Azure accepted the API key but denied access (403). Check that this key can call the resource.",

            HttpStatusCode.NotFound =>
                $"Azure could not find the deployment '{deployment}' (404). Check the endpoint and exact deployment name.",

            HttpStatusCode.TooManyRequests =>
                "Azure is throttling requests (429). The deployment is reachable but over quota. Wait and retry.",

            _ when status >= 500 =>
                $"Azure returned a server error ({status}). This is usually transient; try again shortly.",

            _ => BuildAzureApiKeyFallback(status, deployment, responseBody),
        };
    }

    private static string BuildAzureApiKeyFallback(int status, string deployment, string responseBody)
    {
        var serverMessage = ExtractAzureErrorMessage(responseBody);
        if (serverMessage.Contains("deployment", StringComparison.OrdinalIgnoreCase) ||
            serverMessage.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return $"Azure could not use deployment '{deployment}' ({status}). {serverMessage}";
        }

        return string.IsNullOrWhiteSpace(serverMessage)
            ? $"Azure returned HTTP {status}. Check the endpoint, deployment name, and API key."
            : $"Azure returned HTTP {status}. {serverMessage}";
    }

    private static string ExtractAzureErrorMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
            {
                return FlattenStatusMessage(text);
            }
        }
        catch (JsonException)
        {
            return FlattenStatusMessage(responseBody);
        }

        return FlattenStatusMessage(responseBody);
    }

    private static string FlattenStatusMessage(string value)
    {
        var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 220 ? line : line[..220].TrimEnd() + "...";
    }

    private sealed record AzureApiKeyProbeResult(bool Success, string Message)
    {
        public static AzureApiKeyProbeResult Ok(string message) => new(true, message);

        public static AzureApiKeyProbeResult Fail(string message) => new(false, message);
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
            ? $"{identity} Check Cleanup status below for model availability."
            : $"{identity} Enter the endpoint and deployment name for your model.";
    }

    private async Task VerifyServicePrincipalAsync(bool automatic = false)
    {
        if (_azureSignInAttempts.IsBusy)
        {
            return;
        }

        var principal = CurrentServicePrincipal;
        if (principal is null)
        {
            return;
        }

        // Guard the completion the same way the CLI probe does. Without this, editing a field or
        // switching modes mid-verification would let the old identity's result land on the new one.
        var operationVersion = _azureSignInAttempts.Begin();
        ApplyAzureSettingsAccess();
        _azureStatusMessage = automatic
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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _azureSignInAttempts.CancellationOf(operationVersion));
            var status = await _azureDiscovery.GetSignInStatusAsync(
                principal.TenantId, null, linked.Token, principal);
            if (!_azureSignInAttempts.IsCurrent(operationVersion))
            {
                return;
            }

            _azureSignInStatus = status;
            _servicePrincipalOutcome = status.IsSignedIn
                ? AzureVerificationOutcome.Succeeded(DescribeServicePrincipalReady(status))
                : AzureVerificationOutcome.Failed(status.FailureReason ?? AzureSignInDiagnostics.Generic);
            _azureStatusMessage = _servicePrincipalOutcome.SafeMessage;
        }
        catch (OperationCanceledException)
        {
            if (_azureSignInAttempts.IsCurrent(operationVersion))
            {
                _servicePrincipalOutcome = AzureVerificationOutcome.Failed("Verifying the service principal timed out. Please try again.");
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureStatusMessage = _servicePrincipalOutcome.SafeMessage;
            }
        }
        catch (Exception ex)
        {
            // The exception is logged, never shown: an Entra failure can echo request details, and
            // this path handles a secret.
            TryLog(ex, "Could not verify the Azure service principal.");
            if (_azureSignInAttempts.IsCurrent(operationVersion))
            {
                _servicePrincipalOutcome = AzureVerificationOutcome.Failed("The service principal could not be verified. Check the details and try again.");
                _azureSignInStatus = new AzureSignInStatus(false, null);
                _azureStatusMessage = _servicePrincipalOutcome.SafeMessage;
            }
        }
        finally
        {
            if (_azureSignInAttempts.Finish(operationVersion))
            {
                ApplyAzureSettingsAccess();
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

            // Before anything is shown: a tenant edit or a newer listing during the wait retires this one, and its
            // failure must not replace "Tenant changed" either.
            if (loadVersion != _azureDeploymentLoadVersion)
            {
                return;
            }

            if (!subscriptionsOk)
            {
                _azureStatusMessage = $"{DescribeAzureIdentity(_azureSignInStatus)} {subscriptionsMessage}";
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
            _azureStatusMessage = discovery.FailedTenantCount > 0
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
                _azureStatusMessage = "Listing Azure deployments timed out. Please try again.";
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not list Azure deployments.");
            if (loadVersion == _azureDeploymentLoadVersion)
            {
                _azureStatusMessage =
                    "Couldn't list deployments. Sign in again and make sure you have access to a deployment.";
            }
        }
        finally
        {
            if (loadVersion == _azureDeploymentLoadVersion)
            {
                RefreshAiStatus();
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
        ShowInfo("Installing or updating the Azure CLI. Finish in the terminal, then choose Check sign-in.");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (ok, message) = await _azureCliInstaller.InstallOrUpdateAsync(cts.Token);
            ShowInfo(message);
            if (ok)
            {
                _azureCliInstalled = true;
                _azureConnectionKnown = true;
            }
        }
        catch (Exception ex)
        {
            TryLog(ex, "Could not start the Azure CLI installer.");
            ShowInfo("Couldn't start the Azure CLI installer. Try again, or use an API key instead.", Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            _azureCliInstalled = await _azureCliInstaller.IsInstalledAsync();
            _azureConnectionKnown = true;
            ApplyAzureSettingsAccess();
        }
    }

    private void TryLog(Exception ex, string message)
    {
        try
        {
            _log.LogWarning("{Message} ({Failure})", message, FailureShape.Describe(ex));
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
            // Nothing entered yet; pick the first discovered deployment and let it autofill the fields.
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
                label = $"{baseLabel}  in  {deployment.SubscriptionName}";
                var i = 2;
                while (_azureModelMap.ContainsKey(label))
                {
                    label = $"{baseLabel}  in  {deployment.SubscriptionName} ({i++})";
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
        CopilotPanel.Visibility = provider == CleanupProvider.GitHubCopilot ? Visibility.Visible : Visibility.Collapsed;
        if (provider == CleanupProvider.GitHubCopilot)
        {
            var savedActive = RemoteActivityPolicy.MayContact(_committedSettings, CurrentAiDraftSettings(), RemoteActivityTrigger.WindowOpen);
            RefreshCopilotCliStatus(runVersionProbe: savedActive, allowModelList: savedActive);
        }
    }

    private FoundryLocalSetupDescription CurrentFoundrySetup()
    {
        var alias = SelectedFoundryModelAlias;
        if (_foundryOperationStatus is not null &&
            string.Equals(_foundryOperationAlias, alias, StringComparison.OrdinalIgnoreCase))
        {
            return _foundryOperationStatus;
        }

        _foundryExecutionBuilds.TryGetValue(alias, out var selected);
        var runtimeReady = _foundryExecutionBuilds.Count > 0;
        var liveStatus = SavedActiveFoundryModelMatches(alias) ? _cleanup.Status : CleanupStatus.Disabled;
        var progressText = liveStatus == _cleanup.Status ? _cleanup.StatusDetail : null;
        var stage = FoundryLocalSetup.FromCleanupStatus(liveStatus, runtimeReady, selected?.Cached == true, selected?.Loaded);
        return FoundryLocalSetup.Describe(stage, DisplayNameForFoundryAlias(alias), ModelSizeForAlias(alias), progressText);
    }

    private bool SavedActiveFoundryModelMatches(string alias) =>
        _committedSettings.EnableAiCleanup &&
        _committedSettings.AiCleanupProvider == CleanupProvider.FoundryLocal &&
        string.Equals(
            string.IsNullOrWhiteSpace(_committedSettings.AiCleanupModel) ? CleanupModelCatalog.DefaultAlias : _committedSettings.AiCleanupModel.Trim(),
            alias,
            StringComparison.OrdinalIgnoreCase);

    private AzureAiSetupState CurrentAzureSetup()
    {
        if (_azureSignInAttempts.IsBusy)
        {
            return new(AzureSetupResult.Verifying);
        }

        if (IsAzureApiKeySelected)
        {
            if (!CanVerifyAzureApiKey)
            {
                return new(AzureSetupResult.ApiKeyIncomplete, ApiKeySelected: true);
            }

            var result = _azureApiKeyOutcome.ToApiKeyResult(complete: true);
            return new(result, ApiKeySelected: true, SafeReason: _azureApiKeyOutcome.SafeMessage);
        }

        if (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal)
        {
            if (CurrentServicePrincipal is null)
            {
                return new(AzureSetupResult.ServicePrincipalIncomplete, AuthMode: AzureAuthMode.ServicePrincipal);
            }

            var result = _servicePrincipalOutcome.ToServicePrincipalResult(complete: true);
            return new(result, AuthMode: AzureAuthMode.ServicePrincipal, SafeReason: _servicePrincipalOutcome.SafeMessage);
        }

        if (!_azureConnectionKnown)
        {
            return new(AzureSetupResult.NotChecked);
        }

        if (!_azureCliInstalled)
        {
            return new(AzureSetupResult.CliMissing);
        }

        return _azureSignInStatus.IsSignedIn
            ? new(AzureSetupResult.SignedIn, Account: _azureSignInStatus.Account)
            : new(AzureSetupResult.NotSignedIn);
    }

    private CopilotSetupState CurrentCopilotSetup()
    {
        if (!_copilotChecked)
        {
            return new(CopilotSetupResult.NotChecked);
        }

        if (!_copilotCli.Found)
        {
            return new(CopilotSetupResult.ToolNotFound);
        }

        return _copilotModelsLoaded ? new(CopilotSetupResult.ModelsListed) : new(CopilotSetupResult.Installed);
    }

    private static CustomEndpointSetupState CurrentCustomSetup() => new(CustomEndpointTestResult.NotTested);

    private AiCleanupPageDescription BuildAiPageDescription() =>
        AiCleanupPageState.Describe(
            _committedSettings,
            CurrentAiDraftSettings(),
            _cleanup.Status,
            foundrySetup: CurrentFoundrySetup(),
            azureSetup: CurrentAzureSetup(),
            copilotSetup: CurrentCopilotSetup(),
            customSetup: CurrentCustomSetup(),
            savedSetupState: AiCleanupPageState.SavedSetupState(_committedSettings),
            providerSummary: AiCleanupPageState.ProviderSetupSummary(_committedSettings),
            modelName: DisplayNameForFoundryAlias(SelectedFoundryModelAlias),
            safeReason: _cleanup.StatusDetail);

    private void UpdateAiEnabledState()
    {
        var page = BuildAiPageDescription();
        ApplyAiPageDescription(page);
        AiProviderSummaryText.Text = CleanupDisclosure.SummaryFor(SelectedProvider);
        UpdateAiWritingStyleSummary();
        RefreshAiStatus();
    }

    private void ApplyAiPageDescription(AiCleanupPageDescription page)
    {
        AiCleanupDescription.Text = page.StatusLine;
        AiOffHelperText.Text = page.OffHelperText ?? string.Empty;
        AiOffHelperText.Visibility = page.OffHelperText is null ? Visibility.Collapsed : Visibility.Visible;
        var setupVisibility = page.ShowProviderSetup ? Visibility.Visible : Visibility.Collapsed;
        AiDisclosureCard.Visibility = setupVisibility;
        AiProviderCard.Visibility = setupVisibility;
        AiWritingStyleCard.Visibility = setupVisibility;
        AiAdvancedCard.Visibility = setupVisibility;
    }

    private void UpdateAiWritingStyleSummary()
    {
        if (AiWritingStyleSummary is null || AiWritingStyleBox is null)
        {
            return;
        }

        AiWritingStyleSummary.Text = string.Equals(
            NormalizePrompt(AiWritingStyleBox.Text),
            NormalizePrompt(CleanupPrompt.DefaultWritingStyle),
            StringComparison.Ordinal)
            ? "Scribe's style: clear sentences, correct punctuation, numbers as digits, and your spoken corrections applied."
            : "Your own style.";
    }

    private void ResetWritingStyleButton_Click(object sender, RoutedEventArgs e)
    {
        AiWritingStyleBox.Text = CleanupPrompt.DefaultWritingStyle;
        UpdateAiWritingStyleSummary();
    }

    // Prompt-style selector has no live side effects; the choice is applied on Save with the other
    // cleanup settings. The handler exists only because the XAML binds SelectionChanged.
    private void AiPromptStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateDictionaryGlossaryHint();

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

    private string DisplayNameForFoundryAlias(string? alias)
    {
        var key = alias?.Trim() ?? string.Empty;
        return _foundryCuratedByAlias.TryGetValue(key, out var model)
            ? model.DisplayName.Replace(" (recommended)", string.Empty, StringComparison.Ordinal)
            : string.IsNullOrWhiteSpace(key) ? "the selected model" : key;
    }

    private string? ModelSizeForAlias(string? alias)
    {
        var key = alias?.Trim() ?? string.Empty;
        var choice = FoundryModelChoices.Build(key, CleanupModelCatalog.Curated, [])
            .FirstOrDefault(item => string.Equals(item.Alias, key, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            return null;
        }

        var parts = choice.Label.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return parts.FirstOrDefault(part => part.StartsWith("about ", StringComparison.Ordinal));
    }

    private void UpdateAiModelHint()
    {
        if (AiModelHint is null)
        {
            return;
        }

        var alias = SelectedFoundryModelAlias;
        var buildNote = _foundryExecutionBuilds.TryGetValue(alias, out var option)
            ? option.ExecutionBuildLabel
            : null;

        if (!_foundryCuratedByAlias.TryGetValue(alias, out var model))
        {
            // A live catalog alias that is not curated still deserves the build note: those are
            // exactly the hardware-specific entries where CPU versus GPU is the useful signal.
            AiModelHint.Text = buildNote ?? string.Empty;
            return;
        }

        var hint = model.Hint;
        AiModelHint.Text = buildNote is null ? hint : $"{hint} {buildNote}";
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
        if (FoundryStatusRow is null)
        {
            return;
        }

        var page = BuildAiPageDescription();

        ApplyAiPageDescription(page);
        FoundryStatusRow.Show(SelectedProvider == CleanupProvider.FoundryLocal ? page.StatusRow : null);
        AzureSignInStatusRow.Show(SelectedProvider == CleanupProvider.AzureFoundry && SelectedAzureAuthMode == AzureAuthMode.AzureCli && !IsAzureApiKeySelected ? page.StatusRow : null);
        AzureVerifyStatusRow.Show(SelectedProvider == CleanupProvider.AzureFoundry && (SelectedAzureAuthMode == AzureAuthMode.ServicePrincipal || IsAzureApiKeySelected) ? page.StatusRow : null);
        CopilotStatusRow.Show(SelectedProvider == CleanupProvider.GitHubCopilot ? page.StatusRow : null);
        CustomStatusRow.Show(SelectedProvider == CleanupProvider.OpenAiCompatible ? page.StatusRow : null);
    }

    private async void FoundryStatusRow_PrimaryActionInvoked(object? sender, AiCleanupActionId e) => await InvokeFoundryActionAsync(e);

    private async void FoundryStatusRow_SecondaryActionInvoked(object? sender, AiCleanupActionId e) => await InvokeFoundryActionAsync(e);

    private async Task InvokeFoundryActionAsync(AiCleanupActionId action)
    {
        switch (action)
        {
            case AiCleanupActionId.SetUp:
            case AiCleanupActionId.TryAgain:
                await SetupFoundryLocalAsync();
                break;
            case AiCleanupActionId.DownloadAndLoad:
            case AiCleanupActionId.Load:
                await LoadFoundryModelAsync();
                break;
            case AiCleanupActionId.Unload:
                await UnloadFoundryModelAsync();
                break;
        }
    }

    private async void AzureStatusRow_PrimaryActionInvoked(object? sender, AiCleanupActionId e) => await InvokeAzureActionAsync(e);

    private async void AzureStatusRow_SecondaryActionInvoked(object? sender, AiCleanupActionId e) => await InvokeAzureActionAsync(e);

    private async Task InvokeAzureActionAsync(AiCleanupActionId action)
    {
        switch (action)
        {
            case AiCleanupActionId.CheckSignIn:
            case AiCleanupActionId.SignIn:
            case AiCleanupActionId.RefreshModels:
            case AiCleanupActionId.Verify:
                await RunAzurePrimaryActionAsync(action);
                break;
            case AiCleanupActionId.InstallAzureCli:
                AzureCliButton_Click(this, new RoutedEventArgs());
                break;
            case AiCleanupActionId.UseApiKeyInstead:
                AzureApiKeyRadio.IsChecked = true;
                _azureManualConfiguration = true;
                ApplyAzureSettingsAccess();
                UpdateAzureProjectApiKeyHint();
                AzureEndpointBox.Focus();
                break;
        }
    }

    private void CopilotStatusRow_PrimaryActionInvoked(object? sender, AiCleanupActionId e) => InvokeCopilotAction(e);

    private void CopilotStatusRow_SecondaryActionInvoked(object? sender, AiCleanupActionId e) => InvokeCopilotAction(e);

    private void InvokeCopilotAction(AiCleanupActionId action)
    {
        switch (action)
        {
            case AiCleanupActionId.InstallCopilot:
            case AiCleanupActionId.UpdateCopilot:
                CopilotInstallButton_Click(this, new RoutedEventArgs());
                break;
            case AiCleanupActionId.CheckAgain:
                RefreshCopilotCliStatus(runVersionProbe: true);
                break;
            case AiCleanupActionId.SignInCopilot:
                CopilotSignInButton_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Async handlers that await a background scan resume after this point. Touching a control,
        // or owning a dialog with a closed window, throws from an async void continuation, which
        // takes the process down rather than surfacing anywhere useful.
        _closed = true;

        // Closing mid-capture must never leave the global hook in pass-through, or the
        // push-to-talk key would stay dead until the app restarts.
        if (_capturing)
        {
            _capturing = false;
            _capture = null;
            _setHotkeyCaptureMode(false);
        }

        _cleanup.StatusChanged -= OnCleanupStatusChanged;
        _libraryVocabulary.Changed -= OnLibraryVocabularyChanged;
        if (_updates is not null)
        {
            _updates.UpdateReady -= OnUpdateReady;
        }

        // A running DispatcherTimer roots this window (and its loaded history rows) in the
        // dispatcher's timer list until the tick fires; stop it so close releases everything now.
        _infoDismissTimer?.Stop();

        // Reads still running off the UI thread finish on their own; these make sure nothing they
        // return is published to the closed window, and cancel the usage computation between steps.
        // A Start with Windows change still applying is left to finish, because its saved
        // preference must land together with the Windows change.
        _dictionaryLoad.Close();
        _libraryLoad.Close();
        _snippetLoad.Close();
        _historyLoad.Close();
        _failureLoad.Close();
        _statsLoad.Close();
        _usageLoads.Close();

        Closed -= OnClosed;
        Loaded -= RefreshStartupStatus;
        Activated -= RefreshStartupStatus;
    }

    // --- Themed dialogs / inline notifications -------------------------------------------

    /// <summary>
    /// Asks a routine question whose default answer is to go ahead, such as restoring one prompt (which
    /// nothing saves until Save, and which the box's own undo reverses), and returns true only when the user
    /// picks the primary action.
    /// </summary>
    private Task<bool> ConfirmAsync(string title, string content, string confirmText) =>
        ShowConfirmationAsync(ThemedConfirmation.Create(title, content, confirmText, cancelIsDefault: false));

    /// <summary>
    /// Confirms an action that cannot be taken back, because it deletes data or sends dictation text to a
    /// cloud provider. Cancel is the default, so Enter, or a click through without reading, does nothing.
    /// </summary>
    private Task<bool> ConfirmRiskyAsync(string title, string content, string confirmText) =>
        ShowConfirmationAsync(ThemedConfirmation.Create(title, content, confirmText, cancelIsDefault: true));

    private async Task<bool> ShowConfirmationAsync(Wpf.Ui.Controls.MessageBox dialog)
    {
        dialog.Owner = this;
        return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>
    /// Puts the report on the clipboard, swallowing the failure.
    /// </summary>
    /// <remarks>
    /// The clipboard helper returns false when another process holds the clipboard open, which is common and
    /// transient. Losing the report is bad; taking the Settings window down over it is worse.
    /// </remarks>
    private void CopyReportToClipboard(string report)
    {
        try
        {
            if (!ScribeClipboard.SetText(report))
            {
                throw new InvalidOperationException("Clipboard busy.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not copy the AI report to the clipboard ({Failure}).", FailureShape.Describe(ex));
        }
    }

    /// <summary>
    /// Offers to report one AI result to the developer, showing the exact contents first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required by Store policy 11.16, which obliges a product generating live AI content to
    /// "provide a means for users to report inappropriate content to the developer". Scribe's 0.3.12
    /// submission was rejected for its absence.
    /// </para>
    /// <para>
    /// Scribe transmits nothing here. The report is composed locally and handed to the user's mail
    /// client or clipboard, and a person decides whether to send it. That is not squeamishness: the
    /// report necessarily contains the user's own words, PRIVACY.md states that nothing leaves the
    /// device, and "fully offline" is the product's central claim. Posting it to an endpoint would
    /// make the privacy statement false in order to satisfy a policy about protecting users.
    /// </para>
    /// <para>
    /// The source text is offered as a secondary action rather than included by default, because it
    /// is the most sensitive part and the output alone is usually enough to judge.
    /// </para>
    /// </remarks>
    private async void ShowAiReportDialog(string output)
    {
        var version = UpdateService.RunningVersion;
        var provider = _settings.AiCleanupProvider.ToString();
        var model = string.IsNullOrWhiteSpace(_settings.AiCleanupModel)
            ? "(default)"
            : _settings.AiCleanupModel.Trim();

        var report = AiContentReport.Build(output, provider, model, version, DateTimeOffset.UtcNow);

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Report this AI result",
            Content =
                $"This sends a report to {AiContentReport.SupportAddress}.\n\n" +
                "Scribe does not send anything by itself. Your mail app opens with the report " +
                "below and you decide whether to send it. This is not a Microsoft Store review.\n\n" +
                "The report contains the AI result, the model that produced it, and your Scribe " +
                "version. It does not include what you originally said, your audio, or any other " +
                "dictation.\n\n" +
                "----------------------------------------\n" +
                report,
            PrimaryButtonText = "Open email",
            SecondaryButtonText = "Copy report",
            CloseButtonText = "Cancel",
            Owner = this,
        };

        var choice = await dialog.ShowDialogAsync();
        if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            var uri = AiContentReport.BuildMailtoUri(AiContentReport.BuildSubject(version), report);
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // No mail client, or the shell refused the handler. Falling back to the clipboard
                // keeps the report recoverable instead of losing the user's effort to a dead end.
                // By shape: a failed launch's message quotes the mailto: it was given, report and all.
                _log.LogWarning("Could not open a mail client for the AI report ({Failure}).", FailureShape.Describe(ex));
                CopyReportToClipboard(report);
                ShowThemedMessage(
                    "Report copied",
                    "Scribe could not open your mail app, so the report was copied to your " +
                    $"clipboard instead. Please paste it into an email to {AiContentReport.SupportAddress}.");
            }
        }
        else if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary)
        {
            CopyReportToClipboard(report);
            ShowThemedMessage(
                "Report copied",
                $"The report is on your clipboard. Please paste it into an email to {AiContentReport.SupportAddress}.");
        }
    }

    /// <summary>
    /// Shows a Fluent-themed message dialog that matches the rest of the window, replacing the
    /// dated Win32 <see cref="System.Windows.MessageBox"/>. Fire-and-forget so existing synchronous
    /// click handlers stay simple; the dialog itself is modal to this window.
    /// </summary>
    private void ShowThemedMessage(string title, string content)
    {
        var dialog = ThemedNotice.Create(title, content);
        dialog.Owner = this;
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
    // library already covers, before validating and saving the resulting rows.
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
            if (await ConfirmDictionaryOverlapAsync() && await TrySaveAsync())
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
            if (await ConfirmDictionaryOverlapAsync() && await TrySaveAsync())
            {
                Close();
            }
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    /// <summary>The overlap review is now an explicit command, never a surprise inside Save.</summary>
    private async Task<bool> ConfirmDictionaryOverlapAsync()
    {
        await Task.CompletedTask;
        return true;
    }

    private void OnLibraryVocabularyChanged(long generation) =>
        Dispatcher.BeginInvoke(new Action(async () => await TrySettlePendingWordPackSaveAsync()));

    private async Task TrySettlePendingWordPackSaveAsync()
    {
        if (!_wordPackSaveProtocol.HasPendingSave || _closed)
        {
            return;
        }

        var result = await _wordPackSaveProtocol.TrySettlePendingAsync(BuildWordPackSaveRequest(null, null, null, null, null, useVocabularyReload: true));
        ShowWordPackSaveResult(result);
    }

    private void RefreshWordPackRowsFromWorkspace()
    {
        if (_wordPackWorkspace is null)
        {
            return;
        }

        var enabled = _wordPackWorkspace.Draft.LocalState.EnabledIds;
        _updatingLibraryRows = true;
        foreach (var row in _libraryRows)
        {
            row.Enabled = enabled.Contains(row.Id);
        }
        _updatingLibraryRows = false;
        _libraryLoad.MarkSaved(LibrarySignature());
    }

    private WordPackSaveProtocolRequest BuildWordPackSaveRequest(
        IReadOnlyList<DictionaryEntry>? entries,
        IReadOnlyList<Snippet>? snippets,
        ExternalIntents? intents,
        string? dictionarySignature,
        string? snippetSignature,
        StartupRegistrationStatus? observedStartup = null,
        bool useVocabularyReload = false,
        IReadOnlyList<SnippetSubmission>? snippetSubmission = null)
    {
        var savedAiIntent = intents?.AiCleanup ?? 0;
        var savedMicrophoneIntent = intents?.Microphone ?? 0;
        return new WordPackSaveProtocolRequest(
            _wordPackWorkspace,
            payload => _settingsRepository.SaveBundle(
                _settings,
                entries,
                snippets,
                intents ?? new ExternalIntents(0, 0),
                payload),
            useVocabularyReload ? _reloadVocabulary : () => _applySettings(_settings),
            SaveDraftSignature,
            SaveDraftSignature,
            Validate: null,
            OnSettingsCommitted,
            OnWordPacksChanged);

        void OnSettingsCommitted()
        {
            // The saved and running settings, which the page's notices compare against (Try dictation): refreshed only
            // once SaveBundle has stored the document.
            _committedSettings = _settings.Clone();

            // A Save that makes Foundry Local serve the model an earlier Set up or Load was for starts a new setup, and the
            // row shows that setup's own progress and result from now on.
            if (_foundryOperationStatus is { } foundryOutcome &&
                _foundryOperationAlias is { } foundryOutcomeAlias &&
                SavedActiveFoundryModelMatches(foundryOutcomeAlias) &&
                FoundryLocalSetup.RetiredBySaveThatServesIt(foundryOutcome))
            {
                _foundryOperationStatus = null;
                _foundryOperationAlias = null;
            }

            _settingsRecovered = false;
            _savedBinding = _settings.Hotkey;
            _savedDictationOnlyBinding = _settings.DictationOnlyHotkey;

            // Only now, once the document is stored: _settings already holds the picked provider, and a
            // Save that fails keeps it there while cleanup goes on serving the saved one.
            _savedAiProvider = _settings.AiCleanupProvider;
            _externalAiCleanup.SavedThrough(savedAiIntent);
            _externalMicrophone.SavedThrough(savedMicrophoneIntent);
            if (_externalAiCleanup.NewestRevision == 0 && (AiCleanupCheck.IsChecked == true) != _settings.EnableAiCleanup)
            {
                ShowExternalAiCleanup(_settings.EnableAiCleanup);
            }

            if (_externalMicrophone.NewestRevision == 0 && ShownMicrophone != MicrophoneSelection.From(_settings))
            {
                ShowMicrophones(MicrophoneSelection.From(_settings));
            }

            if (dictionarySignature is not null && entries is not null)
            {
                _dictionaryLoad.MarkSaved(dictionarySignature);
            }

            if (snippetSignature is not null && snippets is not null)
            {
                _snippetLoad.MarkSaved(snippetSignature);
                MarkSnippetRowsSaved(snippetSubmission ?? []);
            }

            MarkProfileRowsSaved();

            if (observedStartup is not null)
            {
                _startupSwitch.Show(observedStartup);
                ShowStartupStatus(observedStartup);
            }

            UpdateAiEnabledState();
        }

        void OnWordPacksChanged()
        {
            RefreshWordPackList();
            RefreshWordPackRowsFromWorkspace();
            RefreshDictionaryStatus();
        }
    }

    private void ShowWordPackSaveResult(WordPackSaveProtocolResult result)
    {
        if (result.Message is null)
        {
            return;
        }

        var severity = result.Severity switch
        {
            WordPackSaveProtocolSeverity.Error => Wpf.Ui.Controls.InfoBarSeverity.Error,
            WordPackSaveProtocolSeverity.Warning => Wpf.Ui.Controls.InfoBarSeverity.Warning,
            _ => Wpf.Ui.Controls.InfoBarSeverity.Success,
        };
        ShowInfo(result.Message, severity);
    }

    // Validates, persists and applies the settings. Returns true on success; on a validation problem, a save error, a save
    // whose vocabulary dictation could not load yet, or a save during which the window's draft changed, it surfaces its
    // own message, leaves the window open, and returns false.
    private async Task<bool> TrySaveAsync()
    {
        // A Start with Windows change may still be saving its preference; writing the whole
        // document over it now would race it.
        if (_startupApply is { IsCompleted: false } pendingStartup)
        {
            try
            {
                await pendingStartup;
            }
            catch (Exception ex)
            {
                // The switch reports its own outcome; a fault there must not also fail this save.
                TryLog(ex, "A Start with Windows change failed while a save waited for it.");
            }
        }

        // Start with Windows is applied by its switch, never by Save. Save only adopts what Windows
        // says now, so an untouched switch cannot undo a change made in Windows Settings at the
        // next launch. Read before the grids are captured, so no await separates capturing the rows
        // from writing them.
        var observedStartup = await _startup.GetStatusAsync();
        if (_closed)
        {
            return false;
        }

        if (_wordPackSaveProtocol.HasPendingSave)
        {
            var settlement = await _wordPackSaveProtocol.TrySettlePendingAsync(BuildWordPackSaveRequest(null, null, null, null, null, useVocabularyReload: true));
            ShowWordPackSaveResult(settlement);
            if (_wordPackSaveProtocol.HasPendingSave)
            {
                return false;
            }
        }

        // Commit any in-progress grid edit first so validation sees the latest input.
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        LibraryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // Only validate and save the dictionary/snippets when the user actually changed them.
        // Pre-existing bad data in an untouched section (e.g. a duplicate entry that was loaded
        // from disk) must never block saving a change made on a different page. A section whose
        // rows have not loaded yet has no changes by definition and is passed as null.
        var dictionarySignature = DictionarySignature();
        var snippetSignature = SnippetSignature();
        var dictionaryDirty = _dictionaryLoad.HasChanges(dictionarySignature);
        var snippetsDirty = _snippetLoad.HasChanges(snippetSignature);

        // Validate the dictionary before touching anything: a duplicate spoken form would violate
        // the unique index, and the user deserves a pointer to the offending row rather than a
        // database error after half the settings were applied. Jump to the section that owns the
        // problem first; the dialog is meaningless while another page is showing.
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

        if (!ValidateSnippetAndProfileDraft())
        {
            return false;
        }

        List<Snippet>? snippets = null;
        IReadOnlyList<SnippetSubmission>? snippetSubmission = null;
        SnippetRow? duplicateSnippet = null;
        if (snippetsDirty)
        {
            snippets = BuildSnippets(out duplicateSnippet, out var submitted);
            snippetSubmission = submitted;
        }

        if (duplicateSnippet is not null)
        {
            ShowSection(SectionVoiceSnippets);
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
            ShowSection(SectionDictation);
            ShowThemedMessage(
                "Hotkey conflict",
                "The AI-cleanup and dictation-only hotkeys must use different keys or mouse buttons.");
            return false;
        }

        // A custom duration outside its range blocks the Save and focuses its box, rather than being clamped or stored.
        if (!ValidateDurationChoices())
        {
            return false;
        }

        // A tray change still waiting on a hotkey recording is newer than the switch as shown, so it is what is saved.
        var aiCleanupEnabled = _externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true);
        var azureValidation = AzureSettingsAccess.ValidateCleanup(
            enabled: aiCleanupEnabled,
            usesAzureProvider: SelectedProvider == CleanupProvider.AzureFoundry,
            signedIn: _azureSignInStatus.IsSignedIn,
            apiKey: SelectedAzureApiKey,
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
                AzureSettingsAccess.ValidationIssue.AuthenticationRequired when IsAzureApiKeySelected =>
                    "Enter an API key before enabling Microsoft Foundry cleanup with key authentication.",
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
            _azureStatusMessage = message;
            ShowThemedMessage("Microsoft Foundry is not ready", message);
            return false;
        }

        try
        {
            // A tray change still waiting on the open list is newer than what the picker shows, so it is what is saved.
            _externalMicrophone.ForSave(ShownMicrophone).ApplyTo(_settings);

            _settings.Hotkey = standardBinding;
            _settings.DictationOnlyHotkey = dictationOnlyBinding;
            _settings.ShowOverlay = OverlayCheck.IsChecked == true;
            _settings.OverlayPosition = SelectedOverlayPosition;
            _settings.UseVoiceActivityDetection = VadCheck.IsChecked == true;
            _settings.AutoStopOnSilence = AutoStopCheck.IsChecked == true;
            _settings.ApplyPostProcessing = PostCheck.IsChecked == true;
            _settings.StoreAudioHistory = StoreAudioCheck.IsChecked == true;
            _settings.ShiftEnterLineBreaks = ShiftEnterCheck.IsChecked == true;
            _settings.AccentSource = AccentSourceCheck.IsChecked == true ? AccentSource.Windows : AccentSource.Scribe;
            _settings.AddSpaceAfterDictation = SpaceAfterDictationCheck.IsChecked == true;
            // NumberBox.Value is a nullable double: a cleared box falls back to the saved value
            // rather than silently becoming 0, which here means "off/forever".
            _settings.MaxDictationMinutes =
                SelectedDurationValue(MaxDictationCombo, MaxDictationCustomBox, _settings.MaxDictationMinutes);
            _settings.ReleaseModelsAfterIdleMinutes =
                SelectedDurationValue(IdleReleaseCombo, IdleReleaseCustomBox, _settings.ReleaseModelsAfterIdleMinutes);
            _settings.HistoryRetentionDays =
                SelectedDurationValue(HistoryRetentionCombo, HistoryRetentionCustomBox, _settings.HistoryRetentionDays);
            _settings.InjectionMethod =
                ((InjectionChoice?)InjectionCombo.SelectedItem)?.Method ?? InjectionMethod.UnicodeType;
            _settings.NewlineHandling =
                ((NewlineChoice?)NewlineCombo.SelectedItem)?.Mode ?? NewlineInjectionMode.SmartFlatten;
            _settings.Profiles = BuildProfiles();
            _settings.DecodeThreads = ((ThreadChoice?)ThreadsCombo.SelectedItem)?.Value ?? _settings.DecodeThreads;
            _settings.TranscriptionModelId =
                ((TranscriptionModelChoice?)TranscriptionModelCombo.SelectedItem)?.Id ??
                TranscriptionModelCatalog.DefaultId;

            _settings.EnableAiCleanup = aiCleanupEnabled;
            _settings.AiCleanupProvider = SelectedProvider;
            _settings.AiCleanupModel =
                NullIfBlank(SelectedFoundryModelAlias) ?? CleanupModelCatalog.DefaultAlias;
            _settings.AiCleanupAzureEndpoint = NullIfBlank(AzureEndpointBox.Text);
            _settings.AiCleanupAzureDeployment = NullIfBlank(AzureDeploymentBox.Text);
            _settings.AiCleanupAzureAuthMode = SelectedAzureAuthMode;

            // One tenant setting, edited from whichever box the active mode shows, and the app registration and key only
            // for their own modes: the same projection the draft uses, so what is stored is what the page compares.
            var signIn = ShownAzureSignInFields;
            _settings.AiCleanupAzureApiKey = signIn.ApiKey;
            _settings.AiCleanupAzureTenantId = signIn.TenantId;
            _settings.AiCleanupAzureClientId = signIn.ClientId;
            _settings.AiCleanupAzureClientSecret = signIn.ClientSecret;
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
            // Blank is a real answer here: it means "this account's default model", which is why it
            // is stored as null rather than rejected on save.
            _settings.AiCleanupCopilotModel = NullIfBlank(CopilotModelCombo.Text);
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

            _settings.LaunchOnLogin = StartupPreference.Observed(observedStartup, _settings.LaunchOnLogin);

            // With the window's intents for the AI cleanup switch and the microphone, read before Saved() forgets them.
            // Once the save commits, a tray change up to the intent for its own setting is superseded. With no intent for
            // one, its stored value is kept and _settings takes it, so the window shows what is stored rather than a value
            // the settings no longer hold.
            var intents = new ExternalIntents(_externalAiCleanup.NewestRevision, _externalMicrophone.NewestRevision);
            var result = await _wordPackSaveProtocol.SaveAsync(
                BuildWordPackSaveRequest(entries, snippets, intents, dictionarySignature, snippetSignature, observedStartup, snippetSubmission: snippetSubmission));
            if (_closed)
            {
                return false;
            }

            ShowWordPackSaveResult(result);
            return result.Success;
        }
        catch (Exception ex) when (_closed)
        {
            _log.LogWarning("Settings save did not complete before the window closed ({Failure}).", FailureShape.Describe(ex));
            return false;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Constraint safety net for anything grid validation didn't anticipate; still phrased
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

    // What a Save stores, read the way the Save reads it, for the Save's check that nothing it stores changed while it
    // waited for its vocabulary generation. Values stored straight from an editor come from walking the parts of the window
    // whose controls a Save reads (the Dictation, AI cleanup and Advanced pages, and History's settings card, never the rest
    // of History, whose search box is not stored), each editor as typed. The rest are taken as the Save computes them: the
    // hotkeys with their modes, the AI cleanup switch and the microphone with any tray change still waiting, the Azure
    // subscription the deployment resolves to, the profiles, the saved dictionary and snippet row signatures, and the
    // word-pack draft content itself. No dirty flag or revision stands in for content, because Save moves those flags while
    // storing. Every value is framed by DraftSnapshot and hashed, so the copy the check keeps holds no key or secret; never
    // logged.
    private string SaveDraftSignature()
    {
        HashSet<DependencyObject> carriedElsewhere =
        [
            LaunchCheck, ModeCombo, DictationOnlyModeCombo, DeviceCombo, AiCleanupCheck, AiModelBox, AzureSubscriptionBox,
        ];
        var draft = new Scribe.Core.Vocabulary.DraftSnapshot();
        foreach (var page in new FrameworkElement[] { SectionDictation, SectionAi, SectionAdvanced, HistorySettingsCard })
        {
            draft.Part(page.Name);
            AppendEditorValues(page, carriedElsewhere, draft);
        }

        draft.Part("hotkeys")
            .Binding(_pendingBinding with { Mode = SelectedMode })
            .Binding(_pendingDictationOnlyBinding is null
                ? null
                : _pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode })
            .Flag(_capturing);
        draft.Part("intents")
            .Flag(_externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true))
            .Microphone(_externalMicrophone.ForSave(ShownMicrophone));
        draft.Part("foundryModel").Text(SelectedFoundryModelAlias);
        draft.Part("subscription").Subscription(AzureSubscriptionSelection.ResolveAuthenticationSubscription(
            _selectedAzureDeployment, SelectedAzureSubscription, AzureEndpointBox.Text, AzureDeploymentBox.Text));
        draft.Part("profiles").Profiles(BuildProfiles());
        draft.Part("libraries").LibrarySet(_libraryLoad.IsLoaded ? CollectEnabledLibraryIds() : _settings.EnabledDictionaryLibraryIds);
        WordPackDraftSignature.Write(draft, _wordPackWorkspace);
        draft.Part("rows")
            .Text(DictionarySignature())
            .Text(SnippetSignature())
            .Flag(RowEditInProgress(DictionaryGrid))
            .Flag(RowEditInProgress(LibraryGrid));
        return draft.Hash();
    }

    // Every editor's value in the page's logical tree, whether shown or not, as the Save reads it, each after its kind: an
    // editable combo box by its text, any other by its selection; a grid's rows are compared through their section instead.
    private static void AppendEditorValues(DependencyObject node, HashSet<DependencyObject> skipped, Scribe.Core.Vocabulary.DraftSnapshot draft)
    {
        if (skipped.Contains(node))
        {
            return;
        }

        switch (node)
        {
            case DataGrid:
                return;
            case Wpf.Ui.Controls.PasswordBox secret:
                draft.Part("password").Text(secret.Password);
                break;
            case Wpf.Ui.Controls.NumberBox number:
                draft.Part("number").Text(number.Text).Number(number.Value);
                break;
            case TextBox text:
                draft.Part("text").Text(text.Text);
                break;
            case PasswordBox secret:
                draft.Part("password").Text(secret.Password);
                break;
            case System.Windows.Controls.Primitives.ToggleButton toggle:
                draft.Part("toggle").Flag(toggle.IsChecked);
                break;
            case ComboBox { IsEditable: true } editable:
                draft.Part("editable").Text(editable.Text);
                break;
            case ComboBox combo:
                draft.Part("choice").Number(combo.SelectedIndex).Text(combo.Text);
                break;
            case Slider slider:
                draft.Part("slider").Number(slider.Value);
                break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject element)
            {
                AppendEditorValues(element, skipped, draft);
            }
        }
    }

    private static bool RowEditInProgress(DataGrid grid) =>
        grid.Items is IEditableCollectionView rows && (rows.IsEditingItem || rows.IsAddingNew);

    private sealed class WordPackSaveStore : IWordPackSaveProtocolStore
    {
        private readonly ILibraryCatalogStore _store;
        private readonly Action<LibrarySavePayload> _commitRepair;

        public WordPackSaveStore(ILibraryCatalogStore store, Action<LibrarySavePayload> commitRepair)
        {
            _store = store;
            _commitRepair = commitRepair;
        }

        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes) =>
            Task.Run(() => WordPackSaveSession.Prepare(_store, changes));

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared) =>
            Task.Run(() => WordPackSaveSession.Complete(_store, prepared));

        public Task<LibraryCatalog> LoadCatalogAsync() =>
            Task.Run(() => WordPackSaveSession.LoadCatalog(_store));

        public void CommitRepair(LibrarySavePayload payload) => _commitRepair(payload);
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

    // Voice snippets and app profiles live in SettingsWindow.Snippets.cs and SettingsWindow.Profiles.cs.

    // --- Recording indicator position picker -----------------------------------------------------------

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
        if (_cleanup.Recipient is { } recipient)
        {
            // The recipient is captured before the question and handed to CompleteAsync, which sends only
            // while cleanup still serves it, so the history goes where the dialog said or nowhere. Asked
            // unless both it and the saved provider run on this PC (AiRequestConsent says why both).
            if (AiRequestConsent.IsNeeded(recipient, _savedAiProvider) &&
                !await ConfirmRiskyAsync(
                    CleanupDisclosure.SuggestionConsentTitle,
                    CleanupDisclosure.SuggestionConsentFor(recipient.Provider),
                    "Send and continue"))
            {
                return;
            }

            await RunDictionarySuggestionAsync(() => SuggestWithAiAsync(current, recipient));
        }
        else
        {
            await RunDictionarySuggestionAsync(() => SuggestWithMinerAsync(current));
        }
    }

    // The history read now runs off the UI thread, so the button has to stay off for the whole run
    // or a second click would add every suggestion twice.
    private async Task RunDictionarySuggestionAsync(Func<Task> suggest)
    {
        DictionarySuggestButton.ToolTip = null;
        DictionarySuggestButton.IsEnabled = false;
        DictionarySuggestBusy.Visibility = Visibility.Visible;
        try
        {
            await suggest();
        }
        finally
        {
            if (!_closed)
            {
                DictionarySuggestBusy.Visibility = Visibility.Collapsed;
                DictionarySuggestButton.IsEnabled = true;
                DictionarySuggestButton.ToolTip =
                    "Learn vocabulary from recent dictations. A configured remote AI provider receives " +
                    "a bounded text sample only after you confirm; otherwise Scribe scans locally.";
            }
        }
    }

    private async Task SuggestWithAiAsync(IReadOnlyList<DictionaryEntry> current, CleanupRecipient recipient)
    {
        List<HistoryEntry> history;
        try
        {
            history = await Task.Run(() => _history.GetRecent(1000).ToList());
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ShowThemedMessage("Scribe", $"Could not read your history:\n{ex.Message}");
            }

            return;
        }

        // A dialog owned by a closed window throws, and this runs under an async void handler.
        if (_closed)
        {
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

        try
        {
            // The recipient the user agreed to, checked again where the request is built: a Save during the
            // history read above cannot turn this into a request to another provider.
            var completion = await _cleanup.CompleteAsync(AiDictionarySuggester.SystemPrompt, sample, recipient);
            if (_closed)
            {
                return;
            }

            if (completion.Outcome == CompletionOutcome.RecipientChanged)
            {
                ShowThemedMessage(
                    "Nothing was sent",
                    "Your AI cleanup provider changed after you agreed to send your dictations, so Scribe sent " +
                    "nothing. Press the button again to decide about the provider now in use.");
                return;
            }

            var response = completion.Text;
            if (string.IsNullOrWhiteSpace(response))
            {
                // The model was unavailable or returned nothing: fall back to the deterministic miner.
                await SuggestWithMinerAsync(current, aiRanFirst: true);
                return;
            }

            var suggestions = AiDictionarySuggester.ParseSuggestions(response, current);
            if (suggestions.Count == 0)
            {
                await SuggestWithMinerAsync(current, aiRanFirst: true);
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
            if (!_closed)
            {
                ShowThemedMessage("Scribe", $"Could not get AI suggestions:\n{ex.Message}");
            }
        }
    }

    private async Task SuggestWithMinerAsync(IReadOnlyList<DictionaryEntry> current, bool aiRanFirst = false)
    {
        IReadOnlyList<DictionaryEntry> suggestions;
        try
        {
            suggestions = await Task.Run(() => DictionaryHistoryLearner.BuildEntries(_history.GetRecent(1000), current));
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ShowThemedMessage("Scribe", $"Could not scan your history:\n{ex.Message}");
            }

            return;
        }

        if (_closed)
        {
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

    // --- Dictionary cleanup ---------------------------------------------------------------

    /// <summary>
    /// Scans dictation history for terms that have never earned their place and offers to retire
    /// them. Dead terms are not free: the enabled dictionary is rendered into the AI cleanup prompt
    /// on every dictation, capped at <see cref="CleanupPrompt.MaxGlossaryTermsLocal"/> terms for
    /// on-device models, so entries the user never says displace the ones they do.
    /// </summary>
    private async void DictionaryCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        LibraryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        // The grid is the live truth, as it is for "Learn from history". Judging the saved rows
        // instead would offer to delete an entry the user added thirty seconds ago.
        var current = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new DictionaryEntry(
                r.Id,
                r.Pattern.Trim(),
                (r.Replacement ?? string.Empty).Trim(),
                r.WholeWord,
                r.Enabled))
            .ToList();

        // Word pack cleanup moves to the Word packs tab. This review offers only the user's own entries.

        // The scan is asynchronous and its findings name specific rows, so anything the user changes
        // while it runs invalidates the result. Applying a stale verdict could turn off a rule they
        // had just edited or re-enabled.
        var dictionaryBefore = DictionarySignature();
        var librariesBefore = LibrarySignature();

        DictionaryCleanupButton.IsEnabled = false;
        DictionaryCleanupBusy.Visibility = Visibility.Visible;
        DictionaryUsageReport report;
        try
        {
            // Off the UI thread: this reads up to a thousand transcripts and runs a regex per term
            // across the lot, which is quick but not instant on a large dictionary.
            var transcripts = await Task.Run(
                () => _history.GetRecent(1000).Select(h => h.Text).ToList());
            report = await Task.Run(
                () => DictionaryUsageAnalyzer.Analyze(transcripts, current, []));
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ShowThemedMessage("Scribe", $"Could not scan your history:\n{ex.Message}");
            }

            return;
        }
        finally
        {
            if (!_closed)
            {
                DictionaryCleanupButton.IsEnabled = true;
                DictionaryCleanupBusy.Visibility = Visibility.Collapsed;
            }
        }

        // Showing a dialog owned by a closed window throws, and this handler is async void, so the
        // exception would take the process down rather than surfacing anywhere useful.
        if (_closed)
        {
            return;
        }

        if (DictionarySignature() != dictionaryBefore || LibrarySignature() != librariesBefore)
        {
            ShowInfo(
                "Your dictionary changed while the scan was running, so the results are out of date. "
                + "Run the cleanup again.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (!report.HasEnoughEvidence || !report.HasFindings)
        {
            ShowThemedMessage("Clean up unused terms", report.Summary);
            return;
        }

        var choice = DictionaryCleanupWindow.Show(this, report);
        if (choice is null)
        {
            return;
        }

        await ApplyCleanupAsync(choice);
    }

    private async Task ApplyCleanupAsync(DictionaryCleanupChoice choice)
    {
        // Match back by spoken form rather than id: the grid can hold a row that has never been
        // saved (id 0), and two of those would be indistinguishable by id.
        var patterns = new HashSet<string>(
            choice.Entries.Select(en => en.Pattern.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var targets = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern) && patterns.Contains(r.Pattern.Trim()))
            .ToList();

        // This cleanup no longer switches a word pack off or copies a word pack's terms into the dictionary.
        if (targets.Count == 0 && choice.Libraries.Count == 0)
        {
            return;
        }

        if (choice.Delete && targets.Count > 0 && !await ConfirmRiskyAsync(
                "Delete these entries?",
                $"{targets.Count} {(targets.Count == 1 ? "entry" : "entries")} will be removed from your "
                + "dictionary when you save. This cannot be undone once saved. Turning them off instead "
                + "keeps them in the list so you can switch them back on later.",
                "Delete"))
        {
            return;
        }

        if (choice.Delete)
        {
            foreach (var row in targets)
            {
                _rows.Remove(row);
            }
        }
        else
        {
            foreach (var row in targets)
            {
                row.Enabled = false;
            }
        }

        RefreshDictionaryStatus();
        ShowInfo(choice.Delete
            ? $"{targets.Count} {(targets.Count == 1 ? "entry" : "entries")} removed. Review the change, then save to apply it."
            : $"{targets.Count} {(targets.Count == 1 ? "entry" : "entries")} turned off. Review the change, then save to apply it.");
    }

    // --- History --------------------------------------------------------------------------

    private async void LoadHistory()
    {
        if (!_historyLoad.TryBegin(null, out var ticket))
        {
            return;
        }

        IReadOnlyList<HistoryEntry> entries;
        try
        {
            entries = await Task.Run(() => _history.GetRecent(200));
        }
        catch (Exception ex)
        {
            if (_historyLoad.Fail(ticket))
            {
                TryLog(ex, "Could not load dictation history for Settings.");
                HistoryEmptyHint.Text = "Couldn't load your history. Close Settings and open it again to retry.";
                HistoryEmptyHint.Visibility = Visibility.Visible;
            }

            return;
        }

        if (!_historyLoad.Publish(ticket, string.Empty))
        {
            return;
        }

        _historyRows.Clear();
        foreach (var entry in entries)
        {
            var row = HistoryRow.From(entry);

            // A rating still being written was read back before it landed; show the new value.
            _historyRows.Add(_ratingWrites.IsPending(row.Id)
                ? row with { Rating = _ratingWrites.Resolve(row.Id, row.Rating), RatingPending = true }
                : row);
        }

        var hasRows = _historyRows.Count > 0;
        HistoryEmptyHint.Text = _historyEmptyText;
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

    /// <summary>
    /// Recomputes the usage page for the selected period.
    /// </summary>
    /// <remarks>
    /// Period changes, refresh clicks and dictionary adds can arrive faster than a full history read
    /// and analysis completes. At most one computation runs; a request made meanwhile waits as the
    /// only pending one, replacing any older pending request, and the running one is cancelled at its
    /// next step because its answer is already stale. Only the newest request may publish, whether
    /// it succeeded or failed, and nothing publishes after the window closes.
    /// </remarks>
    private void LoadUsage()
    {
        if (_closed)
        {
            return;
        }

        var period = UsagePeriodBox.SelectedItem as UsagePeriodChoice ?? UsagePeriodChoice.All[1];
        UsageCoverageText.Text = "Calculating from local history…";

        // The shown snapshot no longer matches the request, so the insight button must not send it.
        _usageSnapshot = null;
        _usageLibraryScope = AiVocabularyScope.None;
        RefreshUsageInsightAvailability();

        if (_usageLoads.Submit(period) is { } work)
        {
            RunUsageLoads(work);
        }
    }

    private async void RunUsageLoads(CoalescedRequest<UsagePeriodChoice> first)
    {
        for (var work = first; work is not null; work = _usageLoads.Complete(work))
        {
            UsageReport.Result? result = null;
            Exception? failure = null;
            try
            {
                var request = work;
                var now = DateTimeOffset.UtcNow;
                result = await Task.Run(
                    () =>
                    {
                        // The library service is a vocabulary source, so the report takes its library entries and its
                        // shareable labels from one Current snapshot of that service and ignores these ids (contract
                        // 3.3.6). The ids, those of the committed vocabulary dictation runs on (the libraries the settings
                        // in use enable, never the window's unsaved switches), are what UsageReport reads a service that
                        // is not a source by, as release 0.4.4 read the libraries, sharing no library label.
                        var vocabulary = _libraryVocabulary.Current;
                        return UsageReport.Build(
                            _history,
                            _dictionary,
                            _libraries,
                            [.. vocabulary.AiScope.PermittedLibraryIds],
                            request.Value.Days,
                            now,
                            request.Cancellation);
                    },
                    request.Cancellation);
            }
            catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
            {
                // Superseded by a newer request, or the window closed. Nothing to show.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (!_usageLoads.IsCurrent(work))
            {
                continue;
            }

            try
            {
                if (result is not null)
                {
                    ShowUsage(work.Value, result);
                }
                else if (failure is not null)
                {
                    ShowUsageFailure(failure);
                }
            }
            catch (Exception ex)
            {
                TryLog(ex, "Could not show usage insights.");
            }
        }
    }

    private void ShowUsage(UsagePeriodChoice period, UsageReport.Result result)
    {
        _usageSnapshot = result.Snapshot;
        _usageLibraryScope = result.LibraryScope;
        var snapshot = _usageSnapshot;

        UsageCoverageText.Text = result.PeriodCapped
            ? $"{period.Label}, based on the latest {UsageReport.HistoryLimit:N0} retained dictations."
            : $"{period.Label}, {_usageSnapshot.Dictations:N0} retained dictation" +
              (_usageSnapshot.Dictations == 1 ? "." : "s.");
        UsageDictationsText.Text = snapshot.Dictations.ToString("N0");
        UsageWordsText.Text = snapshot.Words.ToString("N0");
        UsageActiveDaysText.Text = snapshot.ActiveDays.ToString("N0");
        UsageSpeechText.Text = FormatDuration(snapshot.Speech);
        UsageAverageText.Text = snapshot.AverageWords.ToString("0.#");
        UsageAppsGrid.ItemsSource = snapshot.TopApps.Select(app => new UsageAppRow(app)).ToList();

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

        static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
            ? $"{duration.TotalHours:0.#} hr"
            : $"{duration.TotalMinutes:0.#} min";

        static string FormatUsageTerm(UsageAnalyzer.TermUsage term) =>
            $"{term.Text} ({term.Dictations:N0} dictation{(term.Dictations == 1 ? string.Empty : "s")})";
    }

    private void ShowUsageFailure(Exception failure)
    {
        _usageSnapshot = null;
        _usageLibraryScope = AiVocabularyScope.None;
        UsageCoverageText.Text = "Usage is temporarily unavailable.";
        UsageInsightText.Text = failure.Message;
        UsageInsightButton.IsEnabled = false;
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

            // Only what is stored goes live. After a Save that failed, _settings still holds every edit it was given, a
            // picked AI cleanup provider among them, and applying it would send later dictations there with nothing
            // saved. The stored settings rebuild the post-processor and the glossary with the new entry; when none can be
            // used, only the vocabulary reloads. Either way the entry is said to be added once dictation can use it.
            var reapplied = StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary);
            var refresh = await reapplied.Vocabulary;
            if (_closed)
            {
                return;
            }

            if (refresh.Applied)
            {
                ShowInfo($"Added \"{term.Text}\" to your dictionary.");
            }
            else
            {
                ShowInfo(
                    Scribe.Core.Vocabulary.VocabularyNotice.SavedButNotApplied($"Added \"{term.Text}\" to your dictionary"),
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }

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

        // Taken with the snapshot, so the summary goes under the scope of the report it was built from.
        var libraryScope = _usageLibraryScope;

        if (_cleanup.Recipient is not { } recipient)
        {
            UsageInsightText.Text = "Configure a ready AI cleanup model to generate an insight.";
            return;
        }

        _usageInsightRunning = true;
        RefreshUsageInsightAvailability();
        UsageInsightText.Text = "Generating insight…";
        try
        {
            // Every request, the first attempt and each retry, goes only while the report's library scope is still
            // permitted and its libraries' content is unchanged.
            var completion = await _cleanup.CompleteAsync(
                UsageInsight.SystemPrompt,
                UsageInsight.BuildSummary(snapshot),
                recipient,
                libraryScope);
            if (!InsightStillApplies(snapshot))
            {
                return;
            }

            UsageInsightText.Text = completion.Outcome switch
            {
                ScopedCompletionOutcome.RecipientChanged when completion.NothingSent =>
                    "Your AI cleanup provider changed before the insight was requested, so nothing was sent. Try again.",

                // A later attempt was stopped after an earlier one had gone, so this must not say nothing was sent.
                ScopedCompletionOutcome.RecipientChanged or ScopedCompletionOutcome.NotReady =>
                    "AI cleanup changed while the insight was being requested, so it was stopped. Try again.",
                ScopedCompletionOutcome.LibraryScopeNarrowed =>
                    "Usage changed while the insight was being prepared. Generate it again.",
                _ => UsageInsight.Parse(completion.Text) ?? "The configured model did not return an insight.",
            };
        }
        catch (Exception ex)
        {
            if (!InsightStillApplies(snapshot))
            {
                return;
            }

            UsageInsightText.Text = $"Insight failed: {ex.Message}";
        }
        finally
        {
            _usageInsightRunning = false;
            if (!_closed)
            {
                RefreshUsageInsightAvailability();
            }
        }
    }

    // An insight describes the snapshot it was asked about. Once the page has moved to another
    // period or been refreshed, showing it would describe numbers that are no longer on screen.
    private bool InsightStillApplies(UsageAnalyzer.Snapshot asked)
    {
        if (_closed)
        {
            return false;
        }

        if (ReferenceEquals(_usageSnapshot, asked))
        {
            return true;
        }

        if (_usageSnapshot is null)
        {
            UsageInsightText.Text =
                "Usage changed while the insight was being generated. Generate it again once the page has updated.";
        }

        return false;
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

    private void HistoryThumbUp_Click(object sender, RoutedEventArgs e) =>
        RateHistoryRow(sender, AiRating.Useful);

    private void HistoryThumbDown_Click(object sender, RoutedEventArgs e) =>
        RateHistoryRow(sender, AiRating.NotUseful);

    /// <summary>
    /// Records an opinion about one AI result, or clears it when the same thumb is pressed twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stays local. This is a quality signal about a rewrite, never a rating of Scribe: Store policy
    /// requires an in-app rating OF THE APP to route to the Store's own mechanism regardless of
    /// sentiment, and treats sending positive sentiment to the Store while keeping negative
    /// sentiment private as a fraudulent practice. The Store rating action lives in About, is
    /// unconditional, and is deliberately not wired to these buttons.
    /// </para>
    /// <para>
    /// The write runs off the UI thread and the row shows the new rating at once. The row's thumbs
    /// stay off until the write finishes, the row is found again by id afterwards because the list
    /// may have been reloaded, and a failed write puts the old rating back.
    /// </para>
    /// </remarks>
    private async void RateHistoryRow(object sender, AiRating rating)
    {
        if (sender is not FrameworkElement { Tag: long id })
        {
            return;
        }

        var index = IndexOfHistoryRow(id);
        if (index < 0)
        {
            return;
        }

        // Pressing the same thumb again clears it, so a misclick is undoable without a second
        // control explaining itself.
        var row = _historyRows[index];
        var next = row.Rating == rating ? AiRating.Unrated : rating;
        if (!_ratingWrites.TryBegin(id, row.Rating, next))
        {
            return;
        }

        _historyRows[index] = row with { Rating = next, RatingPending = true };

        var saved = false;
        try
        {
            await Task.Run(() => _history.SetAiRating(id, next));
            saved = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save the rating for history entry {Id} ({Failure}).", id, FailureShape.Describe(ex));
        }

        var shown = _ratingWrites.Complete(id, saved);
        if (_closed)
        {
            return;
        }

        var current = IndexOfHistoryRow(id);
        if (current >= 0)
        {
            _historyRows[current] = _historyRows[current] with { Rating = shown, RatingPending = false };
        }

        // A refresh still running may have read this row before the write landed. It would publish
        // the old rating now that nothing is pending, so it is restarted to read the new one.
        if (saved && _historyLoad.Invalidate())
        {
            LoadHistory();
        }

        if (!saved)
        {
            ShowInfo("Couldn't save that rating. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// Opens a report for one AI result. Composes it, shows the user exactly what it contains, and
    /// leaves the sending to them.
    /// </summary>
    private void HistoryReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long id })
        {
            return;
        }

        var index = IndexOfHistoryRow(id);
        if (index < 0)
        {
            return;
        }

        ShowAiReportDialog(_historyRows[index].Text);
    }

    private int IndexOfHistoryRow(long id)
    {
        for (var i = 0; i < _historyRows.Count; i++)
        {
            if (_historyRows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void HistoryCopyButton_Click(object sender, RoutedEventArgs e) => CopyHistoryText();

    private void CopyHistoryText()
    {
        if (SelectedHistory is not { } row)
        {
            return;
        }

        try
        {
            var copied = ScribeClipboard.SetText(row.Text);
            ShowInfo(
                copied ? "Copied the selected dictation." : "Couldn't copy the dictation. Try again.",
                copied ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("Couldn't copy the dictation. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
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
            if (_closed)
            {
                return;
            }

            LoadHistory();
            ShowInfo("Deleted the selected history entry.");
        }
        catch
        {
            if (_closed)
            {
                return;
            }

            ShowInfo("Couldn't delete the history entry. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
            UpdateHistorySelection();
        }
    }

    private async void HistoryClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRows.Count == 0 ||
            !await ConfirmRiskyAsync(
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
            if (_closed)
            {
                return;
            }

            LoadHistory();
            ShowInfo("Cleared dictation history.");
        }
        catch
        {
            if (_closed)
            {
                return;
            }

            ShowInfo("Couldn't clear history. Try again.", Wpf.Ui.Controls.InfoBarSeverity.Error);
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
    /// Merges imported entries into the grid (not the database; the save button owns persistence,
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Reads a NumberBox into an int setting: a cleared box keeps the previously saved value
    /// (0 is a deliberate "off/forever" choice, so it must never be the accident of an empty
    /// field), and anything typed past the range is clamped rather than rejected.
    /// </summary>
    private static int ClampNumberBox(double? value, int fallback, int max) =>
        value is null ? Math.Clamp(fallback, 0, max) : Math.Clamp((int)Math.Round(value.Value), 0, max);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    // These records back ComboBoxes that use DisplayMemberPath, which sets what is drawn but not
    // what is announced: without a ToString override a screen reader reads the record's default
    // representation ("ProviderChoice { Provider = FoundryLocal, Label = ... }") instead of the
    // option, so the lists are unusable by ear. The microphone picker's entries (MicrophoneChoice,
    // built in Core) do the same.
    private sealed record InjectionChoice(InjectionMethod Method, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record NewlineChoice(NewlineInjectionMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ProviderChoice(CleanupProvider Provider, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PromptStyleChoice(CleanupPromptStyle Style, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record UsagePeriodChoice(int? Days, string Label)
    {
        public override string ToString() => Label;

        public static IReadOnlyList<UsagePeriodChoice> All { get; } =
        [
            new(7, "Last 7 days"),
            new(30, "Last 30 days"),
            new(90, "Last 90 days"),
            new(null, "All retained history"),
        ];
    }

    // Every DataGrid row type in this window compares by reference, never by value, which is why the records
    // below override Equals and the Core records are copied into the row classes after them. When a grid's
    // items are replaced, WPF reuses an old row's automation peer for any new item that merely Equals an old
    // one (ItemsControlAutomationPeer.GetChildrenCore, ItemAutomationPeer.ReuseForItem), but the reused peer
    // keeps the cell peers it made for the old item, and those hold that item only weakly
    // (DataGridItemAutomationPeer.GetOrCreateCellItemPeer, DataGridCellItemAutomationPeer). A reload builds
    // equal new rows, so once the old ones were collected every cell was announced as
    // "Item: , Column Display Index: 0", had no bounds, and focus inside the grid went unannounced.
    private sealed record UsageTrendRow(
        string Period,
        int Dictations,
        int Words,
        double RelativeHeight)
    {
        public string ToolTip =>
            $"{Period}: {Dictations:N0} dictation{(Dictations == 1 ? string.Empty : "s")}, " +
            $"{Words:N0} word{(Words == 1 ? string.Empty : "s")}";

        // UI Automation names a trend bar and a trend row after ToString(); a record's lists every field.
        public override string ToString() => ToolTip;

        public bool Equals(UsageTrendRow? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>One row of the Top apps grid, copied from Core's <see cref="UsageAnalyzer.AppUsage"/> record.</summary>
    private sealed class UsageAppRow(UsageAnalyzer.AppUsage app)
    {
        public string Name { get; } = app.Name;

        public int Dictations { get; } = app.Dictations;

        public int Words { get; } = app.Words;

        public override string ToString() => Name;
    }

    /// <summary>One editable row of the selected word pack.</summary>
    private sealed class LibraryTermRow(long rowId, TermValues values) : INotifyPropertyChanged
    {
        private string _pattern = values.Spoken;
        private string _replacement = values.Written;
        private bool _wholeWord = values.WholeWord;
        private bool _enabled = values.Enabled;

        public long RowId { get; } = rowId;

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

        public override string ToString() => $"Spoken {Pattern}, written {Replacement}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private sealed record UsageTermRow(string Text, int Dictations)
    {
        public string DictationLabel =>
            $"({Dictations:N0} dictation{(Dictations == 1 ? string.Empty : "s")})";

        public override string ToString() => Text;
    }

    private sealed record RecentlyDeletedRow(RecentlyDeletedLibrary Entry)
    {
        public string Display =>
            $"{Entry.Name}, Imported, {Entry.TermCount:N0} {(Entry.TermCount == 1 ? "word" : "words")}, deleted {Entry.DeletedUtc.LocalDateTime:g}";
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

        // The check box and badge cells have no text of their own, so UI Automation names them after
        // ToString(), which would otherwise read out this type's name. A row just added has no spoken
        // form yet, and is named the way its check boxes name it.
        public override string ToString() => string.IsNullOrWhiteSpace(Pattern) ? "New entry" : Pattern;

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
        /// <summary>
        /// Whether AI cleanup actually ran for this dictation. The thumbs and the report only apply
        /// to generative output: a dictation that was merely transcribed, or that had the user's own
        /// dictionary applied, is not AI-generated content and reporting it as such would be noise.
        /// </summary>
        public bool ProducedByAi { get; init; }

        /// <summary>What the user said about the result, if anything.</summary>
        public AiRating Rating { get; init; } = AiRating.Unrated;

        /// <summary>A rating write for this row is still running; its thumbs stay off until it lands.</summary>
        public bool RatingPending { get; init; }

        public bool CanRate => !RatingPending;

        /// <summary>Filled glyph when chosen, outline when not. Segoe MDL2 Assets.</summary>
        public string ThumbUpGlyph => Rating == AiRating.Useful ? "" : "";

        public string ThumbDownGlyph => Rating == AiRating.NotUseful ? "" : "";

        // The thumbs draw only a glyph, so these name them for UI Automation: each says what its ToolTip says,
        // and adds whether it is the rating given, which the filled glyph shows.
        public string ThumbUpName =>
            Rating == AiRating.Useful ? "This rewrite was useful, selected" : "This rewrite was useful";

        public string ThumbDownName =>
            Rating == AiRating.NotUseful ? "This rewrite was not useful, selected" : "This rewrite was not useful";

        /// <summary>The report path opens only on a thumbs-down, which is where it is wanted.</summary>
        public bool CanReport => ProducedByAi && Rating == AiRating.NotUseful;

        // UI Automation names the row after ToString(), and so the Result cell too, which holds buttons rather
        // than text. A record's ToString lists every field.
        public override string ToString() => $"{When}, {Text}";

        // Compared by reference, not by value: see the note above UsageTrendRow.
        public bool Equals(HistoryRow? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        public static HistoryRow From(HistoryEntry entry) => new(
            entry.Id,
            entry.TimestampUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
            entry.Text,
            string.IsNullOrWhiteSpace(entry.TargetApp) ? HistoryRowFormat.NotApplicable : entry.TargetApp!,
            HistoryRowFormat.Audio(entry.AudioMilliseconds),
            HistoryRowFormat.Latency(entry.DecodeMilliseconds),
            HistoryRowFormat.Latency(entry.CleanupMilliseconds))
        {
            ProducedByAi = entry.CleanupMilliseconds is > 0,
            Rating = entry.AiRating,
        };
    }

    /// <summary>
    /// Library row backing the word packs grid. It notifies so staged changes update the workspace and the grid follows
    /// a Save's committed state.
    /// </summary>
    public sealed class LibraryRow : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _aiCleanup;
        private int? _matchCount;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Terms { get; set; }

        /// <summary>Where the word pack comes from, "Built-in" or "Imported": the secondary line under its name.</summary>
        public string Source { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }
        public string DisplaySource => MatchCount is { } count ? $"{Source}, {count:N0} {(count == 1 ? "match" : "matches")}" : Source;
        public int? MatchCount
        {
            get => _matchCount;
            set
            {
                if (_matchCount != value)
                {
                    _matchCount = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySource)));
                }
            }
        }

        /// <summary>What UI Automation reads for the name cell, which shows both lines.</summary>
        public string AccessibleName => $"{Name}, {Source}";

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                }
            }
        }

        public bool AiCleanup
        {
            get => _aiCleanup;
            set
            {
                if (_aiCleanup != value)
                {
                    _aiCleanup = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiCleanup)));
                }
            }
        }

        // A check box cell has no text of its own, so UI Automation names the focused cell after
        // ToString(), which would otherwise read out this type's name.
        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Editable snippet row behind the master-detail editor. Phrase raises change notifications so
    /// the ListBox label tracks edits made in the detail pane.
    /// </summary>
    public sealed class SnippetRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _phrase = string.Empty;
        private string _template = string.Empty;
        private bool _enabled = true;

        public long Id { get; set; }
        public DraftRowOrigin Origin { get; set; } = DraftRowOrigin.New;
        public bool Touched { get; set; }
        public string? LoadedPhrase { get; set; }
        public string? LoadedTemplate { get; set; }
        public bool LoadedEnabled { get; set; } = true;
        public string RowKey => Id > 0 ? Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"new:{GetHashCode()}";

        public string Phrase
        {
            get => _phrase;
            set
            {
                if (_phrase != value)
                {
                    _phrase = value;
                    Notify(nameof(Phrase));
                    Notify(nameof(PrimaryText));
                }
            }
        }

        public string Template
        {
            get => _template;
            set
            {
                if (_template != value)
                {
                    _template = value;
                    Notify(nameof(Template));
                }
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    Notify(nameof(Enabled));
                    Notify(nameof(SecondaryText));
                    Notify(nameof(HasSecondaryText));
                }
            }
        }

        public string PrimaryText => SnippetListText.Describe(Phrase, Enabled).Primary;
        public string SecondaryText => SnippetListText.Describe(Phrase, Enabled).Secondary ?? string.Empty;
        public bool HasSecondaryText => !string.IsNullOrEmpty(SecondaryText);

        public override string ToString() => PrimaryText;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Notify(string propertyName) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public sealed class ProfileRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _processes = string.Empty;

        public DraftRowOrigin Origin { get; set; } = DraftRowOrigin.New;
        public bool Touched { get; set; }
        public string? LoadedName { get; set; }
        public string? LoadedProcesses { get; set; }
        public string? LoadedWritingStyle { get; set; }
        public NewlineInjectionMode? LoadedNewlineHandling { get; set; }
        public string RowKey => Origin == DraftRowOrigin.Saved ? $"saved:{LoadedName}:{LoadedProcesses}" : $"new:{GetHashCode()}";

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    Notify(nameof(Name));
                    Notify(nameof(PrimaryText));
                }
            }
        }

        public string Processes
        {
            get => _processes;
            set
            {
                if (_processes != value)
                {
                    _processes = value;
                    Notify(nameof(Processes));
                    Notify(nameof(SecondaryText));
                }
            }
        }

        public string WritingStyle { get; set; } = string.Empty;
        public NewlineInjectionMode? NewlineHandling { get; set; }
        public string PrimaryText => ProfileListText.Describe(Name, Processes).Primary;
        public string SecondaryText => ProfileListText.Describe(Name, Processes).Secondary;

        public override string ToString() => PrimaryText;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Notify(string propertyName) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public sealed class FailureRow
    {
        public string When { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;

        // UI Automation names a row after ToString(), which would otherwise read out this type's name.
        public override string ToString() => $"{When}, {Model}, {Reason}";
    }
}
