using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// LeanFooterRefresh's wiring: off, the footer's window-wide handlers and row subscriptions are the old ones; on, events
/// from controls Save never reads schedule nothing, and nothing the dirty check reads is one of those controls.
/// </summary>
public sealed class LeanFooterRefreshSourceTests
{
    private static readonly string[] IgnoredControls =
    [
        "SettingsSearchBox", "DictionarySearchBox", "LibrarySearchBox", "HistorySearchBox", "PlaygroundInput",
        "NavList", "HistoryGrid", "DictionaryTabs", "UsagePeriodBox",
    ];

    private static string Footer => Read("src", "Scribe.App", "Settings", "SettingsWindow.Footer.cs");

    [Fact]
    public void Off_the_window_wide_handlers_are_the_old_five()
    {
        var init = Slice(Footer, "private void InitializeFooterAndClose()", "private void RowsChangedForFooter(");
        var off = Slice(init, "else", "_rows.CollectionChanged += RowsChangedForFooter;");

        foreach (var line in new[]
        {
            "AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleFooterRefresh()));",
            "AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));",
            "AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleFooterRefresh()));",
            "AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));",
            "AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleFooterRefresh()));",
        })
        {
            Assert.Contains(line, off, StringComparison.Ordinal);
        }

        Assert.Contains("if (_perfFlags.IsOn(PerfFlags.LeanFooterRefresh))", init, StringComparison.Ordinal);
        var rows = Slice(Footer, "private void RowsChangedForFooter(", "private void RowChangedForFooter(");
        Assert.Contains("foreach (var item in e.OldItems?.OfType<INotifyPropertyChanged>() ?? [])", rows, StringComparison.Ordinal);
        Assert.Contains("foreach (var item in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ignored_controls_are_exactly_the_listed_ones_and_only_text_and_selection_events_consult_them()
    {
        var init = Slice(Footer, "_footerIgnoredSources =", "];");
        var listed = Regex.Matches(init, @"\b[A-Z]\w+\b").Select(m => m.Value).ToList();
        Assert.Equal(IgnoredControls.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));

        var on = Slice(Footer, "if (_perfFlags.IsOn(PerfFlags.LeanFooterRefresh))", "else");
        Assert.Equal(2, Regex.Matches(on, @"ScheduleFooterRefreshFor\(e\)").Count);
        Assert.Contains("TextChangedEventHandler((_, e) => ScheduleFooterRefreshFor(e))", on, StringComparison.Ordinal);
        Assert.Contains("SelectionChangedEventHandler((_, e) => ScheduleFooterRefreshFor(e))", on, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_the_dirty_check_reads_is_an_ignored_control()
    {
        var settings = Path.Combine(RepositoryRoot(), "src", "Scribe.App", "Settings");
        var footer = Footer;
        var bodies = new List<string>
        {
            Slice(footer, "private SettingsChangeSet ComputeCurrentChanges()", "private AppSettings CaptureDraftSettings()"),
            Slice(footer, "private AppSettings CaptureDraftSettings()", "private IReadOnlyList<DictionaryDraftRow> DictionaryDraftRows()"),
            Slice(footer, "private IReadOnlyList<DictionaryDraftRow> DictionaryDraftRows()", "private IReadOnlyList<LoadedDictionaryDraftRow>"),
        };
        var snippets = File.ReadAllText(Path.Combine(settings, "SettingsWindow.Snippets.cs"));
        bodies.Add(Slice(snippets, "private IReadOnlyList<SnippetDraftRow> SnippetDraftRows()", ";\r\n"));
        var profiles = File.ReadAllText(Path.Combine(settings, "SettingsWindow.Profiles.cs"));
        bodies.Add(Slice(profiles, "private List<AppProfile> BuildProfiles()", ";\r\n"));
        bodies.Add(Slice(profiles, "private IReadOnlyList<ProfileDraftRow> ProfileDraftRows()", ";\r\n"));

        foreach (var body in bodies)
        {
            foreach (var control in IgnoredControls)
            {
                Assert.DoesNotMatch(new Regex($@"\b{control}\b"), body);
            }
        }
    }

    [Theory]
    [InlineData("public sealed class DictionaryRow", "_rowKey.ForId(Id, this)", "Id > 0 ? Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $\"new:{GetHashCode()}\"")]
    [InlineData("public sealed class SnippetRow", "_rowKey.ForId(Id, this)", "Id > 0 ? Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $\"new:{GetHashCode()}\"")]
    [InlineData("public sealed class ProfileRow", "_rowKey.ForProfile(Origin == DraftRowOrigin.Saved, LoadedName, LoadedProcesses, this)", "Origin == DraftRowOrigin.Saved ? $\"saved:{LoadedName}:{LoadedProcesses}\" : $\"new:{GetHashCode()}\"")]
    public void Each_row_keeps_its_old_key_expression_for_the_flag_off(string type, string cached, string old)
    {
        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var start = window.IndexOf(type, StringComparison.Ordinal);
        Assert.True(start >= 0, type);
        var key = Slice(window[start..], "public string RowKey => _leanRowKeys", ";");

        Assert.Contains("? " + cached, key, StringComparison.Ordinal);
        Assert.Contains(": " + old, key, StringComparison.Ordinal);
        Assert.Contains("_leanRowKeys = _perfFlags.IsOn(PerfFlags.LeanFooterRefresh);", window, StringComparison.Ordinal);
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
