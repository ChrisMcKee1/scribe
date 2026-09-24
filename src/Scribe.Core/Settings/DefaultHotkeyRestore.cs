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
    /// The hint above the button: what it restores, and what binding those keys costs. The hook swallows a bound key,
    /// so other apps never see it, and a binding without modifiers fires whatever else is held, so Ctrl or Shift with
    /// the key is swallowed too.
    /// </summary>
    public static string Hint
    {
        get
        {
            var dictation = HotkeyText.Describe(HotkeyBinding.DefaultDictation);
            var dictationOnly = HotkeyText.Describe(HotkeyBinding.DefaultDictationOnly);
            return $"Restores the defaults: {Defaults}. While Scribe runs, a key bound here stops reaching other apps, " +
                $"so with the defaults {dictation} and {dictationOnly} no longer page through documents, web pages or " +
                "terminals, even with Ctrl or Shift held. Pause dictation from the tray icon to use them in other apps " +
                $"for a while. Most laptops without {dictation} and {dictationOnly} have them on Fn with the Down and Up " +
                "arrows.";
        }
    }

    // "hold Page Down for dictation with AI cleanup and hold Page Up for dictation only", built from the bindings.
    private static string Defaults =>
        $"{Phrase(HotkeyBinding.DefaultDictation)} for dictation with AI cleanup and " +
        $"{Phrase(HotkeyBinding.DefaultDictationOnly)} for dictation only";

    private static string Phrase(HotkeyBinding binding) =>
        $"{HotkeyText.Verb(binding.Mode).ToLowerInvariant()} {HotkeyText.Describe(binding)}";
}
