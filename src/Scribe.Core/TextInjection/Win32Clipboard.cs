using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.TextInjection;

/// <summary>
/// Win32 implementation of <see cref="IClipboardNative"/>: Unicode text (CF_UNICODETEXT), the small
/// registered-format blobs Scribe writes as privacy markers and borrow receipts, and the whole-clipboard copy a
/// formatting paste keeps (global memory blocks and enhanced metafiles, which <see cref="ClipboardBorrower"/> chooses).
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

    public IReadOnlyList<uint>? ListFormats()
    {
        var formats = new List<uint>();
        uint format = 0;
        while (true)
        {
            format = EnumClipboardFormats(format);
            if (format == 0)
            {
                // ERROR_SUCCESS marks the end of the list; any other code means it stopped short.
                return Marshal.GetLastPInvokeError() == 0 ? formats : null;
            }

            formats.Add(format);
        }
    }

    public byte[]? TryCopyBlock(uint format, long maxBytes)
    {
        nint handle = GetClipboardData(format);
        return handle == 0 ? null : CopyBlock(handle, maxBytes);
    }

    public byte[]? TryCopyEnhancedMetafile(long maxBytes)
    {
        // The clipboard owns this handle, so it is read and never deleted here.
        nint metafile = GetClipboardData(CF_ENHMETAFILE);
        if (metafile == 0)
        {
            return null;
        }

        uint size = GetEnhMetaFileBits(metafile, 0, null);
        if (size == 0 || size > maxBytes)
        {
            return null;
        }

        var bits = new byte[size];
        return GetEnhMetaFileBits(metafile, size, bits) == size ? bits : null;
    }

    public bool SetEnhancedMetafile(ReadOnlySpan<byte> bits)
    {
        nint metafile = SetEnhMetaFileBits((uint)bits.Length, bits.ToArray());
        if (metafile == 0)
        {
            return false;
        }

        if (SetClipboardData(CF_ENHMETAFILE, metafile) == 0)
        {
            // Ownership only transfers to the system on success.
            DeleteEnhMetaFile(metafile);
            return false;
        }

        return true;
    }

    public bool OwnerResponds(uint timeoutMs)
    {
        nint owner = GetClipboardOwner();
        return owner == 0
            || SendMessageTimeoutNoText(owner, WM_NULL, 0, 0, SMTO_BLOCK | SMTO_ABORTIFHUNG, timeoutMs, out _) != 0;
    }

    /// <summary>
    /// A copy of a whole global memory block, or null when it is empty, larger than <paramref name="maxBytes"/>, or cannot
    /// be locked. The block's allocated size can exceed what its owner asked for; the copy keeps all of it, as the
    /// clipboard handed it out, so whatever the format records about its own length stays true.
    /// </summary>
    internal static unsafe byte[]? CopyBlock(nint handle, long maxBytes)
    {
        nuint size = GlobalSize(handle);
        if (size == 0 || maxBytes <= 0 || size > (nuint)maxBytes || size > int.MaxValue)
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
            return new ReadOnlySpan<byte>((void*)pointer, (int)size).ToArray();
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// The text of a CF_UNICODETEXT global memory block: up to its first NUL, or all of it when it has none. False when the
    /// block holds less than one character or cannot be locked.
    /// </summary>
    internal static bool TryReadUnicodeText(nint handle, [NotNullWhen(true)] out string? text)
    {
        text = null;

        // Bounded by the block size: another application's data is not guaranteed to be terminated.
        nuint byteLength = GlobalSize(handle);
        if (byteLength < sizeof(char))
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
            // Read only while the block is locked.
            text = ReadUnicodeText(pointer, byteLength);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// The UTF-16 text in the <paramref name="byteLength"/> bytes at <paramref name="pointer"/>: up to the first NUL, or all
    /// of them when there is none, and never a byte past them; an odd last byte belongs to no character. The memory is read
    /// where it lies, so the text is the one copy made in this process, and whatever follows the terminator is never copied.
    /// </summary>
    internal static unsafe string ReadUnicodeText(nint pointer, nuint byteLength)
    {
        int maxChars = (int)Math.Min((ulong)byteLength / sizeof(char), int.MaxValue);
        var block = new ReadOnlySpan<char>((void*)pointer, maxChars);
        int end = block.IndexOf('\0');
        return new string(end < 0 ? block : block[..end]);
    }

    /// <summary>
    /// A new GMEM_MOVEABLE block holding <paramref name="text"/> as null-terminated UTF-16, unlocked, or 0 when it cannot
    /// be allocated or locked. The caller owns it until SetClipboardData succeeds.
    /// </summary>
    internal static nint AllocUnicodeText(string text)
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
            WriteUnicodeText(target, text);
        }
        finally
        {
            GlobalUnlock(global);
        }

        return global;
    }

    /// <summary>
    /// Writes <paramref name="text"/> and one NUL, as UTF-16, at <paramref name="target"/>, which has room for
    /// text.Length + 1 characters. Nothing after them is touched.
    /// </summary>
    internal static unsafe void WriteUnicodeText(nint target, string text)
    {
        var destination = new Span<char>((void*)target, text.Length + 1);
        text.AsSpan().CopyTo(destination);
        destination[text.Length] = '\0';
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
