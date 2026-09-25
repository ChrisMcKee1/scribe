using System.Security.Cryptography;

namespace Scribe.Core.Libraries;

/// <summary>
/// The content identity of a library file (<see cref="LibraryContentHash"/>): SHA-256 of its bytes in lowercase
/// hexadecimal. Every pre-image, post-image, backup and preservation check in the journal compares these, so nothing
/// the journal decides rests on an error code, a size or a time stamp alone.
/// </summary>
internal static class LibraryContentHashing
{
    public static LibraryContentHash Of(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        return new LibraryContentHash(Convert.ToHexStringLower(digest));
    }

    /// <summary>64 lowercase hexadecimal digits, the only form the journal and the local state store.</summary>
    public static bool IsWellFormed(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParse(string? value, out LibraryContentHash hash)
    {
        hash = default;
        if (!IsWellFormed(value))
        {
            return false;
        }

        hash = new LibraryContentHash(value!);
        return true;
    }

    /// <summary>The first 16 hexadecimal digits, the infix of an edits document's set-aside name (rule R7).</summary>
    public static string Prefix(LibraryContentHash hash) => hash.Value[..16];
}
