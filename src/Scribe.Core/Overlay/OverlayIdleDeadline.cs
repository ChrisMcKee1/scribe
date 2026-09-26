namespace Scribe.Core.Overlay;

/// <summary>
/// Decides when the overlay helper process has sat idle long enough to be suspended, and makes that
/// decision safe against a command racing it. One of the two policies <see cref="OverlayHelperLifetime"/>
/// combines; the client never calls it directly.
/// </summary>
/// <remarks>
/// <para>
/// The helper owns its idle lifetime. Every state command restarts the idle period, and the period is
/// the keep-warm setting that also governs the speech models (zero or less never suspends). Suspending
/// on the speech models' release instead let a stale release end the pill of a recording that had
/// started in the meantime, and never fired at all when no model was resident, so a helper started by
/// a position preview stayed up for the life of the process.
/// </para>
/// <para>
/// Threading: producers stamp each command with <see cref="NoteCommandIssued"/> before queuing it, from
/// any thread. Every other member belongs to the single command consumer, which is also the only thread
/// that ends the helper, so the checks in <see cref="TryCommitSuspend"/> and <see cref="TryCommitRelease"/>
/// and the teardown that follows them are serialized with command processing. A command stamped before
/// the check vetoes the suspend; one stamped after it is processed after the teardown and relaunches the
/// helper through the normal path.
/// </para>
/// <para>Deterministic: callers pass a monotonic millisecond clock reading; nothing here reads a clock.</para>
/// <para>
/// A pill that hides itself (a dictation's outcome) keeps the helper in use until it has hidden, whatever the latest
/// state asks: the idle deadline never falls before it, and a release request that arrives while it is on screen waits
/// for it (<see cref="OverlayReleaseVerdict.Wait"/>). Otherwise a pause could end the helper under the "Typed" or
/// "Nothing typed" of the very dictation it stopped, before anyone could read it.
/// </para>
/// </remarks>
internal sealed class OverlayIdleDeadline
{
    private long _issued;
    private long _armedStamp;
    private long _lastActivityMs;
    private bool _armed;
    private long _shownUntilMs = long.MinValue;

    /// <summary>
    /// Stamps a command that is about to be queued. Thread-safe. Call it before the enqueue, so a commit
    /// that runs while the command is still on its way already sees it.
    /// </summary>
    public long NoteCommandIssued() => Interlocked.Increment(ref _issued);

    /// <summary>
    /// Consumer only. Restarts the idle period because the command carrying <paramref name="stamp"/> was
    /// taken from the queue at <paramref name="nowMs"/>.
    /// </summary>
    public void OnCommandProcessed(long nowMs, long stamp)
    {
        _armed = true;
        _lastActivityMs = nowMs;
        NoteStampSettled(stamp);
    }

    /// <summary>
    /// Consumer only. Accounts for a stamped command that was taken from the queue without restarting the
    /// idle period (a superseded preview step, a release request), so its stamp does not read as a command
    /// still on its way at the next commit.
    /// </summary>
    public void NoteStampSettled(long stamp)
    {
        // Two producers can stamp in one order and enqueue in the other, so keep the newest stamp seen
        // rather than whichever was taken last.
        if (stamp > _armedStamp)
        {
            _armedStamp = stamp;
        }
    }

    /// <summary>
    /// Consumer only. When the deadline falls due for an idle period of <paramref name="idleMs"/>, or
    /// <c>null</c> when nothing is armed or the period is zero or less (never suspend). The period is
    /// passed on every call so a changed setting also applies to a deadline that is already armed. Never
    /// before an outcome on screen has hidden.
    /// </summary>
    public long? DueAtMs(long idleMs) => _armed && idleMs > 0 ? Math.Max(_lastActivityMs + idleMs, _shownUntilMs) : null;

    /// <summary>
    /// Consumer only. A pill that hides itself (an outcome) went on screen and stays there until
    /// <paramref name="untilMs"/>, its fade out included. Keeps the later of two such times.
    /// </summary>
    public void NoteShownUntil(long untilMs)
    {
        if (untilMs > _shownUntilMs)
        {
            _shownUntilMs = untilMs;
        }
    }

    /// <summary>Consumer only. The helper is gone, ended on purpose or lost, so nothing it showed is still on screen.</summary>
    public void ClearShown() => _shownUntilMs = long.MinValue;

    /// <summary>Consumer only. When the outcome on screen at <paramref name="nowMs"/> hides, or <c>null</c> when none is.</summary>
    public long? ShownUntilMs(long nowMs) => nowMs < _shownUntilMs ? _shownUntilMs : null;

    /// <summary>
    /// Consumer only, at the commit point immediately before the helper is ended. Returns <c>true</c>
    /// exactly when the helper should be suspended now: the deadline is due, no command has been stamped
    /// since it was armed, and the latest state does not keep the pill on screen. A <c>true</c> result
    /// disarms the deadline until the next command.
    /// </summary>
    public bool TryCommitSuspend(long nowMs, long idleMs, OverlayDemand latest)
    {
        if (DueAtMs(idleMs) is not { } dueMs || nowMs < dueMs)
        {
            return false;
        }

        var issued = Interlocked.Read(ref _issued);
        if (issued != _armedStamp)
        {
            // A command arrived after the deadline was armed, and processing it re-arms the deadline.
            // Until then, look again only after another full period, so the consumer never spins on a
            // producer that has stamped its command but not queued it yet.
            _armedStamp = issued;
            _lastActivityMs = nowMs;
            return false;
        }

        if (latest == OverlayDemand.Sustained)
        {
            // A recording or processing pill is in use with no new command, which is what a long
            // recording looks like. Check again after another full period.
            _lastActivityMs = nowMs;
            return false;
        }

        _armed = false;
        return true;
    }

    /// <summary>
    /// Consumer only. The immediate form of <see cref="TryCommitSuspend"/> for a release request stamped
    /// <paramref name="stamp"/> (dictation was paused), checked at <paramref name="nowMs"/>: the helper may be ended when no
    /// command was stamped after the request and the latest state does not keep the pill on screen. That is the same
    /// validation the idle commit runs, so a request can never end the helper of a recording started after it. An outcome
    /// still on screen makes the request wait for it instead. A commit disarms the deadline until the next command.
    /// </summary>
    public OverlayReleaseVerdict TryCommitRelease(long stamp, OverlayDemand latest, long nowMs)
    {
        NoteStampSettled(stamp);
        if (Interlocked.Read(ref _issued) != stamp || latest == OverlayDemand.Sustained)
        {
            return OverlayReleaseVerdict.Veto; // the newer command re-arms the deadline when it is processed
        }

        if (nowMs < _shownUntilMs)
        {
            return OverlayReleaseVerdict.Wait;
        }

        _armed = false;
        return OverlayReleaseVerdict.Commit;
    }

    /// <summary>Consumer only. Disarms the deadline until the next processed command.</summary>
    public void Disarm() => _armed = false;
}

/// <summary>How <see cref="OverlayIdleDeadline.TryCommitRelease"/> judged a release request.</summary>
internal enum OverlayReleaseVerdict
{
    /// <summary>End the helper now.</summary>
    Commit,

    /// <summary>A newer command, or a state that keeps the pill on screen, vetoes it for good; the idle deadline takes over.</summary>
    Veto,

    /// <summary>Nothing vetoes it, but an outcome is on screen: ask again once it has hidden.</summary>
    Wait,
}
