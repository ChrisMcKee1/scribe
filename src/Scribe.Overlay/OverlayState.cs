namespace Scribe.Overlay;

/// <summary>
/// The visual states the recording pill can display. Mirrors the WPF overlay's content panels so
/// the WPF engine can drive identical UX over the wire.
/// </summary>
public enum OverlayState
{
    /// <summary>Hidden / parked (no pill visible).</summary>
    Hidden,

    /// <summary>Capturing microphone input — pulsing red dot + live level meter.</summary>
    Listening,

    /// <summary>Transcribing or AI-polishing — bouncing dots.</summary>
    Processing,

    /// <summary>AI cleanup failed at runtime — brief red notice while falling back to raw text.</summary>
    Failed,
}
