using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

public sealed class HookRecoveryObservationTests
{
    [Fact]
    public void An_unfinished_join_has_no_removal_observation()
    {
        var observation = new HookRecoveryObservation();
        Assert.Equal(HookRemovalOutcome.Incomplete, observation.Snapshot.Outcome);
    }

    [Theory]
    [InlineData(true, 0, (int)HookRemovalOutcome.PresentAtRemoval, 0)]
    [InlineData(true, 1404, (int)HookRemovalOutcome.PresentAtRemoval, 0)]
    [InlineData(false, 1404, (int)HookRemovalOutcome.InvalidOrAbsent, 1404)]
    [InlineData(false, 5, (int)HookRemovalOutcome.OtherFailure, 5)]
    [InlineData(false, 0, (int)HookRemovalOutcome.OtherFailure, 0)]
    public void Removal_reports_only_the_observed_result(bool removed, int error, int expected, int expectedError)
    {
        var observation = new HookRecoveryObservation();
        observation.Complete(removed, error);

        Assert.Equal(new HookRemovalSnapshot((HookRemovalOutcome)expected, expectedError), observation.Snapshot);
    }

    [Fact]
    public void Late_completion_belongs_only_to_its_original_installation()
    {
        var oldInstallation = new HookRecoveryObservation();
        var replacement = new HookRecoveryObservation();
        var reportedAtJoinTimeout = oldInstallation.Snapshot;
        replacement.Complete(true, 0);
        oldInstallation.Complete(false, 1404);

        Assert.Equal(HookRemovalOutcome.Incomplete, reportedAtJoinTimeout.Outcome);
        Assert.Equal(HookRemovalOutcome.InvalidOrAbsent, oldInstallation.Snapshot.Outcome);
        Assert.Equal(HookRemovalOutcome.PresentAtRemoval, replacement.Snapshot.Outcome);
    }

    [Fact]
    public void An_observation_is_completed_only_once()
    {
        var observation = new HookRecoveryObservation();
        observation.Complete(false, 5);
        observation.Complete(true, 0);
        Assert.Equal(new HookRemovalSnapshot(HookRemovalOutcome.OtherFailure, 5), observation.Snapshot);
    }
}
