using System.Diagnostics.CodeAnalysis;

namespace Scribe.Core.TextInjection;

/// <summary>
/// One-to-one seam over the Win32 clipboard calls <see cref="ClipboardBorrower"/> makes. Each member is a
/// single native call plus its memory handling; every decision, retry and ordering rule lives in the
/// borrower so it can be tested against a scripted fake. As in Win32, the reading and mutating members
/// are only valid while the calling thread holds the clipboard open.
/// </summary>
internal interface IClipboardNative
{
    /// <summary>GetClipboardSequenceNumber. Valid without opening the clipboard.</summary>
    uint SequenceNumber { get; }

    /// <summary>CountClipboardFormats. Valid without opening the clipboard.</summary>
    int FormatCount { get; }

    /// <summary>IsClipboardFormatAvailable. Valid without opening the clipboard.</summary>
    bool IsFormatAvailable(uint format);

    /// <summary>RegisterClipboardFormat; 0 when registration fails.</summary>
    uint RegisterFormat(string name);

    /// <summary>A single OpenClipboard attempt, with no retry.</summary>
    bool TryOpen();

    /// <summary>CloseClipboard.</summary>
    void Close();

    /// <summary>EmptyClipboard.</summary>
    bool Empty();

    /// <summary>Reads CF_UNICODETEXT; false when it is absent or its memory cannot be read.</summary>
    bool TryReadText([NotNullWhen(true)] out string? text);

    /// <summary>Places <paramref name="text"/> as CF_UNICODETEXT.</summary>
    bool SetText(string text);

    /// <summary>Places a small blob in a registered format.</summary>
    bool SetData(uint format, ReadOnlySpan<byte> data);

    /// <summary>
    /// Fills <paramref name="destination"/> from a registered format's data; false when the format is
    /// absent, unreadable, or smaller than <paramref name="destination"/>.
    /// </summary>
    bool TryReadData(uint format, Span<byte> destination);
}
