import Foundation

enum LocalModelDefaults {
    static let keepAliveMinutes = 10
    static let prewarmAfterIdle = Duration.seconds(30)
    static let startWait = Duration.seconds(30)
    static let sharedLane = AsyncLane()
}
