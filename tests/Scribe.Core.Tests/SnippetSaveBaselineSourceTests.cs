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
        var window = Read("SettingsWindow.xaml.cs");
        var snippets = Read("SettingsWindow.Snippets.cs");

        // The submission is captured where the Save builds the list it stores, and adopted right after it is marked saved.
        var built = window.IndexOf("snippets = BuildSnippets(out duplicateSnippet);", StringComparison.Ordinal);
        var captured = window.IndexOf("snippetSubmission = CaptureSnippetSubmission();", StringComparison.Ordinal);
        Assert.True(built >= 0 && captured > built, "The Save does not capture its snippet submission where it builds the list.");
        Assert.Contains(
            "_snippetLoad.MarkSaved(snippetSignature);\r\n                MarkSnippetRowsSaved(snippetSubmission ?? []);",
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

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings", file));

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
