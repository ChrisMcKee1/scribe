using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.App.QuickAdd;

/// <summary>
/// The tray's quick "Add to dictionary" popup.
///
/// It exists because the slow part of fixing a misrecognition was never the dictionary grid, it was
/// getting there (tray, settings, dictionary page, new row) and then retyping the wrong word from
/// memory. A pattern with a typo in it silently never fires and gives the user no feedback saying
/// why, so the spoken side is filled in by clicking the recognizer's own output instead.
///
/// All of the decisions live in <see cref="QuickDictionaryAdd"/>; this class is the surface.
/// </summary>
public partial class QuickAddWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Func<IReadOnlyList<DictionaryEntry>> _loadExisting;
    private readonly Func<DictionaryEntry, DictionaryEntry> _persist;
    private readonly ILogger? _logger;
    private readonly ObservableCollection<WordChip> _chips = new();

    private IReadOnlyList<DictionaryEntry> _existing;
    private string _transcript = string.Empty;
    private IReadOnlyList<QuickDictionaryAdd.Token> _tokens = [];

    // The word range currently lit, or -1 when nothing is selected. Authoritative: chip visuals are
    // written from it and never read back, so a toggle arriving from assistive tech cannot corrupt it.
    private int _first = -1;
    private int _last = -1;

    // -1 means "no chip has been clicked yet", so a shift-click has nothing to extend from.
    private int _anchor = -1;
    private bool _dragging;

    // Guards the chip toggle handler while the selection itself is writing IsSelected on every chip.
    private bool _syncingChips;

    /// <summary>
    /// What a successful save produced: the stored entry, plus the transcript it was built from and
    /// that transcript rewritten by the new rule. The host uses the pair to repair the retained copy
    /// in place, so "copy last dictation" hands back the corrected wording rather than the mistake.
    /// <see cref="CorrectedTranscript"/> is null when the rule changed nothing in that transcript.
    /// </summary>
    public readonly record struct QuickAddResult(
        DictionaryEntry Entry,
        string? SourceTranscript,
        string? CorrectedTranscript);

    /// <summary>
    /// Raised after an entry has been written. The host uses this to reload the post-processor:
    /// without that the new rule sits in the database and does nothing until the next settings save.
    /// </summary>
    public event Action<QuickAddResult>? Saved;

    /// <param name="recentTranscripts">Finalized dictations, most recent first.</param>
    /// <param name="loadExisting">
    /// Reads the dictionary the popup should check duplicates against. This is a delegate rather
    /// than the repository because an open settings window holds unsaved rows that the database
    /// does not know about yet, and the host has to be free to resolve that at save time.
    /// </param>
    /// <param name="persist">
    /// Writes one entry and returns it with its assigned id. Also a delegate, because writing
    /// straight to the repository while the settings window is open gets the row deleted by that
    /// window's next save.
    /// </param>
    /// <param name="logger">
    /// Optional. A save failure shows the user a plain sentence, so the exception detail has to land
    /// somewhere or the only report of a broken dictionary write is "it didn't work".
    /// </param>
    public QuickAddWindow(
        IReadOnlyList<string> recentTranscripts,
        Func<IReadOnlyList<DictionaryEntry>> loadExisting,
        Func<DictionaryEntry, DictionaryEntry> persist,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(recentTranscripts);
        _loadExisting = loadExisting ?? throw new ArgumentNullException(nameof(loadExisting));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _logger = logger;

        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        InitializeComponent();

        _existing = ReadExisting();
        Chips.ItemsSource = _chips;

        // A drag can end anywhere, including outside the window, so releasing the button always
        // ends it rather than leaving the next hover silently extending a stale selection.
        PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;

            // Only a finished selection hands the caret over. Unpicking the last word is not the
            // end of a gesture, and pulling focus then would drag the user away from the chips
            // just as they were about to pick a different word.
            if (_first >= 0)
            {
                FocusCorrection();
            }
        };

        var sources = recentTranscripts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new TranscriptSource(t))
            .ToList();

        if (sources.Count == 0)
        {
            SourcePanel.Visibility = Visibility.Collapsed;
            Chips.Visibility = Visibility.Collapsed;
            NoTranscriptHint.Visibility = Visibility.Visible;
            Loaded += (_, _) => HeardBox.Focus();
        }
        else
        {
            RecentPicker.ItemsSource = sources;
            RecentPicker.DisplayMemberPath = nameof(TranscriptSource.Preview);
            RecentPicker.SelectedIndex = 0; // fires SelectionChanged, which loads the chips
        }

        UpdateStatus();
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
        _dragging = false;

        _chips.Clear();
        for (var i = 0; i < _tokens.Count; i++)
        {
            _chips.Add(new WordChip { Text = _tokens[i].Text, Index = i });
        }

        // The old selection described the previous transcript. Leaving it in the box would let the
        // user save a rule for words that are no longer anywhere on screen.
        if (HeardBox is not null)
        {
            HeardBox.Text = string.Empty;
        }

        UpdateStatus();
    }

    private void Chip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleButton { Tag: int index })
        {
            return;
        }

        // Suppress the ToggleButton's own toggle. IsChecked is bound two-way to the chip model, so
        // letting the control set it locally would fight the selection ApplySelection writes.
        e.Handled = true;

        // Shift keeps the classic range gesture for anyone who reaches for it.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor >= 0)
        {
            _dragging = true;
            ApplySelection(_anchor, index);
            return;
        }

        ToggleAt(index);
    }

    /// <summary>
    /// Applies one plain click, from the mouse or from assistive tech, to the selected word range.
    /// The decision itself lives in <see cref="QuickDictionaryAdd.Toggle"/>; this only carries the
    /// result into the chips and the drag anchor.
    /// </summary>
    private void ToggleAt(int index)
    {
        var previous = new QuickDictionaryAdd.WordRange(_first, _last);
        var next = QuickDictionaryAdd.Toggle(previous, index);

        if (next.IsEmpty)
        {
            // Still arm the drag. Pressing the only selected word unpicks it, but the press may
            // equally be the start of a drag from that chip, and refusing to track it would kill
            // that gesture on exactly the chips where it used to work.
            _anchor = index;
            _dragging = true;
            ClearSelection();
            return;
        }

        // Anchor the end that did not move, so a shift-click or drag straight afterwards grows from
        // the stable side rather than from wherever the last click happened to land.
        _anchor = next.First == previous.First ? next.First : next.Last;
        _dragging = true;
        ApplySelection(next.First, next.Last);
    }

    private void Chip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        // Self-heals a drag whose mouse-up landed somewhere that never reported it. Suppressing the
        // chip's own mouse handling means nothing captures the mouse, so releasing outside the
        // window never reaches PreviewMouseLeftButtonUp and the gesture is only finishable here.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragging = false;

            if (_first >= 0)
            {
                FocusCorrection();
            }

            return;
        }

        if (sender is ToggleButton { Tag: int index } && _anchor >= 0)
        {
            ApplySelection(_anchor, index);
        }
    }

    private void ApplySelection(int first, int last)
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

        // TextChanged only fires when the string actually differs, and a transcript can repeat a
        // word ("V" three times here), so moving between two identical selections would otherwise
        // leave the hint describing a range that is no longer the one lit.
        UpdateStatus();
    }

    /// <summary>
    /// Handles a chip toggled by anything other than the mouse. Assistive technology drives a
    /// ToggleButton through the UI Automation Toggle pattern, which never raises the mouse events
    /// the selection is built on, so without this the chip would light up while the phrase in the
    /// box below stayed unchanged and the user would save something other than what they saw.
    /// Routed through the same <see cref="ToggleAt"/> rules the mouse uses, so a screen reader can
    /// build a multi-word phrase exactly the way a sighted user can.
    /// </summary>
    private void Chip_Toggled(object sender, RoutedEventArgs e)
    {
        // ApplySelection sets IsSelected on every chip, which raises this event again for each one.
        if (_syncingChips || sender is not ToggleButton { Tag: int index })
        {
            return;
        }

        // The chip has already flipped its own IsChecked. ToggleAt works from the range fields rather
        // than from chip state, and ApplySelection rewrites every chip afterwards, so whichever way
        // it flipped gets corrected here and both input paths land on the same selection.
        ToggleAt(index);
        _dragging = false;

        // Matches the mouse: unpicking the last word leaves the caret where it was rather than
        // throwing it into the correction box.
        if (_first >= 0)
        {
            FocusCorrection();
        }
    }

    private void ClearSelection()
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
        UpdateStatus();
    }

    /// <summary>
    /// Moves the caret to the correction box once a selection is finished. Deliberately not called
    /// while a drag is in flight: doing it on every extension stole focus mid-gesture, and the
    /// SelectAll that went with it meant the next keystroke wiped a correction already typed.
    /// </summary>
    private void FocusCorrection()
    {
        ShouldBeBox.Focus();
        ShouldBeBox.CaretIndex = ShouldBeBox.Text.Length;
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        // StatusText is resolved by InitializeComponent, but TextChanged can fire while the XAML
        // tree is still being built, before the later fields exist.
        if (StatusText is null || SaveButton is null)
        {
            return;
        }

        var plan = BuildPlan(_existing);

        // An empty correction is a real rule (it deletes the word), and the settings grid supports
        // it, but it must never be reachable by a reflexive Enter after clicking a chip. Dropping
        // IsDefault keeps Enter on the common path and makes deletion a deliberate click.
        var deletes = plan.CanSave && plan.Entry is { Replacement.Length: 0 };

        StatusText.Text = plan.Message;
        StatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            plan.Kind switch
            {
                _ when deletes => "SystemFillColorCautionBrush",
                QuickDictionaryAdd.PlanKind.Update => "SystemFillColorCautionBrush",
                _ => "TextFillColorSecondaryBrush",
            });

        SaveButton.IsEnabled = plan.CanSave;
        SaveButton.IsDefault = plan.CanSave && !deletes;

        // "this" rather than "it" or "the word": the selection is often a phrase, and an unanchored
        // pronoun on a button that behaves differently from the primary action reads as a threat.
        SaveButton.Content = deletes ? "Leave this out of dictations" : "Save to dictionary";

        // The hint has to follow the selection. A static line describing a gesture is exactly what
        // hid multi-select before: it was there, and it still read as "one word is all you get".
        if (HintText is not null)
        {
            HintText.Text = _first < 0
                ? "Click the word Scribe got wrong, or just type it below."
                : _first == _last
                    ? "Click the word beside it to build a phrase, or click it again to unpick it."
                    // "next to" rather than "either end": clicking an end word is the shrink
                    // gesture, so telling the user to click an end to grow it would be advice that
                    // does the opposite of what it says.
                    : "Click a word next to the phrase to grow it, or an end word to drop it.";
        }
    }

    private QuickDictionaryAdd.Plan BuildPlan(IReadOnlyList<DictionaryEntry> existing) =>
        QuickDictionaryAdd.Build(
            HeardBox?.Text,
            ShouldBeBox?.Text,
            WholeWordBox?.IsChecked == true,
            existing);

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-read rather than trusting the snapshot taken when the window opened: the settings
        // window may have saved a conflicting rule while this popup sat on screen, and creating a
        // second row for the same spoken form would produce a dictionary the grid refuses to save.
        _existing = ReadExisting();
        var plan = BuildPlan(_existing);
        if (!plan.CanSave || plan.Entry is null)
        {
            UpdateStatus();
            return;
        }

        DictionaryEntry saved;
        try
        {
            saved = _persist(plan.Entry);
        }
        catch (Exception ex)
        {
            // The user gets a sentence they can act on; the detail goes to the log, which is the only
            // place a broken write can actually be diagnosed from later.
            _logger?.LogError(ex, "Quick add failed to save the dictionary entry.");
            StatusText.Text = "Couldn't save that rule. Try again, or add it in Settings, Dictionary.";
            StatusText.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            return;
        }

        // Deliberately outside the catch above. The rule is already stored, so a failure to reload
        // the post-processor or raise a toast must not tell the user their entry was not saved.
        var corrected = QuickDictionaryAdd.Apply(_transcript, saved);
        Saved?.Invoke(new QuickAddResult(
            saved,
            _transcript,
            string.Equals(corrected, _transcript, StringComparison.Ordinal) ? null : corrected));
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private IReadOnlyList<DictionaryEntry> ReadExisting()
    {
        try
        {
            return _loadExisting();
        }
        catch
        {
            // A read failure must not block the popup: the worst case is that a duplicate is
            // reported as a create, and the settings grid still catches it on the next save.
            return [];
        }
    }

    /// <summary>One entry in the recent-dictation picker.</summary>
    private sealed class TranscriptSource(string text)
    {
        public string Text { get; } = text;

        public string Preview { get; } = LastTranscriptStore.FormatPreview(text, maxLength: 64);

        // The picker uses an ItemTemplate, so without this a screen reader reads the type name
        // instead of the dictation, leaving the user no way to tell the five entries apart.
        public override string ToString() => Preview;
    }

    /// <summary>One selectable word in the transcript.</summary>
    private sealed class WordChip : INotifyPropertyChanged
    {
        private bool _isSelected;

        public required string Text { get; init; }

        public required int Index { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
