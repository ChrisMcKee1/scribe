import Combine
import SwiftUI

/// Shared observable model driving the overlay pill's contents. Owned by `OverlayPanelController`, which the
/// dictation lifecycle drives through numbered changes (`render(_:revision:)`), observed by `OverlayPillView` via
/// SwiftUI. Mirrors the intent of Windows' `OverlayIpcServer` pushing state to the separate overlay process, but
/// in-process here since macOS doesn't need the WPF-transparency workaround that forced Windows into a second process.
@MainActor
final class DictationSessionModel: ObservableObject {
    @Published var state: OverlayState = .hidden
}

/// The pill's visual contents: pulsing dot + meter while listening, bouncing dots while processing, a notice that
/// names its stage afterwards. Sized to roughly match Windows' 264x110 logical pill, scaled down since macOS's pill is
/// a lightweight compact indicator rather than a full panel.
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
        case .notice(let notice):
            Image(systemName: notice.isFailure ? "exclamationmark.triangle.fill" : "info.circle.fill")
                .foregroundStyle(notice.isFailure ? Color.red : Color.secondary)
        }
    }

    private var label: some View {
        Text(labelText)
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(.primary)
            .lineLimit(1)
    }

    private var labelText: String {
        switch session.state {
        case .hidden: return ""
        case .listening(let levelDbfs): return String(format: "Listening %.0f dBFS", levelDbfs)
        case .processing: return "Processing…"
        case .notice(let notice): return notice.label
        }
    }
}

/// Borderless, non-activating floating panel hosting `OverlayPillView`. Stays above normal windows, including a
/// full-screen app's space, never steals keyboard focus (so the focused app keeps typing focus for injection), and
/// repositions itself to the configured `OverlayAnchor` whenever it appears.
@MainActor
final class OverlayPanelController {
    private let session = DictationSessionModel()
    private var panel: NSPanel?
    private var revisions = PresentationRevisionGate()
    private let pillSize = NSSize(width: 280, height: 40)
    var anchor: OverlayAnchor = .bottomCenter

    /// What the pill shows now.
    var displayedState: OverlayState {
        session.state
    }

    /// The revision of the change shown last; 0 before the first.
    var lastRenderedRevision: UInt64 {
        revisions.lastAdmitted
    }

    /// The panel's collection behavior, once the panel exists.
    var panelCollectionBehavior: NSWindow.CollectionBehavior? {
        panel?.collectionBehavior
    }

    /// Shows `state` when `revision` is higher than every revision shown before, and returns whether it did. A
    /// change that arrives late, after a newer one, is dropped, so a hide scheduled for one dictation can never take
    /// down the pill of the recording that started after it.
    @discardableResult
    func render(_ state: OverlayState, revision: UInt64) -> Bool {
        guard revisions.admit(revision) else { return false }
        session.state = state
        guard state != .hidden else {
            panel?.orderOut(nil)
            return true
        }
        let panel = ensurePanel()
        if !panel.isVisible {
            reposition(panel)
            panel.orderFrontRegardless()
        }
        return true
    }

    /// Lazily creates the panel on first use so app launch doesn't pay for it when the pill is never shown.
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
        // `.fullScreenAuxiliary` lets the pill share a full-screen app's space, where the user is most likely to be
        // dictating into a focused editor or a browser.
        newPanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        newPanel.isMovableByWindowBackground = false
        newPanel.hidesOnDeactivate = false
        panel = newPanel
        return newPanel
    }

    /// Repositions the panel to the current anchor on the screen holding the mouse cursor (falls back to
    /// `NSScreen.main`), matching Windows' "the pill follows the active display" behavior.
    private func reposition(_ panel: NSPanel) {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        guard let visibleFrame = screen?.visibleFrame else { return }
        let origin = anchor.origin(for: pillSize, in: visibleFrame)
        panel.setFrameOrigin(origin)
    }
}
