using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Scribe.Overlay.Interop;
using Scribe.Overlay.Logging;
using Windows.Graphics;

namespace Scribe.Overlay;

/// <summary>
/// The transparent, always-on-top, click-through recording pill window. Transparency comes from a
/// custom <see cref="TransparentBackdrop"/> (DWM composition), NOT the WPF layered-window path that
/// caused the recurring black box. Every meaningful lifecycle transition and the full window state
/// snapshot are logged to the shared Scribe channel so we can prove exactly how the surface behaves.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    // Logical (DIP) design size, matching the WPF overlay. Converted to physical pixels per-monitor.
    private const double LogicalWidth = 264;
    private const double LogicalHeight = 110;
    private const double MeterTrackWidth = 140;

    // The red "intelligence failed" flash holds the pill on screen briefly so the user can read it,
    // then hides itself. A Hide that arrives during the hold (the Idle-state hide right after
    // processing) is ignored until the hold elapses — ported from the WPF overlay's behaviour.
    private static readonly TimeSpan FailedHold = TimeSpan.FromMilliseconds(1300);

    private readonly IntPtr _hwnd;
    private readonly WindowId _windowId;
    private readonly AppWindow _appWindow;

    private OverlayState _state = OverlayState.Hidden;
    private bool _activatedOnce;

    private DispatcherQueueTimer? _failedTimer;
    private DateTime _failedHoldUntil = DateTime.MinValue;

    public OverlayWindow()
    {
        OverlayLog.Write("OverlayWindow.ctor enter");
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
        OverlayLog.Write($"OverlayWindow.ctor hwnd=0x{_hwnd.ToInt64():X} windowId={_windowId.Value}");

        ConfigurePresenter();
        ApplyExtendedStyles();

        // DWM-composition transparency: a window with no opaque backdrop shows whatever is behind it.
        SystemBackdrop = new TransparentBackdrop();
        OverlayLog.Write("OverlayWindow.ctor SystemBackdrop=TransparentBackdrop assigned");

        SizeAndPosition();

        // Lifecycle tracing — the whole point of the rebuild is to see every transition.
        Activated += OnActivated;
        Closed += OnClosed;
        VisibilityChanged += OnVisibilityChanged;
        _appWindow.Changed += OnAppWindowChanged;

        // Start hidden; the host (or App.OnLaunched in Phase 0) drives the first visible state.
        _appWindow.Hide();
        LogState("ctor.exit");
        OverlayLog.Write("OverlayWindow.ctor exit (hidden)");
    }

    private void ConfigurePresenter()
    {
        _appWindow.IsShownInSwitchers = false; // hidden from Alt-Tab / task switcher
        _appWindow.Title = "Scribe Overlay";

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            // NOTE: deliberately NOT using presenter.IsAlwaysOnTop — it is known to break
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
        var updated = ex
            | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, updated);
        OverlayLog.Write($"OverlayWindow.ApplyExtendedStyles ex=0x{ex:X}->0x{updated:X} (LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE)");

        RemoveDwmFrame();
    }

    // Windows 11's compositor draws a 1px non-client border and rounded corners on every top-level
    // window, even borderless ones — the visible rectangle around the otherwise transparent pill.
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

    private void SizeAndPosition()
    {
        var scale = DpiScale;
        var w = (int)Math.Round(LogicalWidth * scale);
        var h = (int)Math.Round(LogicalHeight * scale);
        _appWindow.Resize(new SizeInt32(w, h));

        var display = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        var x = work.X + (work.Width - w) / 2;
        var y = work.Y + work.Height - h - (int)Math.Round(8 * scale);
        _appWindow.Move(new PointInt32(x, y));

        OverlayLog.Write(
            $"OverlayWindow.SizeAndPosition scale={scale:0.##} size={w}x{h} pos={x},{y} " +
            $"work=({work.X},{work.Y},{work.Width},{work.Height})");
    }

    /// <summary>Switches the visible content panel and shows/hides the window. UI-thread marshalled.</summary>
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
            StopStoryboard("PulseStoryboard");
            StopStoryboard("ProcessingStoryboard");
            _appWindow.Hide();
            LogState("ShowState.hidden");
            OverlayLog.Write($"OverlayWindow.ShowState exit hidden (was {previous})");
            return;
        }

        ListeningContent.Visibility = state == OverlayState.Listening ? Visibility.Visible : Visibility.Collapsed;
        ProcessingContent.Visibility = state == OverlayState.Processing ? Visibility.Visible : Visibility.Collapsed;
        FailedContent.Visibility = state == OverlayState.Failed ? Visibility.Visible : Visibility.Collapsed;
        FailedTint.Visibility = state == OverlayState.Failed ? Visibility.Visible : Visibility.Collapsed;
        OuterGlow.Visibility = state == OverlayState.Failed ? Visibility.Collapsed : Visibility.Visible;
        OuterGlowInner.Visibility = state == OverlayState.Failed ? Visibility.Collapsed : Visibility.Visible;
        OuterGlowFailed.Visibility = state == OverlayState.Failed ? Visibility.Visible : Visibility.Collapsed;

        EnsureShown();

        if (state == OverlayState.Listening)
        {
            StopStoryboard("ProcessingStoryboard");
            StartStoryboard("PulseStoryboard");
        }
        else if (state == OverlayState.Processing)
        {
            StopStoryboard("PulseStoryboard");
            StartStoryboard("ProcessingStoryboard");
        }
        else
        {
            StopStoryboard("PulseStoryboard");
            StopStoryboard("ProcessingStoryboard");
        }

        LogState($"ShowState.{state}");
        OverlayLog.Write($"OverlayWindow.ShowState exit shown={state} (was {previous})");
    }

    // ---- High-level command surface (driven by the WPF engine over IPC) --------------------------

    /// <summary>Listening: pulsing record dot + live input-level meter (reset to empty).</summary>
    public void ShowRecording() => RunOnUi(() =>
    {
        ClearFailedHold();
        StatusText.Text = "Listening…";
        MeterFill.Width = 0;
        ShowState(OverlayState.Listening);
    });

    /// <summary>Processing: bouncing dots while transcribing / AI polishing.</summary>
    public void ShowProcessing(bool polishing) => RunOnUi(() =>
    {
        ClearFailedHold();
        ProcessingText.Text = polishing ? "Polishing…" : "Transcribing…";
        ShowState(OverlayState.Processing);
    });

    /// <summary>Brief red "intelligence failed" flash, then self-hides after the hold elapses.</summary>
    public void ShowFailed(string? reason) => RunOnUi(() =>
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            FailedReasonText.Text = reason!.Trim();
        }

        ShowState(OverlayState.Failed);

        _failedHoldUntil = DateTime.UtcNow + FailedHold;
        _failedTimer ??= CreateFailedTimer();
        _failedTimer.Stop();
        _failedTimer.Start();
        OverlayLog.Write($"OverlayWindow.ShowFailed hold={FailedHold.TotalMilliseconds:0}ms reason='{reason}'");
    });

    /// <summary>Hides the pill, unless the red failure flash is still holding on screen.</summary>
    public void Hide() => RunOnUi(() =>
    {
        if (DateTime.UtcNow < _failedHoldUntil)
        {
            OverlayLog.Write("OverlayWindow.Hide ignored (failed hold active)");
            return;
        }

        ShowState(OverlayState.Hidden);
    });

    /// <summary>Sets the live input-level meter width (0..1). No-op unless listening.</summary>
    public void SetMeter(double level) => RunOnUi(() =>
    {
        if (_state != OverlayState.Listening)
        {
            return;
        }

        var clamped = level < 0 ? 0 : level > 1 ? 1 : level;
        MeterFill.Width = MeterTrackWidth * clamped;
    });

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

    private void ClearFailedHold()
    {
        _failedHoldUntil = DateTime.MinValue;
        _failedTimer?.Stop();
    }

    private DispatcherQueueTimer CreateFailedTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = FailedHold;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _failedHoldUntil = DateTime.MinValue;
            OverlayLog.Write("OverlayWindow.FailedTimer.Tick");
            // Only hide if a fresh dictation hasn't already taken the pill over.
            if (_state == OverlayState.Failed)
            {
                ShowState(OverlayState.Hidden);
            }
        };
        return timer;
    }

    private void EnsureShown()
    {
        // Re-assert size/position in case the monitor/DPI changed between shows.
        SizeAndPosition();

        if (!_activatedOnce)
        {
            _activatedOnce = true;
            Activate(); // realises and shows the content (NOACTIVATE style avoids stealing focus)
            OverlayLog.Write("OverlayWindow.EnsureShown first Activate()");
        }
        else
        {
            _appWindow.Show(activateWindow: false);
            OverlayLog.Write("OverlayWindow.EnsureShown AppWindow.Show(activate:false)");
        }

        AssertTopMost();
    }

    private void AssertTopMost()
    {
        var ok = NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        OverlayLog.Write($"OverlayWindow.AssertTopMost SetWindowPos(HWND_TOPMOST) ok={ok}");
    }

    private void StartStoryboard(string key)
    {
        if (RootGrid.Resources.TryGetValue(key, out var res) && res is Storyboard sb)
        {
            sb.Begin();
            OverlayLog.Write($"OverlayWindow.StartStoryboard {key} begun");
        }
        else
        {
            OverlayLog.Warn($"OverlayWindow.StartStoryboard {key} not found");
        }
    }

    private void StopStoryboard(string key)
    {
        if (RootGrid.Resources.TryGetValue(key, out var res) && res is Storyboard sb)
        {
            sb.Stop();
        }
    }

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
        OverlayLog.Write("OverlayWindow.Closed");
    }

    /// <summary>One-line snapshot of the window's true state — the core diagnostic signal.</summary>
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
                $"backdrop={(SystemBackdrop is null ? "null" : SystemBackdrop.GetType().Name)}");
        }
        catch (Exception e)
        {
            OverlayLog.Error($"overlay[{phase}] LogState failed", e);
        }
    }
}
