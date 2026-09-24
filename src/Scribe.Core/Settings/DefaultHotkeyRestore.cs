using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Settings;

/// <summary>
/// Settings' "Restore default hotkeys": both triggers go back to the keys a new install gets, whatever they are now,
/// Right Ctrl included, so an install from before those defaults can take them up without binding each key by hand.
/// Like every other change on the page it only changes what the page shows; Save applies it and Cancel discards it.
/// </summary>
public static class DefaultHotkeyRestore
{
    /// <param name="Dictation">What the "Dictation with AI cleanup" row shows from now on.</param>
    /// <param name="DictationOnly">What the "Dictation only" row shows from now on.</param>
    /// <param name="Changed">False when the page already showed exactly these keys, pressed the same way.</param>
    /// <param name="Message">What to tell the user, on screen and through the screen reader.</param>
    public readonly record struct Result(HotkeyBinding Dictation, HotkeyBinding DictationOnly, bool Changed, string Message);

    /// <param name="shownDictation">The "Dictation with AI cleanup" binding as the page shows it, mode included.</param>
    /// <param name="shownDictationOnly">The "Dictation only" binding as the page shows it, or null when it is not set.</param>
    public static Result Restore(HotkeyBinding shownDictation, HotkeyBinding? shownDictationOnly)
    {
        ArgumentNullException.ThrowIfNull(shownDictation);

        var dictation = HotkeyBinding.DefaultDictation;
        var dictationOnly = HotkeyBinding.DefaultDictationOnly;
        var changed = !dictation.SameKeysAndBehavior(shownDictation) || !dictationOnly.SameKeysAndBehavior(shownDictationOnly);
        var message = changed
            ? $"Hotkeys set to the defaults: {Defaults}. Save to apply them."
            : $"Your hotkeys already match the defaults: {Defaults}.";
        return new Result(dictation, dictationOnly, changed, message);
    }

    /// <summary>
    /// The hint above the button: what it restores, and what binding those keys costs. The hook swallows a bound key
    /// pressed on its own, so other apps never see it, while the same key pressed with a modifier the binding does not
    /// name, or with Narrator's key, reaches the app as before (see <c>ChordStateMachine</c>). A presentation remote is
    /// a keyboard whose buttons send Page Down and Page Up, so it stops changing slides. The hook matches the virtual-key
    /// code alone, so the numeric keypad's Page Down and Page Up (its 3 and 9 with Num Lock off) are the same keys to it.
    /// </summary>
    public static string Hint
    {
        get
        {
            var dictation = HotkeyText.Describe(HotkeyBinding.DefaultDictation);
            var dictationOnly = HotkeyText.Describe(HotkeyBinding.DefaultDictationOnly);
            return $"Restores the defaults: {Defaults}. While Scribe runs, {dictation} and {dictationOnly} pressed on " +
                "their own no longer reach other apps: they stop paging through documents, web pages and terminals, and " +
                "a presentation remote stops changing slides. With Ctrl, Shift, Alt, Win or the Narrator key held they " +
                "work in other apps as before. Pause dictation from the tray icon to use them for a while, or choose " +
                $"other keys here if you present. Most laptops without {dictation} and {dictationOnly} have them on Fn " +
                $"with the Down and Up arrows, and the keypad's {dictation} and {dictationOnly} with Num Lock off also work.";
        }
    }

    // "hold Page Down for dictation with AI cleanup and hold Page Up for dictation only", built from the bindings.
    private static string Defaults =>
        $"{Phrase(HotkeyBinding.DefaultDictation)} for dictation with AI cleanup and " +
        $"{Phrase(HotkeyBinding.DefaultDictationOnly)} for dictation only";

    private static string Phrase(HotkeyBinding binding) =>
        $"{HotkeyText.Verb(binding.Mode).ToLowerInvariant()} {HotkeyText.Describe(binding)}";
}
