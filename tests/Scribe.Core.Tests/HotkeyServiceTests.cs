using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

public class HotkeyServiceTests
{
    [Fact]
    public void Start_installs_hook_and_Stop_removes_it()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        service.Start();
        Assert.True(service.IsRunning);

        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void Start_is_idempotent_and_Dispose_is_safe()
    {
        var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        service.Start();
        service.Start(); // second call is a no-op
        Assert.True(service.IsRunning);

        service.Dispose();
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void UpdateBinding_replaces_active_binding()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        Assert.Equal(HotkeyBinding.DefaultVirtualKey, service.Binding.VirtualKey);

        var toggle = new HotkeyBinding(0x20, KeyModifiers.Control, HotkeyMode.Toggle, Suppress: false, "Ctrl+Space");
        service.UpdateBinding(toggle);

        Assert.Equal(0x20u, service.Binding.VirtualKey);
        Assert.Equal(HotkeyMode.Toggle, service.Binding.Mode);
        Assert.Equal(KeyModifiers.Control, service.Binding.Modifiers);
    }

    [Fact]
    public void Enqueue_during_shutdown_is_discarded_without_throwing()
    {
        using var queue = new BlockingCollection<HotkeyService.QueuedTransition>();
        queue.CompleteAdding();

        var added = HotkeyService.TryEnqueue(
            queue, new HotkeyService.QueuedTransition(
                HotkeyTransition.Activated, HotkeyTrigger.Standard, 0, AllowReconcile: true));

        Assert.False(added);
    }

    [Fact]
    public void UpdateBindings_preserves_both_trigger_roles()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        var standard = new HotkeyBinding(0x77, KeyModifiers.None, HotkeyMode.Hold, true, "F8");
        var dictationOnly = new HotkeyBinding(0x78, KeyModifiers.None, HotkeyMode.Toggle, true, "F9");

        service.UpdateBindings(standard, dictationOnly);

