using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;

// WasapiCapture is obsoleted by NAudio 3 in favor of WasapiRecorder, and the capture below stays on it by informed
// choice; the reason is on Endpoint.CreateCapture.
#pragma warning disable CS0618

namespace Scribe.Core.Audio;

/// <summary>
/// The capture endpoints behind <see cref="AudioCaptureService"/>: the one seam between it and the audio stack, so its
/// locking and disposal can be tested without a microphone. The production implementation is
/// <see cref="WasapiCaptureDevices"/>.
/// </summary>
internal interface ICaptureDevices : IDisposable
{
    /// <summary>
    /// Opens the active endpoint with this id. Throws when it is unavailable, including an endpoint Windows still knows
    /// but that is unplugged, disabled or not present.
    /// </summary>
    ICaptureDevice Open(string deviceId);

    /// <summary>
    /// Opens the Windows default capture endpoint as it is right now (see <see cref="DefaultInputDevice"/>); null when
    /// there is none.
    /// </summary>
    ICaptureDevice? OpenDefault();

    /// <summary>Lists the active capture endpoints, flagging the default and the communications default.</summary>
    IReadOnlyList<AudioDevice> GetInputDevices();
}

/// <summary>One opened capture endpoint. Disposing it releases the endpoint.</summary>
internal interface ICaptureDevice : IDisposable
{
    string FriendlyName { get; }

    /// <summary>True when the endpoint is muted or its volume is at zero. May throw for a driver with no endpoint volume.</summary>
    bool IsMuted { get; }

    /// <summary>A capture on this endpoint, not yet started.</summary>
    IWaveIn CreateCapture();
}

/// <summary>
/// One short-lived view of the capture endpoints, over a device enumerator of its own, opened for a single operation
/// and disposed when it ends. A device it opened stays usable after the view is gone.
/// </summary>
internal interface ICaptureEndpoints : IDisposable
{
    /// <summary>The default capture endpoint of <paramref name="role"/>, opened; null when Windows has none.</summary>
    ICaptureDevice? OpenDefault(Role role);

    /// <summary>The endpoint ID string of the default capture endpoint of <paramref name="role"/>; null when there is none.</summary>
    string? DefaultId(Role role);

    /// <summary>
    /// Opens the endpoint with this id. Throws <see cref="CaptureDeviceUnavailableException"/> when Windows knows it but it
    /// is not active, and what the enumerator throws when Windows does not know it at all.
    /// </summary>
    ICaptureDevice Open(string deviceId);

    /// <summary>Every capture endpoint that is active now.</summary>
    IReadOnlyList<CaptureEndpoint> ListActive();
}

/// <summary>An active capture endpoint: its endpoint ID string and its friendly name.</summary>
internal readonly record struct CaptureEndpoint(string Id, string Name);

/// <summary>
/// The endpoint exists but is not active. IMMDeviceEnumerator::GetDevice fails only for an ID that "does not identify an
/// audio device that is in this system"
/// (https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdevice), so it
/// returns an unplugged, disabled or not present endpoint as readily as a working one; activating a capture on such an
/// endpoint then fails with AUDCLNT_E_DEVICE_INVALIDATED. Both were measured on real endpoints in every one of those
/// states before this check was added.
/// </summary>
internal sealed class CaptureDeviceUnavailableException(DeviceState state)
    : InvalidOperationException($"The capture endpoint is {state}, not active.")
{
    /// <summary>What Windows reports for the endpoint (DEVICE_STATE_XXX).</summary>
    public DeviceState State { get; } = state;
}

/// <summary>
/// The real endpoints. Every operation opens a view with a device enumerator of its own and releases it before it
/// returns, so nothing about the audio devices is held from one dictation to the next: each press asks Windows which
/// microphone is the default at that moment, and a device enumerator left over from before an audio service restart can
/// never answer for it.
/// </summary>
/// <remarks>
/// Mozilla's cubeb audio library does the same, creating an enumerator per default-device lookup, and a device opened
/// through a view keeps working after the view's enumerator is released: its own COM reference keeps it alive, which was
/// measured here too (name, state, endpoint volume and audio client activation all worked on a device whose enumerator
/// had been released). A lookup costs a couple of milliseconds, far below what opening the capture itself costs.
/// </remarks>
internal sealed class WasapiCaptureDevices : ICaptureDevices
{
    private readonly ILogger _logger;
    private readonly Func<ICaptureEndpoints> _openView;

