import AppKit
import XCTest

@testable import Scribe

@MainActor
final class SettingsUnsavedChangesTests: XCTestCase {
    func testIndicatorDraftSaveDiscardAndFailedValidation() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        let controller = OverlayPanelController(defaults: fixture.defaults.defaults, presentsPanel: false)
        let drafts = SettingsDrafts()
        drafts.configureIndicator(controller: controller, defaults: fixture.defaults.defaults)
        drafts.configureSave(store: fixture.store, libraries: fixture.libraries, onChanged: {})
        drafts.indicator?.select(.topRight)
        drafts.indicator?.showIndicator = false
        XCTAssertEqual(drafts.unsavedSections, ["Dictation"])
        drafts.section = .history
        XCTAssertEqual(drafts.indicator?.anchor, .topRight)
        drafts.snippetPhrase = "incomplete"
        let failed = await drafts.save()
        XCTAssertFalse(failed)
        XCTAssertTrue(controller.showIndicator)
        XCTAssertEqual(controller.anchor, .bottomCenter)
        drafts.snippetPhrase = ""
        let saved = await drafts.save()
        XCTAssertTrue(saved)
        XCTAssertFalse(drafts.hasUnsavedChanges)
        XCTAssertFalse(controller.showIndicator)
        XCTAssertEqual(controller.anchor, .topRight)
        drafts.indicator?.select(.center)
        drafts.indicator?.showIndicator = true
        drafts.discard()
        XCTAssertEqual(drafts.indicator?.anchor, .topRight)
        XCTAssertEqual(drafts.indicator?.showIndicator, false)
        XCTAssertFalse(drafts.hasUnsavedChanges)
    }

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

    func testDiscardRestoresTheSavedCleanupPrompts() {
        let drafts = SettingsDrafts()
        drafts.loadCleanupPrompts(
            writingStyle: "Saved style.", frontierPrompt: "Saved detailed guardrails.",
            localPrompt: "Saved local guardrails.")
        drafts.cleanupWritingStyle = "Unsaved style."
        drafts.cleanupFrontierPrompt = "Unsaved detailed guardrails."
        drafts.cleanupLocalPrompt = "Unsaved local guardrails."

        XCTAssertEqual(drafts.unsavedSections, ["AI cleanup"])
        drafts.discard()

        XCTAssertEqual(drafts.cleanupWritingStyle, "Saved style.")
        XCTAssertEqual(drafts.cleanupFrontierPrompt, "Saved detailed guardrails.")
        XCTAssertEqual(drafts.cleanupLocalPrompt, "Saved local guardrails.")
        XCTAssertFalse(drafts.hasUnsavedChanges)
    }

    func testRestoreDefaultsAreStagedAndSavedAsDefaultPreservingOverrides() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        let cleanup = makeCleanupStore().store
        cleanup.writingStyle = "Saved style."
        cleanup.frontierPrompt = "Saved detailed guardrails."
        cleanup.localPrompt = "Saved local guardrails."
        let drafts = SettingsDrafts()
        drafts.loadCleanupPrompts(
            writingStyle: cleanup.writingStyle, frontierPrompt: cleanup.frontierPrompt,
            localPrompt: cleanup.localPrompt)
        drafts.configureSave(
            store: fixture.store, libraries: fixture.libraries, cleanupSettings: cleanup, onChanged: {})

        drafts.restoreCleanupWritingStyle()
        drafts.restoreCleanupGuardrails()

        XCTAssertTrue(drafts.hasUnsavedCleanupPromptChanges)
        XCTAssertEqual(drafts.cleanupWritingStyle, CleanupPrompt.defaultWritingStyle)
        XCTAssertEqual(drafts.cleanupFrontierPrompt, CleanupPrompt.defaultFrontierPrompt)
        XCTAssertEqual(drafts.cleanupLocalPrompt, CleanupPrompt.defaultLocalPrompt)

        let saved = await drafts.save()
        XCTAssertTrue(saved, drafts.footerMessage ?? "")

        XCTAssertEqual(cleanup.writingStyle, "")
        XCTAssertEqual(cleanup.frontierPrompt, "")
        XCTAssertEqual(cleanup.localPrompt, "")
        XCTAssertFalse(drafts.hasUnsavedChanges)
        XCTAssertEqual(
            CleanupPrompt.effectiveOverride(cleanup.writingStyle, defaultValue: CleanupPrompt.defaultWritingStyle),
            CleanupPrompt.defaultWritingStyle)
        let reloaded = SettingsDrafts()
        reloaded.loadCleanupPrompts(
            writingStyle: cleanup.writingStyle, frontierPrompt: cleanup.frontierPrompt,
            localPrompt: cleanup.localPrompt)
        XCTAssertEqual(reloaded.cleanupWritingStyle, CleanupPrompt.defaultWritingStyle)
        XCTAssertEqual(reloaded.cleanupFrontierPrompt, CleanupPrompt.defaultFrontierPrompt)
        XCTAssertEqual(reloaded.cleanupLocalPrompt, CleanupPrompt.defaultLocalPrompt)
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

    func testCleanupPromptEditsMadeDuringSaveRemainDirtyAgainstTheSavedSnapshot() async {
        let drafts = SettingsDrafts()
        drafts.cleanupWritingStyle = "Style submitted for saving."
        let savedWritingStyle = drafts.cleanupWritingStyle
        let savedFrontierPrompt = drafts.cleanupFrontierPrompt
        let savedLocalPrompt = drafts.cleanupLocalPrompt
        let gate = SettingsTestGate()
        drafts.saveOperation = { drafts in
            await gate.pass()
            drafts.markCleanupPromptsSaved(
                writingStyle: savedWritingStyle,
                frontierPrompt: savedFrontierPrompt,
                localPrompt: savedLocalPrompt)
        }

        let saving = Task { await drafts.save() }
        await gate.waitForArrival()
        drafts.cleanupWritingStyle = "Style typed while saving."
        await gate.open()

        let saved = await saving.value
        XCTAssertFalse(saved)
        XCTAssertEqual(drafts.cleanupWritingStyle, "Style typed while saving.")
        XCTAssertTrue(drafts.hasUnsavedCleanupPromptChanges)
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

    func testQuitGuardCleanStateNeedsNoPromptAndDoesNotCloseSettings() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        let controller = SettingsWindowController(
            window: NSWindow(), drafts: drafts, onClose: { _ in XCTFail("Guard must not close Settings") },
            chooseClose: { _, _ in
                XCTFail("Clean Settings needs no prompt")
                return .keepEditing
            })
        let accepted = await controller.prepareForApplicationTermination()
        XCTAssertTrue(accepted)
        XCTAssertNil(controller.closeOperation)
    }

    func testQuitGuardUsesEveryCloseChoiceForStagedWordPacks() async {
        _ = NSApplication.shared
        for choice in [SettingsCloseChoice.save, .discard, .keepEditing] {
            let drafts = SettingsDrafts()
            let id = drafts.wordPackWorkspace.createLibrary(name: "Pending")
            var saves = 0
            drafts.saveOperation = {
                saves += 1
                $0.wordPackWorkspace.discard()
            }
            let controller = SettingsWindowController(
                window: NSWindow(), drafts: drafts, onClose: { _ in XCTFail("Guard must not close Settings") },
                chooseClose: { _, sections in
                    XCTAssertEqual(sections, ["Word packs"])
                    return choice
                })
            let accepted = await controller.prepareForApplicationTermination()
            XCTAssertEqual(accepted, choice != .keepEditing)
            XCTAssertEqual(saves, choice == .save ? 1 : 0)
            XCTAssertEqual(drafts.hasUnsavedChanges, choice == .keepEditing)
            XCTAssertEqual(drafts.wordPackWorkspace.draft.find(id) != nil, choice == .keepEditing)
        }
    }

    func testQuitGuardSavePersistsWordPacksBeforeApproving() async throws {
        _ = NSApplication.shared
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        let drafts = SettingsDrafts()
        drafts.configureSave(store: fixture.store, libraries: fixture.libraries, onChanged: {})
        try await drafts.loadWordPacks(using: fixture.libraries)
        let id = drafts.wordPackWorkspace.createLibrary(name: "Saved before quitting")
        let controller = SettingsWindowController(
            window: NSWindow(), drafts: drafts, onClose: { _ in },
            chooseClose: { _, _ in .save })
        let accepted = await controller.prepareForApplicationTermination()
        XCTAssertTrue(accepted, drafts.footerMessage ?? "")
        let catalog = try await fixture.libraries.loadCatalog()
        XCTAssertNotNil(catalog.libraries.first { $0.library.id == id })
        XCTAssertFalse(drafts.hasUnsavedChanges)
    }

    func testQuitGuardSaveFailureRetainsWordPacksAndCanBeRetried() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        let id = drafts.wordPackWorkspace.createLibrary(name: "Pending")
        drafts.saveOperation = { _ in throw SettingsDraftSaveError("Save failed.") }
        let controller = SettingsWindowController(
            window: NSWindow(), drafts: drafts, onClose: { _ in XCTFail("Save failure must not close") },
            chooseClose: { _, _ in .save })
        let failed = await controller.prepareForApplicationTermination()
        XCTAssertFalse(failed)
        XCTAssertTrue(drafts.saveFailed)
        XCTAssertEqual(drafts.footerMessage, "Save failed.")
        XCTAssertNotNil(drafts.wordPackWorkspace.draft.find(id))
        drafts.saveOperation = { $0.wordPackWorkspace.discard() }
        let retried = await controller.prepareForApplicationTermination()
        XCTAssertTrue(retried)
    }

    func testQuitGuardWaitsForAnAddBeforeChoosingAndRejectsNewEditsDuringSave() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        drafts.snippetPhrase = "pending"
        XCTAssertTrue(drafts.beginAdding(.snippet))
        let started = expectation(description: "quit guard started")
        var prompts = 0
        let controller = SettingsWindowController(
            window: NSWindow(), drafts: drafts, onClose: { _ in },
            chooseClose: { _, _ in
                prompts += 1
                XCTAssertFalse(drafts.isBusy)
                return .save
            })
        drafts.saveOperation = { $0.snippetPhrase = "new edit" }
        let quitting = Task { @MainActor in
            started.fulfill()
            return await controller.prepareForApplicationTermination()
        }
        await fulfillment(of: [started], timeout: 10)
        XCTAssertEqual(prompts, 0)
        drafts.finishAdding(.snippet)
        let accepted = await quitting.value
        XCTAssertFalse(accepted)
        XCTAssertEqual(prompts, 1)
        XCTAssertEqual(drafts.snippetPhrase, "new edit")
    }

    func testQuitDuringWindowCloseAndRepeatedQuitShareOnePrompt() async {
        _ = NSApplication.shared
        let drafts = SettingsDrafts()
        _ = drafts.wordPackWorkspace.createLibrary(name: "Pending")
        let gate = SettingsClosePromptGate()
        let window = NSWindow()
        window.isReleasedWhenClosed = false
        let controller = SettingsWindowController(
            window: window, drafts: drafts, onClose: { _ in XCTFail("Keep editing must not close") },
            chooseClose: { _, _ in await gate.choose() })
        XCTAssertFalse(controller.windowShouldClose(window))
        await gate.waitUntilShown()
        let joined = expectation(description: "both quits joined the pending close")
        joined.expectedFulfillmentCount = 2
        let first = Task {
            joined.fulfill()
            return await controller.prepareForApplicationTermination()
        }
        let second = Task {
            joined.fulfill()
            return await controller.prepareForApplicationTermination()
        }
        await fulfillment(of: [joined], timeout: 10)
        gate.finish(.keepEditing)
        let firstAccepted = await first.value
        let secondAccepted = await second.value
        await controller.closeOperation?.value
        XCTAssertFalse(firstAccepted)
        XCTAssertFalse(secondAccepted)
        XCTAssertEqual(gate.promptCount, 1)
        XCTAssertTrue(drafts.hasUnsavedChanges)
    }
}

@MainActor
private final class SettingsClosePromptGate {
    private var choice: CheckedContinuation<SettingsCloseChoice, Never>?
    private var shown: CheckedContinuation<Void, Never>?
    private(set) var promptCount = 0

    func choose() async -> SettingsCloseChoice {
        await withCheckedContinuation {
            choice = $0
            promptCount += 1
            shown?.resume()
            shown = nil
        }
    }

    func waitUntilShown() async {
        guard promptCount == 0 else { return }
        await withCheckedContinuation { shown = $0 }
    }

    func finish(_ result: SettingsCloseChoice) {
        choice?.resume(returning: result)
        choice = nil
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
