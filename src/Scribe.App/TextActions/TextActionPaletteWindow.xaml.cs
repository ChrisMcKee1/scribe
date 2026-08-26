using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scribe.Core.TextActions;

namespace Scribe.App.TextActions;

/// <summary>
/// The text action palette: shows what Scribe captured, lists the transformations that can be
/// applied to it, and previews the result before anything replaces the user's text.
/// </summary>
/// <remarks>
/// <para>
/// This window activates normally, which is deliberate and is the opposite of how the recording pill
/// and the dock work. Both of those are <c>WS_EX_NOACTIVATE</c> so they never disturb the app being
/// typed into. A palette cannot be: a non-activating window receives no <c>WM_KEYDOWN</c> at all,
/// because keyboard messages go to the focus window of the FOREGROUND thread's input queue. Type to
/// filter, arrow keys and Escape would all have to be stolen by the global keyboard hook and would
/// stop working in the app underneath for as long as the palette was open.
/// </para>
/// <para>
/// Taking focus is safe because the selection is already captured to a string before this window is
/// shown. The only visible cost is that the target app greys its selection highlight while the
/// palette is up.
/// </para>
/// </remarks>
public partial class TextActionPaletteWindow : Wpf.Ui.Controls.FluentWindow
{
    // Entrance timing. Rows fade in on a short stagger so the list reads as arriving in order rather
    // than appearing all at once, capped so a long list never feels slow to become usable.
    private const int RowStaggerMs = 18;
    private const int MaxStaggerMs = 160;

    private readonly IReadOnlyList<TextAction> _actions;
    private readonly int _vocabularyFixCount;
    private bool _advancedShown;
    private bool _closing;

    /// <summary>The row the user last clicked, so a failure can return focus to it.</summary>
    private Button? _invokedRow;

    /// <summary>Raised when the user picks an action. The host runs it and calls back in.</summary>
    public event Action<TextAction>? ActionChosen;

    /// <summary>Raised when the user accepts a result and wants it written over the selection.</summary>
    public event Action<string>? ReplaceRequested;

    /// <summary>Raised when the user wants the result on the clipboard instead of in the document.</summary>
    public event Action<string>? CopyRequested;

    /// <param name="actions">Actions to offer, already filtered to what this target supports.</param>
    /// <param name="selection">The captured text, for the header confirmation.</param>
    /// <param name="destination">"On this device" or the endpoint host, shown as a permanent badge.</param>
    /// <param name="vocabularyFixCount">
    /// How many terms the deterministic pass would change. Zero hides that row entirely rather than
    /// leaving a button that does nothing.
    /// </param>
    public TextActionPaletteWindow(
        IReadOnlyList<TextAction> actions,
        string selection,
        string destination,
        int vocabularyFixCount)
    {
        InitializeComponent();

        _actions = actions;
        _vocabularyFixCount = vocabularyFixCount;

        var words = CountWords(selection);
        CountText.Text = words == 1 ? "1 word selected" : $"{words:N0} words selected";
        PreviewText.Text = Excerpt(selection);
        DestinationText.Text = destination;
        SetHint("Type to filter, Enter to run, Esc to close");

        BuildList();

        // Focus is set by ForceForeground after the window actually owns the foreground, not here.
        // Focusing a window that is not foreground gives it logical focus only, and the caret never
        // lands, which is what made the palette look like it was ignoring clicks when it was opened
        // from the WS_EX_NOACTIVATE dock.
    }

    /// <summary>The action currently running, so the host can label the busy state.</summary>
    public TextAction? RunningAction { get; private set; }

    private IEnumerable<TextAction> Matching()
    {
        var query = FilterBox.Text?.Trim() ?? string.Empty;
        var source = _advancedShown || query.Length > 0
            ? _actions
            : _actions.Where(a => !a.Advanced);

        foreach (var action in source)
        {
            // A vocabulary pass that would change nothing is not offered. A row guaranteed to be a
            // no-op teaches the user the feature does not work.
            if (action.Id == TextActionCatalog.ApplyVocabularyId && _vocabularyFixCount == 0)
            {
                continue;
            }

            if (query.Length == 0 || Matches(action, query))
            {
                yield return action;
            }
        }
    }

