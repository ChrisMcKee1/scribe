using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class DictionaryImportSummaryTests
{
    [Fact]
    public void Skipped_rows_with_changes_say_nothing_changes_until_save()
    {
        var summary = DictionaryImportSummaryBuilder.Build(2, 1, 0, ["Line 4: bad"]);

        Assert.Equal("Some rows couldn't be imported", summary.Title);
        Assert.Equal("2 added, 1 updated, 1 couldn't be read. Nothing changes until you save.\n\nLine 4: bad", summary.Body);
    }

    [Fact]
    public void Skipped_rows_without_changes_omit_save_sentence()
    {
        var summary = DictionaryImportSummaryBuilder.Build(0, 0, 0, ["Line 4: bad"]);

        Assert.Equal("0 added, 0 updated, 1 couldn't be read.\n\nLine 4: bad", summary.Body);
    }

    [Fact]
    public void Clean_import_with_changes_names_counts_and_save()
    {
        var summary = DictionaryImportSummaryBuilder.Build(1, 2, 3, []);

        Assert.Equal("Dictionary imported", summary.Title);
        Assert.Equal("1 added, 2 updated. 3 already up to date. Nothing changes until you save.", summary.Body);
    }

    [Fact]
    public void Clean_import_without_changes_omits_save_sentence()
    {
        var summary = DictionaryImportSummaryBuilder.Build(0, 0, 2, []);

        Assert.Equal("0 added, 0 updated. 2 already up to date.", summary.Body);
    }
}
