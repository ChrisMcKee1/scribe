using System.Text.Json;
using Microsoft.Extensions.Logging;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.TextInjection;
using Scribe.Core.Tray;

namespace Scribe.Core.Tests;

public sealed class PlainTextOnceTests
{
    [Fact]
    public void Arming_is_visible_cancellable_and_expires_at_sixty_seconds_even_if_the_timer_is_late()
    {
        var clock = new ManualTimeProvider();
        using var once = new PlainTextOnce(clock);
        var states = new List<PlainTextOnceState>();
        once.Changed += states.Add;
        Assert.False(once.Current.IsArmed);
        once.Arm();
        Assert.True(once.Current.IsArmed);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(clock.Timers).DueTime);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(once.Current.IsArmed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PlainTextOnceStatus.Expired, once.Current.Status);
        Assert.False(once.ConsumeForAdmittedCapture(out var noChange));
        Assert.Null(noChange);
        Assert.Equal([PlainTextOnceStatus.Armed, PlainTextOnceStatus.Expired], states.Select(state => state.Status));

        once.Arm();
        once.Cancel();
        Assert.Equal(PlainTextOnceStatus.Cancelled, once.Current.Status);
        Assert.False(once.ConsumeForAdmittedCapture(out _));
    }

    [Fact]
    public void A_stale_or_early_tick_cannot_expire_a_new_arm()
    {
        var clock = new ManualTimeProvider();
        using var once = new PlainTextOnce(clock);
        once.Arm();
        clock.Advance(TimeSpan.FromSeconds(58));
        once.Arm();
        var newRevision = once.Current.Revision;
        var timer = Assert.Single(clock.Timers);
        timer.Fire();
        Assert.True(once.Current.IsArmed);
        Assert.Equal(newRevision, once.Current.Revision);
        clock.Advance(TimeSpan.FromSeconds(59));
        timer.Fire();
        Assert.Equal(TimeSpan.FromSeconds(1), timer.DueTime);
        clock.Advance(TimeSpan.FromSeconds(1));
        timer.Fire();
        Assert.Equal(PlainTextOnceStatus.Expired, once.Current.Status);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.DueTime);
    }

    [Theory]
    [InlineData(ActivationDecision.Paused)]
    [InlineData(ActivationDecision.AlreadyRecording)]
    [InlineData(ActivationDecision.StillProcessing)]
    [InlineData(ActivationDecision.Closing)]
    public void Refused_activations_never_consume_the_choice(ActivationDecision refused)
    {
        using var rig = new AdmissionRig();
        if (refused == ActivationDecision.Paused) rig.Loop.SetPaused(true);
        if (refused is ActivationDecision.AlreadyRecording or ActivationDecision.StillProcessing)
        {
            rig.Loop.TryBeginRecording(() => DictationFormatPlan.Capture(rig.Settings, rig.TargetProcessName));
        }

        if (refused == ActivationDecision.StillProcessing)
        {
            rig.Processing = rig.Loop.TryBeginProcessing().Admission;
        }

        if (refused == ActivationDecision.Closing) rig.Loop.BeginShutdown();
        rig.Once.Arm();
        Assert.Equal(refused, rig.Begin().Decision);
        Assert.Equal(0, rig.FactoryCalls);
        Assert.True(rig.Once.Current.IsArmed);
        Assert.Equal(0, rig.ConsumedNotifications);
    }

    [Fact]
    public void An_unarmed_admission_keeps_the_matched_Markdown_source_plan()
    {
        using var rig = new AdmissionRig();
        var activation = rig.Begin();
        Assert.Equal(ActivationDecision.Started, activation.Decision);
        var plan = Assert.IsType<DictationFormatPlan>(activation.Capture);
        Assert.Equal(DictationFormatDecision.MarkdownSource, plan.Decision);
        Assert.Equal(DictationTextFormat.MarkdownSource, plan.TextFormat);
        Assert.Equal(InjectionMethod.ClipboardPaste, plan.InjectionMethod);
        Assert.False(plan.ShiftEnterLineBreaks);
        Assert.Equal(NewlineInjectionMode.KeepNewlines, plan.NewlineHandling);
        Assert.Equal("Code", plan.TargetProcessName);
        Assert.Equal(PlainTextOnceStatus.Off, rig.Once.Current.Status);
        Assert.Equal(0, rig.ConsumedNotifications);
        Assert.Equal(1, rig.FactoryCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void An_unarmed_admission_refuses_stored_or_inherited_Markdown_newline_conflicts(
        bool inheritFormat, bool inheritNewlines)
    {
        using var rig = new AdmissionRig();
        var profile = rig.Settings.Profiles[0];
        profile.TextFormat = inheritFormat ? null : DictationTextFormat.MarkdownSource;
        profile.NewlineHandling = inheritNewlines ? null : NewlineInjectionMode.AlwaysFlatten;
        rig.Settings.NewlineHandling = inheritNewlines ? NewlineInjectionMode.AlwaysFlatten : NewlineInjectionMode.KeepNewlines;

        var activation = rig.Begin();
        Assert.Equal(ActivationDecision.Started, activation.Decision);
        var plan = Assert.IsType<DictationFormatPlan>(activation.Capture);
        Assert.Equal(DictationFormatDecision.MarkdownNewlineConflict, plan.Decision);
        Assert.Equal(DictationTextFormat.Plain, plan.TextFormat);
        Assert.Equal(InjectionMethod.UnicodeType, plan.InjectionMethod);
        Assert.True(plan.ShiftEnterLineBreaks);
        Assert.Equal(NewlineInjectionMode.AlwaysFlatten, plan.NewlineHandling);
        Assert.Equal("one two", plan.Represent("one\ntwo"));
        Assert.Equal(InjectionMethod.ClipboardPaste, profile.InjectionMethod);
        Assert.False(profile.ShiftEnterLineBreaks);
        Assert.Equal(PlainTextOnceStatus.Off, rig.Once.Current.Status);
        Assert.Equal(0, rig.ConsumedNotifications);
    }

    [Theory]
    [InlineData(NewlineInjectionMode.KeepNewlines)]
    [InlineData(NewlineInjectionMode.AlwaysFlatten)]
    public void Only_the_next_admitted_capture_takes_plain_and_consumption_is_announced_after_admission(
        NewlineInjectionMode newlineHandling)
    {
        using var rig = new AdmissionRig();
        rig.Settings.Profiles[0].NewlineHandling = newlineHandling;
        rig.Once.Arm();
        var first = rig.Begin();
        Assert.Equal(ActivationDecision.Started, first.Decision);
        var plan = Assert.IsType<DictationFormatPlan>(first.Capture);
        Assert.Equal(DictationFormatDecision.PlainOnce, plan.Decision);
        Assert.Equal(DictationTextFormat.Plain, plan.TextFormat);
        Assert.Equal(InjectionMethod.ClipboardPaste, plan.InjectionMethod);
        Assert.False(plan.ShiftEnterLineBreaks);
        Assert.Equal(PlainTextOnceStatus.Consumed, rig.Once.Current.Status);
        Assert.Equal(1, rig.ConsumedNotifications);
        Assert.Equal(DictationPhase.Recording, rig.PhaseAtNotification);
        rig.Loop.TryAbandonRecording(first.DictationId, TimeSpan.Zero);
        var second = rig.Begin();
        var nextPlan = Assert.IsType<DictationFormatPlan>(second.Capture);
        var conflict = newlineHandling == NewlineInjectionMode.AlwaysFlatten;
        Assert.Equal(conflict ? DictationFormatDecision.MarkdownNewlineConflict : DictationFormatDecision.MarkdownSource, nextPlan.Decision);
        Assert.Equal(conflict ? DictationTextFormat.Plain : DictationTextFormat.MarkdownSource, nextPlan.TextFormat);
        Assert.Equal(conflict ? InjectionMethod.UnicodeType : InjectionMethod.ClipboardPaste, nextPlan.InjectionMethod);
        Assert.Equal(conflict, nextPlan.ShiftEnterLineBreaks);
        Assert.Equal(PlainTextOnceStatus.Consumed, rig.Once.Current.Status);
        Assert.Equal(1, rig.ConsumedNotifications);
        Assert.Equal(2, rig.FactoryCalls);
    }

    [Fact]
    public void Admission_at_the_expiry_deadline_keeps_Markdown_and_announces_expiry_after_admission()
    {
        using var rig = new AdmissionRig();
        var observed = new List<(PlainTextOnceState State, DictationPhase Phase)>();
        rig.Once.Changed += state => observed.Add((state, rig.Loop.Phase));
        rig.Once.Arm();
        rig.Clock.Advance(TimeSpan.FromSeconds(60));

        var plan = Assert.IsType<DictationFormatPlan>(rig.Begin().Capture);
        Assert.Equal(DictationFormatDecision.MarkdownSource, plan.Decision);
        Assert.Equal(DictationTextFormat.MarkdownSource, plan.TextFormat);
        Assert.Equal(PlainTextOnceStatus.Expired, rig.Once.Current.Status);
        Assert.Equal([PlainTextOnceStatus.Armed, PlainTextOnceStatus.Expired], observed.Select(change => change.State.Status));
        Assert.Equal(DictationPhase.Recording, observed[^1].Phase);
        Assert.Equal(0, rig.ConsumedNotifications);
    }

    [Fact]
    public void A_failed_capture_factory_does_not_consume_and_shutdown_clears_even_an_unused_arm()
    {
        using var rig = new AdmissionRig();
        rig.Once.Arm();
        Assert.Throws<InvalidOperationException>(() =>
            DictationStartPolicy.BeginRecording(rig.Loop, () => throw new InvalidOperationException("capture state unavailable"), () => { }));
        Assert.True(rig.Once.Current.IsArmed);
        Assert.Equal(DictationPhase.Idle, rig.Loop.Phase);
        rig.Close();
        Assert.Equal(PlainTextOnceStatus.Shutdown, rig.Once.Current.Status);
        rig.Once.Arm();
        Assert.False(rig.Once.Current.IsArmed);
        rig.Clock.Timers[0].Fire();
        Assert.Equal(1, rig.Clock.Timers[0].DisposeCount);
    }

    [Fact]
    public void Presentation_failures_cannot_leave_an_arm_behind_or_prevent_capture()
    {
        var clock = new ManualTimeProvider();
        var log = new CleanupLogging.CapturingLogger<PlainTextOnce>();
        using var once = new PlainTextOnce(clock, log);
        var observed = new List<PlainTextOnceState>();
        const string sensitiveDetail = "private-presentation-detail";
        once.Changed += _ => throw new InvalidOperationException(sensitiveDetail);
        once.Changed += observed.Add;
        once.Arm();
        Assert.True(once.ConsumeForAdmittedCapture(out var change));
        Assert.Single(observed);
        once.NotifyConsumption(change);
        Assert.Equal(PlainTextOnceStatus.Consumed, observed[^1].Status);
        Assert.False(once.Current.IsArmed);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Contains(nameof(InvalidOperationException), entry.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitiveDetail, entry.AllText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_throwing_diagnostic_cannot_interrupt_publication_or_capture_consumption()
    {
        using var once = new PlainTextOnce(new ManualTimeProvider(), new ThrowingLogger());
        var observed = new List<PlainTextOnceState>();
        once.Changed += _ => throw new InvalidOperationException("presentation failed");
        once.Changed += observed.Add;
        once.Arm();
        Assert.True(once.ConsumeForAdmittedCapture(out var change));
        once.NotifyConsumption(change);
        Assert.Equal([PlainTextOnceStatus.Armed, PlainTextOnceStatus.Consumed], observed.Select(state => state.Status));
        Assert.False(once.Current.IsArmed);
    }

    [Fact]
    public void Plain_once_is_not_a_persistent_setting_or_an_ai_dictionary_or_snippet_bypass()
    {
        var settings = AppAwareFormattingTests.NewSettings();
        settings.EnableAiCleanup = true;
        settings.ApplyPostProcessing = true;
        settings.Profiles[0].WritingStyle = "Brief.";
        var plan = DictationFormatPlan.Capture(settings, "Code", plainOnce: true);
        Assert.Equal(DictationFormatDecision.PlainOnce, plan.Decision);
        Assert.Equal(DictationTextFormat.Plain, plan.TextFormat);
        Assert.True(settings.EnableAiCleanup);
        Assert.True(settings.ApplyPostProcessing);
        Assert.Equal("Brief.", settings.Profiles[0].WritingStyle);
        Assert.Equal("- already supplied\nsource", plan.Represent("- already supplied\nsource"));
        Assert.DoesNotContain("PlainTextOnce", JsonSerializer.Serialize(settings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tray_check_tooltip_and_notices_show_armed_and_why_it_cleared_without_phantom_state()
    {
        foreach (var status in Enum.GetValues<PlainTextOnceStatus>())
        {
            var state = new PlainTextOnceState(status, 1);
            var item = TrayMenu.Build(new TrayMenuState(PlainTextOnce: state)).Items.Single(item => item.Command == TrayCommand.PlainTextOnce);
            Assert.Equal(TrayItemKind.Check, item.Kind);
            Assert.Equal(status == PlainTextOnceStatus.Armed, item.IsChecked);
            Assert.False(string.IsNullOrWhiteSpace(item.HelpText));
            Assert.Equal(status != PlainTextOnceStatus.Shutdown, item.Enabled);
            Assert.Equal(status == PlainTextOnceStatus.Armed, item.Label.Contains("(armed)", StringComparison.Ordinal));
            var tooltip = TrayToolTip.Compose(TrayState.Ready, new string('x', 200), HotkeyMode.Hold, plainTextOnceArmed: state.IsArmed);
            Assert.True(tooltip.Length <= TrayToolTip.MaxLength);
            Assert.Equal(state.IsArmed, tooltip.Contains("plain text once armed", StringComparison.Ordinal));
        }

        Assert.Contains("60 seconds", TrayNotices.PlainTextOnce(new(PlainTextOnceStatus.Armed, 1))!.Body, StringComparison.Ordinal);
        Assert.Contains("expired", TrayNotices.PlainTextOnce(new(PlainTextOnceStatus.Expired, 2))!.Title, StringComparison.Ordinal);
        Assert.Contains("cancelled", TrayNotices.PlainTextOnce(new(PlainTextOnceStatus.Cancelled, 2))!.Title, StringComparison.Ordinal);
    }

    private sealed class AdmissionRig : IDisposable
    {
        public ManualTimeProvider Clock { get; } = new();
        public AppSettings Settings { get; } = AppAwareFormattingTests.NewSettings();
        public string? TargetProcessName { get; set; } = "Code";
        public PlainTextOnce Once { get; }
        public DictationLifecycle<DictationFormatPlan> Loop { get; }
        public ProcessingAdmission<DictationFormatPlan>? Processing { get; set; }
        public int FactoryCalls { get; private set; }
        public int ConsumedNotifications { get; private set; }
        public DictationPhase PhaseAtNotification { get; private set; }

        public AdmissionRig()
        {
            Once = new PlainTextOnce(Clock);
            Loop = new DictationLifecycle<DictationFormatPlan>(() => { }, () => { }, Clock);
            Once.Changed += state =>
            {
                if (state.Status != PlainTextOnceStatus.Consumed) return;
                ConsumedNotifications++;
                PhaseAtNotification = Loop.Phase;
            };
        }

        public DictationActivation<DictationFormatPlan> Begin()
        {
            PlainTextOnceState? change = null;
            var activation = DictationStartPolicy.BeginRecording(Loop, () =>
            {
                FactoryCalls++;
                return DictationFormatPlan.CaptureAdmitted(Settings, TargetProcessName, Once, out change);
            }, () => { });
            Once.NotifyConsumption(change);
            return activation;
        }

        public void Close()
        {
            Loop.BeginShutdown();
            Once.Dispose();
        }

        public void Dispose()
        {
            Close();
            if (Processing is { } processing)
            {
                Loop.ReturnToIdle(TimeSpan.Zero);
                Loop.EndProcessing(processing);
                Processing = null;
            }

            Loop.Shutdown([], new NoHistory(), TimeSpan.Zero, TimeSpan.Zero);
        }
    }

    private sealed class NoHistory : IHistoryWriter
    {
        public bool Enqueue(HistoryEntry entry, CapturedAudio? audio, long dictationId = 0) => true;
        public bool WaitForAcceptedWrites(TimeSpan timeout) => true;
        public HistoryDrainResult Complete(TimeSpan timeout) => new(true, 0, 0);
    }

    private sealed class ThrowingLogger : ILogger<PlainTextOnce>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("log unavailable");
    }
}
