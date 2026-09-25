using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>What one key or mouse button event did to a hotkey capture (see <see cref="HotkeyCaptureSession"/>).</summary>
public enum HotkeyCaptureOutcome
{
    /// <summary>Nothing to show: a repeat, a release while another recorded input is still down, or an input with no code.</summary>
    Unchanged,

    /// <summary>Escape: the capture ends and the hotkey stays as it was.</summary>
    Cancelled,

    /// <summary>The left or right mouse button, which a hotkey cannot use. The click keeps its usual meaning.</summary>
    Refused,

    /// <summary>A third input: a hotkey has at most two.</summary>
    TooMany,

    /// <summary>A new input was recorded, and the capture box shows the inputs so far.</summary>
    Recorded,

    /// <summary>Every recorded input is released, so the recorded inputs are the new hotkey.</summary>
    Completed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Handled">
/// Whether the window must mark the event handled, so no control acts on it: true for every key and for the middle and
/// side mouse buttons (in WPF a side button's release left unhandled becomes a Back or Forward command), false for the
/// left and right buttons, which keep their meaning so the user can still click Cancel or anywhere else.
/// </param>
/// <param name="Text">For <see cref="HotkeyCaptureOutcome.Recorded"/>, what the capture box shows.</param>
/// <param name="Message">
/// For <see cref="HotkeyCaptureOutcome.Refused"/> and <see cref="HotkeyCaptureOutcome.TooMany"/>, why; for
/// <see cref="HotkeyCaptureOutcome.Completed"/>, a warning about the new hotkey, or null.
/// </param>
/// <param name="Binding">For <see cref="HotkeyCaptureOutcome.Completed"/>, the new hotkey.</param>
public readonly record struct HotkeyCaptureStep(
    HotkeyCaptureOutcome Outcome,
    bool Handled,
    string? Text = null,
    string? Message = null,
    HotkeyBinding? Binding = null);

/// <summary>
/// What Settings' Set does with the keys and mouse buttons pressed while it records a hotkey. The window maps each key
/// and mouse button event to its virtual-key code and shows the step this returns; every decision is made here.
/// </summary>
/// <remarks>
/// The rules the key capture always had hold for mouse buttons too: up to two inputs, recorded in the order they go
/// down, and the hotkey is set when every one of them is up again, so a key and a button (Left Ctrl, then Mouse Back)
/// make a two-input chord like two keys do. Only the middle, Back and Forward buttons can be recorded
/// (<see cref="MouseButtons.IsBindable"/>); the left and right buttons are refused and keep their meaning. A release
/// the capture never saw go down (the key that pressed Set, a button held before it) changes nothing.
/// </remarks>
public sealed class HotkeyCaptureSession
{
    private const uint VkEscape = 0x1B;

    private readonly List<uint> _recorded = new(2);
    private readonly HashSet<uint> _held = new();
    private readonly Func<uint, string?>? _layoutName;

    /// <param name="layoutName">
    /// Names a key the canonical table leaves to the current keyboard layout (see <see cref="HotkeyText.KeyName"/>).
    /// </param>
    public HotkeyCaptureSession(Func<uint, string?>? layoutName = null) => _layoutName = layoutName;

    /// <summary>What the capture box shows while it waits for the first input.</summary>
    public const string Prompt = "Press one or two keys or mouse buttons\u2026 (dictation is paused)";

    /// <summary>Why a hotkey cannot hold a third input.</summary>
    public const string TooManyMessage = "A dictation hotkey can contain up to two keys or mouse buttons.";

    /// <summary>Why the left and right mouse buttons are refused.</summary>
    public const string RefusedMessage =
        "Left and right clicks can't be a hotkey: every app needs them. Press a key, or the middle, back or forward " +
        "mouse button.";

    /// <summary>
    /// The Settings help text about mouse buttons: how to bind one, which ones, where other buttons fit, and what binding
    /// one costs in other apps (see <c>ChordStateMachine</c> for the modifier rule it states).
    /// </summary>
    public const string MouseButtonsHint =
        "Mouse buttons work too: choose Set, then press the middle, back or forward button with the pointer on this " +
        "window, on its own or after a key (for a chord such as Ctrl and Back, press the key first). Left and right " +
        "clicks can't be used. For any other button, have your mouse software send a key such as F13 to F24, then " +
        "bind that key. While Scribe runs, a bound button no longer does its usual job in other apps, so Back stops " +
        "going back in your browser; a button bound on its own still does, pressed with Ctrl, Shift, Alt, Win or the " +
        "Narrator key.";

