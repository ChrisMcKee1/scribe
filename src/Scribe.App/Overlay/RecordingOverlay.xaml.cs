using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;

namespace Scribe.App.Overlay;

/// <summary>
/// A small always-on-top, click-through recording indicator shown while the microphone is
/// capturing. It renders a pulsing record dot and a live input-level meter fed by
/// <see cref="IAudioCaptureService.LevelChanged"/>. The window never takes focus and lets all
/// mouse input pass straight through to whatever is underneath, so it can sit over the app the
/// user is dictating into without getting in the way.
/// </summary>
public partial class RecordingOverlay : Window
{
    private const double TrackWidth = 168;
    private const double BottomMargin = 12;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;

    private readonly IAudioCaptureService _audio;
    private readonly ILogger? _log;
    private readonly Storyboard _pulse;
    private readonly Storyboard _processing;

    // The red "intelligence failed" flash holds the overlay on screen briefly so the user can read
    // it, then hides itself. The Idle-state hide that arrives right after processing is suppressed
    // until the hold elapses (see HideOverlay).
    private static readonly TimeSpan FailedHold = TimeSpan.FromMilliseconds(1300);
    private DispatcherTimer? _failedTimer;
    private DateTime _failedHoldUntil = DateTime.MinValue;

    private bool _subscribed;
    private bool _closing;
    private bool _shown;
    private float _meterLevel;

    public RecordingOverlay(IAudioCaptureService audio, ILogger<RecordingOverlay>? log = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _log = log;
        InitializeComponent();
        _pulse = (Storyboard)Resources["PulseStoryboard"];
        _processing = (Storyboard)Resources["ProcessingStoryboard"];
    }

    /// <summary>Positions and shows the overlay, starts the pulse and begins metering input.</summary>
    public void ShowRecording()
    {
        if (_closing)
        {
            return;
        }

        _processing.Stop(this);
        ProcessingContent.Visibility = Visibility.Collapsed;
        ListeningContent.Visibility = Visibility.Visible;
        StatusText.Text = "Listening…";
        ResetMeter();
        ClearFailed();

        if (!_subscribed)
        {
            _audio.LevelChanged += OnLevelChanged;
            _subscribed = true;
        }

        Reveal();
        _pulse.Begin(this, isControllable: true);
        LogState("ShowRecording.done");
    }

    /// <summary>
    /// Switches the overlay to its processing state — a row of bouncing dots — shown after the key
    /// is released while the capture is transcribed and (optionally) polished by the AI model so the
    /// user can see that work is happening.
    /// </summary>
    public void ShowProcessing(bool polishing)
    {
        if (_closing)
        {
            return;
        }

        // Capture has stopped, so drop the live level meter and the record-dot pulse.
        if (_subscribed)
        {
            _audio.LevelChanged -= OnLevelChanged;
            _subscribed = false;
        }

        _pulse.Stop(this);
        ResetMeter();
        ClearFailed();

        ListeningContent.Visibility = Visibility.Collapsed;
        ProcessingContent.Visibility = Visibility.Visible;
        ProcessingText.Text = polishing ? "Polishing…" : "Transcribing…";

        Reveal();
        _processing.Begin(this, isControllable: true);
        LogState("ShowProcessing.done");
    }

    /// <summary>
    /// Flashes the overlay red with an "Intelligence failed" notice when AI cleanup failed at runtime
    /// and the dictation fell back to raw transcription. Holds briefly so the user can read it, then
    /// hides itself; a new dictation arriving during the hold takes over immediately.
    /// </summary>
    public void ShowFailed(string? reason)
    {
        if (_closing)
        {
            return;
        }

        // Capture has long since stopped; ensure no metering or animations remain active.
        if (_subscribed)
        {
            _audio.LevelChanged -= OnLevelChanged;
            _subscribed = false;
        }

        _pulse.Stop(this);
        _processing.Stop(this);
        ResetMeter();

        ListeningContent.Visibility = Visibility.Collapsed;
        ProcessingContent.Visibility = Visibility.Collapsed;
        FailedContent.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            FailedReasonText.Text = reason!.Trim();
        }

        SetFailedVisual(true);
        Reveal();

