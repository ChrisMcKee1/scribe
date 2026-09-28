using System.Globalization;
using System.Text;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// The usage corpus the language work's merge gates judge with (sign-off SIG-LANG-01): Astra's
/// <c>AstraChallengeChecks.CheckUsage</c> scenarios (12 curated, 60 seeded and one of every shipped row), a seeded corpus
/// of Unicode words, and the cultures the gates run under. In every comparison the old code decides.
/// </summary>
internal static class UsageEquivalenceCorpus
{
    public static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    public static readonly string[] Cultures = ["en-US", "tr-TR", "az-Latn-AZ"];

    /// <summary>Every combination of the two usage flags, the empty one (the old counting) first.</summary>
    public static readonly string[] FlagCombinations =
    [
        "",
        PerfFlags.SparseUsageAggregation,
        PerfFlags.UsageTermIndex,
        $"{PerfFlags.SparseUsageAggregation},{PerfFlags.UsageTermIndex}",
    ];

    public static IEnumerable<(string Name, DictionaryEntry[] Terms, string[] Texts)> Scenarios()
    {
        yield return ("alias-max-per-history",
            [DictionaryEntry.New("next js", "Next.js")],
            ["next js", "Next.js", "next js next js Next.js Next.js"]);
        yield return ("shared-form-owners",
            [DictionaryEntry.New("same alias", "Alpha"), DictionaryEntry.New("same alias", "Beta")],
            ["same alias", "same alias Alpha Beta"]);
        yield return ("nonoverlapping",
            [DictionaryEntry.New("a-a", "A-A"), DictionaryEntry.New("a a", "A A")],
            ["a-a-a", "a a a", "a-a-a-a a a a a"]);
        yield return ("adjacent-boundaries",
            [DictionaryEntry.New("dot net", ".NET"), DictionaryEntry.New("c#", "C#"), DictionaryEntry.New("rust", "Rust")],
            ["x_Rust .NET/x .NET.NET C#C# xC# Rust2", "x\u0301Rust Rust\u0301x"]);
        yield return ("invariant-i",
            [DictionaryEntry.New("i i", "II"), DictionaryEntry.New("istanbul", "Istanbul")],
            ["I I i i \u0130 \u0131", "ISTANBUL \u0130stanbul \u0131stanbul"]);
        yield return ("kelvin",
            [DictionaryEntry.New("kube key", "KubeKey"), DictionaryEntry.New("k8s", "Kubernetes")],
            ["\u212Aube key", "KUBE KEY", "\u212A8s k8s Kubernetes"]);
        yield return ("non-ascii",
            [DictionaryEntry.New("\u03c3 \u03c3", "Sigma"), DictionaryEntry.New("stra\u00dfe", "Stra\u00dfe"), DictionaryEntry.New("caf\u00e9", "Caf\u00e9")],
            ["\u03a3 \u03a3 \u03c2 \u03c2", "STRASSE stra\u00dfe cafe\u0301 caf\u00e9"]);
        yield return ("literal-syntax",
            [DictionaryEntry.New("a+b", "A+B"), DictionaryEntry.New("x(y)", "X(Y)"), DictionaryEntry.New("a\0b", "AB")],
            ["a+b a+b x(y) a\0b", "a ab a+b"]);
        yield return ("privacy-group",
            [DictionaryEntry.New("safe alias", "Widget"), DictionaryEntry.New("deny alias", " Widget\n"), DictionaryEntry.New("long", new string('X', 101))],
            ["Widget safe alias deny alias", "Widget Widget long"]);
        yield return ("disabled-and-short",
            [DictionaryEntry.New("x", "y"), DictionaryEntry.New("um", ""), DictionaryEntry.New("off", "Disabled") with { Enabled = false }],
            ["x y um off Disabled", "SomethingElse SomethingElse"]);
        yield return ("novel-reset",
            [DictionaryEntry.New("known", "Known")],
            ["CloudThing CloudThing", "ordinary", "CloudThing", "known"]);
        yield return ("supplementary",
            [DictionaryEntry.New("\U00010400 term", "Name"), DictionaryEntry.New("a\ud800b", "BrokenText")],
            ["\U00010428 term \U00010400 term", "a\ud800b a\ud800b"]);

        var random = new Random(72381);
        string[] forms = ["a-a", "a a", "next js", "Next.js", "kube key", "\u212Aube key", "i i", "\u03c3 \u03c3", "caf\u00e9", ".NET", "C#", "x_y", "short", "deny alias"];
        string[] written = ["A-A", "A A", "Next.js", "KubeKey", "II", "Shared", " Shared\n", "", "\"quote\"", new string('x', 101)];
        for (var round = 0; round < 60; round++)
        {
            var terms = Enumerable.Range(0, 8)
                .Select(_ => DictionaryEntry.New(forms[random.Next(forms.Length)], written[random.Next(written.Length)]) with { Enabled = random.Next(5) != 0 })
                .ToArray();
            var texts = Enumerable.Range(0, 4)
                .Select(_ => string.Join(" ", Enumerable.Range(0, 8).Select(_ => forms[random.Next(forms.Length)])))
                .ToArray();
            yield return ("generated-" + round, terms, texts);
        }

        var shipped = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries).ToArray();
        yield return ("every-shipped-row", shipped, [.. shipped.Select(entry => "(" + entry.Pattern + ") " + entry.Replacement)]);
    }

    public static HistoryEntry[] History(IReadOnlyList<string> texts) =>
        [.. texts.Select((text, index) => new HistoryEntry(index + 1, Now.AddMinutes(-index), text, 9500, 300))];

    /// <summary><see cref="FlagCombinations"/> as theory data.</summary>
    public static TheoryData<string> FlagSets()
    {
        var data = new TheoryData<string>();
        foreach (var flags in FlagCombinations)
        {
            data.Add(flags);
        }

        return data;
    }

    /// <summary>Every field of two snapshots, terms with their sharing, in order.</summary>
    public static void AssertSameSnapshot(UsageAnalyzer.Snapshot expected, UsageAnalyzer.Snapshot actual, string context)
    {
        Assert.Equal(expected.Dictations, actual.Dictations);
        Assert.Equal(expected.Words, actual.Words);
        Assert.Equal(expected.ActiveDays, actual.ActiveDays);
        Assert.Equal(expected.Speech, actual.Speech);
        Assert.Equal(expected.AverageWords, actual.AverageWords);
        Assert.Equal(expected.TopApps, actual.TopApps);
        Assert.Equal(expected.Trend, actual.Trend);
        AssertSameTerms(expected.Terms, actual.Terms, context);
        Assert.Equal(expected.Granularity, actual.Granularity);
        Assert.Equal(expected.LongestDictation, actual.LongestDictation);
    }

    // The scenarios' sharing rule: a spoken form starting with "deny" keeps its label on this PC.
    public static bool MayShare(DictionaryEntry entry) => !entry.Pattern.StartsWith("deny", StringComparison.Ordinal);

    /// <summary>
    /// Seeded words for the word counters: letters, marks, numbers, apostrophes and hyphens of several scripts, supplementary
    /// and lone surrogates, controls, the Turkish and Azeri i, and the characters the two counters differ on.
    /// </summary>
    public static List<string?> UnicodeTexts(int seed, int count)
    {
        string[] pieces =
        [
            "word", "don't", "don\u2019t", "state-of-the-art", "a--b", "a''b", "x'", "'x", "-", "'", "\u2019", "12", "3.14",
            "1,000", "e\u0301te", "\u00E9t\u00E9", "na\u00EFve", "\u0130stanbul", "\u0131\u0131", "I\u0307", "\u212Aelvin",
            "\u03a3\u03c2", "\u0661\u0662\u0663", "\u4E2D\u6587", "\uD835\uDCB3yz", "\uD83D\uDE00", "\ud800", "\udc00x", "\0",
            "\u200B", "\u00A0", "a\u00ADb", "K8s", ".NET", "C#", "e.g.", "x/y", "under_score", "#hash", "--", "...", "\u05D0\u05B8",
            "\u0915\u094D\u0937", "\u3042\u3099", "\u2160\u2161", "\u00BD", "\u0660", "Ab\u0301c", "\uFF21\uFF22",
        ];
        string[] separators = [" ", "  ", ", ", ". ", "\n", "\t", "\r\n", "", "-", "'"];
        var random = new Random(seed);
        var texts = new List<string?>(count);
        for (var i = 0; i < count; i++)
        {
            if (random.Next(20) == 0)
            {
                texts.Add(random.Next(2) == 0 ? null : random.Next(2) == 0 ? string.Empty : " \t ");
                continue;
            }

            var text = new StringBuilder();
            for (var w = random.Next(0, 30); w > 0; w--)
            {
                text.Append(pieces[random.Next(pieces.Length)]);
                text.Append(separators[random.Next(separators.Length)]);
            }

            texts.Add(text.ToString());
        }

        return texts;
    }

    public static void InCulture(string name, Action run)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            run();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    public static string Describe(IReadOnlyList<UsageAnalyzer.TermUsage> terms) =>
        string.Join("; ", terms.Select(term => $"{term.Text}|{term.Dictations}|{term.Occurrences}|{term.Covered}|{term.Shareable}"));

    public static void AssertSameTerms(IReadOnlyList<UsageAnalyzer.TermUsage> expected, IReadOnlyList<UsageAnalyzer.TermUsage> actual, string context)
    {
        Assert.True(
            expected.SequenceEqual(actual) &&
            expected.Select(term => term.Shareable).SequenceEqual(actual.Select(term => term.Shareable)),
            $"{context}: [{Describe(actual)}], the old code gave [{Describe(expected)}].");
    }
}
