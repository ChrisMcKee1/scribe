namespace Scribe.Core.Overlay;

/// <summary>What the overlay client's command consumer can see of the helper process.</summary>
public enum OverlayHelperStatus
{
    /// <summary>No helper process: none was launched yet, or the last one was ended and cleaned up.</summary>
    Absent,

    /// <summary>A helper is running and its pipe is connected.</summary>
    Alive,

    /// <summary>
    /// A helper launched earlier is gone (its process exited or its pipe broke) and its leftovers are
    /// still held. The caller discards them right after the decision it passed this to.
    /// </summary>
    Lost,
}

/// <summary>The helper process as the consumer observed it when it asked for a decision.</summary>
/// <param name="Status">Whether a helper is running.</param>
/// <param name="LostAtMs">
/// For a lost helper, when it was lost on the caller's monotonic clock: its process exit time when the
/// operating system reported one, otherwise the moment the loss was noticed. Ignored otherwise.
/// </param>
public readonly record struct OverlayHelperObservation(OverlayHelperStatus Status, long LostAtMs = 0)
{
    /// <summary>No helper is running.</summary>
    public static OverlayHelperObservation Absent => new(OverlayHelperStatus.Absent);

    /// <summary>A helper is running and reachable.</summary>
    public static OverlayHelperObservation Alive => new(OverlayHelperStatus.Alive);

    /// <summary>A helper launched earlier was lost at <paramref name="lostAtMs"/>.</summary>
    public static OverlayHelperObservation Lost(long lostAtMs) => new(OverlayHelperStatus.Lost, lostAtMs);
}

/// <summary>What the consumer does with one command.</summary>
public enum OverlayCommandAction
{
    /// <summary>The helper is running: write the command to it.</summary>
    Write,

    /// <summary>
    /// No helper is running, the command needs one, and no cooldown holds it back: launch it (the launch
    /// replays the applied anchor and the latest state), report the outcome, then write the command if the
    /// helper came up.
    /// </summary>
    Launch,

    /// <summary>
    /// No helper is running and a cooldown holds the launch back: drop the command. When the latest state
    /// keeps the pill on screen, exactly one retry is pending at the end of the cooldown.
    /// </summary>
    Hold,

    /// <summary>No helper is running and the command does not need one: drop it.</summary>
    Drop,
}

/// <summary>Work that has fallen due between commands.</summary>
public enum OverlayDueWork
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>
    /// End the running helper to reclaim its memory (EXIT, then kill), keeping the consumer running so the
    /// next command that needs the pill relaunches it.
    /// </summary>
    Suspend,

    /// <summary>A cooldown ended while the pill must stay on screen: launch the helper; the launch replays the latest state.</summary>
    Retry,

    /// <summary>
    /// End the running helper because dictation was paused (EXIT, then kill), as <see cref="Suspend"/> does, now or once the
    /// outcome that was on screen when the pause asked has hidden.
    /// </summary>
    Release,
}

/// <summary>How a launch attempt ended.</summary>
public enum OverlayLaunchResult
{
    /// <summary>The helper started and its pipe connected.</summary>
    Launched,

    /// <summary>The helper could not be started, died during startup, or never opened its pipe. Starts the next cooldown.</summary>
    Failed,

    /// <summary>Shutdown cancelled the attempt. Not a failure.</summary>
    Abandoned,
}

/// <summary>How a lost helper was classified, for the log.</summary>
/// <param name="LivedMs">How long it ran after its launch succeeded, or <c>null</c> when that launch was not tracked.</param>
/// <param name="CooldownMs">The cooldown the loss caused, or <c>null</c> when the helper had proven stable.</param>
public readonly record struct OverlayHelperLoss(long? LivedMs, long? CooldownMs);

