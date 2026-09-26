namespace Scribe.Core.Tests;

public sealed class YourWordsReviewTests
{
    [Fact]
    public void Word_pack_settlement_refreshes_the_catalog_snapshot_before_the_hint()
    {
        var code = ReadSettingsWindowCode();
        var callback = Body(code, "void OnWordPacksChanged()");

        Assert.Contains("RefreshWordPackCatalogSnapshot();", callback, StringComparison.Ordinal);
        Assert.True(
            callback.IndexOf("RefreshWordPackCatalogSnapshot();", StringComparison.Ordinal) <
            callback.IndexOf("RefreshDictionaryStatus();", StringComparison.Ordinal));
        Assert.Contains("_wordPackCatalog = _libraryStore.LoadCatalog();", code, StringComparison.Ordinal);
        Assert.Contains("Word pack vocabulary is unavailable right now.", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlap_review_removes_only_reviewed_rows_and_refuses_stale_results()
    {
        var code = ReadSettingsWindowCode();
        var body = Body(code, "private async void ReviewDictionaryOverlaps_Click");

        Assert.Contains("var dictionaryBefore = DictionarySignature();", body, StringComparison.Ordinal);
        Assert.Contains("var wordPacksBefore = LibrarySignature();", body, StringComparison.Ordinal);
        Assert.Contains("new ReviewedOverlap(row, row.Pattern, row.Replacement, row.WholeWord, row.Enabled)", body, StringComparison.Ordinal);
        Assert.Contains("The word list changed. Review overlaps again.", body, StringComparison.Ordinal);
        Assert.Contains("_rows.Remove(item.Row);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveRedundant(entries, report)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_permission_changes_refresh_the_dictionary_hint()
    {
        var body = Body(ReadSettingsWindowCode(), "private void LibraryRow_PropertyChanged");
        var aiBranch = body[body.IndexOf("nameof(LibraryRow.AiCleanup)", StringComparison.Ordinal)..];

        Assert.Contains("_wordPackWorkspace?.SetAiPermission(row.Id, row.AiCleanup);", aiBranch, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(RefreshDictionaryStatus);", aiBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_changes_turn_on_stages_the_post_processing_check_box()
    {
        var body = Body(ReadSettingsWindowCode(), "private void TextChangesNoticeButton_Click");

        Assert.Contains("PostCheck.IsChecked = true;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowPage(SettingsPage.Advanced", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Learn_from_history_has_one_reentry_guard_for_both_buttons()
    {
        var body = Body(ReadSettingsWindowCode(), "private async Task RunDictionarySuggestionAsync");

        Assert.Contains("if (_dictionarySuggestionRunning)", body, StringComparison.Ordinal);
        Assert.Contains("DictionarySuggestButton.IsEnabled = false;", body, StringComparison.Ordinal);
        Assert.Contains("DictionaryEmptyLearnButton.IsEnabled = false;", body, StringComparison.Ordinal);
        Assert.Contains("_dictionarySuggestionRunning = false;", body, StringComparison.Ordinal);
        Assert.Contains("DictionaryEmptyLearnButton.IsEnabled = true;", body, StringComparison.Ordinal);
    }

    private static string ReadSettingsWindowCode()
    {
        var folder = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        return string.Join(
            '\n',
            Directory.GetFiles(folder, "SettingsWindow*.cs")
                .OrderBy(path => Path.GetFileName(path).Equals("SettingsWindow.xaml.cs", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Not found: {signature}");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"No end found for: {signature}");
        return source[start..end];
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
