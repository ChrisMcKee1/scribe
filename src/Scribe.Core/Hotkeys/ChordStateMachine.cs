using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

internal enum HotkeyTransition
{
    None,
    Activated,
    Deactivated,
}

/// <summary>
/// One hook event's outcome. <paramref name="Generation"/> identifies the state epoch the
/// transition was computed in: state-clearing operations (capture mode, binding update, reset)
/// bump the generation, letting the dispatcher discard an Activated that raced one of them.
/// </summary>
internal readonly record struct ChordUpdate(HotkeyTransition Transition, bool ShouldSuppress, long Generation);

/// <summary>
/// Pure key-set state machine shared by the global hook and deterministic unit tests.
///
/// It has a single owner and takes no lock. In the service that owner is the hook thread, driven
/// through <see cref="HotkeyEngine"/>: the low-level hook callback runs against a hard OS deadline
/// (LowLevelHooksTimeout, at most 1000 ms, after which Windows silently removes the hook), and a
/// monitor shared with settings reads, rebinding and toggle resets made every key event wait behind
/// whichever of them held it. Other threads change this state only through engine commands that
/// the owner applies. <see cref="IsPressed"/> is the one member any thread may call.
/// </summary>
internal sealed class ChordStateMachine
{
    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkAlt = 0x12;
    private const uint VkLeftShift = 0xA0;
    private const uint VkRightShift = 0xA1;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkLeftAlt = 0xA4;
    private const uint VkRightAlt = 0xA5;
    private const uint VkLeftWin = 0x5B;
    private const uint VkRightWin = 0x5C;

    private readonly KeySet _pressed = new();
    private readonly KeySet _suppressed = new();
    private HotkeyBinding _binding;
    private bool _satisfied;
    private bool _active;
    private bool _captureMode;
    private bool _paused;
    private long _generation;

    public ChordStateMachine(HotkeyBinding binding) => _binding = binding;

    /// <summary>The current state epoch; bumped by every state-clearing operation.</summary>
    public long Generation => _generation;

    public ChordUpdate Process(uint virtualKey, bool isDown)
    {
        // Binding capture in Settings owns the keyboard: every event passes through untouched
        // so the capture box can see the current push-to-talk key, and nothing can start a
        // dictation while the user is choosing a new chord. A code outside the hook's documented
        // 1 to 254 range can never be part of a binding, so it passes through the same way.
        if (_captureMode || !KeySet.IsTrackable(virtualKey))
        {
            return new ChordUpdate(HotkeyTransition.None, ShouldSuppress: false, _generation);
        }

        var isBindingKey = IsBindingKey(_binding, virtualKey);
        var wasSatisfied = _satisfied;
        var repeated = isDown && _pressed.Contains(virtualKey);
        if (isDown)
        {
            _pressed.Add(virtualKey);
        }
        else
        {
            _pressed.Remove(virtualKey);
        }

        // Paused, the binding stands down: nothing activates and no new press is swallowed. Key
        // tracking carries on regardless, because the physical view has to stay true for the
        // leak reconciler, and because a chord still held when dictation resumes must wait for a
        // fresh press rather than firing on the next autorepeat.
        var satisfied = IsSatisfied(_binding);
        var transition = _paused ? HotkeyTransition.None : NextTransition(satisfied);
        _satisfied = satisfied;

        // A keystroke is swallowed or passed whole, paused or not: its autorepeats and its release
        // get the decision its fresh press got (only a state clear, which forgets every key, breaks
        // the pairing). Letting half of one through leaves other apps holding an orphaned key-up,
        // or a key the system believes is stuck down. So pre-emption judges only a fresh press: a
        // Windows key pressed while paused and still held at resume must not have a later repeat
        // pre-empted, or its release is swallowed too and the shell keeps Win held down.
        var shouldSuppress = false;
        if (isDown && _binding.Suppress && isBindingKey &&
            ((!repeated && !_paused && _binding.SuppressChordMembers && NeedsPreemptiveSuppression(virtualKey)) ||
             (!_paused && !wasSatisfied && satisfied) ||
             (repeated && _suppressed.Contains(virtualKey))))
        {
            _suppressed.Add(virtualKey);
            shouldSuppress = true;
        }
        else if (!isDown && _suppressed.Remove(virtualKey))
        {
            shouldSuppress = true;
        }

        return new ChordUpdate(transition, shouldSuppress, _generation);
    }

    // The hold/toggle edge logic, applied to the satisfaction state this event produced.
    private HotkeyTransition NextTransition(bool satisfied)
    {
        if (_binding.Mode == HotkeyMode.Hold)
        {
            if (!_satisfied && satisfied)
            {
                _active = true;
                return HotkeyTransition.Activated;
            }

            if (_satisfied && !satisfied && _active)
            {
                _active = false;
                return HotkeyTransition.Deactivated;
            }

            return HotkeyTransition.None;
        }

        if (!_satisfied && satisfied)
        {
            _active = !_active;
            return _active ? HotkeyTransition.Activated : HotkeyTransition.Deactivated;
        }

        return HotkeyTransition.None;
    }

    public (HotkeyTransition Transition, long Generation) UpdateBinding(HotkeyBinding binding)
    {
        var transition = _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
        _binding = binding;
        ClearState();
        return (transition, _generation);
    }