/// <summary>
/// Every lifetime decision the overlay client's command consumer makes about the helper process: when to
/// wake, whether an idle helper is suspended, whether a command is written, launches the helper, or waits
/// out a cooldown, how a lost helper is classified, what a launch outcome does, and what shutdown ends.
/// The client carries the decisions out (process start, pipe I/O, kill) and decides nothing itself, so the
/// behavior is pinned here against a scripted clock instead of living in glue that no test reaches.
/// </summary>
/// <remarks>
/// <para>
/// It combines two policies: <see cref="OverlayIdleDeadline"/> (the keep-warm period and the validation
/// that makes a suspend safe against a racing command) and <see cref="OverlayLaunchBackoff"/> (cooldown,
/// the one coalesced retry, and the stable window). The combination rules live here: a suspend or release
/// cancels a pending retry, a lost helper is classified before anything else is decided, the consumer
/// wakes at the earliest of the idle deadline, the retry and a waiting release, and after shutdown nothing
/// falls due.
/// </para>
/// <para>
/// A lost helper is brought back whenever the latest state keeps the pill on screen (a recording or
/// processing pill), whichever command noticed the loss (a live level meter included) and after a failed
/// write alike, always through the cooldown gate. Before this, a helper that crashed during a long recording
/// stayed gone until the next state change, because only state commands relaunched and a gone helper is
/// never written to. A dictation's outcome is not such a state: it is shown once, by its own command (which
/// may launch the helper), and never replayed, so relaunching for it later would show nothing.
/// </para>
/// <para>
/// An outcome keeps the helper in use while it is on screen (its hold and fade out): the idle deadline never falls
/// before it has hidden, and a pause release that arrives while it is on screen waits for it, then commits if nothing
/// vetoed it meanwhile. It is timed from when its write to the helper returned (<see cref="OnShown"/>), after a launch
/// too: a pipe write can take up to the client's timeout and still succeed, and the overlay starts its own hold only
/// once it has the line. One whose write failed, or that was never written, keeps nothing.
/// </para>
/// <para>
/// Threading: <see cref="IssueStamp"/> is called by producers on any thread, before they queue a command.
/// Every other member belongs to the single command consumer, which is also the only thread that starts or
/// ends the helper. The caller passes the demand of the latest requested state read at the moment of each
/// call: a demand read earlier (before a blocking launch, say) could suspend a helper that a long
/// recording still shows, since such a recording sends only unstamped level meters.
/// </para>
/// <para>Deterministic: callers pass monotonic millisecond readings; nothing here reads a clock or blocks.</para>
/// </remarks>
public sealed class OverlayHelperLifetime
{
    private readonly OverlayIdleDeadline _idle = new();
    private readonly OverlayLaunchBackoff _backoff;
    private long _idleMs;
    private bool _exited;

    // A pause release that waits for the outcome on screen to hide: its stamp, and when it falls due.
    private long _releaseStamp;
    private long? _releaseDueAtMs;

    /// <summary>Uses the default schedule: 1 s cooldown doubling to 60 s, and a 10 s stable window.</summary>
    public OverlayHelperLifetime()
        : this(OverlayLaunchBackoff.DefaultInitialCooldown, OverlayLaunchBackoff.DefaultMaxCooldown, OverlayLaunchBackoff.DefaultStableAfter)
    {
    }

    /// <summary>Uses a custom failure schedule.</summary>
    public OverlayHelperLifetime(TimeSpan initialCooldown, TimeSpan maxCooldown, TimeSpan stableAfter)
    {
        _backoff = new OverlayLaunchBackoff(initialCooldown, maxCooldown, stableAfter);
    }

    /// <summary>Consecutive failed launches, zero after a success. For logging.</summary>
    public int ConsecutiveFailures => _backoff.ConsecutiveFailures;

    /// <summary>Length of the most recent cooldown in milliseconds. For logging.</summary>
    public long LastCooldownMs => _backoff.LastCooldownMs;

    /// <summary>When the pending coalesced retry falls due, or <c>null</c> when none is pending.</summary>
    public long? RetryDueAtMs => _backoff.RetryDueAtMs;

    /// <summary>How long a launched helper must run before losing it is no longer counted as a failed launch.</summary>
    public long StableAfterMs => _backoff.StableAfterMs;

    /// <summary>The idle period in milliseconds; zero never suspends.</summary>
    public long IdlePeriodMs => _idleMs;

