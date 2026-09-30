namespace Scribe.Core.Tests;

/// <summary>
/// The Your words tab's grid, pinned by source because the app has no tests of its own (review of 5e34468).
/// </summary>
public sealed class YourWordsGridSourceTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void The_removal_placeholder_is_drawn_over_the_cell_and_never_edited()
    {
        // The placeholder was a style setter on the column's TextBlock, which DataGridTextColumn's local Text binding
        // overrides, so a removal rule showed an empty cell; before that, a two-way ReplacementDisplay binding let the
        // placeholder be stored as the replacement.
        var xaml = File.ReadAllText(Path.Combine(Root, "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var column = xaml.IndexOf("<DataGridTextColumn x:Name=\"DictionaryWrittenColumn\" Header=\"Scribe writes\" Binding=\"{Binding Replacement, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal);
        Assert.True(column >= 0, "Scribe writes binds two-way to Replacement.");
        var end = xaml.IndexOf("</DataGridTextColumn>", column, StringComparison.Ordinal);
        var body = xaml[column..end];
        Assert.Contains("CellStyle=\"{StaticResource DictionaryWrittenCell}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("(removes these words)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplacementDisplay", xaml, StringComparison.Ordinal);

        var style = xaml.IndexOf("x:Key=\"DictionaryWrittenCell\"", StringComparison.Ordinal);
        Assert.True(style >= 0);
        var styleBody = xaml[style..xaml.IndexOf("</Style>", style, StringComparison.Ordinal)];
        Assert.Contains("<Condition Binding=\"{Binding IsEditing, RelativeSource={RelativeSource Self}}\" Value=\"False\"/>", styleBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_the_search_for_a_new_row_commits_an_open_edit_first()
    {
        // Suggestions arriving while a filtered row was being edited threw on the view refresh and were lost.
        var code = File.ReadAllText(Path.Combine(Root, "src", "Scribe.App", "Settings", "SettingsWindow.YourWords.cs"));
        var clear = code[code.IndexOf("private bool ClearDictionarySearchForNewRow()", StringComparison.Ordinal)..];
        clear = clear[..clear.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.True(
            clear.IndexOf("DictionaryGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true)", StringComparison.Ordinal) <
            clear.IndexOf("_dictionaryView?.Refresh();", StringComparison.Ordinal),
            "An open row edit must be committed before the view refreshes.");
        Assert.Contains("change.AddedRows.Count > 0 && !ClearDictionarySearchForNewRow()", code, StringComparison.Ordinal);
    }

    private static string FindRoot()
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