    /// <summary>The inputs recorded so far, in the order they went down.</summary>
    public IReadOnlyList<uint> Recorded => _recorded;

    /// <summary>A key or mouse button went down, by its virtual-key code.</summary>
    public HotkeyCaptureStep Press(uint virtualKey)
    {
        if (virtualKey is MouseButtons.Left or MouseButtons.Right)
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.Refused, Handled: false, Message: RefusedMessage);
        }

        if (virtualKey == VkEscape)
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.Cancelled, Handled: true);
        }

        // A key with no code (WPF maps some to none) can never be matched: the hook sees codes 1 to 254 only.
        if (virtualKey == 0 || !_held.Add(virtualKey) || _recorded.Contains(virtualKey))
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.Unchanged, Handled: true);
        }

        if (_recorded.Count == 2)
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.TooMany, Handled: true, Message: TooManyMessage);
        }

        _recorded.Add(virtualKey);
        var names = string.Join("+", _recorded.Select(Name));
        var hint = _recorded.Count == 1 ? "  (add another key or mouse button, or release)" : "  (release to set)";
        return new HotkeyCaptureStep(HotkeyCaptureOutcome.Recorded, Handled: true, Text: names + hint);
    }

    /// <summary>A key or mouse button went up, by its virtual-key code; <paramref name="mode"/> is the row's mode.</summary>
    public HotkeyCaptureStep Release(uint virtualKey, HotkeyMode mode)
    {
        if (virtualKey is MouseButtons.Left or MouseButtons.Right)
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.Unchanged, Handled: false);
        }

        _held.Remove(virtualKey);
        if (_recorded.Count == 0 || _held.Count > 0)
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.Unchanged, Handled: true);
        }

        var binding = Build(_recorded, mode, _layoutName);
        return new HotkeyCaptureStep(
            HotkeyCaptureOutcome.Completed, Handled: true, Message: ChordOrderWarning(_recorded), Binding: binding);
    }

    /// <summary>
    /// The binding for one or two recorded inputs, in the order they went down: an exact physical binding with no
    /// modifier flags, swallowed while it drives a dictation, its name stored for older builds to show (0.4.3 and 0.4.2
    /// show the stored name; this build names every input afresh from its code).
    /// </summary>
    public static HotkeyBinding Build(IReadOnlyList<uint> inputs, HotkeyMode mode, Func<uint, string?>? layoutName = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 1 or > 2)
        {
            throw new ArgumentException("A hotkey must contain one or two inputs.", nameof(inputs));
        }

        uint? secondary = inputs.Count == 2 ? inputs[1] : null;
        var display = string.Join("+", inputs.Select(input => HotkeyText.KeyNameOrCode(input, layoutName)));
        return new HotkeyBinding(
            inputs[0],
            KeyModifiers.None,
            mode,
            Suppress: true,
            display,
            SecondaryVirtualKey: secondary,
            SuppressChordMembers: secondary is not null);
    }

    // A chord is swallowed from the input that completes it, so the first one pressed still reaches the app under the
    // pointer. For a key that costs nothing, but a mouse button pressed first still does its job there (Back goes back),
    // so the warning says which to press first, or, for two buttons, which reaches the app.
    private string? ChordOrderWarning(IReadOnlyList<uint> inputs)
    {
        if (inputs.Count != 2 || !MouseButtons.IsBindable(inputs[0]))
        {
            return null;
        }

        return MouseButtons.IsBindable(inputs[1])
            ? $"Whichever mouse button of this chord you press first still reaches the app under the pointer, so " +
              $"{Name(inputs[0])} pressed first still does its usual job there."
            : $"Press {Name(inputs[1])} before {Name(inputs[0])} when you use this chord: a mouse button pressed " +
              "first still reaches the app under the pointer.";
    }

    private string Name(uint virtualKey) => HotkeyText.KeyNameOrCode(virtualKey, _layoutName);
}
