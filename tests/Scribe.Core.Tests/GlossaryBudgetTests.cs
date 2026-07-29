using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Covers the glossary budget. The cap used to be a single number (80) applied to every provider,
/// which silently dropped the user's own entries before they ever reached a cloud model that had
/// room for thousands. The budget now follows where cleanup runs, and is bounded by size rather
/// than by an entry count with no relationship to cost.
/// </summary>
public sealed class GlossaryBudgetTests
{
    private static DictionaryEntry[] Entries(int count, string prefix = "Term") =>
        [.. Enumerable.Range(0, count).Select(i => DictionaryEntry.New($"spoken {prefix} {i}", $"{prefix}{i}"))];

    private static int CountLines(string s) => s.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));

    [Fact]
    public void Cloud_budget_includes_every_entry_when_it_fits()
    {
        var glossary = CleanupPrompt.BuildGlossary(Entries(400), CleanupPrompt.MaxGlossaryTermsCloud);

        Assert.Equal(400, CountLines(glossary));
    }

    [Fact]
    public void Local_budget_stays_short_so_vocabulary_cannot_crowd_out_the_transcript()
    {
        var glossary = CleanupPrompt.BuildGlossary(Entries(400), CleanupPrompt.MaxGlossaryTermsLocal);

        Assert.Equal(CleanupPrompt.MaxGlossaryTermsLocal, CountLines(glossary));
    }

    [Fact]
    public void Earlier_entries_survive_truncation()
    {
        // The caller puts the user's own dictionary first precisely because those are the terms a
        // model cannot infer. If truncation ever dropped from the front, that guarantee is gone.
        var glossary = CleanupPrompt.BuildGlossary(Entries(400), CleanupPrompt.MaxGlossaryTermsLocal);

        Assert.Contains("Term0", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain("Term399", glossary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_budget_bounds_the_block_even_under_the_term_limit()
    {
        // 5,000 long entries are within MaxGlossaryTermsCloud but would blow up every request, so
        // the size budget has to be the thing that actually stops it.
        var longEntries = Enumerable.Range(0, 5000)
            .Select(i => DictionaryEntry.New($"spoken phrase number {i} with padding", new string('x', 80) + i))
            .ToArray();

        var glossary = CleanupPrompt.BuildGlossary(longEntries, CleanupPrompt.MaxGlossaryTermsCloud);

        Assert.True(glossary.Length < 30_000, $"Glossary grew to {glossary.Length} characters.");
        Assert.True(CountLines(glossary) < 5000);
        Assert.Contains("- " + new string('x', 80) + "0", glossary, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_entries_never_reach_the_prompt()
    {
        var entries = new[]
        {
            DictionaryEntry.New("spoken one", "One"),
            DictionaryEntry.New("spoken two", "Two") with { Enabled = false },
        };

        var glossary = CleanupPrompt.BuildGlossary(entries, CleanupPrompt.MaxGlossaryTermsCloud);

        Assert.Contains("One", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain("Two", glossary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_or_negative_budget_yields_nothing_rather_than_throwing()
    {
        Assert.Equal(string.Empty, CleanupPrompt.BuildGlossary(Entries(10), 0));
        Assert.Equal(string.Empty, CleanupPrompt.BuildGlossary(Entries(10), -5));
    }

    [Theory]
    [InlineData(CleanupProvider.FoundryLocal, CleanupPromptStyle.Local)]
    [InlineData(CleanupProvider.AzureFoundry, CleanupPromptStyle.Frontier)]
    [InlineData(CleanupProvider.OpenAiCompatible, CleanupPromptStyle.Frontier)]
    public void Prompt_style_resolution_is_what_selects_the_budget(
        CleanupProvider provider, CleanupPromptStyle expected)
    {
        // The budget is chosen from the resolved style, so this mapping is load-bearing: get it wrong
        // and a cloud model silently loses vocabulary, or a 1B model gets a 6,000-token glossary.
        Assert.Equal(expected, CleanupPrompt.ResolvePromptStyle(CleanupPromptStyle.Auto, provider));
    }
}
