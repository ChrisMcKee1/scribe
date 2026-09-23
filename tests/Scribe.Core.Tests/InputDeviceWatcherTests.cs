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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();

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
        rig.StartAndReadFirst();

        rig.Notifications.Raise(EndpointChange.RenderDefault);
        rig.Notifications.Raise(EndpointChange.OtherProperty);

        Assert.Equal(0, rig.QueuedCount);
    }

    [Fact]
    public void Nothing_is_announced_when_the_microphones_did_not_change()
    {
        // A render device plugged in raises device notifications too; the endpoints are read and found the same.
        var rig = new Rig([Insta, Elgato]);
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        rig.StartAndReadFirst();
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
        Assert.Equal(1, rig.Reads); // the first reading still happens

        rig.Notifications.RegisterFailure = null;
        rig.Watcher.EnsureListening(); // within the bound since the failed attempt
        Assert.False(rig.Watcher.IsListening);
        rig.Time.Advance(InputDeviceWatcher.DefaultRecoveryInterval);
        rig.Watcher.EnsureListening();
        Assert.True(rig.Watcher.IsListening);
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

        public int Reads => Volatile.Read(ref _reads);

        public int QueuedCount => _queued.Count;

        public void StartAndReadFirst()
        {
            Watcher.Start();
            RunQueued();
        }

        public void RunQueued()
        {
            while (_queued.TryDequeue(out var work))
            {
                work();
            }
        }

        // The quiet period's timer, delivering a tick as the pool would.
        public void FireTimer() => Assert.Single(Time.Timers).Fire();

        private IReadOnlyList<AudioDevice> Read()
        {
            Interlocked.Increment(ref _reads);
            DuringRead?.Invoke();
            return Devices;
        }
    }
}
