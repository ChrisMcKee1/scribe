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

    private readonly Func<uint, bool> _isLogicallyDown;
    private readonly Func<uint, bool> _isPhysicallyPressed;
    private readonly Action<uint> _releaseKey;

    /// <param name="isLogicallyDown">The system's view (GetAsyncKeyState high bit).</param>
    /// <param name="isPhysicallyPressed">The hook's view (ChordStateMachine.IsPressed).</param>
    /// <param name="releaseKey">Injects a synthetic, marker-tagged key-up for the key.</param>
    public SuppressedKeyReconciler(
        Func<uint, bool> isLogicallyDown,
        Func<uint, bool> isPhysicallyPressed,
        Action<uint> releaseKey)
    {
        _isLogicallyDown = isLogicallyDown;
        _isPhysicallyPressed = isPhysicallyPressed;
        _releaseKey = releaseKey;
    }

    /// <summary>
    /// Releases every candidate key of <paramref name="binding"/> that the system believes is
    /// still down although the hook saw it released. A key the user genuinely holds right now
    /// (hook agrees it is down) is never touched, so a real modifier held for a shortcut
    /// survives reconciliation. Returns the keys that were released, for logging.
    /// </summary>
    public IReadOnlyList<uint> ReleaseLeakedKeys(HotkeyBinding binding)
    {
        if (!binding.Suppress)
        {
            return [];
        }

        List<uint>? released = null;
        foreach (var key in CandidateKeys(binding))
        {
            if (_isLogicallyDown(key) && !_isPhysicallyPressed(key))
            {
                _releaseKey(key);
                (released ??= []).Add(key);
            }
        }

        return released ?? (IReadOnlyList<uint>)[];
    }

    /// <summary>
    /// The physical keys this binding can suppress: the explicit chord keys plus, for a
    /// modifier-flag binding, both left/right variants (the hook suppresses whichever generic
    /// modifier completes the chord).
    /// </summary>
    internal static IEnumerable<uint> CandidateKeys(HotkeyBinding binding)
    {
        var keys = new HashSet<uint> { binding.VirtualKey };
        if (binding.SecondaryVirtualKey is { } secondary)
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
