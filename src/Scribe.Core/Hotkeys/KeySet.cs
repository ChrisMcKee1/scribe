namespace Scribe.Core.Hotkeys;

/// <summary>
/// A set of virtual-key codes with one writer and lock-free readers.
///
/// The low-level hook reports codes in the documented range 1 to 254 (KBDLLHOOKSTRUCT.vkCode), so
/// 256 bits cover every key the hook can see. Codes outside that range are never members and cannot
/// be added. Only the thread that owns the enclosing state machine may call the mutating members;
/// <see cref="Contains"/> is safe from any thread because each word is published with a volatile
/// write, which is what lets the leak reconciler ask "is this key held?" without taking a lock the
/// hook callback could be waiting on.
/// </summary>
internal sealed class KeySet
{
    private const uint MaxKey = 0xFF;

    private readonly ulong[] _words = new ulong[4];

    public static bool IsTrackable(uint key) => key <= MaxKey;

    public bool Contains(uint key) =>
        key <= MaxKey && (Volatile.Read(ref _words[key >> 6]) & Bit(key)) != 0;

    /// <summary>Owner thread only. Returns true when the key was not already a member.</summary>
    public bool Add(uint key)
    {
        if (key > MaxKey)
        {
            return false;
        }

        ref var word = ref _words[key >> 6];
        var bit = Bit(key);
        if ((word & bit) != 0)
        {
            return false;
        }

        Volatile.Write(ref word, word | bit);
        return true;
    }

    /// <summary>Owner thread only. Returns true when the key was a member.</summary>
    public bool Remove(uint key)
    {
        if (key > MaxKey)
        {
            return false;
        }

        ref var word = ref _words[key >> 6];
        var bit = Bit(key);
        if ((word & bit) == 0)
        {
            return false;
        }

        Volatile.Write(ref word, word & ~bit);
        return true;
    }

    /// <summary>
    /// Any thread: whether a key other than <paramref name="a"/>, <paramref name="b"/> and <paramref name="c"/> is a member.
    /// The three must be below 64 (the mouse button codes are). No enumerator, no allocation.
    /// </summary>
    public bool ContainsAnyExcept(uint a, uint b, uint c)
    {
        var first = Volatile.Read(ref _words[0]) & ~(Bit(a) | Bit(b) | Bit(c));
        return first != 0 || Volatile.Read(ref _words[1]) != 0 || Volatile.Read(ref _words[2]) != 0 ||
            Volatile.Read(ref _words[3]) != 0;
    }

    /// <summary>Owner thread only.</summary>
    public void Clear()
    {
        for (var i = 0; i < _words.Length; i++)
        {
            Volatile.Write(ref _words[i], 0UL);
        }
    }

    private static ulong Bit(uint key) => 1UL << (int)(key & 63);
}
