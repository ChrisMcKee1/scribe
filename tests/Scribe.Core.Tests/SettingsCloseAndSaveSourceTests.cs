namespace Scribe.Core.Tests;

/// <summary>
/// The Settings window's close and Save paths, pinned by source because the app has no tests of its own. Each test names
/// the review finding it keeps fixed (files\impl\review-phase1.md in the redesign session).
/// </summary>
public sealed class SettingsCloseAndSaveSourceTests
{
    private static readonly string Window = ReadWindow();

    [Fact]
    public void A_native_close_never_closes_while_a_Save_runs_or_a_close_is_under_way()
    {
        // Item 4: revert the staged changes while a word pack Save prepares, then Alt+F4: the window closed at once and the
        // running Save still committed what it had captured.
        var closing = Body(Window, "protected override void OnClosing(CancelEventArgs e)");
        Assert.Contains("if (!decision.Ask && !_saveInProgress && _closeOperation is null)", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_close_commits_every_pending_editor_before_it_decides()
    {
        // Item 14: type 7 over 90 in a custom duration and press Alt+F4: only grid edits were committed, so the close read
        // 90 and closed without asking.
        var closing = Body(Window, "protected override void OnClosing(CancelEventArgs e)");
        var run = Body(Window, "private async Task RunCloseAsync(CloseTrigger trigger)");
        foreach (var body in new[] { closing, run })
        {
            Assert.True(
                body.IndexOf("CommitPendingEditorValues();", StringComparison.Ordinal) is >= 0 and var commit &&
                commit < body.IndexOf("ShouldAskBeforeClose(", StringComparison.Ordinal),
                "Pending editors must be committed before the close decision.");
            Assert.DoesNotContain("CommitPendingGridEdits();", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Try_dictation_s_Save_refreshes_the_footer_when_it_ends()
    {
        // Item 11: the footer kept saying there were unsaved changes after Try dictation's Save now.
        var save = Body(Window, "private async void TryDictationSaveNow_Click(");
        var cleanup = save[save.IndexOf("finally", StringComparison.Ordinal)..];
        Assert.Contains("ScheduleFooterRefresh();", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Save_stops_when_the_word_pack_draft_moved_on_after_the_preflight()
    {
        // Item 13: a word pack edited while the Save waited (for Start with Windows, or an earlier save) was stored and
        // marked saved, though it was never validated.
        var save = Body(Window, "private async Task<bool> TrySaveAsync()");
        var check = save.IndexOf("if (WordPackDraftMovedSincePreflight(preflight))", StringComparison.Ordinal);
        Assert.True(check >= 0, "The Save must compare the workspace with the preflight's.");
        Assert.True(check < save.IndexOf("_wordPackSaveProtocol.SaveAsync(", StringComparison.Ordinal));
        Assert.Contains("_wordPackWorkspace, _wordPackWorkspace?.EditRevision);", Window, StringComparison.Ordinal);

        // The first load arriving during the wait is not an edit.
        var moved = Body(Window, "private bool WordPackDraftMovedSincePreflight(");
        Assert.Contains("return current is not null && (current.EditRevision != 0 || current.HasUnsavedChanges);", moved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Save_never_writes_the_startup_preference_from_its_captured_draft()
    {
        // Item 15: turn on Start with Windows (applied at once), then Save another setting: the draft captured before the
        // switch turned it back off.
        var copy = Body(Window, "private static void CopySettings(AppSettings source, AppSettings target)");
        Assert.Contains("property.Name != nameof(AppSettings.LaunchOnLogin)", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_baselines_start_at_the_load_and_move_to_the_whole_stored_submission()
    {
        // Item 7: snippets and profiles looked unsaved as soon as Settings opened, and a row deleted while a Save waited
        // vanished from the baseline, so the footer said everything was saved.
        Assert.Contains("_loadedSnippetRows = LoadedSnippetDraftRowsFromRows();\n        _snippetLoad.Publish(ticket, SnippetSignature());", Window, StringComparison.Ordinal);
        Assert.Contains("_loadedProfileRows = LoadedProfileDraftRowsFromRows();\n        ProfileList.ItemsSource = _profileRows;", Window, StringComparison.Ordinal);
        Assert.Contains("_loadedDictionaryRows = [.. submission.Select(", Body(Window, "private void MarkDictionaryRowsSaved("), StringComparison.Ordinal);
        Assert.Contains("_loadedSnippetRows = [.. submission.Select(", Body(Window, "private void MarkSnippetRowsSaved("), StringComparison.Ordinal);
        Assert.Contains("_loadedProfileRows = [.. submission.Select(", Body(Window, "private void MarkProfileRowsSaved("), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dictionary_write_outside_Save_advances_only_the_row_it_stored()
    {
        // Item 7: a quick add or learned word while other rows were unsaved left the stored row unsaved, and a quick add
        // on a clean window rebuilt the whole baseline from the rows shown.
        foreach (var signature in new[] { "public DictionaryEntry ApplyQuickDictionaryEntry(", "public IReadOnlyList<DictionaryEntry> PersistLearnedDictionaryEntries(" })
        {
            var body = Body(Window, signature);
            Assert.Contains("AdvanceDictionaryBaseline(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("_loadedDictionaryRows = LoadedDictionaryDraftRowsFromRows();", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_dictionary_issue_puts_focus_in_the_offending_cell_s_editor()
    {
        // Item 10: the message pointed at a row, but focus stayed on the grid.
        var show = Body(Window, "private void ShowValidationIssue(ValidationIssue issue)");
        Assert.Contains("FocusDictionarySpokenCell(row);", show, StringComparison.Ordinal);

        // A warning lets the Save continue; opening an editor then would read as a change made while saving.
        Assert.True(
            show.IndexOf("if (issue.Severity == ValidationSeverity.Blocking)", StringComparison.Ordinal) <
            show.IndexOf("FocusDictionarySpokenCell(row);", StringComparison.Ordinal));
        var focus = Body(Window, "private void FocusDictionarySpokenCell(DictionaryRow row)");
        Assert.Contains("DataGridTextEdit.Begin(DictionaryGrid, row, column, selectAll: true)", focus, StringComparison.Ordinal);
    }

    // A method's text, from its signature to the closing brace at its indentation.
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Not found: {signature}");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"No end found for: {signature}");
        return source[start..end];
    }

    private static string ReadWindow()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var folder = Path.Combine(root.FullName, "src", "Scribe.App", "Settings");
        return string.Join(
            '\n',
            Directory.GetFiles(folder, "SettingsWindow*.cs")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => File.ReadAllText(path).ReplaceLineEndings("\n")));
    }
}