        _failedHoldUntil = DateTime.UtcNow + FailedHold;
        _failedTimer ??= CreateFailedTimer();
        _failedTimer.Stop();
        _failedTimer.Start();
        LogState("ShowFailed.done");
    }

    /// <summary>Hides the overlay and stops metering; safe to call when already hidden.</summary>
    public void HideOverlay()
    {
        // While the red failure flash is holding, ignore the Idle-state hide that arrives immediately
        // after processing — the failure timer hides the overlay once the hold elapses. Shutdown
        // (_closing) bypasses this so teardown always hides.
        if (!_closing && DateTime.UtcNow < _failedHoldUntil)
        {
            return;
        }

        LogState("HideOverlay.enter");

        if (_subscribed)
        {
            _audio.LevelChanged -= OnLevelChanged;
            _subscribed = false;
        }

        _pulse.Stop(this);
        _processing.Stop(this);
        ResetMeter();
        ClearFailed();

        // Keep the layered window shown and only drop its opacity to hide. Calling Hide()/Show()
        // each cycle re-presents the AllowsTransparency surface, which intermittently leaks one
        // opaque (black) frame before per-pixel alpha is composited — the transient dark box the
        // user sees. Toggling Opacity on the already-shown window animates the existing composition
        // and never re-presents, so the artifact cannot occur.
        Opacity = 0;

        // Park the now-transparent window off-screen. On some GPU/DWM configurations a layered
        // window that drops to Opacity 0 keeps its last-presented (opaque) frame composited at its
        // on-screen position until that screen region is invalidated — leaving a dark rounded box
        // lingering where the pill was. Moving the window forces DWM to recomposite the vacated
        // region (clearing the stale frame) without re-creating the surface, so hide is reliable.
        // Reveal() repositions to the work area before raising opacity again, so there is no flash.
        PositionOffScreen();
        LogState("HideOverlay.exit");
    }

    /// <summary>
    /// Presents the layered window once, off-screen and fully transparent, so its composition
    /// surface is established before the user ever sees it. Call once after construction. Subsequent
    /// shows merely reposition and fade in, avoiding the transient black frame WPF emits when an
    /// <c>AllowsTransparency</c> window is presented from a hidden state.
    /// </summary>
    public void Warmup() => EnsureShown();

    /// <summary>Permanently tears down the overlay during application shutdown.</summary>
    public void CloseOverlay()
    {
        _closing = true;
        HideOverlay();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Click-through (TRANSPARENT) + never-activate (NOACTIVATE) + hidden from Alt+Tab
        // (TOOLWINDOW). WPF already sets LAYERED for AllowsTransparency; we re-assert it so the
        // TRANSPARENT hit-test pass-through is reliable regardless of init order.
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        // Belt-and-suspenders for the transient "dark rounded box" artifact: tell DWM never to round
        // this window's corners. The window is a per-pixel-alpha layered surface whose visible shape is
        // the WPF Card; if a re-present ever briefly leaked the window's own backing before the alpha
        // composited, Win11's automatic corner rounding is what made that leak read as a rounded box.
        // DONOTROUND removes that failure mode entirely (no-op on Win10, which ignores the attribute).
        var pref = (int)DwmWindowCornerPreference.DoNotRound;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));

        EnsureSoftwareRendering("OnSourceInitialized");
        LogState("OnSourceInitialized");
    }

    // Pins this overlay window to WPF's software rendering pipeline and KEEPS it pinned. This is the
    // root-cause mitigation for the recurring transient "black box" behind the pill.
    //
    // The overlay is an AllowsTransparency (per-pixel-alpha) layered window. On the default hardware path
    // WPF composes it through DirectComposition, whose redirection-surface back-buffer is opaque black; on
    // a show/move/resize/monitor-DPI change or a GPU-driver hiccup that black back-buffer can present for
    // one frame before WPF's alpha-correct content lands — exactly the dark rectangle the user sees.
    // .NET 10 makes this worse (cf. dotnet/wpf #11321, an uninitialised BLENDFUNCTION leaving AlphaFormat=0
    // so UpdateLayeredWindow rendered transparent pixels black), and brand-new GPUs/drivers stress the
    // hardware path further. Software rendering rasterises to a 32-bpp DIB and calls UpdateLayeredWindow
    // with premultiplied per-pixel alpha every frame, so there is no GPU back-buffer that can leak.
    //
    // CRITICAL: a DPI/monitor change (or other surface re-creation) can rebuild the window's HwndTarget and
    // reset RenderMode back to the default hardware path — silently re-introducing the black box even after
    // it was set once at init. That is why this is RE-ASSERTED on every reveal and DPI change rather than
    // set once. Setting it on this window's HwndTarget only keeps every other window hardware-accelerated.
    //
    // Note: this is also why a GPU-accelerated visualization (WebView2 / D3DImage / SwapChainPanel) cannot
    // be hosted *inside* this window — a hardware child surface cannot composite into a software layered
    // window (WPF airspace). Such a surface must live in a separate opaque window over the card.
    private void EnsureSoftwareRendering(string when)
    {
        var src = PresentationSource.FromVisual(this) as HwndSource
                  ?? HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (src?.CompositionTarget is not { } target)
        {
            _log?.LogWarning("overlay[{When}] CompositionTarget unavailable; SoftwareOnly NOT applied", when);
            return;
        }

        var before = target.RenderMode;
        if (before == RenderMode.SoftwareOnly)
        {
            return;
        }

        target.RenderMode = RenderMode.SoftwareOnly;

        // Anything other than SoftwareOnly here means the hardware path was (re)active — the exact
        // condition that leaks the black box. Log it loudly so a repro pinpoints when the surface reverted,
        // and confirm the re-assert took.
        _log?.LogWarning(
            "overlay[{When}] RenderMode was {Before}; re-asserted SoftwareOnly (now {After})",
            when, before, target.RenderMode);
    }

    // One-line snapshot of the overlay's window/composition state, logged at every lifecycle transition so
    // an intermittent-black-box repro can be correlated against exactly what the window was doing.
    private void LogState(string phase)
    {
        if (_log is null)
        {
            return;
        }

        var render = "n/a";
        var dpi = 1.0;
        try
        {
            if ((PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget is { } ct)
            {
                render = ct.RenderMode.ToString();
            }

            dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch
        {
            // Diagnostics are best-effort; never let logging throw on the UI thread.
        }

        var content =
            FailedContent.Visibility == Visibility.Visible ? "Failed"
            : ProcessingContent.Visibility == Visibility.Visible ? "Processing"
            : ListeningContent.Visibility == Visibility.Visible ? "Listening"
            : "none";

        var holdMs = Math.Max(0, (_failedHoldUntil - DateTime.UtcNow).TotalMilliseconds);

        _log.LogInformation(
            "overlay[{Phase}] op={Op:0.00} vis={Vis} isVis={IsVis} wstate={WState} render={Render} "
            + "L={L:0} T={T:0} W={W:0} H={H:0} aw={AW:0} ah={AH:0} dpi={Dpi:0.00} content={Content} "
            + "shown={Shown} sub={Sub} closing={Closing} holdMs={Hold:0}",
            phase, Opacity, Visibility, IsVisible, WindowState, render,
            Left, Top, Width, Height, ActualWidth, ActualHeight, dpi, content,
            _shown, _subscribed, _closing, holdMs);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // A DPI/monitor change can recreate the HwndTarget and revert RenderMode to the leaky hardware
        // path, so re-assert software rendering immediately and record the transition.
        _log?.LogInformation("overlay[DpiChanged] {Old:0.00}->{New:0.00}", oldDpi.DpiScaleX, newDpi.DpiScaleX);
        EnsureSoftwareRendering("DpiChanged");
        LogState("DpiChanged");
    }

    private void OnLevelChanged(object? sender, float level)
    {
        // LevelChanged arrives on the capture thread; marshal to the UI thread without blocking it.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || Opacity <= 0)
            {
                return;
            }

            // Perceptual curve so quiet speech still moves the meter, with a fast attack and a
            // gentle decay so the bar reads like a VU meter rather than flickering.
            var v = level <= 0f ? 0f : MathF.Sqrt(Math.Min(1f, level));
            _meterLevel = v > _meterLevel ? v : (_meterLevel * 0.82f) + (v * 0.18f);
            MeterFill.Width = TrackWidth * _meterLevel;
        });
    }

    private void ResetMeter()
    {
        _meterLevel = 0f;
        MeterFill.Width = 0;
    }

    // Swaps the card's glow/wash between the normal (blue) and failed (red) look.
    private void SetFailedVisual(bool failed)
    {
        FailedTint.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        OuterGlowFailed.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        OuterGlow.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        if (!failed)
        {
            FailedContent.Visibility = Visibility.Collapsed;
        }
    }

    // Cancels any in-flight failure flash and restores the normal visual, so a fresh dictation (or a
    // shutdown) never inherits the red state.
    private void ClearFailed()
    {
        _failedHoldUntil = DateTime.MinValue;
        _failedTimer?.Stop();
        SetFailedVisual(false);
    }

    private DispatcherTimer CreateFailedTimer()
    {
        var timer = new DispatcherTimer { Interval = FailedHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _failedHoldUntil = DateTime.MinValue;
            LogState("FailedTimer.Tick");
            // Only hide if a new dictation hasn't already taken over the overlay.
            if (FailedContent.Visibility == Visibility.Visible)
            {
                HideOverlay();
            }
        };
        return timer;
    }

    // Brings the (already-shown) overlay on-screen and fades it in. The window stays presented for
    // its lifetime; only its position and opacity change, so the layered surface is never re-created.
    private void Reveal()
    {
        LogState("Reveal.enter");
        EnsureShown();
        EnsureSoftwareRendering("Reveal");
        PositionToWorkArea();
        Opacity = 1;
        LogState("Reveal.exit");
    }

    private void EnsureShown()
    {
        if (_shown || _closing)
        {
            return;
        }

        Opacity = 0;
        PositionOffScreen();
        Show();
        _shown = true;
        LogState("EnsureShown.shown");
    }

    private void PositionOffScreen()
    {
        // Park fully below the entire virtual desktop (all monitors) rather than at a magic negative
        // constant, so the transparent layered surface is guaranteed off every monitor regardless of
        // multi-monitor layout or negative-coordinate arrangements. Coordinates are in device-
        // independent units, matching PositionToWorkArea.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + Height;
    }

    private void PositionToWorkArea()
    {
        // SystemParameters.WorkArea is in device-independent units for the primary monitor, so the
        // placement is DPI-correct without manual scaling.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Bottom - Height - BottomMargin;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    // DWM corner-rounding control (Windows 11 22000+). Ignored on older builds.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
