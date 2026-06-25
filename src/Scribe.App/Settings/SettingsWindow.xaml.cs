using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scribe.App.Infrastructure;
using Scribe.Core.Audio;
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
    private readonly Action<AppSettings> _applySettings;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<DictionaryRow> _rows = new();
    private IReadOnlyList<DictionaryEntry> _originalEntries = Array.Empty<DictionaryEntry>();

    private HotkeyBinding _pendingBinding;
    private readonly HashSet<Key> _heldModifiers = new();
    private bool _capturing;
    private bool _finalized;

    public SettingsWindow(
        ISettingsRepository settingsRepository,
        IAudioCaptureService audio,
        IDictionaryRepository dictionary,
        Action<AppSettings> applySettings)
    {
        _settingsRepository = settingsRepository;
        _audio = audio;
        _dictionary = dictionary;
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

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record DeviceChoice(string? Id, string Name);

    private sealed record InjectionChoice(InjectionMethod Method, string Label);

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
