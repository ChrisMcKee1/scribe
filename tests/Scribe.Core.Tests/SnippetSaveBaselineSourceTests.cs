namespace Scribe.Core.Tests;

// The Voice snippets page after a Save: the rows take what the Save stored as their baseline in memory, and nothing reads
// the snippets back from storage. A read after a committed Save once cleared the rows before a fallible read (a failure
// left the list empty, so a retried Save deleted every snippet), and then, moved off the UI thread, replaced edits the user
// made while it ran (both found in the P5-SP reviews).
public sealed class SnippetSaveBaselineSourceTests
{
    [Fact]
    public void A_save_takes_its_submitted_snippet_rows_as_the_baseline_without_reading_storage()
    {
        var window = ReadSettingsWindowSources();
        var snippets = Read("SettingsWindow.Snippets.cs");

        // The submission is what the builder stored, taken where the Save builds the list, and adopted right after it is
        // marked saved.
        var built = window.IndexOf("snippets = BuildSnippets(out duplicateSnippet, out var submitted);", StringComparison.Ordinal);
        var captured = window.IndexOf("snippetSubmission = submitted;", StringComparison.Ordinal);
        Assert.True(built >= 0 && captured > built, "The Save does not take its snippet submission from the builder.");
        Assert.Contains(
            "_snippetLoad.MarkSaved(snippetSignature);\r\n                MarkSnippetRowsSaved(snippetSubmission);",
            window.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);

        // No read of storage follows a Save.
        Assert.DoesNotContain("RefreshSnippetRowsFromStorage", window + snippets, StringComparison.Ordinal);
        Assert.DoesNotContain("StartSnippetRowsRefreshAfterSave", window + snippets, StringComparison.Ordinal);
        var mark = Body(snippets, "private void MarkSnippetRowsSaved(");
        Assert.DoesNotContain("_snippets.", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("await", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("_snippetRows.Clear()", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_row_keeps_its_stored_phrase_and_saves_complete_text_verbatim()
    {
        var snippets = Read("SettingsWindow.Snippets.cs");
        var toRow = Body(snippets, "private static SnippetBuilder.Row ToBuilderRow(");
        Assert.Contains("SettingsDraftValidator.IsUnchanged(ToDraftRow(row))", toRow, StringComparison.Ordinal);
        Assert.Contains("var template = IsIncomplete(row) ? row.LoadedTemplate : row.Template;", toRow, StringComparison.Ordinal);
        Assert.Contains("row.LoadedPhrase, template, row.LoadedEnabled, KeepAsStored: true", toRow, StringComparison.Ordinal);

        // A complete row goes to the builder with its current text, which the builder stores verbatim.
        var built = Scribe.Core.Settings.SnippetBuilder.Build([new(4, "sig", "\r\nRegards\r\n", true)]);
        Assert.Equal("\r\nRegards\r\n", Assert.Single(built.Snippets).Template);
    }

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", file));

    private static string ReadSettingsWindowSources() =>
        string.Join("\n", Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings"), "SettingsWindow*.cs")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText));

    // A member's text, from its signature to the closing brace at its own indentation.
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is missing.");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{signature} has no end.");
        return code[start..end];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Scribe.slnx was not found.");
    }
}
