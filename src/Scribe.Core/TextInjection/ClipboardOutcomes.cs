namespace Scribe.Core.TextInjection;

/// <summary>
/// How the clipboard paste path ended. Reported separately from <see cref="ClipboardRestoreOutcome"/>
/// because the two fail independently: a paste can be delivered while the restore fails.
/// </summary>
public enum PasteDelivery
{
    /// <summary>The clipboard paste path was not used.</summary>
    NotUsed = 0,

    /// <summary>
    /// SendInput inserted Ctrl+V at least through the V key-down, so the target was asked to paste.
    /// This proves only that the events entered the input stream, not that the target read the
    /// clipboard or rendered the text.
    /// </summary>
    ChordInserted,

    /// <summary>SendInput stopped before the V key-down, so no paste can have fired.</summary>
    ChordIncomplete,

    /// <summary>The clipboard could not be opened within the bounded retries. Nothing was changed.</summary>
    ClipboardBusy,

    /// <summary>
    /// The clipboard held content Scribe cannot put back (an image, files, a rich multi-format copy),
    /// so it was left untouched.
    /// </summary>
    NonTextContent,

    /// <summary>The clipboard reported text that could not be read, so it was left untouched.</summary>
    SnapshotUnreadable,

    /// <summary>Scribe could not place its text on the clipboard.</summary>
    WriteFailed,

    /// <summary>
    /// Scribe placed its text but could not write the receipt that later proves the clipboard still
    /// holds it, so it undid the borrow before releasing the clipboard and sent no paste. Whether the
    /// previous content came back is reported as the restore outcome.
    /// </summary>
    ReceiptFailed,

    /// <summary>Another application replaced Scribe's text before Ctrl+V, so no paste was sent.</summary>
    Superseded,

    /// <summary>
    /// Scribe's text could not be re-confirmed before Ctrl+V because the clipboard stayed busy, so no
    /// paste was sent rather than risk pasting another application's content.
    /// </summary>
    Unconfirmed,

    /// <summary>The focused window changed before Ctrl+V, so no paste was sent.</summary>
    FocusChanged,
}

/// <summary>What happened to the clipboard content Scribe borrowed for a paste.</summary>
public enum ClipboardRestoreOutcome
{
    /// <summary>Nothing was borrowed, so there was nothing to restore.</summary>
    NotApplicable = 0,

    /// <summary>The previous clipboard text, or its emptiness, was put back.</summary>
    Restored,

    /// <summary>
    /// Another application changed the clipboard after Scribe's write, so its newer content was left
    /// untouched and the previous text was not put back.
    /// </summary>
    Superseded,

    /// <summary>The restore could not complete: the clipboard stayed busy, or writing the previous text failed.</summary>
    Failed,
}
