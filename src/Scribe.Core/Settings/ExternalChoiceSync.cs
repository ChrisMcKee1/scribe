namespace Scribe.Core.Settings;

/// <summary>
/// Keeps one setting in a settings window in step with changes made to it outside the window, such as from the tray.
/// Each outside change is stamped with a revision when it is made (<see cref="ExternalSwitchSync.NextRevision"/>), and so
/// is each change the user makes in the window, so the newest change wins however late word of an older one arrives. A
/// change the window cannot show the moment it arrives waits until it can, and a save never writes a value older than
/// the newest change the window has. <see cref="ExternalSwitchSync"/> is this for the AI cleanup switch; the microphone
/// uses it directly.
/// </summary>
/// <remarks>
/// The invariant this serves, with <see cref="Scribe.Core.Persistence.SettingsRepository"/>: the newest intent for the
/// setting wins, ordered by when the user made it, from outside or in the window, and a save never writes over a stored
/// value the window neither showed nor changed.
/// </remarks>
public sealed class ExternalChoiceSync<T>
    where T : struct
{
    private T? _waiting;
    private long _newest;

    /// <summary>True while an outside change is waiting to be shown.</summary>
    public bool HasWaitingChange => _waiting.HasValue;

    /// <summary>
    /// The window's intent for the setting: the revision of the newest change it holds, the user's own or an outside one
    /// it took, or zero when it holds none (just opened, or a save has stored it). A save passes it on in its
    /// <see cref="Scribe.Core.Persistence.ExternalIntents"/>. Every outside change up to it is then superseded, and with
    /// zero the save keeps the stored value instead of writing one nobody set in the window.
    /// </summary>
    public long NewestRevision => _newest;

    /// <summary>
    /// An outside change, or word of how it ended (stored, or failed and put back), with the revision the change was
    /// given when it was made. Returns false and changes nothing when the window already has a newer change, the user's
    /// own or a later outside one. Otherwise <paramref name="showNow"/> says whether the window can show
    /// <paramref name="value"/> now; if not, it waits for <see cref="Release"/>, and a save writes it meanwhile.
    /// </summary>
    public bool TryAdopt(T value, long revision, bool canShowNow, out bool showNow)
    {
        if (revision < _newest)
        {
            showNow = false;
            return false;
        }

        _newest = revision;
        _waiting = canShowNow ? null : value;
        showNow = canShowNow;
        return true;
    }

    /// <summary>The window can show changes again: returns the change that waited, once, or null.</summary>
    public T? Release()
    {
        var waiting = _waiting;
        _waiting = null;
        return waiting;
    }

    /// <summary>
    /// The user changed the setting in the window: newer than every outside change made before it, including one whose
    /// word has yet to arrive.
    /// </summary>
    public void UserChanged()
    {
        _waiting = null;
        _newest = ExternalSwitchSync.NextRevision();
    }

    /// <summary>
    /// The window saved its whole document, this setting included, so it holds nothing newer than what is stored. Word of
    /// a change made before the save is taken again from then on: it carries the settings as stored, which are the truth
    /// even when that change's write reached the database after the save.
    /// </summary>
    public void Saved() => _newest = 0;

    /// <summary>What a save writes: an outside change still waiting to be shown, otherwise what the window shows.</summary>
    public T ForSave(T shown) => _waiting ?? shown;
}
