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
    /// <param name="SaveNeeded">False when the saved settings already hold these keys, so Save has nothing to apply.</param>
    /// <param name="Message">What to tell the user, on screen and through the screen reader.</param>
    public readonly record struct Result(
        HotkeyBinding Dictation, HotkeyBinding DictationOnly, bool Changed, bool SaveNeeded, string Message);

    /// <param name="shownDictation">The "Dictation with AI cleanup" binding as the page shows it, mode included.</param>
    /// <param name="shownDictationOnly">The "Dictation only" binding as the page shows it, or null when it is not set.</param>
    /// <param name="savedDictation">The "Dictation with AI cleanup" binding the saved settings hold.</param>
    /// <param name="savedDictationOnly">The saved "Dictation only" binding, or null when none is saved.</param>
    /// <remarks>
    /// The message is judged against both, because a second press, or a double click, finds the page already showing
    /// the defaults: it must still say that Save is what applies them when the saved settings hold other keys, rather
    /// than replace the first notice with one that reads as if nothing were left to do.
    /// </remarks>
    public static Result Restore(
        HotkeyBinding shownDictation,
        HotkeyBinding? shownDictationOnly,
        HotkeyBinding savedDictation,
        HotkeyBinding? savedDictationOnly)
    {
        ArgumentNullException.ThrowIfNull(shownDictation);
        ArgumentNullException.ThrowIfNull(savedDictation);

        var dictation = HotkeyBinding.DefaultDictation;
        var dictationOnly = HotkeyBinding.DefaultDictationOnly;
        var changed = !dictation.SameKeysAndBehavior(shownDictation) || !dictationOnly.SameKeysAndBehavior(shownDictationOnly);
        var saveNeeded = !dictation.SameKeysAndBehavior(savedDictation) || !dictationOnly.SameKeysAndBehavior(savedDictationOnly);
        var message = (changed, saveNeeded) switch
        {
            (true, true) => $"Hotkeys set to the defaults: {Defaults}. Save to apply them.",
            (true, false) => $"Hotkeys set back to the defaults: {Defaults}. They are already saved.",
            (false, true) => $"The hotkeys already show the defaults: {Defaults}. Save to apply them.",
            (false, false) => $"Your hotkeys already match the defaults: {Defaults}.",
        };
        return new Result(dictation, dictationOnly, changed, saveNeeded, message);
    }

    /// <summary>
    /// The hint above the button: what it restores, and what binding those keys costs. The hook swallows Page Up and
    /// Page Down pressed on their own, so other apps never see them, while either pressed with a modifier or a Narrator
    /// key reaches the app as before (see <c>ChordStateMachine</c>, which judges only a bare Page Up or Page Down
    /// binding). A presentation remote is a keyboard whose buttons send Page Down and Page Up, so it stops changing
    /// slides. The hook matches the virtual-key code alone, so the numeric keypad's Page Down and Page Up (its 3 and 9
    /// with Num Lock off) are the same keys to it.
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
