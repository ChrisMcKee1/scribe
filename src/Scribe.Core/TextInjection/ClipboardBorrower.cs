using System.Security.Cryptography;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>How <see cref="ClipboardBorrower.TryBorrow"/> ended.</summary>
internal enum ClipboardBorrowStatus
{
    Borrowed,
    Busy,
    NonTextContent,
    SnapshotUnreadable,
    WriteFailed,
    ReceiptFailed,
}

/// <summary>Whether the clipboard still holds the write a <see cref="ClipboardLease"/> describes.</summary>
internal enum ClipboardCheck
{
    Ours,
    Superseded,
    Busy,
}

/// <summary>Result of <see cref="ClipboardBorrower.TryBorrow"/>.</summary>
internal readonly struct ClipboardBorrowResult
{
    internal ClipboardBorrowResult(
        ClipboardBorrowStatus status, ClipboardLease? lease, int openAttempts, ClipboardRestoreOutcome rollback)
    {
        Status = status;
        Lease = lease;
        OpenAttempts = openAttempts;
        Rollback = rollback;
    }

    public ClipboardBorrowStatus Status { get; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="ClipboardBorrowStatus.Borrowed"/>.</summary>
    public ClipboardLease? Lease { get; }

    public int OpenAttempts { get; }

    /// <summary>
    /// When writing Scribe's text or its receipt failed after the clipboard had already been emptied,
    /// whether the snapshot was put back in the same session. <see cref="ClipboardRestoreOutcome.NotApplicable"/> otherwise.
    /// </summary>
    public ClipboardRestoreOutcome Rollback { get; }
}

/// <summary>
/// What one borrow must remember to restore safely. A plain class rather than a record on purpose: it
/// holds the user's previous clipboard text, and a generated ToString would print that into any log
/// line that ever formatted a lease.
/// </summary>
internal sealed class ClipboardLease
{
    internal ClipboardLease(bool wasEmpty, string? previous, string injected, byte[] receipt, uint sequence)
    {
        WasEmpty = wasEmpty;
        Previous = previous;
        Injected = injected;
        Receipt = receipt;
        Sequence = sequence;
    }

    /// <summary>The clipboard held no formats before the borrow, so restoring means emptying it.</summary>
    public bool WasEmpty { get; }

    /// <summary>The user's text before the borrow. Never logged.</summary>
    public string? Previous { get; }

    /// <summary>The text Scribe wrote. Never logged.</summary>
    public string Injected { get; }

    /// <summary>
    /// The random receipt written beside Scribe's text. Every lease has one: a borrow whose receipt could
    /// not be written is rolled back instead of leased, because nothing else can settle a moved number.
    /// </summary>
    public byte[] Receipt { get; }

    /// <summary>
    /// A sequence number read while the clipboard was held and proven to be Scribe's, either right after
    /// the borrow's last write or after a later proof by receipt. Only a number read that way is proof.
    /// </summary>
    public uint Sequence { get; private set; }

    /// <summary>
    /// Whether the number read right after the borrow released the clipboard differed from
    /// <see cref="Sequence"/>. A diagnostic for the per-paste log line and nothing more: another
    /// application can change the clipboard between the release and that read, so the number read then
    /// never counts as proof of what the clipboard holds.
    /// </summary>
    public bool CloseMovedSequence { get; private set; }

    internal void Rebaseline(uint sequence) => Sequence = sequence;

    internal void RecordSequenceAfterClose(uint sequence) => CloseMovedSequence = sequence != Sequence;
}

/// <summary>
/// Borrows the clipboard for one paste: remember the user's text, replace it with Scribe's, and later
/// put it back, but only while the clipboard still holds exactly what Scribe wrote.
/// </summary>
/// <remarks>
/// <para>
/// Every decision that leads to a mutation is made while the clipboard is held open, because between an
/// unlocked check and the open another application can replace the content, and Scribe would then
/// overwrite the user's newer copy. That is the defect this type replaced: the restore compared the
/// sequence number before the open retry loop.
/// </para>
/// <para>
/// The receipt that identifies Scribe's write is taken inside the same open session as the write, after
/// the last SetClipboardData, so another application's later change can never be mistaken for Scribe's
/// value. It has two parts because the sequence number alone is not settled here. Microsoft documents
/// that it moves whenever the content changes or the clipboard is emptied, and that delayed rendering
/// moves it only once the data is rendered, but not whether CloseClipboard (which adds CF_LOCALE) or an
/// on-demand synthesized format (CF_TEXT made from CF_UNICODETEXT) moves it too. A number unchanged since
/// Scribe read it while holding the clipboard is proof on its own. A moved one is settled by 16 random
/// bytes in a registered format only Scribe writes: a replacement starts with EmptyClipboard, which
/// frees every registered format, so that receipt still sitting next to the exact text Scribe wrote
/// means nobody replaced the item. (A registered format, not one in the CF_PRIVATEFIRST range, whose
/// handles the system does not free.)
/// </para>
/// <para>
/// Only values read while Scribe holds the clipboard count as proof, for the paste as much as for a
/// restore. OpenClipboard keeps other applications from changing the clipboard only while it is open,
/// so a number read after CloseClipboard may already belong to another application's copy, and a
/// Ctrl+V authorized by it would paste that copy. The borrow reads the number right after its release
/// only to log whether the release moved it. Whenever the number has moved from the held value,
/// <see cref="Confirm"/> opens the clipboard and checks the receipt, which costs one more open per paste
/// on a machine where the release itself moves the number. Without a receipt a moved number could never
/// be settled, so a borrow whose receipt could not be written is never leased: it puts the snapshot back
/// in the same session and reports <see cref="ClipboardBorrowStatus.ReceiptFailed"/>, and the caller
/// types instead.
/// </para>
/// </remarks>
internal sealed class ClipboardBorrower
{
    internal const int OpenAttempts = 6;
    internal const int OpenRetryDelayMs = 15;
    internal const int ReceiptBytes = 16;
    internal const string ReceiptFormatName = "Scribe.ClipboardReceipt";

    // Scribe's long-standing heuristic for "plain text": at most four formats, which is CF_UNICODETEXT
    // plus the CF_LOCALE, CF_TEXT and CF_OEMTEXT companions a text copy typically shows. More suggests a
    // richer companion (HTML, RTF) that restoring plain text would drop. Whether CountClipboardFormats
    // includes synthesized formats is not documented, so a sparse rich copy can still fall under it.
    private const int PlainTextFormatCount = 4;

    /// <summary>
    /// Registered formats, known to Windows, that keep Scribe's writes out of Clipboard History (Win+V)
    /// and cross-device cloud sync. See
    /// <see href="https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats"/>.
    /// </summary>
    internal static readonly string[] PrivacyMarkerFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];

    // Everything Scribe adds to its own writes, discounted when judging what a restore would lose.
    private static readonly string[] OwnFormats = [.. PrivacyMarkerFormats, ReceiptFormatName];

    // A serialized DWORD zero: "no" for the granular pair, and ignored by the blanket format.
    private static readonly byte[] MarkerPayload = new byte[sizeof(uint)];

    private readonly IClipboardNative _native;
    private readonly Action<int> _sleep;

    internal ClipboardBorrower(IClipboardNative native, Action<int> sleep)
    {
        _native = native;
        _sleep = sleep;
    }

    /// <summary>
    /// Snapshots the user's text and replaces it with <paramref name="text"/> in one open session, and
    /// records the receipt of that write before letting go. A borrow whose receipt cannot be written is
    /// undone in that same session and refused.
    /// </summary>
    public ClipboardBorrowResult TryBorrow(string text)
    {
        if (!TryOpenWithRetries(out int attempts))
        {
            return new(ClipboardBorrowStatus.Busy, null, attempts, ClipboardRestoreOutcome.NotApplicable);
        }

        ClipboardBorrowResult result;
        try
        {
            result = BorrowWhileHeld(text, attempts);
        }
        finally
        {
            _native.Close();
        }

        // A diagnostic only (see the class remarks), read at once so that a later change is unlikely to
        // be counted as the release's own.
        result.Lease?.RecordSequenceAfterClose(_native.SequenceNumber);
        return result;
    }

    // Must be called while the clipboard is held open.
    private ClipboardBorrowResult BorrowWhileHeld(string text, int attempts)
    {
        // Snapshot and replacement share this session, so what is remembered for the restore is
        // exactly what gets replaced; no other application can write in between.
        if (HasNonTextContent())
        {
            return new(ClipboardBorrowStatus.NonTextContent, null, attempts, ClipboardRestoreOutcome.NotApplicable);
        }

        bool wasEmpty = _native.FormatCount == 0;
        string? previous = null;
        if (!wasEmpty && !_native.TryReadText(out previous))
        {
            return new(ClipboardBorrowStatus.SnapshotUnreadable, null, attempts, ClipboardRestoreOutcome.NotApplicable);
        }

        if (!_native.Empty())
        {
            return new(ClipboardBorrowStatus.WriteFailed, null, attempts, ClipboardRestoreOutcome.NotApplicable);
        }

        if (!_native.SetText(text))
        {
            // The user's content is already freed, so put the snapshot back before letting go.
            var rollback = WriteBack(wasEmpty, previous)
                ? ClipboardRestoreOutcome.Restored
                : ClipboardRestoreOutcome.Failed;
            return new(ClipboardBorrowStatus.WriteFailed, null, attempts, rollback);
        }

        MarkPrivate();
        if (TryWriteReceipt() is not { } receipt)
        {
            // No lease may exist without a receipt (see the class remarks), so the borrow is undone while
            // still held, exactly as a failed text write is: nobody else can have written in between.
            var rollback = _native.Empty() && WriteBack(wasEmpty, previous)
                ? ClipboardRestoreOutcome.Restored
                : ClipboardRestoreOutcome.Failed;
            return new(ClipboardBorrowStatus.ReceiptFailed, null, attempts, rollback);
        }

        // Read while still held and after Scribe's last write, so no other application's change
        // can be taken for Scribe's.
        var lease = new ClipboardLease(wasEmpty, previous, text, receipt, _native.SequenceNumber);
        return new(ClipboardBorrowStatus.Borrowed, lease, attempts, ClipboardRestoreOutcome.NotApplicable);
    }

    /// <summary>
    /// Confirms, just before Ctrl+V, that the clipboard still holds Scribe's text, so the paste cannot
    /// deliver another application's content. Nothing can close the gap between this check and the
    /// target's own read; it only makes that gap as short as possible. <paramref name="openedClipboard"/>
    /// reports whether the check had to try to open the clipboard, which is a diagnostic of how the
    /// sequence number behaves on this machine.
    /// </summary>
    public ClipboardCheck Confirm(ClipboardLease lease, out bool openedClipboard)
    {
        openedClipboard = false;

        // The number moves on every content change and every EmptyClipboard, so one unchanged since it
        // was read while held proves nothing replaced the write. Any other value is settled under the
        // lock, including one read just after a release: another application may have copied then.
        if (_native.SequenceNumber == lease.Sequence)
        {
            return ClipboardCheck.Ours;
        }

        openedClipboard = true;
        if (!TryOpenWithRetries(out _))
        {
            return ClipboardCheck.Busy;
        }

        try
        {
            if (!HoldsLeasedWrite(lease))
            {
                return ClipboardCheck.Superseded;
            }

            // Proven Scribe's while held, so the current number is a sound receipt from here on.
            lease.Rebaseline(_native.SequenceNumber);
            return ClipboardCheck.Ours;
        }
        finally
        {
            _native.Close();
        }
    }

    /// <summary>
    /// Puts the user's previous text (or emptiness) back, but only if the clipboard still holds
    /// Scribe's write once it has been acquired.
    /// </summary>
    public ClipboardRestoreOutcome Restore(ClipboardLease lease)
    {
        if (!TryOpenWithRetries(out _))
        {
            return ClipboardRestoreOutcome.Failed;
        }

        try
        {
            // Judged only now that the clipboard is held: a check made before the open can be
            // overtaken during the retries, and the restore would then overwrite the newer copy.
            if (!HoldsLeasedWrite(lease))
            {
                return ClipboardRestoreOutcome.Superseded;
            }

            return _native.Empty() && WriteBack(lease.WasEmpty, lease.Previous)
                ? ClipboardRestoreOutcome.Restored
                : ClipboardRestoreOutcome.Failed;
        }
        finally
        {
            _native.Close();
        }
    }

    /// <summary>
    /// True when the clipboard holds content a text-only restore could not put back: an image, copied
    /// files, a spreadsheet range, or (by the format-count heuristic) text carrying richer companions
    /// such as HTML or RTF, whose formatting a plain text restore would drop. Scribe's own markers and
    /// receipt never count against it. The caller types instead of clobbering such content. None of
    /// these calls require the clipboard to be open, but the borrow evaluates it while held so the answer
    /// describes exactly the content it replaces.
    /// </summary>
    internal bool HasNonTextContent()
    {
        int total = _native.FormatCount;
        if (total == 0)
        {
            return false;
        }

        if (!_native.IsFormatAvailable(CF_UNICODETEXT))
        {
            return true;
        }

        // Scribe's own markers and receipt are discounted first. Counted, they pushed Scribe's own plain
        // text past the threshold, so its clipboard content read as unrestorable and the paste path
        // refused to use it.
        return total - OwnFormatCount() > PlainTextFormatCount;
    }

    // Must be called while the clipboard is held open, because a true answer authorizes a paste or a
    // mutation.
    private bool HoldsLeasedWrite(ClipboardLease lease)
    {
        if (_native.SequenceNumber == lease.Sequence)
        {
            return true;
        }

        uint format = _native.RegisterFormat(ReceiptFormatName);
        Span<byte> found = stackalloc byte[ReceiptBytes];

        // The receipt is checked before the text so a replacement, which never carries it, is rejected
        // without reading its text: that read would make another application render a delayed format.
        return format != 0
            && _native.TryReadData(format, found)
            && found.SequenceEqual(lease.Receipt)
            && _native.TryReadText(out var current)
            && string.Equals(current, lease.Injected, StringComparison.Ordinal);
    }

    // Must be called while the clipboard is held open and just emptied.
    private bool WriteBack(bool wasEmpty, string? previous)
    {
        if (wasEmpty)
        {
            return true;
        }

        if (previous is null || !_native.SetText(previous))
        {
            return false;
        }

        MarkPrivate();
        return true;
    }

    /// <summary>
    /// Opts the item just written out of Windows Clipboard History (Win+V) and cross-device cloud
    /// clipboard sync.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scribe uses the clipboard as a transport, not as a place to leave things: it borrows it to paste a
    /// result, then puts the user's own content back. Without these markers every dictation Scribe pastes
    /// is captured by Win+V and, when the user has cross-device clipboard turned on, uploaded to Microsoft
    /// and synced to their other machines. That is a straightforward violation of what this product
    /// promises.
    /// </para>
    /// <para>
    /// Microsoft documents three registered formats for this. Both of the granular ones are written as
    /// well as the blanket one, because the blanket format is the newer mechanism and the granular pair is
    /// what older builds honour: <c>ExcludeClipboardContentFromMonitorProcessing</c> excludes the item from
    /// history AND sync and from third-party clipboard monitors, <c>CanIncludeInClipboardHistory</c> set
    /// to 0 blocks history only, and <c>CanUploadToCloudClipboard</c> set to 0 blocks sync only.
    /// </para>
    /// <para>
    /// <b>This only protects clipboard writes Scribe performs itself.</b> An annotation can only be
    /// attached by the process placing the data, so anything another application copies is captured by
    /// clipboard history and Scribe cannot prevent it.
    /// </para>
    /// <para>
    /// Best effort throughout: a failure to annotate must never fail the paste the user is waiting for.
    /// The clipboard must already be held open.
    /// </para>
    /// </remarks>
    private void MarkPrivate()
    {
        foreach (var name in PrivacyMarkerFormats)
        {
            try
            {
                uint format = _native.RegisterFormat(name);
                if (format != 0)
                {
                    _native.SetData(format, MarkerPayload);
                }
            }
            catch (Exception)
            {
                // Privacy hardening is not allowed to break the operation it is hardening.
            }
        }
    }

    // Null when the receipt cannot be written; the borrow then rolls back instead of leasing (see the
    // class remarks).
    private byte[]? TryWriteReceipt()
    {
        try
        {
            uint format = _native.RegisterFormat(ReceiptFormatName);
            if (format == 0)
            {
                return null;
            }

            byte[] receipt = RandomNumberGenerator.GetBytes(ReceiptBytes);
            return _native.SetData(format, receipt) ? receipt : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private int OwnFormatCount()
    {
        int count = 0;
        foreach (var name in OwnFormats)
        {
            uint format = _native.RegisterFormat(name);
            if (format != 0 && _native.IsFormatAvailable(format))
            {
                count++;
            }
        }

        return count;
    }

    private bool TryOpenWithRetries(out int attempts)
    {
        for (attempts = 1; ; attempts++)
        {
            if (_native.TryOpen())
            {
                return true;
            }

            if (attempts >= OpenAttempts)
            {
                return false;
            }

            // Another process routinely holds the clipboard for a moment (clipboard history, a clipboard
            // manager, the paste target itself), so one attempt is not enough and endless ones are wrong.
            _sleep(OpenRetryDelayMs);
        }
    }
}
