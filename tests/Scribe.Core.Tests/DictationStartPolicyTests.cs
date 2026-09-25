using Scribe.Core.Lifecycle;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// A hotkey press the lifecycle turns away because the previous dictation is still processing releases its own latch
/// (the controller's activation path begins with <see cref="DictationStartPolicy.BeginRecording"/>, and has no tests of
/// its own). A press that starts a recording keeps its latch, and so do the other refusals.
/// </summary>
public sealed class DictationStartPolicyTests
{
    [Fact]
    public void A_press_turned_away_while_the_previous_dictation_processes_releases_its_latch()
    {
        var lifecycle = NewLifecycle();
        Assert.Equal(ActivationDecision.Started, lifecycle.TryBeginRecording(() => new Capture()).Decision);
        Assert.NotNull(lifecycle.TryBeginProcessing().Admission);
        var released = 0;

        var activation = DictationStartPolicy.BeginRecording(lifecycle, () => new Capture(), () => released++);

        Assert.Equal(ActivationDecision.StillProcessing, activation.Decision);
        Assert.Equal(1, released);
    }

    [Fact]
    public void A_press_that_starts_a_recording_keeps_its_latch()
    {
        var lifecycle = NewLifecycle();
        var released = 0;

        var activation = DictationStartPolicy.BeginRecording(lifecycle, () => new Capture(), () => released++);

        Assert.Equal(ActivationDecision.Started, activation.Decision);
        Assert.NotNull(activation.Capture);
        Assert.Equal(0, released);
    }

    [Fact]
    public void The_other_refusals_keep_the_latch()
    {
        // Paused: the hook's own pause clears every latch. Already recording: this press is what can still end the live
        // recording. Closing: the hook is about to stop.
        var released = 0;

        var paused = NewLifecycle();
        paused.SetPaused(true);
        Assert.Equal(
            ActivationDecision.Paused,
            DictationStartPolicy.BeginRecording(paused, () => new Capture(), () => released++).Decision);

        var recording = NewLifecycle();
        Assert.Equal(ActivationDecision.Started, recording.TryBeginRecording(() => new Capture()).Decision);
        Assert.Equal(
            ActivationDecision.AlreadyRecording,
            DictationStartPolicy.BeginRecording(recording, () => new Capture(), () => released++).Decision);

        var closing = NewLifecycle();
        Assert.True(closing.BeginShutdown());
        Assert.Equal(
            ActivationDecision.Closing,
            DictationStartPolicy.BeginRecording(closing, () => new Capture(), () => released++).Decision);

        Assert.Equal(0, released);
    }

    [Fact]
    public void Every_activation_decision_is_classified_above()
    {
        // A decision added later has to join the tests above, so its effect on the press's latch is decided, not inherited.
        ActivationDecision[] classified =
        [
            ActivationDecision.StillProcessing,
            ActivationDecision.Started,
            ActivationDecision.Paused,
            ActivationDecision.AlreadyRecording,
            ActivationDecision.Closing,
        ];
        Assert.Equal(Enum.GetValues<ActivationDecision>().Order(), classified.Order());
    }

    private sealed class Capture;

    private static DictationLifecycle<Capture> NewLifecycle() => new(() => { }, () => { }, new ManualTimeProvider());
}
