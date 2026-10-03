import Foundation

/// The recording indicator's five level bars: each laid out 16 DIP tall, with a 4 DIP floor in silence, and the rest
/// of the travel split by the icon's proportions.
enum PillLevelBars {
    static let count = 5
    static let floor = 4.0 / 16.0

    private static let proportions = [0.26, 0.56, 1.0, 0.56, 0.26]

    static func proportion(_ bar: Int) -> Double {
        proportions[bar]
    }

    static func scale(of bar: Int, level: Double) -> Double {
        let held: Double
        if level.isFinite {
            held = min(max(level, 0), 1)
        } else {
            held = 0
        }
        return floor + ((1 - floor) * proportion(bar) * held)
    }
}
