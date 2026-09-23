using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Win32 implementation of <see cref="IClipboardNative"/>: Unicode text (CF_UNICODETEXT) plus the small
/// registered-format blobs Scribe writes as privacy markers and borrow receipts. Preserving non-text
/// formats (images, files) is intentionally out of scope for v1; only text is round-tripped.
/// All members must be called on an STA thread that owns a message queue.
/// </summary>
/// <remarks>
/// The clipboard is opened with a NULL owner, which is how Scribe has always opened it and the path its
/// real-clipboard round trip (<c>Win32ClipboardTests</c>) exercises. Microsoft documents that
/// EmptyClipboard after OpenClipboard(NULL) leaves the owner NULL and that this "causes SetClipboardData
/// to fail". That conflict is not settled: an isolated measurement was not possible, and an owner window
/// carries obligations of its own, because another application's EmptyClipboard sends the owner
/// WM_DESTROYCLIPBOARD and whether that send waits on a thread that is not pumping was not measured
/// either. The working behavior is kept rather than swapped for an unmeasured one.
/// </remarks>
internal sealed class Win32Clipboard : IClipboardNative
{
    public static Win32Clipboard Instance { get; } = new();

    private Win32Clipboard()
    {
    }

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public int FormatCount => CountClipboardFormats();

    public bool IsFormatAvailable(uint format) => IsClipboardFormatAvailable(format);

    public uint RegisterFormat(string name) => RegisterClipboardFormat(name);

    public bool TryOpen() => OpenClipboard(nint.Zero);

    public void Close() => CloseClipboard();

    public bool Empty() => EmptyClipboard();

    public bool TryReadText([NotNullWhen(true)] out string? text)
    {
        text = null;
        nint handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == 0)
        {
            return false;
        }

        // Bounded by the block size: another application's data is not guaranteed to be terminated.
        int maxChars = (int)Math.Min((ulong)GlobalSize(handle) / sizeof(char), int.MaxValue);
        if (maxChars == 0)
        {
            return false;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return false;
        }

        try
        {
            var buffer = new char[maxChars];
            Marshal.Copy(pointer, buffer, 0, maxChars);
            int end = Array.IndexOf(buffer, '\0');
            text = new string(buffer, 0, end < 0 ? maxChars : end);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    public bool SetText(string text)
    {
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

    public bool SetData(uint format, ReadOnlySpan<byte> data)
    {
        // Always a real allocation, even for an empty payload: SetClipboardData rejects a null handle
        // for a registered format (a null handle means delayed rendering).
        nint global = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
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
            if (data.Length > 0)
            {
                Marshal.Copy(data.ToArray(), 0, target, data.Length);
            }
        }
        finally
        {
            GlobalUnlock(global);
        }

        if (SetClipboardData(format, global) == 0)
        {
            GlobalFree(global);
            return false;
        }

        return true;
    }

    public bool TryReadData(uint format, Span<byte> destination)
    {
        nint handle = GetClipboardData(format);
        if (handle == 0 || GlobalSize(handle) < (nuint)destination.Length)
        {
            return false;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return false;
        }

        try
        {
            var buffer = new byte[destination.Length];
            Marshal.Copy(pointer, buffer, 0, buffer.Length);
            buffer.CopyTo(destination);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}