    /// <summary>
    /// Clears all key state, reporting the deactivation the caller must dispatch when an
    /// activation was in flight (e.g. a hook reinstall mid-recording must stop the recording,
    /// because the held key's eventual release can no longer be matched to cleared state).
    /// </summary>
    public (HotkeyTransition Transition, long Generation) Reset()
    {
        var transition = _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
        ClearState();
        return (transition, _generation);
    }

    // Every clear starts a new generation so transitions computed against the old state are
    // identifiable as stale. The capture and pause modes are not key state and survive it.
    private void ClearState()
    {
        _pressed.Clear();
        _suppressed.Clear();
        _satisfied = false;
        _active = false;
        _generation++;
    }

    public void CancelToggle() => _active = false;

    /// <summary>
    /// Enters or leaves pause. Pausing cancels a hold or toggle latch WITHOUT reporting a
    /// deactivation, because the caller owns stopping a dictation that was already running; it
    /// deliberately keeps every other piece of key state, so a key swallowed before the pause is
    /// still swallowed through its release. Resuming changes nothing else: a chord held across
    /// the resume has to be released and pressed again before it activates.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            _active = false;
        }
    }

    /// <summary>
    /// Enters or leaves binding-capture pass-through. Entering clears all key state and reports
    /// whether an in-flight activation must be deactivated (so a recording in progress stops when
    /// the user starts rebinding). Leaving also clears state: a key still held from the capture
    /// gesture must not satisfy the (possibly brand new) chord until it is pressed fresh.
    /// </summary>
    public (HotkeyTransition Transition, long Generation) SetCaptureMode(bool enabled)
    {
        _captureMode = enabled;
        var transition = enabled && _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
        ClearState();
        return (transition, _generation);
    }

    /// <summary>
    /// True when the hook has seen this key go down and not yet come back up. This is the hook's
    /// view of the physical keyboard, which can legitimately differ from the system's logical
    /// key state when this machine suppressed the events (see the leak reconciler). Safe to call
    /// from any thread: it reads the published key set without a lock.
    /// </summary>
    public bool IsPressed(uint virtualKey) => _pressed.Contains(virtualKey);

    public static bool IsBindingKey(HotkeyBinding binding, uint virtualKey) =>
        virtualKey == binding.VirtualKey ||
        virtualKey == binding.SecondaryVirtualKey ||
        (binding.Modifiers.HasFlag(KeyModifiers.Control) && IsControl(virtualKey)) ||
        (binding.Modifiers.HasFlag(KeyModifiers.Alt) && IsAlt(virtualKey)) ||
        (binding.Modifiers.HasFlag(KeyModifiers.Shift) && IsShift(virtualKey)) ||
        (binding.Modifiers.HasFlag(KeyModifiers.Win) && IsWin(virtualKey));

    // Explicit membership tests rather than LINQ over the set: this runs for every key event on
    // the hook thread, and an enumerator per modifier check is garbage the callback can do without.
    private bool IsSatisfied(HotkeyBinding binding) =>
        _pressed.Contains(binding.VirtualKey) &&
        (binding.SecondaryVirtualKey is not { } second || _pressed.Contains(second)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Control) || AnyPressed(VkControl, VkLeftControl, VkRightControl)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Alt) || AnyPressed(VkAlt, VkLeftAlt, VkRightAlt)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Shift) || AnyPressed(VkShift, VkLeftShift, VkRightShift)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Win) || _pressed.Contains(VkLeftWin) || _pressed.Contains(VkRightWin));

    private bool AnyPressed(uint generic, uint left, uint right) =>
        _pressed.Contains(generic) || _pressed.Contains(left) || _pressed.Contains(right);

    /// <summary>
    /// Whether a chord member has to be swallowed on its own key-down, before the second key
    /// arrives and completes the chord. Only the Windows key does: the shell opens Start the
    /// moment it sees that key-down, so waiting for the partner key is already too late.
    ///
    /// Every other member waits for the chord to complete, because pre-empting a member swallows
    /// that key GLOBALLY for as long as Scribe runs. Binding "Right Ctrl+Right Shift" used to kill
    /// Right Shift system-wide (so Win+Shift+S and every right-handed capital letter died), and
    /// binding "Left Win+H" used to eat every "h" the user typed. Neither key is reserved, so
    /// neither needs pre-empting; the chord-completing branch below still suppresses them once the
    /// chord actually fires.
    /// </summary>
    private static bool NeedsPreemptiveSuppression(uint virtualKey) => IsWin(virtualKey);

    private static bool IsControl(uint key) => key is VkControl or VkLeftControl or VkRightControl;
    private static bool IsAlt(uint key) => key is VkAlt or VkLeftAlt or VkRightAlt;
    private static bool IsShift(uint key) => key is VkShift or VkLeftShift or VkRightShift;
    private static bool IsWin(uint key) => key is VkLeftWin or VkRightWin;
}

internal static class SyntheticInputMarker
{
    internal static readonly nuint Value = Environment.Is64BitProcess
    ? unchecked((nuint)0x534352494245494EUL)
    : (nuint)0x53435249U;
}