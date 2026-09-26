using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

/// <summary>
/// The registrations a move ahead replaced, which the hook thread keeps a while before releasing them: an event already
/// on its way to one when the new registration landed must still find it (see <c>HotkeyService.HookInstallation</c>).
/// Fed the time, so the rule is tested without a clock.
/// </summary>
public class RetiredHookRegistrationsTests
{
    private const long Grace = 2000;

    [Fact]
    public void A_replaced_registration_is_released_only_once_its_grace_is_over()
    {
        var retired = new RetiredHookRegistrations();

        Assert.True(retired.Retire(0x10, nowMs: 1000));
        Assert.Equal(1, retired.Count);

        Assert.Equal(0, retired.TakeExpired(nowMs: 2999, Grace));
        Assert.Equal(0x10, retired.TakeExpired(nowMs: 3000, Grace));
        Assert.Equal(0, retired.TakeExpired(nowMs: 3000, Grace));
        Assert.Equal(0, retired.Count);
    }

    [Fact]
    public void Expired_registrations_are_released_oldest_first_and_younger_ones_kept()
    {
        var retired = new RetiredHookRegistrations();
        retired.Retire(0x10, nowMs: 0);
        retired.Retire(0x20, nowMs: 500);
        retired.Retire(0x30, nowMs: 2600);

        Assert.Equal(0x10, retired.TakeExpired(nowMs: 3000, Grace));
        Assert.Equal(0x20, retired.TakeExpired(nowMs: 3000, Grace));
        Assert.Equal(0, retired.TakeExpired(nowMs: 3000, Grace));
        Assert.Equal(1, retired.Count);
        Assert.Equal(0x30, retired.TakeExpired(nowMs: 4600, Grace));
    }

    [Fact]
    public void A_full_set_releases_nothing_early_and_says_when_its_oldest_grace_ends()
    {
        // Review round 2, item 3 (A3 = G2). This used to pin the opposite: a retirement past the capacity handed back the
        // oldest registration to be unhooked at once, whatever its age.
        var retired = new RetiredHookRegistrations();
        for (var i = 1; i <= RetiredHookRegistrations.Capacity; i++)
        {
            Assert.True(retired.Retire(i, nowMs: i * 300));
        }

        Assert.True(retired.IsFull);
        Assert.False(retired.Retire(0x99, nowMs: 1300));
        Assert.Equal(RetiredHookRegistrations.Capacity, retired.Count);
        Assert.Equal(1000, retired.MillisecondsUntilOldestExpires(nowMs: 1300, Grace));
        Assert.Equal(0, retired.TakeExpired(nowMs: 2299, Grace));
        Assert.Equal(1, retired.TakeExpired(nowMs: 2300, Grace));
        Assert.False(retired.IsFull);
        Assert.Equal(300, retired.MillisecondsUntilOldestExpires(nowMs: 2300, Grace)); // the second, replaced at 600
        Assert.Equal(0, retired.MillisecondsUntilOldestExpires(nowMs: 5000, Grace));
        Assert.Equal(0, new RetiredHookRegistrations().MillisecondsUntilOldestExpires(nowMs: 0, Grace));
    }

    [Fact]
    public void The_thread_s_exit_takes_every_one_whatever_its_age()
    {
        var retired = new RetiredHookRegistrations();
        retired.Retire(0x10, nowMs: 0);
        retired.Retire(0x20, nowMs: 5);

        var taken = new List<nint>();
        for (var handle = retired.TakeAny(); handle != 0; handle = retired.TakeAny())
        {
            taken.Add(handle);
        }

        Assert.Equal([0x10, 0x20], taken);
        Assert.Equal(0, retired.Count);
    }

    [Fact]
    public void Nothing_is_retired_for_no_registration()
    {
        var retired = new RetiredHookRegistrations();

        Assert.False(retired.Retire(0, nowMs: 0));
        Assert.Equal(0, retired.Count);
    }
}
