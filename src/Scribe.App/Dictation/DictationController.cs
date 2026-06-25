using Microsoft.Extensions.Logging;
using Scribe.Core.Audio;
using Scribe.Core.Hotkeys;
using Scribe.Core.TextInjection;
using Scribe.Core.Transcription;

namespace Scribe.App.Dictation;

/// <summary>
/// Wires the dictation loop together: hotkey down starts microphone capture; hotkey up stops
/// capture and, on a background thread, transcribes the audio and injects the text into the
/// focused application. A single state gate prevents overlapping sessions, and the heavy
/// stop→decode→inject work is offloaded so the hotkey consumer thread is never blocked.
/// </summary>
internal sealed class DictationController : IDisposable
{
    private readonly IHotkeyService _hotkeys;
    private readonly IAudioCaptureService _audio;
    private readonly ITranscriptionService _transcription;
    private readonly ITextInjector _injector;
    private readonly ILogger<DictationController> _log;

    private readonly object _gate = new();
    private DictationState _state = DictationState.Idle;
    private bool _started;
    private bool _disposed;

    public DictationController(
        IHotkeyService hotkeys,
        IAudioCaptureService audio,
        ITranscriptionService transcription,
        ITextInjector injector,
        ILogger<DictationController> log)
    {
        _hotkeys = hotkeys;
        _audio = audio;
        _transcription = transcription;
        _injector = injector;
        _log = log;
    }

    /// <summary>Raised whenever the dictation state changes (on a background thread).</summary>
    public event Action<DictationState>? StateChanged;

    /// <summary>Raised when a capture or transcription step fails.</summary>
    public event Action<string>? Error;

    /// <summary>Subscribes to the hotkey and installs the global keyboard hook.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _hotkeys.Activated += OnActivated;
        _hotkeys.Deactivated += OnDeactivated;
        _hotkeys.Start();
        _started = true;
        _log.LogInformation("Dictation controller started; binding = {Binding}.", _hotkeys.Binding.DisplayName);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_state != DictationState.Idle)
            {
                _log.LogDebug("Hotkey activated while {State}; ignoring.", _state);
                return;
            }

            _state = DictationState.Recording;
        }

        try
        {
            _audio.Start();
            _log.LogInformation("Recording started.");
            Raise(DictationState.Recording);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start audio capture.");
            ResetToIdle();
            Error?.Invoke("microphone unavailable");
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_state != DictationState.Recording)
            {
                return;
            }

            _state = DictationState.Processing;
        }

        Raise(DictationState.Processing);
        _ = Task.Run(ProcessAsync);
    }

    private void ProcessAsync()
    {
        try
        {
            var audio = _audio.Stop();
            _log.LogInformation("Captured {Seconds:F2}s of audio.", audio.Duration.TotalSeconds);

            if (audio.IsEmpty)
            {
                _log.LogInformation("Capture was empty; nothing to transcribe.");
                return;
            }

            if (!_transcription.IsReady)
            {
                _log.LogWarning("Transcription engine not ready yet; discarding capture.");
                Error?.Invoke("model still loading");
                return;
            }

            var result = _transcription.Transcribe(audio);
            if (result.IsEmpty)
            {
                _log.LogInformation("No speech recognized.");
                return;
            }

            _log.LogInformation(
                "Transcribed {Chars} chars in {Decode:F2}s (RTF {Rtf:F2}).",
                result.Text.Length,
                result.DecodeDuration.TotalSeconds,
                result.RealTimeFactor);

            _injector.Inject(result.Text);
            _log.LogInformation("Text injected.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Dictation processing failed.");
            Error?.Invoke("transcription failed");
        }
        finally
        {
            ResetToIdle();
        }
    }

    private void ResetToIdle()
    {
        lock (_gate)
        {
            _state = DictationState.Idle;
        }

        Raise(DictationState.Idle);
    }

    private void Raise(DictationState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A StateChanged handler threw.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _hotkeys.Activated -= OnActivated;
            _hotkeys.Deactivated -= OnDeactivated;
            _hotkeys.Stop();
        }
    }
}
