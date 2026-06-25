namespace Scribe.App.Dictation;

/// <summary>The lifecycle state of the dictation loop, surfaced through the tray icon.</summary>
internal enum DictationState
{
    /// <summary>Waiting for the hotkey; nothing is being captured.</summary>
    Idle,

    /// <summary>The hotkey is engaged and the microphone is being captured.</summary>
    Recording,

    /// <summary>Capture has stopped and the audio is being transcribed and injected.</summary>
    Processing,
}
