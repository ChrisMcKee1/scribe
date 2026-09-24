using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The device watcher against Microsoft's rules for IMMNotificationClient callbacks (nonblocking; never register or
/// unregister in one; never release an MMDevice API object in one) and against the bursts Windows actually sends: one
/// OnDefaultDeviceChanged per role that moved, device state changes and property churn on plug and unplug. The clock,
/// the thread pool and the notification thread are all the test's, so nothing here waits on time.
/// </summary>
public sealed class InputDeviceWatcherTests
{
    private static readonly TimeSpan Quiet = InputDeviceWatcher.DefaultQuietPeriod;

    private static readonly AudioDevice Insta = new("insta", "Microphone (Insta360)", IsDefault: true);
    private static readonly AudioDevice Elgato = new("elgato", "Mic In (Elgato)", IsDefault: false, IsCommunicationsDefault: true);

    [Fact]
    public void A_burst_of_notifications_is_read_once_after_it_has_been_quiet_and_announced_once()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var announced = new List<IReadOnlyList<AudioDevice>>();
        rig.Watcher.Changed += announced.Add;

        // Changing the default input in Windows Settings: once per role that moved.
        rig.Devices = [Insta with { IsDefault = false }, Elgato with { IsDefault = true }];
        rig.Notifications.Raise(EndpointChange.CaptureDefault);
        rig.Notifications.Raise(EndpointChange.CaptureDefault);
        rig.Notifications.Raise(EndpointChange.CaptureDefault);

        Assert.Equal(1, rig.QueuedCount); // one restart of the quiet period for the whole burst
        rig.RunQueued();
        Assert.Equal(1, rig.Reads); // the first reading only

        rig.Time.Advance(Quiet - TimeSpan.FromMilliseconds(1));
        rig.FireTimer();
        Assert.Equal(1, rig.Reads); // not quiet for long enough yet

        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        rig.FireTimer();

