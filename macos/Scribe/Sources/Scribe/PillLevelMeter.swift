import Foundation

/// The level the pill's five bars show, from the peak of each piece of audio the microphone delivers.
struct PillLevelMeter: Sendable {
    static let quietDbfs = -54.0
    static let fullDbfs = -6.0
    static let attackMs = 10.0
    static let releaseMs = 120.0
    static let nominalStepMs = 10.0
    static let maxStepMs = 250.0

    private(set) var level = 0.0
    private var started = false

    mutating func reset() {
        level = 0
        started = false
    }

    mutating func update(_ peak: Float, sincePrevious: Duration) -> Double {
        let stepMs =
            started
            ? min(max(sincePrevious.milliseconds, 0), Self.maxStepMs)
            : Self.nominalStepMs
        started = true

        let target = Self.target(of: peak)
        let timeConstant = target > level ? Self.attackMs : Self.releaseMs
        level = target + ((level - target) * Foundation.exp(-stepMs / timeConstant))
        return level
    }

    static func target(of peak: Float) -> Double {
        guard peak > 0 else { return 0 }
        let dbfs = 20 * log10(Double(peak))
        return min(max((dbfs - Self.quietDbfs) / (Self.fullDbfs - Self.quietDbfs), 0), 1)
    }
}

extension Duration {
    fileprivate var milliseconds: Double {
        let (seconds, attoseconds) = components
        return (Double(seconds) * 1_000) + (Double(attoseconds) / 1e15)
    }
}