    // Subsequence matching, so "fmt teams" and "rwai" both find their row. Cheap, predictable, and
    // forgiving of the abbreviations people actually type into a palette.
    private static bool Matches(TextAction action, string query)
    {
        return Contains(action.Label) || Contains(action.Description) || Subsequence(action.Label);

        bool Contains(string haystack) =>
            haystack.Contains(query, StringComparison.OrdinalIgnoreCase);

        bool Subsequence(string haystack)
        {
            var index = 0;
            foreach (var c in haystack)
            {
                if (index < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[index]))
                {
                    index++;
                }
            }

            return index == query.Length;
        }
    }

    private void BuildList()
    {
        ActionList.Children.Clear();

        TextActionGroup? lastGroup = null;
        var delay = 0;
        var any = false;

        foreach (var action in Matching())
        {
            any = true;

            if (lastGroup != action.Group)
            {
                ActionList.Children.Add(new TextBlock
                {
                    Text = GroupLabelFor(action.Group),
                    Style = (Style)FindResource("GroupLabel"),
                });
                lastGroup = action.Group;
            }

            var row = BuildRow(action);
            ActionList.Children.Add(row);
            FadeIn(row, delay);
            delay = Math.Min(delay + RowStaggerMs, MaxStaggerMs);
        }

        if (!any)
        {
            ActionList.Children.Add(new TextBlock
            {
                Text = "No actions match that.",
                FontSize = 12,
                Opacity = 0.5,
                Margin = new Thickness(20, 16, 20, 16),
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            });
        }

        // The disclosure only makes sense while browsing; a filter already searches everything.
        if (!_advancedShown && FilterBox.Text.Length == 0 && _actions.Any(a => a.Advanced))
        {
            var more = new Button
            {
                Style = (Style)FindResource("ActionRow"),
                Tag = FindResource("AccentFormat"),
                Content = new TextBlock
                {
                    Text = "More formats",
                    FontSize = 12,
                    Opacity = 0.6,
                    Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
                },
            };
            more.Click += (_, _) => { _advancedShown = true; BuildList(); };
            ActionList.Children.Add(more);
        }
    }

    private static void FadeIn(UIElement element, int delayMs)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private Button BuildRow(TextAction action)
    {
        var description = action.Id == TextActionCatalog.ApplyVocabularyId
            ? $"{_vocabularyFixCount} term{(_vocabularyFixCount == 1 ? string.Empty : "s")} would change. Works offline, no model needed."
            : action.Description;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = action.Label,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
        });

        var button = new Button
        {
            Style = (Style)FindResource("ActionRow"),
            Content = stack,
            Tag = AccentFor(action.Group),
            DataContext = action,
        };

        button.Click += (_, _) =>
        {
            _invokedRow = button;
            ActionChosen?.Invoke(action);
        };
        return button;
    }

    private Brush AccentFor(TextActionGroup group) => (Brush)FindResource(group switch
    {
        TextActionGroup.Rewrite => "AccentRewrite",
        TextActionGroup.Format => "AccentFormat",
        _ => "AccentVocabulary",
    });

    private static string GroupLabelFor(TextActionGroup group) => group switch
    {
        TextActionGroup.Rewrite => "REWRITE",
        TextActionGroup.Format => "FORMAT",
        TextActionGroup.Vocabulary => "YOUR VOCABULARY",
        _ => string.Empty,
    };

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BuildList();
    }

    /// <summary>Switches to the in-flight state while an action runs.</summary>
    public void ShowRunning(TextAction action)
    {
        RunningAction = action;
        BusyText.Text = action.RequiresModel ? action.Label : "Applying your vocabulary";

        ListScroller.Visibility = Visibility.Collapsed;
        FilterHost.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        ResultButtons.Visibility = Visibility.Collapsed;
        SetHint("Esc to cancel");
    }

    /// <summary>Shows a finished result with accept, copy and discard.</summary>
    public void ShowResult(TextAction action, string result, bool canReplace, string? replaceBlockedReason)
    {
        RunningAction = action;
        ResultHeading.Text = action.Label.ToUpperInvariant();
        ResultText.Text = result;

        BusyPanel.Visibility = Visibility.Collapsed;
        ListScroller.Visibility = Visibility.Collapsed;
        FilterHost.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultButtons.Visibility = Visibility.Visible;

        ReplaceButton.IsEnabled = canReplace;
        ReplaceButton.Appearance = canReplace
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        CopyButton.Appearance = canReplace
            ? Wpf.Ui.Controls.ControlAppearance.Secondary
            : Wpf.Ui.Controls.ControlAppearance.Primary;

        SetHint(canReplace ? string.Empty : replaceBlockedReason ?? string.Empty);
        FadeIn(ResultPanel, 0);
        SizeToContent = SizeToContent.Height;

        // Enter should accept the result the user is now looking at, so put focus on the button it
        // would activate rather than leaving it on the filter box behind the panel.
        _ = (canReplace ? ReplaceButton : CopyButton).Focus();
    }

    /// <summary>
    /// Writes the footer line, as an ambient hint or as a failure the user must actually notice.
    /// </summary>
    /// <remarks>
    /// The footer was styled purely as a hint: 11px, 55% opacity, tertiary foreground, ellipsised on
    /// overflow. That is right for "Esc to cancel" and badly wrong for the only channel a failed
    /// action has. A throttled endpoint reported "The AI endpoint is throttling requests (429)" into
    /// that styling, where it read as decoration next to the button the user had just pressed, and
    /// the action appearing to do nothing while focus returned to the filter box was indistinguishable
    /// from a dead button. An error therefore takes full opacity, the critical brush, and wraps
    /// instead of truncating, because a message nobody reads is the same as no message.
    /// </remarks>
    private void SetHint(string text, bool isError = false)
    {
        HintText.Text = text;
        HintText.Opacity = isError ? 1.0 : 0.55;
        HintText.TextWrapping = isError ? TextWrapping.Wrap : TextWrapping.NoWrap;
        HintText.TextTrimming = isError ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        HintText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            isError ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush");
    }

    /// <summary>Returns to the action list after a failure, showing why.</summary>
    public void ShowFailure(string message)
    {
        RunningAction = null;
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        ResultButtons.Visibility = Visibility.Collapsed;
        ListScroller.Visibility = Visibility.Visible;
        FilterHost.Visibility = Visibility.Visible;
        SetHint(message, isError: true);

        // Focus goes back to the row the user clicked, NOT to the filter box. A failed action
        // already restores the list unchanged, and on a synchronous refusal no frame is composed
        // between ShowRunning and here, so the busy ring never paints. Yanking the caret to the
        // search box on top of that left the click with no visible consequence whatsoever, which is
        // what made a working button read as a dead one. Leaving focus on the row keeps the error
        // beside where the user was looking, and Enter retries the same action.
        if (_invokedRow is { } row && row.IsVisible)
        {
            _ = row.Focus();
        }
        else
        {
            _ = FilterBox.Focus();
        }
    }

    /// <summary>Positions the palette near the cursor, kept fully inside the current monitor.</summary>
    /// <remarks>
    /// Uses Win32 directly rather than WinForms' Cursor and Screen helpers, because Scribe.App does
    /// not reference WindowsForms and pulling that in for two calls would grow the self-contained
    /// payload for both architectures.
    /// </remarks>
    public void PositionNearCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        // The window is measured in WPF logical units; the monitor rect and cursor are device pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var width = Width;
        var height = ActualHeight > 0 ? ActualHeight : 440;

        var left = (cursor.X / scaleX) + 10;
        var top = (cursor.Y / scaleY) + 10;

        var areaLeft = info.rcWork.Left / scaleX;
        var areaTop = info.rcWork.Top / scaleY;
        var areaRight = info.rcWork.Right / scaleX;
        var areaBottom = info.rcWork.Bottom / scaleY;

        if (left + width > areaRight) left = areaRight - width - 10;
        if (top + height > areaBottom) top = areaBottom - height - 10;
        if (left < areaLeft) left = areaLeft + 10;
        if (top < areaTop) top = areaTop + 10;

        Left = left;
        Top = top;
    }

    /// <summary>
    /// Forces the palette to become the real foreground window, then puts the caret in the filter box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF's <c>Activate()</c> is not enough when the palette was opened from the floating dock. The
    /// dock carries <c>WS_EX_NOACTIVATE</c> on purpose, so that clicking it cannot take focus away
    /// from the app holding the user's selection. The consequence is that Scribe is NOT the
    /// foreground process at the moment the palette opens, and <c>SetForegroundWindow</c> is refused
    /// for a process that neither owns the foreground nor is credited with the last input event.
    /// </para>
    /// <para>
    /// The failure is quiet and confusing rather than obvious: the palette appears, it is topmost, and
    /// WPF hands logical focus to the filter box, but the window never owns the foreground input
    /// queue. Clicks on the action rows do not route, and focus visually snaps back to the search box,
    /// which reads as "the button is broken".
    /// </para>
    /// <para>
    /// Calling <c>SetForegroundWindow</c> explicitly here does work, because the user has just clicked
    /// Scribe's own dock window and Windows therefore credits this process with the last input event,
    /// which is one of the documented conditions under which the call is permitted. It is safe to do
    /// now for the same reason the write-back is: the selection was captured to a string before this
    /// window was ever shown, so taking foreground can no longer lose it.
    /// </para>
    /// </remarks>
    public void ForceForeground()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            _ = SetForegroundWindow(handle);
        }

        _ = Activate();

        // After the window genuinely owns the foreground, not before: focus set into a window that is
        // not foreground is logical only, and the caret does not actually land.
        _ = FilterBox.Focus();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseOnce();
                return;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case Key.Enter when ResultPanel.Visibility != Visibility.Visible:
                // Enter from the filter box runs the first match, which is what makes typing a
                // complete path to an action without ever touching the arrow keys.
                e.Handled = true;
                var target = FirstRow();
                target?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return;
        }
    }

    private Button? FirstRow() =>
        ActionList.Children.OfType<Button>().FirstOrDefault(b => b.DataContext is TextAction);

    private void MoveSelection(int delta)
    {
        var rows = ActionList.Children.OfType<Button>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var current = rows.FindIndex(b => b.IsKeyboardFocused);
        var next = current < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(current + delta, 0, rows.Count - 1);

        rows[next].Focus();
        rows[next].BringIntoView();
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseOnce();

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        var text = ResultText.Text;
        _closing = true; // suppress the Deactivated close so the host controls the teardown order
        ReplaceRequested?.Invoke(text);
    }

    private void OnCopyInstead(object sender, RoutedEventArgs e)
    {
        _closing = true;
        CopyRequested?.Invoke(ResultText.Text);
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => CloseOnce();

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    // The header excerpt is the only confirmation the user gets that Scribe read the text they meant,
    // so it shows the beginning verbatim with line breaks flattened rather than a summary.
    private static string Excerpt(string text)
    {
        const int MaxChars = 140;
        var flattened = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        return flattened.Length <= MaxChars ? flattened : flattened[..MaxChars].TrimEnd() + "...";
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}
