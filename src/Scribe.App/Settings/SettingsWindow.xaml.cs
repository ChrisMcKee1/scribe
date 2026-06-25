using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scribe.App.Infrastructure;
using Scribe.Core.Audio;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

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
    private readonly ITextCleanupService _cleanup;
    private readonly IAzureFoundryDiscovery _azureDiscovery;
    private readonly Action<AppSettings> _applySettings;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<DictionaryRow> _rows = new();
    private readonly ObservableCollection<AzureFoundryDeployment> _azureDeployments = new();
    private IReadOnlyList<DictionaryEntry> _originalEntries = Array.Empty<DictionaryEntry>();

    private HotkeyBinding _pendingBinding;
    private readonly HashSet<Key> _heldModifiers = new();
    private bool _capturing;
    private bool _finalized;
    private bool _loadingUi;

    public SettingsWindow(
        ISettingsRepository settingsRepository,
        IAudioCaptureService audio,
        IDictionaryRepository dictionary,
        ITextCleanupService cleanup,
        IAzureFoundryDiscovery azureDiscovery,
        Action<AppSettings> applySettings)
    {
        _settingsRepository = settingsRepository;
        _audio = audio;
        _dictionary = dictionary;
        _cleanup = cleanup;
        _azureDiscovery = azureDiscovery;
        _applySettings = applySettings;

        _settings = settingsRepository.Load();
        _pendingBinding = _settings.Hotkey;

        // Match the system light/dark theme + accent colour and enable the Mica backdrop.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        InitializeComponent();
        PopulateDevices();
        PopulateChoices();
        LoadFromSettings();
        LoadDictionary();

        // Reflect live cleanup-engine state (download progress, ready, errors) in the UI.
        _cleanup.StatusChanged += OnCleanupStatusChanged;
        Closed += OnClosed;
        RefreshAiStatus();
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
    }

    private void LoadFromSettings()
    {
        _loadingUi = true;
        try
        {
            HotkeyBox.Text = HotkeyCapture.Describe(_pendingBinding);
            ModeCombo.SelectedIndex = _pendingBinding.Mode == HotkeyMode.Toggle ? 1 : 0;

            OverlayCheck.IsChecked = _settings.ShowOverlay;
            VadCheck.IsChecked = _settings.UseVoiceActivityDetection;
            PostCheck.IsChecked = _settings.ApplyPostProcessing;
            LaunchCheck.IsChecked = _settings.LaunchOnLogin;
            StoreAudioCheck.IsChecked = _settings.StoreAudioHistory;
            BeamSearchCheck.IsChecked = _settings.UseHighAccuracyDecoding;

            var items = (InjectionChoice[])InjectionCombo.ItemsSource;
            InjectionCombo.SelectedItem =
                items.FirstOrDefault(i => i.Method == _settings.InjectionMethod) ?? items[0];

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
            new ProviderChoice(CleanupProvider.AzureFoundry, "Azure AI Foundry — your Azure sign-in"),
        };

        AiModelCombo.DisplayMemberPath = nameof(CleanupModel.DisplayName);
        AiModelCombo.ItemsSource = CleanupModelCatalog.Curated;

        AzureDeploymentCombo.DisplayMemberPath = nameof(AzureFoundryDeployment.DisplayName);
        AzureDeploymentCombo.ItemsSource = _azureDeployments;

        var providers = (ProviderChoice[])AiProviderCombo.ItemsSource;
        AiProviderCombo.SelectedItem =
            providers.FirstOrDefault(p => p.Provider == _settings.AiCleanupProvider) ?? providers[0];

        var model = CleanupModelCatalog.Curated
            .FirstOrDefault(m => string.Equals(m.Alias, _settings.AiCleanupModel, StringComparison.OrdinalIgnoreCase));
        AiModelCombo.SelectedItem = model ?? CleanupModelCatalog.Curated[0];

        // Manual endpoint/deployment/key are the source of truth Save reads; discovery just autofills
        // them. Populate from saved settings (key is decrypted in memory by AppSettings).
        AzureEndpointBox.Text = _settings.AiCleanupAzureEndpoint ?? string.Empty;
        AzureDeploymentBox.Text = _settings.AiCleanupAzureDeployment ?? string.Empty;
        AzureApiKeyBox.Password = _settings.AiCleanupAzureApiKey ?? string.Empty;
        AzureTenantBox.Text = _settings.AiCleanupAzureTenantId ?? string.Empty;

        // Open Advanced automatically when manual auth is configured, so an override isn't hidden away.
        AzureAdvancedExpander.IsExpanded =
            !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureApiKey) ||
            !string.IsNullOrWhiteSpace(_settings.AiCleanupAzureTenantId);

        // Reflect the saved deployment in the Model picker before any sign-in discovery runs.
        SeedAzureModelFromSettings();

        // Show the effective writing style: the user's saved guidance, or the default when blank so
        // they can see and edit exactly what gets sent to the model.
        AiWritingStyleBox.Text = CleanupPrompt.ResolveWritingStyle(_settings.AiCleanupWritingStyle);

        UpdateAiProviderPanels();
        UpdateAiEnabledState();
        UpdateAiModelHint();
        UpdateAzureDeploymentHint();
    }

    private void LoadDictionary()
    {
        _originalEntries = _dictionary.GetAll();
        foreach (var entry in _originalEntries)
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
    }

    private void AiModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        UpdateAiModelHint();
    }

    private void AzureDeploymentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        // A discovered deployment autofills the manual fields, which are what Save persists.
        if (AzureDeploymentCombo.SelectedItem is AzureFoundryDeployment deployment)
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

    private async void AzureRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        AzureRefreshButton.IsEnabled = false;
        AzureStatusText.Text = "Signing in and listing your Azure deployments…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var tenantId = NullIfBlank(AzureTenantBox.Text);
            var deployments = await Task.Run(() => _azureDiscovery.DiscoverAsync(tenantId, cts.Token), cts.Token);

            var previous = AzureDeploymentCombo.SelectedItem as AzureFoundryDeployment;
            SetAzureDeployments(
                deployments,
                preferEndpoint: NullIfBlank(AzureEndpointBox.Text) ?? previous?.Endpoint,
                preferDeployment: NullIfBlank(AzureDeploymentBox.Text) ?? previous?.DeploymentName);

            AzureStatusText.Text = deployments.Count == 0
                ? "No chat-model deployments were found in your Azure subscriptions."
                : $"Found {deployments.Count} deployment(s). Choose one to use for cleanup.";
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

    private void SetAzureDeployments(
        IReadOnlyList<AzureFoundryDeployment> deployments, string? preferEndpoint, string? preferDeployment)
    {
        _azureDeployments.Clear();
        foreach (var deployment in deployments)
        {
            _azureDeployments.Add(deployment);
        }

        AzureFoundryDeployment? match = null;
        if (!string.IsNullOrWhiteSpace(preferDeployment))
        {
            match = _azureDeployments.FirstOrDefault(d =>
                string.Equals(d.DeploymentName, preferDeployment, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(preferEndpoint) ||
                 string.Equals(d.Endpoint, preferEndpoint, StringComparison.OrdinalIgnoreCase)));
        }

        if (match is not null)
        {
            AzureDeploymentCombo.SelectedItem = match;
        }
        else if (string.IsNullOrWhiteSpace(preferEndpoint) && string.IsNullOrWhiteSpace(preferDeployment)
                 && _azureDeployments.Count > 0)
        {
            // Nothing entered yet — pick the first discovered deployment and let it autofill the fields.
            AzureDeploymentCombo.SelectedIndex = 0;
        }
        else
        {
            // Keep the user's manually entered endpoint/deployment; don't overwrite with an unrelated match.
            AzureDeploymentCombo.SelectedItem = null;
        }

        UpdateAzureDeploymentHint();
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

        _azureDeployments.Clear();
        _azureDeployments.Add(current);
        AzureDeploymentCombo.SelectedItem = current;
    }

    private void UpdateAiProviderPanels()
    {
        if (FoundryPanel is null || AzurePanel is null)
        {
            return;
        }

        var azure = SelectedProvider == CleanupProvider.AzureFoundry;
        FoundryPanel.Visibility = azure ? Visibility.Collapsed : Visibility.Visible;
        AzurePanel.Visibility = azure ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAiEnabledState()
    {
        var on = AiCleanupCheck.IsChecked == true;
        AiProviderCombo.IsEnabled = on;
        FoundryPanel.IsEnabled = on;
        AzurePanel.IsEnabled = on;
        AiWritingStyleBox.IsEnabled = on;
        ResetWritingStyleButton.IsEnabled = on;
    }

    private void ResetWritingStyleButton_Click(object sender, RoutedEventArgs e) =>
        AiWritingStyleBox.Text = CleanupPrompt.DefaultWritingStyle;

    private void UpdateAiModelHint()
    {
        if (AiModelHint is null)
        {
            return;
        }

        AiModelHint.Text = (AiModelCombo.SelectedItem as CleanupModel)?.Hint ?? string.Empty;
    }

    private void UpdateAzureDeploymentHint()
    {
        if (AzureDeploymentHint is null)
        {
            return;
        }

        AzureDeploymentHint.Text = AzureDeploymentCombo.SelectedItem is AzureFoundryDeployment deployment
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

    // --- Save / cancel -------------------------------------------------------------------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = (DeviceChoice?)DeviceCombo.SelectedItem;
            _settings.InputDeviceId = device?.Id;
            _settings.InputDeviceName = device?.Id is null ? null : StripDefaultSuffix(device.Name);

            _settings.Hotkey = _pendingBinding with { Mode = SelectedMode };
            _settings.ShowOverlay = OverlayCheck.IsChecked == true;
            _settings.UseVoiceActivityDetection = VadCheck.IsChecked == true;
            _settings.ApplyPostProcessing = PostCheck.IsChecked == true;
            _settings.LaunchOnLogin = LaunchCheck.IsChecked == true;
            _settings.StoreAudioHistory = StoreAudioCheck.IsChecked == true;
            _settings.UseHighAccuracyDecoding = BeamSearchCheck.IsChecked == true;
            _settings.InjectionMethod =
                ((InjectionChoice?)InjectionCombo.SelectedItem)?.Method ?? InjectionMethod.UnicodeType;
            _settings.DecodeThreads = (int)ThreadsSlider.Value;

            _settings.EnableAiCleanup = AiCleanupCheck.IsChecked == true;
            _settings.AiCleanupProvider = SelectedProvider;
            _settings.AiCleanupModel =
                (AiModelCombo.SelectedItem as CleanupModel)?.Alias ?? CleanupModelCatalog.DefaultAlias;
            _settings.AiCleanupAzureEndpoint = NullIfBlank(AzureEndpointBox.Text);
            _settings.AiCleanupAzureDeployment = NullIfBlank(AzureDeploymentBox.Text);
            _settings.AiCleanupAzureApiKey = NullIfBlank(AzureApiKeyBox.Password);
            _settings.AiCleanupAzureTenantId = NullIfBlank(AzureTenantBox.Text);

            // Persist the writing style only when it differs from the default; storing blank for the
            // default keeps users tracking future improvements to the built-in guidance.
            var writingStyle = AiWritingStyleBox.Text?.Trim() ?? string.Empty;
            _settings.AiCleanupWritingStyle =
                writingStyle.Length == 0 || writingStyle == CleanupPrompt.DefaultWritingStyle
                    ? string.Empty
                    : writingStyle;

            PersistDictionary();
            _settingsRepository.Save(_settings);
            StartupRegistration.Set(_settings.LaunchOnLogin);
            _applySettings(_settings);

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save settings:\n{ex.Message}", "Scribe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PersistDictionary()
    {
        // Commit any in-progress grid edit so the bound row reflects the latest input.
        DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var keptIds = new HashSet<long>();
        foreach (var row in _rows)
        {
            if (string.IsNullOrWhiteSpace(row.Pattern))
            {
                continue; // skip blank placeholder / incomplete rows
            }

            var pattern = row.Pattern.Trim();
            var replacement = (row.Replacement ?? string.Empty).Trim();

            if (row.Id == 0)
            {
                _dictionary.Add(new DictionaryEntry(0, pattern, replacement, row.WholeWord, row.Enabled));
            }
            else
            {
                keptIds.Add(row.Id);
                _dictionary.Update(new DictionaryEntry(row.Id, pattern, replacement, row.WholeWord, row.Enabled));
            }
        }

        foreach (var entry in _originalEntries)
        {
            if (!keptIds.Contains(entry.Id))
            {
                _dictionary.Delete(entry.Id);
            }
        }
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

    private sealed record ProviderChoice(CleanupProvider Provider, string Label);

    /// <summary>Editable dictionary row backing the grid. Parameterless ctor enables grid add-row.</summary>
    public sealed class DictionaryRow
    {
        public long Id { get; set; }
        public string Pattern { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public bool WholeWord { get; set; } = true;
        public bool Enabled { get; set; } = true;
    }
}