        Assert.Equal(2, rig.Reads);
        var devices = Assert.Single(announced);
        Assert.True(Assert.Single(devices, device => device.Id == "elgato").IsDefault);
        Assert.Contains(rig.Log.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("default='Mic In (Elgato)'") &&
            entry.Message.Contains("previousDefault='Microphone (Insta360)'"));
    }

    [Fact]
    public void A_notification_after_the_quiet_period_restarted_restarts_it_again()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();

        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();
        rig.Time.Advance(Quiet / 2);
        rig.Notifications.Raise(EndpointChange.Device);
        Assert.Equal(1, rig.QueuedCount);
        rig.RunQueued();

        rig.Time.Advance(Quiet / 2);
        rig.FireTimer();
        Assert.Equal(1, rig.Reads); // the second notification moved the reading back

        rig.Time.Advance(Quiet / 2);
        rig.FireTimer();
        Assert.Equal(2, rig.Reads);
    }

    [Fact]
    public void Render_defaults_and_ordinary_property_changes_never_wake_the_watcher()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();

        rig.Notifications.Raise(EndpointChange.RenderDefault);
        rig.Notifications.Raise(EndpointChange.OtherProperty);

        Assert.Equal(0, rig.QueuedCount);
    }

    [Fact]
    public void Nothing_is_announced_when_the_microphones_did_not_change()
    {
        // A render device plugged in raises device notifications too; the endpoints are read and found the same.
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var announced = 0;
        rig.Watcher.Changed += _ => announced++;

        rig.Devices = [Elgato, Insta]; // the same picture in another order
        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();

        Assert.Equal(2, rig.Reads);
        Assert.Equal(0, announced);
    }

    [Fact]
    public void A_renamed_microphone_is_announced()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        IReadOnlyList<AudioDevice>? announced = null;
        rig.Watcher.Changed += devices => announced = devices;

        rig.Devices = [Insta with { Name = "Desk microphone" }, Elgato];
        rig.Notifications.Raise(EndpointChange.Name);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();

        Assert.NotNull(announced);
        Assert.Contains(announced, device => device.Name == "Desk microphone");
    }

    [Fact]
    public void A_notification_callback_never_waits_even_while_a_reading_is_holding_the_watcher()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();

        // Armed only now, so a callback that read inside itself would have read unblocked above and failed the wait for
        // the reading below, rather than hang the test on its own thread.
        using var readEntered = new ManualResetEventSlim();
        using var readMayFinish = new ManualResetEventSlim();
        rig.DuringRead = () =>
        {
            readEntered.Set();
            readMayFinish.Wait();
        };

        rig.Time.Advance(Quiet);
        var reading = BlockedThreads.Start(rig.FireTimer);
        Assert.True(readEntered.Wait(BlockedThreads.SafetyTimeout));

        // The audio worker thread delivers more notifications while the reading holds the watcher's reading gate.
        var callbacks = BlockedThreads.Start(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                rig.Notifications.Raise(i % 2 == 0 ? EndpointChange.CaptureDefault : EndpointChange.Device);
            }
        });
        BlockedThreads.Join(callbacks); // returned, with the reading still blocked

        Assert.False(readMayFinish.IsSet);
        Assert.True(reading.IsAlive);
        readMayFinish.Set();
        BlockedThreads.Join(reading);
        Assert.Equal(0, rig.Notifications.CallsInsideCallback);
    }

    [Fact]
    public void Nothing_registers_unregisters_reads_or_touches_a_device_inside_a_callback()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var readsBefore = rig.Reads;

        foreach (var change in Enum.GetValues<EndpointChange>())
        {
            rig.Notifications.Raise(change);
        }

        Assert.Equal(readsBefore, rig.Reads); // reading happens on the pool, later
        Assert.Equal(0, rig.Notifications.CallsInsideCallback);
        Assert.Equal(["register #1"], rig.Notifications.Events);
    }

    [Fact]
    public void Disposal_unregisters_before_the_enumerator_is_released_and_later_notifications_do_nothing()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var announced = 0;
        rig.Watcher.Changed += _ => announced++;
        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();

        rig.Watcher.Dispose();
        rig.Watcher.Dispose();

        Assert.Equal(["register #1", "unregister #1", "release #1"], rig.Notifications.Events);
        Assert.False(rig.Watcher.IsListening);

        // A late notification, and the reading that was already scheduled, raise nothing.
        rig.Devices = [Elgato with { IsDefault = true }];
        rig.Notifications.RaiseOnLastRegistration(EndpointChange.Device);
        Assert.Equal(0, rig.QueuedCount);
        rig.Time.Advance(Quiet);
        rig.FireTimer();
        Assert.Equal(0, announced);
        rig.Watcher.EnsureListening();
        Assert.Equal(3, rig.Notifications.Events.Count);
    }

    [Fact]
    public void A_handler_that_throws_does_not_keep_the_change_from_the_others()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var second = 0;
        rig.Watcher.Changed += _ => throw new InvalidOperationException("A window that is already gone.");
        rig.Watcher.Changed += _ => second++;

        rig.Devices = [Insta];
        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();

        Assert.Equal(1, second);
        Assert.Contains(rig.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("handler threw"));
    }

    [Fact]
    public void A_registration_that_lost_the_audio_service_is_replaced_once_and_logged_once()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Notifications.ProbeFailure = () => new COMException("Disconnected.", AudioServiceFailure.Disconnected);

        rig.Watcher.EnsureListening();

        Assert.Equal(["register #1", "unregister #1", "release #1", "register #2"], rig.Notifications.Events);
        Assert.True(rig.Watcher.IsListening);
        var warning = Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("reconnected", warning.Message);
        Assert.Contains("0x80010108", warning.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing was heard while it was gone, so it reads again.
        Assert.Equal(1, rig.QueuedCount);

        // The next check within the bound changes nothing, however often the list is opened.
        rig.Watcher.EnsureListening();
        rig.Watcher.EnsureListening();
        Assert.Equal(4, rig.Notifications.Events.Count);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_reconnection_that_keeps_failing_is_retried_only_after_the_bound_and_warned_about_once()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Notifications.ProbeFailure = () => new COMException("Server unavailable.", AudioServiceFailure.RpcServerUnavailable);
        rig.Notifications.RegisterFailure = () => new COMException("Not running.", AudioServiceFailure.AudioServiceNotRunning);

        rig.Watcher.EnsureListening();
        Assert.False(rig.Watcher.IsListening);
        Assert.Equal(1, rig.Notifications.RegisterAttempts - 1);

        rig.Watcher.EnsureListening(); // within the bound: no attempt
        Assert.Equal(1, rig.Notifications.RegisterAttempts - 1);

        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.Watcher.EnsureListening(); // due again: one more attempt, logged only at Debug
        Assert.Equal(2, rig.Notifications.RegisterAttempts - 1);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);

        rig.Notifications.RegisterFailure = null;
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.Watcher.EnsureListening();
        Assert.True(rig.Watcher.IsListening);
        Assert.Contains(rig.Log.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("Watching for audio device changes again"));
    }

    [Fact]
    public void A_failure_that_is_not_the_audio_service_going_away_leaves_the_registration_alone()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Notifications.ProbeFailure = () => new COMException("Unspecified.", unchecked((int)0x80004005));

        rig.Watcher.EnsureListening();

        Assert.Equal(["register #1"], rig.Notifications.Events);
        Assert.True(rig.Watcher.IsListening);
    }

    [Fact]
    public void A_registration_that_fails_at_startup_never_throws_and_is_made_when_the_list_is_next_opened()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.Notifications.RegisterFailure = () => new COMException("Not running.", AudioServiceFailure.AudioServiceNotRunning);

        rig.Watcher.Start();

        Assert.False(rig.Watcher.IsListening);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        rig.RunQueued();
        Assert.Equal(0, rig.Reads); // nothing is read until a list is shown

        rig.Notifications.RegisterFailure = null;
        rig.Watcher.EnsureListening(); // within the bound since the failed attempt
        Assert.False(rig.Watcher.IsListening);
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.Watcher.EnsureListening();
        Assert.True(rig.Watcher.IsListening);
    }

    [Fact]
    public void A_change_that_lands_before_the_watcher_reads_anything_itself_still_reaches_a_list_shown_before_it()
    {
        // The reviewed ordering: the session banner and then a Settings window read the list at startup, Windows moves the
        // default, and only then does the watcher take a reading of its own. With a baseline of its own, that reading
        // became the baseline in silence and the Settings window kept the old default.
        var rig = new Rig([Insta, Elgato]);
        rig.Watcher.Start();
        var announced = new List<IReadOnlyList<AudioDevice>>();
        rig.Watcher.Changed += announced.Add;
        rig.Watcher.Read(); // the banner
        rig.Watcher.Read(); // the Settings window
        Assert.Empty(announced); // the first reading is nobody's news, and the second found the same

        rig.Devices = [Insta with { IsDefault = false }, Elgato with { IsDefault = true }];
        rig.Notifications.Raise(EndpointChange.CaptureDefault);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();

        var shown = Assert.Single(announced);
        Assert.True(Assert.Single(shown, device => device.Id == "elgato").IsDefault);
    }

    [Fact]
    public void A_list_shown_earlier_hears_about_a_change_another_caller_read_first()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow(); // a Settings window shows Insta as the default
        var announced = new List<IReadOnlyList<AudioDevice>>();
        rig.Watcher.Changed += announced.Add;

        // The default moves, and the tray menu opens and reads it before the watcher's own reading comes round.
        rig.Devices = [Insta with { IsDefault = false }, Elgato with { IsDefault = true }];
        var tray = rig.Watcher.Read();

        Assert.Same(tray, Assert.Single(announced)); // announced at once, by the reading that noticed
        rig.Notifications.Raise(EndpointChange.CaptureDefault);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();
        Assert.Single(announced); // the watcher's own reading found nothing new
    }

    [Fact]
    public void A_change_undone_within_one_burst_still_reaches_a_list_that_saw_the_change()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var announced = new List<IReadOnlyList<AudioDevice>>();
        rig.Watcher.Changed += announced.Add;

        rig.Devices = [Insta, Elgato, new AudioDevice("usb", "USB Microphone", false)];
        rig.Watcher.Read(); // the tray, while the USB microphone is plugged in
        rig.Devices = [Insta, Elgato]; // and it is pulled out again before the burst goes quiet
        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();

        Assert.Equal([3, 2], announced.Select(devices => devices.Count));
    }

    [Fact]
    public void After_a_failed_reading_the_next_one_is_news_even_when_nothing_changed()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        var announced = new List<IReadOnlyList<AudioDevice>>();
        rig.Watcher.Changed += announced.Add;

        // A Settings window opens while the audio service is restarting and shows no microphones.
        rig.FailReads = true;
        Assert.Throws<COMException>(() => rig.Watcher.Read());
        rig.FailReads = false;

        rig.Notifications.Raise(EndpointChange.Device);
        rig.RunQueued();
        rig.Time.Advance(Quiet);
        rig.FireTimer();

        Assert.Equal(2, Assert.Single(announced).Count);
    }

    [Fact]
    public void While_something_listens_a_lost_registration_is_replaced_on_the_interval_without_a_list_being_shown()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Watcher.SetWatched(true);
        Assert.Equal(InputDeviceWatcher.DefaultRecoveryInterval, rig.HealthTimer.DueTime);

        rig.Notifications.ProbeFailure = () => new COMException("Disconnected.", AudioServiceFailure.Disconnected);
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.HealthTimer.Fire();

        Assert.Equal(["register #1", "unregister #1", "release #1", "register #2"], rig.Notifications.Events);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(InputDeviceWatcher.DefaultRecoveryInterval, rig.HealthTimer.DueTime); // checking again, on the interval
    }

    [Fact]
    public void The_check_while_listening_keeps_to_the_recovery_bound_and_warns_once()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Watcher.SetWatched(true);
        rig.Notifications.ProbeFailure = () => new COMException("Server unavailable.", AudioServiceFailure.RpcServerUnavailable);
        rig.Notifications.RegisterFailure = () => new COMException("Not running.", AudioServiceFailure.AudioServiceNotRunning);

        for (var tick = 0; tick < 3; tick++)
        {
            rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
            rig.HealthTimer.Fire();
        }

        Assert.Equal(1 + 3, rig.Notifications.RegisterAttempts); // one attempt per interval, never more
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);

        rig.Notifications.RegisterFailure = null;
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.HealthTimer.Fire();
        Assert.True(rig.Watcher.IsListening);
    }

    [Fact]
    public void While_nothing_listens_the_registration_waits_for_the_next_list_to_be_checked()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Watcher.SetWatched(true);
        rig.Watcher.SetWatched(false);
        Assert.Equal(Timeout.InfiniteTimeSpan, rig.HealthTimer.DueTime);
        rig.Notifications.ProbeFailure = () => new COMException("Disconnected.", AudioServiceFailure.Disconnected);

        // The canceled timer delivers nothing, and a tick that was already past it when the last listener left neither
        // checks nor arms the timer again.
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.HealthTimer.Fire();
        rig.Watcher.DeliverWatchedCheck();

        Assert.Equal(0, rig.Notifications.Probes);
        Assert.Equal(["register #1"], rig.Notifications.Events);
        Assert.Equal(Timeout.InfiniteTimeSpan, rig.HealthTimer.DueTime);
    }

    [Fact]
    public void The_last_listener_leaving_while_a_check_runs_stops_the_checks()
    {
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndShow();
        rig.Watcher.SetWatched(true);

        // The Settings window closes while the periodic check is probing the registration.
        rig.Notifications.ProbeFailure = () =>
        {
            rig.Watcher.SetWatched(false);
            return null!;
        };
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.HealthTimer.Fire();

        Assert.Equal(1, rig.Notifications.Probes);
        Assert.Equal(Timeout.InfiniteTimeSpan, rig.HealthTimer.DueTime); // not armed again
    }

    [Theory]
    [InlineData(unchecked((int)0x88890010), true)] // AUDCLNT_E_SERVICE_NOT_RUNNING
    [InlineData(unchecked((int)0x80010108), true)] // RPC_E_DISCONNECTED
    [InlineData(unchecked((int)0x800401FD), true)] // CO_E_OBJNOTCONNECTED
    [InlineData(unchecked((int)0x80010007), true)] // RPC_E_SERVER_DIED
    [InlineData(unchecked((int)0x80010012), true)] // RPC_E_SERVER_DIED_DNE
    [InlineData(unchecked((int)0x800706BA), true)] // RPC_S_SERVER_UNAVAILABLE
    [InlineData(unchecked((int)0x800706BE), true)] // RPC_S_CALL_FAILED
    [InlineData(unchecked((int)0x800706BF), true)] // RPC_S_CALL_FAILED_DNE
    [InlineData(unchecked((int)0x88890004), false)] // AUDCLNT_E_DEVICE_INVALIDATED: a device, not the service
    [InlineData(unchecked((int)0x80070490), false)] // E_NOTFOUND: no such device
    [InlineData(unchecked((int)0x80004005), false)] // E_FAIL
    public void Only_the_documented_codes_for_a_server_that_has_gone_count_as_losing_the_audio_service(int hresult, bool lost)
    {
        Assert.Equal(lost, AudioServiceFailure.IsConnectionLost(new COMException("x", hresult)));
        Assert.False(AudioServiceFailure.IsConnectionLost(new InvalidOperationException("x") { HResult = hresult }));
    }

    [Fact]
    public void The_change_line_names_devices_and_never_endpoint_ids()
    {
        var before = new List<AudioDevice> { Insta, Elgato };
        var now = new List<AudioDevice> { Elgato with { IsDefault = true }, new("usb", "USB Microphone", false) };

        var line = InputDeviceWatcher.DescribeChange(before, now);

        Assert.Contains("default='Mic In (Elgato)'", line);
        Assert.Contains("previousDefault='Microphone (Insta360)'", line);
        Assert.Contains("added='USB Microphone'", line);
        Assert.Contains("removed='Microphone (Insta360)'", line);
        Assert.Contains("inputs=2", line);
        Assert.DoesNotContain("insta'", line);
        Assert.DoesNotContain("usb'", line);
    }

    /// <summary>A watcher over a fake registration, the test's clock and the test's thread pool.</summary>
    private sealed class Rig
    {
        private readonly ConcurrentQueue<Action> _queued = new();
        private int _reads;

        public Rig(IReadOnlyList<AudioDevice> devices)
        {
            Devices = devices;
            Watcher = new InputDeviceWatcher(
                Notifications.Register,
                Read,
                Log,
                Time,
                _queued.Enqueue);
        }

        public FakeEndpointNotifications Notifications { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public CapturingLogger<InputDeviceWatcher> Log { get; } = new();

        public InputDeviceWatcher Watcher { get; }

        public volatile IReadOnlyList<AudioDevice> Devices;

        public Action? DuringRead { get; set; }

        /// <summary>Makes every reading fail, as it does while the audio service restarts.</summary>
        public volatile bool FailReads;

        public int Reads => Volatile.Read(ref _reads);

        public int QueuedCount => _queued.Count;

        /// <summary>Starts the watcher and shows a list, as Settings opening does: that reading is the baseline.</summary>
        public void StartAndShow()
        {
            Watcher.Start();
            Watcher.Read();
            RunQueued();
        }

        public void RunQueued()
        {
            while (_queued.TryDequeue(out var work))
            {
                work();
            }
        }

        // The quiet period's timer, delivering a tick as the pool would. The watcher makes it first.
        public void FireTimer() => Time.Timers[0].Fire();

        // The timer that checks the registration while something listens. The watcher makes it second.
        public ManualTimer HealthTimer => Time.Timers[1];

        private IReadOnlyList<AudioDevice> Read()
        {
            Interlocked.Increment(ref _reads);
            DuringRead?.Invoke();
            if (FailReads)
            {
                throw new COMException("Not running.", AudioServiceFailure.AudioServiceNotRunning);
            }

            return Devices;
        }
    }
}
