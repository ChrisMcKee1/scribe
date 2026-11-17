import XCTest

@testable import Scribe

/// Who owns the word pack permission AI cleanup is held to, through the service that publishes it
/// (`DictionaryLibraryService.loadVocabulary` and `save`, `AiVocabularyGate`). The order the operations began in
/// decides it, never a persisted generation and never a value that merely equals another. Every test uses a gate of
/// its own, and the transport of the requests in them is a stub.
///
/// The reads are the service's own `loadVocabulary()`, with nothing added to it, held where a test needs them: before
/// the first word pack file is checked (`BarrierFileManager`, the service's file manager) and at the last storage call
/// of the read, the one for the Recently deleted metadata (`StoreBarrier`, through the store's test hooks). A held
/// read has its ticket and has published nothing, so what a test does meanwhile happens between the two, the way a
/// delayed read meets a newer read or a save. A ticket taken after the point a read is held at, in
/// `loadVocabulary()`, would make `testADelayedReadOfOneGenerationNeverWidensWhatANewerReadFoundPaused` and
/// `testAReadHeldAtItsLastStorageCallNeverWidensWhatASaveWithdrewMeanwhile` fail.
final class WordPackPermissionOwnershipTests: XCTestCase {
    /// A point in a read that a test holds it at. The read calls `hold()` there and waits; the test sees that it has
    /// got there, does what it needs to meanwhile, and lets it go.
    private final class Barrier: @unchecked Sendable {
        // `@unchecked Sendable`: `reachedFlag` is only read or written while `lock` is held.
        private let lock = NSLock()
        private var reachedFlag = false
        private let released = StorageTestSignal()

        var hasReached: Bool {
            lock.lock()
            defer { lock.unlock() }
            return reachedFlag
        }

        func hold() {
            lock.lock()
            reachedFlag = true
            lock.unlock()
            // Bounded, so a test that forgets to release fails instead of hanging the run.
            _ = released.wait()
        }

        func release() {
            released.signal()
        }
    }

    /// The service's file manager, which stops the first file check after `arm()`. A read makes that check before it
    /// reads any word pack file, and after it has taken its ticket.
    private final class BarrierFileManager: FileManager, @unchecked Sendable {
        // `@unchecked Sendable`: `armed` is only read or written while `lock` is held.
        private let lock = NSLock()
        private var armed = false
        let barrier = Barrier()

        func arm() {
            lock.lock()
            armed = true
            lock.unlock()
        }

        override func fileExists(atPath path: String) -> Bool {
            lock.lock()
            let stop = armed
            armed = false
            lock.unlock()
            if stop {
                barrier.hold()
            }
            return super.fileExists(atPath: path)
        }
    }

    /// The store's test hooks, which stop the storage call a read makes last, for the Recently deleted metadata. A read
    /// makes it after it has looked at the word pack files and the state, and before it publishes. The hook runs on
    /// the calling thread before the call is queued, so the storage queue stays free for whatever the test does while
    /// the read is held. How many calls a read makes is counted first, with a read that is not held.
    private final class StoreBarrier: @unchecked Sendable {
        // `@unchecked Sendable`: `seen` and `holdAt` are only read or written while `lock` is held.
        private let lock = NSLock()
        private var seen = 0
        private var holdAt: Int?
        let barrier = Barrier()

        var hooks: PersistenceStore.TestHooks {
            var hooks = PersistenceStore.TestHooks()
            hooks.onForegroundWait = { [self] in
                registered()
            }
            return hooks
        }

        /// Starts counting the storage calls that register.
        func startCounting() {
            lock.lock()
            seen = 0
            lock.unlock()
        }

        /// How many storage calls have registered since `startCounting()` or `hold(atCall:)`.
        var count: Int {
            lock.lock()
            defer { lock.unlock() }
            return seen
        }

        /// Holds the storage call that is number `call` from now, once.
        func hold(atCall call: Int) {
            lock.lock()
            seen = 0
            holdAt = call
            lock.unlock()
        }

        private func registered() {
            lock.lock()
            seen += 1
            let stop = seen == holdAt
            if stop {
                holdAt = nil
            }
            lock.unlock()
            if stop {
                barrier.hold()
            }
        }
    }