    /// <summary>How the most recently lost helper was classified, or <c>null</c> before any loss. For logging.</summary>
    public OverlayHelperLoss? LastLoss { get; private set; }

    /// <summary>Milliseconds left in the current cooldown; zero when a launch is allowed.</summary>
    public long CooldownRemainingMs(long nowMs) => _backoff.CooldownRemainingMs(nowMs);

    /// <summary>
    /// Producer side, thread-safe. Stamps a command that is about to be queued. Call it after publishing
    /// the state the command asks for and before the enqueue, so a suspend committing while the command is
    /// still on its way already sees it.
    /// </summary>
    public long IssueStamp() => _idle.NoteCommandIssued();

    /// <summary>
    /// Sets the idle period after which an unused helper is suspended (the keep-warm setting). Zero or less
    /// never suspends. It also applies to a deadline that is already armed.
    /// </summary>
    public void SetIdlePeriodMs(long idleMs) => _idleMs = Math.Max(0, idleMs);

    /// <summary>
    /// When the consumer must next wake to run <see cref="TakeDueWork"/> even if no command arrives: the
    /// earliest of the idle deadline, the pending retry and a pause release waiting for an outcome to hide, or
    /// <c>null</c> to wait for the next command.
    /// </summary>
    public long? NextWakeAtMs() =>
        _exited ? null : Earliest(Earliest(_idle.DueAtMs(_idleMs), _backoff.RetryDueAtMs), _releaseDueAtMs);

    /// <summary>
    /// Runs whatever has fallen due at <paramref name="nowMs"/>. Call it whenever <see cref="NextWakeAtMs"/>
    /// is at or before now; it always moves that wake time past now or clears it, so the consumer cannot
    /// spin. A pause release that waited for an outcome to hide is judged again, as when it arrived. An idle
    /// suspend commits only when no command has been stamped since the deadline was armed, the latest state
    /// does not keep the pill on screen and no outcome is still on screen; it cancels a pending retry. The
    /// retry applies only while the latest state still keeps the pill on screen.
    /// </summary>
    public OverlayDueWork TakeDueWork(long nowMs, OverlayDemand demand, OverlayHelperObservation helper)
    {
        if (_exited)
        {
            return OverlayDueWork.None;
        }

        NoteIfLost(helper, demand);
        if (_releaseDueAtMs is { } releaseDueMs && nowMs >= releaseDueMs && TryRelease(nowMs, _releaseStamp, demand))
        {
            return helper.Status == OverlayHelperStatus.Alive ? OverlayDueWork.Release : OverlayDueWork.None;
        }

        if (_idle.TryCommitSuspend(nowMs, _idleMs, demand))
        {
            EndedOnPurpose();
            return helper.Status == OverlayHelperStatus.Alive ? OverlayDueWork.Suspend : OverlayDueWork.None;
        }

        return _backoff.TryTakeDueRetry(nowMs, demand) && helper.Status != OverlayHelperStatus.Alive
            ? OverlayDueWork.Retry
            : OverlayDueWork.None;
    }

    /// <summary>
    /// A stamped state command was taken from the queue at <paramref name="nowMs"/>. It restarts the idle
    /// period. <paramref name="ensureAlive"/> marks a command that needs the helper running (a state the
    /// pill must show, the startup warmup, a preview); <paramref name="cancelsRetry"/> marks the engine's
    /// hide, which drops a pending retry. An outcome the command shows keeps the helper once its write has
    /// returned (<see cref="OnShown"/>), not from here: the write, after a launch or not, may still take a while.
    /// <paramref name="superseded"/> marks a command for a state the engine has since replaced; the newer state's own
    /// command is queued behind it. Such a command never launches the helper on its own account (it would be launched to
    /// show something already over); a lost helper it notices is still brought back while the latest state keeps the pill
    /// on screen, as by any command. With the helper running it is <see cref="OverlayCommandAction.Write"/>, for what the
    /// command owes besides its own line (the anchor a superseded preview moved), and the caller writes nothing of the
    /// line itself, judging again right before the write, after any launch.
    /// </summary>
    public OverlayCommandAction OnStateCommand(
        long nowMs, long stamp, bool ensureAlive, bool cancelsRetry, OverlayDemand demand, OverlayHelperObservation helper,
        bool superseded = false)
    {
        if (_exited)
        {
            return OverlayCommandAction.Drop;
        }

        _idle.OnCommandProcessed(nowMs, stamp);
        if (cancelsRetry)
        {
            _backoff.CancelRetry();
        }

        return Decide(nowMs, ensureAlive && !superseded, demand, helper);
    }

