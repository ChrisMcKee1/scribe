using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Tests;

/// <summary>
/// A dictionary replacement that spans lines or runs past the glossary's 100-character cap is a template
/// (a signature, an address, a footer), not vocabulary. The glossary used to send its first hundred
/// characters with every AI cleanup request; it now leaves such entries out, exactly the ones the usage
/// insight leaves out, by one shared rule. The dictionary still applies them on this PC. None of the
/// shipped vocabulary is a template, so the glossary the eval harness measures is unchanged.
/// </summary>
public sealed class GlossaryVocabularyTests
{
    public static TheoryData<string?, bool> Replacements => new()
    {
        { "Kubernetes", true },
        { "GitHub Actions", true },
        { "tab\tseparated", true },
        { "two\nlines", false },
        { "two\rlines", false },
        { "two\r\nlines", false },
        { "vertical\u000Btab", false },
        { "form\u000Cfeed", false },
        { "next\u0085line", false },
        { "line\u2028separator", false },
        { "paragraph\u2029separator", false },
        { "trailing break\n", false },
        { "", false },
        { "   ", false },
        { null, false },
    };

    [Theory]
    [MemberData(nameof(Replacements))]
    public void Only_a_single_line_replacement_is_vocabulary(string? replacement, bool vocabulary)
    {
        Assert.Equal(vocabulary, CleanupPrompt.IsVocabularyReplacement(replacement));
    }

    [Fact]
    public void The_length_limit_is_the_glossary_term_cap_counted_before_any_trimming()
    {
        var cap = CleanupPrompt.MaxGlossaryTermChars;

        Assert.True(CleanupPrompt.IsVocabularyReplacement(new string('a', cap)));
        Assert.False(CleanupPrompt.IsVocabularyReplacement(new string('a', cap + 1)));
        Assert.False(CleanupPrompt.IsVocabularyReplacement("Fabrikam" + new string(' ', cap)));
        Assert.False(CleanupPrompt.IsVocabularyReplacement(new string(' ', cap) + "Fabrikam"));
    }

    public static TheoryData<string> Templates => new()
    {
        "Best regards,\nChris McKee\nPrincipal Architect",
        "Best regards,\r\nChris McKee",
        "Sent from Scribe. " + new string('x', 90),
        "Principal Architect\n",
        "Fabrikam" + new string(' ', 93),
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void A_template_replacement_is_left_out_of_the_glossary_and_its_count(string template)
    {
        var entries = new[]
        {
            DictionaryEntry.New("kubernetes", "Kubernetes"),
            DictionaryEntry.New("sign off", template),
            DictionaryEntry.New("a p i m", "APIM"),
        };

        var glossary = CleanupPrompt.BuildGlossary(entries);

        Assert.Contains("- Kubernetes", glossary, StringComparison.Ordinal);
        Assert.Contains("- APIM (transcribed as \"a p i m\")", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain("sign off", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain(template.Trim().Split('\n', '\r')[0].Trim(), glossary, StringComparison.Ordinal);
        Assert.Equal(new GlossaryCount(2, 2), CleanupPrompt.CountGlossary(entries));
    }

    [Fact]
    public void The_same_rule_decides_the_glossary_and_the_usage_insight()
    {
        // One label is a template; the other is vocabulary. Both features must agree on which is which.
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var entries = new[] { DictionaryEntry.New("sign off", "Best regards,\nChris"), DictionaryEntry.New("kube", "Kubernetes") };
        var snapshot = UsageAnalyzer.Compute(
            [
                new HistoryEntry(1, now, "sign off and kube", 1_000, 100, CleanupMilliseconds: null, TargetApp: null),
                new HistoryEntry(2, now.AddHours(-1), "kube and sign off", 1_000, 100, CleanupMilliseconds: null, TargetApp: null),
            ],
            entries,
            now.AddDays(-1),
            now,
            TimeZoneInfo.Utc);

        foreach (var entry in entries)
        {
            var inGlossary = CleanupPrompt.BuildGlossary([entry]).Length > 0;
            var shared = snapshot.Terms.Single(term => term.Text == entry.Replacement.Trim()).Shareable;
            Assert.Equal(inGlossary, shared);
        }
    }

    [Fact]
    public void The_rule_only_drops_templates_and_leaves_every_other_line_as_it_was()
    {
        // Templates interleaved with vocabulary: the glossary is exactly the one those entries would build
        // without the templates in the dictionary at all, so no budget or de-duplication moves for the rest.
        var entries = new List<DictionaryEntry>();
        for (var i = 0; i < 300; i++)
        {
            entries.Add(DictionaryEntry.New($"spoken {i}", $"Term{i}"));
            if (i % 7 == 0)
            {
                entries.Add(DictionaryEntry.New($"template {i}", $"Line one {i}\nLine two"));
            }
        }

        var withoutTemplates = entries.Where(entry => CleanupPrompt.IsVocabularyReplacement(entry.Replacement)).ToList();
        Assert.True(withoutTemplates.Count < entries.Count, "The fixture must contain templates, or it proves nothing.");
        foreach (var budget in new[] { CleanupPrompt.MaxGlossaryTermsLocal, CleanupPrompt.MaxGlossaryTermsCloud })
        {
            Assert.Equal(CleanupPrompt.BuildGlossary(withoutTemplates, budget), CleanupPrompt.BuildGlossary(entries, budget));
        }
    }

    [Fact]
    public void Every_shipped_term_is_vocabulary_so_the_benchmark_glossary_is_unchanged()
    {
        // The eval harness builds its glossary from the shipped libraries (--glossary-libraries), every
        // entry at the cloud budget, and a new install seeds DefaultVocabulary into the dictionary. None of
        // them is a template, so by the test above the rule leaves their glossary byte for byte as it was.
        // (An empty replacement is a removal rule, which the glossary never carried either way.)
        var shipped = BuiltInDictionaryLibraries.All
            .SelectMany(library => library.Entries.Select(entry => (Source: library.Id, entry.Replacement)))
            .Concat(DefaultVocabulary.Entries.Select(entry => (Source: "default vocabulary", entry.Replacement)))
            .ToList();

        Assert.NotEmpty(shipped);
        Assert.All(shipped, term => Assert.True(
            string.IsNullOrWhiteSpace(term.Replacement) || CleanupPrompt.IsVocabularyReplacement(term.Replacement),
            $"{term.Source} ships a template replacement of {term.Replacement.Length} characters."));
    }
}
