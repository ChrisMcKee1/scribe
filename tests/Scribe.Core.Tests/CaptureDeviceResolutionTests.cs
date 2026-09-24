using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Scribe.Core.Audio;
using Scribe.Core.Lifecycle;
using Scribe.Core.Models;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// Which microphone a dictation records from, through the real capture service and the real device layer over fake
/// endpoints. The reported bug: Windows Settings showed one default microphone, Scribe recorded from another (the
/// communications default), and changing the default in Windows never reached Scribe, restart or not. Also the chosen
/// microphone that is unplugged: Windows keeps it by its ID, so looking it up succeeds and only creating its capture
/// fails, which turned every press into "microphone unavailable" instead of recording from the default.
/// </summary>
public sealed class CaptureDeviceResolutionTests
{
    private const string Insta360 = "{0.0.1.00000000}.{insta360}";
    private const string Elgato = "{0.0.1.00000000}.{elgato}";
    private const string Yeti = "{0.0.1.00000000}.{yeti}";

    // 100 ms of 16 kHz mono float.
    private static readonly byte[] Packet = new byte[16_000 / 10 * sizeof(float)];

    /// <summary>The machine that reported it: console and multimedia on one device, communications on another.</summary>
    private static FakeCaptureEndpoints ReportedMachine()
    {
        var endpoints = new FakeCaptureEndpoints();
        endpoints.Add(Insta360, "Microphone (6- Insta360 Link 2 Pro)");
        endpoints.Add(Elgato, "Mic In (Elgato Wave Neo)");
        endpoints.SetDefaults(console: Insta360, multimedia: Insta360, communications: Elgato);
        return endpoints;
    }

    [Fact]
    public void Following_Windows_records_from_the_default_windows_settings_shows_not_the_communications_default()
    {
        var endpoints = ReportedMachine();
        using var service = endpoints.CreateService();

        Assert.True(service.Start());

        Assert.Equal("Microphone (6- Insta360 Link 2 Pro)", service.LastDeviceName);
        Assert.False(service.LastRequestedDeviceUnavailable);
        service.Stop();
    }

    [Theory]
    [InlineData(null, "multimedia", "communications", "multimedia")]
    [InlineData(null, null, "communications", "communications")]
    [InlineData("console", null, null, "console")]
    public void The_console_role_comes_first_then_multimedia_then_communications(
        string? console, string? multimedia, string? communications, string expected)
    {
        var endpoints = new FakeCaptureEndpoints();
        foreach (var id in new[] { "console", "multimedia", "communications" })
        {
            endpoints.Add(id, id);
        }

        endpoints.SetDefaults(console, multimedia, communications);
        using var service = endpoints.CreateService();

        Assert.True(service.Start());

        Assert.Equal(expected, service.LastDeviceName);
        service.Stop();
    }

