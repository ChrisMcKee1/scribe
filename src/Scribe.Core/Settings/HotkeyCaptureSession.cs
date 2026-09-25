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

    /// <summary>
    /// An input the hotkey cannot hold: a third, unless every input but one is a Ctrl, Alt or Shift key (see
    /// <see cref="HotkeyCaptureSession"/>).
    /// </summary>
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
/// make a two-input chord like two keys do. Beyond two, only a key or button held with modifiers is recorded: Ctrl,
/// Alt and Shift keys and at most one other input, which is what a mouse's own software sends for an extra button set
/// to a shortcut such as Ctrl+Shift+F13; that becomes the one input with the modifiers as flags, either side of each
/// (<see cref="Build"/>). Only the input that completes a shortcut is kept from Windows and the app, which is its key
/// when the modifiers go down first, as a mouse's software and a person send one; recorded the other way round, the
/// capture warns (<see cref="ChordOrderWarning"/>). A Windows key is never one of more than two inputs: Windows would see
/// it pressed and released with nothing between, which opens Start. Two inputs with a Windows
/// key stay the chord they always were, whose Windows key is kept from Windows from its first press (see
/// <c>ChordStateMachine</c>). Only the middle, Back and Forward buttons can be recorded
/// (<see cref="MouseButtons.IsBindable"/>); the left and right buttons are refused and keep their meaning. A release
/// the capture never saw go down (the key that pressed Set, a button held before it) changes nothing.
/// </remarks>
public sealed class HotkeyCaptureSession
{
    private const uint VkEscape = 0x1B;

    // Three modifier kinds and one other input, with room for both sides of one kind: more than any real shortcut holds.
    private const int MaxInputs = 5;

    private readonly List<uint> _recorded = new(2);
    private readonly HashSet<uint> _held = new();
    private readonly Func<uint, string?>? _layoutName;

    /// <param name="layoutName">
    /// Names a key the canonical table leaves to the current keyboard layout (see <see cref="HotkeyText.KeyName"/>).
    /// </param>
    public HotkeyCaptureSession(Func<uint, string?>? layoutName = null) => _layoutName = layoutName;

    /// <summary>What the capture box shows while it waits for the first input.</summary>
    public const string Prompt = "Press one or two keys or mouse buttons\u2026 (dictation is paused)";

    /// <summary>Why a hotkey cannot hold another input.</summary>
    public const string TooManyMessage =
        "A dictation hotkey is one or two keys or mouse buttons, or one key or button with Ctrl, Alt or Shift.";

    /// <summary>
    /// The warning for a shortcut of Shift with Ctrl or Alt: only the input that completes it is kept from Windows, which
    /// so sees the modifiers pressed and released with nothing between, the gesture that switches the keyboard language
    /// or layout.
    /// </summary>
    public const string LayoutSwitchWarning =
        "Windows still sees this hotkey's modifier keys, so they can switch your keyboard language or layout " +
        "(Alt+Shift or Ctrl+Shift) if you use more than one. A key such as F13 on its own avoids that.";

    /// <summary>Why the left and right mouse buttons are refused.</summary>
    public const string RefusedMessage =
        "Left and right clicks can't be a hotkey: every app needs them. Press a key, or the middle, back or forward " +
        "mouse button.";

    /// <summary>
    /// The Settings help text about mouse buttons: which ones bind directly, how to bind any other, and what binding one
    /// costs in other apps (see <c>ChordStateMachine</c> for the modifier rule it states). Windows itself delivers only
    /// five mouse buttons to apps, the left, right, middle, Back and Forward, so a mouse's other buttons reach Scribe
    /// only as the keys its software or firmware sends for them. The promise is kept exact: nothing Microsoft documents
    /// says whether a press a low-level hook swallows still reaches an app reading Raw Input, and it was not measured,
    /// so the text says a game that reads the mouse directly may still see it; and while Scribe's hooks are not seeing
    /// the mouse (after Windows removed the mouse hook for a missed deadline, until the next successful renewal, or while
    /// both hooks are reinstalled after Windows removed the keyboard hook), a click made then reaches the app, and so
    /// does the release of one held across that time, which the text says in one plain clause.
    /// </summary>
    public const string MouseButtonsHint =
        "Middle, Back and Forward mouse buttons bind directly: choose Set, then press the button with the pointer on this " +
        "window, on its own or after a key (for a chord such as Ctrl and Back, press the key first). For other mouse " +
        "buttons, set the button to a key such as F13 in your mouse's software, then choose Set and press it here. Left " +
        "and right clicks can't be used. While Scribe runs, a bound button no longer does its usual job in other apps, so " +
        "Back stops going back in your browser, with two exceptions: a game that reads the mouse directly may still see " +
        "it, and if Windows briefly stops passing input to Scribe, a click made or held while that lasts gets through. " +
        "Pressed with Ctrl, Shift, Alt, Win or the Narrator key, a button bound on its own still does its usual job.";

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

        if (!CanRecord(virtualKey))
        {
            return new HotkeyCaptureStep(HotkeyCaptureOutcome.TooMany, Handled: true, Message: TooManyMessage);
        }

