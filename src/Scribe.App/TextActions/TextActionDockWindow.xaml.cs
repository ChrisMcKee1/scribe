using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Scribe.App.TextActions;

/// <summary>What the dock is currently communicating.</summary>
public enum DockState
{
    /// <summary>Nothing happening. Eyes closed, breathing slowly.</summary>
    Idle,

    /// <summary>Reading the selection right now. Eyes open.</summary>
    Reading,

    /// <summary>An action is running.</summary>
    Working,

    /// <summary>The action succeeded.</summary>
    Done,

    /// <summary>The action failed.</summary>
    Failed,
}

/// <summary>
/// The floating dock: a small always-on-top tile that opens the text action palette for whatever is
/// selected, without ever taking focus away from the app the text is in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than the tray menu.</b> Clicking a tray menu item makes Scribe the
/// foreground window, so by the time the handler runs the selection's owner has been deactivated and
/// the selection is no longer reachable. This window carries <c>WS_EX_NOACTIVATE</c>, which means a
/// click on it does not change activation at all: the app holding the text stays foreground, keeps
/// its selection, and a synthesized Ctrl+C reaches the right place. That property is the whole point
/// of the dock, not decoration.
/// </para>
/// <para>
/// <b>Why WPF here and WinUI for the pill.</b> The pill needs per-pixel transparency for its glow,
/// which is what dragged it onto the layered-window path and produced the documented "black box"
/// bug. This window is opaque with DWM-rounded corners, so it never uses that path.
/// </para>
/// <para>
/// The face carries personality, but colour and shape carry the meaning, so the state is still
/// readable at a glance and for anyone who cannot resolve a small expression.
/// </para>
/// </remarks>
public partial class TextActionDockWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(nint hWnd, int index, long value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    private Point _dragOrigin;
    private bool _dragging;
    private bool _moved;
    private Storyboard? _breathing;
    private Storyboard? _working;
    private DockState _state = DockState.Idle;

    /// <summary>Raised when the dock is clicked without being dragged.</summary>
    public event Action? Clicked;

    /// <summary>Raised after a drag, with the new position, so the host can persist it.</summary>
    public event Action<double, double>? Moved;

    public TextActionDockWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartIdleLoop();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // NOACTIVATE is the load-bearing one: it is what lets a click land here without the app
        // holding the user's selection losing activation. TOOLWINDOW keeps the dock out of Alt-Tab
        // and off the taskbar, which is what makes it read as a dock rather than an application.
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE);
        _ = SetWindowLongPtr(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // Rounded corners from DWM rather than from an AllowsTransparency layered window.
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>Moves the dock to a saved position, clamped into the current work area.</summary>
    public void PlaceAt(double left, double top)
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    /// <summary>Parks the dock at its default spot, above the tray.</summary>
    public void PlaceAtDefault()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    /// <summary>Sets the visible state. Colour and shape carry the meaning; the face adds character.</summary>
    public void SetState(DockState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;

        // Each state gets a ring colour, a two-stop fill for depth, an eye shape and a mouth shape.
        // Colour and shape both carry the meaning, so the state survives being glanced at in
        // peripheral vision and does not depend on resolving a small expression.
        var (ring, fillTop, fillBottom, eyesOpen, mouth) = state switch
        {
            // Slate, eyes closed: Scribe is reading nothing. True by construction, because the
            // selection is only ever read inside a window the user opens by clicking.
            DockState.Idle => ("#48484A", "#3A3A3E", "#242428", false, "M 20,31 L 26,31"),
            // Blue, eyes open: reading the selection right now.
            DockState.Reading => ("#0A84FF", "#123A5E", "#0A1E34", true, "M 20,31 L 26,31"),
            // Amber, thinking.
            DockState.Working => ("#FF9F0A", "#4A3410", "#2A1D06", true, "M 20,31 Q 23,33.5 26,31"),
            // Green, smiling.
            DockState.Done => ("#30D158", "#123A22", "#0A2214", true, "M 19,30 Q 23,34 27,30"),
            // Red, concerned.
            _ => ("#FF453A", "#4A1C18", "#2A100E", true, "M 19,32 Q 23,29 27,32"),
        };

        AnimateBrush(StateRing, Shape.StrokeProperty, ring);
        Halo.Fill = Brush(ring);
        FillTop.Color = Color(fillTop);
        FillBottom.Color = Color(fillBottom);
        LeftEyeOpen.Visibility = eyesOpen ? Visibility.Visible : Visibility.Collapsed;
        RightEyeOpen.Visibility = eyesOpen ? Visibility.Visible : Visibility.Collapsed;
        LeftEyeClosed.Visibility = eyesOpen ? Visibility.Collapsed : Visibility.Visible;
        RightEyeClosed.Visibility = eyesOpen ? Visibility.Collapsed : Visibility.Visible;
        SleepMarks.Visibility = state == DockState.Idle ? Visibility.Visible : Visibility.Collapsed;
        Mouth.Data = Geometry.Parse(mouth);

        // Each state owns exactly one continuous motion, and transient states fire a one-shot on top.
        // Stopping the loops first matters: breathing and the working pulse both drive scale
        // transforms, and leaving one running would fight the other for the same property.
        StopIdleLoop();
        StopWorkingPulse();

        switch (state)
        {
            case DockState.Idle:
                StartIdleLoop();
                break;

            case DockState.Working:
                StartWorkingPulse();
                break;

            case DockState.Done:
                Celebrate();
                break;

            case DockState.Failed:
                Shake();
                break;
        }
    }

    private static SolidColorBrush Brush(string hex) => new(Color(hex));

    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // State changes cross-fade rather than snapping. 180 ms decelerating is long enough to register
    // as a transition and short enough that the dock never feels laggy behind the work it reports.
    private static void AnimateBrush(Shape target, DependencyProperty property, string toHex)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.SetValue(property, Brush(toHex));
            return;
        }

        // Animate a private brush instance: mutating a shared or frozen brush would either throw or
        // silently recolour every element bound to it.
        var brush = new SolidColorBrush(((SolidColorBrush)target.GetValue(property))?.Color ?? Color(toHex));
        target.SetValue(property, brush);

        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = Color(toHex),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>The idle sleep loop: breathing, a gentle bob, and a stream of rising sleep marks.</summary>
    /// <remarks>
    /// <para>
    /// The numbers are chosen against one bar: alive in peripheral vision, ignorable while writing.
    /// A 2.6 second breath is slower than a resting human one, which is what makes it read as asleep
    /// rather than agitated, and 3.5 percent is under the threshold at which peripheral motion
    /// reliably pulls the eye. The bob shares the breath's period so the two look like one motion
    /// instead of two things happening to the same object.
    /// </para>
    /// <para>
    /// The sleep marks are three separate clocks offset by a third of a cycle each, rather than one
    /// glyph animating. A single repeating glyph reads as a blink; a stagger reads as a stream.
    /// </para>
    /// </remarks>
    private void StartIdleLoop()
    {
        // Honour the Windows "show animations" setting. PRODUCT.md requires reduced-motion behavior,
        // and a thing that moves forever in peripheral vision is the first thing to violate it.
        if (!SystemParameters.ClientAreaAnimation || _breathing is not null)
        {
            return;
        }

        const double BreathSeconds = 2.6;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        _breathing = new Storyboard();

        // Breath: scale the face on both axes together.
        foreach (var axis in (string[])["ScaleX", "ScaleY"])
        {
            var breath = new DoubleAnimation
            {
                From = 1.0,
                To = 1.035,
                Duration = TimeSpan.FromSeconds(BreathSeconds / 2),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(breath, BreathScale);
            Storyboard.SetTargetProperty(breath, new PropertyPath(axis));
            _breathing.Children.Add(breath);
        }

        // Bob: the face rises slightly as it inflates, in phase with the breath.
        var bob = new DoubleAnimation
        {
            From = 0.6,
            To = -0.9,
            Duration = TimeSpan.FromSeconds(BreathSeconds / 2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(bob, FaceBob);
        Storyboard.SetTargetProperty(bob, new PropertyPath("Y"));
        _breathing.Children.Add(bob);

        AddSleepMark(_breathing, Zed1, Zed1Scale, Zed1Move, 0.0);
        AddSleepMark(_breathing, Zed2, Zed2Scale, Zed2Move, 1.0);
        AddSleepMark(_breathing, Zed3, Zed3Scale, Zed3Move, 2.0);

        _breathing.Begin();
    }

    /// <summary>One rising sleep mark: fades in, drifts up and out, grows, fades away.</summary>
    private static void AddSleepMark(
        Storyboard board, UIElement glyph, ScaleTransform scale, TranslateTransform move, double delaySeconds)
    {
        const double CycleSeconds = 3.0;
        var begin = TimeSpan.FromSeconds(delaySeconds);
        var cycle = TimeSpan.FromSeconds(CycleSeconds);

        // Opacity is a triangle rather than a fade-out: a mark that appears at full strength looks
        // like it was switched on, and one that never reaches full strength looks like a rendering
        // fault. Peak at a third of the way through, then a long tail.
        var fade = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = begin,
            Duration = cycle,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromPercent(0.30)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.80)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
        Storyboard.SetTarget(fade, glyph);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        board.Children.Add(fade);

        // Rise, decelerating, so the mark slows as it fades rather than shooting off.
        var rise = new DoubleAnimation
        {
            From = 0,
            To = -16,
            BeginTime = begin,
            Duration = cycle,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, move);
        Storyboard.SetTargetProperty(rise, new PropertyPath("Y"));
        board.Children.Add(rise);

        // A little sideways drift stops the three marks looking like one column.
        var drift = new DoubleAnimation
        {
            From = 0,
            To = 5,
            BeginTime = begin,
            Duration = cycle,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(drift, move);
        Storyboard.SetTargetProperty(drift, new PropertyPath("X"));
        board.Children.Add(drift);

        foreach (var axis in (string[])["ScaleX", "ScaleY"])
        {
            var grow = new DoubleAnimation
            {
                From = 0.6,
                To = 1.25,
                BeginTime = begin,
                Duration = cycle,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, scale);
            Storyboard.SetTargetProperty(grow, new PropertyPath(axis));
            board.Children.Add(grow);
        }
    }

    private void StopIdleLoop()
    {
        _breathing?.Stop();
        _breathing = null;

        BreathScale.ScaleX = 1;
        BreathScale.ScaleY = 1;
        FaceBob.X = 0;
        FaceBob.Y = 0;

        foreach (var (glyph, scale, move) in ((UIElement, ScaleTransform, TranslateTransform)[])
            [(Zed1, Zed1Scale, Zed1Move), (Zed2, Zed2Scale, Zed2Move), (Zed3, Zed3Scale, Zed3Move)])
        {
            // Clear the animation before writing the property, or the animation's held value wins
            // and the marks stay frozen wherever they happened to be.
            glyph.BeginAnimation(OpacityProperty, null);
            glyph.Opacity = 0;
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    /// <summary>
    /// The success celebration: a spring-loaded bounce plus two expanding ring pulses.
    /// </summary>
    /// <remarks>
    /// Deliberately not confetti or particles. PRODUCT.md's anti-references name confetti explicitly,
    /// alongside gamification and arbitrary scores. A bounce and a pulse are the same beat of delight
    /// without the consumer-game register the brand rejects, and they cost two transforms rather than
    /// a particle system that would run on the same surface that sits on screen all day.
    /// </remarks>
    private void Celebrate()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        // Overshoot and settle. BackEase on the return is what makes it feel sprung rather than
        // merely scaled: it undershoots slightly before coming to rest.
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.16, KeyTime.FromPercent(0.30), new CubicEase { EasingMode = EasingMode.EaseOut }));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0, KeyTime.FromPercent(1.0),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }));

        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce.Clone());

        PulseRing(Ring1, Ring1Scale, 0);
        PulseRing(Ring2, Ring2Scale, 140);
    }

    /// <summary>One expanding ring: grows outward while fading to nothing.</summary>
    private static void PulseRing(UIElement ring, ScaleTransform scale, int delayMs)
    {
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = TimeSpan.FromMilliseconds(620);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = duration };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        ring.BeginAnimation(OpacityProperty, fade);

        foreach (var axis in (DependencyProperty[])[ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty])
        {
            scale.BeginAnimation(axis, new DoubleAnimation
            {
                From = 1.0,
                To = 1.9,
                BeginTime = begin,
                Duration = duration,
                EasingFunction = ease,
            });
        }
    }

    /// <summary>A single apologetic shake for the failure state.</summary>
    private void Shake()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
        foreach (var (at, offset) in ((double, double)[])
            [(0, 0), (0.15, -3), (0.35, 3), (0.55, -2), (0.75, 1.5), (1.0, 0)])
        {
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(
                offset, KeyTime.FromPercent(at), new SineEase { EasingMode = EasingMode.EaseInOut }));
        }

        FaceBob.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    /// <summary>A slow patient pulse while a model call is in flight.</summary>
    private void StartWorkingPulse()
    {
        if (!SystemParameters.ClientAreaAnimation || _working is not null)
        {
            return;
        }

        _working = new Storyboard();
        foreach (var axis in (string[])["ScaleX", "ScaleY"])
        {
            var pulse = new DoubleAnimation
            {
                From = 1.0,
                To = 1.06,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(pulse, RingScale);
            Storyboard.SetTargetProperty(pulse, new PropertyPath(axis));
            _working.Children.Add(pulse);
        }

        _working.Begin();
    }

    private void StopWorkingPulse()
    {
        _working?.Stop();
        _working = null;
        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RingScale.ScaleX = 1;
        RingScale.ScaleY = 1;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(this);
        _dragging = true;
        _moved = false;
        Press();
        _ = CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(this);
        var dx = position.X - _dragOrigin.X;
        var dy = position.Y - _dragOrigin.Y;

        // A few pixels of slop so a slightly shaky click is still a click, not a drag.
        if (!_moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
        {
            return;
        }

        _moved = true;
        PlaceAt(Left + dx, Top + dy);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        Scale(IsMouseOver ? 1.08 : 1.0, 160);

        if (_moved)
        {
            Moved?.Invoke(Left, Top);
            return;
        }

        Clicked?.Invoke();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => Scale(1.08, 140);

    private void OnMouseLeave(object sender, MouseEventArgs e) => Scale(1.0, 140);

    // A press dips the tile slightly before it springs back, which is the cheapest way to make a
    // click feel like it landed on a physical object rather than on a picture of one.
    private void Press() => Scale(0.92, 90);

    private void Scale(double to, int durationMs)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            PressScale.ScaleX = to;
            PressScale.ScaleY = to;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        PressScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        PressScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
    }
}
