using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scribe.Core.Settings;
using WpfUiProgressRing = Wpf.Ui.Controls.ProgressRing;

namespace Scribe.App.Settings;

/// <summary>
/// Stops a Settings status row's busy spinner whenever it is not shown (StopInactiveProgress). WPF-UI's indeterminate
/// ProgressRing begins its storyboard when its arc loads and only pauses it when the ring hides, and WPF keeps scheduling a
/// render pass every vsync for as long as a paused clock exists: that kept the UI thread working while Settings sat idle.
/// Here the ring is indeterminate only while it is shown: hiding it (its busy state ending, its page or an ancestor
/// collapsing, its tree unloading, the window closing) stops and removes the template's storyboard and makes it
/// determinate, and showing it again makes it indeterminate, which gives it a fresh template whose arc begins the same
/// storyboard from the start, as WPF-UI's pause, seek to zero and resume did. A ring that is shown but disabled keeps
/// WPF-UI's own pause.
/// </summary>
internal sealed class BusyRingAnimation
{
    private readonly WpfUiProgressRing _ring;
    private readonly BusyAnimationLifecycle _lifecycle = new();
    private IReadOnlyList<Storyboard> _storyboards = [];
    private bool _recheckPending;

    public BusyRingAnimation(WpfUiProgressRing ring)
    {
        _ring = ring;
        _ring.IsVisibleChanged += OnIsVisibleChanged;
        Apply(_lifecycle.Update(_ring.IsVisible));
        if (!_lifecycle.IsRunning)
        {
            _ring.IsIndeterminate = false;
        }
    }

    /// <summary>The window closed: stop for good, and let nothing that arrives later start the ring again.</summary>
    public void Close()
    {
        _ring.IsVisibleChanged -= OnIsVisibleChanged;
        Apply(_lifecycle.Close());
        StopStoryboards();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Apply(_lifecycle.Update(_ring.IsVisible));

    private void Apply(BusyAnimationChange change)
    {
        switch (change)
        {
            case BusyAnimationChange.Start:
                _ring.IsIndeterminate = true;
                break;
            case BusyAnimationChange.Stop:
                var storyboards = BusyAnimationTemplates.Storyboards(_ring.Template);
                if (storyboards.Count > 0)
                {
                    _storyboards = storyboards;
                }

                StopStoryboards();
                _ring.IsIndeterminate = false;
                ScheduleRecheck();
                break;
        }
    }

    // An arc that loads after the stop (a template applied and hidden again before its Loaded event ran) would begin the
    // storyboard on a ring that is not shown; look again once every pending Loaded event has been raised.
    private void ScheduleRecheck()
    {
        if (_recheckPending)
        {
            return;
        }

        _recheckPending = true;
        _ring.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _recheckPending = false;
            if (!_lifecycle.IsRunning)
            {
                StopStoryboards();
            }
        });
    }

    private void StopStoryboards()
    {
        foreach (var storyboard in _storyboards)
        {
            // WPF keeps a template trigger's storyboard clock on the templated control, which is where WPF-UI's own pause
            // and seek find it; stopping and removing one that was never begun there does nothing.
            storyboard.Stop(_ring);
            storyboard.Remove(_ring);
        }
    }
}

/// <summary>
/// Runs a Dictionary busy bar's sweep on a clock this owns (StopInactiveProgress). WPF's ProgressBar animates its
/// <c>PART_GlowRect</c> itself and, when the bar hides, only detaches that animation, which leaves its clock running, so
/// WPF kept scheduling render passes after the busy state ended. The bar gets WPF-UI's own style with the glow renamed
/// (BusyBarStyle.xaml), so ProgressBar's code no longer touches it, and this runs the same sweep, computed as ProgressBar
/// computes it (<see cref="BusyBarSweep"/>), at the same moments ProgressBar starts its own: whenever the bar becomes shown,
/// and whenever its track changes size while it is shown (ProgressBar's own handler for that event, attached when the
/// template is applied, runs first and sets the indicator's length this reads); hiding the bar removes the animation, as
/// ProgressBar does, and also stops its clock. Scribe never changes a busy bar's IsIndeterminate, Style or Template after
/// this attaches, so the template parts found on each start stay the ones on screen.
/// </summary>
/// <remarks>
/// ProgressBar also gives its glow a gradient: it sets a Shape's Fill, which a Border does not have, and, only when the
/// Foreground is not a solid brush, an opacity mask. Every WPF-UI theme's ProgressBarForeground is a SolidColorBrush and
/// Scribe never sets a bar's Foreground, so renaming the part leaves nothing drawn differently.
/// </remarks>
internal sealed class BusyBarAnimation
{
    internal const string StyleKey = "ScribeBusyBarStyle";
    internal const string GlowName = "ScribeBusyGlow";
    internal const string IndicatorName = "PART_Indicator";
    internal const string TrackName = "PART_Track";

