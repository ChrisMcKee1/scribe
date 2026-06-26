using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

    public RecordingOverlay(IAudioCaptureService audio)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
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
        EnsureShown();
        PositionToWorkArea();
        Opacity = 1;
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
