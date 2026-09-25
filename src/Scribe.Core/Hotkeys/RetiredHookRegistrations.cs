namespace Scribe.Core.Hotkeys;

/// <summary>
/// Hook thread only: the keyboard hook registrations a move ahead replaced and still keeps. An event already on its way to
/// a replaced registration when the new one landed (inside a hook ahead of it, such as a Remote Desktop client's) still
/// finds it there, and is decided there (<see cref="KeyEventPassOn"/>); a replaced registration is released once it has
/// been replaced for a grace longer than any such event can take to reach it. Every hook ahead of it answers within
/// LowLevelHooksTimeout ("The maximum timeout value the system allows is 1000 milliseconds", Windows 10 1709 and later) or
/// is removed and the event passed on, so the hook thread releases a registration only after twice that. Fed the time, so
/// the rule is tested without a clock. A few slots, filled only by moves; a move past them releases the oldest at once.
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

    /// <summary>
    /// Keeps <paramref name="handle"/>, replaced at <paramref name="nowMs"/>. Returns the oldest one, which must be
    /// released now, when every slot was taken, or zero.
    /// </summary>
    public nint Retire(nint handle, long nowMs)
    {
        if (handle == 0)
        {
            return 0;
        }

        nint evicted = 0;
        if (_count == Capacity)
        {
            evicted = _handles[0];
            RemoveAt(0);
        }

        _handles[_count] = handle;
        _retiredAt[_count] = nowMs;
        Volatile.Write(ref _count, _count + 1);
        return evicted;
    }

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
