namespace Scribe.Core.Overlay;

/// <summary>What the command consumer should do when a command needs the overlay helper and it is not running.</summary>
internal enum OverlayLaunchDecision
{
    /// <summary>Launch the helper now.</summary>
    Attempt,

    /// <summary>
    /// A cooldown is running and the latest state does not keep the pill on screen, so nothing is
    /// launched or retried. The next command after the cooldown may launch.
    /// </summary>
    HeldBack,

    /// <summary>
    /// A cooldown is running and the latest state keeps the pill on screen, so one coalesced retry is
    /// pending at the end of the cooldown. It applies whichever state is latest when it fires.
    /// </summary>
    RetryPending,
}

/// <summary>
/// Owns the overlay helper's relaunch policy after failures: an exponential cooldown, and at most one
/// coalesced retry that applies the latest state instead of replaying stale commands. One of the two
/// policies <see cref="OverlayHelperLifetime"/> combines; the client never calls it directly.
/// </summary>
/// <remarks>
/// <para>
/// Without it every state command that needed the helper launched it again. A helper that starts but
/// never opens its pipe costs the full connect timeout on each try, so a single dictation could queue
/// several blocking attempts behind one another and delay everything after them, shutdown included.
/// </para>
/// <para>
/// Schedule: the first failure cools down for <see cref="DefaultInitialCooldown"/> (1 s), each further
/// consecutive failure doubles it, and it never exceeds <see cref="DefaultMaxCooldown"/> (60 s). One
/// second is short enough that a transient failure (an antivirus scan of a freshly updated binary, a
/// development rebuild racing the launch) costs a recording in progress about a second of missing pill,
/// and long enough that the next state command of the same dictation cannot immediately repeat a
/// failure that may have just blocked for the whole connect timeout. The 60 s cap holds a persistently
/// broken helper to about one attempt a minute while something is on screen, so attempts that block for
/// the client's connect timeout (8 s today) occupy the consumer roughly an eighth of the time at worst,
/// while a helper that recovers during a long recording is back within a minute. There is no jitter:
/// jitter spreads many clients retrying against one shared service, and here one client drives one local
/// helper.
/// </para>
/// <para>
/// A successful launch resets the backoff. A helper that is lost within <see cref="DefaultStableAfter"/>
/// (10 s) of its launch is treated as a launch that did not really succeed: the count resumes from where
/// it stood before that launch, so a helper that connects and then crashes on the state it was just
/// shown backs off like any other failure instead of being relaunched on every pipe write. Ten seconds
/// comfortably covers process start, the state replay written right after the pipe connects, and the
/// first render. A helper that ran longer had proven itself, so losing it later relaunches at once. The
/// loss is measured at the moment the helper was lost (its process exit time when known), not when the
/// consumer noticed, and the cooldown it causes runs from that moment too: a crash noticed only at the
/// next state change must neither pass for a stable run nor hold back a relaunch that is long overdue.
/// </para>
/// <para>
/// Threading: single consumer. Every member is called only from the overlay command consumer, which
/// holds this state. Nothing here blocks, sleeps or reads a clock; callers pass a monotonic reading.
/// </para>
/// </remarks>
internal sealed class OverlayLaunchBackoff
{
    /// <summary>Cooldown after the first failure.</summary>
    public static TimeSpan DefaultInitialCooldown { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on any cooldown.</summary>
    public static TimeSpan DefaultMaxCooldown { get; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a launched helper must survive before its launch counts as a lasting success.</summary>
    public static TimeSpan DefaultStableAfter { get; } = TimeSpan.FromSeconds(10);

    private readonly long _initialCooldownMs;
    private readonly long _maxCooldownMs;
    private readonly long _stableAfterMs;

    private int _failures;
    private int _failuresBeforeLaunch;
    private long? _launchedAtMs;
    private long? _cooldownUntilMs;
    private long _lastCooldownMs;
    private long? _retryAtMs;

    public OverlayLaunchBackoff()
        : this(DefaultInitialCooldown, DefaultMaxCooldown, DefaultStableAfter)
    {
    }

    public OverlayLaunchBackoff(TimeSpan initialCooldown, TimeSpan maxCooldown, TimeSpan stableAfter)
    {
        _initialCooldownMs = (long)initialCooldown.TotalMilliseconds;
        _maxCooldownMs = (long)maxCooldown.TotalMilliseconds;
        _stableAfterMs = (long)stableAfter.TotalMilliseconds;

        ArgumentOutOfRangeException.ThrowIfLessThan(_initialCooldownMs, 1, nameof(initialCooldown));
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxCooldownMs, _initialCooldownMs, nameof(maxCooldown));
        ArgumentOutOfRangeException.ThrowIfNegative(_stableAfterMs, nameof(stableAfter));
    }

    /// <summary>Consecutive failed launches counted against the helper, zero after a success.</summary>
    public int ConsecutiveFailures => _failures;

    /// <summary>Length of the most recent cooldown in milliseconds, for logging.</summary>
    public long LastCooldownMs => _lastCooldownMs;

    /// <summary>When the pending coalesced retry falls due, or <c>null</c> when none is pending.</summary>
    public long? RetryDueAtMs => _retryAtMs;

    /// <summary>The stable window in milliseconds.</summary>
    public long StableAfterMs => _stableAfterMs;

    /// <summary>When the running helper's launch succeeded, or <c>null</c> when no launched helper is being tracked.</summary>
    public long? LaunchedAtMs => _launchedAtMs;

    /// <summary>Milliseconds left in the current cooldown; zero when a launch is allowed.</summary>
    public long CooldownRemainingMs(long nowMs) =>
        _cooldownUntilMs is { } untilMs && nowMs < untilMs ? untilMs - nowMs : 0;

    /// <summary>The cooldown that follows <paramref name="failures"/> consecutive failures (one-based).</summary>
    public long CooldownAfterFailures(int failures)
    {
        if (failures <= 0)
        {
            return 0;
        }

        var cooldownMs = _initialCooldownMs;
        for (var i = 1; i < failures && cooldownMs < _maxCooldownMs; i++)
        {
            cooldownMs = cooldownMs > _maxCooldownMs / 2 ? _maxCooldownMs : cooldownMs * 2;
        }

        return Math.Min(cooldownMs, _maxCooldownMs);
    }

    /// <summary>
    /// A command needs the helper and it is not running. During a cooldown nothing is launched; when the
    /// latest state keeps the pill on screen, one retry is left pending at the end of the cooldown.
    /// </summary>
    public OverlayLaunchDecision OnLaunchWanted(long nowMs, OverlayDemand latest)
    {
        if (CooldownRemainingMs(nowMs) == 0)
        {
            return OverlayLaunchDecision.Attempt;
        }

        if (latest != OverlayDemand.Sustained)
        {
            return OverlayLaunchDecision.HeldBack;
        }

        // However many commands arrive during the cooldown, exactly one retry fires at its end.
        _retryAtMs ??= _cooldownUntilMs;
        return OverlayLaunchDecision.RetryPending;
    }

    /// <summary>The helper started and its pipe connected. Resets the backoff.</summary>
    public void OnLaunchSucceeded(long nowMs)
    {
        _failuresBeforeLaunch = _failures;
        _failures = 0;
        _cooldownUntilMs = null;
        _retryAtMs = null;
        _launchedAtMs = nowMs;
    }

    /// <summary>
    /// A launch attempt failed. Starts the next cooldown, schedules the one coalesced retry when the
    /// latest state keeps the pill on screen, and returns the cooldown in milliseconds.
    /// </summary>
    public long OnLaunchFailed(long nowMs, OverlayDemand latest)
    {
        _launchedAtMs = null;
        return StartCooldown(nowMs, Increment(_failures), latest);
    }

    /// <summary>
    /// A helper this policy saw launch is gone (its pipe broke or its process exited) as of
    /// <paramref name="lostAtMs"/>. Returns the cooldown it caused in milliseconds, or <c>null</c> when a
    /// relaunch may happen at once: nothing was running, or the helper had been up for at least the
    /// stable period. The cooldown runs from <paramref name="lostAtMs"/>, so it may already have ended.
    /// </summary>
    public long? OnHelperLost(long lostAtMs, OverlayDemand latest)
    {
        if (_launchedAtMs is not { } launchedAtMs)
        {
            return null;
        }

        _launchedAtMs = null;
        if (lostAtMs - launchedAtMs >= _stableAfterMs)
        {
            return null;
        }

        return StartCooldown(lostAtMs, Increment(_failuresBeforeLaunch), latest);
    }

    /// <summary>The helper was ended on purpose (idle suspend, release, shutdown). That is never a failure.</summary>
    public void OnHelperStopped() => _launchedAtMs = null;

    /// <summary>Drops the pending retry. Hiding, suspending, releasing and exiting all call this.</summary>
    public void CancelRetry() => _retryAtMs = null;

    /// <summary>
    /// Takes the pending retry once it is due. Returns <c>true</c> only when the latest state still keeps
    /// the pill on screen, so the retry applies the current state and never a superseded one; a retry
    /// that is no longer needed is dropped.
    /// </summary>
    public bool TryTakeDueRetry(long nowMs, OverlayDemand latest)
    {
        if (_retryAtMs is not { } dueMs || nowMs < dueMs)
        {
            return false;
        }

        _retryAtMs = null;
        return latest == OverlayDemand.Sustained;
    }

    private long StartCooldown(long fromMs, int failures, OverlayDemand latest)
    {
        _failures = failures;
        _lastCooldownMs = CooldownAfterFailures(failures);
        _cooldownUntilMs = fromMs + _lastCooldownMs;

        // Only a pill that stays on screen earns the deferred relaunch. A failure flash does not: by the
        // time the cooldown ends, the moment it described has passed.
        _retryAtMs = latest == OverlayDemand.Sustained ? _cooldownUntilMs : null;
        return _lastCooldownMs;
    }

    private static int Increment(int count) => count == int.MaxValue ? count : count + 1;
}
