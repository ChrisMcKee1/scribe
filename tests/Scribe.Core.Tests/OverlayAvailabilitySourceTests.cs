using System.Text.RegularExpressions;

namespace Scribe.Core.Tests;

public sealed class OverlayAvailabilitySourceTests
{
    [Fact]
    public void Process_exit_wakes_the_existing_consumer_without_an_app_health_timer_or_a_stamped_command()
    {
        var client = Source("Scribe.App", "Overlay", "OverlayProcessClient.cs");
        Assert.Contains("exitWatch.Exited.ContinueWith(", client, StringComparison.Ordinal);
        Assert.Contains("_ => TryAdd(Command.HelperChanged)", client, StringComparison.Ordinal);
        Assert.Contains("public static Command HelperChanged { get; } = new(CommandKind.HelperChanged);", client, StringComparison.Ordinal);
        Assert.Contains("case CommandKind.HelperChanged:", client, StringComparison.Ordinal);
        Assert.Contains("_lifetime.OnHelperChanged(nowMs, _desired.Demand, helper)", client, StringComparison.Ordinal);
        Assert.Contains("_queue.TryTake(out var item, WaitMilliseconds())", client, StringComparison.Ordinal);
        Assert.DoesNotContain("new Timer(", client, StringComparison.Ordinal);
        Assert.DoesNotContain("new DispatcherTimer", client, StringComparison.Ordinal);

        var lifetime = Source("Scribe.Core", "Overlay", "OverlayHelperLifetime.cs");
        var wake = lifetime[lifetime.IndexOf("public long? NextWakeAtMs()", StringComparison.Ordinal)..];
        Assert.Contains("_stableDueAtMs", wake[..wake.IndexOf(';')], StringComparison.Ordinal);
    }

