import Foundation

/// Engine presentations always supersede a preview, even when the indicator is switched off.
struct OverlayPreviewGate {
    private(set) var engineState: OverlayState = .hidden
    private(set) var generation: UInt64 = 0
    private(set) var candidate: OverlayAnchor?
    var enabled = true

    var state: OverlayState {
        if candidate != nil { return .listening(level: 0.65) }
        return enabled ? engineState : .hidden
    }

    mutating func render(_ state: OverlayState) {
        cancel()
        engineState = state
    }

    mutating func begin(at anchor: OverlayAnchor) -> UInt64? {
        guard engineState == .hidden else { return nil }
        generation &+= 1
        candidate = anchor
        return generation
    }

    @discardableResult
    mutating func end(_ token: UInt64) -> Bool {
        guard token == generation, candidate != nil else { return false }
        cancel()
        return true
    }

    mutating func cancel() {
        generation &+= 1
        candidate = nil
    }
}