    private static readonly Uri StyleSource = new("/Scribe;component/Settings/BusyBarStyle.xaml", UriKind.Relative);

    private readonly ProgressBar _bar;
    private readonly BusyAnimationLifecycle _lifecycle = new();
    private FrameworkElement? _track;
    private FrameworkElement? _indicator;
    private FrameworkElement? _glow;
    private AnimationClock? _clock;

    public BusyBarAnimation(ProgressBar bar, Style style)
    {
        _bar = bar;
        _bar.Style = style;
        _bar.IsVisibleChanged += OnIsVisibleChanged;
        Apply(_lifecycle.Update(IsShown));
    }

    /// <summary>Loads the bar style: WPF-UI's ProgressBar style with its glow renamed.</summary>
    public static Style LoadStyle() => (Style)((ResourceDictionary)Application.LoadComponent(StyleSource))[StyleKey];

    /// <summary>The window closed: stop for good.</summary>
    public void Close()
    {
        _bar.IsVisibleChanged -= OnIsVisibleChanged;
        if (_track is not null)
        {
            _track.SizeChanged -= OnTrackSizeChanged;
        }

        Apply(_lifecycle.Close());
        StopClock();
    }

    private bool IsShown => _bar.IsVisible && _bar.IsIndeterminate;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Apply(_lifecycle.Update(IsShown));

    private void Apply(BusyAnimationChange change)
    {
        switch (change)
        {
            case BusyAnimationChange.Start:
                FindParts();
                Sweep();
                break;
            case BusyAnimationChange.Stop:
                StopClock();
                break;
        }
    }

    // On the first show the template has not been applied yet; applying it now (layout would, before the first frame) runs
    // ProgressBar's OnApplyTemplate, which attaches its own track handler before this one, so the first layout sets the
    // indicator's length and then starts the sweep here, in the same layout pass as ProgressBar would have started its own.
    private void FindParts()
    {
        _bar.ApplyTemplate();
        var track = _bar.Template?.FindName(TrackName, _bar) as FrameworkElement;
        var indicator = _bar.Template?.FindName(IndicatorName, _bar) as FrameworkElement;
        var glow = _bar.Template?.FindName(GlowName, _bar) as FrameworkElement;
        if (!ReferenceEquals(track, _track))
        {
            if (_track is not null)
            {
                _track.SizeChanged -= OnTrackSizeChanged;
            }

            _track = track;
            if (_track is not null)
            {
                _track.SizeChanged += OnTrackSizeChanged;
            }
        }

        _indicator = indicator;
        if (!ReferenceEquals(glow, _glow))
        {
            StopClock();
            _glow = glow;
        }
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lifecycle.IsRunning)
        {
            Sweep();
        }
    }

    // ProgressBar.UpdateAnimation, on this bar's own clock: the sweep for the indicator's and glow's widths, begun as far
    // back as the glow already is, replacing the one running; no sweep where ProgressBar would not animate.
    private void Sweep()
    {
        if (_indicator is null || _glow is null ||
            BusyBarSweep.For(_indicator.Width, _glow.Width, _glow.Margin.Left) is not { } sweep)
        {
            StopClock();
            return;
        }

        var animation = new ThicknessAnimationUsingKeyFrames
        {
            BeginTime = sweep.BeginTime,
            Duration = new Duration(sweep.Duration),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        animation.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(sweep.StartLeft, 0, 0, 0), KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(sweep.EndLeft, 0, 0, 0), KeyTime.FromTimeSpan(sweep.Travel)));

        var replaced = _clock;
        _clock = animation.CreateClock();
        _glow.ApplyAnimationClock(FrameworkElement.MarginProperty, _clock);
        replaced?.Controller?.Stop();
    }

    private void StopClock()
    {
        if (_clock is not { } clock)
        {
            return;
        }

        _clock = null;
        _glow?.ApplyAnimationClock(FrameworkElement.MarginProperty, null);
        clock.Controller?.Stop();
    }
}

/// <summary>Finds the storyboards a control template's triggers begin.</summary>
internal static class BusyAnimationTemplates
{
    public static IReadOnlyList<Storyboard> Storyboards(ControlTemplate? template)
    {
        if (template is null)
        {
            return [];
        }

        var found = new List<Storyboard>();
        foreach (var trigger in template.Triggers)
        {
            if (trigger is EventTrigger eventTrigger)
            {
                Collect(eventTrigger.Actions, found);
            }

            Collect(trigger.EnterActions, found);
            Collect(trigger.ExitActions, found);
        }

        return found;
    }

    private static void Collect(TriggerActionCollection actions, List<Storyboard> found)
    {
        foreach (var action in actions)
        {
            if (action is BeginStoryboard { Storyboard: { } storyboard } && !found.Contains(storyboard))
            {
                found.Add(storyboard);
            }
        }
    }
}
