using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Scribe.Core.Audio;

namespace Scribe.Core.Tests.Concurrency;

/// <summary>
/// Fake capture endpoints behind the per-operation view seam, shaped by what was measured on real endpoints: every view
/// is a fresh enumerator that refuses use after it is disposed; the defaults are read live, per role; looking up an
/// endpoint Windows still knows succeeds whatever its state (IMMDeviceEnumerator::GetDevice fails only for an unknown ID,
/// with E_NOTFOUND), unless the production state check is modeled; and creating a capture on an endpoint that is not
/// active fails with AUDCLNT_E_DEVICE_INVALIDATED, as activating its audio client does. Captures are the shared
/// <see cref="FakeCapture"/>, so they behave like NAudio 3.0.1's WasapiCapture.
/// </summary>
internal sealed class FakeCaptureEndpoints
{
    public const int NotFound = unchecked((int)0x80070490);
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentQueue<FakeCapture> _captures = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<Role, string?> _defaults = new();
    private int _viewsOpened;
    private int _viewsDisposed;

    /// <summary>
    /// True to refuse an endpoint that is not active at lookup, as <see cref="WasapiCaptureEndpoints"/> does; false to hand
    /// it out as IMMDeviceEnumerator::GetDevice itself does, leaving the failure to capture creation.
    /// </summary>
    public bool CheckStateOnOpen { get; set; } = true;

    /// <summary>Fails reading the default of this role, as a lookup the audio stack refuses does.</summary>
    public Role? FailDefaultLookupFor { get; set; }

    public CapturingLogger<AudioCaptureService> Log { get; } = new();

    public int ViewsOpened => Volatile.Read(ref _viewsOpened);

    public int ViewsDisposed => Volatile.Read(ref _viewsDisposed);

    public IReadOnlyList<string> Events => [.. _events];

    public IReadOnlyList<FakeCapture> Captures => [.. _captures];

    public FakeCapture Capture => Captures is [.., var last] ? last : throw new InvalidOperationException("Nothing was opened.");

    public void Add(string id, string name, DeviceState state = DeviceState.Active)
    {
        lock (_sync)
        {
            _endpoints[id] = new Endpoint(id, name, state);
        }
    }

    public void SetState(string id, DeviceState state)
    {
        lock (_sync)
        {
            _endpoints[id] = _endpoints[id] with { State = state };
        }
    }

    public void SetDefaults(string? console, string? multimedia, string? communications)
    {
        lock (_sync)
        {
            _defaults[Role.Console] = console;
            _defaults[Role.Multimedia] = multimedia;
            _defaults[Role.Communications] = communications;
        }
    }

    public ICaptureEndpoints OpenView()
    {
        Interlocked.Increment(ref _viewsOpened);
        return new View(this);
    }

    /// <summary>The real device layer over these endpoints.</summary>
    public WasapiCaptureDevices CreateDevices() => new(Log, OpenView);

    /// <summary>The real capture service over the real device layer over these endpoints.</summary>
    public AudioCaptureService CreateService(Func<ICaptureDevices, InputDeviceWatcher>? watch = null) =>
        new(Log, CreateDevices(), BlockedThreads.SafetyTimeout, watch);

    private Endpoint? Find(string id)
    {
        lock (_sync)
        {
            return _endpoints.GetValueOrDefault(id);
        }
    }

    private string? DefaultFor(Role role)
    {
        lock (_sync)
        {
            return _defaults.GetValueOrDefault(role);
        }
    }

    private List<Endpoint> Active()
    {
        lock (_sync)
        {
            return [.. _endpoints.Values.Where(endpoint => endpoint.State == DeviceState.Active)];
        }
    }

    private sealed record Endpoint(string Id, string Name, DeviceState State);

    private sealed class View(FakeCaptureEndpoints owner) : ICaptureEndpoints
    {
        private bool _disposed;

        public ICaptureDevice? OpenDefault(Role role)
        {
            ThrowIfDisposed();
            return DefaultId(role) is { } id ? Open(id) : null;
        }

        public string? DefaultId(Role role)
        {
            ThrowIfDisposed();
            if (owner.FailDefaultLookupFor == role)
            {
                throw new COMException("The lookup failed.", unchecked((int)0x80004005));
            }

            return owner.DefaultFor(role);
        }

        public ICaptureDevice Open(string deviceId)
        {
            ThrowIfDisposed();
            var endpoint = owner.Find(deviceId) ?? throw new COMException("Element not found.", NotFound);
            if (owner.CheckStateOnOpen && endpoint.State != DeviceState.Active)
            {
                throw new CaptureDeviceUnavailableException(endpoint.State);
            }

            owner._events.Enqueue($"opened {endpoint.Name}");
            return new Device(owner, endpoint);
        }

        public IReadOnlyList<CaptureEndpoint> ListActive()
        {
            ThrowIfDisposed();
            return [.. owner.Active().Select(endpoint => new CaptureEndpoint(endpoint.Id, endpoint.Name))];
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Interlocked.Increment(ref owner._viewsDisposed);
            }
        }

        // A view is one enumerator for one operation; using it again afterwards is exactly what must never happen.
        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Device(FakeCaptureEndpoints owner, Endpoint endpoint) : ICaptureDevice
    {
        public string FriendlyName => endpoint.Name;

        public bool IsMuted => false;

        public IWaveIn CreateCapture()
        {
            // The live state, as activating an audio client sees it.
            if (owner.Find(endpoint.Id)?.State != DeviceState.Active)
            {
                throw new COMException("The audio device has been disconnected.", DeviceInvalidated);
            }

            var capture = new FakeCapture(owner._events);
            owner._captures.Enqueue(capture);
            return capture;
        }

        public void Dispose() => owner._events.Enqueue($"released {endpoint.Name}");
    }
}
