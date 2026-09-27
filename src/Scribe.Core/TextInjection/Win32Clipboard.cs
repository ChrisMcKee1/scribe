using System.Diagnostics.CodeAnalysis;
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

        return TryReadUnicodeText(handle, out text);
    }

    public bool SetText(string text)
    {
        nint global = AllocUnicodeText(text);
        if (global == 0)
        {
            return false;
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
        nint global = AllocData(data);
        if (global == 0)
        {
            return false;
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
        return handle != 0 && TryReadBlock(handle, destination);
    }

    /// <summary>
    /// The text of a CF_UNICODETEXT global memory block: up to its first NUL, or all of it when it has none. False when the
    /// block holds less than one character or cannot be locked.
    /// </summary>
    internal static unsafe bool TryReadUnicodeText(nint handle, [NotNullWhen(true)] out string? text)
    {
        text = null;

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
            // Read where it lies, only while it is locked, and only as far as the terminator: the text is the one copy made
            // in this process, and whatever the block holds after it is never copied at all.
            var block = new ReadOnlySpan<char>((void*)pointer, maxChars);
            int end = block.IndexOf('\0');
            text = new string(end < 0 ? block : block[..end]);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// A new GMEM_MOVEABLE block holding <paramref name="text"/> as null-terminated UTF-16, unlocked, or 0 when it cannot
    /// be allocated or locked. The caller owns it until SetClipboardData succeeds.
    /// </summary>
    internal static unsafe nint AllocUnicodeText(string text)
    {
        // Null-terminated UTF-16; GMEM_MOVEABLE memory is required for clipboard handles.
        nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
        nint global = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (global == 0)
        {
            return 0;
        }

        nint target = GlobalLock(global);
        if (target == 0)
        {
            GlobalFree(global);
            return 0;
        }

        try
        {
            var destination = new Span<char>((void*)target, text.Length + 1);
            text.AsSpan().CopyTo(destination);
            destination[text.Length] = '\0';
        }
        finally
        {
            GlobalUnlock(global);
        }

        return global;
    }

    /// <summary>
    /// A new GMEM_MOVEABLE block holding <paramref name="data"/>, unlocked, or 0 when it cannot be allocated or locked. The
    /// caller owns it until SetClipboardData succeeds.
    /// </summary>
    internal static unsafe nint AllocData(ReadOnlySpan<byte> data)
    {
        // Always a real allocation, even for an empty payload: SetClipboardData rejects a null handle
        // for a registered format (a null handle means delayed rendering).
        nint global = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (global == 0)
        {
            return 0;
        }

        nint target = GlobalLock(global);
        if (target == 0)
        {
            GlobalFree(global);
            return 0;
        }

        try
        {
            data.CopyTo(new Span<byte>((void*)target, data.Length));
        }
        finally
        {
            GlobalUnlock(global);
        }

        return global;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from the start of a global memory block; false when the block is smaller than
    /// <paramref name="destination"/> or cannot be locked.
    /// </summary>
    internal static unsafe bool TryReadBlock(nint handle, Span<byte> destination)
    {
        if (GlobalSize(handle) < (nuint)destination.Length)
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
            new ReadOnlySpan<byte>((void*)pointer, destination.Length).CopyTo(destination);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }
}
