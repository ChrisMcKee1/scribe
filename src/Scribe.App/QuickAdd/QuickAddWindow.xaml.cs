using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Scribe.App.Infrastructure;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Wpf.Ui.Controls;

namespace Scribe.App.QuickAdd;

/// <summary>
/// The tray's Add to dictionary window. Core builds the states and messages; this class binds them to WPF.
/// </summary>
public partial class QuickAddWindow : FluentWindow
{
    private const int WmDpiChanged = 0x02E0;

    private readonly Func<IReadOnlyList<DictionaryEntry>> _loadExisting;
    private readonly Func<DictionaryEntry, DictionaryEntry> _persist;
    private readonly QuickAddWindowOptions _options;
    private readonly ILogger? _logger;
    private readonly ObservableCollection<WordChip> _chips = new();
    private readonly List<TranscriptSource> _sources;
    private readonly System.Windows.Threading.DispatcherTimer _announcementTimer;

    private QuickAddVocabulary _vocabulary;
    private bool _referencesAvailable = true;
    private string _transcript = string.Empty;
    private IReadOnlyList<QuickDictionaryAdd.Token> _tokens = [];
    private int _first = -1;
    private int _last = -1;
    private int _anchor = -1;
    private int _focusIndex = -1;
    private bool _dragging;
    private bool _syncingChips;
    private bool _keyboardFocusInWords;
    private bool _allowClose;
    private bool _closePromptActive;
    private bool _dirtySinceSave;
    private bool _suppressFieldChanged;
    private string? _fixedTranscript;
    private string? _pendingAnnouncementText;
    private QuickDictionaryAdd.Plan? _lastPlan;
    private QuickDictionaryAdd.Plan _currentPlan;

    public sealed record QuickAddWindowOptions(
        bool AiCleanupEnabled = false,
        bool DictionaryEnabled = true,
        bool SettingsOpen = false,
        Func<QuickAddVocabulary>? LoadVocabulary = null,
        Func<IEnumerable<string>>? PendingSettingsSpokenForms = null,
        Action? OpenAdvancedSettings = null,
        Action<string>? ShowInSettings = null,
        Action<string>? AddItInSettings = null,
        Action<string>? FixInstead = null);

    public readonly record struct QuickAddResult(
        DictionaryEntry Entry,
        string? SourceTranscript,
        string? CorrectedTranscript);

    public event Action<QuickAddResult>? Saved;

    public QuickAddWindow(
        IReadOnlyList<string> recentTranscripts,
        Func<IReadOnlyList<DictionaryEntry>> loadExisting,
        Func<DictionaryEntry, DictionaryEntry> persist,
        ILogger? logger = null,
        QuickAddWindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(recentTranscripts);
        _loadExisting = loadExisting ?? throw new ArgumentNullException(nameof(loadExisting));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _logger = logger;
        _options = options ?? new QuickAddWindowOptions();
        _vocabulary = ReadVocabulary();
        _currentPlan = new QuickDictionaryAdd.Plan(QuickDictionaryAdd.PlanKind.Empty, null, string.Empty, QuickDictionaryAdd.PlanSeverity.None);

        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        InitializeComponent();
        ApplyWindowFit();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);

        _announcementTimer = new System.Windows.Threading.DispatcherTimer { Interval = QuickAddAnnouncement.AnnouncementDelay };
        _announcementTimer.Tick += (_, _) =>
        {
            _announcementTimer.Stop();
            var text = _pendingAnnouncementText;
            _pendingAnnouncementText = null;
            Announce(StatusText, text);
        };

        WordsList.ItemsSource = _chips;
        AiVocabularyText.Text = CleanupDisclosure.AddToDictionaryVocabularyLine;
        AiVocabularyText.Visibility = _options.AiCleanupEnabled ? Visibility.Visible : Visibility.Collapsed;
        DictionaryOffText.Text = _options.AiCleanupEnabled
            ? "Your dictionary and snippets are turned off, so Scribe doesn't replace any words with them. AI cleanup still receives your vocabulary when this is off."
            : "Your dictionary and snippets are turned off, so Scribe saves words here but doesn't use them.";
        DictionaryOffNotice.Visibility = _options.DictionaryEnabled ? Visibility.Collapsed : Visibility.Visible;
        if (!_options.DictionaryEnabled)
        {
            Loaded += (_, _) => Announce(DictionaryOffNotice, DictionaryOffText.Text);
        }

