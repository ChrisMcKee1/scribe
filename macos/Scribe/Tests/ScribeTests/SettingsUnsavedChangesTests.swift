import AppKit
import XCTest

@testable import Scribe

@MainActor
final class SettingsUnsavedChangesTests: XCTestCase {
    func testNavigationAndSavedLoadsAreNotEdits() {
        let drafts = SettingsDrafts()
        drafts.section = .dictionary
        drafts.wordPackWorkspace = LibraryWorkspace(libraries: [])
        XCTAssertFalse(drafts.hasUnsavedChanges)
        XCTAssertEqual(drafts.footerText, "No unsaved changes.")
    }

    func testDirtySectionsDescribeOnlyPendingInput() {
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "email"
        drafts.profileName = "Work"
        drafts.azureClientSecret = "test-only"
        XCTAssertEqual(drafts.unsavedSections, ["Voice snippets", "App profiles", "AI cleanup"])
        XCTAssertTrue(drafts.footerText.contains("Unsaved changes"))
        drafts.snippetPhrase = ""
        drafts.profileName = ""
        drafts.azureClientSecret = ""
        XCTAssertFalse(drafts.hasUnsavedChanges)
    }

    func testWordPacksSurviveNavigationAndUndoRestoresCleanBaseline() {
        let drafts = SettingsDrafts()
        _ = drafts.wordPackWorkspace.createLibrary(name: "Work")
        drafts.section = .history
        XCTAssertEqual(drafts.unsavedSections, ["Word packs"])
        drafts.wordPackWorkspace.undo()
        XCTAssertFalse(drafts.hasUnsavedChanges)
        drafts.wordPackWorkspace.redo()
        XCTAssertTrue(drafts.hasUnsavedChanges)
    }

    func testKeepEditingDoesNotSaveOrDiscard() async {
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "email"
        drafts.saveOperation = { _ in XCTFail("Keep editing must not save") }
        let accepted = await drafts.acceptClose(.keepEditing)
        XCTAssertFalse(accepted)
        XCTAssertEqual(drafts.snippetPhrase, "email")
    }

    func testDiscardResetsAllPendingFieldsAndWordPacks() async {
        let drafts = SettingsDrafts()
        drafts.dictionaryPattern = "word"
        drafts.dictionaryReplacement = "Word"
        drafts.snippetTemplate = "template"
        drafts.profileWritingStyle = "formal"
        drafts.openAIApiKey = "test-only"
        _ = drafts.wordPackWorkspace.createLibrary(name: "Work")
        let accepted = await drafts.acceptClose(.discard)
        XCTAssertTrue(accepted)
        XCTAssertFalse(drafts.hasUnsavedChanges)
        XCTAssertTrue(drafts.wordPackWorkspace.draft.libraries.isEmpty)
        XCTAssertEqual(drafts.footerMessage, "Discarded unsaved changes.")
    }

    func testSaveFailureKeepsEditsAndPreventsClose() async {
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "email"
        drafts.saveOperation = { _ in throw SettingsDraftSaveError("Couldn't save. Try again.") }
        let accepted = await drafts.acceptClose(.save)
        XCTAssertFalse(accepted)
        XCTAssertTrue(drafts.hasUnsavedChanges)
        XCTAssertTrue(drafts.saveFailed)
        XCTAssertEqual(drafts.footerMessage, "Couldn't save. Try again.")
        XCTAssertFalse(drafts.isSaving)
    }

    func testSuccessfulSaveAllowsClose() async {
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "email"
        drafts.saveOperation = { $0.snippetPhrase = "" }
        let accepted = await drafts.acceptClose(.save)
        XCTAssertTrue(accepted)
        XCTAssertFalse(drafts.hasUnsavedChanges)
        XCTAssertEqual(drafts.footerMessage, "Changes saved.")
    }

    func testEditsMadeDuringSaveAreNotAcknowledgedAsSaved() async {
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "email"
        drafts.saveOperation = { $0.snippetPhrase = "later edit" }
        let accepted = await drafts.acceptClose(.save)
        XCTAssertFalse(accepted)
        XCTAssertEqual(drafts.snippetPhrase, "later edit")
        XCTAssertTrue(drafts.footerMessage?.contains("changed while saving") == true)
    }

    func testAnEntryBeingAddedBlocksDiscardAndClose() async {
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "email"
        XCTAssertTrue(drafts.beginAdding(.snippet))
        drafts.discard()
        XCTAssertEqual(drafts.snippetPhrase, "email")
        let accepted = await drafts.acceptClose(.discard)
        XCTAssertFalse(accepted)
        drafts.finishAdding(.snippet)
        XCTAssertFalse(drafts.isBusy)
    }

    func testLoadedCatalogStaysCleanAndFooterSavesRealWordPacksAndSnippets() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        let drafts = SettingsDrafts()
        drafts.configureSave(store: fixture.store, libraries: fixture.libraries, onChanged: {})
        try await drafts.loadWordPacks(using: fixture.libraries)
        XCTAssertFalse(drafts.hasUnsavedChanges)
        let id = drafts.wordPackWorkspace.createLibrary(name: "Footer test")
        _ = drafts.wordPackWorkspace.addTerm(id, values: TermValues("scribe test", "ScribeTest"))
        drafts.wordPackWorkspace.setEnabled(id, enabled: true)
        drafts.snippetPhrase = "my email"
        drafts.snippetTemplate = "test@example.invalid"
        // A page revisit must never replace an unsaved workspace with another stored catalog.
        try await drafts.loadWordPacks(using: fixture.libraries)
        XCTAssertNotNil(drafts.wordPackWorkspace.draft.find(id))