    private final class Fixture {
        let directory: StorageTestDirectory
        let defaults = StorageTestDefaults()
        let storeBarrier: StoreBarrier
        let store: PersistenceStore
        let fileManager = BarrierFileManager()
        let libraries: URL
        let gate = AiVocabularyGate()

        init() throws {
            directory = try StorageTestDirectory()
            let storeBarrier = StoreBarrier()
            self.storeBarrier = storeBarrier
            store = PersistenceStore(databaseURL: directory.databaseURL, testHooks: storeBarrier.hooks)
            try store.initialize()
            libraries = directory.url.appendingPathComponent("Libraries", isDirectory: true)
            try FileManager.default.createDirectory(at: libraries, withIntermediateDirectories: true)
        }

        func remove() {
            defaults.remove()
            directory.remove()
        }

        func service(_ hooks: WordPackSaveHooks = WordPackSaveHooks()) -> DictionaryLibraryService {
            DictionaryLibraryService(
                fileManager: fileManager,
                librariesDirectory: libraries,
                settings: DictionaryLibrarySettings(defaults: defaults.defaults),
                persistenceStore: store,
                saveHooks: hooks,
                vocabularyGate: gate)
        }

        func file(_ stem: String) -> URL {
            libraries.appendingPathComponent("\(stem).csv", isDirectory: false)
        }

        func writeCustom(_ stem: String, name: String, terms: [TermValues]) throws {
            let data = try DictionaryLibraryCsv.exportManaged(
                LibraryCsvContent(name: name, category: "Custom", description: nil, basedOn: nil, rows: terms))
            try data.write(to: file(stem))
        }

        /// Two custom packs that are on and that AI cleanup may use, as packs that existed before the upgrade are.
        func writeTwoPermittedPacks() throws {
            try writeCustom("team", name: "Team", terms: [TermValues("kube", "Kubernetes")])
            try writeCustom("other", name: "Other", terms: [TermValues("helm", "Helm")])
            defaults.defaults.set(["team", "other"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        }

        /// The content hash of what is saved for the custom pack `stem` now.
        func hash(_ stem: String) throws -> String {
            LibraryContentHash(data: try Data(contentsOf: file(stem))).value
        }

        /// The bytes a save of `changeSet` writes for the custom pack `id`, encoded as the service encodes them.
        func proposedBytes(of changeSet: LibraryChangeSet, for id: String) throws -> Data {
            let write = try XCTUnwrap(changeSet.writes.first { $0.libraryID == id })
            let content = try XCTUnwrap(write.content)
            return try DictionaryLibraryCsv.exportManaged(
                LibraryCsvContent(
                    name: content.name, category: content.category, description: content.description,
                    basedOn: content.basedOn, rows: content.entries.map(TermValues.init(entry:))))
        }

        /// Stores a readable word pack state at `generation` that has only the custom pack "team" on and permitted, for
        /// what its file holds now.
        func writeState(generation: Int64) throws {
            var state = LibraryLocalState.absent
            state.generation = generation
            state.health = .ok
            state.enabledIds = ["team"]
            state.legacyEnabledIds = ["team"]
            state.aiPermissions = ["team": true]
            state.acceptedContent = ["team": try hash("team")]
            let value = String(decoding: try JSONEncoder().encode(state), as: UTF8.self)
            try store.writeStringSetting(key: DictionaryLibraryService.libraryStateKey, value: value)
        }
    }

    /// An Entra credential that waits for the test before it answers, as one that has to ask `az` or the tenant does.
    private final class SuspendedCredential: AzureCredentialProvider {
        let reached = LifecycleGate()
        let release = LifecycleGate()

        func accessToken(scope: String) async throws -> AzureAccessToken {
            reached.open()
            try await release.wait()
            return AzureAccessToken(token: "entra-token", expiresAt: .distantFuture)
        }
    }

    /// What `waitForRead` throws, after failing the test, when no read gets to the barrier.
    private struct NoReadHeld: Error {}

    /// Waits until a read is held at `barrier`, checking between short sleeps, and fails the test instead of hanging
    /// when none gets there. The sleeps only pace the checks; the barrier decides the order.
    private func waitForRead(
        _ description: String, at barrier: Barrier, file: StaticString = #filePath, line: UInt = #line
    ) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(10))
        while !barrier.hasReached {
            guard ContinuousClock.now < deadline else {
                XCTFail("Timed out waiting for \(description)", file: file, line: line)
                throw NoReadHeld()
            }
            try? await Task.sleep(for: .milliseconds(2))
        }
    }

