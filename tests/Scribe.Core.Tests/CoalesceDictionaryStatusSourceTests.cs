using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// CoalesceDictionaryStatus's wiring on the Dictionary page: the scheduler exists only with the flag on, every refresh the
/// page asks for goes through one of three helpers whose flag-off branch is the old call, and every bulk edit is a batch.
/// </summary>
public sealed class CoalesceDictionaryStatusSourceTests
{
    private static string YourWords => Read("src", "Scribe.App", "Settings", "SettingsWindow.YourWords.cs");

    [Fact]
    public void The_scheduler_exists_only_with_the_flag_on_and_closes_with_the_window()
    {
        var init = Slice(YourWords, "private void InitializeDictionaryGrid()", "private void SetDictionaryEditable(");
        Assert.Contains(
            "if (_perfFlags.IsOn(PerfFlags.CoalesceDictionaryStatus))\r\n        {\r\n            _dictionaryStatus = new StatusRefreshScheduler(RefreshDictionaryStatus, work => Dispatcher.BeginInvoke(work));",
            init.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(YourWords, @"_dictionaryStatus = new StatusRefreshScheduler\("));

        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var onClosed = Slice(window, "private void OnClosed(object? sender, EventArgs e)", "// --- Themed dialogs");
        Assert.Contains("_dictionaryStatus?.Close();", onClosed, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_every_request_is_the_old_call()
    {
        var queue = Slice(YourWords, "private void QueueDictionaryStatusRefresh()", "private void RefreshDictionaryStatusForRowChange()");
        Assert.Contains("scheduler.Request();", queue, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(RefreshDictionaryStatus);", queue, StringComparison.Ordinal);

        var now = Slice(YourWords, "private void RefreshDictionaryStatusForRowChange()", "private IDisposable? BatchDictionaryStatus()");
        Assert.Contains("scheduler.RefreshNow();", now, StringComparison.Ordinal);
        Assert.Contains("RefreshDictionaryStatus();", now, StringComparison.Ordinal);

        Assert.Contains("private IDisposable? BatchDictionaryStatus() => _dictionaryStatus?.Batch();", YourWords, StringComparison.Ordinal);

        // The old posted refresh survives only as that fallback; the row and cell handlers go through the helper.
        Assert.Single(Regex.Matches(YourWords, @"Dispatcher\.BeginInvoke\(RefreshDictionaryStatus\)"));
        Assert.Contains("DictionaryGrid.CellEditEnding += (_, _) => QueueDictionaryStatusRefresh();", YourWords, StringComparison.Ordinal);
        var rowChanged = Slice(YourWords, "private void DictionaryRow_PropertyChanged(", "private void QueueDictionaryStatusRefresh()");
        Assert.Contains("QueueDictionaryStatusRefresh();", rowChanged, StringComparison.Ordinal);
        Assert.Contains("e.PropertyName.StartsWith(\"Coverage\", StringComparison.Ordinal)", rowChanged, StringComparison.Ordinal);
        var collection = Slice(YourWords, "private void DictionaryRows_CollectionChanged(", "private void DictionaryRow_PropertyChanged(");
        Assert.Contains("RefreshDictionaryStatusForRowChange();", collection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async void ReviewDictionaryOverlaps_Click(", "private static bool IsRedundantWithWordPack(")]
    [InlineData("private void AddSuggestionRows(", "// --- Dictionary cleanup")]
    [InlineData("private async Task ApplyCleanupAsync(", "// --- Dictionary CSV import / export")]
    [InlineData("private (int Added, int Updated, int Unchanged) MergeImportedEntries(", "private sealed class RedundantDictionaryRowKeyComparer")]
    public void Every_bulk_edit_is_one_batch(string start, string end)
    {
        var method = Slice(YourWords, start, end);

        Assert.Single(Regex.Matches(method, @"BatchDictionaryStatus\(\)"));
        Assert.DoesNotMatch(new Regex(@"(?<![.\w])RefreshDictionaryStatus\(\);"), method);
    }

    [Fact]
    public void Suggestions_refresh_before_the_grid_selects_and_scrolls_to_the_first_new_row()
    {
        var method = Slice(YourWords, "private void AddSuggestionRows(", "// --- Dictionary cleanup");
        var batchEnd = method.IndexOf("first ??= row;", StringComparison.Ordinal);
        var select = method.IndexOf("DictionaryGrid.SelectedItem = first;", StringComparison.Ordinal);

        Assert.True(batchEnd > 0 && select > batchEnd);
        Assert.Contains("}\r\n        }\r\n\r\n        if (first is not null)", method.ReplaceLineEndings("\r\n"), StringComparison.Ordinal);
    }

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
