namespace Scribe.Core.Libraries;

/// <summary>
/// The identity of a library term: its spoken form, trimmed, with every run of white space inside it collapsed to one
/// space, compared case-insensitively (<see cref="StringComparison.OrdinalIgnoreCase"/>). One key serves the editor
/// (a Spoken repeated in one library), the built-in overlay (which shipped row an edit belongs to) and composition (one
/// rule per spoken form across libraries), so "get hub", "Get  Hub" and " get hub " are one term everywhere.
/// </summary>
/// <remarks>
/// <para>
/// White space is what <see cref="char.IsWhiteSpace(char)"/> says it is, the same set <see cref="string.Trim()"/>
/// removes: the Unicode White_Space characters, so a no-break space, an ideographic space, a tab or a line break inside
/// a spoken form all collapse to one U+0020. Nothing else changes: no Unicode normalization (a precomposed "é" and
/// "e" plus a combining accent are different keys, as they are to the matcher), no removal of zero-width characters,
/// and no case folding beyond <see cref="StringComparison.OrdinalIgnoreCase"/>, whose simple uppercase mapping differs
/// from Swift's <c>lowercased()</c> for a few letters (the Kelvin sign, capital sharp s, final sigma). The shared
/// fixture <c>tests/fixtures/libraries/term-keys.json</c> pins those answers for the macOS port.
/// </para>
/// <para>
/// The personal dictionary's merge keeps its own key (<see cref="PostProcessing.DictionaryLibraryComposer.Merge"/>
/// trims and compares case-insensitively without collapsing), because the dictionary always wins and its rows are
/// validated by their own rules.
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

    /// <summary>
    /// The spoken form in key form: trimmed, each inner run of white space replaced by one space, case kept. Never
    /// null; empty for a null or blank spoken form. <c>Normalize(Normalize(x)) == Normalize(x)</c>.
    /// </summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True when the spoken form was null, empty or only white space.</summary>
    public bool IsEmpty => Value.Length == 0;

    /// <summary>The key of <paramref name="spoken"/>.</summary>
    public static LibraryTermKey From(string? spoken) => new(Normalize(spoken));

    /// <summary>
    /// <paramref name="spoken"/> trimmed, with each inner run of white space replaced by one space and its case kept:
    /// the form the editor commits a Spoken value in, and <see cref="Value"/>.
    /// </summary>
    public static string Normalize(string? spoken)
    {
        if (string.IsNullOrEmpty(spoken))
        {
            return string.Empty;
        }

        if (IsNormalized(spoken))
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

    // Most spoken forms are already in key form, so the common case returns the caller's string instead of copying it.
    private static bool IsNormalized(string spoken)
    {
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

    private sealed class SpokenFormComparer : IEqualityComparer<string?>
    {
        public bool Equals(string? x, string? y) => AreSame(x, y);

        public int GetHashCode(string? obj) => From(obj).GetHashCode();
    }
}
