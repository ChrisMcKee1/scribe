using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

/// <summary>
/// Self-healing for leaked suppressed keys. The low-level hook swallows the push-to-talk key so
/// other apps never see it, but Windows enforces a hard deadline on hook callbacks
/// (LowLevelHooksTimeout, capped at 1000 ms since Windows 10 1709): a late callback means that
/// one event is delivered PAST the hook. If an autorepeat key-down leaks through during a long
/// hold and the final key-up is then suppressed as usual, the system's logical key state is left
/// stuck down, and because the hook keeps swallowing that key, the user can never release it from
/// the affected side of the keyboard. This class detects exactly that state (system says down,
/// the hook's physical view says released) and injects a synthetic key-up to unstick it.
///
/// "The hook's view says released" is evidence only while that view is whole, so the service runs this only when a key
/// or button release the bindings themselves swallowed asks for it, or a dictation's release does (review round 8, A9),
/// and it is excluded from binding capture (round 9, A11). Capture tracks no key, and every state clear (capture's start
/// and end, a desktop switch, new bindings, a reinstall) forgets the keys held across it; a mouse event that only settles
/// a release owed from before such a clear asks for the mouse hook's sync alone. Asked for then, the check found a
/// modifier the user was holding down in Windows and not in the view, and released it under the user's finger.
///
/// The exclusion is <c>gate</c> and <c>mayJudge</c>: each key is judged and released inside the gate, which the service's
/// capture start also takes, and <c>mayJudge</c> (false while capture is being admitted or owns input, or no hook runs)
/// is checked inside it twice, before the key's reads and again after them, before the key-up. So a key-up is sent only
/// on reads made while capture did not own input, and, while capture's start holds the gate, none is sent; capture's
/// start waits for the gate a bounded time, and if a key-up outlasts that wait, the second check still stops every key
/// whose reads came after capture was requested (see <c>HotkeyService.AdmitCapture</c>).
///
/// Keys only: a mouse button is never a candidate, and Scribe injects no mouse input of any kind. "The hook does not
/// hold it" also means "the hook never saw it go down", as for a button held in another app since before the mouse hook
/// existed, and a claim made on better evidence could still be overtaken by a new press before the injection, which then
/// ended a drag the user was making. Nothing needs the repair either: a mouse button does not repeat, and on Windows 7 and
/// later a hook that misses the deadline is removed rather than skipped, so a leaked press is one Windows received. Its
/// release reaches the app too: before the watchdog's renewal no hook sees it, and the renewal that finds the hook gone
/// drops every release the engine owed, so a release after it passes with no read
/// (<see cref="HotkeyEngine.OnMouseHookLost"/>), which also ends the recording that press started.
/// </summary>
internal sealed class SuppressedKeyReconciler
{
    private const uint VkLeftShift = 0xA0;
    private const uint VkRightShift = 0xA1;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkLeftAlt = 0xA4;
    private const uint VkRightAlt = 0xA5;
    private const uint VkLeftWin = 0x5B;
    private const uint VkRightWin = 0x5C;

    /// <summary>
    /// Keys whose synthetic release succeeded, keys where SendInput was rejected, and whether the check stopped because
    /// it could not judge any more (<c>mayJudge</c> said no).
    /// </summary>
    public readonly record struct Result(IReadOnlyList<uint> Released, IReadOnlyList<uint> Failed, bool Interrupted = false);

    private readonly Func<uint, bool> _isLogicallyDown;
    private readonly Func<uint, bool> _isPhysicallyPressed;
    private readonly Func<uint, bool> _releaseKey;
    private readonly object _gate;
    private readonly Func<bool> _mayJudge;

    /// <param name="isLogicallyDown">The system's view (GetAsyncKeyState high bit).</param>
    /// <param name="isPhysicallyPressed">The hook's view (ChordStateMachine.IsPressed).</param>
    /// <param name="releaseKey">
    /// Injects a synthetic, marker-tagged key-up; returns false when the injection was rejected
    /// (e.g. UIPI or a desktop switch), so a failed release is never reported as healed.
    /// </param>
    public SuppressedKeyReconciler(
        Func<uint, bool> isLogicallyDown,
        Func<uint, bool> isPhysicallyPressed,
        Func<uint, bool> releaseKey)
        : this(isLogicallyDown, isPhysicallyPressed, releaseKey, new object(), static () => true)
    {
    }

