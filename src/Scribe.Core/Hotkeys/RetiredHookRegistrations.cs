namespace Scribe.Core.Hotkeys;

/// <summary>
/// Hook thread only: the keyboard hook registrations a move ahead replaced and still keeps. An event already on its way to
/// a replaced registration when the new one landed (inside a hook ahead of it, such as a Remote Desktop client's) still
/// finds it there, and is decided there (<see cref="KeyEventPassOn"/>); a replaced registration is released only once it
/// has been replaced for <see cref="GraceMs"/>. Each hook ahead of it answers within LowLevelHooksTimeout or is skipped:
/// "The hook procedure should process a message in less time than the data entry specified in the LowLevelHooksTimeout
/// value ... If the hook procedure times out, the system passes the message to the next hook", and "The maximum timeout
/// value the system allows is 1000 milliseconds" (Windows 10 1709 and later, LowLevelKeyboardProc). The timeout is each
/// hook's, not the chain's, so the grace, twice that maximum, covers an event held by up to two slow hooks between the new
/// registration and the replaced one, not a longer chain of them. Never released sooner (review round 2, item 3): when
/// every slot holds a registration still inside its grace, the hook thread makes no move until the oldest one's grace ends
/// (<see cref="MillisecondsUntilOldestExpires"/>), rather than release one early. Fed the time, so the rule is tested
/// without a clock.
/// </summary>
internal sealed class RetiredHookRegistrations
{
    /// <summary>How many replaced registrations are kept at most.</summary>
    public const int Capacity = 4;

    /// <summary>How long a replaced registration is kept at least: twice LowLevelHooksTimeout's maximum.</summary>
    public const long GraceMs = 2000;

    private readonly nint[] _handles = new nint[Capacity];
    private readonly long[] _retiredAt = new long[Capacity];
    private int _count;

    /// <summary>Any thread: how many replaced registrations are kept.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Whether every slot is taken, so no registration can be replaced until one is released.</summary>
    public bool IsFull => _count == Capacity;

    /// <summary>
    /// Keeps <paramref name="handle"/>, replaced at <paramref name="nowMs"/>. Returns false, keeping nothing, when every slot
    /// is taken; the hook thread checks <see cref="IsFull"/> before it registers the replacement, so that never happens.
    /// </summary>
    public bool Retire(nint handle, long nowMs)
    {
        if (handle == 0 || _count == Capacity)
        {
            return false;
        }

        _handles[_count] = handle;
        _retiredAt[_count] = nowMs;
        Volatile.Write(ref _count, _count + 1);
        return true;
    }

    /// <summary>
    /// How long, from <paramref name="nowMs"/>, until the oldest registration kept has been replaced for
    /// <paramref name="graceMs"/>: zero when it already has, or when none is kept.
    /// </summary>
    public long MillisecondsUntilOldestExpires(long nowMs, long graceMs) =>
        _count == 0 ? 0 : Math.Max(0, graceMs - (nowMs - _retiredAt[0]));

    /// <summary>
    /// The oldest registration replaced at least <paramref name="graceMs"/> before <paramref name="nowMs"/>, no longer
    /// kept; zero when there is none. Call it until it returns zero.
    /// </summary>
    public nint TakeExpired(long nowMs, long graceMs)
    {
        if (_count == 0 || nowMs - _retiredAt[0] < graceMs)
        {
            return 0;
        }

        var handle = _handles[0];
        RemoveAt(0);
        return handle;
    }

    /// <summary>Any registration still kept, whatever its age, for the thread's exit; zero when none is.</summary>
    public nint TakeAny()
    {
        if (_count == 0)
        {
            return 0;
        }

        var handle = _handles[0];
        RemoveAt(0);
        return handle;
    }

    private void RemoveAt(int index)
    {
        for (var i = index; i < _count - 1; i++)
        {
            _handles[i] = _handles[i + 1];
            _retiredAt[i] = _retiredAt[i + 1];
        }

        _handles[_count - 1] = 0;
        _retiredAt[_count - 1] = 0;
        Volatile.Write(ref _count, _count - 1);
    }
}
