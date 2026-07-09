using Microsoft.Extensions.Logging.Abstractions;
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
    public void Rebinding_an_active_chord_emits_deactivation_and_resets_pressed_state()
    {
        var state = new ChordStateMachine(
            new HotkeyBinding(0x77, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F8"));
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x77, isDown: true).Transition);

        var transition = state.UpdateBinding(
            new HotkeyBinding(0x78, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9"));

        Assert.Equal(HotkeyTransition.Deactivated, transition);
        Assert.Equal(HotkeyTransition.None, state.Process(0x77, isDown: false).Transition);
        Assert.Equal(HotkeyTransition.Activated, state.Process(0x78, isDown: true).Transition);
    }
}