    /// <param name="isLogicallyDown">As above.</param>
    /// <param name="isPhysicallyPressed">As above.</param>
    /// <param name="releaseKey">As above.</param>
    /// <param name="gate">
    /// Held while one key is judged and released; whoever must exclude a key-up (the service's capture start) takes it
    /// too. Never the hook thread's.
    /// </param>
    /// <param name="mayJudge">Whether the hook's view may be trusted right now; checked inside the gate.</param>
    public SuppressedKeyReconciler(
        Func<uint, bool> isLogicallyDown,
        Func<uint, bool> isPhysicallyPressed,
        Func<uint, bool> releaseKey,
        object gate,
        Func<bool> mayJudge)
    {
        _isLogicallyDown = isLogicallyDown;
        _isPhysicallyPressed = isPhysicallyPressed;
        _releaseKey = releaseKey;
        _gate = gate;
        _mayJudge = mayJudge;
    }

    /// <summary>
    /// Releases every candidate key of <paramref name="binding"/> that the system believes is
    /// still down although the hook saw it released. A key the user genuinely holds right now
    /// (hook agrees it is down) is never touched, so a real modifier held for a shortcut
    /// survives reconciliation. Stops, reporting <see cref="Result.Interrupted"/>, at the first key it may not judge.
    /// </summary>
    public Result ReleaseLeakedKeys(HotkeyBinding binding)
    {
        if (!binding.Suppress)
        {
            return new Result([], []);
        }

        List<uint>? released = null;
        List<uint>? failed = null;
        foreach (var key in CandidateKeys(binding))
        {
            // One key at a time, so capture's start waits for at most one key's reads and key-up.
            lock (_gate)
            {
                if (!_mayJudge())
                {
                    return Finish(released, failed, interrupted: true);
                }

                if (!_isLogicallyDown(key) || _isPhysicallyPressed(key))
                {
                    continue;
                }

                // Again after the reads, which the fence keeps before it: capture requested since by a start that could
                // not wait for this gate means they may have seen a view capture had cleared, and the key-up is not sent.
                Interlocked.MemoryBarrier();
                if (!_mayJudge())
                {
                    return Finish(released, failed, interrupted: true);
                }

                if (_releaseKey(key))
                {
                    (released ??= []).Add(key);
                }
                else
                {
                    (failed ??= []).Add(key);
                }
            }
        }

        return Finish(released, failed, interrupted: false);
    }

    private static Result Finish(List<uint>? released, List<uint>? failed, bool interrupted) =>
        new(released ?? (IReadOnlyList<uint>)[], failed ?? (IReadOnlyList<uint>)[], interrupted);

    /// <summary>
    /// The physical keys this binding can suppress: the explicit chord keys plus, for a
    /// modifier-flag binding, both left/right variants (the hook suppresses whichever generic
    /// modifier completes the chord). Never a mouse button (see the class summary).
    /// </summary>
    internal static IEnumerable<uint> CandidateKeys(HotkeyBinding binding)
    {
        var keys = new HashSet<uint>();
        if (!MouseButtons.IsMouseButton(binding.VirtualKey))
        {
            keys.Add(binding.VirtualKey);
        }

        if (binding.SecondaryVirtualKey is { } secondary && !MouseButtons.IsMouseButton(secondary))
        {
            keys.Add(secondary);
        }

        if (binding.Modifiers.HasFlag(KeyModifiers.Control))
        {
            keys.Add(VkLeftControl);
            keys.Add(VkRightControl);
        }

        if (binding.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            keys.Add(VkLeftAlt);
            keys.Add(VkRightAlt);
        }

        if (binding.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            keys.Add(VkLeftShift);
            keys.Add(VkRightShift);
        }

        if (binding.Modifiers.HasFlag(KeyModifiers.Win))
        {
            keys.Add(VkLeftWin);
            keys.Add(VkRightWin);
        }

        return keys;
    }
}
