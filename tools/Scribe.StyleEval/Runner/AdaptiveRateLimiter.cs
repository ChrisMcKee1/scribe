namespace Scribe.StyleEval.Runner;

using System.Diagnostics;

/// <summary>
/// Paces every model call in a run against one shared budget, and lowers that budget when the
/// deployment says it is being pushed too hard.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece the suite was missing. <c>StyleActionClient</c> already retried a throttled
/// call with exponential backoff and jitter, which keeps a run from losing cells, but retry is not
/// rate limiting: each worker backed off in isolation while the other seven kept firing, so the
/// aggregate offered load stayed pinned at whatever the deployment would refuse. A run behaved as a
/// denial of service against its own quota, and against anything else sharing the deployment. That
/// is not hypothetical. A 10,000 cell run at concurrency 8 saturated the shared <c>gpt-5.6-terra</c>
/// deployment so completely that Scribe itself, the app the suite exists to measure, got HTTP 429 on
/// every interactive text action for as long as the run lasted.
/// </para>
/// <para>
/// So the governor is global rather than per call, and it moves in both directions (AIMD, the same
/// shape TCP congestion control uses, for the same reason: it finds the ceiling without knowing it
/// in advance, and it yields quickly when something else needs the pipe).
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Slow start.</b> The rate opens at <see cref="StartingFraction"/> of the ceiling rather than at
/// the ceiling. A run that begins by firing every worker at once spends its first minute being
/// refused, which is exactly the burst that starves a co-tenant.
/// </item>
/// <item>
/// <b>Multiplicative decrease.</b> One 429 halves the rate immediately. Throttling means the real
/// ceiling is below the current rate, and halving crosses that gap in a few observations instead of
/// creeping down while continuing to overshoot.
/// </item>
/// <item>
/// <b>Additive increase.</b> Sustained success adds a small fixed step, so recovery is gradual and
/// the rate settles just under the true limit instead of oscillating across it.
/// </item>
/// <item>
/// <b>Retry-After is obeyed.</b> When the service says how long to wait, that beats any guess this
/// class could make, and the pause applies to every worker rather than to the one that was refused.
/// </item>
/// </list>
/// <para>
/// Pacing is deliberately a single serialized decision. Handing out a timestamp per permit under one
/// lock is what makes the limit global; a per-worker limiter would multiply the real rate by the
/// worker count, which is the bug this class exists to prevent.
/// </para>
/// </remarks>
internal sealed class AdaptiveRateLimiter
{
    /// <summary>Fraction of the ceiling the limiter opens at, before it has observed anything.</summary>
    private const double StartingFraction = 0.5;

    /// <summary>What a throttle multiplies the current rate by.</summary>
    private const double DecreaseFactor = 0.5;

    /// <summary>Requests per minute added after a clean stretch.</summary>
    private const double IncreaseStep = 2.0;

    /// <summary>Consecutive successes required before the rate is allowed to climb.</summary>
    private const int SuccessesBeforeIncrease = 20;

    /// <summary>
    /// Floor for the adaptive rate. Below roughly one call every six seconds a long run stops being
    /// finishable, so the limiter gives up ground no further and lets the retry policy carry the rest.
    /// </summary>
    private const double MinimumRequestsPerMinute = 10.0;

    private readonly object _gate = new();
    private readonly double _ceiling;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _current;
    private int _consecutiveSuccesses;

    /// <summary>When the next permit may be issued, on the <see cref="_clock"/> timeline.</summary>
    private TimeSpan _nextSlot = TimeSpan.Zero;

    /// <summary>Count of throttles observed, for the run summary.</summary>
    private int _throttleCount;

    /// <param name="requestsPerMinute">
    /// Ceiling the adaptive rate may climb back to. This is the headroom decision: set it below what
    /// the deployment can serve and an interactive co-tenant keeps working while the run proceeds.
    /// </param>
    public AdaptiveRateLimiter(int requestsPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);

        _ceiling = requestsPerMinute;
        _current = Math.Max(MinimumRequestsPerMinute, _ceiling * StartingFraction);
    }

    /// <summary>Requests per minute currently permitted.</summary>
    public double CurrentRequestsPerMinute
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>How many throttles have been reported.</summary>
    public int ThrottleCount
    {
        get { lock (_gate) { return _throttleCount; } }
    }

    /// <summary>
    /// Waits until this caller is allowed to issue a request.
    /// </summary>
    /// <remarks>
    /// The slot is claimed under the lock and the wait happens outside it, so callers queue in the
    /// order they arrived without holding the lock across a delay.
    /// </remarks>
    public async Task WaitAsync(CancellationToken ct)
    {
        TimeSpan wait;

        lock (_gate)
        {
            var interval = TimeSpan.FromMinutes(1.0 / _current);
            var now = _clock.Elapsed;

            // A slot in the past means the run has been idle relative to the budget; issue now and
            // schedule from now, rather than letting unused slots bank into a burst.
            var slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + interval;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports that a call was throttled. Halves the rate, and holds every worker off for
    /// <paramref name="retryAfter"/> when the service supplied one.
    /// </summary>
    public void ReportThrottled(TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            _throttleCount++;
            _consecutiveSuccesses = 0;
            _current = Math.Max(MinimumRequestsPerMinute, _current * DecreaseFactor);

            // The pause is applied to the shared schedule, not to the calling worker, because the
            // service refused the deployment rather than refusing one connection.
            if (retryAfter is { } pause && pause > TimeSpan.Zero)
            {
                var resume = _clock.Elapsed + pause;
                if (resume > _nextSlot)
                {
                    _nextSlot = resume;
                }
            }
        }
    }

    /// <summary>
    /// Reports a call that was not throttled. Climbs back toward the ceiling once a clean stretch
    /// suggests the earlier decrease gave up more than it needed to.
    /// </summary>
    public void ReportSuccess()
    {
        lock (_gate)
        {
            if (_current >= _ceiling)
            {
                return;
            }

            if (++_consecutiveSuccesses < SuccessesBeforeIncrease)
            {
                return;
            }

            _consecutiveSuccesses = 0;
            _current = Math.Min(_ceiling, _current + IncreaseStep);
        }
    }
}
