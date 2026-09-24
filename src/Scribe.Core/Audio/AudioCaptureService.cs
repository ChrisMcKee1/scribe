using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;

namespace Scribe.Core.Audio;

/// <summary>
/// WASAPI shared-mode microphone capture. Records in the device's native mix format
/// (commonly 32-bit float, 44.1/48 kHz, 1-2 channels), then on stop downmixes to mono and
/// resamples to 16 kHz using the managed WDL resampler; no MediaFoundation dependency.
/// </summary>
/// <remarks>
/// The device is resolved on every <see cref="Start"/>: the chosen microphone when one is chosen and available, otherwise
/// the Windows default as it is at that moment (see <see cref="DefaultInputDevice"/>). Nothing about the devices is kept
/// from one capture to the next, so a new Windows default applies from the next dictation without a restart, while a
/// capture already running stays on the device it opened.
/// </remarks>
public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int TargetSampleRate = 16000;

    // Peak below this over a whole capture means the endpoint delivered digital silence. A live
    // microphone always has an analog noise floor well above -60 dBFS; a muted endpoint (Teams
    // hardware-mute sync, the Win11 taskbar mic mute, a headset mute switch) streams exact zeros.
    internal const float SilentCapturePeak = 0.001f;

    private readonly ILogger<AudioCaptureService> _logger;
    private readonly ICaptureDevices _devices;
    private readonly InputDeviceWatcher? _watcher;
    private readonly TimeSpan _disposeOpenWait;
    private readonly CaptureBufferPool _buffers = new();
    private readonly object _sync = new();

    // The listeners for InputDevicesChanged, kept here so the watcher knows whether anything is listening.
    private readonly object _subscriptionGate = new();
    private Action<IReadOnlyList<AudioDevice>>? _inputDevicesChanged;

    private IWaveIn? _capture;
    private ICaptureDevice? _device;
    private CaptureRecording? _raw;
    private WaveFormat? _captureFormat;
    private ManualResetEventSlim? _stopped;
    private Exception? _captureError;
    private bool _stopRequested;

    // Under _sync. The recording the live capture belongs to (0 for none), and the highest recording whose stop has
    // reached this service. Start refuses a recording at or below that mark, so a recording stopped before its microphone
    // opened is never handed a capture nobody will stop; and a stop that names a recording touches only that recording's
    // capture, so its processing receives every sample exactly once, whoever else is stopping.
    private long _captureOwner;
    private long _stoppedThrough;

    // Set once disposal begins, before it waits for the lock, so a Start that reaches the lock afterwards is refused. An
    // open already in progress finishes normally, and disposal stops what it opened once it has the lock.
    private int _disposing;

    // Running peak across the whole capture. Written on the audio callback thread, read after the
    // stop handshake; float writes are atomic so no extra locking is needed.
    private float _capturePeak;

    /// <summary>
    /// How long disposal waits for a device open in progress. Opens have been measured at over five seconds on the first
    /// press after launch; past this bound the capture endpoints are left for the process to release at exit, rather than
    /// released under the open that is still using them. It bounds only that wait, not the stop that follows it.
    /// </summary>
    internal static readonly TimeSpan DisposeOpenWait = TimeSpan.FromSeconds(10);

    public AudioCaptureService(ILogger<AudioCaptureService> logger)
        : this(logger, new WasapiCaptureDevices(logger), DisposeOpenWait, WatchWindowsEndpoints(logger))
    {
    }

    /// <summary>
    /// Test seam: the capture endpoints, the bound disposal waits for an open in progress, and what watches the endpoints
    /// for changes (nothing when null).
    /// </summary>
    internal AudioCaptureService(
        ILogger<AudioCaptureService> logger,
        ICaptureDevices devices,
        TimeSpan disposeOpenWait,
        Func<ICaptureDevices, InputDeviceWatcher>? watch = null)
    {
        _logger = logger;
        _devices = devices;
        _disposeOpenWait = disposeOpenWait;
        if (watch is not null)
        {
            // Start never throws: a watcher that cannot register logs it and tries again when the list is next read.
            _watcher = watch(devices);
            _watcher.Changed += RaiseInputDevicesChanged;
            _watcher.Start();
        }
    }

    public bool IsCapturing { get; private set; }

    private bool Disposing => Volatile.Read(ref _disposing) != 0;

    public string? LastDeviceName { get; private set; }

    public bool LastDeviceMuted { get; private set; }

    public bool LastRequestedDeviceUnavailable { get; private set; }

    public bool LastCaptureWasSilent => _capturePeak < SilentCapturePeak;

    /// <summary>
    /// Measured shape of the most recent completed capture: levels, clipping, DC offset and what
    /// each channel contributed before the downmix. Null until a capture has completed. Statistics
    /// only, never audio.
    /// </summary>
    public CaptureSignalReport? LastSignalReport { get; private set; }

    public event EventHandler<float>? LevelChanged;

    public event EventHandler<Exception>? CaptureFaulted;

    /// <inheritdoc/>
    /// <remarks>
    /// While anything listens, the watcher also checks its registration on its recovery interval (see
    /// <see cref="InputDeviceWatcher.SetWatched"/>), so a registration lost to an audio service restart comes back without
    /// the list being read again; the shell listens only while a Settings window is open.
    /// </remarks>
    public event Action<IReadOnlyList<AudioDevice>>? InputDevicesChanged
    {
        add
        {
            lock (_subscriptionGate)
            {
                _inputDevicesChanged += value;
                _watcher?.SetWatched(_inputDevicesChanged is not null);
            }
        }

        remove
        {
            lock (_subscriptionGate)
            {
                _inputDevicesChanged -= value;
                _watcher?.SetWatched(_inputDevicesChanged is not null);
            }
        }
    }

    public IReadOnlyList<AudioDevice> GetInputDevices()
    {
        ObjectDisposedException.ThrowIf(Disposing, this);

        // Whoever asks is about to show the list, so it is the moment to make sure the list keeps up afterwards too, and
        // the reading goes through the watcher's baseline so a list shown earlier hears about anything this one finds.
        if (_watcher is null)
        {
            return _devices.GetInputDevices();
        }

        _watcher.EnsureListening();
        return _watcher.Read();
    }

    public bool Start(string? deviceId = null, long owner = 0)
    {
        lock (_sync)
        {
            // Checked under the lock disposal waits for, so a Start that gets the lock once disposal began never reaches
            // the enumerator disposal is about to release.
            ObjectDisposedException.ThrowIf(Disposing, this);

            // Under the same lock as every stop's claim, so this is exact: the stop for this recording either reached the
            // service before this open, and then nothing opens, or it comes after it and finds this capture.
            if (owner != 0 && owner <= _stoppedThrough)
            {
                return false;
            }

            if (IsCapturing)
            {
                _logger.LogWarning("Start called while already capturing; ignoring.");
                return false;
            }

            try
            {
                LastRequestedDeviceUnavailable = false;
                var opened = OpenCapture(deviceId);
                _device = opened.Device;
                _capture = opened.Capture;
                LastRequestedDeviceUnavailable = opened.Source == CaptureSource.WindowsDefaultInsteadOfChosen;
                _captureFormat = NormalizeFormat(_capture.WaveFormat);

                // The previous capture's working buffer when it is still retained, otherwise a
                // fresh ~30 s reservation (see ReservationBytes). One capture runs at a time, so in
                // steady state no press allocates this large object heap buffer again.
                _raw = _buffers.Rent(ReservationBytes(_captureFormat));
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
                    "Starting capture on '{Device}' ({Source}) at {Rate} Hz, {Channels} ch, {Bits}-bit {Encoding}.",
                    _device.FriendlyName,
                    opened.Source,
                    _captureFormat.SampleRate,
                    _captureFormat.Channels,
                    _captureFormat.BitsPerSample,
                    _captureFormat.Encoding);

                _capture.StartRecording();
                _captureOwner = owner;
                IsCapturing = true;
                return true;
            }
            catch
            {
                // Cleanup disposes the capture, joining its thread, before the recording goes back,
                // so nothing can still be writing to a buffer that is kept for the next capture.
                Cleanup(_capture, _raw, _stopped, retainBuffer: true);
                throw;
            }
        }
    }

    public void RequestStop(long owner = 0)
    {
        IWaveIn? capture;
        lock (_sync)
        {
            NoteStopArrived(owner);
            if (!IsCapturing || _stopRequested || !OwnsCapture(owner))
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

    public CapturedAudio Stop(long owner = 0)
    {
        IWaveIn? capture;
        CaptureRecording? raw;
        WaveFormat? format;
        ManualResetEventSlim? stopped;
        bool stopAlreadyRequested;

        lock (_sync)
        {
            NoteStopArrived(owner);
            if (!IsCapturing || !OwnsCapture(owner))
            {
                return CapturedAudio.Empty;
            }

            IsCapturing = false;
            capture = _capture;
            raw = _raw;
            format = _captureFormat;
            stopped = _stopped;

            // Claimed together with the stop, so the capture thread's end-of-stream callback reads this as the stop it
            // was asked for (disposal stops a capture nobody requested a stop for), not as a stream that ended on its own.
            stopAlreadyRequested = _stopRequested;
            _stopRequested = true;
        }

        var quiesced = false;
        try
        {
            if (!stopAlreadyRequested)
            {
                capture?.StopRecording();
            }

            if (stopped is not null && !stopped.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new TimeoutException("The microphone did not stop within three seconds.");
            }

            // Only a stop that completed its handshake keeps the working buffer for the next capture;
            // one that timed out drops it. Either way Cleanup disposes the capture, which joins its
            // thread, before the buffer is zeroed and goes back.
            quiesced = stopped is not null;

            if (_captureError is not null)
            {
                _logger.LogError(_captureError, "Capture stopped due to an error.");
            }

            if (raw is null || format is null || raw.Length == 0)
            {
                return CapturedAudio.Empty;
            }

            var captured = ConvertCapture(raw, format, report => LastSignalReport = report, _logger);
            var signal = LastSignalReport!;

            // The signal shape is on this line deliberately. It separates failure modes that produce
            // an identical-looking capture: a stream that stopped delivering, a microphone that was
            // never live, a gain so low the recognizer sees nothing usable, and a channel being
            // averaged away. None of those are distinguishable from a duration alone.
            _logger.LogInformation(
                "Capture complete: {Seconds:F2}s ({Samples} samples @ {Rate} Hz) from {Signal}",
                captured.Duration.TotalSeconds,
                captured.Samples.Length,
                TargetSampleRate,
                signal.Describe());

            WarnAboutSignalProblems(signal);
            return captured;
        }
        finally
        {
            Cleanup(capture, raw, stopped, retainBuffer: quiesced);
        }
    }

    /// <summary>
    /// Drops the capture working buffer kept between captures, returning the bytes released. The
    /// buffer is already zeroed; releasing it only lets the garbage collector reclaim the memory,
    /// and the next capture reserves a fresh one. Takes only the pool's gate, never
    /// <c>_sync</c>, and never touches the buffer of a capture that is starting, recording or
    /// converting (see <see cref="CaptureBufferPool"/>), so the idle release may call it from any
    /// thread at any time.
    /// </summary>
    public long ReleaseRetainedBuffers() => _buffers.ReleaseRetained();

    /// <summary>
    /// About 30 s of the device's native format (11,520,000 bytes at 48 kHz stereo float), clamped
    /// to 64 KiB..32 MiB. Starting at a realistic capture length means a normal dictation never grows
    /// its buffer, and every growth of a buffer this size briefly holds old and new copies on the
    /// large object heap.
    /// </summary>
    internal static int ReservationBytes(WaveFormat format) =>
        (int)Math.Clamp(format.AverageBytesPerSecond * 30L, 64 * 1024, 32 * 1024 * 1024);

    /// <summary>
    /// One capture callback's worth of device-format audio: stored, then metered. The WASAPI
    /// callback and the synthetic soak harness both go through here.
    /// </summary>
    internal static float AppendChunk(CaptureRecording recording, ReadOnlySpan<byte> chunk, WaveFormat format)
    {
        recording.Write(chunk);
        return ComputePeak(chunk, format);
    }

    /// <summary>
    /// The stop-time conversion shared by <see cref="Stop"/> and the soak harness: measures the raw
    /// device-format signal (per channel, before the downmix) and reports it through
    /// <paramref name="onSignal"/> before anything else can fail, then downmixes and resamples to the
    /// 16 kHz mono the VAD and recognizer consume. The samples are a new array that never aliases
    /// the recording, whose buffer the next capture reuses while history may still hold this one.
    /// </summary>
    internal static CapturedAudio ConvertCapture(
        CaptureRecording recording,
        WaveFormat format,
        Action<CaptureSignalReport> onSignal,
        ILogger? logger = null)
    {
        // Measured on the RAW buffer, before the downmix, so per-channel levels are still
        // visible. Once channels are averaged the evidence is gone, and "what was on the other
        // channel" is the question a multi-channel headset or speakerphone always raises.
        onSignal(AnalyzeSafely(recording.Written, format, logger));
        if (recording.Length == 0)
        {
            return CapturedAudio.Empty;
        }

        var samples = ResampleToTarget(recording.Buffer, recording.Length, format);
        return new CapturedAudio(samples, TargetSampleRate);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        CaptureRecording? raw = _raw;
        WaveFormat? format = _captureFormat;
        if (raw is null || format is null || e.BytesRecorded == 0)
        {
            return;
        }

        // Peak is computed unconditionally (not just for the level meter): the running maximum is
        // what lets the pipeline tell "you spoke while muted" apart from "no speech in the audio".
        float peak = AppendChunk(raw, e.Buffer.AsSpan(0, e.BytesRecorded), format);
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

    private static CaptureSignalReport AnalyzeSafely(ReadOnlySpan<byte> raw, WaveFormat format, ILogger? logger)
    {
        try
        {
            return CaptureSignalAnalyzer.Analyze(raw, format);
        }
        catch (Exception ex)
        {
            // Diagnostics never break a dictation: a capture the analyzer cannot describe is still
            // a capture the user wants transcribed.
            logger?.LogDebug(ex, "Could not analyze the capture signal.");
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
    private bool ProbeEndpointMuted(ICaptureDevice device)
    {
        try
        {
            return device.IsMuted;
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

    internal static float[] ResampleToTarget(byte[] bytes, int length, WaveFormat format)
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

    internal static float[] ReadAll(ISampleProvider provider) => ReadAll(provider, ArrayPool<float>.Shared);

    internal static float[] ReadAll(ISampleProvider provider, ArrayPool<float> pool)
    {
        // The shared pool keeps returned arrays for whoever rents next, and Return(clearArray) only
        // clears an array the pool decides to keep, so this dictation's audio is wiped before each
        // array goes back.
        var samples = pool.Rent(provider.WaveFormat.SampleRate);
        var count = 0;
        try
        {
            while (true)
            {
                if (count == samples.Length)
                {
                    var expanded = pool.Rent(checked(samples.Length * 2));
                    samples.AsSpan(0, count).CopyTo(expanded);
                    samples.AsSpan().Clear();
                    pool.Return(samples);
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
            // The whole array, not just the counted part: a read that throws may already have
            // written past it.
            samples.AsSpan().Clear();
            pool.Return(samples);
        }
    }

    /// <summary>Where a capture's device came from, for the log and for telling the user once.</summary>
    private enum CaptureSource
    {
        /// <summary>No microphone was chosen, so the capture follows the Windows default.</summary>
        WindowsDefault,

        /// <summary>The microphone the user chose.</summary>
        Chosen,

        /// <summary>A microphone was chosen but could not be opened, so the Windows default recorded instead.</summary>
        WindowsDefaultInsteadOfChosen,
    }

    private readonly record struct OpenedCapture(ICaptureDevice Device, IWaveIn Capture, CaptureSource Source);

    /// <summary>
    /// Opens the chosen device and a capture on it, or, when there is no choice or the chosen device cannot be opened,
    /// the Windows default as it is now. Whatever it opened and could not hand back is released before it throws.
    /// </summary>
    /// <remarks>
    /// The chosen device's capture is created inside the fallback on purpose. Windows keeps an unplugged, disabled or
    /// not present endpoint by its ID, so looking it up succeeds and it is creating the capture, which activates the
    /// audio client, that fails (AUDCLNT_E_DEVICE_INVALIDATED). With only the lookup inside the fallback, a chosen USB
    /// microphone that was unplugged turned every press into "microphone unavailable" instead of recording from the
    /// default.
    /// </remarks>
    private OpenedCapture OpenCapture(string? deviceId)
    {
        var source = CaptureSource.WindowsDefault;
        if (!string.IsNullOrEmpty(deviceId))
        {
            ICaptureDevice? chosen = null;
            try
            {
                chosen = _devices.Open(deviceId);
                return new OpenedCapture(chosen, chosen.CreateCapture(), CaptureSource.Chosen);
            }
            catch (Exception ex)
            {
                ReleaseQuietly(chosen);
                source = CaptureSource.WindowsDefaultInsteadOfChosen;
                _logger.LogWarning(
                    "The chosen microphone could not be opened ({Failure}); recording from the Windows default instead.",
                    DescribeUnavailable(ex));
            }
        }

        var device = _devices.OpenDefault()
            ?? throw new InvalidOperationException("No active microphone (capture) device was found.");
        try
        {
            return new OpenedCapture(device, device.CreateCapture(), source);
        }
        catch
        {
            ReleaseQuietly(device);
            throw;
        }
    }

    // The state Windows reports says what happened to the device; any other failure is described by its shape.
    private static string DescribeUnavailable(Exception ex) =>
        ex is CaptureDeviceUnavailableException unavailable ? $"it is {unavailable.State}" : FailureShape.Describe(ex);

    private void ReleaseQuietly(ICaptureDevice? device)
    {
        try
        {
            device?.Dispose();
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogDebug("Releasing a microphone that could not be used failed ({Failure}).", FailureShape.Describe(ex)));
        }
    }

    private void RaiseInputDevicesChanged(IReadOnlyList<AudioDevice> devices) =>
        ResilientEvent.InvokeAll(
            Volatile.Read(ref _inputDevicesChanged),
            devices,
            ex => TryLog(log => log.LogWarning(
                "An input device change handler threw ({Failure}).", FailureShape.DescribeWithStack(ex))));

    private static Func<ICaptureDevices, InputDeviceWatcher> WatchWindowsEndpoints(ILogger logger) =>
        devices => new InputDeviceWatcher(WasapiEndpointNotifications.Register, devices.GetInputDevices, logger);

    private void Cleanup(
        IWaveIn? capture,
        CaptureRecording? raw,
        ManualResetEventSlim? stopped,
        bool retainBuffer)
    {
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            // NAudio's WasapiCapture.Dispose joins the capture thread, and that thread clears its
            // own handle only after its last DataAvailable. Either way, once this returns nothing
            // can still be writing to the recording that is about to be zeroed and reused.
            capture.Dispose();
        }

        if (raw is not null)
        {
            _buffers.Return(raw, retainBuffer);
        }

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
            _captureOwner = 0;
        }
    }

    // Caller holds _sync. Stops arrive in recording order, so one mark covers every recording up to it.
    private void NoteStopArrived(long owner)
    {
        if (owner > _stoppedThrough)
        {
            _stoppedThrough = owner;
        }
    }

    // Caller holds _sync. A stop without an owner (disposal, shutdown) takes whatever is capturing.
    private bool OwnsCapture(long owner) => owner == 0 || owner == _captureOwner;

    /// <summary>
    /// Stops watching the endpoints, stops a live capture and releases the capture endpoints. Never throws, and only the
    /// first call does anything.
    /// </summary>
    /// <remarks>
    /// <see cref="Start"/> holds the lock for the whole device open and uses the endpoints throughout it, and the host can
    /// dispose this service while an open is still under way (the keyboard hook's thread outlives its bounded join at
    /// shutdown). So disposal waits for the lock, with a bound, before it touches them: no new open starts once it has
    /// begun, and the capture an open in progress started is stopped once the lock is free. Past the bound the endpoints
    /// are left for the process to release at exit, since releasing them under the open could be a use after release
    /// inside the audio stack; the controller stops that capture itself when its open returns during shutdown. The bound
    /// covers only that wait for the open: stopping a capture afterwards still ends in NAudio's disposal of it, which joins
    /// the capture thread without a bound. The device watcher goes first and unconditionally: it shares nothing with an
    /// open or a capture, and its registration must not outlive the service.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return;
        }

        if (_watcher is not null)
        {
            _watcher.Changed -= RaiseInputDevicesChanged;
            _watcher.Dispose();
        }

        if (!Monitor.TryEnter(_sync, _disposeOpenWait))
        {
            // The retained buffer is safe to drop while an open runs (the pool never hands out a buffer in use); the
            // endpoints are not.
            _buffers.ReleaseRetained();
            TryLog(log => log.LogWarning(
                "A microphone was still opening {Seconds:F0} s into shutdown; the audio devices were left for the " +
                "process to release at exit instead of being released while in use.",
                _disposeOpenWait.TotalSeconds));
            return;
        }

        bool capturing;
        try
        {
            capturing = IsCapturing;
        }
        finally
        {
            Monitor.Exit(_sync);
        }

        // Stopped outside the lock: the stop waits for the capture thread and joins it, and that thread's callbacks take
        // the lock to request a stop of their own.
        if (capturing)
        {
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                TryLog(log => log.LogWarning(ex, "Error while stopping capture during dispose."));
            }
        }

        _buffers.ReleaseRetained();
        try
        {
            _devices.Dispose();
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogWarning(ex, "Releasing the audio device enumerator failed."));
        }
    }

    // Disposal and the paths it can race: a log call that throws must never become the failure it describes.
    private void TryLog(Action<ILogger> write)
    {
        try
        {
            write(_logger);
        }
        catch
        {
            // Nothing useful is left to do.
        }
    }
}
