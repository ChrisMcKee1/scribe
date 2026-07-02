using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Xunit;

namespace Scribe.Core.Tests;

public class DictionaryCsvTests
{
    [Fact]
    public void Export_then_parse_round_trips_entries()
    {
        var entries = new[]
        {
            new DictionaryEntry(1, "azure", "Azure"),
            new DictionaryEntry(2, "cube flow", "Kubeflow", WholeWord: false),
            new DictionaryEntry(3, "kay eight ess", "K8s", Enabled: false),
        };

        var result = DictionaryCsv.Parse(DictionaryCsv.Export(entries));

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Entries.Count);
        // Imported entries are new rows (Id 0); everything else survives the trip.
        Assert.All(result.Entries, e => Assert.Equal(0, e.Id));
        Assert.Equal(("azure", "Azure", true, true),
            (result.Entries[0].Pattern, result.Entries[0].Replacement, result.Entries[0].WholeWord, result.Entries[0].Enabled));
        Assert.Equal(("cube flow", "Kubeflow", false, true),
            (result.Entries[1].Pattern, result.Entries[1].Replacement, result.Entries[1].WholeWord, result.Entries[1].Enabled));
        Assert.Equal(("kay eight ess", "K8s", true, false),
            (result.Entries[2].Pattern, result.Entries[2].Replacement, result.Entries[2].WholeWord, result.Entries[2].Enabled));
    }

    [Fact]
    public void Quoted_fields_with_commas_and_quotes_round_trip()
    {
        var entries = new[] { new DictionaryEntry(1, "acme, inc", "Acme \"The Best\" Inc.") };

        var result = DictionaryCsv.Parse(DictionaryCsv.Export(entries));

        Assert.Empty(result.Errors);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("acme, inc", entry.Pattern);
        Assert.Equal("Acme \"The Best\" Inc.", entry.Replacement);
    }

    [Fact]
    public void Comments_blank_lines_and_header_are_skipped()
    {
        const string csv =
            """
            # a comment, with a comma
            pattern,replacement,whole_word,enabled

            azure,Azure
            """;

        var result = DictionaryCsv.Parse(csv);

        Assert.Empty(result.Errors);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("azure", entry.Pattern);
        Assert.True(entry.WholeWord);  // optional column defaults
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void The_shipped_template_parses_cleanly()
    {
        var result = DictionaryCsv.Parse(DictionaryCsv.Template);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.Pattern == "cube flow" && e.Replacement == "Kubeflow");
    }

    [Fact]
    public void Flag_columns_accept_yes_no_and_numeric_forms()
    {
        const string csv =
            """
            one,One,no,0
            two,Two,YES,1
            """;

        var result = DictionaryCsv.Parse(csv);

        Assert.Empty(result.Errors);
        Assert.False(result.Entries[0].WholeWord);
        Assert.False(result.Entries[0].Enabled);
        Assert.True(result.Entries[1].WholeWord);
        Assert.True(result.Entries[1].Enabled);
    }

    [Fact]
    public void Bad_rows_are_reported_with_line_numbers_and_good_rows_still_import()
    {
        const string csv =
            """
            azure,Azure
            just-a-pattern
            ,MissingPattern
            foundry,Foundry,maybe
            rebac,ReBAC
            """;

        var result = DictionaryCsv.Parse(csv);

        Assert.Equal(2, result.Entries.Count); // azure + rebac survive
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.StartsWith("Line 2:"));
        Assert.Contains(result.Errors, e => e.StartsWith("Line 3:"));
        Assert.Contains(result.Errors, e => e.StartsWith("Line 4:") && e.Contains("maybe"));
    }

    [Fact]
    public void Empty_or_null_input_yields_nothing()
    {
        Assert.Empty(DictionaryCsv.Parse(null).Entries);
        Assert.Empty(DictionaryCsv.Parse("  \r\n ").Entries);
        Assert.Empty(DictionaryCsv.Parse(string.Empty).Errors);
    }

    [Fact]
    public void Quoted_field_containing_a_line_break_stays_one_record()
    {
        // Spreadsheets can emit embedded line breaks in quoted cells; the reader must not split
        // the record. (The glossary/post-processor flatten the value later; parsing just has to
        // survive it.)
        const string csv = "\"multi\nline\",Value";

        var result = DictionaryCsv.Parse(csv);

        Assert.Empty(result.Errors);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("multi\nline", entry.Pattern);
        Assert.Equal("Value", entry.Replacement);
    }
}
