import XCTest

@testable import Scribe

final class SnippetAndProfileSettingsModelTests: XCTestCase {
    private var directory: StorageTestDirectory!

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private func makeStore(_ control: StorageTestQueueControl? = nil) throws -> PersistenceStore {
        let store = PersistenceStore(
            databaseURL: directory.databaseURL, testHooks: control?.hooks ?? PersistenceStore.TestHooks())
        try store.initialize()
        return store
    }

    // MARK: - Snippets

    @MainActor
    func testAddingASnippetWhileAWriteHoldsTheStorageQueueLeavesTheMainActorFree() async throws {
        let control = StorageTestQueueControl()
        let store = try makeStore(control)
        let drafts = SettingsDrafts()
        let refreshes = SettingsTestCounter()
        let model = SnippetSettingsModel(access: .live(store), drafts: drafts, onChanged: { refreshes.increment() })
        await model.reload()

        control.holdOperation()
        let write = StorageTestBackground.recordDictation(on: store)
        await control.holding.wait()

        drafts.snippetPhrase = "sign off block"
        drafts.snippetTemplate = "Best,\nChris"
        let addQueued = control.nextForegroundCaller()
        let adding = Task { await model.addFromDrafts() }
        await addQueued.wait()

        XCTAssertTrue(model.isAdding)
        let otherWork = Task { @MainActor in 7 }
        let otherResult = await otherWork.value
        XCTAssertEqual(otherResult, 7)
        XCTAssertTrue(model.snippets.isEmpty)
        XCTAssertEqual(refreshes.count, 0)

        control.release.signal()
        await adding.value
        XCTAssertTrue(write.wait())

        XCTAssertEqual(model.snippets.map(\.phrase), ["sign off block"])
        XCTAssertEqual(model.snippets.map(\.template), ["Best,\nChris"])
        XCTAssertEqual(refreshes.count, 1)
        XCTAssertEqual(drafts.snippetPhrase, "")
        XCTAssertEqual(drafts.snippetTemplate, "")
    }

    /// What the user typed while a save was running is theirs, not the saved snippet's.
    @MainActor
    func testTypingWhileASnippetIsSavedStaysInTheDrafts() async {
        let gate = SettingsTestGate()
        let drafts = SettingsDrafts()
        let model = SnippetSettingsModel(
            access: SnippetSettingsAccess(
                loadSnippets: { [] },
                addSnippet: { _ in await gate.pass() },
                setEnabled: { _, _ in },
                deleteSnippet: { _ in }),
            drafts: drafts,
            onChanged: {})

        drafts.snippetPhrase = "first phrase"
        drafts.snippetTemplate = "first template"
        let adding = Task { await model.addFromDrafts() }
        await gate.waitForArrival()
        drafts.snippetPhrase = "second phrase"
        await gate.open()
        await adding.value

        XCTAssertEqual(drafts.snippetPhrase, "second phrase")
        XCTAssertEqual(drafts.snippetTemplate, "first template")
    }

    @MainActor
    func testSwitchingAndDeletingASnippetGoThroughTheStore() async throws {
        let store = try makeStore()
        let id = try store.insertSnippet(Snippet(phrase: "greeting", template: "Hello there"))
        let refreshes = SettingsTestCounter()
        let model = SnippetSettingsModel(
            access: .live(store), drafts: SettingsDrafts(), onChanged: { refreshes.increment() })
        await model.reload()
        let snippet = try XCTUnwrap(model.snippets.first)

        await model.setEnabled(snippet, enabled: false)
        XCTAssertEqual(try store.fetchAllSnippets().map(\.enabled), [false])
        XCTAssertEqual(model.snippets.map(\.enabled), [false])

        await model.delete(snippet)
        XCTAssertTrue(try store.fetchAllSnippets().isEmpty)
        XCTAssertTrue(model.snippets.isEmpty)
        XCTAssertEqual(refreshes.count, 2)
        XCTAssertEqual(snippet.id, id)
    }

    // MARK: - App profiles

