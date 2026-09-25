using Scribe.Core.Lifecycle;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// Which stops release the hotkey latch of the press that started the recording, and when (the controller's shared stop
/// path begins with <see cref="DictationStopPolicy.BeginStop"/>, and has no tests of its own). A stop Scribe makes itself
/// must release it, or the next press is swallowed as the toggle-off of a dictation that already ended; a stop the hook
/// sent itself must not; and nothing is released before the lifecycle admits the stop, or for a stop it turns away.
/// </summary>
public sealed class DictationStopPolicyTests
{
    [Theory]
    [InlineData(DictationStopReason.SilenceAutoStop)]
    [InlineData(DictationStopReason.MicrophoneFault)]
    [InlineData(DictationStopReason.Paused)]
    [InlineData(DictationStopReason.DurationLimit)]
    public void A_stop_scribe_makes_itself_releases_the_hotkey_toggle(DictationStopReason reason) =>
        Assert.True(DictationStopPolicy.ReleasesHotkeyToggle(reason));

    [Theory]
    [InlineData(DictationStopReason.HotkeyReleased)]
    [InlineData(DictationStopReason.DesktopSwitch)]
    public void A_stop_the_hook_sent_itself_leaves_the_toggle_alone(DictationStopReason reason) =>
        Assert.False(DictationStopPolicy.ReleasesHotkeyToggle(reason));

    [Fact]
    public void Every_stop_reason_is_classified_above()
    {
        // A reason added later has to join one of the two lists, so its effect on the toggle is decided, not inherited.
        DictationStopReason[] classified =
        [
            DictationStopReason.SilenceAutoStop,
            DictationStopReason.MicrophoneFault,
            DictationStopReason.Paused,
            DictationStopReason.DurationLimit,
            DictationStopReason.HotkeyReleased,
            DictationStopReason.DesktopSwitch,
        ];
        Assert.Equal(Enum.GetValues<DictationStopReason>().Order(), classified.Order());
    }

    [Theory]
    [InlineData(DictationStopReason.SilenceAutoStop)]
    [InlineData(DictationStopReason.MicrophoneFault)]
    [InlineData(DictationStopReason.Paused)]
    [InlineData(DictationStopReason.DurationLimit)]
    public void A_stop_scribe_makes_releases_the_press_of_the_recording_it_admitted_once_admitted(DictationStopReason reason)
    {
        var lifecycle = RecordingStartedBy(activation: 42);
        var released = new List<(long Activation, DictationPhase Phase)>();

        var stop = DictationStopPolicy.BeginStop(
            lifecycle, reason, expectedDictationId: 1, c => released.Add((c.Activation, lifecycle.Phase)), out var failure);

        Assert.NotNull(stop.Admission);
        Assert.Null(failure);
        Assert.Equal(new[] { (42L, DictationPhase.Processing) }, released.ToArray());
    }

    [Theory]
    [InlineData(DictationStopReason.HotkeyReleased)]
    [InlineData(DictationStopReason.DesktopSwitch)]
    public void A_stop_the_hook_sent_is_admitted_and_releases_nothing(DictationStopReason reason)
    {
        var lifecycle = RecordingStartedBy(activation: 42);
        var released = 0;

        var stop = DictationStopPolicy.BeginStop(lifecycle, reason, expectedDictationId: 0, _ => released++, out _);

        Assert.NotNull(stop.Admission);
        Assert.Equal(0, released);
    }

    [Fact]
    public void A_stop_the_lifecycle_turns_away_releases_nothing()
    {
        // One meant for an earlier recording, and one that finds nothing recording: whatever the hook holds now belongs to
        // a press this stop knows nothing about.
        var lifecycle = RecordingStartedBy(activation: 42);
        var released = 0;

        Assert.Null(DictationStopPolicy.BeginStop(
            lifecycle, DictationStopReason.SilenceAutoStop, expectedDictationId: 7, _ => released++, out _).Admission);
        Assert.NotNull(DictationStopPolicy.BeginStop(
            lifecycle, DictationStopReason.HotkeyReleased, expectedDictationId: 0, _ => released++, out _).Admission);
        Assert.Null(DictationStopPolicy.BeginStop(
            lifecycle, DictationStopReason.MicrophoneFault, expectedDictationId: 0, _ => released++, out _).Admission);

        Assert.Equal(0, released);
    }

    [Fact]
    public void A_release_that_throws_is_handed_back_and_the_stop_stays_admitted()
    {
        // The admission is ended only by its processing, which has not started yet, so the throw must not escape.
        var lifecycle = RecordingStartedBy(activation: 42);
        var thrown = new InvalidOperationException("the hook is gone");

        var stop = DictationStopPolicy.BeginStop(
            lifecycle, DictationStopReason.MicrophoneFault, expectedDictationId: 0, _ => throw thrown, out var failure);

        Assert.NotNull(stop.Admission);
        Assert.Same(thrown, failure);
        Assert.Equal(DictationPhase.Processing, lifecycle.Phase);
    }

    private sealed record Capture(long Activation);

    private static DictationLifecycle<Capture> RecordingStartedBy(long activation)
    {
        var lifecycle = new DictationLifecycle<Capture>(() => { }, () => { }, new ManualTimeProvider());
        Assert.Equal(ActivationDecision.Started, lifecycle.TryBeginRecording(() => new Capture(activation)).Decision);
        return lifecycle;
    }
}
