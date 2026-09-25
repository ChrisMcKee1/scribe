namespace Scribe.Core.Models;

/// <summary>
/// A configurable dictation trigger: a primary virtual-key plus optional modifiers and
/// the press semantics (hold vs toggle). <paramref name="Suppress"/> indicates the key
/// event should be swallowed by the low-level hook so it does not reach other apps
/// (appropriate for a dedicated push-to-talk key).
///
/// <paramref name="SuppressChordMembers"/> asks for a chord member to be swallowed on its own
/// key-down rather than waiting for the chord to complete. Only the Windows key actually gets
/// that treatment (see <c>ChordStateMachine.NeedsPreemptiveSuppression</c>): honouring it for
/// every member swallowed that key globally, which is how binding "Right Ctrl+Right Shift" once
/// killed Right Shift across the whole system. The name is load-bearing for settings
/// deserialization, so it stays even though the state machine now narrows what it means.
/// </summary>
public sealed record HotkeyBinding(
    uint VirtualKey,
    KeyModifiers Modifiers,
    HotkeyMode Mode,
    bool Suppress,
    string? DisplayName = null,
    uint? SecondaryVirtualKey = null,
    bool SuppressChordMembers = false)
{
    private const uint VkPrior = 0x21; // Page Up
    private const uint VkNext = 0x22; // Page Down
    private const uint VkRightControl = 0xA3;

    /// <summary>
    /// "Dictation with AI cleanup" on a new install: hold Page Down, swallowed while bound. Not every keyboard has a
    /// Right Ctrl (on the Canadian CSA layout that key even reports VK_OEM_8), while Page Up and Page Down are on
    /// nearly every one, and laptops without them usually put them on Fn with the Up and Down arrows.
    /// </summary>
    public static HotkeyBinding DefaultDictation { get; } =
        new(VkNext, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Page Down");

    /// <summary>"Dictation only" on a new install: hold Page Up, swallowed while bound.</summary>
    public static HotkeyBinding DefaultDictationOnly { get; } =
        new(VkPrior, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Page Up");

    /// <summary>
    /// Hold Right Ctrl, the binding every release up to 0.4.3 shipped, which came with no dictation-only key. It is
    /// what an existing install falls back to: a stored document without a hotkey, and a session whose saved settings
    /// could not be used (<see cref="AppSettings.CreateForExistingInstall"/>). That person has been pressing Right Ctrl,
    /// so it is the key that keeps working for them; the new defaults are for new installs only
    /// (<see cref="AppSettings.CreateDefault"/>).
    /// </summary>
    public static HotkeyBinding Legacy { get; } =
        new(VkRightControl, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Ctrl");

    public bool HasModifiers => Modifiers != KeyModifiers.None;

    /// <summary>True when the binding requires two physical non-generic key identities.</summary>
    public bool IsPhysicalChord => SecondaryVirtualKey is not null;

    /// <summary>
    /// Whether <paramref name="other"/> presses the same keys the same way: everything but the stored name, which only
    /// ever described the keys and can be an old alias such as "Next".
    /// </summary>
    public bool SameKeysAndBehavior(HotkeyBinding? other) =>
        other is not null &&
        VirtualKey == other.VirtualKey &&
        SecondaryVirtualKey == other.SecondaryVirtualKey &&
        Modifiers == other.Modifiers &&
        Mode == other.Mode &&
        Suppress == other.Suppress &&
        SuppressChordMembers == other.SuppressChordMembers;
}
