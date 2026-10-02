import Foundation

/// Actor reentrancy alone does not order file installation and the legacy projection across SQLite awaits.
actor WordPackMaterializationLane {
    private var tails: [String: (UUID, Task<Void, Never>)] = [:]

    func run<T: Sendable>(
        root: URL, operation: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        let key = root.standardizedFileURL.resolvingSymlinksInPath().path
        let previous = tails[key]?.1
        let id = UUID()
        let task = Task {
            await previous?.value
            return try await operation()
        }
        let tail = Task { _ = try? await task.value }
        tails[key] = (id, tail)
        defer {
            if tails[key]?.0 == id { tails.removeValue(forKey: key) }
        }
        return try await task.value
    }
}
