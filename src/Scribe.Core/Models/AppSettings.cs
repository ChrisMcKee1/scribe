namespace Scribe.Core.Models;

/// <summary>
/// User-configurable application settings. Mutable POCO so it can back a settings view-model
/// and be (de)serialized to the settings store. Construct via <see cref="CreateDefault"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>WASAPI capture device id; <see langword="null"/> uses the system default.</summary>
    public string? InputDeviceId { get; set; }

    /// <summary>Friendly name of the selected device (for display only).</summary>
    public string? InputDeviceName { get; set; }

    public HotkeyBinding Hotkey { get; set; } = HotkeyBinding.Default;

    /// <summary>Show the always-on-top recording overlay while capturing.</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Register the app to start at user logon.</summary>
    public bool LaunchOnLogin { get; set; }

    /// <summary>Decode thread count for sherpa-onnx; 0 lets the app pick a sensible default.</summary>
    public int DecodeThreads { get; set; }

    /// <summary>
    /// Use beam-search decoding (<c>modified_beam_search</c>) instead of greedy. Slightly more
    /// accurate on hard audio at a latency cost. Takes effect after restarting Scribe because the
    /// recognizer is warm-loaded once.
    /// </summary>
    public bool UseHighAccuracyDecoding { get; set; }

    /// <summary>Trim leading/trailing silence and reject no-speech captures using VAD.</summary>
    public bool UseVoiceActivityDetection { get; set; } = true;

    /// <summary>Apply the user dictionary and casing/spacing fixups to decoded text.</summary>
    public bool ApplyPostProcessing { get; set; } = true;

    /// <summary>
    /// How decoded text is placed into the focused app. Unicode typing is the default because it
    /// works in the widest range of apps (including paste-blocking fields) and never touches the
    /// clipboard.
    /// </summary>
    public InjectionMethod InjectionMethod { get; set; } = InjectionMethod.UnicodeType;

    /// <summary>Persist a copy of each capture's audio alongside its history entry.</summary>
    public bool StoreAudioHistory { get; set; }

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
