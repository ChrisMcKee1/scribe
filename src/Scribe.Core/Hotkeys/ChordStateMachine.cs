using Scribe.Core.Models;

namespace Scribe.Core.Hotkeys;

internal enum HotkeyTransition
{
    None,
    Activated,
    Deactivated,
}

internal readonly record struct ChordUpdate(HotkeyTransition Transition, bool ShouldSuppress);

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

    public ChordStateMachine(HotkeyBinding binding) => _binding = binding;

    public ChordUpdate Process(uint virtualKey, bool isDown)
    {
        lock (_gate)
        {
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

            return new ChordUpdate(transition, shouldSuppress);
        }
    }

    public HotkeyTransition UpdateBinding(HotkeyBinding binding)
    {
        lock (_gate)
        {
            var transition = _active ? HotkeyTransition.Deactivated : HotkeyTransition.None;
            _binding = binding;
            _pressed.Clear();
            _suppressed.Clear();
            _satisfied = false;
            _active = false;
            return transition;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pressed.Clear();
            _suppressed.Clear();
            _satisfied = false;
            _active = false;
        }
    }

    public void CancelToggle()
    {
        lock (_gate)
        {
            _active = false;
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