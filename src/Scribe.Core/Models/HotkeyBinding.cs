namespace Scribe.Core.Models;

/// <summary>
/// A configurable dictation trigger: a primary virtual-key plus optional modifiers and
/// the press semantics (hold vs toggle). <paramref name="Suppress"/> indicates the key
/// event should be swallowed by the low-level hook so it does not reach other apps
/// (appropriate for a dedicated push-to-talk key).
/// </summary>
public sealed record HotkeyBinding(
    uint VirtualKey,
    KeyModifiers Modifiers,
    HotkeyMode Mode,
    bool Suppress,
    string? DisplayName = null)
{
    // 0xA3 == VK_RCONTROL. Right Ctrl is a comfortable, rarely-soloed push-to-talk key
    // and is distinguishable from Left Ctrl in a WH_KEYBOARD_LL hook.
    public const uint DefaultVirtualKey = 0xA3;

    /// <summary>Default binding: hold Right Ctrl to talk; the key is suppressed while bound.</summary>
    public static HotkeyBinding Default { get; } =
        new(DefaultVirtualKey, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Ctrl");

    public bool HasModifiers => Modifiers != KeyModifiers.None;
}