    /// A cleanup request admitted under `scope` that waits for its credential, and is sent, or refused, once
    /// `credential.release` opens.
    private func startRequest(
        under scope: AiVocabularyScope, gate: AiVocabularyGate, credential: SuspendedCredential, log: RequestLog
    ) -> Task<CleanupResponse, any Error> {
        let admission = CleanupVocabularyAdmission(scope: scope, gate: gate)
        let provider = MicrosoftFoundryCleanupProvider(
            inferenceBase: URL(string: "https://my-res.services.ai.azure.com/openai/v1/")!,
            deployment: "gpt-5-mini",
            promptCachingEnabled: { true },
            credential: credential,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            })
        return Task {
            try await admission.whileAdmitted {
                try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Style."))
            }
        }
    }

    // MARK: - A read that began first can look last

    /// The finding: a ticket orders where a read began, not when it looked. A read is held before it checks any file; a
    /// newer read publishes the pack as permitted; a request is admitted under that and waits for its credential; and
    /// another program changes the pack's file. The held read then looks, finds the file is no longer the content that
    /// was accepted, and narrows what the newer read published, so the request is not sent. Nothing else loses its
    /// permission, and a read that begins after the file is put back publishes the pack again.
    func testAnOlderReadThatLooksLastNarrowsWhatANewerReadPublishedForAWaitingRequest() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        let service = fixture.service()
        fixture.fileManager.arm()
        let older = Task { try await service.loadVocabulary() }
        try await waitForRead("the older read to be held before it checks a file", at: fixture.fileManager.barrier)
        let newer = try await service.loadVocabulary()
        XCTAssertTrue(newer.aiScope.permittedLibraryIds.contains("team"), "premise: the newer read permits the pack")
        let publishedHash = try fixture.hash("team")
        let published = AiVocabularyScope(generation: 1, permittedContent: ["team": publishedHash])
        let untouched = AiVocabularyScope(generation: 1, permittedContent: ["other": try fixture.hash("other")])
        XCTAssertTrue(fixture.gate.covers(published), "premise: the newer read published the pack")
        let credential = SuspendedCredential()
        let log = RequestLog()
        let request = startRequest(under: published, gate: fixture.gate, credential: credential, log: log)
        try await credential.reached.wait()
        // Another program changes the pack's file before the held read looks at it.
        try fixture.writeCustom(
            "team", name: "Team", terms: [TermValues("kube", "Kubernetes"), TermValues("flux", "Flux")])
        XCTAssertNotEqual(try fixture.hash("team"), publishedHash, "premise: the file changed")

        fixture.fileManager.barrier.release()
        let looked = try await older.value

        XCTAssertFalse(looked.aiScope.permittedLibraryIds.contains("team"), "premise: the older read found it changed")
        XCTAssertFalse(fixture.gate.covers(published), "the older read's finding was dropped")
        XCTAssertTrue(fixture.gate.covers(untouched), "a pack nobody changed lost its permission")
        credential.release.open()
        do {
            _ = try await request.value
            XCTFail("a request composed under the content another program changed was sent")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .vocabularyChanged)
        }
        XCTAssertEqual(log.count, 0, "the request reached the transport")
        // Put the file back: a read that begins now publishes the permission again.
        try fixture.writeCustom("team", name: "Team", terms: [TermValues("kube", "Kubernetes")])
        let restored = try await service.loadVocabulary()
        XCTAssertTrue(restored.aiScope.permittedLibraryIds.contains("team"), "premise: the restored pack is permitted")
        XCTAssertTrue(fixture.gate.covers(published), "a read that began after the file was put back could not restore")
    }

    /// The same schedule with the pack paused instead of changed: two reads of one stored state share a generation and
    /// can still find different things, here a built-in paused by an edits file that cannot be read. The read that
    /// began first, held before it checks any file, finishes last and finds the pack readable again. It must not widen
    /// what the newer read found, which the generation cannot tell. Taking its ticket after it had been held would
    /// make it the newer read, and this test fail.
    func testADelayedReadOfOneGenerationNeverWidensWhatANewerReadFoundPaused() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        fixture.defaults.defaults.set(["github"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        let service = fixture.service()
        let editsURL = BuiltInLibraryOverlay.editsURL(root: fixture.libraries, id: "github")
        try FileManager.default.createDirectory(at: editsURL, withIntermediateDirectories: true)
        fixture.fileManager.arm()
        let delayed = Task { try await service.loadVocabulary() }
        try await waitForRead("the delayed read to be held before it checks a file", at: fixture.fileManager.barrier)
        let paused = try await service.loadVocabulary()
        XCTAssertFalse(paused.aiScope.permittedLibraryIds.contains("github"), "premise: the newer read found it paused")
        try FileManager.default.removeItem(at: editsURL)

        fixture.fileManager.barrier.release()
        let readable = try await delayed.value

        XCTAssertEqual(readable.generation, paused.generation, "premise: both reads found one stored generation")
        XCTAssertTrue(
            readable.aiScope.permittedLibraryIds.contains("github"), "premise: the delayed read found it readable")
        XCTAssertFalse(fixture.gate.covers(readable.aiScope), "a delayed read widened what a newer one found paused")
    }

    /// The finding: an unreadable state reads as a low generation, below the one the gate last saw, so ordering by the
    /// persisted generation kept a repaired state from restoring what the reset withdrew. The repaired state is read
    /// last, so it is the permission, with no blanket denial left behind.
    func testARepairedStateBelowAnEarlierGenerationRestoresThePermission() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        fixture.defaults.defaults.set(["team"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try fixture.writeCustom("team", name: "Team", terms: [TermValues("kube", "Kubernetes")])
        let service = fixture.service()
        try fixture.writeState(generation: 9)
        let high = try await service.loadVocabulary()
        XCTAssertEqual(high.generation, 9)
        XCTAssertTrue(high.aiScope.permittedLibraryIds.contains("team"), "premise: the state permits the pack")

        try fixture.store.writeStringSetting(key: DictionaryLibraryService.libraryStateKey, value: "not a state")
        let reset = try await service.loadVocabulary()
        XCTAssertTrue(reset.aiScope.permittedContent.isEmpty, "premise: an unreadable state permits nothing")
        XCTAssertFalse(fixture.gate.covers(high.aiScope), "the reset did not withdraw the permission")

        try fixture.writeState(generation: 2)
        let repaired = try await service.loadVocabulary()

        XCTAssertLessThan(repaired.generation, high.generation, "premise: the repaired state is the lower generation")
        XCTAssertTrue(fixture.gate.covers(high.aiScope), "a repaired state could not restore the permission")
    }

    // MARK: - A read held while a save runs

    /// The delay in the middle of the read: it has looked at the word pack files and the state, and is held at its last
    /// storage call, for the Recently deleted metadata, when a save withdraws a pack and commits. It then publishes
    /// what it saw before the save. It began before the save, so it can only narrow what the save left, and cannot give
    /// the pack back. A ticket taken after that storage call would make it the newest read, and this test fail.
    func testAReadHeldAtItsLastStorageCallNeverWidensWhatASaveWithdrewMeanwhile() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        let service = fixture.service()
        _ = try await service.loadVocabulary()
        fixture.storeBarrier.startCounting()
        _ = try await service.loadVocabulary()
        let calls = fixture.storeBarrier.count
        XCTAssertGreaterThan(calls, 0, "premise: the store reported the storage calls of a read")
        let team = AiVocabularyScope(generation: 1, permittedContent: ["team": try fixture.hash("team")])
        let other = AiVocabularyScope(generation: 1, permittedContent: ["other": try fixture.hash("other")])
        var workspace = LibraryWorkspace(catalog: try await service.loadCatalog())
        workspace.setAiPermission("team", permitted: false)
        let changeSet = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        fixture.storeBarrier.hold(atCall: calls)
        let held = Task { try await service.loadVocabulary() }
        try await waitForRead("the read to be held at its last storage call", at: fixture.storeBarrier.barrier)

        try service.save(changeSet: changeSet)
        fixture.storeBarrier.barrier.release()
        let stale = try await held.value

        XCTAssertTrue(
            stale.aiScope.permittedLibraryIds.contains("team"), "premise: the held read looked before the save")
        XCTAssertFalse(fixture.gate.covers(team), "a read that began before the save gave back what it withdrew")
        XCTAssertTrue(fixture.gate.covers(other))
    }

    /// The finding: a read finds a pack no longer permitted, say because another app rewrote it, and publishes while a
    /// save has it narrowed, to a result equal to the narrowing. The save then does not commit. Giving the pack back
    /// because the published scope equals the narrowing would hand a request, still waiting for its credential, the
    /// permission that read had revoked. The read is held at its last storage call, after it has looked, and goes on
    /// from the save's own hook, once the save has narrowed.
    func testAFailedSaveNeverGivesBackWhatAnEqualValuedReadRevokedForAWaitingRequest() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        let service = fixture.service()
        let admitted = try await service.loadVocabulary().aiScope
        XCTAssertEqual(Set(admitted.permittedContent.keys), ["team", "other"])
        let teamOnly = AiVocabularyScope(generation: 1, permittedContent: ["team": try fixture.hash("team")])
        fixture.storeBarrier.startCounting()
        _ = try await service.loadVocabulary()
        let calls = fixture.storeBarrier.count
        XCTAssertGreaterThan(calls, 0, "premise: the store reported the storage calls of a read")
        var workspace = LibraryWorkspace(catalog: try await service.loadCatalog())
        workspace.setAiPermission("other", permitted: false)
        let changeSet = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        let credential = SuspendedCredential()
        let log = RequestLog()
        let request = startRequest(under: admitted, gate: fixture.gate, credential: credential, log: log)
        try await credential.reached.wait()
        // Another program rewrites "other", so a read that looks now finds it no longer what was accepted.
        try fixture.writeCustom(
            "other", name: "Other", terms: [TermValues("helm", "Helm"), TermValues("flux", "Flux")])
        fixture.storeBarrier.hold(atCall: calls)
        let finished = StorageTestSignal()
        let looking = Task<LibraryVocabulary, any Error> {
            let vocabulary = try await service.loadVocabulary()
            finished.signal()
            return vocabulary
        }
        try await waitForRead("the read to be held at its last storage call", at: fixture.storeBarrier.barrier)
        let gate = fixture.gate
        let store = fixture.store
        let held = fixture.storeBarrier.barrier
        var hooks = WordPackSaveHooks()
        hooks.beforeCommit = {
            // The save has narrowed the permission. The held read goes on and publishes, then another writer changes
            // the stored state so the commit is refused.
            held.release()
            _ = finished.wait()
            try store.writeStringSetting(key: DictionaryLibraryService.libraryStateKey, value: "{\"changed\":true}")
        }

        XCTAssertThrowsError(try fixture.service(hooks).save(changeSet: changeSet)) { error in
            XCTAssertEqual(error as? WordPackSaveError, .changedWhileSaving)
        }

        let found = try await looking.value
        XCTAssertFalse(found.aiScope.permittedLibraryIds.contains("other"), "premise: the read found the pack revoked")
        XCTAssertFalse(gate.covers(admitted), "the failed save gave back a permission a read had revoked")
        XCTAssertTrue(gate.covers(teamOnly))
        credential.release.open()
        do {
            _ = try await request.value
            XCTFail("a request composed under a permission a read had revoked was sent")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .vocabularyChanged)
        }
        XCTAssertEqual(log.count, 0, "the request reached the transport")
    }

    // MARK: - A save judges a pack by the content its permission was published for

    /// The finding: another app already left exactly the bytes the save proposes for a pack, so what the save writes
    /// equals what the file holds when the save is prepared. The permission was published for the content before that,
    /// and the save accepts the new bytes, so a request admitted under the older content is stopped when the save
    /// stands, before any read publishes the new content.
    func testASaveOfBytesAnotherWriterAlreadyLeftStillWithdrawsThePublishedPermission() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        _ = try await fixture.service().loadVocabulary()
        let publishedHash = try fixture.hash("team")
        let published = AiVocabularyScope(generation: 1, permittedContent: ["team": publishedHash])
        XCTAssertTrue(fixture.gate.covers(published), "premise: the permission was published for the older content")
        let service = fixture.service()
        var workspace = LibraryWorkspace(catalog: try await service.loadCatalog())
        _ = workspace.addTerm("team", values: TermValues("flux", "Flux"))
        let changeSet = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        let proposed = try fixture.proposedBytes(of: changeSet, for: "team")
        try proposed.write(to: fixture.file("team"))
        let externalHash = try fixture.hash("team")
        XCTAssertNotEqual(externalHash, publishedHash, "premise: the file no longer holds the published content")
        let credential = SuspendedCredential()
        let log = RequestLog()
        let request = startRequest(under: published, gate: fixture.gate, credential: credential, log: log)
        try await credential.reached.wait()

        try service.save(changeSet: changeSet)
        credential.release.open()

        XCTAssertEqual(try fixture.hash("team"), externalHash, "premise: the save wrote the bytes already there")
        do {
            _ = try await request.value
            XCTFail("a request composed under the older content was sent after the save accepted the new bytes")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .vocabularyChanged)
        }
        XCTAssertEqual(log.count, 0, "the request reached the transport")
        // A read that begins after the save publishes the permission for what the pack holds now.
        let reread = try await service.loadVocabulary()
        XCTAssertTrue(reread.aiScope.permittedLibraryIds.contains("team"))
        XCTAssertTrue(fixture.gate.covers(reread.aiScope), "the pack was not permitted for the content it holds now")
        XCTAssertFalse(fixture.gate.covers(published), "the permission for the older content came back")
    }

    /// The control: the save writes a pack with exactly the content its permission was published for, so a request
    /// admitted under it is still sent, while the pack the save really changed is withdrawn.
    func testASaveThatKeepsAPackAtItsPublishedContentKeepsItsPermission() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        _ = try await fixture.service().loadVocabulary()
        let published = AiVocabularyScope(generation: 1, permittedContent: ["team": try fixture.hash("team")])
        let changedPack = AiVocabularyScope(generation: 1, permittedContent: ["other": try fixture.hash("other")])
        let service = fixture.service()
        let catalog = try await service.loadCatalog()
        var workspace = LibraryWorkspace(catalog: catalog)
        _ = workspace.addTerm("other", values: TermValues("flux", "Flux"))
        let captured = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        let team = try XCTUnwrap(catalog.find(id: "team"))
        let unchanged = LibraryWrite(
            libraryID: "team", builtIn: false, content: team.library, enabled: true, aiPermitted: true)
        let changeSet = LibraryChangeSet(
            baseGeneration: captured.baseGeneration,
            draftRevision: captured.draftRevision,
            writes: captured.writes + [unchanged],
            deletions: captured.deletions,
            recentlyDeletedActions: captured.recentlyDeletedActions,
            localState: captured.localState,
            localStateChanged: captured.localStateChanged)
        let proposed = try fixture.proposedBytes(of: changeSet, for: "team")
        XCTAssertEqual(proposed, try Data(contentsOf: fixture.file("team")), "premise: the save proposes what is saved")
        let credential = SuspendedCredential()
        let log = RequestLog()
        let request = startRequest(under: published, gate: fixture.gate, credential: credential, log: log)
        try await credential.reached.wait()

        try service.save(changeSet: changeSet)
        credential.release.open()

        let response = try await request.value
        XCTAssertEqual(response.cleanedText, "Cleaned.")
        XCTAssertEqual(log.count, 1)
        XCTAssertTrue(fixture.gate.covers(published))
        XCTAssertFalse(fixture.gate.covers(changedPack), "a pack the save rewrote kept its permission")
    }
}
