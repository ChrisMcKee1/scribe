using System.Diagnostics;
using Scribe.StyleEval.Runner;

namespace Scribe.StyleEval.Tests;

/// <summary>
/// Behaviour of the shared rate governor. These are about the CONTROL LOOP, not about wall-clock
/// precision: timing assertions use generous bounds so the suite does not go flaky on a loaded
/// machine, and the exact-rate questions are asked of the reported rate rather than of a stopwatch.
/// </summary>
public class AdaptiveRateLimiterTests
{
    [Fact]
    public void Opens_below_the_ceiling_rather_than_at_it()
    {
        // Slow start. A run that opens at the ceiling spends its first minute being refused, which
        // is exactly the burst that starves anything sharing the deployment.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 100);

        Assert.True(limiter.CurrentRequestsPerMinute < 100);
        Assert.Equal(50, limiter.CurrentRequestsPerMinute);
    }

    [Fact]
    public void A_throttle_halves_the_rate_for_everyone()
    {
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 100);
        var before = limiter.CurrentRequestsPerMinute;

        limiter.ReportThrottled(retryAfter: null);

        Assert.Equal(before / 2, limiter.CurrentRequestsPerMinute);
        Assert.Equal(1, limiter.ThrottleCount);
    }

    [Fact]
    public void Repeated_throttles_stop_at_a_floor_rather_than_reaching_zero()
    {
        // Multiplicative decrease with no floor converges on an unusable rate, and a run that can
        // never issue another request is worse than a run that is merely slow.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 100);

        for (var i = 0; i < 40; i++)
        {
            limiter.ReportThrottled(retryAfter: null);
        }

        Assert.True(limiter.CurrentRequestsPerMinute >= 10);
    }

    [Fact]
    public void One_success_does_not_move_the_rate()
    {
        // Additive increase is gated on a clean STRETCH. Climbing after a single success would
        // undo a decrease immediately and oscillate across the real limit.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 100);
        limiter.ReportThrottled(retryAfter: null);
        var afterThrottle = limiter.CurrentRequestsPerMinute;

        limiter.ReportSuccess();

        Assert.Equal(afterThrottle, limiter.CurrentRequestsPerMinute);
    }

    [Fact]
    public void A_clean_stretch_climbs_back_toward_the_ceiling()
    {
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 100);
        limiter.ReportThrottled(retryAfter: null);
        var afterThrottle = limiter.CurrentRequestsPerMinute;

        for (var i = 0; i < 20; i++)
        {
            limiter.ReportSuccess();
        }

        Assert.True(limiter.CurrentRequestsPerMinute > afterThrottle);
    }

    [Fact]
    public void The_rate_never_climbs_past_the_ceiling()
    {
        // The ceiling is the headroom promise. Whatever the control loop does, it may not exceed it.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 60);

        for (var i = 0; i < 5_000; i++)
        {
            limiter.ReportSuccess();
        }

        Assert.True(limiter.CurrentRequestsPerMinute <= 60);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_budget_instead_of_one_each()
    {
        // The defect this class exists to prevent: a per-worker limiter multiplies the real rate by
        // the worker count. Eight callers against a 60/min budget must be paced as 60/min in total,
        // so the second permit each caller takes cannot arrive immediately.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 60); // opens at 30/min = 2s spacing
        var clock = Stopwatch.StartNew();

        // 8 permits at 2s spacing: the first is immediate, so the last waits about 14s. Take only
        // four and assert a floor well under the true spacing, to keep the test quick and stable.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => limiter.WaitAsync(CancellationToken.None)));

        // Three intervals of 2s = 6s if the budget is shared; ~0s if every caller got its own.
        Assert.True(
            clock.Elapsed > TimeSpan.FromSeconds(3),
            $"permits were not paced across callers: elapsed {clock.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task Retry_after_holds_every_worker_not_just_the_one_that_was_refused()
    {
        // The service refused the DEPLOYMENT, so the pause belongs to the shared schedule. Applying
        // it only to the caller that saw the 429 leaves the other seven hammering through it.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 6000); // 0.01s spacing, so pacing is not the cause
        limiter.ReportThrottled(TimeSpan.FromSeconds(2));

        var clock = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);

        Assert.True(
            clock.Elapsed > TimeSpan.FromSeconds(1.5),
            $"Retry-After was not applied to the shared schedule: elapsed {clock.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task Idle_time_does_not_bank_into_a_burst()
    {
        // A slot left unused while the run was blocked elsewhere must not accumulate. Otherwise a
        // pause is followed by a thundering burst, which is the shape that trips the limit again.
        var limiter = new AdaptiveRateLimiter(requestsPerMinute: 120); // opens at 60/min = 1s spacing

        await limiter.WaitAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(3));

        var clock = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);

        // Two permits after an idle stretch: the first is free, the second still costs one interval.
        Assert.True(
            clock.Elapsed > TimeSpan.FromMilliseconds(500),
            $"banked idle slots produced a burst: elapsed {clock.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public void A_ceiling_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveRateLimiter(requestsPerMinute: 0));
    }
}
