using System.Buffers;
using System.Text.RegularExpressions;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// When an ordinal ignore-case search may stand in for a literal regular expression: a rule may be skipped without
/// running its regex only when the search proves the regex cannot match. The regex still decides every match.
/// </summary>
/// <remarks>
/// For a pattern of ASCII characters, compiled as an escaped literal with <see cref="TextPostProcessor.DictionaryMatchOptions"/>
/// (lookarounds only restrict it), every match is a run of characters each of which the regex engine equates with the
/// pattern's character in its place. <see cref="UnsafeForAscii"/> holds every character the engine equates with some ASCII
/// character that <see cref="StringComparison.OrdinalIgnoreCase"/> does not (U+212A KELVIN SIGN on .NET 10). In a text
/// holding none of them every such run is an ordinal ignore-case occurrence of the pattern, so a failed search drops no
/// match. The set is read from the running engine once, never written down, so a runtime whose case table changes cannot
/// make the search unsound. A pattern with any other character, or a text holding one of these, always runs its regex.
/// </remarks>
internal static class OrdinalPrefilter
{
    private static readonly Lazy<Guard> s_guard = new(() => new Guard(ComputeUnsafeForAscii()));

    /// <summary>The characters that make the search unsound for an ASCII pattern (see the remarks).</summary>
    public static SearchValues<char> UnsafeForAscii => s_guard.Value.Values;

    /// <summary>The same set, in ascending order, for tests and diagnostics.</summary>
    public static IReadOnlyList<char> UnsafeForAsciiList => s_guard.Value.List;

    /// <summary>Whether the search may decide for ASCII patterns in <paramref name="text"/>.</summary>
    public static bool IsSound(ReadOnlySpan<char> text) => !text.ContainsAny(UnsafeForAscii);

    /// <summary>
    /// False only when the regex for <paramref name="asciiPattern"/> cannot match <paramref name="text"/>: the pattern is
    /// ASCII (the caller checked it once), the text is sound (<see cref="IsSound"/>, checked once per text) and the pattern
    /// does not occur in it, ignoring case.
    /// </summary>
    public static bool MayMatch(ReadOnlySpan<char> text, string asciiPattern) =>
        text.IndexOf(asciiPattern, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Reads the set from the regex engine with the matcher's own options: every UTF-16 code unit the regex for each ASCII
    /// character matches that is neither that character nor equal to it ignoring case, ordinally.
    /// </summary>
    internal static char[] ComputeUnsafeForAscii()
    {
        var every = string.Create(char.MaxValue + 1, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)i;
            }
        });

        var found = new SortedSet<char>();
        for (var c = 0; c < 128; c++)
        {
            var ascii = (char)c;
            var regex = new Regex(Regex.Escape(ascii.ToString()), TextPostProcessor.DictionaryMatchOptions);
            foreach (var match in regex.EnumerateMatches(every))
            {
                var other = every[match.Index];
                if (other != ascii &&
                    !MemoryExtensions.Equals(new ReadOnlySpan<char>(in other), new ReadOnlySpan<char>(in ascii), StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(other);
                }
            }
        }

        return [.. found];
    }

    private sealed class Guard(char[] list)
    {
        public SearchValues<char> Values { get; } = SearchValues.Create(list);

        public IReadOnlyList<char> List { get; } = Array.AsReadOnly(list);
    }
}
