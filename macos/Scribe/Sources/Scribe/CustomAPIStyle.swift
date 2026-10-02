import Foundation

enum CustomAPIStyle: String, CaseIterable, Identifiable, Sendable {
    case chatCompletions
    case responses

    var id: String { rawValue }
}