        _recorded.Add(virtualKey);
        var names = string.Join("+", _recorded.Select(Name));
        var hint = _recorded.Count == 1
            ? "  (add another key or mouse button, or release)"
            : _recorded.All(IsModifier)
                ? "  (add a key or mouse button, or release)"
                : "  (release to set)";
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
            HotkeyCaptureOutcome.Completed,
            Handled: true,
            Message: ChordOrderWarning(_recorded) ?? LayoutSwitchRisk(binding),
            Binding: binding);
    }

    /// <summary>
    /// The binding for the recorded inputs, in the order they went down, swallowed while it drives a dictation, its name
    /// stored for older builds to show (0.4.3 and 0.4.2 show the stored name; this build names every input afresh from
    /// its code). One or two inputs make an exact physical binding with no modifier flags, as every build has captured
    /// them. Three or more are Ctrl, Alt and Shift keys and at most one other input, never a Windows key (the capture
    /// allows nothing else): that input, or the last modifier pressed if there is none, held with the others as modifier
    /// flags, which the hook matches on either side of each (<c>ChordStateMachine</c>), as it matches a shortcut a mouse's
    /// software sends whichever side's modifier code it uses.
    /// </summary>
    public static HotkeyBinding Build(IReadOnlyList<uint> inputs, HotkeyMode mode, Func<uint, string?>? layoutName = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 1 or > MaxInputs)
        {
            throw new ArgumentException($"A hotkey must contain one to {MaxInputs} inputs.", nameof(inputs));
        }

        if (inputs.Count <= 2)
        {
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

        if (inputs.Any(IsWindowsKey) || inputs.Count(input => !IsModifier(input)) > 1)
        {
            throw new ArgumentException(
                "More than two inputs must be Ctrl, Alt or Shift keys and at most one other input, never a Windows key.",
                nameof(inputs));
        }

        var key = inputs.Where(input => !IsModifier(input)).DefaultIfEmpty(inputs[^1]).First();
        var modifiers = KeyModifiers.None;
        foreach (var input in inputs)
        {
            if (input != key)
            {
                modifiers |= ModifierOf(input);
            }
        }

        var binding = new HotkeyBinding(key, modifiers, mode, Suppress: true);
        return binding with { DisplayName = HotkeyText.Describe(binding, layoutName) };
    }

    // Two inputs of any kind, as the capture always allowed; beyond that Ctrl, Alt and Shift keys and at most one other
    // key or mouse button, never a Windows key, up to MaxInputs.
    private bool CanRecord(uint virtualKey)
    {
        if (_recorded.Count < 2)
        {
            return true;
        }

        if (_recorded.Count >= MaxInputs || IsWindowsKey(virtualKey))
        {
            return false;
        }

        var others = IsModifier(virtualKey) ? 0 : 1;
        foreach (var input in _recorded)
        {
            if (IsWindowsKey(input))
            {
                return false;
            }

            if (!IsModifier(input))
            {
                others++;
            }
        }

        return others <= 1;
    }

    private static bool IsModifier(uint virtualKey) => ModifierOf(virtualKey) != KeyModifiers.None;

    private static bool IsWindowsKey(uint virtualKey) => virtualKey is 0x5B or 0x5C;

    // The modifier flag a Ctrl, Alt or Shift key stands for, either side or generic, or None for any other input. A
    // Windows key is not one here: it is never part of a shortcut of more than two inputs (see the class remarks).
    private static KeyModifiers ModifierOf(uint virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => KeyModifiers.Control,
        0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt,
        _ => KeyModifiers.None,
    };

    // Only the input that completes a shortcut with modifier flags is kept from Windows, so Windows sees the modifiers
    // pressed before it pressed and released with nothing between: for Shift with Ctrl or Alt, the gesture that switches
    // the keyboard language or layout. The key counts when it is a modifier itself, since the modifiers can be pressed in
    // any order.
    private static string? LayoutSwitchRisk(HotkeyBinding binding)
    {
        var held = binding.Modifiers | ModifierOf(binding.VirtualKey);
        return binding.Modifiers != KeyModifiers.None &&
               (held & KeyModifiers.Shift) != 0 &&
               (held & (KeyModifiers.Control | KeyModifiers.Alt)) != 0
            ? LayoutSwitchWarning
            : null;
    }

    // A chord or shortcut is swallowed from the input that completes it, so one pressed before that still reaches the app.
    // For a chord's first key that costs little, as it always has, but a mouse button pressed early still does its job
    // there (Back goes back), and so does a shortcut's key pressed before its modifiers (a media key still plays), so the
    // warning says which to press last, or, for two buttons, which reaches the app.
    private string? ChordOrderWarning(IReadOnlyList<uint> inputs)
    {
        if (inputs.Count > 2)
        {
            // The one input that is not a modifier, unless the modifiers alone were recorded. Only the input that
            // completes a shortcut is kept from the app, so this one must go down last.
            var key = inputs.FirstOrDefault(input => !IsModifier(input));
            if (key == 0 || key == inputs[^1])
            {
                return null;
            }

            return MouseButtons.IsBindable(key)
                ? $"Press {Name(key)} last when you use this hotkey: a mouse button pressed before the keys held with " +
                  "it still reaches the app under the pointer."
                : $"Press {Name(key)} last when you use this hotkey: pressed before the keys held with it, it still " +
                  "reaches the app you're using.";
        }

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
