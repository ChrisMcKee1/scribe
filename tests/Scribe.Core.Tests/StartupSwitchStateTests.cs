using Scribe.Core.Infrastructure;
using Windows.ApplicationModel;

namespace Scribe.Core.Tests;

public sealed class StartupSwitchStateTests
{
    private static readonly StartupRegistrationStatus Disabled = new(StartupTaskState.Disabled);
    private static readonly StartupRegistrationStatus Enabled = new(StartupTaskState.Enabled);

    [Fact]
    public void Only_the_first_read_disables_the_switch()
    {
        var state = new StartupSwitchState();

        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var first));
        Assert.True(state.DisablesSwitchWhileRefreshing);
        Assert.True(state.CompleteRefresh(first, Disabled));

        // A read run because the window was activated must leave the switch usable, or the click
        // that activated the window lands on a disabled switch and is lost.
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out _));
        Assert.False(state.DisablesSwitchWhileRefreshing);
    }

    [Fact]
    public void Read_with_nothing_in_between_is_shown()
    {
        var state = Showing(Disabled);

        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));
        Assert.True(state.CompleteRefresh(ticket, Enabled));

        Assert.Same(Enabled, state.Shown);
    }

    [Fact]
    public void Flip_during_a_read_is_applied_and_the_read_finishing_first_is_discarded()
    {
        var state = Showing(Disabled);
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));

        // The first click after returning to Settings arrives while the activation read runs.
        Assert.Equal(StartupFlipDecision.Apply, state.TryBeginApply(true, saveInProgress: false, settingsRecovered: false));
        Assert.True(state.IsApplying);

        Assert.False(state.CompleteRefresh(ticket, Disabled));
        Assert.Same(Disabled, state.Shown);

        state.CompleteApply(Enabled);
        Assert.False(state.IsApplying);
        Assert.Same(Enabled, state.Shown);
    }

    [Fact]
    public void Read_that_began_before_a_flip_and_finishes_after_it_is_discarded()
    {
        var state = Showing(Disabled);
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));
        Assert.Equal(StartupFlipDecision.Apply, state.TryBeginApply(true, saveInProgress: false, settingsRecovered: false));
        state.CompleteApply(Enabled);

        // The registration gate ran the read first, so it saw the state from before the change.
        Assert.False(state.CompleteRefresh(ticket, Disabled));
        Assert.Same(Enabled, state.Shown);

        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var next));
        Assert.True(state.CompleteRefresh(next, Enabled));
    }

    [Fact]
    public void Save_read_supersedes_an_older_read_still_running()
    {
        var state = Showing(Disabled);
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));

        state.Show(Enabled);

        Assert.False(state.CompleteRefresh(ticket, Disabled));
        Assert.Same(Enabled, state.Shown);
    }

    [Fact]
    public void No_read_starts_while_a_read_a_flip_or_a_save_is_running()
    {
        var state = Showing(Disabled);

        Assert.False(state.TryBeginRefresh(saveInProgress: true, out _));

        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));
        Assert.False(state.TryBeginRefresh(saveInProgress: false, out _));
        Assert.True(state.CompleteRefresh(ticket, Disabled));

        Assert.Equal(StartupFlipDecision.Apply, state.TryBeginApply(true, saveInProgress: false, settingsRecovered: false));
        Assert.False(state.TryBeginRefresh(saveInProgress: false, out _));
        state.CompleteApply(Enabled);
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out _));
    }

    public static TheoryData<StartupTaskState?, bool, bool, bool, StartupFlipDecision> Decisions => new()
    {
        { StartupTaskState.Disabled, true, false, false, StartupFlipDecision.Apply },
        { StartupTaskState.Enabled, false, false, false, StartupFlipDecision.Apply },
        { StartupTaskState.Disabled, false, false, false, StartupFlipDecision.Unchanged },
        { StartupTaskState.Enabled, true, false, false, StartupFlipDecision.Unchanged },
        { StartupTaskState.Disabled, true, true, false, StartupFlipDecision.SaveInProgress },
        { StartupTaskState.Disabled, true, false, true, StartupFlipDecision.RecoveredSettings },
        { StartupTaskState.DisabledByUser, true, false, false, StartupFlipDecision.NotChangeable },
        { StartupTaskState.DisabledByPolicy, true, false, false, StartupFlipDecision.NotChangeable },
        { StartupTaskState.EnabledByPolicy, false, false, false, StartupFlipDecision.NotChangeable },
        { null, true, false, false, StartupFlipDecision.NotChangeable },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Flip_is_applied_only_when_nothing_stands_in_the_way(
        StartupTaskState? shown, bool requested, bool saveInProgress, bool settingsRecovered, StartupFlipDecision expected)
    {
        var state = Showing(new StartupRegistrationStatus(shown));

        Assert.Equal(expected, state.TryBeginApply(requested, saveInProgress, settingsRecovered));
        Assert.Equal(expected == StartupFlipDecision.Apply, state.IsApplying);
    }

    [Fact]
    public void Nothing_can_be_flipped_before_the_first_read_has_shown_a_state()
    {
        var state = new StartupSwitchState();

        Assert.Equal(StartupFlipDecision.NotChangeable, state.Decide(true, saveInProgress: false, settingsRecovered: false));
    }

    [Fact]
    public void Second_flip_while_one_applies_is_refused_and_the_first_is_unaffected()
    {
        var state = Showing(Disabled);
        Assert.Equal(StartupFlipDecision.Apply, state.TryBeginApply(true, saveInProgress: false, settingsRecovered: false));

        Assert.Equal(StartupFlipDecision.ApplyInFlight, state.TryBeginApply(false, saveInProgress: false, settingsRecovered: false));
        Assert.True(state.IsApplying);

        state.CompleteApply(Enabled);
        Assert.Equal(StartupFlipDecision.Apply, state.TryBeginApply(false, saveInProgress: false, settingsRecovered: false));
    }

    [Fact]
    public void Refused_flip_starts_nothing()
    {
        var state = Showing(Disabled);

        Assert.Equal(StartupFlipDecision.SaveInProgress, state.TryBeginApply(true, saveInProgress: true, settingsRecovered: false));
        Assert.False(state.IsApplying);

        // A read still running is not overtaken by a flip that never started.
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));
        Assert.Equal(StartupFlipDecision.RecoveredSettings, state.TryBeginApply(true, saveInProgress: false, settingsRecovered: true));
        Assert.True(state.CompleteRefresh(ticket, Enabled));
    }

    private static StartupSwitchState Showing(StartupRegistrationStatus status)
    {
        var state = new StartupSwitchState();
        Assert.True(state.TryBeginRefresh(saveInProgress: false, out var ticket));
        Assert.True(state.CompleteRefresh(ticket, status));
        return state;
    }
}
