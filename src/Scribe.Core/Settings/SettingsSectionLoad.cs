namespace Scribe.Core.Settings;

public enum SettingsSectionState
{
    /// <summary>Nothing has been read yet. The section's editors must stay off.</summary>
    Unloaded,

    /// <summary>A read is running in the background.</summary>
    Loading,

    /// <summary>The rows on screen came from a completed read and can be edited and saved.</summary>
    Loaded,

    /// <summary>The last read failed. Treated like <see cref="Unloaded"/> by Save.</summary>
    Failed,
}

/// <summary>
/// Load state and saved-state snapshot for one Settings section whose rows are read off the UI
/// thread.
/// </summary>
/// <remarks>
/// <para>
/// Save writes a section only when its rows differ from the snapshot of what storage holds, and
/// <c>SettingsRepository.SaveBundle</c> treats a null section as unchanged. Before this type the
/// snapshot started as the signature of an empty grid, which was harmless only while the rows were
/// read synchronously in the constructor. Once they arrive later, an empty grid could be taken for
/// "the user deleted everything". Here a section that has not finished loading never has changes,
/// so Save leaves it untouched.
/// </para>
/// <para>
/// Every read gets a ticket, and only the newest ticket may publish. A write that goes around the
/// grid while a read is running (the tray's quick add, for example) invalidates it, so a stale read
/// cannot replace what storage now holds. A loaded section with unsaved edits is never reloaded.
/// </para>
/// <para>
/// Not thread-safe by design: the Settings window drives it from its dispatcher thread, and the
/// background reads never touch it.
/// </para>
/// </remarks>
public sealed class SettingsSectionLoad
{
    private long _ticket;

    public SettingsSectionState State { get; private set; } = SettingsSectionState.Unloaded;

    public bool IsLoaded => State == SettingsSectionState.Loaded;

    public bool IsClosed { get; private set; }

    /// <summary>Signature of what storage held when the rows were published, or null before that.</summary>
    public string? Snapshot { get; private set; }

    /// <summary>
    /// Starts a read. Returns false when the owner has closed, or when the section is loaded and
    /// <paramref name="currentSignature"/> shows unsaved edits that a reload would replace. Pass null
    /// for a read-only section, which can always be refreshed.
    /// </summary>
    public bool TryBegin(string? currentSignature, out long ticket)
    {
        ticket = 0;
        if (IsClosed || (currentSignature is not null && HasChanges(currentSignature)))
        {
            return false;
        }

        ticket = ++_ticket;
        State = SettingsSectionState.Loading;
        return true;
    }

    /// <summary>
    /// Whether a finished read may still be shown: it is the newest read, nothing has invalidated
    /// it, and the owner is open.
    /// </summary>
    public bool CanPublish(long ticket) =>
        !IsClosed && ticket == _ticket && State == SettingsSectionState.Loading;

    /// <summary>
    /// Records that the rows now show the read identified by <paramref name="ticket"/>, whose
    /// signature is <paramref name="snapshot"/>. Returns false, changing nothing, for a stale read.
    /// </summary>
    public bool Publish(long ticket, string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!CanPublish(ticket))
        {
            return false;
        }

        Snapshot = snapshot;
        State = SettingsSectionState.Loaded;
        return true;
    }

    /// <summary>Records a failed read. Returns false, changing nothing, for a stale read.</summary>
    public bool Fail(long ticket)
    {
        if (!CanPublish(ticket))
        {
            return false;
        }

        State = SettingsSectionState.Failed;
        return true;
    }

    /// <summary>
    /// Stored data changed without going through the rows. Returns true when the caller should start
    /// a fresh read: no published read is known to include the change, because the section has not
    /// loaded yet or a refresh is still running, and a read already under way may predate it. A
    /// loaded section with no read running returns false, because its rows are the source of truth
    /// and the caller merges the change into them.
    /// </summary>
    public bool Invalidate()
    {
        if (IsClosed || State == SettingsSectionState.Loaded)
        {
            return false;
        }

        _ticket++;
        State = SettingsSectionState.Unloaded;
        return true;
    }

    /// <summary>
    /// Whether the rows differ from storage. Always false before a read has been published: an
    /// empty grid that has not loaded is not an edit.
    /// </summary>
    public bool HasChanges(string currentSignature)
    {
        ArgumentNullException.ThrowIfNull(currentSignature);
        return State == SettingsSectionState.Loaded &&
            !string.Equals(currentSignature, Snapshot, StringComparison.Ordinal);
    }

    /// <summary>Records that storage now holds <paramref name="signature"/>. Ignored before a load.</summary>
    public void MarkSaved(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (State == SettingsSectionState.Loaded)
        {
            Snapshot = signature;
        }
    }

    /// <summary>The owner closed: no read may publish or start after this.</summary>
    public void Close()
    {
        IsClosed = true;
        _ticket++;
    }
}