        let saved = await drafts.save()
        XCTAssertTrue(saved, drafts.footerMessage ?? "")
        XCTAssertFalse(drafts.hasUnsavedChanges)
        let catalog = try await fixture.libraries.loadCatalog()
        XCTAssertTrue(catalog.libraries.contains { $0.library.id == id })
        let snippets = try await fixture.store.loadAllSnippets()
        XCTAssertEqual(snippets.map(\.phrase), ["my email"])
        XCTAssertEqual(snippets.map(\.template), ["test@example.invalid"])
    }

    func testValidationFailureWritesNothingAndKeepsWordPackEdit() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        let drafts = SettingsDrafts()
        drafts.configureSave(store: fixture.store, libraries: fixture.libraries, onChanged: {})
        try await drafts.loadWordPacks(using: fixture.libraries)
        let id = drafts.wordPackWorkspace.createLibrary(name: "Invalid save")
        drafts.snippetPhrase = "incomplete"
        let saved = await drafts.save()
        XCTAssertFalse(saved)
        XCTAssertTrue(drafts.hasUnsavedChanges)
        let snippets = try await fixture.store.loadAllSnippets()
        XCTAssertTrue(snippets.isEmpty)
        let catalog = try await fixture.libraries.loadCatalog()
        XCTAssertFalse(catalog.libraries.contains { $0.library.id == id })
    }

    func testWindowDelegateKeepEditingAndSaveFailureNeverCloseTheWindow() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "incomplete"
        let window = NSWindow()
        window.isReleasedWhenClosed = false
        let controller = SettingsWindowController(
            window: window, drafts: drafts, onClose: { _ in XCTFail("Must stay open") },
            chooseClose: { _, sections in
                XCTAssertEqual(sections, ["Voice snippets"])
                return .keepEditing
            })
        XCTAssertFalse(controller.windowShouldClose(window))
        await controller.closeOperation?.value
        XCTAssertTrue(drafts.hasUnsavedChanges)

        drafts.saveOperation = { _ in throw SettingsDraftSaveError("Save failed.") }
        let failureController = SettingsWindowController(
            window: window, drafts: drafts, onClose: { _ in XCTFail("Save failure must stay open") },
            chooseClose: { _, _ in .save })
        XCTAssertFalse(failureController.windowShouldClose(window))
        await failureController.closeOperation?.value
        XCTAssertTrue(drafts.saveFailed)
        XCTAssertTrue(drafts.hasUnsavedChanges)
    }

    func testWindowDelegateAllowsCleanCloseAndWaitsForAnAdd() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        let window = NSWindow()
        window.isReleasedWhenClosed = false
        let controller = SettingsWindowController(
            window: window, drafts: drafts, onClose: { _ in },
            chooseClose: { _, _ in
                XCTAssertFalse(drafts.isBusy)
                return .keepEditing
            })
        XCTAssertTrue(controller.windowShouldClose(window))
        drafts.snippetPhrase = "email"
        XCTAssertTrue(drafts.beginAdding(.snippet))
        XCTAssertFalse(controller.windowShouldClose(window))
        drafts.finishAdding(.snippet)
        await controller.closeOperation?.value
        XCTAssertTrue(drafts.hasUnsavedChanges)
    }

    func testWindowDelegateActuallyClosesAfterSaveOrDiscard() async {
        _ = NSApplication.shared
        for choice in [SettingsCloseChoice.save, .discard] {
            let drafts = SettingsDrafts()
            drafts.snippetPhrase = "email"
            drafts.saveOperation = { $0.snippetPhrase = "" }
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 860, height: 600),
                styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.isReleasedWhenClosed = false
            let closed = expectation(description: "accepted close completes")
            let signal = SettingsTestSignal(closed)
            let controller = SettingsWindowController(
                window: window, drafts: drafts, onClose: { _ in signal.fulfill() },
                chooseClose: { _, _ in choice })
            XCTAssertFalse(controller.windowShouldClose(window))
            await controller.closeOperation?.value
            await fulfillment(of: [closed], timeout: 10)
            XCTAssertFalse(drafts.hasUnsavedChanges)
        }
    }

    func testReopeningReadsFreshWordPacksWithoutMakingTheCatalogDirty() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        let drafts = SettingsDrafts()
        try await drafts.loadWordPacks(using: fixture.libraries)
        drafts.windowClosed()
        XCTAssertFalse(drafts.wordPacksLoaded)
        let catalog = try await fixture.libraries.loadCatalog()
        var external = LibraryWorkspace(catalog: catalog)
        let id = external.createLibrary(name: "Added elsewhere")
        try fixture.libraries.save(changeSet: XCTUnwrap(external.captureChangeSet().changeSet))
        try await drafts.loadWordPacks(using: fixture.libraries)
        XCTAssertNotNil(drafts.wordPackWorkspace.draft.find(id))
        XCTAssertFalse(drafts.hasUnsavedChanges)
    }
}

final class SettingsGapStorageFixture {
    let directory: URL
    let defaults = StorageTestDefaults()
    let store: PersistenceStore
    let libraries: DictionaryLibraryService

    init() throws {
        directory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
            .appendingPathComponent(".build/settings-gap-tests/\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        store = PersistenceStore(databaseURL: directory.appendingPathComponent("scribe.db"))
        try store.initialize()
        libraries = DictionaryLibraryService(
            librariesDirectory: directory.appendingPathComponent("Libraries", isDirectory: true),
            settings: DictionaryLibrarySettings(defaults: defaults.defaults),
            persistenceStore: store)
    }

    func remove() {
        defaults.remove()
        try? FileManager.default.removeItem(at: directory)
    }
}
