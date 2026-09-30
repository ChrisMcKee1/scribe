namespace Scribe.Core.Tests;

public sealed class DictionaryWordEditorSourceTests
{
    [Fact]
    public void Dialog_results_are_staged_as_one_batch_without_writing_settings_or_replacing_existing_rows()
    {
        var source = Read("src", "Scribe.App", "Settings", "SettingsWindow.YourWords.cs");
        var start = source.IndexOf("private void OpenDictionaryWordEditor(", StringComparison.Ordinal);
        var end = source.IndexOf("private static DictionaryEntryBuilder.Row DictionaryEditorRow(", start, StringComparison.Ordinal);
        var body = source[start..end];
        Assert.Contains("using (BatchDictionaryStatus())", body, StringComparison.Ordinal);
        Assert.Contains("edited.Pattern = changed.Pattern", body, StringComparison.Ordinal);
        Assert.Contains("edited.Replacement = changed.Replacement", body, StringComparison.Ordinal);
        Assert.DoesNotContain("edited.Enabled =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("edited.WholeWord =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveBundle(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_applySettings(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_rows[", body, StringComparison.Ordinal);
        Assert.Contains("change.Succeeded && change.AddedRows.Count > 0 && !ClearDictionarySearchForNewRow()", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("return change;", StringComparison.Ordinal) <
            body.IndexOf("if (result is null)", StringComparison.Ordinal));
        Assert.True(body.IndexOf("if (_saveInProgress)", StringComparison.Ordinal) <
            body.IndexOf("DictionaryWordWindow.Show(", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_existing_and_new_quick_add_drafts_reach_the_actual_spoken_editor()
    {
        var source = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var start = source.IndexOf("internal void AddDictionaryDraft(", StringComparison.Ordinal);
        var end = source.IndexOf("private async void UpdateCheckButton_Click", start, StringComparison.Ordinal);
        var body = source[start..end];
        Assert.Contains("FocusDictionarySpokenCell(existing);", body, StringComparison.Ordinal);
        Assert.Contains("FocusDictionarySpokenCell(row);", body, StringComparison.Ordinal);
        Assert.Contains("ClearDictionarySearchForNewRow()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryGrid.Columns[1]", body, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    }
}
