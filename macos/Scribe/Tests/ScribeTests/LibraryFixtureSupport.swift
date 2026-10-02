import Foundation

@testable import Scribe

enum LibraryFixtureSupport {
    static let fixturesDirectory = repositoryRoot()
        .appendingPathComponent("tests", isDirectory: true)
        .appendingPathComponent("fixtures", isDirectory: true)
        .appendingPathComponent("libraries", isDirectory: true)

    static func json(_ name: String) throws -> Data {
        try Data(contentsOf: fixturesDirectory.appendingPathComponent(name, isDirectory: false))
    }

    static func repositoryRoot() -> URL {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 {
            root.deleteLastPathComponent()
        }
        return root
    }
}
