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

/// <summary>Pure key-set state machine shared by the global hook and deterministic unit tests.</summary>
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

    private readonly object _gate = new();
    private readonly HashSet<uint> _pressed = new();
    private readonly HashSet<uint> _suppressed = new();
    private HotkeyBinding _binding;
    private bool _satisfied;
    private bool _active;
    private bool _captureMode;
    private long _generation;

    public ChordStateMachine(HotkeyBinding binding) => _binding = binding;

    /// <summary>The current state epoch; bumped by every state-clearing operation.</summary>
    public long Generation
    {
        get { lock (_gate) { return _generation; } }
    }

    public ChordUpdate Process(uint virtualKey, bool isDown)
    {
        lock (_gate)
        {
            // Binding capture in Settings owns the keyboard: every event passes through untouched
            // so the capture box can see the current push-to-talk key, and nothing can start a
            // dictation while the user is choosing a new chord.
            if (_captureMode)
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

            var satisfied = IsSatisfied(_binding);
            var transition = HotkeyTransition.None;
            if (_binding.Mode == HotkeyMode.Hold)
            {
                if (!_satisfied && satisfied)
                {
                    _active = true;
                    transition = HotkeyTransition.Activated;
                }
                else if (_satisfied && !satisfied && _active)
                {
                    _active = false;
                    transition = HotkeyTransition.Deactivated;
                }
            }
            else if (!_satisfied && satisfied)
            {
                _active = !_active;
                transition = _active ? HotkeyTransition.Activated : HotkeyTransition.Deactivated;
            }

            _satisfied = satisfied;

            var shouldSuppress = false;
            if (isDown && _binding.Suppress && isBindingKey &&
                (_binding.SuppressChordMembers ||
                 (!wasSatisfied && satisfied) ||
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
    }

    public (HotkeyTransition Transition, long Generation) UpdateBinding(HotkeyBinding binding)
    {
        lock (_gate)
        {
            var transition = _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
            _binding = binding;
            ClearStateLocked();
            return (transition, _generation);
        }
    }

    /// <summary>
    /// Clears all key state, reporting the deactivation the caller must dispatch when an
    /// activation was in flight (e.g. a hook reinstall mid-recording must stop the recording,
    /// because the held key's eventual release can no longer be matched to cleared state).
    /// </summary>
    public (HotkeyTransition Transition, long Generation) Reset()
    {
        lock (_gate)
        {
            var transition = _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
            ClearStateLocked();
            return (transition, _generation);
        }
    }

    // Callers hold _gate. Every clear starts a new generation so transitions computed against
    // the old state are identifiable as stale.
    private void ClearStateLocked()
    {
        _pressed.Clear();
        _suppressed.Clear();
        _satisfied = false;
        _active = false;
        _generation++;
    }

    public void CancelToggle()
    {
        lock (_gate)
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
        lock (_gate)
        {
            _captureMode = enabled;
            var transition = enabled && _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
            ClearStateLocked();
            return (transition, _generation);
        }
    }

    /// <summary>
    /// True when the hook has seen this key go down and not yet come back up. This is the hook's
    /// view of the physical keyboard, which can legitimately differ from the system's logical
    /// key state when this machine suppressed the events (see the leak reconciler).
    /// </summary>
    public bool IsPressed(uint virtualKey)
    {
        lock (_gate)
        {
            return _pressed.Contains(virtualKey);
        }
    }

    public static bool IsBindingKey(HotkeyBinding binding, uint virtualKey) =>
        virtualKey == binding.VirtualKey ||
        virtualKey == binding.SecondaryVirtualKey ||
        (binding.Modifiers.HasFlag(KeyModifiers.Control) && IsControl(virtualKey)) ||
        (binding.Modifiers.HasFlag(KeyModifiers.Alt) && IsAlt(virtualKey)) ||
        (binding.Modifiers.HasFlag(KeyModifiers.Shift) && IsShift(virtualKey)) ||
        (binding.Modifiers.HasFlag(KeyModifiers.Win) && IsWin(virtualKey));

    private bool IsSatisfied(HotkeyBinding binding) =>
        _pressed.Contains(binding.VirtualKey) &&
        (binding.SecondaryVirtualKey is not { } second || _pressed.Contains(second)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Control) || _pressed.Any(IsControl)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Alt) || _pressed.Any(IsAlt)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Shift) || _pressed.Any(IsShift)) &&
        (!binding.Modifiers.HasFlag(KeyModifiers.Win) || _pressed.Any(IsWin));

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