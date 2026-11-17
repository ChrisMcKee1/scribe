using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Settings;

/// <summary>
/// The settings document one Settings Save stores, frozen when the Save's request is built, together with the intents the
/// window held then. The window's editing document keeps changing while the Save waits for its word pack journal to be
/// prepared: a tray change is adopted into it at once, before its own write has stored it, and put back out when that
/// write fails. A Save that stored the editing document when its commit finally ran stored whatever the document held
/// then, under the intents it froze earlier: a newer tray choice that was never stored (the tray's own failure could not
/// undo it), or a rolled back one in place of the choice the Save captured. The Save stores this copy, takes its baseline
/// from it, and hands the editing document only what the store kept for a setting the window holds nothing newer for.
/// </summary>
public sealed class SaveCandidate
{
    private SaveCandidate(AppSettings document, ExternalIntents intents)
    {
        Document = document;
        Intents = intents;
    }

    /// <summary>
    /// The document the Save stores: a copy of the editing document as it was when the Save froze it. The settings store
    /// takes into it the values it kept for the settings the tray can change, and the stored enabled-library list, so once
    /// the Save has committed it is the document that is stored, and the Save's baseline.
    /// </summary>
    public AppSettings Document { get; }

    /// <summary>The window's newest intent for each setting the tray can change, when the Save froze its document.</summary>
    public ExternalIntents Intents { get; }

    /// <summary>Freezes a copy of <paramref name="editing"/> with the intents the Save carries.</summary>
    public static SaveCandidate Freeze(AppSettings editing, ExternalIntents intents)
    {
        ArgumentNullException.ThrowIfNull(editing);
        return new SaveCandidate(editing.Clone(), intents);
    }

    /// <summary>
    /// Whether <paramref name="propertyName"/> names a setting the tray can change while a Settings window is open
    /// (<see cref="ExternalSetting"/>): the AI cleanup switch, and the microphone's ID and name. The window's editing
    /// document holds the newest choice for each, which a Save's preflight, captured earlier, must never put back.
    /// </summary>
    public static bool IsExternal(string propertyName) =>
        propertyName is nameof(AppSettings.EnableAiCleanup) or nameof(AppSettings.InputDeviceId) or nameof(AppSettings.InputDeviceName);

    /// <summary>
    /// Freezes the document a Save stores from what its preflight captured, which is what was validated and submitted.
    /// The preflight took the AI cleanup switch and the microphone together with the intents it carries, and the Save
    /// stores them under exactly those intents, so they come from <paramref name="preflight"/> and not from
    /// <paramref name="editing"/>: by the time the Save runs, the window's editing document may already hold a newer tray
    /// choice that the tray has not stored, and storing it under an older intent would keep it even if the tray's write
    /// then fails. Every other field is the editing document's, which the Save has already made the preflight's.
    /// </summary>
    public static SaveCandidate FreezePreflight(AppSettings preflight, AppSettings editing, ExternalIntents intents)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(editing);
        var document = editing.Clone();
        document.EnableAiCleanup = preflight.EnableAiCleanup;
        document.InputDeviceId = preflight.InputDeviceId;
        document.InputDeviceName = preflight.InputDeviceName;
        return new SaveCandidate(document, intents);
    }

    /// <summary>
    /// After the Save committed: every change up to the intents it carried is stored, and the editing document takes what
    /// the store kept for a setting, the stored value of a tray change that committed before word of it reached the window,
    /// unless the window now holds a newer intent for it. A newer intent is a tray change adopted while the Save waited, or
    /// the user's own click, and the editing document already holds its value: it is not the Save's to replace, and its own
    /// write, or its own failure, settles it.
    /// </summary>
    public void Settle(AppSettings editing, ExternalSwitchSync aiCleanup, ExternalChoiceSync<MicrophoneSelection> microphone)
    {
        ArgumentNullException.ThrowIfNull(editing);
        ArgumentNullException.ThrowIfNull(aiCleanup);
        ArgumentNullException.ThrowIfNull(microphone);

        aiCleanup.SavedThrough(Intents.AiCleanup);
        microphone.SavedThrough(Intents.Microphone);
        if (aiCleanup.NewestRevision == 0)
        {
            editing.EnableAiCleanup = Document.EnableAiCleanup;
        }

        if (microphone.NewestRevision == 0)
        {
            MicrophoneSelection.From(Document).ApplyTo(editing);
        }
    }
}
