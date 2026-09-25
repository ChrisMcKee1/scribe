using System.Reflection;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 11. A14: no reconcile signal is shared across hook installations, so a callback of an installation a
/// reinstall replaced can never overwrite the replacement's pending repair (the service-level regression is
/// <c>HotkeyServiceTests.Start_serves_the_current_installation_s_repair_whatever_an_obsolete_one_asks_for</c>). A13: repair
/// requests are counted where they are made, on the asking thread, so a test sees one without waiting for the pool.
/// </summary>
public sealed class MouseButtonRound11Tests
{
    // The design A14 rests on: the service keeps no signal of its own for every installation to use, and an installation
    // is given none; each makes its own, which its hook thread disposes once its hooks are gone.
    [Fact]
    public void No_reconcile_signal_is_shared_across_hook_installations()
    {
        const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.DoesNotContain(
            typeof(HotkeyService).GetFields(Instance), field => field.FieldType == typeof(HotkeyReconcileSignal));

        var installation = typeof(HotkeyService).GetNestedType("HookInstallation", BindingFlags.NonPublic)!;
        Assert.All(
            installation.GetConstructors(Instance | BindingFlags.Public),
            constructor => Assert.DoesNotContain(
                constructor.GetParameters(), parameter => parameter.ParameterType == typeof(HotkeyReconcileSignal)));
        Assert.Contains(installation.GetFields(Instance), field => field.FieldType == typeof(HotkeyReconcileSignal));
    }

    // A13: the signal counts a repair as it is asked for, on the asking thread (a sync-only request is not one), so the
    // count moves before the pool has run anything.
    [Fact]
    public void The_signal_counts_each_repair_as_it_is_asked_for()
    {
        using var hold = new ManualResetEventSlim(false);
        using var signal = new HotkeyReconcileSignal(static _ => { }) { HoldBeforeTakingForTests = hold };
        try
        {
            signal.Signal(5);
            signal.SignalMouseHookSync();
            signal.Signal(6);

            Assert.Equal(2, signal.RepairRequests);
        }
        finally
        {
            signal.HoldBeforeTakingForTests = null;
            hold.Set();
        }
    }

    // A13: the queue counts each transition that will ask the consumer for a repair (a key transition's Deactivated) as it
    // is queued: not an Activated, and not a state clear's Deactivated.
    [Fact]
    public void The_queue_counts_each_release_that_asks_the_consumer_for_a_repair()
    {
        using var queue = new HotkeyTransitionQueue();

        queue.TryEnqueue(new HotkeyService.QueuedTransition(HotkeyTransition.Activated, HotkeyTrigger.Standard, 1, true));
        queue.TryEnqueue(new HotkeyService.QueuedTransition(HotkeyTransition.Deactivated, HotkeyTrigger.Standard, 1, true));
        queue.TryEnqueue(new HotkeyService.QueuedTransition(HotkeyTransition.Deactivated, HotkeyTrigger.Standard, 1, false));

        Assert.Equal(1, queue.RepairRequests);
    }
}
