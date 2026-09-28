using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

/// <summary>
/// The small shell changes behind PillBeforeTray, SkipVelopackWhenPackaged and BatchCleanupSelectionCounts: each keeps the
/// old code for the flag off and changes only an order or a skipped repetition with it on.
/// </summary>
public sealed class ShellOrderSourceTests
{
    [Fact]
    public void The_tray_and_the_pill_are_shown_through_one_ordered_call_before_the_warmup_and_release()
    {
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var render = Slice(app, "private void RenderDictationState(DictationStateChange change)", "private void UpdateTrayState(DictationState state)");

        // Both views go through DictationStateViews (Core, DictationStateViewsTests), the order from the flag: each receives
        // the state once, and a throw from one never keeps the other from it.
        var show = render.IndexOf("DictationStateViews.Show(", StringComparison.Ordinal);
        var flag = render.IndexOf("pillFirst: _perfFlags.IsOn(PerfFlags.PillBeforeTray),", StringComparison.Ordinal);
        var tray = render.IndexOf("tray: () => UpdateTrayState(state),", StringComparison.Ordinal);
        var pill = render.IndexOf("pill: () =>", StringComparison.Ordinal);
        var settings = render.IndexOf("var settings = _controller?.CurrentSettings;", StringComparison.Ordinal);
        var push = render.IndexOf("_overlay?.SetKeepWarm(", StringComparison.Ordinal);
        var switchStart = render.IndexOf("switch (state)", StringComparison.Ordinal);
        var warmup = render.IndexOf("_overlay?.Warmup();", StringComparison.Ordinal);
        var release = render.IndexOf("_overlay?.ReleaseWhenIdle();", StringComparison.Ordinal);

        Assert.True(
            show > 0 && show < flag && flag < tray && tray < pill && pill < settings && settings < push && push < switchStart &&
            switchStart < warmup && warmup < release,
            render);
        Assert.Single(Regex.Matches(render, Regex.Escape("DictationStateViews.Show(")));
        Assert.Single(Regex.Matches(render, Regex.Escape("UpdateTrayState(state)")));
        Assert.DoesNotContain("_tray?.SetState(", render, StringComparison.Ordinal);

        // The warmup reads the flag the pill's part set, and nothing here waits on another thread or process.
        Assert.Contains("var overlayEnabled = false;", render, StringComparison.Ordinal);
        Assert.Contains("overlayEnabled = settings?.ShowOverlay ?? false;", render, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\.(Invoke|Wait|WaitOne|Join)\(|\.Result\b|GetAwaiter\(\)"), render);

        var trayView = Slice(app, "private void UpdateTrayState(DictationState state)", "/// <summary>");
        Assert.Contains("_tray?.SetState(state);", trayView, StringComparison.Ordinal);
        Assert.Contains("\"Could not update the tray icon for state {State} ({Failure}).\", state, FailureShape.Describe(ex)", trayView, StringComparison.Ordinal);
    }

    [Fact]
    public void Velopack_is_left_out_only_with_the_flag_on_and_package_identity()
    {
        var program = Read("src", "Scribe.App", "Program.cs");

        Assert.Matches(
            new Regex(@"var skipVelopack = PerfFlags\.FromEnvironment\(\)\.IsOn\(PerfFlags\.SkipVelopackWhenPackaged\) &&\s*WindowsPackageIdentity\.IsPackaged\(\);"),
            program);
        Assert.Matches(
            new Regex(@"if \(!skipVelopack\)\s*\{\s*VelopackApp\.Build\(\)\s*\.OnAfterInstallFastCallback\(_ => Infrastructure\.ShellIconCache\.Refresh\(\)\)\s*\.OnAfterUpdateFastCallback\(_ => Infrastructure\.ShellIconCache\.Refresh\(\)\)\s*\.OnRestarted\(_ => Infrastructure\.ShellIconCache\.Refresh\(\)\)\s*\.Run\(\);\s*\}"),
            program);
    }

    [Fact]
    public void Select_everything_counts_once_only_with_the_flag_on()
    {
        var window = Read("src", "Scribe.App", "Settings", "DictionaryCleanupWindow.xaml.cs");

        Assert.Contains("_batchSelectionCounts = perfFlags.IsOn(PerfFlags.BatchCleanupSelectionCounts);", window, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"row\.PropertyChanged \+= \(_, _\) =>\s*\{\s*if \(_batchSelectionCounts && _syncingSelectAll\)\s*\{\s*return;\s*\}\s*UpdateButtons\(\);\s*SyncSelectAll\(\);\s*\};"),
            window);

        // Select everything still counts once at its end, after the guard is down.
        var selectAll = Slice(window, "private void SelectAll_Click(object sender, RoutedEventArgs e)", "private void DisableButton_Click(");
        Assert.Matches(new Regex(@"_syncingSelectAll = false;\s*UpdateButtons\(\);\s*\}"), selectAll);

        var yourWords = Read("src", "Scribe.App", "Settings", "SettingsWindow.YourWords.cs");
        Assert.Contains("DictionaryCleanupWindow.Show(this, report, _perfFlags);", yourWords, StringComparison.Ordinal);
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