    /// <summary>
    /// A live level meter was taken from the queue. Meters are unstamped: a long recording sends nothing
    /// else, and it must not read as activity that keeps restarting the idle period. A meter never launches
    /// a helper on its own, but one that finds the helper lost brings it back while the pill is on screen.
    /// </summary>
    public OverlayCommandAction OnMeter(long nowMs, OverlayDemand demand, OverlayHelperObservation helper) =>
        _exited ? OverlayCommandAction.Drop : Decide(nowMs, ensureAlive: false, demand, helper);

    /// <summary>
    /// A stamped command was taken from the queue and dropped without being carried out (a superseded
    /// preview step). Its stamp is settled so it does not read as a command still on its way; it is not
    /// activity.
    /// </summary>
    public void OnCommandDropped(long stamp)
    {
        if (!_exited)
        {
            _idle.NoteStampSettled(stamp);
        }
    }

    /// <summary>
    /// Writing to a running helper failed, so it is lost as of <paramref name="lostAtMs"/>. The caller
    /// discards it; the result says whether to bring it back now (<see cref="OverlayCommandAction.Launch"/>),
    /// after the cooldown (<see cref="OverlayCommandAction.Hold"/>), or not at all while the latest state does
    /// not keep the pill on screen (<see cref="OverlayCommandAction.Drop"/>): nothing is, or an outcome is, which
    /// a relaunch would not show again.
    /// </summary>
    public OverlayCommandAction OnWriteFailed(long nowMs, long lostAtMs, OverlayDemand demand)
    {
        if (_exited)
        {
            return OverlayCommandAction.Drop;
        }

        NoteLoss(lostAtMs, demand);
        return demand == OverlayDemand.Sustained ? Gate(nowMs, demand) : OverlayCommandAction.Drop;
    }

    /// <summary>
    /// A request stamped <paramref name="stamp"/> to end the helper now instead of after the idle period
    /// (dictation was paused) was taken from the queue at <paramref name="nowMs"/>. It runs the idle commit's
    /// validation immediately: a command stamped after the request, or a latest state that keeps the pill on
    /// screen, vetoes it, so it can never end the helper of a recording started after it. When nothing vetoes it
    /// but an outcome is on screen (the dictation the pause stopped has just shown its "Typed" or "Nothing
    /// typed"), it waits for the outcome to hide and is judged again then (<see cref="ReleaseDueAtMs"/>); a newer
    /// request takes its place. A commit cancels a pending retry.
    /// </summary>
    public OverlayDueWork OnReleaseWhenIdle(long nowMs, long stamp, OverlayDemand demand, OverlayHelperObservation helper)
    {
        if (_exited)
        {
            return OverlayDueWork.None;
        }

        NoteIfLost(helper, demand);
        return TryRelease(nowMs, stamp, demand) && helper.Status == OverlayHelperStatus.Alive
            ? OverlayDueWork.Release
            : OverlayDueWork.None;
    }

    /// <summary>When a pause release waiting for an outcome to hide falls due, or <c>null</c> when none waits. For logging.</summary>
    public long? ReleaseDueAtMs => _releaseDueAtMs;

    /// <summary>
    /// An outcome's write to the running helper returned at <paramref name="nowMs"/>, a fresh reading taken after the
    /// write: it is on screen for <paramref name="showsForMs"/> from then (its hold and fade out), and until then the helper
    /// is not ended. The overlay starts its own hold once it has read and shown the line, a moment after the write
    /// returns; the fade out allowance covers that.
    /// </summary>
    public void OnShown(long nowMs, long showsForMs)
    {
        if (!_exited && showsForMs > 0)
        {
            _idle.NoteShownUntil(nowMs + showsForMs);
        }
    }

