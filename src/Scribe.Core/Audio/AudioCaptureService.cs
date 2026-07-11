using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Models;

namespace Scribe.Core.Audio;

/// <summary>
/// WASAPI shared-mode microphone capture. Records in the device's native mix format
/// (commonly 32-bit float, 44.1/48 kHz, 1-2 channels), then on stop downmixes to mono and
/// resamples to 16 kHz using the managed WDL resampler — no MediaFoundation dependency.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int TargetSampleRate = 16000;

    private readonly ILogger<AudioCaptureService> _logger;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _sync = new();

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private MemoryStream? _raw;
    private WaveFormat? _captureFormat;
    private ManualResetEventSlim? _stopped;
    private Exception? _captureError;
    private bool _stopRequested;

    public AudioCaptureService(ILogger<AudioCaptureService> logger) => _logger = logger;

    public bool IsCapturing { get; private set; }

    public string? LastDeviceName { get; private set; }

    public event EventHandler<float>? LevelChanged;

    public event EventHandler<Exception>? CaptureFaulted;

    public IReadOnlyList<AudioDevice> GetInputDevices()
    {
        string? defaultId = TryGetDefaultCaptureId();
        var devices = new List<AudioDevice>();

        foreach (MMDevice device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                devices.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
            finally
            {
                device.Dispose();
            }
        }

        return devices;
    }

    public void Start(string? deviceId = null)
    {
        lock (_sync)
        {
            if (IsCapturing)
            {
                _logger.LogWarning("Start called while already capturing; ignoring.");
                return;
            }

            try
            {
                _device = ResolveDevice(deviceId);
                _capture = new WasapiCapture(_device, useEventSync: true);
                _captureFormat = _capture.WaveFormat;
                _raw = new MemoryStream();
                _stopped = new ManualResetEventSlim(false);
                _captureError = null;
                _stopRequested = false;

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                LastDeviceName = _device.FriendlyName;

                _logger.LogInformation(
                    "Starting capture on '{Device}' at {Rate} Hz, {Channels} ch, {Bits}-bit {Encoding}.",
                    _device.FriendlyName,
                    _captureFormat.SampleRate,
                    _captureFormat.Channels,
                    _captureFormat.BitsPerSample,
                    _captureFormat.Encoding);

                _capture.StartRecording();
                IsCapturing = true;
            }
            catch
            {
                Cleanup(_capture, _raw, _stopped);
                throw;
            }
        }
    }

    public void RequestStop()
    {
        WasapiCapture? capture;
        lock (_sync)
        {
            if (!IsCapturing || _stopRequested)
            {
                return;
            }

            _stopRequested = true;
            capture = _capture;
        }

        try
        {
            capture?.StopRecording();
        }
        catch (Exception ex)
        {
            _captureError = ex;
            _stopped?.Set();
            CaptureFaulted?.Invoke(this, ex);
        }
    }

    public CapturedAudio Stop()
    {
        WasapiCapture? capture;
        MemoryStream? raw;
        WaveFormat? format;
        ManualResetEventSlim? stopped;

        lock (_sync)
        {
            if (!IsCapturing)
            {
                return CapturedAudio.Empty;
            }

            IsCapturing = false;
            capture = _capture;
            raw = _raw;
            format = _captureFormat;
            stopped = _stopped;
        }

        try
        {
            if (!_stopRequested)
            {
                capture?.StopRecording();
            }

            if (stopped is not null && !stopped.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("The microphone did not stop within three seconds.");
            }

            if (_captureError is not null)
            {
                _logger.LogError(_captureError, "Capture stopped due to an error.");
            }

            if (raw is null || format is null || raw.Length == 0)
            {
                return CapturedAudio.Empty;
            }

            float[] samples = ResampleToTarget(raw.GetBuffer(), (int)raw.Length, format);
            var captured = new CapturedAudio(samples, TargetSampleRate);
            _logger.LogInformation(
                "Capture complete: {Seconds:F2}s ({Samples} samples @ {Rate} Hz).",
                captured.Duration.TotalSeconds,
                samples.Length,
                TargetSampleRate);
            return captured;
        }
        finally
        {
            Cleanup(capture, raw, stopped);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        MemoryStream? raw = _raw;
        WaveFormat? format = _captureFormat;
        if (raw is null || format is null || e.BytesRecorded == 0)
        {
            return;
        }

        raw.Write(e.Buffer, 0, e.BytesRecorded);
        RaiseLevel(e.Buffer, e.BytesRecorded, format);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _captureError = e.Exception;
        _stopped?.Set();
        if (e.Exception is not null)
        {
            CaptureFaulted?.Invoke(this, e.Exception);
        }
    }

    private void RaiseLevel(byte[] buffer, int bytes, WaveFormat format)
    {
        EventHandler<float>? handler = LevelChanged;
        if (handler is null)
        {
            return;
        }

        float peak = ComputePeak(buffer, bytes, format);
        handler(this, peak);
    }

    private static float ComputePeak(byte[] buffer, int bytes, WaveFormat format)
    {
        float peak = 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes));
            foreach (float sample in samples)
            {
                float abs = Math.Abs(sample);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytes));
            foreach (short sample in samples)
            {
                float abs = Math.Abs(sample / 32768f);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private static float[] ResampleToTarget(byte[] bytes, int length, WaveFormat format)
    {
        var rawStream = new RawSourceWaveStream(bytes, 0, length, format);
        ISampleProvider source = rawStream.ToSampleProvider();

        ISampleProvider mono = format.Channels == 1
            ? source
            : new MonoDownmixSampleProvider(source);

        ISampleProvider resampled = mono.WaveFormat.SampleRate == TargetSampleRate
            ? mono
            : new WdlResamplingSampleProvider(mono, TargetSampleRate);

        return ReadAll(resampled);
    }

    internal static float[] ReadAll(ISampleProvider provider)
    {
        var samples = ArrayPool<float>.Shared.Rent(provider.WaveFormat.SampleRate);
        var count = 0;
        try
        {
            while (true)
            {
                if (count == samples.Length)
                {
                    var expanded = ArrayPool<float>.Shared.Rent(checked(samples.Length * 2));
                    samples.AsSpan(0, count).CopyTo(expanded);
                    ArrayPool<float>.Shared.Return(samples);
                    samples = expanded;
                }

                var read = provider.Read(samples, count, samples.Length - count);
                if (read <= 0)
                {
                    return samples.AsSpan(0, count).ToArray();
                }

                count += read;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }

    private MMDevice ResolveDevice(string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                return _enumerator.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Requested device '{DeviceId}' unavailable; falling back to default.", deviceId);
            }
        }

        if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        throw new InvalidOperationException("No active microphone (capture) device was found.");
    }

    private string? TryGetDefaultCaptureId()
    {
        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                using MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                return device.ID;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve default capture device.");
        }

        return null;
    }

    private void Cleanup(WasapiCapture? capture, MemoryStream? raw, ManualResetEventSlim? stopped)
    {
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }

        raw?.Dispose();
        stopped?.Dispose();
        _device?.Dispose();

        lock (_sync)
        {
            _capture = null;
            _raw = null;
            _captureFormat = null;
            _stopped = null;
            _device = null;
            _stopRequested = false;
        }
    }

    public void Dispose()
    {
        if (IsCapturing)
        {
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while stopping capture during dispose.");
            }
        }

        _enumerator.Dispose();
    }
}
