using System.Runtime.InteropServices;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Minimal Win32 clipboard access for Unicode text (CF_UNICODETEXT). Preserving non-text
/// formats (images, files) is intentionally out of scope for v1; only text is round-tripped.
/// All methods must be called on an STA thread that owns a message queue.
/// </summary>
internal static class Win32Clipboard
{
    private const int OpenRetries = 6;
    private const int OpenRetryDelayMs = 15;

    /// <summary>
    /// True when the clipboard holds content that is NOT representable as text: an image, copied
    /// files, a spreadsheet range. Text-bearing content reports false even when rich companions
    /// (HTML/RTF) accompany it, because the text round-trip preserves what matters. Non-text
    /// content cannot be saved and restored by this class, so callers should avoid clobbering it.
    /// Neither Win32 call requires opening the clipboard, so this never contends for the lock.
    /// </summary>
    public static bool HasNonTextContent()
    {
        var total = CountClipboardFormats();
        if (total == 0)
        {
            return false;
        }

        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            return true;
        }

        // Discount Scribe's own privacy markers before applying the companion-count heuristic.
        // MarkPrivate adds three registered formats to every write this class performs, which on its
        // own pushed a plain text item from 1 format to 4 and a rich one past the threshold, so
        // Scribe's own clipboard content started reporting as unrestorable non-text and the paste
        // path refused to use it.
        return total - PrivateMarkerCount() > 4;
    }

    /// <summary>How many of <see cref="MarkPrivate"/>'s markers are currently on the clipboard.</summary>
    private static int PrivateMarkerCount()
    {
        var count = 0;
        foreach (var name in PrivateMarkerFormats)
        {
            var format = RegisterClipboardFormat(name);
            if (format != 0 && IsClipboardFormatAvailable(format))
            {
                count++;
            }
        }

        return count;
    }

    private static readonly string[] PrivateMarkerFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];

    /// <summary>
    /// True when the current clipboard contents can be saved and put back afterwards, so a caller
    /// may safely borrow the clipboard.
    /// </summary>
    /// <remarks>
    /// Deliberately a different question from <see cref="HasNonTextContent"/>, and deliberately not
    /// its inverse.
    /// <para>
    /// <see cref="HasNonTextContent"/> asks "would borrowing the clipboard lose anything at all",
    /// and answers yes for rich text, because restoring plain text drops the HTML and RTF companions.
    /// That is the right question for <see cref="TextInjector"/>, which has somewhere to go when the
    /// answer is yes: it falls back to typing the text instead of pasting it.
    /// </para>
    /// <para>
    /// A selection read has no such fallback, so reusing that guard meant any ordinary copy from a
    /// browser, Word, Teams or an editor disabled the whole feature. Those put CF_UNICODETEXT,
    /// CF_TEXT, CF_OEMTEXT, CF_LOCALE and HTML Format on the clipboard, which is five formats and
    /// trips the "more than four" heuristic even though the text round-trips perfectly.
    /// </para>
    /// <para>
    /// The question that actually matters before borrowing is narrower: is the user's content
    /// recoverable? It is when the clipboard is empty, and it is when the clipboard holds text. It
    /// is not when the clipboard holds an image, copied files or a spreadsheet range with no text
    /// representation, because this class cannot reproduce those.
    /// </para>
    /// </remarks>
    public static bool CanBorrow() =>
        CountClipboardFormats() == 0 || IsClipboardFormatAvailable(CF_UNICODETEXT);

    public static int FormatCount => CountClipboardFormats();

    public static uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>Returns the current clipboard text, or null if the clipboard holds no text.</summary>
    public static string? TryGetText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            return null;
        }

        if (!TryOpen())
        {
            return null;
        }

        try
        {
            nint handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == 0)
            {
                return null;
            }

            nint pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Replaces the clipboard contents with <paramref name="text"/>.</summary>
    public static bool SetText(string text)
    {
        if (!TryOpen())
        {
            return false;
        }

        try
        {
            EmptyClipboard();

            // Null-terminated UTF-16; GMEM_MOVEABLE memory is required for clipboard handles.
            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
            nint global = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (global == 0)
            {
                return false;
            }

            nint target = GlobalLock(global);
            if (target == 0)
            {
                GlobalFree(global);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(global);
            }

            if (SetClipboardData(CF_UNICODETEXT, global) == 0)
            {
                // Ownership only transfers to the system on success; free on failure.
                GlobalFree(global);
                return false;
            }

            MarkPrivate();
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Opts this clipboard item out of Windows Clipboard History (Win+V) and out of cross-device
    /// cloud clipboard sync.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scribe uses the clipboard as a transport, not as a place to leave things: it borrows it to
    /// paste a result, then puts the user's own content back. Without these markers every dictation
    /// and every rewrite Scribe pastes is captured by Win+V and, when the user has cross-device
    /// clipboard turned on, uploaded to Microsoft and synced to their other machines. That is a
    /// straightforward violation of what this product promises.
    /// </para>
    /// <para>
    /// Microsoft documents three registered formats for this. Both of the granular ones are written
    /// as well as the blanket one, because the blanket format is the newer mechanism and the granular
    /// pair is what older builds honour: <c>ExcludeClipboardContentFromMonitorProcessing</c> excludes
    /// the item from history AND sync and from third-party clipboard monitors,
    /// <c>CanIncludeInClipboardHistory</c> set to 0 blocks history only, and
    /// <c>CanUploadToCloudClipboard</c> set to 0 blocks sync only.
    /// See <see href="https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats"/>.
    /// </para>
    /// <para>
    /// <b>This only protects clipboard writes Scribe performs itself.</b> The selection reader works
    /// by synthesizing Ctrl+C, which makes the TARGET application perform the write, and an
    /// annotation can only be attached by the process placing the data. So the text a user selects is
    /// captured by clipboard history and Scribe cannot prevent it. That limitation is real, is not
    /// fixable from here, and belongs in PRIVACY.md rather than being quietly hoped over.
    /// </para>
    /// <para>
    /// Best effort throughout: a failure to annotate must never fail the paste the user is waiting
    /// for. The caller must already hold the clipboard open.
    /// </para>
    /// </remarks>
    private static void MarkPrivate()
    {
        // A zero-length blob is enough for the blanket format: its presence is the signal.
        TrySetMarker("ExcludeClipboardContentFromMonitorProcessing");

        // The granular pair carry a serialized DWORD, and zero means "no".
        TrySetMarker("CanIncludeInClipboardHistory");
        TrySetMarker("CanUploadToCloudClipboard");
    }

    private static void TrySetMarker(string formatName)
    {
        try
        {
            var format = RegisterClipboardFormat(formatName);
            if (format == 0)
            {
                return;
            }

            // Still a real allocation for the blanket format: SetClipboardData rejects a null handle
            // for a registered format, so allocate the smallest block that can carry the marker.
            nint handle = GlobalAlloc(GMEM_MOVEABLE, sizeof(uint));
            if (handle == 0)
            {
                return;
            }

            nint pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                GlobalFree(handle);
                return;
            }

            try
            {
                // Zero for the granular pair means "no". The blanket format ignores the payload, so
                // zero is equally correct there and keeps one code path.
                Marshal.WriteInt32(pointer, 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(format, handle) == 0)
            {
                GlobalFree(handle);
            }
        }
        catch (Exception)
        {
            // Privacy hardening is not allowed to break the operation it is hardening.
        }
    }

    public static bool Clear()
    {
        if (!TryOpen())
        {
            return false;
        }

        try
        {
            return EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpen()
    {
        for (int attempt = 0; attempt < OpenRetries; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }
}