        PreviewMouseLeftButtonUp += (_, _) => FinishDrag();
        Closing += QuickAddWindow_Closing;

        _sources = recentTranscripts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new TranscriptSource(t))
            .ToList();

        if (_sources.Count == 0)
        {
            SourcePanel.Visibility = Visibility.Collapsed;
            NoTranscriptHint.Visibility = Visibility.Visible;
            Loaded += (_, _) => HeardBox.Focus();
        }
        else
        {
            RecentPicker.ItemsSource = _sources;
            RecentPicker.DisplayMemberPath = nameof(TranscriptSource.Preview);
            RecentPicker.SelectedIndex = 0;
            Loaded += (_, _) => FocusWordsAtInitialWord();
        }

        UpdateStatus(forceAnnouncement: false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(ApplyWindowFit);
        }

        return IntPtr.Zero;
    }

    private void ApplyWindowFit()
    {
        var area = WindowPlacement.WorkAreaFor(this);
        var fit = WindowFit.Compute(560, 640, 440, 460, area, Left, Top);
        MinWidth = fit.MinWidth;
        MinHeight = fit.MinHeight;
        Width = fit.Width;
        Height = fit.Height;
        Left = fit.Left;
        Top = fit.Top;
    }

    private void RecentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentPicker.SelectedItem is TranscriptSource source)
        {
            LoadTranscript(source.Text);
        }
    }

    private void LoadTranscript(string transcript)
    {
        _transcript = transcript;
        _tokens = QuickDictionaryAdd.Tokenize(transcript);
        _anchor = -1;
        _first = -1;
        _last = -1;
        _focusIndex = _tokens.Count > 0 ? 0 : -1;
        _dragging = false;
        _fixedTranscript = null;
        if (SavedDetailText is not null)
        {
            SavedDetailText.Visibility = Visibility.Collapsed;
        }

        if (CopyFixedButton is not null)
        {
            CopyFixedButton.Visibility = Visibility.Collapsed;
        }

        _chips.Clear();
        for (var i = 0; i < _tokens.Count; i++)
        {
            _chips.Add(new WordChip { Text = _tokens[i].Text, Index = i, PositionInSet = i + 1, SizeOfSet = _tokens.Count });
        }

        if (HeardBox is not null)
        {
            RunWithoutFieldChanged(() => HeardBox.Text = string.Empty);
        }

        UpdateFocusedChip();
        UpdateStatus(forceAnnouncement: false);
    }

    private void Chip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleButton { Tag: int index })
        {
            return;
        }

        e.Handled = true;
        _keyboardFocusInWords = false;
        _focusIndex = index;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0)
        {
            _dragging = true;
            ApplySelection(_anchor, index, announce: false);
            return;
        }

        ToggleAt(index, announce: false);
    }

    private void ToggleAt(int index, bool announce)
    {
        var previous = CurrentRange;
        var next = QuickDictionaryAdd.Toggle(previous, index);
        _focusIndex = index;
        _anchor = AnchorAfterToggle(previous, next, index);
        _dragging = true;

        if (next.IsEmpty)
        {
            ClearSelection(announce);
            return;
        }

        ApplySelection(next.First, next.Last, announce);
    }

    private static int AnchorAfterToggle(QuickDictionaryAdd.WordRange previous, QuickDictionaryAdd.WordRange next, int focus)
    {
        if (next.IsEmpty || previous.IsEmpty)
        {
            return focus;
        }

        return next.First == previous.First ? next.First : next.Last;
    }

    private void Chip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag();
            return;
        }

        if (sender is ToggleButton { Tag: int index } && _anchor >= 0)
        {
            _focusIndex = index;
            ApplySelection(_anchor, index, announce: false);
        }
    }

    private void FinishDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (_first >= 0)
        {
            FocusCorrection();
        }
    }

    private void Chip_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingChips || sender is not ToggleButton { Tag: int index })
        {
            return;
        }

        ToggleAt(index, announce: true);
        _dragging = false;
        if (_first >= 0)
        {
            FocusCorrection();
        }
    }

    private void WordsList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _keyboardFocusInWords = true;
        if (_focusIndex < 0)
        {
            _focusIndex = CurrentRange.IsEmpty ? 0 : CurrentRange.First;
            _anchor = _focusIndex;
        }

        UpdateFocusedChip();
        UpdateHint();
    }

    private void WordsList_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!WordsList.IsKeyboardFocusWithin)
        {
            _keyboardFocusInWords = false;
            _focusIndex = -1;
            UpdateFocusedChip();
            UpdateHint();
        }
    }

    private void FocusWordsAtInitialWord()
    {
        if (_chips.Count == 0)
        {
            HeardBox.Focus();
            return;
        }

        _keyboardFocusInWords = true;
        _focusIndex = CurrentRange.IsEmpty ? 0 : CurrentRange.First;
        _anchor = _focusIndex;
        UpdateFocusedChip();
        Dispatcher.BeginInvoke(() => FindChipButton(_focusIndex)?.Focus());
        UpdateHint();
    }

    private void Chip_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ToggleButton { Tag: int index })
        {
            _keyboardFocusInWords = true;
            _focusIndex = index;
            UpdateFocusedChip();
            UpdateHint();
        }
    }

    private void Chip_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!WordsList.IsKeyboardFocusWithin)
        {
            _keyboardFocusInWords = false;
            _focusIndex = -1;
            UpdateFocusedChip();
            UpdateHint();
        }
    }

    private void WordsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = ToChipKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (key == ChipKey.Other)
        {
            return;
        }

        e.Handled = true;
        var state = new ChipKeyboardState(_focusIndex, _anchor, CurrentRange, ComputeRows());
        var result = ChipKeyboard.Apply(state, key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), _chips.Count);
        ApplyKeyboardState(result.State);
        if (result.Outcome == ChipKeyboardOutcome.MoveToWrites)
        {
            FocusCorrection();
        }
    }

    private void ApplyKeyboardState(ChipKeyboardState state)
    {
        var oldRange = CurrentRange;
        _focusIndex = state.FocusIndex;
        _anchor = state.AnchorIndex;
        if (state.Selection.IsEmpty)
        {
            ClearSelection(announce: !oldRange.IsEmpty);
        }
        else
        {
            ApplySelection(state.Selection.First, state.Selection.Last, announce: state.Selection != oldRange);
        }

        UpdateFocusedChip();
        FindChipButton(_focusIndex)?.Focus();
        BringFocusedChipIntoView();
    }

    private static ChipKey ToChipKey(Key key) => key switch
    {
        Key.Left => ChipKey.Left,
        Key.Right => ChipKey.Right,
        Key.Up => ChipKey.Up,
        Key.Down => ChipKey.Down,
        Key.Home => ChipKey.Home,
        Key.End => ChipKey.End,
        Key.Space => ChipKey.Space,
        Key.Enter => ChipKey.Enter,
        _ => ChipKey.Other,
    };

    private IReadOnlyList<int> ComputeRows()
    {
        var rows = new int[_chips.Count];
        var rowByTop = new List<double>();
        for (var i = 0; i < _chips.Count; i++)
        {
            if (FindChipButton(i) is not { } button)
            {
                rows[i] = i == 0 ? 0 : rows[i - 1];
                continue;
            }

            var top = button.TransformToAncestor(WordsList).Transform(new Point(0, 0)).Y;
            var row = rowByTop.FindIndex(y => Math.Abs(y - top) < 4);
            if (row < 0)
            {
                row = rowByTop.Count;
                rowByTop.Add(top);
            }

            rows[i] = row;
        }

        return rows;
    }

    private ToggleButton? FindChipButton(int index)
    {
        if (index < 0 || index >= _chips.Count)
        {
            return null;
        }

        if (WordsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
        {
            return null;
        }

        return FindDescendant<ToggleButton>(container);
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void ApplySelection(int first, int last, bool announce)
    {
        if (first > last)
        {
            (first, last) = (last, first);
        }

        _first = first;
        _last = last;
        _syncingChips = true;
        try
        {
            foreach (var chip in _chips)
            {
                chip.IsSelected = chip.Index >= first && chip.Index <= last;
            }
        }
        finally
        {
            _syncingChips = false;
        }

        HeardBox.Text = QuickDictionaryAdd.Select(_transcript, _tokens, first, last);
        _dirtySinceSave = true;
        if (announce)
        {
            Announce(WordsList, string.IsNullOrWhiteSpace(HeardBox.Text) ? "Nothing selected" : $"Selected: {HeardBox.Text}");
        }

        UpdateFocusedChip();
        UpdateStatus(forceAnnouncement: false);
    }

    private void ClearSelection(bool announce)
    {
        _first = -1;
        _last = -1;
        _syncingChips = true;
        try
        {
            foreach (var chip in _chips)
            {
                chip.IsSelected = false;
            }
        }
        finally
        {
            _syncingChips = false;
        }

        HeardBox.Text = string.Empty;
        _dirtySinceSave = true;
        if (announce)
        {
            Announce(WordsList, "Nothing selected");
        }

        UpdateFocusedChip();
        UpdateStatus(forceAnnouncement: false);
    }

    private void UpdateFocusedChip()
    {
        foreach (var chip in _chips)
        {
            chip.IsFocusedChip = chip.Index == _focusIndex;
        }
    }

    private void BringFocusedChipIntoView() => FindChipButton(_focusIndex)?.BringIntoView();

    private void FocusCorrection()
    {
        ShouldBeBox.Focus();
        ShouldBeBox.CaretIndex = ShouldBeBox.Text.Length;
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFieldChanged)
        {
            return;
        }

        if (RemoveBox is not null && ShouldBeBox is not null)
        {
            ShouldBeBox.IsEnabled = RemoveBox.IsChecked != true;
            ShouldBeBox.PlaceholderText = RemoveBox.IsChecked == true ? "(removes these words)" : "For example, Copilot";
        }

        if (!_syncingChips && sender == HeardBox)
        {
            var selectedText = CurrentRange.IsEmpty ? string.Empty : QuickDictionaryAdd.Select(_transcript, _tokens, CurrentRange.First, CurrentRange.Last);
            var reconciled = QuickAddSelection.Reconcile(HeardBox.Text, selectedText, CurrentRange);
            if (reconciled != CurrentRange)
            {
                if (reconciled.IsEmpty)
                {
                    _first = -1;
                    _last = -1;
                    _syncingChips = true;
                    try
                    {
                        foreach (var chip in _chips) chip.IsSelected = false;
                    }
                    finally
                    {
                        _syncingChips = false;
                    }
                }
            }
        }

        _dirtySinceSave = true;
        _fixedTranscript = null;
        if (SavedDetailText is not null)
        {
            SavedDetailText.Visibility = Visibility.Collapsed;
        }

        if (CopyFixedButton is not null)
        {
            CopyFixedButton.Visibility = Visibility.Collapsed;
        }

        UpdateStatus(forceAnnouncement: false);
    }


    private void RunWithoutFieldChanged(Action action)
    {
        _suppressFieldChanged = true;
        try
        {
            action();
        }
        finally
        {
            _suppressFieldChanged = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            if (SaveButton.IsEnabled)
            {
                Save(closeAfterSaving: false);
            }

            return;
        }

        if (e.Key == Key.Escape && !IsOpenDropDown(e.OriginalSource))
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.Enter && IsSaveButtonSource(e.OriginalSource))
        {
            e.Handled = true;
        }
    }
    private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
        }
    }

    private void UpdateStatus(bool forceAnnouncement)
    {
        if (StatusText is null || SaveButton is null || SaveCloseButton is null)
        {
            return;
        }

        var previous = _lastPlan;
        var plan = BuildPlan();
        _currentPlan = plan;
        StatusText.Text = plan.Message;
        StatusActionButton.Visibility = plan.Action == QuickDictionaryAdd.PlanAction.None ? Visibility.Collapsed : Visibility.Visible;
        StatusActionButton.Content = ActionText(plan);
        StatusIcon.Visibility = plan.Severity == QuickDictionaryAdd.PlanSeverity.None ? Visibility.Collapsed : Visibility.Visible;
        StatusIcon.Symbol = SymbolFor(plan.Severity);
        StatusIcon.SetResourceReference(ForegroundProperty, BrushFor(plan.Severity));

        SaveButton.IsEnabled = plan.CanSave;
        SaveButton.IsDefault = false;
        SaveCloseButton.IsEnabled = plan.CanSave;
        SaveButton.Content = plan.IsRemoval ? "Remove from dictation_s" : "_Save";
        SaveCloseButton.Content = plan.IsRemoval ? "Remove _and close" : "Save _and close";
        UpdateHint();

        var timing = forceAnnouncement
            ? QuickAddAnnouncementTiming.Immediate
            : QuickAddAnnouncement.ShouldAnnounce(previous, plan);
        _lastPlan = plan;
        if (timing == QuickAddAnnouncementTiming.Immediate)
        {
            _announcementTimer.Stop();
            _pendingAnnouncementText = null;
            Announce(StatusText, plan.Message);
        }
        else if (timing == QuickAddAnnouncementTiming.Delayed)
        {
            ScheduleStatusAnnouncement(plan.Message);
        }
        else if (_announcementTimer.IsEnabled)
        {
            ScheduleStatusAnnouncement(_pendingAnnouncementText ?? plan.Message);
        }
    }

    private void ScheduleStatusAnnouncement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _pendingAnnouncementText = text;
        _announcementTimer.Stop();
        _announcementTimer.Start();
    }

    private void UpdateHint()
    {
        if (HintText is null)
        {
            return;
        }

        HintText.Text = QuickAddHint.For(CurrentRange, _keyboardFocusInWords, _sources.Count > 0, !_referencesAvailable);
    }

    private QuickDictionaryAdd.Plan BuildPlan() => QuickDictionaryAdd.Build(CurrentRequest(), _vocabulary);

    private QuickDictionaryAdd.QuickAddRequest CurrentRequest() => new(
        HeardBox?.Text,
        ShouldBeBox?.Text,
        RemoveBox?.IsChecked == true,
        WholeWordBox?.IsChecked == true,
        _transcript,
        _options.DictionaryEnabled,
        _referencesAvailable);

    private static SymbolRegular SymbolFor(QuickDictionaryAdd.PlanSeverity severity) => severity switch
    {
        QuickDictionaryAdd.PlanSeverity.Success => SymbolRegular.CheckmarkCircle24,
        QuickDictionaryAdd.PlanSeverity.Warning => SymbolRegular.Warning24,
        QuickDictionaryAdd.PlanSeverity.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };

    private static string BrushFor(QuickDictionaryAdd.PlanSeverity severity) => severity switch
    {
        QuickDictionaryAdd.PlanSeverity.Success => "SystemFillColorSuccessBrush",
        QuickDictionaryAdd.PlanSeverity.Warning => "SystemFillColorCautionBrush",
        QuickDictionaryAdd.PlanSeverity.Error => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    };

    private static string? ActionText(QuickDictionaryAdd.Plan plan) => plan.Action switch
    {
        QuickDictionaryAdd.PlanAction.ShowInSettings => "Show in Settings",
        QuickDictionaryAdd.PlanAction.FixInstead => string.IsNullOrWhiteSpace(plan.ActionTarget) ? "Fix instead" : $"Fix \"{plan.ActionTarget}\" instead",
        QuickDictionaryAdd.PlanAction.AddItInSettings => "Add it in Settings",
        QuickDictionaryAdd.PlanAction.CopyFixedDictation => "Copy fixed dictation",
        QuickDictionaryAdd.PlanAction.TryAgain => "Try again",
        _ => null,
    };

    private void StatusActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_currentPlan.Action)
        {
            case QuickDictionaryAdd.PlanAction.TryAgain:
                _vocabulary = ReadVocabulary();
                UpdateStatus(forceAnnouncement: true);
                break;
            case QuickDictionaryAdd.PlanAction.ShowInSettings:
                _options.ShowInSettings?.Invoke(_currentPlan.ActionTarget ?? HeardBox.Text.Trim());
                break;
            case QuickDictionaryAdd.PlanAction.FixInstead:
                if (!string.IsNullOrWhiteSpace(_currentPlan.ActionTarget))
                {
                    HeardBox.Text = _currentPlan.ActionTarget;
                    ShouldBeBox.Clear();
                    HeardBox.Focus();
                }

                _options.FixInstead?.Invoke(_currentPlan.ActionTarget ?? string.Empty);
                break;
            case QuickDictionaryAdd.PlanAction.AddItInSettings:
                _options.AddItInSettings?.Invoke(HeardBox.Text.Trim());
                break;
            case QuickDictionaryAdd.PlanAction.CopyFixedDictation:
                CopyFixedTranscript();
                break;
        }
    }


    private static bool IsOpenDropDown(object originalSource) =>
        FindAncestor<ComboBox>(originalSource as DependencyObject) is { IsDropDownOpen: true };

    private bool IsSaveButtonSource(object originalSource)
    {
        var button = FindAncestor<System.Windows.Controls.Button>(originalSource as DependencyObject);
        return ReferenceEquals(button, SaveButton) || ReferenceEquals(button, SaveCloseButton);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static void MakeKeepEditingDefault(Wpf.Ui.Controls.MessageBox dialog)
    {
        var primary = MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Primary);
        var close = MessageBoxTemplate.FindButton(dialog, Wpf.Ui.Controls.MessageBoxButton.Close);
        if (primary is null || close is null)
        {
            return;
        }

        primary.IsDefault = false;
        close.IsDefault = true;
        close.IsCancel = true;
        close.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Save(closeAfterSaving: false);

    private void SaveCloseButton_Click(object sender, RoutedEventArgs e) => Save(closeAfterSaving: true);

    private void Save(bool closeAfterSaving)
    {
        _vocabulary = ReadVocabulary();
        var plan = BuildPlan();
        if (!plan.CanSave || plan.Entry is null)
        {
            UpdateStatus(forceAnnouncement: true);
            return;
        }

        DictionaryEntry saved;
        try
        {
            saved = _persist(plan.Entry);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Quick add failed to save the dictionary entry: {Failure}", FailureShape.DescribeWithStack(ex));
            ShowResult(QuickDictionaryAdd.SaveFailed());
            return;
        }

        var savedRequest = CurrentRequest();
        var sourceTranscript = _transcript;
        var corrected = QuickDictionaryAdd.Apply(sourceTranscript, saved);
        var fixedTranscript = string.Equals(corrected, sourceTranscript, StringComparison.Ordinal) ? null : corrected;
        Saved?.Invoke(new QuickAddResult(saved, sourceTranscript, fixedTranscript));
        if (closeAfterSaving)
        {
            _allowClose = true;
            Close();
            return;
        }

        foreach (var source in _sources)
        {
            if (string.Equals(source.Text, sourceTranscript, StringComparison.Ordinal))
            {
                source.Update(corrected);
            }
        }

        _vocabulary = ReadVocabulary();
        var scrollOffset = TranscriptScroll.VerticalOffset;
        LoadTranscript(corrected);
        RunWithoutFieldChanged(() =>
        {
            ShouldBeBox.Clear();
            RemoveBox.IsChecked = false;
        });
        _fixedTranscript = fixedTranscript;
        _dirtySinceSave = false;
        SavedDetailText.Visibility = fixedTranscript is null ? Visibility.Collapsed : Visibility.Visible;
        CopyFixedButton.Visibility = fixedTranscript is null ? Visibility.Collapsed : Visibility.Visible;
        TranscriptScroll.ScrollToVerticalOffset(scrollOffset);
        ShowResult(QuickDictionaryAdd.Saved(savedRequest));
        FocusWordsAtInitialWord();
    }

    private void ShowResult(QuickDictionaryAdd.Plan result)
    {
        _currentPlan = result;
        _lastPlan = result;
        StatusText.Text = result.Message;
        StatusIcon.Visibility = result.Severity == QuickDictionaryAdd.PlanSeverity.None ? Visibility.Collapsed : Visibility.Visible;
        StatusIcon.Symbol = SymbolFor(result.Severity);
        StatusIcon.SetResourceReference(ForegroundProperty, BrushFor(result.Severity));
        StatusActionButton.Visibility = result.Action == QuickDictionaryAdd.PlanAction.CopyFixedDictation && _fixedTranscript is not null ? Visibility.Visible : Visibility.Collapsed;
        StatusActionButton.Content = ActionText(result);
        Announce(StatusText, result.Message);
    }

    private void CopyFixedButton_Click(object sender, RoutedEventArgs e) => CopyFixedTranscript();

    private void CopyFixedTranscript()
    {
        if (_fixedTranscript is null)
        {
            return;
        }

        ShowResult(ScribeClipboard.SetText(_fixedTranscript)
            ? QuickDictionaryAdd.CopySucceeded()
            : QuickDictionaryAdd.CopyFailed());
    }

    private void OpenAdvancedButton_Click(object sender, RoutedEventArgs e) => _options.OpenAdvancedSettings?.Invoke();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void QuickAddWindow_Closing(object? sender, CancelEventArgs e)
    {
        _announcementTimer.Stop();
        _pendingAnnouncementText = null;
        if (_allowClose || _closePromptActive || !HasUnsavedSavableCorrection())
        {
            return;
        }

        e.Cancel = true;
        _closePromptActive = true;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var prompt = QuickAddClosePrompt.ForUnsavedWord();
                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = prompt.Title,
                    Content = prompt.Body,
                    PrimaryButtonText = prompt.PrimaryButton,
                    SecondaryButtonText = prompt.DiscardButton,
                    CloseButtonText = prompt.CancelButton,
                    PrimaryButtonAppearance = ControlAppearance.Primary,
                    CloseButtonAppearance = ControlAppearance.Secondary,
                };
                dialog.Loaded += (_, _) => MakeKeepEditingDefault(dialog);
                var choice = await dialog.ShowDialogAsync();
                if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    Save(closeAfterSaving: true);
                }
                else if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary)
                {
                    _allowClose = true;
                    Close();
                }
            }
            finally
            {
                _closePromptActive = false;
            }
        });
    }

    private bool HasUnsavedSavableCorrection() => _dirtySinceSave && BuildPlan().CanSave;

    private QuickAddVocabulary ReadVocabulary()
    {
        try
        {
            _referencesAvailable = true;
            if (_options.LoadVocabulary is { } loadVocabulary)
            {
                return loadVocabulary();
            }

            var personal = _loadExisting();
            var pending = _options.PendingSettingsSpokenForms?.Invoke() ?? [];
            return QuickAddVocabulary.Compose(personal, pending, Array.Empty<DictionaryLibrary>(), Array.Empty<string>());
        }
        catch
        {
            _referencesAvailable = false;
            return QuickAddVocabulary.Compose(Array.Empty<DictionaryEntry>(), Array.Empty<string>(), Array.Empty<DictionaryLibrary>(), Array.Empty<string>());
        }
    }

    private QuickDictionaryAdd.WordRange CurrentRange => _first < 0 ? QuickDictionaryAdd.WordRange.None : new QuickDictionaryAdd.WordRange(_first, _last);

    private void Announce(UIElement source, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AnnouncementText.Text = text;
        try
        {
            var peer = UIElementAutomationPeer.FromElement(AnnouncementText)
                ?? UIElementAutomationPeer.CreatePeerForElement(AnnouncementText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch
        {
            // UI Automation notifications are best effort for assistive technology.
        }
    }

    private sealed class TranscriptSource(string text) : INotifyPropertyChanged
    {
        public string Text { get; private set; } = text;
        public string Preview => LastTranscriptStore.FormatPreview(Text, maxLength: 64);

        public void Update(string text)
        {
            Text = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Preview;
    }

    private sealed class WordChip : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isFocusedChip;

        public required string Text { get; init; }
        public required int Index { get; init; }
        public required int PositionInSet { get; init; }
        public required int SizeOfSet { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public bool IsFocusedChip
        {
            get => _isFocusedChip;
            set
            {
                if (_isFocusedChip == value) return;
                _isFocusedChip = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocusedChip)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Text;
    }
}








