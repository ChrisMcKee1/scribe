using System.Text.RegularExpressions;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Folds a spoken form so that any two spoken forms dictation could treat as the same text fold to the same string. It is
/// deliberately broader than every comparison dictation makes: when two folded forms neither equal nor contain each
/// other, no rule built from one can match text that a rule built from the other matches, and the composer cannot take
/// them for the same spoken form.
/// </summary>
/// <remarks>
/// <para>
/// A character folds to the smallest member of the set reached from it through four relations: the matcher's case
/// equivalence (<see cref="TextPostProcessor"/> compiles every spoken form as an escaped literal, so a rule matches
/// exactly its own length of text, one character against one), <see cref="StringComparison.OrdinalIgnoreCase"/> (the
/// composer's key and Save's duplicate check), invariant upper and lower case in both directions, and dotted and dotless
/// i with i. Each relation is symmetric (<c>SpokenFormFoldTests</c> checks the matcher's on every character), so the set
/// is the same from any of its members, and a character folds the same whichever member was asked about first.
/// </para>
/// <para>
/// The relations are read from the running .NET rather than written down. The matcher's comes from the regex engine
/// itself, with the matcher's own options: the invariant case mapping comes from the operating system's Unicode data and
/// lacks pairs the engine's own table has (U+0264 with U+A7CB on the machine this was written on), so a fold built from
/// case mapping alone would be narrower than the matcher. OrdinalIgnoreCase's comes from the comparer; where it reads the
/// same case data as the invariant mapping it adds nothing, but it is what the composer and Save compare with, so it is
/// linked in for any machine where the two differ. So the fold is broader than dictation on any machine, whatever Unicode
/// data it carries.
/// </para>
/// <para>
/// Every UTF-16 surrogate folds to one value, so a character outside the Basic Multilingual Plane overlaps any other in
/// the same place: OrdinalIgnoreCase compares such characters as whole code points and folds some of them in pairs,
/// which a character-by-character fold cannot see.
/// </para>
/// </remarks>
internal static class SpokenFormFold
{
    private const char Surrogate = '\uD800';

    // The folded character plus one, or 0 until its set has been worked out. Each value is a single int write, so a reader
    // never sees half of one; two threads working out the same set write the same values.
    private static readonly int[] s_folded = new int[char.MaxValue + 1];

    private static readonly Lazy<string> s_everyCharacter = new(() => string.Create(char.MaxValue + 1, 0, static (span, _) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = (char)i;
        }
    }));

    private static readonly Lazy<Dictionary<char, List<char>>> s_caseRelated = new(BuildCaseRelated);

    public static string Fold(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = Fold(source[i]);
        }
    });

    public static char Fold(char c)
    {
        if (char.IsSurrogate(c))
        {
            return Surrogate;
        }

        var known = Volatile.Read(ref s_folded[c]);
        return known != 0 ? (char)(known - 1) : FoldSet(c);
    }

    private static char FoldSet(char start)
    {
        var members = new HashSet<char> { start };
        var pending = new Stack<char>();
        pending.Push(start);
        while (pending.TryPop(out var c))
        {
            foreach (var other in Related(c))
            {
                if (!char.IsSurrogate(other) && members.Add(other))
                {
                    pending.Push(other);
                }
            }
        }

        var folded = members.Min();
        foreach (var member in members)
        {
            Volatile.Write(ref s_folded[member], folded + 1);
        }

        return folded;
    }

    private static IEnumerable<char> Related(char c)
    {
        if (s_caseRelated.Value.TryGetValue(c, out var cased))
        {
            foreach (var other in cased)
            {
                yield return other;
            }
        }

        // Every character the matcher's own regex for this one matches.
        var text = s_everyCharacter.Value;
        var regex = new Regex(Regex.Escape(c.ToString()), TextPostProcessor.DictionaryMatchOptions);
        for (var match = regex.Match(text); match.Success; match = match.NextMatch())
        {
            yield return text[match.Index];
        }
    }

    // Invariant upper and lower case both ways, OrdinalIgnoreCase, and dotted and dotless i with i, as symmetric links.
    private static Dictionary<char, List<char>> BuildCaseRelated()
    {
        var related = new Dictionary<char, List<char>>();
        void Link(char a, char b)
        {
            if (a == b)
            {
                return;
            }

            Add(a, b);
            Add(b, a);
        }

        void Add(char from, char to)
        {
            if (!related.TryGetValue(from, out var list))
            {
                related[from] = list = [];
            }

            if (!list.Contains(to))
            {
                list.Add(to);
            }
        }

        // OrdinalIgnoreCase gives strings it calls equal the same hash code, so characters it calls equal share a bucket.
        var buckets = new Dictionary<int, List<char>>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            Link(c, char.ToUpperInvariant(c));
            Link(c, char.ToLowerInvariant(c));

            var hash = string.GetHashCode(new ReadOnlySpan<char>(in c), StringComparison.OrdinalIgnoreCase);
            if (!buckets.TryGetValue(hash, out var bucket))
            {
                buckets[hash] = bucket = [];
            }

            bucket.Add(c);
        }

        foreach (var bucket in buckets.Values.Where(b => b.Count > 1))
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    if (string.Equals(bucket[i].ToString(), bucket[j].ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        Link(bucket[i], bucket[j]);
                    }
                }
            }
        }

        Link('\u0130', 'i');
        Link('\u0131', 'i');
        return related;
    }
}
