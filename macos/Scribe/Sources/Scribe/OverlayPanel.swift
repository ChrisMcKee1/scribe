import AppKit
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

struct OverlayPillView: View {
    @ObservedObject var session: DictationSessionModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 10) {
            indicator
            label
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(Color(red: 0.07, green: 0.1, blue: 0.18).opacity(0.96))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(borderColor, lineWidth: borderWidth)
        )
        .shadow(color: Color.black.opacity(0.28), radius: 16, y: 8)
        .animation(reduceMotion ? nil : .easeInOut(duration: 0.12), value: session.state)
    }

    @ViewBuilder
    private var indicator: some View {
        switch session.state {
        case .hidden:
            EmptyView()
        case .listening(let level):
            OverlayLevelBarsView(level: level)
        case .processing, .startingLocalModel:
            ProcessingDotsView()
        case .notice(let notice):
            if let outcome = notice.pillOutcome {
                OutcomeIconView(outcome: outcome)
            } else {
                Image(systemName: notice.isFailure ? "exclamationmark.triangle.fill" : "info.circle.fill")
                    .foregroundStyle(notice.isFailure ? Color.red.opacity(0.95) : Color.white.opacity(0.82))
                    .font(.system(size: 16, weight: .semibold))
                    .frame(width: 18, height: 18)
            }
        }
    }

    @ViewBuilder
    private var label: some View {
        switch session.state {
        case .hidden:
            EmptyView()
        case .listening:
            Text("Listening")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Color.white)
        case .processing:
            Text("Processing…")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Color.white)
        case .startingLocalModel:
            VStack(alignment: .leading, spacing: 1) {
                Text("Starting local model…")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Color.white)
                Text("This can take time")
                    .font(.system(size: 11))
                    .foregroundStyle(Color.white.opacity(0.78))
            }
        case .notice(let notice):
            if let outcome = notice.pillOutcome {
                OutcomeTextView(outcome: outcome)
            } else {
                Text(notice.label)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Color.white)
                    .lineLimit(1)
            }
        }
    }

    private var borderColor: Color {
        switch session.state {
        case .listening:
            return Color(red: 0.24, green: 0.54, blue: 0.98)
        case .notice(let notice):
            if let outcome = notice.pillOutcome {
                if outcome.isCaution {
                    return Color(red: 0.98, green: 0.77, blue: 0.27)
                }
                if outcome.isFailure {
                    return Color(red: 0.96, green: 0.42, blue: 0.51)
                }
            }
            return Color.white.opacity(0.18)
        case .hidden, .processing, .startingLocalModel:
            return Color.white.opacity(0.18)
        }
    }

    private var borderWidth: CGFloat {
        if case .listening = session.state {
            return 1.5
        }
        return 1
    }
}

private struct OverlayLevelBarsView: View {
    let level: Double

    var body: some View {
        HStack(alignment: .bottom, spacing: 3) {
            ForEach(0..<PillLevelBars.count, id: \.self) { index in
                Capsule()
                    .fill(index == 2 ? Color.white : Color.white.opacity(0.84))
                    .frame(width: 4, height: 16)
                    .scaleEffect(x: 1, y: PillLevelBars.scale(of: index, level: level), anchor: .bottom)
            }
        }
        .frame(width: 34, height: 18, alignment: .bottom)
    }
}

private struct ProcessingDotsView: View {
    var body: some View {
        HStack(spacing: 4) {
            ForEach(0..<3, id: \.self) { _ in
                Circle()
                    .fill(Color.white.opacity(0.84))
                    .frame(width: 5, height: 5)
            }
        }
        .frame(width: 24, height: 18)
    }
}

private struct OutcomeIconView: View {
    let outcome: PillOutcome

    var body: some View {
        Image(systemName: symbol)
            .foregroundStyle(color)
            .font(.system(size: 16, weight: .semibold))
            .frame(width: 18, height: 18)
    }

    private var symbol: String {
        if outcome.kind == .typed {
            return "checkmark.circle.fill"
        }
        if outcome.isCaution {
            return "exclamationmark.triangle.fill"
        }
        return "xmark.circle.fill"
    }

    private var color: Color {
        if outcome.kind == .typed {
            return Color.green.opacity(0.95)
        }
        if outcome.isCaution {
            return Color(red: 0.98, green: 0.77, blue: 0.27)
        }
        return Color(red: 0.96, green: 0.42, blue: 0.51)
    }
}

private struct OutcomeTextView: View {
    let outcome: PillOutcome

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(outcome.title)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Color.white)
            if !outcome.detail.isEmpty {
                Text(outcome.detail)
                    .font(.system(size: 11))
                    .foregroundStyle(Color.white.opacity(0.82))
            }
        }
        .fixedSize(horizontal: false, vertical: true)
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
    private let pillSize = NSSize(width: 280, height: 52)
    private var displayedStateValue: OverlayState = .hidden
    private var hiddenRevision: UInt64?
    var anchor: OverlayAnchor = .bottomCenter

    var displayedState: OverlayState {
        displayedStateValue
    }

    var lastRenderedRevision: UInt64 {
        revisions.lastAdmitted
    }

    var panelCollectionBehavior: NSWindow.CollectionBehavior? {
        panel?.collectionBehavior
    }

    @discardableResult
    func render(_ state: OverlayState, revision: UInt64) -> Bool {
        guard revisions.admit(revision) else { return false }
        displayedStateValue = state
        guard state != .hidden else {
            hidePanel(for: revision)
            return true
        }

        hiddenRevision = nil
        session.state = state
        let panel = ensurePanel()
        reposition(panel)
        if !panel.isVisible {
            panel.alphaValue = prefersReducedMotion ? 1 : 0
            panel.orderFrontRegardless()
            if !prefersReducedMotion {
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = PillTiming.fadeInSeconds
                    panel.animator().alphaValue = 1
                }
            }
        } else {
            panel.alphaValue = 1
        }
        return true
    }

    private var prefersReducedMotion: Bool {
        NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
    }

    private func hidePanel(for revision: UInt64) {
        guard let panel else {
            session.state = .hidden
            return
        }
        guard panel.isVisible else {
            session.state = .hidden
            panel.alphaValue = 1
            panel.orderOut(nil)
            return
        }
        guard !prefersReducedMotion else {
            hiddenRevision = nil
            session.state = .hidden
            panel.alphaValue = 1
            panel.orderOut(nil)
            return
        }

        hiddenRevision = revision
        let token = revision
        NSAnimationContext.runAnimationGroup { context in
            context.duration = PillTiming.fadeOutSeconds
            panel.animator().alphaValue = 0
        } completionHandler: { [weak self, weak panel] in
            Task { @MainActor [weak self, weak panel] in
                guard let self, let panel, self.hiddenRevision == token else { return }
                self.hiddenRevision = nil
                self.session.state = .hidden
                panel.alphaValue = 1
                panel.orderOut(nil)
            }
        }
    }

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
        newPanel.alphaValue = 1
        newPanel.hasShadow = true
        newPanel.level = .floating
        newPanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        newPanel.isMovableByWindowBackground = false
        newPanel.hidesOnDeactivate = false
        panel = newPanel
        return newPanel
    }

    private func reposition(_ panel: NSPanel) {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        guard let visibleFrame = screen?.visibleFrame else { return }
        let origin = anchor.origin(for: pillSize, in: visibleFrame)
        panel.setFrameOrigin(origin)
    }
}
