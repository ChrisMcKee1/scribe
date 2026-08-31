using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Models;

// WasapiCapture is obsoleted by NAudio 3 in favor of WasapiRecorder, and this file stays on it by
// informed choice: the recorder produced two live capture regressions on real hardware (an
// un-unwrapped extensible mix format and ~20 dB quieter capture). See the comment in Start().
#pragma warning disable CS0618

namespace Scribe.Core.Audio;

/// <summary>
/// WASAPI shared-mode microphone capture. Records in the device's native mix format
/// (commonly 32-bit float, 44.1/48 kHz, 1-2 channels), then on stop downmixes to mono and
/// resamples to 16 kHz using the managed WDL resampler; no MediaFoundation dependency.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int TargetSampleRate = 16000;

    // Peak below this over a whole capture means the endpoint delivered digital silence. A live
    // microphone always has an analog noise floor well above -60 dBFS; a muted endpoint (Teams
    // hardware-mute sync, the Win11 taskbar mic mute, a headset mute switch) streams exact zeros.
    internal const float SilentCapturePeak = 0.001f;

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

    // Running peak across the whole capture. Written on the audio callback thread, read after the
    // stop handshake; float writes are atomic so no extra locking is needed.
    private float _capturePeak;

    public AudioCaptureService(ILogger<AudioCaptureService> logger) => _logger = logger;

    public bool IsCapturing { get; private set; }

    public string? LastDeviceName { get; private set; }

    public bool LastDeviceMuted { get; private set; }

    public bool LastCaptureWasSilent => _capturePeak < SilentCapturePeak;

    /// <summary>
    /// Measured shape of the most recent completed capture: levels, clipping, DC offset and what
    /// each channel contributed before the downmix. Null until a capture has completed. Statistics
    /// only, never audio.
    /// </summary>
    public CaptureSignalReport? LastSignalReport { get; private set; }

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

                // Deliberately the obsoleted WasapiCapture, not NAudio 3's WasapiRecorder. The
                // recorder was tried (0.3.16) and produced two live capture regressions in one
                // day on the same microphone: the mix format arrived with its extensible header
                // intact (blinding every Encoding-switch consumer), and captured speech came in
                // roughly 20 dB quieter than WasapiCapture on the same endpoint (peaks that were
                // -14 dBFS became -35 dBFS, so VAD rejected real dictations as silence - the
                // recorder evidently taps the stream at a different point in the effects/AGC
                // chain). Correct levels beat one saved buffer copy; do not swap this back
                // without A/B-ing recorded peaks on real hardware.
                _capture = new WasapiCapture(_device, useEventSync: true);
                _captureFormat = NormalizeFormat(_capture.WaveFormat);

                // Pre-size for ~30 s at the device's native rate (typically ~11 MB at 48 kHz
                // stereo float). MemoryStream grows by doubling, and every doubling of a large
                // buffer momentarily holds old + new on the LOH; starting at a realistic capture
                // length removes the churn for the common case without meaningfully overpaying
                // for short presses.
                var presizeBytes = Math.Clamp(
                    _captureFormat.AverageBytesPerSecond * 30L, 64 * 1024, 32 * 1024 * 1024);
                _raw = new MemoryStream((int)presizeBytes);
                _stopped = new ManualResetEventSlim(false);
                _captureError = null;
                _stopRequested = false;

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                LastDeviceName = _device.FriendlyName;

                // An unmeterable format leaves the peak blind; seed it above the silence threshold
                // so LastCaptureWasSilent can never false-positive a "muted" error for it.
                _capturePeak = IsMeterableFormat(_captureFormat) ? 0f : 1f;
                LastDeviceMuted = ProbeEndpointMuted(_device);
                if (LastDeviceMuted)
                {
                    _logger.LogWarning(
                        "Capture device '{Device}' is muted at the endpoint; the capture will contain silence.",
                        _device.FriendlyName);
                }

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

            // Measured on the RAW buffer, before the downmix, so per-channel levels are still
            // visible. Once channels are averaged the evidence is gone, and "what was on the other
            // channel" is the question a multi-channel headset or speakerphone always raises.
            LastSignalReport = AnalyzeSafely(raw, format);

            float[] samples = ResampleToTarget(raw.GetBuffer(), (int)raw.Length, format);
            var captured = new CapturedAudio(samples, TargetSampleRate);

            // The signal shape is on this line deliberately. It separates failure modes that produce
            // an identical-looking capture: a stream that stopped delivering, a microphone that was
            // never live, a gain so low the recognizer sees nothing usable, and a channel being
            // averaged away. None of those are distinguishable from a duration alone.
            _logger.LogInformation(
                "Capture complete: {Seconds:F2}s ({Samples} samples @ {Rate} Hz) from {Signal}",
                captured.Duration.TotalSeconds,
                samples.Length,
                TargetSampleRate,
                LastSignalReport.Describe());

            WarnAboutSignalProblems(LastSignalReport);
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

        // Peak is computed unconditionally (not just for the level meter): the running maximum is
        // what lets the pipeline tell "you spoke while muted" apart from "no speech in the audio".
        float peak = ComputePeak(e.Buffer.AsSpan(0, e.BytesRecorded), format);
        if (peak > _capturePeak)
        {
            _capturePeak = peak;
        }

        LevelChanged?.Invoke(this, peak);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _captureError = e.Exception;
        _stopped?.Set();
        if (e.Exception is not null)
        {
            _logger.LogError(e.Exception, "The capture stream on '{Device}' faulted.", LastDeviceName ?? "unknown");
            CaptureFaulted?.Invoke(this, e.Exception);
            return;
        }

        if (_stopRequested)
        {
            _logger.LogDebug("Capture on '{Device}' stopped as requested.", LastDeviceName ?? "unknown");
            return;
        }

        // WASAPI ended the stream cleanly without anyone asking. Nothing above this point can tell:
        // there is no exception, so CaptureFaulted does not fire, and the controller keeps believing
        // it is recording until the user releases the key. Everything spoken from here on is gone.
        // Windows does this when the endpoint is reconfigured under a live capture: an effects
        // pipeline engaging, another app taking exclusive mode, a Bluetooth profile switch, or a
        // driver reset. It is logged loudly because it is invisible everywhere else.
        _logger.LogWarning(
            "The capture stream on '{Device}' ended on its own after {Seconds:F2}s without an error " +
            "and without being asked to stop. Audio spoken after this point was not recorded.",
            LastDeviceName ?? "unknown",
            CapturedSecondsSoFar());
    }

    private CaptureSignalReport AnalyzeSafely(MemoryStream raw, WaveFormat format)
    {
        try
        {
            return CaptureSignalAnalyzer.Analyze(raw.GetBuffer().AsSpan(0, (int)raw.Length), format);
        }
        catch (Exception ex)
        {
            // Diagnostics never break a dictation: a capture the analyzer cannot describe is still
            // a capture the user wants transcribed.
            _logger.LogDebug(ex, "Could not analyze the capture signal.");
            return new CaptureSignalReport(format.Channels, format.SampleRate, 0, 0, 0, 0, 0, []);
        }
    }

    /// <summary>
    /// Calls out the signal problems that silently ruin a decode. Each of these leaves a capture
    /// that looks perfectly healthy from the outside: the level meter moves, the duration is right,
    /// and the recognizer then returns little or nothing.
    /// </summary>
    private void WarnAboutSignalProblems(CaptureSignalReport signal)
    {
        if (signal.PerChannel.Count == 0)
        {
            return;
        }

        if (signal.HasSilentChannel)
        {
            _logger.LogWarning(
                "Capture device '{Device}' delivered {Channels} channels and at least one carries no " +
                "audio. Scribe averages channels, so the speech reaching the recognizer is quieter " +
                "than the microphone actually recorded. Signal: {Signal}",
                LastDeviceName ?? "unknown", signal.Channels, signal.Describe());
        }
        else if (signal.ChannelsDiverge)
        {
            _logger.LogWarning(
                "Capture device '{Device}' delivered channels with very different levels, which is " +
                "what a reference or echo-cancellation channel looks like. Averaging them mixes that " +
                "channel into the speech. Signal: {Signal}",
                LastDeviceName ?? "unknown", signal.Describe());
        }

        // -45 dBFS peak over a whole utterance is far below anything a working microphone produces
        // for someone speaking to it, and well above the digital-silence floor that is all
        // LastCaptureWasSilent can detect.
        if (signal.Peak > 0 && signal.PeakDbfs < -45)
        {
            _logger.LogWarning(
                "Capture from '{Device}' peaked at only {Peak:F1} dBFS. Recognition is unreliable at " +
                "this level; the input gain is very low, or the speaker is far from the microphone. " +
                "Signal: {Signal}",
                LastDeviceName ?? "unknown", signal.PeakDbfs, signal.Describe());
        }

        if (signal.ClippedFraction > 0.01)
        {
            _logger.LogWarning(
                "Capture from '{Device}' clipped on {Fraction:P1} of samples. Signal: {Signal}",
                LastDeviceName ?? "unknown", signal.ClippedFraction, signal.Describe());
        }
    }

    /// <summary>
    /// Seconds of audio buffered so far, derived from the raw byte count. Used only for diagnostics,
    /// so a torn read of the stream length is acceptable and never worth a lock on a callback path.
    /// </summary>
    private double CapturedSecondsSoFar()
    {
        try
        {
            var raw = _raw;
            var format = _captureFormat;
            if (raw is null || format is null || format.AverageBytesPerSecond <= 0)
            {
                return 0;
            }

            return raw.Length / (double)format.AverageBytesPerSecond;
        }
        catch
        {
            return 0;
        }
    }

    // Endpoint-level mute (or a volume slider at zero) means WASAPI happily records zeros: the
    // classic "muted in a Teams meeting" case. Probed once per capture so the controller can warn
    // the user the moment recording starts instead of silently discarding the capture afterwards.
    private bool ProbeEndpointMuted(MMDevice device)
    {
        try
        {
            var volume = device.AudioEndpointVolume;
            return volume.Mute || volume.MasterVolumeLevelScalar <= 0.0001f;
        }
        catch (Exception ex)
        {
            // Some drivers expose no endpoint-volume interface; treat as not muted.
            _logger.LogDebug(ex, "Could not read the endpoint mute state.");
            return false;
        }
    }

    /// <summary>
    /// Unwraps a WAVEFORMATEXTENSIBLE header to its underlying standard format. WASAPI reports the
    /// shared mix format as extensible, and the old WasapiCapture normalized it before anyone saw
    /// it; WasapiRecorder (NAudio 3) hands it over raw. Left extensible, every consumer that
    /// switches on <see cref="WaveFormat.Encoding"/> goes blind at once: the peak meter reads
    /// zero, silence auto-stop never fires, the signal analyzer reports an empty -99 dBFS capture,
    /// and the "muted?" heuristic is disabled - which is exactly what a support log showed the day
    /// NAudio 3 landed (IeeeFloat captures at -14 dBFS became Extensible captures at -99).
    /// The raw audio bytes are identical either way; only the header interpretation changes.
    /// </summary>
    internal static WaveFormat NormalizeFormat(WaveFormat format)
    {
        if (format is not WaveFormatExtensible extensible)
        {
            return format;
        }

        try
        {
            return extensible.ToStandardWaveFormat();
        }
        catch (InvalidOperationException)
        {
            // An exotic subformat has no standard equivalent. Keep the extensible header: the
            // meter stays blind for that device (seeded non-silent, as before), but capture and
            // resampling still work.
            return format;
        }
    }

    internal static float ComputePeak(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        float peak = 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
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
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer);
            foreach (short sample in samples)
            {
                float abs = Math.Abs(sample / 32768f);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            ReadOnlySpan<int> samples = MemoryMarshal.Cast<byte, int>(buffer);
            foreach (int sample in samples)
            {
                float abs = Math.Abs(sample / 2147483648f);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
        {
            ReadOnlySpan<byte> raw = buffer[..(buffer.Length - (buffer.Length % 3))];
            for (var i = 0; i + 2 < raw.Length; i += 3)
            {
                // Little-endian 24-bit sample, sign-extended via a shifted 32-bit build-up.
                int sample = (raw[i] << 8) | (raw[i + 1] << 16) | (raw[i + 2] << 24);
                float abs = Math.Abs(sample / 2147483648f);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    // Formats the peak meter can measure. Anything else leaves the level (and therefore the
    // silent-capture heuristic) blind, so callers must not report "muted" for those captures.
    internal static bool IsMeterableFormat(WaveFormat format) =>
        (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        || (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32);

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

                var read = provider.Read(samples.AsSpan(count));
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