    /// <summary>
    /// A launch this type asked for (<see cref="OverlayCommandAction.Launch"/> or
    /// <see cref="OverlayDueWork.Retry"/>) ended at <paramref name="nowMs"/>. Pass the demand read after the
    /// attempt, since the state may have moved on while it blocked. A success resets the backoff; a failure
    /// starts the next cooldown and, when the latest state keeps the pill on screen, schedules the one retry;
    /// an attempt abandoned by shutdown changes nothing.
    /// </summary>
    public void OnLaunchCompleted(long nowMs, OverlayLaunchResult result, OverlayDemand demand)
    {
        if (_exited)
        {
            return;
        }

        switch (result)
        {
            case OverlayLaunchResult.Launched:
                _backoff.OnLaunchSucceeded(nowMs);
                break;
            case OverlayLaunchResult.Failed:
                _backoff.OnLaunchFailed(nowMs, demand);
                break;
        }
    }

    /// <summary>
    /// The consumer took EXIT: the helper is ended on purpose (never a failure), a pending retry is dropped,
    /// and from here on nothing falls due and every command is dropped.
    /// </summary>
    public void OnExit()
    {
        _exited = true;
        _idle.Disarm();
        EndedOnPurpose();
    }

    private OverlayCommandAction Decide(long nowMs, bool ensureAlive, OverlayDemand demand, OverlayHelperObservation helper)
    {
        var lost = NoteIfLost(helper, demand);
        if (helper.Status == OverlayHelperStatus.Alive)
        {
            return OverlayCommandAction.Write;
        }

        // Only a state that stays on screen brings a lost helper back: an outcome is never replayed.
        var needsHelper = ensureAlive || (lost && demand == OverlayDemand.Sustained);
        return needsHelper ? Gate(nowMs, demand) : OverlayCommandAction.Drop;
    }

    // Judges a release request (a new one, or one that waited): a commit ends the helper on purpose, and a request that
    // must wait for the outcome on screen is kept, in place of any that waited before it, until the outcome hides.
    private bool TryRelease(long nowMs, long stamp, OverlayDemand demand)
    {
        _releaseDueAtMs = null;
        switch (_idle.TryCommitRelease(stamp, demand, nowMs))
        {
            case OverlayReleaseVerdict.Commit:
                EndedOnPurpose();
                return true;
            case OverlayReleaseVerdict.Wait:
                _releaseStamp = stamp;
                _releaseDueAtMs = _idle.ShownUntilMs(nowMs);
                return false;
            default:
                return false;
        }
    }

    private static long? Earliest(long? first, long? second) =>
        first is { } a && second is { } b ? Math.Min(a, b) : first ?? second;

    // The one gate every launch passes: outside a cooldown it launches; inside one it holds, and a pill
    // that must stay on screen leaves exactly one retry pending for the cooldown's end.
    private OverlayCommandAction Gate(long nowMs, OverlayDemand demand) =>
        _backoff.OnLaunchWanted(nowMs, demand) == OverlayLaunchDecision.Attempt
            ? OverlayCommandAction.Launch
            : OverlayCommandAction.Hold;

    private bool NoteIfLost(OverlayHelperObservation helper, OverlayDemand demand)
    {
        if (helper.Status != OverlayHelperStatus.Lost)
        {
            return false;
        }

        NoteLoss(helper.LostAtMs, demand);
        return true;
    }

    private void NoteLoss(long lostAtMs, OverlayDemand demand)
    {
        var launchedAtMs = _backoff.LaunchedAtMs;
        var cooldownMs = _backoff.OnHelperLost(lostAtMs, demand);
        LastLoss = new OverlayHelperLoss(launchedAtMs is { } atMs ? Math.Max(0, lostAtMs - atMs) : null, cooldownMs);
        _idle.ClearShown(); // a gone helper shows nothing
    }

    private void EndedOnPurpose()
    {
        _backoff.CancelRetry();
        _backoff.OnHelperStopped();
        _releaseDueAtMs = null;
    }
}
