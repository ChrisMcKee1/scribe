using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

public partial class HotkeyServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_independent_services_keep_their_own_watchdog_probe_on_ci(bool localIdentity)
    {
        // The normal Start_ exclusion and this independent CI gate both precede every native input or hook operation.
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Assert.True(NativeMethods.ThreadDesktopReceivesInput() == true, "This CI test needs the input desktop.");

        var flags = localIdentity ? PerfFlags.Parse(PerfFlags.ServiceLocalHookProbe) : PerfFlags.None;
        using var older = ProbeService(flags);
        using var newer = ProbeService(flags);
        older.Start();
        newer.Start();
        var originalEngine = older.CurrentEngineForTests;

        var before = older.CallbackCountForTests;
        Assert.True(NativeMethods.SendWatchdogProbe(older.WatchdogIdentityForTests));
        Assert.Equal(localIdentity ? before + 1 : before, older.CallbackCountForTests);

        older.ProbeKeyboardHookNowForTests();
        Assert.True(older.WatchdogProbeArmedForTests, "The first real watchdog request was not armed.");
        older.ProbeKeyboardHookNowForTests();
        if (localIdentity)
        {
            Assert.Same(originalEngine, older.CurrentEngineForTests);
        }
        else
        {
            Assert.NotSame(originalEngine, older.CurrentEngineForTests);
        }
    }

    [Fact]
    public void Start_higher_integrity_foreground_window_withholds_and_disarms_watchdog_probe_on_ci()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Assert.True(NativeMethods.ThreadDesktopReceivesInput() == true, "This CI test needs the input desktop.");

        var higherIntegrity = true;
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            new HotkeyCommandRouter(HotkeyBinding.DefaultDictation),
            () => true,
            foregroundWindow: () => 0x4242,
            processNameOfWindow: _ => "elevated",
            keyRepeatWindowMs: () => 1000,
            perfFlags: PerfFlags.Parse(PerfFlags.ServiceLocalHookProbe),
            isHigherIntegrityWindow: _ => higherIntegrity)
        {
            WatchdogPeriodForTests = Timeout.InfiniteTimeSpan,
        };
        service.Start();
        var originalEngine = service.CurrentEngineForTests;

        service.ProbeKeyboardHookNowForTests();
        Assert.False(service.WatchdogProbeArmedForTests);
        service.ProbeKeyboardHookNowForTests();
        Assert.Same(originalEngine, service.CurrentEngineForTests);

        higherIntegrity = false;
        service.ProbeKeyboardHookNowForTests();
        Assert.True(service.WatchdogProbeArmedForTests);
    }

    private static HotkeyService ProbeService(PerfFlags flags) => new(
        NullLogger<HotkeyService>.Instance,
        new HotkeyCommandRouter(HotkeyBinding.DefaultDictation),
        () => true,
        foregroundWindow: () => 0,
        processNameOfWindow: _ => null,
        keyRepeatWindowMs: () => 1000,
        perfFlags: flags)
    {
        WatchdogPeriodForTests = Timeout.InfiniteTimeSpan,
    };
}
