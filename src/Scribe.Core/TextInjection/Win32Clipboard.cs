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
    /// True when the clipboard holds content that is NOT representable as text — an image, copied
    /// files, a spreadsheet range. Text-bearing content reports false even when rich companions
    /// (HTML/RTF) accompany it, because the text round-trip preserves what matters. Non-text
    /// content cannot be saved and restored by this class, so callers should avoid clobbering it.
    /// Neither Win32 call requires opening the clipboard, so this never contends for the lock.
    /// </summary>
    public static bool HasNonTextContent() =>
        CountClipboardFormats() > 0 && !IsClipboardFormatAvailable(CF_UNICODETEXT);

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

            return true;
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
