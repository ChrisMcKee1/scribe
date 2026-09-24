namespace Scribe.Core.Settings;

/// <summary>
/// Keeps a settings window's switch in step with changes made outside it (the tray's AI cleanup item). Each outside
/// change is stamped with a revision when it is made, and so is each change the user makes in the window, so the
/// newest change wins however late word of an older one arrives. A change the window cannot show the moment it
/// arrives, as while it records a hotkey, waits until it can, and a save never writes a value older than the newest
/// change the window has.
/// </summary>
/// <remarks>
/// <para>
/// The invariant this serves, with <see cref="Scribe.Core.Persistence.SettingsRepository"/>: the newest intent for the
/// switch wins, ordered by when the user made it, from the tray or in the window, and a save never writes over a
/// stored value the window neither showed nor changed.
/// </para>
/// <para>
/// Two orderings used to lose a choice. The window dropped an outside change that arrived mid-capture, so its next
/// save put back the value the tray had just replaced. And the tray tells the window twice, when a change is asked
/// for and again once it is stored, so word of a stored change that came after the user had set the switch the other
/// way replaced the user's newer choice.
/// </para>
/// </remarks>
public sealed class ExternalSwitchSync
{
    private static long s_lastRevision;

    // The same rules for a switch as for any setting changed from outside the window; this keeps the bool-shaped API the
    // AI cleanup switch and its tests use.
    private readonly ExternalChoiceSync<bool> _choice = new();

    /// <summary>
    /// A revision later than every one handed out before it in this process. An outside change takes one when it is
    /// made and passes the same one with every word of that change.
    /// </summary>
    public static long NextRevision() => Interlocked.Increment(ref s_lastRevision);

    /// <summary>True while an outside change is waiting to be shown.</summary>
    public bool HasWaitingChange => _choice.HasWaitingChange;

    /// <summary>
    /// The window's intent for the switch: the revision of the newest change it holds, the user's own click or an
    /// outside change it took, or zero when it holds none (just opened, or a save has stored it). A save passes it on
    /// (<see cref="Scribe.Core.Persistence.ISettingsRepository.SaveBundle"/>). Every outside change up to it is then
    /// superseded, and with zero the save keeps the stored value instead of writing a switch nobody set in the window.
    /// </summary>
    public long NewestRevision => _choice.NewestRevision;

    /// <summary>
    /// An outside change, or word of how it ended (stored, or failed and put back), with the revision the change was
    /// given when it was made. Returns false and changes nothing when the window already has a newer change, the
    /// user's own or a later outside one. Otherwise <paramref name="showNow"/> says whether the switch can show
    /// <paramref name="value"/> now; if not, it waits for <see cref="Release"/>, and a save writes it meanwhile.
    /// </summary>
    public bool TryAdopt(bool value, long revision, bool canShowNow, out bool showNow) =>
        _choice.TryAdopt(value, revision, canShowNow, out showNow);

    /// <summary>The switch can show changes again: returns the change that waited, once, or null.</summary>
    public bool? Release() => _choice.Release();

    /// <summary>
    /// The user set the switch in the window: newer than every outside change made before it, including one whose
    /// word has yet to arrive.
    /// </summary>
    public void UserChanged() => _choice.UserChanged();

    /// <summary>
    /// The window saved its whole document, this switch included, so it holds nothing newer than what is stored. Word
    /// of a change made before the save is taken again from then on: it carries the settings as stored (the tray's
    /// lane reads them again when a save lands while a change is on its way), which are the truth even when that
    /// change's write reached the database after the save.
    /// </summary>
    public void Saved() => _choice.Saved();

    /// <summary>What a save writes: an outside change still waiting to be shown, otherwise what the switch shows.</summary>
    public bool ForSave(bool shown) => _choice.ForSave(shown);
}