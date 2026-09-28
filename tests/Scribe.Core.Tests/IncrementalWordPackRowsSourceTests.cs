using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// IncrementalWordPackRows' wiring on the Word packs tab: a header change (On, AI cleanup) patches the list in place and
/// rebuilds the detail and terms once, and falls back to the old rebuild whenever the list no longer matches; term rows are
/// rebuilt with one Reset; off, the old code runs unchanged.
/// </summary>
public sealed class IncrementalWordPackRowsSourceTests
{
    private static string WordPacks => Read("src", "Scribe.App", "Settings", "SettingsWindow.WordPacks.cs");

    [Fact]
    public void Off_both_header_handlers_run_the_old_rebuild()
    {
        var use = Slice(WordPacks, "private void LibraryUseCheck_Changed(", "private void LibraryAiCheck_Changed(");
        Assert.EndsWith(
            "RefreshWordPackList(_selectedLibraryId); RefreshDictionaryStatus(); UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow); }",
            Collapse(use));

        var ai = Slice(WordPacks, "private void LibraryAiCheck_Changed(", "private bool TryPatchWordPackList()");
        Assert.Contains("RefreshWordPackList(_selectedLibraryId); }", Collapse(ai), StringComparison.Ordinal);

        foreach (var handler in new[] { use, ai })
        {
            Assert.Contains("if (_perfFlags.IsOn(PerfFlags.IncrementalWordPackRows) && TryPatchWordPackList())", handler, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void On_the_semantic_work_still_runs_once_after_the_patch()
    {
        var use = Collapse(Slice(WordPacks, "private void LibraryUseCheck_Changed(", "private void LibraryAiCheck_Changed("));
        Assert.Contains(
            "&& TryPatchWordPackList()) { RefreshDictionaryStatus(); UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow); ShowWordPackCardPageIfStackedAndOpen(); return; }",
            use,
            StringComparison.Ordinal);

        var ai = Collapse(Slice(WordPacks, "private void LibraryAiCheck_Changed(", "private bool TryPatchWordPackList()"));
        Assert.Contains(
            "&& TryPatchWordPackList()) { UpdateLibraryDetail(LibraryGrid.SelectedItem as LibraryRow); ShowWordPackCardPageIfStackedAndOpen(); return; }",
            ai,
            StringComparison.Ordinal);

        // UpdateLibraryDetail rebuilds the term rows, with their statuses, from the draft it reads.
        var detail = Slice(WordPacks, "private void UpdateLibraryDetail(LibraryRow? row)", "private void SyncWordPackHeader(");
        Assert.Contains("RefreshTermRows(library.Content.Id);", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_patch_gives_each_row_what_a_rebuild_would_and_gives_up_when_the_list_changed()
    {
        var patch = Slice(WordPacks, "private bool TryPatchWordPackList()", "private void ShowWordPackCardPageIfStackedAndOpen()");
        Assert.Contains("_renamingLibraryId is not null", patch, StringComparison.Ordinal);
        foreach (var field in new[] { "row.Id, library.Id", "row.Name, library.Name", "row.Category, library.Category", "row.Terms != library.Rows.Count", "row.BuiltIn != library.BuiltIn", "row.Source, LibrarySource(library.BuiltIn)" })
        {
            Assert.Contains(field, patch, StringComparison.Ordinal);
        }

        // The same three values NewLibraryRow gives a new row, the same search state, the same tab summaries.
        var newRow = Slice(WordPacks, "private LibraryRow NewLibraryRow(LibraryContent library) => new()", "};");
        foreach (var (patched, built) in new[]
        {
            ("row.Unsaved = _wordPackWorkspace.UnsavedLibraryIds.Contains(row.Id);", "Unsaved = _wordPackWorkspace?.UnsavedLibraryIds.Contains(library.Id) == true,"),
            ("row.Enabled = _wordPackWorkspace.Draft.LocalState.EnabledIds.Contains(row.Id);", "Enabled = _wordPackWorkspace?.Draft.LocalState.EnabledIds.Contains(library.Id) == true,"),
            ("row.AiCleanup = _wordPackWorkspace.ShowsAiPermission(row.Id);", "AiCleanup = _wordPackWorkspace?.ShowsAiPermission(library.Id) == true,"),
        })
        {
            Assert.Contains(patched, patch, StringComparison.Ordinal);
            Assert.Contains(built, newRow, StringComparison.Ordinal);
        }

        Assert.Contains("ApplySearchState(row);", patch, StringComparison.Ordinal);
        Assert.Contains("UpdateDictionaryTabSummaries();", patch, StringComparison.Ordinal);
        var rebuild = Slice(WordPacks, "private void RefreshWordPackList(string? selectId = null)", "private void LibraryImportButton_Click(");
        Assert.Contains("ApplySearchState(row);", rebuild, StringComparison.Ordinal);
        Assert.Contains("UpdateDictionaryTabSummaries();", rebuild, StringComparison.Ordinal);
    }

    [Fact]
    public void Term_rows_are_built_the_same_way_in_both_paths_and_put_in_with_one_reset_when_on()
    {
        var terms = Slice(WordPacks, "private void RefreshTermRows(string libraryId)", "private void RefreshSearchLinks(");
        const string make = "var view = new LibraryTermRow(row.RowId, row.Row.Values, status, LibraryTermLint.Check(row.Row.Values));";
        Assert.Equal(2, Regex.Matches(terms, Regex.Escape(make)).Count);
        Assert.Equal(2, Regex.Matches(terms, Regex.Escape("view.PropertyChanged += LibraryTermRow_PropertyChanged;")).Count);

        var on = Slice(terms, "if (_perfFlags.IsOn(PerfFlags.IncrementalWordPackRows))", "else");
        Assert.Contains("_libraryTermRows.ReplaceAll(views);", on, StringComparison.Ordinal);
        Assert.Contains("WatchRowsForFooter(views);", on, StringComparison.Ordinal);
        var off = Slice(terms, "else", "_updatingLibraryTerms = false;");
        Assert.Contains("_libraryTermRows.Clear();", off, StringComparison.Ordinal);
        Assert.Contains("_libraryTermRows.Add(view);", off, StringComparison.Ordinal);

        Assert.Contains("private readonly ReplaceableObservableCollection<LibraryTermRow> _libraryTermRows = new();", WordPacks, StringComparison.Ordinal);
        var footer = Read("src", "Scribe.App", "Settings", "SettingsWindow.Footer.cs");
        Assert.Contains("_libraryTermRows.CollectionChanged += RowsChangedForFooter;", footer, StringComparison.Ordinal);
        Assert.Contains("rows[i].PropertyChanged += handler;", Slice(footer, "private void WatchRowsForFooter<T>(", "private void ScheduleFooterRefreshFor("), StringComparison.Ordinal);
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker {start}.");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker {end}.");
        return source[startIndex..endIndex];
    }

    private static string RepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Scribe.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
