namespace Scribe.Core.Settings;

/// <summary>What a busy indicator's owner does after telling <see cref="BusyAnimationLifecycle"/> about a change.</summary>
public enum BusyAnimationChange
{
    /// <summary>Nothing: the animation is already in the state the indicator needs.</summary>
    None,

    /// <summary>Start the animation afresh.</summary>
    Start,

    /// <summary>Stop the animation, so no clock is left for WPF to tick.</summary>
    Stop,
}

/// <summary>
/// Decides when a Settings busy indicator (an AI cleanup status row's spinner, a Dictionary progress bar) animates: only
/// while it is shown, and never once its window has closed. "Shown" is the control's effective visibility (WPF's
/// <c>IsVisible</c>), which folds in its own busy visibility (what the code sets while work runs), a hidden page, a
/// collapsed ancestor, a tree that was unloaded and a closed window, so a spinner still busy on a page the user left stops,
/// and starts again when the page comes back, with no status update.
/// </summary>
/// <remarks>
/// WPF keeps scheduling a render pass every vsync while any animation clock is interactively paused (WPF-UI's indeterminate
/// ProgressRing pauses its storyboard when it hides), and WPF's ProgressBar only detaches its glow clock when it hides, which
/// leaves it running: either way the UI thread keeps rendering for something nobody sees. The owner therefore stops the animation whenever
/// this says <see cref="BusyAnimationChange.Stop"/> and starts it afresh on <see cref="BusyAnimationChange.Start"/>. A
/// status update can still arrive after the window closed (a posted refresh), so a closed lifecycle never starts again.
/// </remarks>
public sealed class BusyAnimationLifecycle
{
    private bool _shown;

    /// <summary>Whether the owner should have its animation running now.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Whether <see cref="Close"/> was called.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>
    /// Reports whether the indicator is shown now and returns what its owner does about it. Reporting the same state again
    /// returns <see cref="BusyAnimationChange.None"/>, so the owner can call this from every event it gets.
    /// </summary>
    public BusyAnimationChange Update(bool shown)
    {
        _shown = shown;
        return Settle();
    }

    /// <summary>The window closed: stop for good.</summary>
    public BusyAnimationChange Close()
    {
        IsClosed = true;
        return Settle();
    }

    private BusyAnimationChange Settle()
    {
        var run = !IsClosed && _shown;
        if (run == IsRunning)
        {
            return BusyAnimationChange.None;
        }

        IsRunning = run;
        return run ? BusyAnimationChange.Start : BusyAnimationChange.Stop;
    }
}
