namespace Scribe.Core.Libraries;

/// <summary>
/// The identity of a library term: its spoken form trimmed, compared case-insensitively
/// (<see cref="StringComparison.OrdinalIgnoreCase"/>), which is exactly the key 0.4.3 composed libraries by. One key
/// serves the editor (a Spoken repeated in one library), the built-in overlay (which shipped row an edit belongs to)
/// and composition (one rule per spoken form across libraries), so "get hub", "Get Hub" and " get hub " are one term
/// everywhere.
/// </summary>
/// <remarks>
/// <para>
/// White space inside a spoken form is part of the key, and <see cref="Normalize"/> is what keeps it regular: the
/// editor commits every typed Spoken value in that form (trimmed, each inner run of white space replaced by one
/// U+0020), so a value committed from now on never holds a double space, a tab or a no-break space, and for such a
/// value this key and a collapsing one agree. Only an untouched row from an older file can hold irregular inner white
/// space, and it keeps 0.4.3's behaviour: its pattern never matches dictated text (the matcher reduces the
/// transcript's runs of <c>[ \t\f\v]</c> to one space first), so it must not take the key of a regular row that does
/// match. Collapsing here would let an enabled "get  hub" in one library suppress a working "get hub" in another
/// (review finding A10). Editing such a row, or choosing to fix its spacing, commits it in normal form.
/// </para>
/// <para>
/// Trimming is <see cref="string.Trim()"/>'s: every <see cref="char.IsWhiteSpace(char)"/> character, the Unicode
/// White_Space set. Nothing else changes: no Unicode normalization (a precomposed "é" and "e" plus a combining accent
/// are different keys, as they are to the matcher), no removal of zero-width characters, and no case folding beyond
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, whose simple uppercase mapping differs from Swift's
/// <c>lowercased()</c> for a few letters (the Kelvin sign, capital sharp s, final sigma). The shared fixture
/// <c>tests/fixtures/libraries/term-keys.json</c> pins those answers for the macOS port.
/// </para>
/// <para>
/// The personal dictionary's merge (<see cref="PostProcessing.DictionaryLibraryComposer.Merge"/>) trims and compares
/// case-insensitively too, so a dictionary row and a library row that differ only in edge white space or case are one
/// spoken form there as well.
/// </para>
/// <para>
/// <see cref="Value"/> keeps the spelling it was made from, so a document can store it and a message can show it.
/// <see cref="ToString"/> deliberately returns only its length: a key is the user's words, and a key handed to a log
/// template by mistake must not put them in the log.
/// </para>
/// </remarks>
public readonly struct LibraryTermKey : IEquatable<LibraryTermKey>
{
    private readonly string? _value;

    private LibraryTermKey(string value) => _value = value;

    /// <summary>The key of no spoken form. <see cref="IsEmpty"/> is true, and it equals <c>default</c>.</summary>
    public static LibraryTermKey Empty => default;

    /// <summary>
    /// Compares spoken forms by their keys, for sets and dictionaries keyed by raw spoken text. A null spoken form has
    /// the empty key.
    /// </summary>
    public static IEqualityComparer<string?> SpokenComparer { get; } = new SpokenFormComparer();

    /// <summary>The spoken form trimmed, case and inner white space kept. Never null; empty for a null or blank spoken form.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True when the spoken form was null, empty or only white space.</summary>
    public bool IsEmpty => Value.Length == 0;

    /// <summary>The key of <paramref name="spoken"/>: the spoken form trimmed.</summary>
    public static LibraryTermKey From(string? spoken) => new(spoken?.Trim() ?? string.Empty);

    /// <summary>
    /// <paramref name="spoken"/> in the form the editor commits a Spoken value in: trimmed, each inner run of white
    /// space replaced by one space, case kept. Never null; empty for a null or blank spoken form.
    /// <c>Normalize(Normalize(x)) == Normalize(x)</c>, and a value in this form has the same key under this struct as
    /// under a collapsing comparison.
    /// </summary>
    public static string Normalize(string? spoken)
    {
        if (string.IsNullOrEmpty(spoken))
        {
            return string.Empty;
        }

        if (IsInCommitForm(spoken))
        {
            return spoken;
        }

        var buffer = new char[spoken.Length];
        var length = 0;
        var pendingSpace = false;
        foreach (var ch in spoken)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer[length++] = ' ';
                pendingSpace = false;
            }

            buffer[length++] = ch;
        }

        return new string(buffer, 0, length);
    }

    /// <summary>
    /// Whether <paramref name="spoken"/> is already in the form <see cref="Normalize"/> commits: no white space at its
    /// edges, and no inner white space but single U+0020 spaces. False for a row an older file left with a double
    /// space, a tab or a no-break space inside it, which the editor offers to fix. True for an empty or null value.
    /// </summary>
    public static bool IsInCommitForm(string? spoken)
    {
        if (string.IsNullOrEmpty(spoken))
        {
            return true;
        }

        if (char.IsWhiteSpace(spoken[0]) || char.IsWhiteSpace(spoken[^1]))
        {
            return false;
        }

        for (var i = 1; i < spoken.Length; i++)
        {
            var ch = spoken[i];
            if (char.IsWhiteSpace(ch) && (ch != ' ' || spoken[i - 1] == ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether two spoken forms are the same term.</summary>
    public static bool AreSame(string? spoken, string? otherSpoken) => From(spoken).Equals(From(otherSpoken));

    public bool Equals(LibraryTermKey other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is LibraryTermKey other && Equals(other);

    // Zero for the empty key, the hash HashSet and Dictionary give a null element without asking the comparer, so a null
    // and a blank spoken form stay one entry in a set built on SpokenComparer.
    public override int GetHashCode() => IsEmpty ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>The key's shape, never its text; read <see cref="Value"/> for the text.</summary>
    public override string ToString() => $"LibraryTermKey({Value.Length} characters)";

    public static bool operator ==(LibraryTermKey left, LibraryTermKey right) => left.Equals(right);

    public static bool operator !=(LibraryTermKey left, LibraryTermKey right) => !left.Equals(right);

    private sealed class SpokenFormComparer : IEqualityComparer<string?>
    {
        public bool Equals(string? x, string? y) => AreSame(x, y);

        public int GetHashCode(string? obj) => From(obj).GetHashCode();
    }
}
