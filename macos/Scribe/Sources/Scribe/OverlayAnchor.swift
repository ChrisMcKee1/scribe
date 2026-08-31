import AppKit

/// Where the pill sits within the screen's visible frame. Mirrors Windows'
/// `Scribe.Overlay.OverlayAnchor` (and `Scribe.Core.Models.OverlayPosition`) by name so the same
/// nine-position picker concept applies here, even though macOS drives the pill in-process rather
/// than over an IPC pipe to a second process.
enum OverlayAnchor: String, CaseIterable, Codable {
    case topLeft
    case topCenter
    case topRight
    case middleLeft
    case center
    case middleRight
    case bottomLeft
    case bottomCenter
    case bottomRight

    /// Human-readable label for the position picker menu.
    var displayName: String {
        switch self {
        case .topLeft: return "Top Left"
        case .topCenter: return "Top Center"
        case .topRight: return "Top Right"
        case .middleLeft: return "Middle Left"
        case .center: return "Center"
        case .middleRight: return "Middle Right"
        case .bottomLeft: return "Bottom Left"
        case .bottomCenter: return "Bottom Center"
        case .bottomRight: return "Bottom Right"
        }
    }

    /// Computes the top-left origin for a panel of `size` anchored within `visibleFrame`, with a
    /// fixed margin from the screen edges so the pill never touches the notch/menu bar/dock.
    func origin(for size: NSSize, in visibleFrame: NSRect, margin: CGFloat = 24) -> NSPoint {
        let minX = visibleFrame.minX + margin
        let maxX = visibleFrame.maxX - size.width - margin
        let midX = visibleFrame.midX - size.width / 2
        let minY = visibleFrame.minY + margin
        let maxY = visibleFrame.maxY - size.height - margin
        let midY = visibleFrame.midY - size.height / 2

        switch self {
        case .topLeft: return NSPoint(x: minX, y: maxY)
        case .topCenter: return NSPoint(x: midX, y: maxY)
        case .topRight: return NSPoint(x: maxX, y: maxY)
        case .middleLeft: return NSPoint(x: minX, y: midY)
        case .center: return NSPoint(x: midX, y: midY)
        case .middleRight: return NSPoint(x: maxX, y: midY)
        case .bottomLeft: return NSPoint(x: minX, y: minY)
        case .bottomCenter: return NSPoint(x: midX, y: minY)
        case .bottomRight: return NSPoint(x: maxX, y: minY)
        }
    }
}

/// The visual states the recording pill can display. Mirrors Windows' `Scribe.Overlay.OverlayState`.
enum OverlayState: Equatable {
    /// Hidden / parked (no pill visible).
    case hidden
    /// Capturing microphone input: pulsing red dot and live level meter.
    case listening(levelDbfs: Float)
    /// Transcribing or AI-polishing: bouncing dots.
    case processing
    /// AI cleanup failed at runtime; brief red notice while falling back to raw text.
    case failed
}