    public WasapiCaptureDevices(ILogger logger)
        : this(logger, WasapiCaptureEndpoints.Create)
    {
    }

    /// <summary>Test seam: where each operation's view of the endpoints comes from.</summary>
    internal WasapiCaptureDevices(ILogger logger, Func<ICaptureEndpoints> openView)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(openView);
        _logger = logger;
        _openView = openView;
    }

    public ICaptureDevice Open(string deviceId)
    {
        using var endpoints = _openView();
        return endpoints.Open(deviceId);
    }

    public ICaptureDevice? OpenDefault()
    {
        using var endpoints = _openView();
        foreach (var role in DefaultInputDevice.RolePreference)
        {
            if (endpoints.OpenDefault(role) is { } device)
            {
                return device;
            }
        }

        return null;
    }

    public IReadOnlyList<AudioDevice> GetInputDevices()
    {
        using var endpoints = _openView();

        // A default that cannot be read still leaves the list worth showing: every device stays choosable.
        var defaults = DefaultInputDevice.Resolve(role => TryReadDefault(endpoints, role));
        return DefaultInputDevice.Describe(endpoints.ListActive(), defaults);
    }

    // Nothing is held between operations, so there is nothing to release.
    public void Dispose()
    {
    }

    private string? TryReadDefault(ICaptureEndpoints endpoints, Role role)
    {
        try
        {
            return endpoints.DefaultId(role);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Could not read the default capture device for the {Role} role ({Failure}).", role, FailureShape.Describe(ex));
            return null;
        }
    }
}

/// <summary>A view of the endpoints through a device enumerator of its own, released with the view.</summary>
internal sealed class WasapiCaptureEndpoints : ICaptureEndpoints
{
    private readonly MMDeviceEnumerator _enumerator = new();

    private WasapiCaptureEndpoints()
    {
    }

    public static ICaptureEndpoints Create() => new WasapiCaptureEndpoints();

    public ICaptureDevice? OpenDefault(Role role) =>
        _enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, role, out var device) && device is not null
            ? new Endpoint(device)
            : null;

    public string? DefaultId(Role role)
    {
        if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, role, out var device) || device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    }

    public ICaptureDevice Open(string deviceId)
    {
        var device = _enumerator.GetDevice(deviceId);
        try
        {
            var state = device.State;
            if (state != DeviceState.Active)
            {
                throw new CaptureDeviceUnavailableException(state);
            }

            return new Endpoint(device);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public IReadOnlyList<CaptureEndpoint> ListActive()
    {
        using var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var endpoints = new List<CaptureEndpoint>(collection.Count);
        foreach (var device in collection)
        {
            using (device)
            {
                endpoints.Add(new CaptureEndpoint(device.ID, device.FriendlyName));
            }
        }

        return endpoints;
    }

    public void Dispose() => _enumerator.Dispose();

    private sealed class Endpoint(MMDevice device) : ICaptureDevice
    {
        public string FriendlyName => device.FriendlyName;

        public bool IsMuted
        {
            get
            {
                var volume = device.AudioEndpointVolume;
                return volume.Mute || volume.MasterVolumeLevelScalar <= 0.0001f;
            }
        }

        // Deliberately the obsoleted WasapiCapture, not NAudio 3's WasapiRecorder. The
        // recorder was tried (0.3.16) and produced two live capture regressions in one
        // day on the same microphone: the mix format arrived with its extensible header
        // intact (blinding every Encoding-switch consumer), and captured speech came in
        // roughly 20 dB quieter than WasapiCapture on the same endpoint (peaks that were
        // -14 dBFS became -35 dBFS, so VAD rejected real dictations as silence - the
        // recorder evidently taps the stream at a different point in the effects/AGC
        // chain). Correct levels beat one saved buffer copy; do not swap this back
        // without A/B-ing recorded peaks on real hardware.
        public IWaveIn CreateCapture() => new WasapiCapture(device, useEventSync: true);

        public void Dispose() => device.Dispose();
    }
}
