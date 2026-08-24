import SwiftUI
import Combine

/// Shared observable model driving the overlay pill's contents. Owned by `AppDelegate`, updated
/// from the audio capture/transcription pipeline, observed by `OverlayPillView` via SwiftUI.
/// Mirrors the intent of Windows' `OverlayIpcServer` pushing state to the separate overlay
/// process, but in-process here since macOS doesn't need the WPF-transparency workaround that
/// forced Windows into a second process.
@MainActor
final class DictationSessionModel: ObservableObject {
    @Published var state: OverlayState = .hidden
}

/// The pill's visual contents: pulsing dot + meter while listening, bouncing dots while
/// processing, a red notice on failure. Sized to roughly match Windows' 264x110 logical pill,
/// scaled down since macOS's pill is a lightweight compact indicator rather than a full panel.
struct OverlayPillView: View {
    @ObservedObject var session: DictationSessionModel

    var body: some View {
        HStack(spacing: 8) {
            indicator
            label
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(.thinMaterial)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(Color.white.opacity(0.15), lineWidth: 1)
        )
    }

    @ViewBuilder
    private var indicator: some View {
        switch session.state {
        case .hidden:
            EmptyView()
        case .listening:
            Circle()
                .fill(Color.red)
                .frame(width: 10, height: 10)
        case .processing:
            ProgressView()
                .controlSize(.small)
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Color.red)
        }
    }

    private var label: some View {
        Text(labelText)
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(.primary)
    }

    private var labelText: String {
        switch session.state {
        case .hidden: return ""
        case .listening(let levelDbfs): return String(format: "Listening %.0f dBFS", levelDbfs)
        case .processing: return "Processing…"
        case .failed: return "Cleanup failed"
        }
    }
}

/// Borderless, non-activating floating panel hosting `OverlayPillView`. Stays above normal
/// windows, never steals keyboard focus (so the focused app keeps typing focus for injection),
/// and repositions itself to the configured `OverlayAnchor` whenever shown.
@MainActor
final class OverlayPanelController {
    private let session = DictationSessionModel()
    private var panel: NSPanel?
    private let pillSize = NSSize(width: 220, height: 40)
    var anchor: OverlayAnchor = .bottomCenter

    /// Lazily creates the panel on first use so app launch doesn't pay for it when the pill is
    /// never shown (e.g. hotkey-only workflows where the user never glances at the menu bar).
    private func ensurePanel() -> NSPanel {
        if let panel { return panel }

        let hostingController = NSHostingController(rootView: OverlayPillView(session: session))
        let newPanel = NSPanel(
            contentRect: NSRect(origin: .zero, size: pillSize),
            styleMask: [.nonactivatingPanel, .borderless],
            backing: .buffered,
            defer: false)
        newPanel.contentViewController = hostingController
        newPanel.isOpaque = false
        newPanel.backgroundColor = .clear
        newPanel.hasShadow = true
        newPanel.level = .floating
        newPanel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        newPanel.isMovableByWindowBackground = false
        newPanel.hidesOnDeactivate = false
        panel = newPanel
        return newPanel
    }

    /// Repositions the panel to the current anchor on the screen holding the mouse cursor (falls
    /// back to `NSScreen.main`), matching Windows' "the pill follows the active display" behavior.
    private func reposition(_ panel: NSPanel) {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        guard let visibleFrame = screen?.visibleFrame else { return }
        let origin = anchor.origin(for: pillSize, in: visibleFrame)
        panel.setFrameOrigin(origin)
    }

    func show(state: OverlayState) {
        session.state = state
        guard state != .hidden else {
            hide()
            return
        }
        let panel = ensurePanel()
        reposition(panel)
        panel.orderFrontRegardless()
    }

    func update(state: OverlayState) {
        session.state = state
    }

    func hide() {
        session.state = .hidden
        panel?.orderOut(nil)
    }
}
