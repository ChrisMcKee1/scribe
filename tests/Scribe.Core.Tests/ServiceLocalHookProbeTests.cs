using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public sealed class ServiceLocalHookProbeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Independent_services_receive_their_probe_in_both_chain_orders(bool olderLocal, bool newerLocal)
    {
        var older = olderLocal ? new HookProbeIdentity(unchecked((nuint)0x1741221133445566UL)) : default;
        var newer = newerLocal ? new HookProbeIdentity(unchecked((nuint)0x2751332244556677UL)) : default;
        using var first = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        using var second = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var probe = new KeyEventIdentity(NativeMethods.VK_PROBE, 0, 0x80, 0);

        var reachedOlder = !KeyboardHookFilter.Route(second.Engine, new KeyEventPassOn(), true, probe, false, older.Marker, newer).Swallow;
        Assert.Equal(olderLocal || newerLocal, reachedOlder);
        if (reachedOlder)
        {
            Assert.True(KeyboardHookFilter.Route(first.Engine, new KeyEventPassOn(), true, probe, false, older.Marker, older).Swallow);
        }

        Assert.True(KeyboardHookFilter.Route(second.Engine, new KeyEventPassOn(), true, probe, false, newer.Marker, newer).Swallow);
        Assert.Empty(first.TakeTransitions());
        Assert.Empty(second.TakeTransitions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_and_retired_registrations_use_the_same_service_identity(bool current)
    {
        using var harness = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var identity = HookProbeIdentity.Create(PerfFlags.Parse(PerfFlags.ServiceLocalHookProbe));
        var probe = new KeyEventIdentity(NativeMethods.VK_PROBE, 0, 0x80, 0);
        foreach (var marker in new[] { identity.Marker, (nuint)(uint)identity.Marker })
        {
            var route = KeyboardHookFilter.Route(harness.Engine, new KeyEventPassOn(), current, probe, false, marker, identity);
            Assert.True(route.Swallow);
            Assert.False(route.TrackPass);
            Assert.Equal(0, route.RepairAt);
        }
        Assert.Empty(harness.TakeTransitions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flags_do_not_change_dictated_text_or_the_wire_shape_except_the_probe_identity(bool enabled)
    {
        var identity = HookProbeIdentity.Create(enabled ? PerfFlags.Parse(PerfFlags.ServiceLocalHookProbe) : PerfFlags.None);
        Assert.Equal(enabled, identity.IsLocal);
        var input = NativeMethods.BuildWatchdogProbe(identity);
        Assert.Equal(NativeMethods.VK_PROBE, input.U.ki.wVk);
        Assert.Equal(0, input.U.ki.wScan);
        Assert.Equal(0u, input.U.ki.time);
        Assert.Equal(2u, input.U.ki.dwFlags);
        Assert.Equal(identity.Marker, input.U.ki.dwExtraInfo);

        using var harness = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        foreach (var key in new uint[] { 0xE7, 0xA3 })
        {
            foreach (var isDown in new[] { true, false })
            {
                var route = KeyboardHookFilter.Route(harness.Engine, new KeyEventPassOn(), true,
                    new KeyEventIdentity(key, 0, 0, 0), isDown, SyntheticInputMarker.Value, identity);
                Assert.False(route.Swallow);
                Assert.False(route.TrackPass);
            }
        }
        Assert.Empty(harness.TakeTransitions());
    }

    [Fact]
    public void A_local_identity_never_treats_a_foreign_marker_or_key_down_as_its_probe()
    {
        var own = new HookProbeIdentity(unchecked((nuint)0x1741221133445566UL));
        var foreign = new HookProbeIdentity(unchecked((nuint)0x2751332244556677UL));
        Assert.False(own.IsOwn(NativeMethods.VK_PROBE, true, foreign.Marker));
        Assert.False(own.IsOwn(NativeMethods.VK_PROBE, true, SyntheticInputMarker.Value));
        Assert.False(own.IsOwn(NativeMethods.VK_PROBE, true, (nuint)(uint)SyntheticInputMarker.Value));
        Assert.False(own.IsOwn(NativeMethods.VK_PROBE, false, own.Marker));
        Assert.False(own.IsOwn(0xA3, true, own.Marker));
    }
}
