namespace Scribe.Core.Hotkeys;

internal enum HookRemovalOutcome
{
    Incomplete,
    PresentAtRemoval,
    InvalidOrAbsent,
    OtherFailure,
}

internal sealed record HookRemovalSnapshot(HookRemovalOutcome Outcome, int Error)
{
    public static HookRemovalSnapshot Incomplete { get; } = new(HookRemovalOutcome.Incomplete, 0);
}

/// <summary>One installation's completed removal observation, never a verdict about why input stopped.</summary>
internal sealed class HookRecoveryObservation
{
    private HookRemovalSnapshot _snapshot = HookRemovalSnapshot.Incomplete;

    public HookRemovalSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Complete(bool removed, int error)
    {
        var snapshot = new HookRemovalSnapshot(
            removed ? HookRemovalOutcome.PresentAtRemoval :
            error == 1404 ? HookRemovalOutcome.InvalidOrAbsent : HookRemovalOutcome.OtherFailure,
            removed ? 0 : error);
        Interlocked.CompareExchange(ref _snapshot, snapshot, HookRemovalSnapshot.Incomplete);
    }
}
