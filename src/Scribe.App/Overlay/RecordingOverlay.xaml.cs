using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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

    private bool _subscribed;
    private bool _closing;
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
        PositionToWorkArea();

        if (!_subscribed)
        {
            _audio.LevelChanged += OnLevelChanged;
            _subscribed = true;
        }

        Show();
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

        ListeningContent.Visibility = Visibility.Collapsed;
        ProcessingContent.Visibility = Visibility.Visible;
        ProcessingText.Text = polishing ? "Polishing…" : "Transcribing…";

        PositionToWorkArea();
        if (!IsVisible)
        {
            Show();
        }

        _processing.Begin(this, isControllable: true);
    }

    /// <summary>Hides the overlay and stops metering; safe to call when already hidden.</summary>
    public void HideOverlay()
    {
        if (_subscribed)
        {
            _audio.LevelChanged -= OnLevelChanged;
            _subscribed = false;
        }

        _pulse.Stop(this);
        _processing.Stop(this);
        ResetMeter();
        Hide();
    }

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
    }

    private void OnLevelChanged(object? sender, float level)
    {
        // LevelChanged arrives on the capture thread; marshal to the UI thread without blocking it.
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible)
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
}