    [Fact]
    public void Every_start_asks_windows_again_so_a_new_default_applies_from_the_next_press_without_a_restart()
    {
        var endpoints = ReportedMachine();
        using var service = endpoints.CreateService();
        Assert.True(service.Start());
        service.Stop();
        Assert.Equal(1, endpoints.ViewsOpened);

        // The user makes the Elgato the default in Windows Settings.
        endpoints.SetDefaults(console: Elgato, multimedia: Elgato, communications: Elgato);
        Assert.True(service.Start());

        Assert.Equal("Mic In (Elgato Wave Neo)", service.LastDeviceName);
        service.Stop();

        // One fresh view of the endpoints per start, each released before its start returned: nothing about the devices
        // is carried from one press to the next.
        Assert.Equal(2, endpoints.ViewsOpened);
        Assert.Equal(endpoints.ViewsOpened, endpoints.ViewsDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_chosen_microphone_that_is_unplugged_records_from_the_windows_default_instead(bool stateCheckedOnOpen)
    {
        // Without the state check the lookup succeeds, as GetDevice does for an unplugged endpoint, and it is creating the
        // capture that fails; the old code only fell back for the lookup.
        var endpoints = ReportedMachine();
        endpoints.CheckStateOnOpen = stateCheckedOnOpen;
        endpoints.Add(Yeti, "Blue Yeti", DeviceState.Unplugged);
        using var service = endpoints.CreateService();

        Assert.True(service.Start(Yeti));

        Assert.Equal("Microphone (6- Insta360 Link 2 Pro)", service.LastDeviceName);
        Assert.True(service.LastRequestedDeviceUnavailable);
        Assert.Contains(endpoints.Log.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("recording from the Windows default instead"));
        if (!stateCheckedOnOpen)
        {
            Assert.Contains("released Blue Yeti", endpoints.Events); // the endpoint that failed was let go
        }

        service.Stop();
    }

    [Fact]
    public void The_state_is_named_when_the_chosen_microphone_is_there_but_not_active()
    {
        var endpoints = ReportedMachine();
        endpoints.Add(Yeti, "Blue Yeti", DeviceState.Disabled);
        using var service = endpoints.CreateService();

        Assert.True(service.Start(Yeti));
        service.Stop();

        var warning = Assert.Single(endpoints.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Disabled", warning.Message);
        Assert.False(warning.Mentions(Yeti)); // the shape, not the endpoint ID
    }

    [Fact]
    public void A_chosen_microphone_windows_no_longer_knows_records_from_the_windows_default()
    {
        var endpoints = ReportedMachine();
        using var service = endpoints.CreateService();

        Assert.True(service.Start("{0.0.1.00000000}.{gone}"));

        Assert.Equal("Microphone (6- Insta360 Link 2 Pro)", service.LastDeviceName);
        Assert.True(service.LastRequestedDeviceUnavailable);
        service.Stop();
    }

    [Fact]
    public void A_chosen_microphone_that_is_there_records_from_it()
    {
        var endpoints = ReportedMachine();
        endpoints.Add(Yeti, "Blue Yeti");
        using var service = endpoints.CreateService();

        Assert.True(service.Start(Yeti));

        Assert.Equal("Blue Yeti", service.LastDeviceName);
        Assert.False(service.LastRequestedDeviceUnavailable);
        service.Stop();

        // It comes back: the next press without a choice follows Windows again, and says nothing fell back.
        Assert.True(service.Start());
        Assert.False(service.LastRequestedDeviceUnavailable);
        service.Stop();
    }

    [Fact]
    public void With_no_microphone_at_all_start_fails_and_leaves_nothing_open()
    {
        var endpoints = new FakeCaptureEndpoints();
        endpoints.Add(Yeti, "Blue Yeti", DeviceState.Unplugged);
        endpoints.SetDefaults(null, null, null);
        using var service = endpoints.CreateService();

        Assert.Throws<InvalidOperationException>(() => service.Start(Yeti));

        Assert.False(service.IsCapturing);
        Assert.Equal(endpoints.ViewsOpened, endpoints.ViewsDisposed);
        Assert.Empty(endpoints.Captures);
    }

    [Fact]
    public void The_list_flags_the_windows_default_and_the_communications_default_separately()
    {
        var endpoints = ReportedMachine();
        using var service = endpoints.CreateService();

        var devices = service.GetInputDevices();

        var insta = Assert.Single(devices, device => device.Id == Insta360);
        var elgato = Assert.Single(devices, device => device.Id == Elgato);
        Assert.True(insta.IsDefault);
        Assert.False(insta.IsCommunicationsDefault);
        Assert.False(elgato.IsDefault);
        Assert.True(elgato.IsCommunicationsDefault);
        Assert.Equal(endpoints.ViewsOpened, endpoints.ViewsDisposed);
    }

    [Fact]
    public void A_default_that_cannot_be_read_still_lists_every_microphone()
    {
        var endpoints = ReportedMachine();
        endpoints.FailDefaultLookupFor = Role.Console;
        using var service = endpoints.CreateService();

        var devices = service.GetInputDevices();

        Assert.Equal(2, devices.Count);
        Assert.True(Assert.Single(devices, device => device.Id == Insta360).IsDefault); // multimedia answers instead
    }

    [Fact]
    public void A_default_change_during_a_dictation_leaves_the_live_capture_on_its_device_and_the_next_press_follows_it()
    {
        var endpoints = ReportedMachine();
        using var service = endpoints.CreateService();
        Assert.True(service.Start());
        var live = endpoints.Capture;
        Speak(service, live);

        endpoints.SetDefaults(console: Elgato, multimedia: Elgato, communications: Elgato);
        Speak(service, live);
        var captured = service.Stop();

        Assert.Equal(2 * Packet.Length / sizeof(float), captured.Samples.Length); // nothing was cut or reopened
        Assert.Equal(1, live.DisposeCount);
        Assert.True(service.Start());
        Assert.Equal("Mic In (Elgato Wave Neo)", service.LastDeviceName);
        service.Stop();
    }

    [Fact]
    public void A_microphone_removed_during_a_dictation_faults_the_capture_and_the_next_press_resolves_afresh()
    {
        var endpoints = ReportedMachine();
        using var service = endpoints.CreateService();
        using var faulted = new ManualResetEventSlim();
        Exception? fault = null;
        service.CaptureFaulted += (_, error) =>
        {
            fault = error;
            faulted.Set();
        };
        Assert.True(service.Start());
        Speak(service, endpoints.Capture);

        // Unplugged: WASAPI fails the next packet read with AUDCLNT_E_DEVICE_INVALIDATED, and Windows moves the default.
        endpoints.SetState(Insta360, DeviceState.Unplugged);
        endpoints.SetDefaults(console: Elgato, multimedia: Elgato, communications: Elgato);
        endpoints.Capture.Fault(new COMException("The audio device has been disconnected.", FakeCaptureEndpoints.DeviceInvalidated));

        Assert.True(faulted.Wait(BlockedThreads.SafetyTimeout));
        Assert.Equal(FakeCaptureEndpoints.DeviceInvalidated, fault!.HResult);
        var captured = service.Stop();
        Assert.Equal(Packet.Length / sizeof(float), captured.Samples.Length); // what was recorded before the fault

        Assert.True(service.Start());
        Assert.Equal("Mic In (Elgato Wave Neo)", service.LastDeviceName);
        service.Stop();
    }

    [Fact]
    public void An_open_carries_the_fallback_of_its_own_start_and_one_that_opened_nothing_carries_none()
    {
        var endpoints = ReportedMachine();
        endpoints.Add(Yeti, "Blue Yeti", DeviceState.Unplugged);
        using var service = endpoints.CreateService();
        var lifecycle = new DictationLifecycle<string>(() => { }, () => { }, new ManualTimeProvider());
        lifecycle.Start(Timeout.InfiniteTimeSpan);

        var a = lifecycle.TryBeginRecording(() => "a");
        var fellBack = RecordingCapture.Open(lifecycle, service, a.DictationId, Yeti);
        Assert.Equal(RecordingOpenOutcome.Live, fellBack.Outcome);
        Assert.True(fellBack.RequestedDeviceUnavailable);
        Assert.Equal("Microphone (6- Insta360 Link 2 Pro)", fellBack.DeviceName);
        var stopA = lifecycle.TryBeginProcessing(a.DictationId);
        service.Stop(a.DictationId);
        lifecycle.ReturnToIdle(Timeout.InfiniteTimeSpan);
        lifecycle.EndProcessing(stopA.Admission!);

        // The next recording's stop reaches the capture service before its open, so nothing opens. The service's last
        // start, which fell back, belongs to the recording before.
        var b = lifecycle.TryBeginRecording(() => "b");
        service.RequestStop(b.DictationId);
        var nothing = RecordingCapture.Open(lifecycle, service, b.DictationId, Yeti);

        Assert.Equal(RecordingOpenOutcome.NotOpened, nothing.Outcome);
        Assert.True(service.LastRequestedDeviceUnavailable);
        Assert.False(nothing.RequestedDeviceUnavailable);
        Assert.Null(nothing.DeviceName);
    }

    [Fact]
    public void A_default_change_during_a_dictation_is_announced_while_the_live_capture_keeps_its_device()
    {
        var endpoints = ReportedMachine();
        var notifications = new FakeEndpointNotifications();
        var time = new ManualTimeProvider();
        var pool = new ConcurrentQueue<Action>();
        var service = endpoints.CreateService(devices =>
            new InputDeviceWatcher(notifications.Register, devices.GetInputDevices, endpoints.Log, time, pool.Enqueue));
        RunAll(pool); // the watcher's first reading
        IReadOnlyList<AudioDevice>? announced = null;
        service.InputDevicesChanged += devices => announced = devices;
        Assert.True(service.Start());
        var live = endpoints.Capture;
        Speak(service, live);

        // Windows Settings moves the default: one notification per role.
        endpoints.SetDefaults(console: Elgato, multimedia: Elgato, communications: Elgato);
        notifications.Raise(EndpointChange.CaptureDefault);
        notifications.Raise(EndpointChange.CaptureDefault);
        notifications.Raise(EndpointChange.CaptureDefault);
        RunAll(pool);
        time.Advance(InputDeviceWatcher.DefaultQuietPeriod);
        Assert.Single(time.Timers).Fire();

        Assert.NotNull(announced);
        Assert.True(Assert.Single(announced, device => device.Id == Elgato).IsDefault);
        Assert.True(service.IsCapturing);
        Speak(service, live);
        var captured = service.Stop();
        Assert.Equal(2 * Packet.Length / sizeof(float), captured.Samples.Length);
        Assert.Equal("Microphone (6- Insta360 Link 2 Pro)", service.LastDeviceName);

        Assert.True(service.Start());
        Assert.Equal("Mic In (Elgato Wave Neo)", service.LastDeviceName);
        service.Stop();

        // Disposal ends the registration first, unregistering before its enumerator is released.
        service.Dispose();
        Assert.Equal(["register #1", "unregister #1", "release #1"], notifications.Events);
        Assert.Equal(0, notifications.CallsInsideCallback);
    }

    private static void RunAll(ConcurrentQueue<Action> pool)
    {
        while (pool.TryDequeue(out var work))
        {
            work();
        }
    }

    // A stop is only a flag the capture loop reads between packets, as in NAudio, so a packet is waited for (its level
    // reaching the service) before anything that could stop the capture.
    private static void Speak(AudioCaptureService service, FakeCapture capture)
    {
        using var recorded = new ManualResetEventSlim();
        void OnLevel(object? sender, float level) => recorded.Set();
        service.LevelChanged += OnLevel;
        try
        {
            capture.Deliver(Packet);
            Assert.True(recorded.Wait(BlockedThreads.SafetyTimeout));
        }
        finally
        {
            service.LevelChanged -= OnLevel;
        }
    }
}
