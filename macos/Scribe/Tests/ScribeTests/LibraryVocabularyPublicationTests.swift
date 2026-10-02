import XCTest
import os

@testable import Scribe

final class LibraryVocabularyPublicationTests: XCTestCase {
    func testFirstLoadRetriesAfterItsOwnAdoptionWithoutRevivingAnOldScope() async throws {
        let fixture = try PublicationFixture()
        defer { fixture.remove() }
        fixture.settings.enabledLibraryIds = ["github"]
        let vocabulary = try await fixture.service.loadVocabulary()
        XCTAssertTrue(vocabulary.aiScope.permittedLibraryIds.contains("github"))
        XCTAssertEqual(fixture.authority.scope, vocabulary.aiScope)
        XCTAssertEqual(fixture.authority.changes, 1)
        XCTAssertEqual(fixture.authority.reads, 2)
    }

    func testAChangeBetweenReadAndPublicationCannotRestoreTheEarlierPermission() async throws {
        let fixture = try PublicationFixture()
        defer { fixture.remove() }
        fixture.settings.enabledLibraryIds = ["github"]
        _ = try await fixture.service.loadVocabulary()
        let authority = fixture.authority
        let settings = fixture.settings
        authority.beforeNextPublication {
            authority.begin()
            settings.enabledLibraryIds = []
            authority.end()
        }
        let vocabulary = try await fixture.service.loadVocabulary()
        XCTAssertFalse(vocabulary.aiScope.permittedLibraryIds.contains("github"))
        XCTAssertFalse(authority.scope.permittedLibraryIds.contains("github"))
        XCTAssertEqual(authority.scope, vocabulary.aiScope)
    }

    func testAsyncMutationStaysWithheldAcrossAnAwaitAndAfterFailure() async throws {
        let authority = PublicationAuthority()
        let scope = AiVocabularyScope(generation: 1, permittedContent: ["github": nil])
        XCTAssertTrue(authority.publish(scope, after: authority.revision))
        do {
            try await authority.callbacks.changingAsync {
                XCTAssertEqual(authority.scope, .none)
                XCTAssertFalse(authority.publish(scope, after: authority.revision))
                await Task.yield()
                XCTAssertFalse(authority.publish(scope, after: authority.revision))
                throw PublicationFailure.injected
            }
            XCTFail("The injected mutation must fail")
        } catch {
            XCTAssertEqual(error as? PublicationFailure, .injected)
        }
        XCTAssertEqual(authority.scope, .none)
        XCTAssertTrue(authority.publish(scope, after: authority.revision))
    }

    func testImportAndRemoveWithdrawBeforeAFileOrPermissionIsChanged() async throws {
        let fixture = try PublicationFixture()
        defer { fixture.remove() }
        _ = try await fixture.service.loadVocabulary()
        let authority = fixture.authority
        let before = authority.changes
        let url = fixture.directory.url.appendingPathComponent("Libraries/custom-terms.csv")
        authority.beforeNextChange {
            XCTAssertEqual(authority.scope, .none)
            XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        }
        let library = try fixture.service.import(csv: "pattern,replacement\nterm,Term\n", suggestedName: "Terms")
        XCTAssertEqual(authority.scope, .none)
        XCTAssertEqual(authority.changes, before + 1)
        _ = try await fixture.service.loadVocabulary()
        authority.beforeNextChange {
            XCTAssertEqual(authority.scope, .none)
            XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
        }
        try fixture.service.remove(id: library.id)
        XCTAssertEqual(authority.scope, .none)
        XCTAssertEqual(authority.changes, before + 2)
    }

    func testSameGenerationPublicationStillUpdatesTheAuthority() {
        let authority = PublicationAuthority()
        let first = AiVocabularyScope(generation: 7, permittedContent: ["terms": "first"])
        let second = AiVocabularyScope(generation: 7, permittedContent: ["terms": "second"])
        let earlier = authority.revision
        XCTAssertTrue(authority.publish(first, after: earlier))
        XCTAssertFalse(authority.publish(second, after: earlier))
        XCTAssertTrue(authority.publish(second, after: authority.revision))
        XCTAssertEqual(authority.scope, second)
    }
}

private enum PublicationFailure: Error, Equatable {
    case injected
}

private final class PublicationAuthority: Sendable {
    private struct State {
        var revision: UInt64 = 0
        var scope = AiVocabularyScope.none
        var changing = 0
        var changes = 0
        var reads = 0
        var beforeRead: (@Sendable () -> Void)?
        var beforeChange: (@Sendable () -> Void)?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var revision: UInt64 { state.withLock { $0.revision } }
    var scope: AiVocabularyScope { state.withLock { $0.scope } }
    var changes: Int { state.withLock { $0.changes } }
    var reads: Int { state.withLock { $0.reads } }

    var callbacks: LibraryVocabularyPublication {
        LibraryVocabularyPublication(
            revision: { self.revision },
            publishRead: { self.publish($0, after: $1) },
            beginChange: { self.begin() },
            endChange: { self.end() })
    }

    func beforeNextPublication(_ callback: @escaping @Sendable () -> Void) {
        state.withLock { $0.beforeRead = callback }
    }

    func beforeNextChange(_ callback: @escaping @Sendable () -> Void) {
        state.withLock { $0.beforeChange = callback }
    }

    func begin() {
        let before = state.withLock {
            $0.changing += 1
            $0.changes += 1
            $0.scope = .none
            $0.revision &+= 1
            let before = $0.beforeChange
            $0.beforeChange = nil
            return before
        }
        before?()
    }

    func end() {
        state.withLock {
            $0.changing -= 1
            $0.scope = .none
            $0.revision &+= 1
        }
    }

    func publish(_ scope: AiVocabularyScope, after revision: UInt64) -> Bool {
        let before = state.withLock {
            $0.reads += 1
            let before = $0.beforeRead
            $0.beforeRead = nil
            return before
        }
        before?()
        return state.withLock {
            guard $0.revision == revision, $0.changing == 0 else { return false }
            $0.scope = scope
            $0.revision &+= 1
            return true
        }
    }
}

private struct PublicationFixture {
    let directory: StorageTestDirectory
    let defaults: StorageTestDefaults
    let settings: DictionaryLibrarySettings
    let store: PersistenceStore
    let authority: PublicationAuthority
    let service: DictionaryLibraryService

    init() throws {
        directory = try StorageTestDirectory()
        defaults = StorageTestDefaults()
        settings = DictionaryLibrarySettings(defaults: defaults.defaults)
        store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        authority = PublicationAuthority()
        service = DictionaryLibraryService(
            librariesDirectory: directory.url.appendingPathComponent("Libraries"),
            settings: settings, persistenceStore: store, publication: authority.callbacks)
    }

    func remove() {
        store.closeConnection()
        defaults.remove()
        directory.remove()
    }
}
