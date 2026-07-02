namespace Scribe.Overlay;

/// <summary>
/// Where the pill sits within the monitor's work area. Value names are the wire tokens of the
/// <c>POSITION</c> IPC command and mirror <c>Scribe.Core.Models.OverlayPosition</c> — the overlay
/// deliberately has no reference to Scribe.Core, so the two enums are kept in sync by name.
/// </summary>
public enum OverlayAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}