    @MainActor
    func testAProfileAddedFromTheDraftsIsStoredAndTheDraftsReset() async throws {
        let store = try makeStore()
        let drafts = SettingsDrafts()
        let refreshes = SettingsTestCounter()
        let model = AppProfileSettingsModel(access: .live(store), drafts: drafts, onChanged: { refreshes.increment() })
        await model.reload()

        drafts.profileName = "Terminals"
        drafts.profileBundleIdentifiers = "com.apple.Terminal, com.googlecode.iterm2,"
        drafts.profileNewlineMode = .keepNewlines
        XCTAssertTrue(model.canAdd)
        await model.addFromDrafts()

        let stored = try store.fetchAppProfiles()
        XCTAssertEqual(stored.map(\.name), ["Terminals"])
        XCTAssertEqual(stored.first?.bundleIdentifiers, ["com.apple.Terminal", "com.googlecode.iterm2"])
        XCTAssertEqual(stored.first?.newlineHandling, .keepNewlines)
        XCTAssertNil(stored.first?.writingStylePrompt)
        XCTAssertEqual(model.profiles, stored)
        XCTAssertEqual(refreshes.count, 1)
        XCTAssertEqual(drafts.profileName, "")
        XCTAssertEqual(drafts.profileBundleIdentifiers, "")
        XCTAssertEqual(drafts.profileNewlineMode, .smartFlatten)

        let profile = try XCTUnwrap(model.profiles.first)
        await model.delete(profile)
        XCTAssertTrue(try store.fetchAppProfiles().isEmpty)
        XCTAssertEqual(refreshes.count, 2)
    }

    @MainActor
    func testAFailedSnippetAddStaysShownWhenAnOlderReloadFinishesAfterIt() async {
        let gate = SettingsTestGate()
        let drafts = SettingsDrafts()
        let model = SnippetSettingsModel(
            access: SnippetSettingsAccess(
                loadSnippets: {
                    await gate.pass()
                    return [Snippet(id: 1, phrase: "stored", template: "Stored")]
                },
                addSnippet: { _ in throw StorageTestFailure(message: "the snippet was not saved") },
                setEnabled: { _, _ in },
                deleteSnippet: { _ in }),
            drafts: drafts,
            onChanged: {})

        let loading = Task { await model.reload() }
        await gate.waitForArrival()
        drafts.snippetPhrase = "sign off block"
        drafts.snippetTemplate = "Best"
        await model.addFromDrafts()
        XCTAssertEqual(model.errorMessage, "the snippet was not saved")

        await gate.open()
        await loading.value

        XCTAssertEqual(model.snippets.map(\.phrase), ["stored"])
        XCTAssertEqual(model.errorMessage, "the snippet was not saved")
        XCTAssertNil(model.loadError)
    }

    @MainActor
    func testAFailedProfileAddStaysShownWhenAnOlderReloadFinishesAfterIt() async {
        let gate = SettingsTestGate()
        let drafts = SettingsDrafts()
        let model = AppProfileSettingsModel(
            access: AppProfileSettingsAccess(
                loadProfiles: {
                    await gate.pass()
                    return [
                        AppProfile(id: 1, name: "Stored", bundleIdentifiers: ["com.apple.Terminal"], processNames: [])
                    ]
                },
                addProfile: { _ in throw StorageTestFailure(message: "the profile was not saved") },
                deleteProfile: { _ in }),
            drafts: drafts,
            onChanged: {})

        let loading = Task { await model.reload() }
        await gate.waitForArrival()
        drafts.profileName = "Chat"
        drafts.profileBundleIdentifiers = "com.tinyspeck.slackmacgap"
        await model.addFromDrafts()
        XCTAssertEqual(model.errorMessage, "the profile was not saved")

        await gate.open()
        await loading.value

        XCTAssertEqual(model.profiles.map(\.name), ["Stored"])
        XCTAssertEqual(model.errorMessage, "the profile was not saved")
        XCTAssertNil(model.loadError)
    }

    @MainActor
    func testAFailedProfileWriteIsShownAndKeepsTheDrafts() async {
        let drafts = SettingsDrafts()
        let refreshes = SettingsTestCounter()
        let model = AppProfileSettingsModel(
            access: AppProfileSettingsAccess(
                loadProfiles: { [] },
                addProfile: { _ in throw StorageTestFailure(message: "the profile was not saved") },
                deleteProfile: { _ in }),
            drafts: drafts,
            onChanged: { refreshes.increment() })
        drafts.profileName = "Chat"
        drafts.profileBundleIdentifiers = "com.tinyspeck.slackmacgap"

        await model.addFromDrafts()

        XCTAssertEqual(model.errorMessage, "the profile was not saved")
        XCTAssertEqual(drafts.profileName, "Chat")
        XCTAssertEqual(refreshes.count, 0)
        XCTAssertFalse(model.isAdding)
    }
}
