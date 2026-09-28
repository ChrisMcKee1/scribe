using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Frozen text-memory code from Win32Clipboard.cs at 10c9a0b4689b6977fa5609fe4f91f120edb150d7.
/// The read/write conversion statements are verbatim. The wrappers supply an already locked pointer and a logical
/// byte length instead of looking up clipboard content. TryReadUnicodeText copies the old private-block read path.
/// No clipboard lookup, publication or ownership API is called, and no shipping conversion helper is reused here.
/// </summary>
internal static class Win32Clipboard050TextOracle
{
    public static string ReadUnicodeText(nint pointer, nuint byteLength)
    {
        int maxChars = (int)Math.Min((ulong)byteLength / sizeof(char), int.MaxValue);
        string text;

        var buffer = new char[maxChars];
        Marshal.Copy(pointer, buffer, 0, maxChars);
        int end = Array.IndexOf(buffer, '\0');
        text = new string(buffer, 0, end < 0 ? maxChars : end);

        return text;
    }

    public static void WriteUnicodeText(nint target, string text)
    {
        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
        Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
    }

    public static bool TryReadUnicodeText(nint handle, [NotNullWhen(true)] out string? text)
    {
        text = null;
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
}
