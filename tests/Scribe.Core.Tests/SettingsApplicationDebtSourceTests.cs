namespace Scribe.Core.Tests;

/// <summary>
/// How the host and the Settings window wire the process-wide owner of what a committed Settings save still owes dictation
/// (<see cref="Scribe.Core.Settings.SettingsApplicationDebt"/>): the owner the host keeps for the process so a closed window
/// cannot take the debt along, the application it retries with no window, the log that carries only a failure's shape, and
/// the teardown that cancels it. What the owner and the protocol do is behavioural, in
/// <see cref="SettingsApplicationDebtTests"/> and <c>SettingsApplicationDebtRealStoreTests</c>; these pins hold the parts only
/// a window or the host can break.
/// </summary>
public sealed class SettingsApplicationDebtSourceTests
{
    [Fact]
    public void The_host_keeps_one_owner_for_the_process_attaches_the_stored_settings_application_and_hands_it_to_every_window()
    {
        var app = Read("src", "Scribe.App", "App.xaml.cs");

        Assert.Contains(
            "_settingsDebt = new Scribe.Core.Settings.SettingsApplicationDebt(reportFailure: ReportSettingsApplicationFailure);",
            app,
            StringComparison.Ordinal);
        Assert.Contains("_settingsDebt.Attach(ApplyStoredSettingsInBackground);", app, StringComparison.Ordinal);

        // A window that opens after the last one's attempts ran out starts them again, and takes the owner and the app's own
        // application of settings, never a debt of its own.
        var open = app.IndexOf("_settingsDebt?.Resume();", StringComparison.Ordinal);
        var created = app.IndexOf("_settingsWindow = new SettingsWindow(", StringComparison.Ordinal);
        Assert.True(open > 0 && created > open, "The window is made before the owner resumes.");
        Assert.Matches(@"position => _overlay\?\.Preview\(position\),\s+ApplySettingsToApp,", app);
        Assert.Contains("settingsDebt: _settingsDebt)", app, StringComparison.Ordinal);

        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        Assert.Contains("SettingsApplicationDebt? settingsDebt = null)", window, StringComparison.Ordinal);
        Assert.Contains("payload => _settingsRepository.CommitLibraryState(payload)), settingsDebt);", window, StringComparison.Ordinal);
    }

    [Fact]
    public void The_background_application_reads_and_applies_the_stored_settings_only_on_the_dispatcher_and_waits_for_nothing()
    {
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var apply = Slice(app, "private Task<VocabularyRefresh> ApplyStoredSettingsInBackground()", "\n\n");

        Assert.Contains("Dispatcher.InvokeAsync(", apply, StringComparison.Ordinal);
        Assert.Contains("StoredSettingsReapply.ApplyStoredOnly(", apply, StringComparison.Ordinal);
        Assert.Contains("ApplySettingsToApp", apply, StringComparison.Ordinal);

        // No vocabulary-only fallback (it would look like success without putting the saved settings into effect), and
        // nothing blocks on the dispatcher.
        Assert.DoesNotContain("ReloadVocabulary", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("Reapply(", apply, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", apply, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter", apply, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke(", apply.Replace("InvokeAsync(", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_callback_logs_only_the_failures_shape_and_never_throws()
    {
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var report = Slice(app, "private void ReportSettingsApplicationFailure(", "\n    }");

        Assert.Contains("try", report, StringComparison.Ordinal);
        Assert.Contains("catch", report, StringComparison.Ordinal);
        Assert.Contains("FailureShape.Describe(ex)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.ToString", report, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", report, StringComparison.Ordinal);
        Assert.DoesNotContain("InnerException", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Quitting_cancels_the_retry_first_and_waits_for_nothing()
    {
        var app = Read("src", "Scribe.App", "App.xaml.cs");

        var stop = app.IndexOf("new(\"stop settings application\", () => _settingsDebt?.Dispose()),", StringComparison.Ordinal);
        var firstControllerStep = app.IndexOf("new(\"begin dictation shutdown\"", StringComparison.Ordinal);
        Assert.True(stop > 0, "Quitting does not dispose the owner.");
        Assert.True(stop < firstControllerStep, "The owner is disposed after the controller began shutting down.");
    }

    [Fact]
    public void The_owner_keeps_nothing_on_disk_never_polls_and_never_waits()
    {
        var owner = Read("src", "Scribe.Core", "Settings", "SettingsApplicationDebt.cs");

        // In memory only: no marker in the settings document, the database or a file.
        foreach (var persisted in new[] { "ISettingsRepository", "SqliteConnection", "File.", "Directory.", "AppPaths", "RegistryKey" })
        {
            Assert.DoesNotContain(persisted, owner, StringComparison.Ordinal);
        }

        // One delay between attempts on a time provider, cut short by a signal: no periodic timer, no sleep, and nothing
        // that blocks the thread that raised, settled or disposed the debt.
        foreach (var spinning in new[] { "new Timer(", "PeriodicTimer", "Thread.Sleep", ".Wait(", ".Result", "GetAwaiter().GetResult()", "SpinWait" })
        {
            Assert.DoesNotContain(spinning, owner, StringComparison.Ordinal);
        }

        Assert.Contains("Task.Delay(delay, _time, stop.Token)", owner, StringComparison.Ordinal);
        Assert.Contains("_shutdown.Cancel();", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void The_protocol_reports_every_failure_after_a_commit_to_the_owner_and_never_swallows_one_silently()
    {
        var protocol = Read("src", "Scribe.Core", "Settings", "WordPackSaveProtocol.cs");

        Assert.Contains("_debt.Report(SettingsApplicationStep.Apply, ex);", protocol, StringComparison.Ordinal);
        Assert.Contains("_debt.Report(SettingsApplicationStep.Settlement, ex);", protocol, StringComparison.Ordinal);
        Assert.Contains("_debt.Report(SettingsApplicationStep.ReferenceRepair, ex);", protocol, StringComparison.Ordinal);

        // Every operation pays an old debt on its way out, whatever it ends in.
        var run = Slice(protocol, "private async Task<WordPackSaveProtocolResult> RunAsync(", "\n    }");
        Assert.Contains("await PayOwedOnTheWayOutAsync(request).ConfigureAwait(true);", run, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(run, "PayOwedOnTheWayOutAsync(request)"));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Not found: {start}");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"No end found for: {start}");
        return source[from..to];
    }

    private static string Read(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