        Assert.Equal(standard, service.Binding);
        Assert.Equal(dictationOnly, service.DictationOnlyBinding);
    }

    [Fact]
    public void Trigger_arbiter_ignores_a_second_binding_until_the_active_binding_releases()
    {
        var arbiter = new HotkeyTriggerArbiter();

        Assert.True(arbiter.TryActivate(HotkeyTrigger.Standard));
        Assert.False(arbiter.TryActivate(HotkeyTrigger.DictationOnly));
        Assert.False(arbiter.TryDeactivate(HotkeyTrigger.DictationOnly));
        Assert.True(arbiter.TryDeactivate(HotkeyTrigger.Standard));
        Assert.True(arbiter.TryActivate(HotkeyTrigger.DictationOnly));
    }

    [Fact]
    public void Physical_hold_chord_activates_in_any_order_and_stops_when_either_key_releases()
    {
        var binding = new HotkeyBinding(
            0x77, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F8+F9",
            SecondaryVirtualKey: 0x78, SuppressChordMembers: true);
        var state = new ChordStateMachine(binding);

        Assert.Equal(HotkeyTransition.None, state.Process(0x78, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x77, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x77, isDown: true).Transition); // repeat
        Assert.Equal(HotkeyTransition.Deactivated, state.Process(0x78, isDown: false).Transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x77, isDown: false).Transition);
    }

    [Fact]
    public void Toggle_chord_fires_once_per_complete_press_and_rearms_after_release()
    {
        var binding = new HotkeyBinding(
            0x5B, KeyModifiers.None, HotkeyMode.Toggle, Suppress: true, "Left Win+H",
            SecondaryVirtualKey: 0x48, SuppressChordMembers: true);
        var state = new ChordStateMachine(binding);

        Assert.Equal(HotkeyTransition.None, state.Process(0x5B, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x48, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x48, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x48, isDown: false).Transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x5B, isDown: false).Transition);

        Assert.Equal(HotkeyTransition.None, state.Process(0x48, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.Deactivated, state.Process(0x5B, isDown: true).Transition);
    }

    [Fact]
    public void Legacy_modifier_binding_stops_when_modifier_releases()
    {
        var state = new ChordStateMachine(
            new HotkeyBinding(0x20, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "Ctrl+Space"));

        Assert.Equal(HotkeyTransition.None, state.Process(0xA3, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x20, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.Deactivated, state.Process(0xA3, isDown: false).Transition);
    }

    [Fact]
    public void Modified_primary_key_is_suppressed_only_when_the_full_chord_completes()
    {
        var state = new ChordStateMachine(
            new HotkeyBinding(0x20, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "Ctrl+Space"));

        var standaloneDown = state.Process(0x20, isDown: true);
        var standaloneUp = state.Process(0x20, isDown: false);
        Assert.False(standaloneDown.ShouldSuppress);
        Assert.False(standaloneUp.ShouldSuppress);

        Assert.False(state.Process(0xA2, isDown: true).ShouldSuppress);
        Assert.True(state.Process(0x20, isDown: true).ShouldSuppress);
        Assert.True(state.Process(0x20, isDown: false).ShouldSuppress);
        Assert.False(state.Process(0xA2, isDown: false).ShouldSuppress);
    }

    [Fact]
    public void Reserved_physical_chord_suppresses_each_member_down_and_matching_release()
    {
        var state = new ChordStateMachine(new HotkeyBinding(
            0x5B, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Left Win+H",
            SecondaryVirtualKey: 0x48, SuppressChordMembers: true));

        Assert.True(state.Process(0x5B, isDown: true).ShouldSuppress);
        Assert.True(state.Process(0x48, isDown: true).ShouldSuppress);
        Assert.True(state.Process(0x48, isDown: false).ShouldSuppress);
        Assert.True(state.Process(0x5B, isDown: false).ShouldSuppress);
    }

    [Fact]
    public void Capture_mode_passes_the_bound_key_through_without_activating_or_suppressing()
    {
        var state = new ChordStateMachine(HotkeyBinding.Default); // Right Ctrl, suppressed

        Assert.Equal(HotkeyTransition.None, state.SetCaptureMode(true).Transition);

        var down = state.Process(0xA3, isDown: true);
        var up = state.Process(0xA3, isDown: false);
        Assert.Equal(HotkeyTransition.None, down.Transition);
        Assert.False(down.ShouldSuppress);
        Assert.Equal(HotkeyTransition.None, up.Transition);
        Assert.False(up.ShouldSuppress);
    }

    [Fact]
    public void Entering_capture_mode_mid_hold_deactivates_the_recording()
    {
        var state = new ChordStateMachine(HotkeyBinding.Default);

        Assert.Equal(HotkeyTransition.Activated, state.Process(0xA3, isDown: true).Transition);
        Assert.Equal(HotkeyTransition.Deactivated, state.SetCaptureMode(true).Transition);
    }

    [Fact]
    public void Leaving_capture_mode_requires_a_fresh_press_to_activate()
    {
        var state = new ChordStateMachine(HotkeyBinding.Default);
        state.SetCaptureMode(true);

        // Key held across the capture-mode boundary must not satisfy the chord on exit.
        state.Process(0xA3, isDown: true);
        Assert.Equal(HotkeyTransition.None, state.SetCaptureMode(false).Transition);

        Assert.Equal(HotkeyTransition.None, state.Process(0xA3, isDown: false).Transition);
        Assert.Equal(HotkeyTransition.Activated, state.Process(0xA3, isDown: true).Transition);
    }

    [Fact]
    public void State_clearing_operations_start_a_new_generation_so_stale_activations_are_detectable()
    {
        var state = new ChordStateMachine(HotkeyBinding.Default);

        // An Activated computed in the old epoch must be distinguishable after a clear: this is
        // what stops a racing hook callback from starting a recording during binding capture.
        var beforeClear = state.Process(0xA3, isDown: true);
        var (_, afterClear) = state.SetCaptureMode(true);

        Assert.Equal(HotkeyTransition.Activated, beforeClear.Transition);
        Assert.NotEqual(beforeClear.Generation, afterClear);
        Assert.Equal(afterClear, state.Generation);
    }

    [Fact]
    public void Reset_reports_the_deactivation_of_an_interrupted_recording()
    {
        var state = new ChordStateMachine(HotkeyBinding.Default);

        Assert.Equal(HotkeyTransition.Activated, state.Process(0xA3, isDown: true).Transition);

        // A hook reinstall mid-hold clears state; the caller must learn the recording died.
        Assert.Equal(HotkeyTransition.Deactivated, state.Reset().Transition);
        Assert.Equal(HotkeyTransition.None, state.Reset().Transition);
    }

    [Fact]
    public void Reconciler_releases_only_keys_the_system_holds_but_the_hook_saw_released()
    {
        var leaked = new HashSet<uint> { 0xA3 };       // system: Right Ctrl still down
        var physicallyHeld = new HashSet<uint>();      // hook: everything released
        var released = new List<uint>();
        var reconciler = new SuppressedKeyReconciler(
            leaked.Contains, physicallyHeld.Contains, key => { released.Add(key); return true; });

        var result = reconciler.ReleaseLeakedKeys(HotkeyBinding.Default);

        Assert.Equal([0xA3u], result.Released);
        Assert.Empty(result.Failed);
        Assert.Equal([0xA3u], released);
    }

    [Fact]
    public void Reconciler_never_releases_a_key_the_user_genuinely_holds()
    {
        var systemDown = new HashSet<uint> { 0xA3 };
        var physicallyHeld = new HashSet<uint> { 0xA3 }; // hook agrees: user is holding it
        var released = new List<uint>();
        var reconciler = new SuppressedKeyReconciler(
            systemDown.Contains, physicallyHeld.Contains, key => { released.Add(key); return true; });

        Assert.Empty(reconciler.ReleaseLeakedKeys(HotkeyBinding.Default).Released);
        Assert.Empty(released);
    }

    [Fact]
    public void Reconciler_skips_unsuppressed_bindings_entirely()
    {
        var systemDown = new HashSet<uint> { 0x20 };
        var released = new List<uint>();
        var reconciler = new SuppressedKeyReconciler(
            systemDown.Contains, _ => false, key => { released.Add(key); return true; });

        var binding = new HotkeyBinding(0x20, KeyModifiers.None, HotkeyMode.Hold, Suppress: false, "Space");
        Assert.Empty(reconciler.ReleaseLeakedKeys(binding).Released);
        Assert.Empty(released);
    }

    [Fact]
    public void Reconciler_reports_a_rejected_injection_as_failed_not_released()
    {
        var leaked = new HashSet<uint> { 0xA3 };
        var reconciler = new SuppressedKeyReconciler(
            leaked.Contains, _ => false, _ => false); // SendInput rejected (e.g. UIPI)

        var result = reconciler.ReleaseLeakedKeys(HotkeyBinding.Default);

        Assert.Empty(result.Released);
        Assert.Equal([0xA3u], result.Failed);
    }

    [Fact]
    public void Reconciler_covers_chord_members_and_both_variants_of_flag_modifiers()
    {
        var chord = new HotkeyBinding(
            0x77, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F8+F9",
            SecondaryVirtualKey: 0x78, SuppressChordMembers: true);
        Assert.Equal(
            new[] { 0x77u, 0x78u },
            SuppressedKeyReconciler.CandidateKeys(chord).OrderBy(k => k));

        var modified = new HotkeyBinding(0x20, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "Ctrl+Space");
        Assert.Equal(
            new[] { 0x20u, 0xA2u, 0xA3u },
            SuppressedKeyReconciler.CandidateKeys(modified).OrderBy(k => k));
    }

    [Fact]
    public void Rebinding_an_active_chord_emits_deactivation_and_resets_pressed_state()
    {
        var state = new ChordStateMachine(
            new HotkeyBinding(0x77, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F8"));
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x77, isDown: true).Transition);

        var transition = state.UpdateBinding(
            new HotkeyBinding(0x78, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9"));

        Assert.Equal(HotkeyTransition.Deactivated, transition.Transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x77, isDown: false).Transition);
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x78, isDown: true).Transition);
    }
}
