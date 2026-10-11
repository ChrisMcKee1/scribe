using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Scribe.Core.Overlay;
using Scribe.Overlay.Interop;
using Scribe.Overlay.Logging;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Scribe.Overlay;

/// <summary>
/// The transparent, always-on-top, click-through recording pill window. Transparency comes from a
/// custom <see cref="TransparentBackdrop"/> (DWM composition), NOT the WPF layered-window path that
/// caused the recurring black box. Every meaningful lifecycle transition and the full window state
/// snapshot are logged to the shared Scribe channel so we can prove exactly how the surface behaves.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    // How long each state stays on screen. The overlay cannot reference Scribe.Core, so these copy
    // Scribe.Core.Overlay.PillTiming, and a test keeps them equal; the storyboards' durations are checked the same way.
    // An outcome holds the pill on screen: a Hide that arrives during the hold (the app's own hide after processing) is
    // ignored until the hold elapses. A new recording or processing state replaces it at once.
    private static readonly TimeSpan TypedHold = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan NoticeHold = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan RecordingWarningHold = TimeSpan.FromMilliseconds(1800);

    private const string ProcessingStoryboardKey = "ProcessingStoryboard";
    private const string FadeInStoryboardKey = "FadeInStoryboard";
    private const string FadeOutStoryboardKey = "FadeOutStoryboard";

    private readonly IntPtr _hwnd;
    private readonly WindowId _windowId;
    private readonly AppWindow _appWindow;
    private readonly ScaleTransform[] _barScales;

    private OverlayState _state = OverlayState.Hidden;
    private OverlayAnchor _anchor = OverlayAnchor.BottomCenter;
    private bool _shownOnce;

    // WS_EX_LAYERED, or 0 under SCRIBE_OVERLAY_DIAG_NOLAYERED: put on before every show and taken off after every hide
    // that follows one (SetLayered), unless SCRIBE_OVERLAY_DIAG_KEEPLAYERED keeps it on, as every earlier build did.
    private long _layeredStyle;
    private bool _keepLayeredWhileHidden;

    // Windows' "Animation effects" and contrast theme, read in this process at each show.
    private UISettings? _uiSettings;
    private AccessibilitySettings? _accessibility;
    private bool _animate;
    private bool _contrast;
    private bool _displaySettingsFailed;

    // Windows text size, as the scale the whole pill is drawn at: read at each show and again when Windows changes it.
    // PillTextScale holds the scale the window is sized for and decides when a new one applies, never mid-fade.
    private readonly PillTextScale _textScale = new();
    private double _textScaleRead = PillGeometry.MinTextScale;
    private bool _textScaleFailed;

    private bool _fadingIn;
    private bool _fadingOut;
    private DispatcherQueueTimer? _outcomeTimer;
    private DispatcherQueueTimer? _recordingWarningTimer;
    private DateTime _outcomeHoldUntil = DateTime.MinValue;

    public OverlayWindow()
    {
        var started = Stopwatch.GetTimestamp();
        OverlayLog.Write("OverlayWindow.ctor enter");
        OverlayLog.Write("OverlayWindow.ctor stage=xaml begin");
        InitializeComponent();
        OverlayLog.Write("OverlayWindow.ctor stage=xaml complete");
        _barScales = [Bar1Scale, Bar2Scale, Bar3Scale, Bar4Scale, Bar5Scale];

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
        OverlayLog.Write($"OverlayWindow.ctor hwnd=0x{_hwnd.ToInt64():X} windowId={_windowId.Value}");

        OverlayLog.Write("OverlayWindow.ctor stage=presenter begin");
        ConfigurePresenter();
        OverlayLog.Write("OverlayWindow.ctor stage=presenter complete");
        OverlayLog.Write("OverlayWindow.ctor stage=styles begin");
        ApplyExtendedStyles();
        OverlayLog.Write("OverlayWindow.ctor stage=styles complete");

        // DWM-composition transparency: a window with no opaque backdrop shows whatever is behind it.
        if (Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_DIAG_NOBACKDROP") == "1")
        {
            OverlayLog.Write("OverlayWindow.ctor DIAG: SystemBackdrop skipped");
        }
        else
        {
            SystemBackdrop = new TransparentBackdrop();
            OverlayLog.Write("OverlayWindow.ctor SystemBackdrop=TransparentBackdrop assigned");
        }

        OverlayLog.Write("OverlayWindow.ctor stage=position begin");
        SizeAndPosition();
        OverlayLog.Write("OverlayWindow.ctor stage=position complete");

        // The fade out ends in the hide. Without the storyboard the pill hides at once instead.
        if (FindStoryboard(FadeOutStoryboardKey) is { } fadeOut)
        {
            fadeOut.Completed += OnFadeOutCompleted;
        }

        // A text size change that arrived during the fade in is applied once it has finished.
        if (FindStoryboard(FadeInStoryboardKey) is { } fadeIn)
        {
            fadeIn.Completed += OnFadeInCompleted;
        }

        // Lifecycle tracing: the whole point of the rebuild is to see every transition.
        Activated += OnActivated;
        Closed += OnClosed;
        VisibilityChanged += OnVisibilityChanged;
        _appWindow.Changed += OnAppWindowChanged;

        // Start hidden; the host (or App.OnLaunched in Phase 0) drives the first visible state.
        OverlayLog.Write("OverlayWindow.ctor stage=hide begin");
        _appWindow.Hide();
        LogState("ctor.exit");
        OverlayLog.Write("OverlayWindow.ctor exit (hidden)");
        OverlayLog.Write($"OverlayWindow.ctor complete durationMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
    }

    private void ConfigurePresenter()
    {
        OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=switchers begin");
        _appWindow.IsShownInSwitchers = false; // hidden from Alt-Tab / task switcher
        OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=title begin");
        _appWindow.Title = "Scribe recording indicator";

        OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=presenter begin");
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=border begin");
            presenter.SetBorderAndTitleBar(false, false);
            OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=resize begin");
            presenter.IsResizable = false;
            OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=maximize begin");
            presenter.IsMaximizable = false;
            OverlayLog.Write("OverlayWindow.ConfigurePresenter stage=minimize begin");
            presenter.IsMinimizable = false;
            // NOTE: deliberately NOT using presenter.IsAlwaysOnTop; it is known to break
            // WS_EX_TRANSPARENT click-through. Top-most is asserted via SetWindowPos instead.
            OverlayLog.Write("OverlayWindow.ConfigurePresenter borderless overlapped presenter applied");
        }
        else
        {
            OverlayLog.Warn($"OverlayWindow.ConfigurePresenter unexpected presenter kind={_appWindow.Presenter?.Kind}");
        }
    }

    private void ApplyExtendedStyles()
    {
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);

        // WS_EX_LAYERED is what makes WS_EX_TRANSPARENT pass clicks through: "if the layered window has the
        // WS_EX_TRANSPARENT extended window style, the shape of the layered window will be ignored and the mouse events
        // will be passed to other windows underneath" (Window Features). Unlayered, WS_EX_TRANSPARENT only orders painting.
        //
        // Win+Shift+S (0.3.12 bisection, still open): never created works, created but never shown works, shown fails, with
        // the screen snip starting two SnippingTool processes that both exit. The backdrop, HWND_TOPMOST, the process and
        // the keyboard hook were ruled out; WS_EX_LAYERED was the remaining suspect and its rung was never reported. That
        // run still activated the window at its first show, which 0.5.0 removed, and since 0.5.0 the helper stays running
        // hidden all session, so a window shown once would keep screenshots broken until Scribe quits or dictation pauses.
        // So the style is on only while the pill is on screen: SetLayered puts it on before every show and takes it off
        // after every hide that follows one, which is "desired to let the system know that it can free up some memory
        // associated with layering and redirection" (Using Windows, Using Layered Windows). A window that has never been
        // shown keeps it, as the bisection's working case did. Unproven on a machine that shows the failure:
        // SCRIBE_OVERLAY_DIAG_KEEPLAYERED=1 keeps it on while hidden, as before, for the comparison.
        _layeredStyle = Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_DIAG_NOLAYERED") == "1"
            ? 0
            : NativeMethods.WS_EX_LAYERED;
        if (_layeredStyle == 0)
        {
            OverlayLog.Write("OverlayWindow.ApplyExtendedStyles DIAG: WS_EX_LAYERED omitted");
        }

        _keepLayeredWhileHidden = Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_DIAG_KEEPLAYERED") == "1";
        if (_keepLayeredWhileHidden)
        {
            OverlayLog.Write("OverlayWindow.ApplyExtendedStyles DIAG: WS_EX_LAYERED kept while hidden");
        }

        var updated = ex
            | _layeredStyle
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, updated);
        OverlayLog.Write($"OverlayWindow.ApplyExtendedStyles ex=0x{ex:X}->0x{updated:X} (LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE)");

        RemoveDwmFrame();
    }

    // Windows 11's compositor draws a 1px non-client border and rounded corners on every top-level
    // window, even borderless ones; the visible rectangle around the otherwise transparent pill.
    // Suppress both so only the XAML card is ever visible. Best-effort: on Windows 10 (or if DWM
    // rejects the attributes) the calls fail with an HRESULT and the pill simply keeps the frame.
    private void RemoveDwmFrame()
    {
        var corner = NativeMethods.DWMWCP_DONOTROUND;
        var cornerHr = NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var borderColor = NativeMethods.DWMWA_COLOR_NONE;
        var borderHr = NativeMethods.DwmSetWindowAttributeUint(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        OverlayLog.Write(
            $"OverlayWindow.RemoveDwmFrame corner(DONOTROUND) hr=0x{cornerHr:X8} " +
            $"border(COLOR_NONE) hr=0x{borderHr:X8}");
    }

    private double DpiScale
    {
        get
        {
            var dpi = NativeMethods.GetDpiForWindow(_hwnd);
            return dpi <= 0 ? 1.0 : dpi / 96.0;
        }
    }

    // The window is the 100% window times the text scale the pill is drawn at (the Viewbox in OverlayWindow.xaml scales the
    // content to fill it), placed at its anchor with the 8 DIP margin and kept inside the work area (PillGeometry).
    private void SizeAndPosition()
    {
        var scale = DpiScale;
        var textScale = _textScale.Current;
        var work = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest).WorkArea;

        // OverlayAnchor has PillAnchor's names in PillAnchor's order (both are the engine's OverlayPosition's).
        var place = PillGeometry.Place(
            (PillAnchor)(int)_anchor, new PillRect(work.X, work.Y, work.Width, work.Height), textScale, scale);
        _appWindow.MoveAndResize(new RectInt32(place.X, place.Y, place.Width, place.Height));

        OverlayLog.Write(
            $"OverlayWindow.SizeAndPosition scale={scale:0.##} textScale={textScale:0.##} size={place.Width}x{place.Height} " +
            $"pos={place.X},{place.Y} anchor={_anchor} work=({work.X},{work.Y},{work.Width},{work.Height}) clamped={place.Clamped}");
    }

    /// <summary>
    /// Moves the pill's anchor (driven by the engine's POSITION command). Takes effect immediately
    /// when the pill is on screen, otherwise on the next show via <see cref="EnsureShown"/>.
    /// </summary>
    public void SetAnchor(OverlayAnchor anchor) => RunOnUi(() =>
    {
        if (_anchor == anchor)
        {
            return;
        }

        _anchor = anchor;
        OverlayLog.Write($"OverlayWindow.SetAnchor {anchor}");
        if (_appWindow.IsVisible)
        {
            SizeAndPosition();
        }
    });

    /// <summary>
    /// Switches the visible content, edge and motion for <paramref name="state"/> and shows or hides the window.
    /// UI-thread marshalled. Windows' animation and contrast settings are read here, at each show.
    /// </summary>
    public void ShowState(OverlayState state)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            OverlayLog.Write($"OverlayWindow.ShowState({state}) marshalling to UI thread");
            DispatcherQueue.TryEnqueue(() => ShowState(state));
            return;
        }

        OverlayLog.Write($"OverlayWindow.ShowState enter requested={state} current={_state}");
        var previous = _state;
        _state = state;

        if (state == OverlayState.Hidden)
        {
            HidePill(previous);
            return;
        }

        var error = IsError(state);
        ListeningContent.Visibility = VisibleIf(state == OverlayState.Listening);
        ProcessingContent.Visibility = VisibleIf(state == OverlayState.Processing);
        TypedContent.Visibility = VisibleIf(state == OverlayState.Typed);
        NoticeContent.Visibility = VisibleIf(IsNotice(state));
        CautionIcon.Visibility = VisibleIf(state == OverlayState.TypedWithoutCleanup);
        ErrorIcon.Visibility = VisibleIf(error);
        NoticeTitle.Text = NoticeTitleFor(state);
        ListeningEdge.Visibility = VisibleIf(state == OverlayState.Listening);
        ErrorEdge.Visibility = VisibleIf(error);
        NeutralEdge.Visibility = VisibleIf(state != OverlayState.Listening && !error);

        ReadDisplaySettings();

        // A fade that was taking the pill away gives it back at full opacity; it never left the screen.
        var appearing = !_appWindow.IsVisible;
        CancelFadeOut();

        // The text size read above applies before an appearing pill's first frame and at once to one on screen, except
        // during its fade in, which the change waits out; EnsureShown sizes and anchors the window for it.
        _textScale.OnShow(_textScaleRead, appearing, _fadingIn);
        if (appearing && _animate)
        {
            StartStoryboard(FadeInStoryboardKey); // begun before the show, so the first frame is already transparent
            _fadingIn = true;
        }

        EnsureShown();

        if (state == OverlayState.Processing && _animate)
        {
            StartStoryboard(ProcessingStoryboardKey);
        }
        else
        {
            StopStoryboard(ProcessingStoryboardKey); // with Animation effects off the dots stand still
        }

        LogState($"ShowState.{state}");
        OverlayLog.Write($"OverlayWindow.ShowState exit shown={state} (was {previous})");
    }

    // ---- High-level command surface (driven by the WPF engine over IPC) --------------------------

    /// <summary>Listening: the listening edge and the level bars, back at their floor.</summary>
    public void ShowRecording() => RunOnUi(() =>
    {
        ClearOutcomeHold();
        _recordingWarningTimer?.Stop();
        StatusText.Text = "Listening…";
        SetBars(0);
        ShowState(OverlayState.Listening);
    });

    /// <summary>Brief warning text that preserves the active recording state and live level bars.</summary>
    public void ShowRecordingWarning(string? reason) => RunOnUi(() =>
    {
        ClearOutcomeHold();
        StatusText.Text = string.IsNullOrWhiteSpace(reason) ? "Microphone muted" : reason.Trim();
        ShowState(OverlayState.Listening);

        _recordingWarningTimer ??= CreateRecordingWarningTimer();
        _recordingWarningTimer.Stop();
        _recordingWarningTimer.Start();
        OverlayLog.Write($"OverlayWindow.ShowRecordingWarning hold={RecordingWarningHold.TotalMilliseconds:0}ms reasonLength={ReasonLength(reason)}");
    });

    /// <summary>
    /// Processing: three dots, and the words say whether it is recognizing speech (<paramref name="kind"/> 0), running AI
    /// cleanup (1), or waiting for AI cleanup's model on this PC to start (2), which can take a while and says so.
    /// </summary>
    public void ShowProcessing(int kind) => RunOnUi(() =>
    {
        ClearOutcomeHold();
        var starting = kind == 2;
        ProcessingText.Text = starting ? "Starting local model…" : kind == 1 ? "Running AI cleanup…" : "Recognizing speech…";
        ProcessingDetail.Text = starting ? "This can take time" : string.Empty;
        ProcessingDetail.Visibility = VisibleIf(starting);
        ShowState(OverlayState.Processing);
    });

    /// <summary>
    /// A finished dictation's outcome (<see cref="OverlayState.Typed"/>, <see cref="OverlayState.TypedWithoutCleanup"/>,
    /// <see cref="OverlayState.NothingTyped"/> or <see cref="OverlayState.PartlyTyped"/>), held on screen for its hold and
    /// then hidden. <paramref name="detail"/> is the second line of a notice: the reason, or the next step.
    /// </summary>
    public void ShowOutcome(OverlayState state, string? detail) => RunOnUi(() =>
    {
        if (!IsOutcome(state))
        {
            OverlayLog.Warn($"OverlayWindow.ShowOutcome refused state={state}");
            return;
        }

        _recordingWarningTimer?.Stop();
        NoticeDetail.Text = IsNotice(state) ? (detail ?? string.Empty).Trim() : string.Empty;
        ShowState(state);

        var hold = state == OverlayState.Typed ? TypedHold : NoticeHold;
        _outcomeHoldUntil = DateTime.UtcNow + hold;
        _outcomeTimer ??= CreateOutcomeTimer();
        _outcomeTimer.Stop();
        _outcomeTimer.Interval = hold;
        _outcomeTimer.Start();
        OverlayLog.Write($"OverlayWindow.ShowOutcome state={state} hold={hold.TotalMilliseconds:0}ms reasonLength={ReasonLength(detail)}");
    });

    // The reason is display text composed by the engine and can carry user configuration, such as a
    // microphone's name or a custom cleanup endpoint's host inside a failure detail. The pill shows it; the
    // shared log only records how long it was. The wire protocol carries no fixed category to log instead.
    private static int ReasonLength(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? 0 : reason.Trim().Length;

    /// <summary>Hides the pill, unless an outcome is still holding on screen.</summary>
    public void Hide() => RunOnUi(() =>
    {
        if (DateTime.UtcNow < _outcomeHoldUntil)
        {
            OverlayLog.Write("OverlayWindow.Hide ignored (outcome hold active)");
            return;
        }

        ShowState(OverlayState.Hidden);
    });

    /// <summary>Scales the level bars to the live input level (0..1). No-op unless listening.</summary>
    public void SetMeter(double level) => RunOnUi(() =>
    {
        if (_state != OverlayState.Listening)
        {
            return;
        }

        SetBars(level);
    });

    // PillLevelBars (Scribe.Core, compiled in here) gives each bar's height: a 4 DIP floor, and above it the icon's
    // proportions of the other 12 DIP. A level update is five render-transform writes and never a layout pass (the old
    // meter's width change laid out 40 times a second).
    private void SetBars(double level)
    {
        for (var i = 0; i < _barScales.Length; i++)
        {
            _barScales[i].ScaleY = PillLevelBars.ScaleOf(i, level);
        }
    }

    private static bool IsOutcome(OverlayState state) => state is OverlayState.Typed || IsNotice(state);

    private static bool IsNotice(OverlayState state) =>
        state is OverlayState.TypedWithoutCleanup || IsError(state);

    private static bool IsError(OverlayState state) =>
        state is OverlayState.NothingTyped or OverlayState.PartlyTyped;

    private static string NoticeTitleFor(OverlayState state) => state switch
    {
        OverlayState.TypedWithoutCleanup => "Typed without AI cleanup",
        OverlayState.NothingTyped => "Nothing typed",
        OverlayState.PartlyTyped => "Not all of it was typed",
        _ => string.Empty,
    };

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void RunOnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    // Read at each show, in this process. Motion is decoration, so without an answer the pill stays still; the contrast
    // theme is WinUI's to apply (the HighContrast brushes in App.xaml) and is read here for the state line. The text size
    // is read with them.
    private void ReadDisplaySettings()
    {
        try
        {
            _uiSettings ??= CreateUiSettings();
            _accessibility ??= new AccessibilitySettings();
            _animate = _uiSettings.AnimationsEnabled;
            _contrast = _accessibility.HighContrast;
        }
        catch (Exception ex)
        {
            _animate = false;
            if (!_displaySettingsFailed)
            {
                _displaySettingsFailed = true;
                OverlayLog.Warn($"OverlayWindow.ReadDisplaySettings failed ({ex.GetType().Name} 0x{ex.HResult:X8}); no motion");
            }
        }

        _textScaleRead = ReadTextScale();
    }

    // Windows text size, as the scale the whole pill is drawn at (PillGeometry.TextScale clamps it to 1 to 2.25). Without an
    // answer the pill keeps its 100% size.
    private double ReadTextScale()
    {
        try
        {
            _uiSettings ??= CreateUiSettings();
            return PillGeometry.TextScale(_uiSettings.TextScaleFactor);
        }
        catch (Exception ex)
        {
            if (!_textScaleFailed)
            {
                _textScaleFailed = true;
                OverlayLog.Warn($"OverlayWindow.ReadTextScale failed ({ex.GetType().Name} 0x{ex.HResult:X8}); 100% text size");
            }

            return PillGeometry.MinTextScale;
        }
    }

    // The one UISettings this window keeps, so its text size event keeps firing for as long as the window lives.
    private UISettings CreateUiSettings()
    {
        var settings = new UISettings();
        settings.TextScaleFactorChanged += OnTextScaleFactorChanged;
        return settings;
    }

    // Windows raises this off the UI thread. The scale is read again there; it resizes and re-anchors a pill on screen at
    // once, one fading in once the fade has finished, and one hidden or fading out at its next show (PillTextScale).
    private void OnTextScaleFactorChanged(UISettings sender, object args) => RunOnUi(() =>
    {
        var textScale = ReadTextScale();
        var resize = _textScale.OnChanged(textScale, _appWindow.IsVisible, _fadingIn, _fadingOut);
        OverlayLog.Write($"OverlayWindow.TextScaleChanged textScale={textScale:0.##} resize={resize}");
        if (resize)
        {
            SizeAndPosition();
            LogState("TextScaleChanged");
        }
    });

    // Nothing runs while the pill is hidden: the dots and both timers stop here, and a fade out ends in the hide.
    private void HidePill(OverlayState previous)
    {
        StopStoryboard(ProcessingStoryboardKey);
        _outcomeTimer?.Stop();
        _recordingWarningTimer?.Stop();
        _outcomeHoldUntil = DateTime.MinValue;

        if (_fadingOut)
        {
            OverlayLog.Write($"OverlayWindow.ShowState exit already fading out (was {previous})");
            return;
        }

        StopStoryboard(FadeInStoryboardKey);
        _fadingIn = false;
        _textScale.OnHidden();
        if (_animate && _appWindow.IsVisible && FindStoryboard(FadeOutStoryboardKey) is not null)
        {
            _fadingOut = true;
            StartStoryboard(FadeOutStoryboardKey);
            LogState("ShowState.fadingOut");
            OverlayLog.Write($"OverlayWindow.ShowState exit fading out (was {previous})");
            return;
        }

        _appWindow.Hide();
        SetLayered(false);
        LogState("ShowState.hidden");
        OverlayLog.Write($"OverlayWindow.ShowState exit hidden (was {previous})");
    }

    private void OnFadeOutCompleted(object? sender, object e)
    {
        if (!_fadingOut)
        {
            return; // a new state took the pill back first
        }

        _fadingOut = false;
        _appWindow.Hide();
        SetLayered(false);
        StopStoryboard(FadeOutStoryboardKey); // full opacity again for the next show, while hidden
        LogState("FadeOut.completed");
        OverlayLog.Write("OverlayWindow.FadeOut completed; window hidden");
    }

    // A text size change that arrived during the fade in resizes the pill now, if it is still on screen.
    private void OnFadeInCompleted(object? sender, object e)
    {
        _fadingIn = false;
        if (_textScale.OnFadeInCompleted(_appWindow.IsVisible, _fadingOut))
        {
            SizeAndPosition();
            LogState("FadeIn.completed");
        }
    }

    private void CancelFadeOut()
    {
        if (!_fadingOut)
        {
            return;
        }

        _fadingOut = false;
        StopStoryboard(FadeOutStoryboardKey);
    }

    private void ClearOutcomeHold()
    {
        _outcomeHoldUntil = DateTime.MinValue;
        _outcomeTimer?.Stop();
    }

    private DispatcherQueueTimer CreateOutcomeTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _outcomeHoldUntil = DateTime.MinValue;
            OverlayLog.Write($"OverlayWindow.OutcomeTimer.Tick state={_state}");
            // Only hide if a fresh dictation hasn't already taken the pill over.
            if (IsOutcome(_state))
            {
                ShowState(OverlayState.Hidden);
            }
        };
        return timer;
    }

    private DispatcherQueueTimer CreateRecordingWarningTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = RecordingWarningHold;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_state == OverlayState.Listening)
            {
                StatusText.Text = "Listening…";
            }
        };
        return timer;
    }

    private void EnsureShown()
    {
        // Diagnostic rung of the Win+Shift+S bisection (see ApplyExtendedStyles): with this switch the window exists but is
        // never shown, which is the case the bisection found working.
        if (Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_DIAG_NEVERSHOW") == "1")
        {
            OverlayLog.Write("OverlayWindow.EnsureShown DIAG: show suppressed (window exists, stays hidden)");
            return;
        }

        // Layered again before it is sized and shown, the order the constructor applies the styles in, so the pill on
        // screen is the click-through window it always was.
        SetLayered(true);

        // Re-assert size/position in case the monitor/DPI changed between shows.
        SizeAndPosition();

        // Every show, the first included, is AppWindow.Show without activation. The first show used to call
        // Window.Activate(), which "Attempts to activate the application window by bringing it to the foreground and
        // setting the input focus to it"; WinUI implements it as ShowWindow(SW_SHOW) and SetActiveWindow, and
        // WS_EX_NOACTIVATE does not stop an explicit activation ("To activate the window, use the SetActiveWindow or
        // SetForegroundWindow function"). The log recorded OverlayWindow.Activated state=CodeActivated during the first
        // dictation after every overlay launch. Whenever Windows allowed the foreground to move (SetForegroundWindow lists
        // when it does), the window being dictated into would lose it mid-recording, and with it a Remote Desktop client
        // its activation. AppWindow.Show is a supported way to show a XAML window (microsoft-ui-xaml#10995 is one shown
        // that way, which differs from an activated one in what activation brings, such as tooltips); the pill takes no
        // input and shows no tooltip.
        var first = !_shownOnce;
        _shownOnce = true;
        _appWindow.Show(activateWindow: false);
        OverlayLog.Write(first
            ? "OverlayWindow.EnsureShown first show AppWindow.Show(activate:false)"
            : "OverlayWindow.EnsureShown AppWindow.Show(activate:false)");

        AssertTopMost();
    }

    private void AssertTopMost()
    {
        // Diagnostic rung of the Win+Shift+S bisection (see ApplyExtendedStyles), same shape as
        // SCRIBE_OVERLAY_DIAG_NOBACKDROP and _NOWINDOW: the window is shown without HWND_TOPMOST, which the bisection ruled
        // out as the cause.
        if (Environment.GetEnvironmentVariable("SCRIBE_OVERLAY_DIAG_NOTOPMOST") == "1")
        {
            OverlayLog.Write("OverlayWindow.AssertTopMost DIAG: HWND_TOPMOST skipped");
            return;
        }

        var ok = NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        OverlayLog.Write($"OverlayWindow.AssertTopMost SetWindowPos(HWND_TOPMOST) ok={ok}");
    }

    // Puts WS_EX_LAYERED on (before a show) or takes it off (after a hide); see ApplyExtendedStyles. The window is hidden
    // whenever the style comes off, so the repaint Using Layered Windows asks for after removing it has nothing to show.
    // Best-effort: a failed change is logged and the pill keeps whatever style it has.
    private void SetLayered(bool onScreen)
    {
        if (_layeredStyle == 0 || (!onScreen && (_keepLayeredWhileHidden || !_shownOnce)))
        {
            return;
        }

        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        var wanted = onScreen ? ex | _layeredStyle : ex & ~_layeredStyle;
        if (wanted == ex)
        {
            return;
        }

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, wanted);
        var now = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        if (now == wanted)
        {
            OverlayLog.Write(
                $"OverlayWindow.SetLayered {(onScreen ? "on before show" : "off after hide")} ex=0x{ex:X}->0x{now:X}");
        }
        else
        {
            OverlayLog.Warn(
                $"OverlayWindow.SetLayered {(onScreen ? "on before show" : "off after hide")} did not apply: " +
                $"ex=0x{ex:X} wanted=0x{wanted:X} now=0x{now:X}");
        }
    }

    private Storyboard? FindStoryboard(string key) =>
        RootGrid.Resources.TryGetValue(key, out var res) && res is Storyboard sb ? sb : null;

    private void StartStoryboard(string key)
    {
        if (FindStoryboard(key) is { } sb)
        {
            sb.Begin();
            OverlayLog.Write($"OverlayWindow.StartStoryboard {key} begun");
        }
        else
        {
            OverlayLog.Warn($"OverlayWindow.StartStoryboard {key} not found");
        }
    }

    private void StopStoryboard(string key) => FindStoryboard(key)?.Stop();

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        OverlayLog.Write($"OverlayWindow.Activated state={args.WindowActivationState}");
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            AssertTopMost(); // z-order can be lost on (re)activation
        }
    }

    private void OnVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        OverlayLog.Write($"OverlayWindow.VisibilityChanged visible={args.Visible}");
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidVisibilityChange)
        {
            OverlayLog.Write(
                $"OverlayWindow.AppWindowChanged pos={args.DidPositionChange} size={args.DidSizeChange} " +
                $"vis={args.DidVisibilityChange} rect=({sender.Position.X},{sender.Position.Y},{sender.Size.Width},{sender.Size.Height}) isVisible={sender.IsVisible}");
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_uiSettings is not null)
        {
            _uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
        }

        OverlayLog.Write("OverlayWindow.Closed");
    }

    /// <summary>One-line snapshot of the window's true state: the core diagnostic signal.</summary>
    private void LogState(string phase)
    {
        try
        {
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
            var layered = (ex & NativeMethods.WS_EX_LAYERED) != 0;
            var transparent = (ex & NativeMethods.WS_EX_TRANSPARENT) != 0;
            var noactivate = (ex & NativeMethods.WS_EX_NOACTIVATE) != 0;
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            OverlayLog.Write(
                $"overlay[{phase}] state={_state} isVisible={_appWindow.IsVisible} " +
                $"rect=({pos.X},{pos.Y},{size.Width},{size.Height}) dpi={DpiScale:0.##} " +
                $"ex=0x{ex:X} layered={layered} transparent={transparent} noactivate={noactivate} " +
                $"backdrop={(SystemBackdrop is null ? "null" : SystemBackdrop.GetType().Name)} " +
                $"animations={_animate} contrast={_contrast} textScale={_textScale.Current:0.##}");
        }
        catch (Exception e)
        {
            OverlayLog.Error($"overlay[{phase}] LogState failed", e);
        }
    }
}