    [Fact]
    public void Availability_and_delivery_subscribers_are_nonthrowing_and_cannot_reach_teardown()
    {
        var client = Source("Scribe.App", "Overlay", "OverlayProcessClient.cs");
        var publish = Body(client, "private void PublishAvailability()");
        Assert.Contains("Interlocked.Exchange(ref _availability, availability)", publish, StringComparison.Ordinal);
        Assert.Contains("handlers.GetInvocationList()", publish, StringComparison.Ordinal);
        Assert.Matches(@"try\s*\{\s*handler\(\);\s*\}\s*catch \(Exception ex\)", publish);
        Assert.DoesNotContain("KillProcess(", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("EndHelper(", publish, StringComparison.Ordinal);

        var complete = Body(client, "private void CompleteDelivery(DeliveryReceipt? receipt)");
        Assert.Matches(@"try\s*\{\s*receipt\?\.Complete\(\);\s*\}\s*catch \(Exception ex\)", complete);
        Assert.DoesNotContain("KillProcess(", complete, StringComparison.Ordinal);
        var handle = Body(client, "private bool HandleCommand(Command item)");
        Assert.Contains("CompleteDelivery(item.Delivery);", handle[handle.IndexOf("finally", StringComparison.Ordinal)..], StringComparison.Ordinal);
        var state = Body(client, "private void HandleState(Command item)");
        Assert.True(state.IndexOf("WriteWithTimeout(item.AppliedAnchor", StringComparison.Ordinal) <
                    state.IndexOf("receipt.Delivered =", StringComparison.Ordinal));
    }

    [Fact]
    public void Intentional_close_drops_pending_health_work_and_a_replay_broken_by_close_is_abandoned()
    {
        var client = Source("Scribe.App", "Overlay", "OverlayProcessClient.cs");
        Assert.Contains("if (_closed)", Body(client, "private void RunDueWork()"), StringComparison.Ordinal);
        Assert.Contains("return Timeout.Infinite;", Body(client, "private int WaitMilliseconds()"), StringComparison.Ordinal);
        Assert.Contains("if (_closed)", Body(client, "private bool HandleCommand(Command item)"), StringComparison.Ordinal);
        var recovery = Body(client, "private void RecoverFromFailedWrite()");
        Assert.True(recovery.IndexOf("if (_closing.IsCancellationRequested)", StringComparison.Ordinal) <
                    recovery.IndexOf("_lifetime.OnWriteFailed(", StringComparison.Ordinal));
        var launch = Body(client, "private bool Launch()");
        Assert.Matches(@"if \(_closing\.IsCancellationRequested\)\s*\{\s*outcome = LaunchOutcome\.Abandoned;\s*\}", launch);
        Assert.True(launch.LastIndexOf("outcome = LaunchOutcome.Abandoned;", StringComparison.Ordinal) <
                    launch.IndexOf("_lifetime.OnLaunchCompleted(", StringComparison.Ordinal));
    }

    [Fact]
    public void Failure_notices_are_revision_bound_and_do_not_touch_the_speech_model_condition()
    {
        var shell = Source("Scribe.App", "App.xaml.cs");
        Assert.Contains("_overlay.AvailabilityChanged += OnOverlayAvailabilityChanged;", shell, StringComparison.Ordinal);
        var changed = Body(shell, "private void OnOverlayAvailabilityChanged()");
        Assert.Contains("var revision = relay.LastRenderedRevision;", changed, StringComparison.Ordinal);
        Assert.Contains("relay.PublishIfCurrent(revision, () => TryShowOverlayFailureNotice(revision));", changed, StringComparison.Ordinal);
        var notice = Body(shell, "private void TryShowOverlayFailureNotice(long revision)");
        Assert.Contains("_controller?.IsClosing != false", notice, StringComparison.Ordinal);
        Assert.Contains("overlay.Availability, _controller.CurrentSettings.ShowOverlay", notice, StringComparison.Ordinal);
        Assert.Contains("OverlayDemand.Sustained", notice, StringComparison.Ordinal);
        Assert.Contains("TryShowOverlayNotice(OverlayFeedback.FailureNotice());", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("_trayCondition", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTrayCondition", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("_trayFeedback", notice, StringComparison.Ordinal);
        Assert.Contains("TryShowOverlayFailureNotice(change.Revision);", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_problem_routes_and_late_delivery_fallbacks_read_live_availability_and_preserve_revision_gates()
    {
        var shell = Source("Scribe.App", "App.xaml.cs");
        var problem = Body(shell, "private void ShowDictationProblem(DictationProblemReport report, bool controllerError, long reportedRevision = 0)");
        Assert.Contains("OverlayFeedback.DecideProblem(report.Problem, settings?.ShowOverlay == true, _overlay?.Availability)", problem, StringComparison.Ordinal);
        var warning = Body(shell, "private void ShowRecordingWarningOrNotice(");
        Assert.Contains("availability: _overlay?.Availability", warning, StringComparison.Ordinal);
        Assert.Contains("_overlay?.ShowRecordingWarning(reason, delivered =>", warning, StringComparison.Ordinal);
        Assert.Contains("relay.PublishIfCurrent(report.RecordingRevision", warning, StringComparison.Ordinal);
        Assert.Contains("if (!delivered || !OverlayFeedback.CanShow(", warning, StringComparison.Ordinal);
        var render = Body(shell, "private void RenderDictationState(DictationStateChange change)");
        Assert.Contains("_overlay?.ShowOutcome(outcome, delivered =>", render, StringComparison.Ordinal);
        Assert.Contains("_dictationState?.PublishIfCurrent(change.Revision", render, StringComparison.Ordinal);
        Assert.Contains("var copyEntryId =", render, StringComparison.Ordinal);
        var fallback = Body(shell, "private void ShowOverlayOutcomeFallback(");
        Assert.Contains("_overlay?.Availability", fallback, StringComparison.Ordinal);
        Assert.Contains("feedback.TakeNotice(delivered && canShow", fallback, StringComparison.Ordinal);
        Assert.Contains("_noticeCopyEntryId = copyEntryId;", fallback, StringComparison.Ordinal);
        var mark = Body(shell, "private void NoteOverlayProblemNoticed(");
        Assert.Contains("OverlayFeedback.IsCurrent(reportedRevision, _dictationState?.LastRenderedRevision ?? 0)", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_diagnostics_keep_hex_exit_codes_fixed_stages_and_bounded_replay_writes()
    {
        var client = Source("Scribe.App", "Overlay", "OverlayProcessClient.cs");
        Assert.Contains("OverlayExitCode.Format(TryGetExitCode(process))", client, StringComparison.Ordinal);
        Assert.Contains("stage {Stage}, exit code {ExitCode}", client, StringComparison.Ordinal);
        Assert.Contains("duration {DurationMs} ms", client, StringComparison.Ordinal);
        Assert.Contains("attempt {Attempt}", client, StringComparison.Ordinal);
        Assert.Contains("WriteWithTimeout(OverlayPipeProtocol.PositionLine(_position));", client, StringComparison.Ordinal);
        Assert.Contains("WriteWithTimeout(_desired.ReplayLine);", client, StringComparison.Ordinal);
        Assert.DoesNotContain("writer.WriteLine(_desired.ReplayLine);", client, StringComparison.Ordinal);

        var window = Source("Scribe.Overlay", "OverlayWindow.xaml.cs");
        foreach (var (stage, operation) in new[]
                 {
                     ("switchers", "_appWindow.IsShownInSwitchers = false;"),
                     ("title", "_appWindow.Title ="),
                     ("presenter", "if (_appWindow.Presenter is OverlappedPresenter presenter)"),
                     ("border", "presenter.SetBorderAndTitleBar(false, false);"),
                     ("resize", "presenter.IsResizable = false;"),
                     ("maximize", "presenter.IsMaximizable = false;"),
                     ("minimize", "presenter.IsMinimizable = false;"),
                 })
        {
            var begin = window.IndexOf($"OverlayWindow.ConfigurePresenter stage={stage} begin", StringComparison.Ordinal);
            Assert.True(begin >= 0 && begin < window.IndexOf(operation, StringComparison.Ordinal));
        }

        Assert.Contains("OverlayWindow.ctor complete durationMs=", window, StringComparison.Ordinal);
    }

    private static string Source(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Regex.Replace(File.ReadAllText(Path.Combine([root.FullName, "src", .. parts])), @"//[^\n]*", string.Empty)
            .ReplaceLineEndings("\n");
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = code.IndexOf('{', code.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }
}
