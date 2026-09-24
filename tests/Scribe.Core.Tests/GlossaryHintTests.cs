using System.Globalization;
using Scribe.Core.Cleanup;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The dictionary page's status line says what AI cleanup receives. It used to count only the page's
/// own rows and tell a cloud user that "all of them" were sent, when the enabled libraries go too and
/// the list stops at a budget. The count now comes from the glossary's own selection, so these tests
/// hold it to <see cref="CleanupPrompt.BuildGlossary"/>.
/// </summary>
public sealed class GlossaryHintTests
{
    private static DictionaryEntryBuilder.Row Row(string pattern, string replacement, bool enabled = true) =>
        new(0, pattern, replacement, WholeWord: true, enabled);

    private static string Describe(
        IReadOnlyList<DictionaryEntryBuilder.Row> rows,
        IReadOnlyList<DictionaryEntry> libraries,
        CleanupProvider provider = CleanupProvider.AzureFoundry,
        CleanupPromptStyle style = CleanupPromptStyle.Auto,
        bool aiCleanupOn = true) =>
        GlossaryHint.Describe(new GlossaryHint.Input(rows, libraries, aiCleanupOn, provider, style));

    private static int CountLines(string glossary) =>
        glossary.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));

    [Fact]
    public void With_ai_cleanup_off_it_only_counts_the_entries()
    {
        var text = Describe([Row("azure", "Azure"), Row("um", string.Empty)], [], aiCleanupOn: false);

        Assert.Equal("1 of 2 entries enabled.", text);
    }

    [Fact]
    public void A_remote_provider_is_said_to_receive_every_term_libraries_included_whether_or_not_it_is_said()
    {
        var text = Describe(
            [Row("azure", "Azure"), Row("a p i m", "APIM")],
            [DictionaryEntry.New("azure", "AZURE-from-library"), DictionaryEntry.New("cosmos db", "Cosmos DB"), DictionaryEntry.New("k eight s", "K8s")]);

        Assert.Equal(
            "2 of 2 entries enabled plus 2 from enabled libraries. All of them are replaced locally. Your AI provider " +
            "receives all 4 terms as vocabulary with every cleanup request, whether or not the dictation mentions them.",
            text);
    }

    [Fact]
    public void An_on_device_list_cut_to_the_local_budget_quotes_the_count_the_glossary_is_built_with()
    {
        var rows = Enumerable.Range(0, 150).Select(i => Row($"spoken {i}", $"Term{i}")).ToList();
        var libraries = Enumerable.Range(0, 400).Select(i => DictionaryEntry.New($"library {i}", $"Library{i}")).ToList();

        var text = Describe(rows, libraries, CleanupProvider.FoundryLocal);

        var effective = DictionaryLibraryComposer.Merge(DictionaryEntryBuilder.Build(rows).Entries, libraries);
        var sent = CountLines(CleanupPrompt.BuildGlossary(effective, CleanupPrompt.MaxGlossaryTermsLocal));
        Assert.Equal(CleanupPrompt.MaxGlossaryTermsLocal, sent);
        Assert.Contains($"The on-device model receives the first {sent} of 550 terms as vocabulary", text, StringComparison.Ordinal);
        Assert.Contains("Your own entries come first", text, StringComparison.Ordinal);
        Assert.EndsWith(
            $"The Local prompt style stops the list at {CleanupPrompt.MaxGlossaryTermsLocal} terms so it fits a small model's context.",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cloud_list_past_its_size_budget_says_where_it_stops()
    {
        var rows = Enumerable.Range(0, 3000)
            .Select(i => Row($"spoken phrase number {i}", new string('x', 60) + i))
            .ToList();

        var text = Describe(rows, [], CleanupProvider.OpenAiCompatible);

        var sent = CountLines(CleanupPrompt.BuildGlossary(DictionaryEntryBuilder.Build(rows).Entries));
        Assert.True(sent < 3000, "The fixture must outgrow the character budget, or it proves nothing.");
        Assert.Contains(
            $"Your AI provider receives the first {sent.ToString("N0", CultureInfo.InvariantCulture)} of 3,000 terms",
            text, StringComparison.Ordinal);
        Assert.EndsWith("The list stops at 5,000 terms or 24,000 characters.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_row_neither_counts_nor_keeps_a_library_term_out_of_the_glossary()
    {
        // Dictation reads only enabled entries, so a switched-off personal entry does not shadow the
        // library term with the same spoken form.
        var text = Describe([Row("azure", "AZURE-mine", enabled: false)], [DictionaryEntry.New("azure", "Azure")]);

        Assert.StartsWith("0 of 1 entries enabled plus 1 from enabled libraries.", text, StringComparison.Ordinal);
        Assert.Contains("Your AI provider receives that term as vocabulary", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_send_is_said_plainly()
    {
        Assert.Equal("0 of 0 entries enabled. AI cleanup receives no vocabulary.", Describe([], []));
        Assert.Equal(
            "0 of 1 entries enabled. All of them are replaced locally. AI cleanup receives no vocabulary.",
            Describe([Row("um", string.Empty)], []));
    }

    [Fact]
    public void The_count_is_the_glossary_the_prompt_is_built_from()
    {
        var entries = new List<DictionaryEntry>
        {
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("azure", "Azure"),
            DictionaryEntry.New("a z u r e", "Azure"),
            DictionaryEntry.New("dropped", string.Empty),
            DictionaryEntry.New("off", "Off") with { Enabled = false },
            DictionaryEntry.New("quoted", "\"\"\""),
        };
        entries.AddRange(Enumerable.Range(0, 200).Select(i => DictionaryEntry.New($"spoken {i}", $"Term{i}")));

        foreach (var budget in new[] { CleanupPrompt.MaxGlossaryTermsLocal, CleanupPrompt.MaxGlossaryTermsCloud })
        {
            var count = CleanupPrompt.CountGlossary(entries, budget);

            Assert.Equal(CountLines(CleanupPrompt.BuildGlossary(entries, budget)), count.Included);
            Assert.Equal(202, count.Eligible);
        }

        Assert.Equal(new GlossaryCount(0, 0), CleanupPrompt.CountGlossary(null));
        Assert.Equal(new GlossaryCount(0, 202), CleanupPrompt.CountGlossary(entries, 0));
    }
}
