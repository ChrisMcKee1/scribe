using System.Text.RegularExpressions;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

// The merge gate for staging the case relations without a list per hash (combined.md row 14, E.7; sign-off SIG-LANG-01):
// the builder as it was, copied below, decides every relation list and every fold.
public sealed partial class SpokenFormFoldTests
{
    [Fact]
    public void Case_relations_equal_the_reference_builder_for_every_code_unit()
    {
        var actual = SpokenFormFold.BuildCaseRelated(OrdinalIgnoreCaseHash);
        var expected = ReferenceBuildCaseRelated(OrdinalIgnoreCaseHash);
        AssertSameRelations(expected, actual, "the real hash");

        // And every fold: the reference relations plus the matcher's own equivalences, closed, and the smallest member of
        // each set, for all 65,536 code units (surrogates fold to one value, as they always have).
        var parent = new int[char.MaxValue + 1];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
            {
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        foreach (var (from, others) in expected)
        {
            foreach (var other in others)
            {
                if (!char.IsSurrogate(other))
                {
                    Union(from, other);
                }
            }
        }

        var every = EveryCharacter();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            if (char.IsSurrogate((char)i))
            {
                continue;
            }

            for (var match = new Regex(Regex.Escape(((char)i).ToString()), MatcherOptions).Match(every); match.Success; match = match.NextMatch())
            {
                if (!char.IsSurrogate(every[match.Index]))
                {
                    Union(i, match.Index);
                }
            }
        }

        var mismatches = new List<string>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            var reference = char.IsSurrogate(c) ? '\uD800' : (char)Find(i);
            var folded = SpokenFormFold.Fold(c);
            if (folded != reference && mismatches.Count < 20)
            {
                mismatches.Add($"U+{i:X4} folds to U+{(int)folded:X4}, the reference to U+{(int)reference:X4}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Colliding_hashes_are_compared_pair_by_pair_as_the_reference_builder_compared_them()
    {
        // The real hash is randomized per process and almost never collides, so the collision path is forced: every
        // character in a small number of buckets, and the real hash folded into 4,096, which keeps every pair that is
        // really equal together while adding thousands of pairs that are not.
        Func<char, int>[] hashes =
        [
            static c => c % 4_096,
            static c => c >> 4,
            static c => OrdinalIgnoreCaseHash(c) & 0xFFF,
        ];
        foreach (var (hash, name) in hashes.Zip(new[] { "c % 4096", "c >> 4", "real hash & 0xFFF" }))
        {
            AssertSameRelations(ReferenceBuildCaseRelated(hash), SpokenFormFold.BuildCaseRelated(hash), name);
        }

        // Folding the real hash loses no relation: only unequal pairs were added to the buckets.
        var real = SpokenFormFold.BuildCaseRelated(OrdinalIgnoreCaseHash);
        var masked = SpokenFormFold.BuildCaseRelated(hashes[2]);
        Assert.Equal(real.Keys.Order(), masked.Keys.Order());
        foreach (var (from, others) in real)
        {
            Assert.Equal(others.Order(), masked[from].Order());
        }
    }

    private static int OrdinalIgnoreCaseHash(char c) =>
        string.GetHashCode(new ReadOnlySpan<char>(in c), StringComparison.OrdinalIgnoreCase);

    private static void AssertSameRelations(Dictionary<char, List<char>> expected, Dictionary<char, List<char>> actual, string hash)
    {
        Assert.True(expected.Keys.SequenceEqual(actual.Keys), $"With {hash}, the related characters differ.");
        foreach (var (from, others) in expected)
        {
            Assert.True(
                others.SequenceEqual(actual[from]),
                $"With {hash}, U+{(int)from:X4} relates to [{Describe(actual[from])}], the reference to [{Describe(others)}].");
        }
    }

    private static string Describe(IEnumerable<char> characters) => string.Join(", ", characters.Select(c => $"U+{(int)c:X4}"));

    // SpokenFormFold.BuildCaseRelated as it was before the staging change, verbatim but for the hash, which it took from
    // OrdinalIgnoreCase directly: a new list for every distinct hash, then every pair of every list of two or more.
    private static Dictionary<char, List<char>> ReferenceBuildCaseRelated(Func<char, int> hashOf)
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

            var hash = hashOf(c);
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
