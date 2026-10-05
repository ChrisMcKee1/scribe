import Foundation

/// How long each pill outcome stays on screen, and how long the panel fade takes when motion is allowed.
enum PillTiming {
    static let typedHold: Duration = .milliseconds(400)
    static let noticeHold: Duration = .milliseconds(1_300)
    static let fadeInSeconds = 0.12
    static let fadeOutSeconds = 0.15
}
