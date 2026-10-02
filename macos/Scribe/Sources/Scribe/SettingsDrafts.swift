import Foundation

/// A kind of entry a Settings tab adds from its drafts.
enum SettingsDraftEntry: Hashable, Sendable {
    case dictionaryRule
    case snippet
    case appProfile
}

/// What the user has typed into Settings but not saved yet (a new dictionary rule, snippet or app profile, and the
/// two secret fields), the word pack workspace, plus the section that was showing. The app owns this rather than
/// the window: page navigation keeps pending input, and a normal close asks to save, discard or keep editing.
/// Already-immediate macOS settings are not staged here; each tab reads those when it appears.
///
/// A typed secret stays in memory until it is saved or cleared, or Scribe quits, as it did in a kept window. It is
/// never written anywhere by this type and never logged.
@MainActor
final class SettingsDrafts: ObservableObject {
    @Published var section: SettingsSection? = .dictation

    @Published var dictionaryPattern = ""
    @Published var dictionaryReplacement = ""
    @Published var dictionaryWholeWord = true

    @Published var snippetPhrase = ""
    @Published var snippetTemplate = ""

    @Published var profileName = ""
    @Published var profileBundleIdentifiers = ""
    @Published var profileWritingStyle = ""
    @Published var profileNewlineMode: NewlineInjectionMode = .smartFlatten

    @Published var openAIApiKey = ""
    @Published var azureClientSecret = ""

    // The app owns this draft too: changing pages must not destroy a word pack edit.
    @Published var wordPackWorkspace = LibraryWorkspace(libraries: [])
    @Published private(set) var wordPacksLoaded = false
    @Published private(set) var isSaving = false
    @Published private(set) var footerMessage: String?
    @Published private(set) var saveFailed = false
    @Published private(set) var saveRevision: UInt64 = 0
    private var wordPacksLoading = false
    private var wordPackLoadRevision: UInt64 = 0
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    var saveOperation: (@MainActor (SettingsDrafts) async throws -> Void)?
    @Published private(set) var entriesBeingAdded: Set<SettingsDraftEntry> = []

    var unsavedSections: [String] {
        var sections: [String] = []
        if wordPackWorkspace.hasUnsavedChanges { sections.append("Word packs") }
        if !dictionaryPattern.isEmpty || !dictionaryReplacement.isEmpty { sections.append("Dictionary") }
        if !snippetPhrase.isEmpty || !snippetTemplate.isEmpty { sections.append("Voice snippets") }
        if !profileName.isEmpty || !profileBundleIdentifiers.isEmpty || !profileWritingStyle.isEmpty
            || profileNewlineMode != .smartFlatten
        {
            sections.append("App profiles")
        }
        if !openAIApiKey.isEmpty || !azureClientSecret.isEmpty { sections.append("AI cleanup") }
        return sections
    }

    var hasUnsavedChanges: Bool { !unsavedSections.isEmpty }
    var isBusy: Bool { isSaving || !entriesBeingAdded.isEmpty }

    var footerText: String {
        if isBusy { return "Saving..." }
        if hasUnsavedChanges { return "Unsaved changes: \(unsavedSections.joined(separator: ", "))." }
        return "No unsaved changes."
    }

    func loadWordPacks(using service: DictionaryLibraryService) async throws {
        guard !wordPacksLoaded, !wordPacksLoading else { return }
        wordPacksLoading = true
        let revision = wordPackLoadRevision
        defer {
            if revision == wordPackLoadRevision { wordPacksLoading = false }
        }
        let catalog = try await service.loadCatalog()
        guard revision == wordPackLoadRevision, !wordPackWorkspace.hasUnsavedChanges else { return }
        wordPackWorkspace = LibraryWorkspace(catalog: catalog)
        wordPacksLoaded = true
    }

    func windowClosed() {
        wordPackLoadRevision &+= 1
        wordPacksLoading = false
        if !wordPackWorkspace.hasUnsavedChanges { wordPacksLoaded = false }
    }

    @discardableResult
    func save() async -> Bool {
        guard !isBusy else { return false }
        guard hasUnsavedChanges else { return true }
        isSaving = true
        footerMessage = nil
        saveFailed = false
        defer {
            isSaving = false
            saveRevision &+= 1
            resumeIdleWaiters()
        }
        do {
            guard let saveOperation else { throw SettingsDraftSaveError("Settings are not ready. Try again.") }
            try await saveOperation(self)
            guard !hasUnsavedChanges else {
                footerMessage = "Settings changed while saving. Save again to keep your latest edits."
                return false
            }
            footerMessage = "Changes saved."
            return true
        } catch {
            saveFailed = true
            footerMessage =
                (error as? SettingsDraftSaveError)?.message
                ?? "Couldn't save your changes. Your unsaved edits have been kept. Try again."
            return false
        }
    }

    func discard() {
        guard !isBusy else { return }
        wordPackWorkspace.discard()
        dictionaryPattern = ""
        dictionaryReplacement = ""
        dictionaryWholeWord = true
        snippetPhrase = ""
        snippetTemplate = ""
        profileName = ""
        profileBundleIdentifiers = ""
        profileWritingStyle = ""
        profileNewlineMode = .smartFlatten
        openAIApiKey = ""
        azureClientSecret = ""
        saveFailed = false
        footerMessage = "Discarded unsaved changes."
    }
}

struct SettingsDraftSaveError: Error {
    let message: String
    init(_ message: String) { self.message = message }
}

enum SettingsCloseChoice {
    case save, discard, keepEditing
}

extension SettingsDrafts {
    func acceptClose(_ choice: SettingsCloseChoice) async -> Bool {
        guard !isBusy else { return false }
        switch choice {
        case .save: return await save()
        case .discard:
            discard()
            return true
        case .keepEditing: return false
        }
    }

    func configureSave(
        store: PersistenceStore,
        libraries: DictionaryLibraryService,
        onChanged: @escaping @MainActor () -> Void
    ) {
        saveOperation = { drafts in
            let nonblank: (String) -> Bool = { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            if drafts.unsavedSections.contains("Dictionary"),
                !nonblank(drafts.dictionaryPattern) || !nonblank(drafts.dictionaryReplacement)
            {
                throw SettingsDraftSaveError("Enter both the spoken and written forms in Dictionary.")
            }
            if drafts.unsavedSections.contains("Voice snippets"),
                !nonblank(drafts.snippetPhrase) || !nonblank(drafts.snippetTemplate)
            {
                throw SettingsDraftSaveError("Enter both the phrase and template in Voice snippets.")
            }
            if drafts.unsavedSections.contains("App profiles"),
                !nonblank(drafts.profileName)
                    || drafts.profileBundleIdentifiers.split(separator: ",").allSatisfy({ !nonblank(String($0)) })
            {
                throw SettingsDraftSaveError("Enter a name and app identifiers in App profiles.")
            }
            if drafts.wordPackWorkspace.hasUnsavedChanges,
                let issue = drafts.wordPackWorkspace.captureChangeSet().issues.first
            {
                throw SettingsDraftSaveError(LibraryEditor.message(for: issue))
            }
            // Existing macOS add/credential actions remain immediate; the footer submits only pending input.
            if !drafts.dictionaryPattern.isEmpty || !drafts.dictionaryReplacement.isEmpty {
                let model = DictionarySettingsModel(access: .live(store), drafts: drafts, onChanged: onChanged)
                guard model.canAdd else {
                    throw SettingsDraftSaveError("Enter both the spoken and written forms in Dictionary.")
                }
                await model.addFromDrafts()
                if let error = model.errorMessage { throw SettingsDraftSaveError(error) }
            }
            if !drafts.snippetPhrase.isEmpty || !drafts.snippetTemplate.isEmpty {
                let model = SnippetSettingsModel(access: .live(store), drafts: drafts, onChanged: onChanged)
                guard model.canAdd else {
                    throw SettingsDraftSaveError("Enter both the phrase and template in Voice snippets.")
                }
                await model.addFromDrafts()
                if let error = model.errorMessage { throw SettingsDraftSaveError(error) }
            }
            if drafts.unsavedSections.contains("App profiles") {
                let model = AppProfileSettingsModel(access: .live(store), drafts: drafts, onChanged: onChanged)
                guard model.canAdd else {
                    throw SettingsDraftSaveError("Enter a name and app identifiers in App profiles.")
                }
                await model.addFromDrafts()
                if let error = model.errorMessage { throw SettingsDraftSaveError(error) }
            }
            if !drafts.openAIApiKey.isEmpty || !drafts.azureClientSecret.isEmpty {
                let model = CleanupSettingsModel(access: .live, drafts: drafts)
                model.reload()
                if !drafts.openAIApiKey.isEmpty {
                    model.saveOpenAIApiKey()
                    if let error = model.errorMessage { throw SettingsDraftSaveError(error) }
                }
                if !drafts.azureClientSecret.isEmpty {
                    model.saveAzureClientSecret()
                    if let error = model.errorMessage { throw SettingsDraftSaveError(error) }
                }
            }
            if drafts.wordPackWorkspace.hasUnsavedChanges {
                let capture = drafts.wordPackWorkspace.captureChangeSet()
                if let issue = capture.issues.first {
                    throw SettingsDraftSaveError(LibraryEditor.message(for: issue))
                }
                guard let changeSet = capture.changeSet else {
                    throw SettingsDraftSaveError("Word packs could not be saved. Review their edits and try again.")
                }
                let revision = drafts.wordPackWorkspace.draft.revision
                let catalog = try await Task.detached {
                    try libraries.save(changeSet: changeSet)
                    return try await libraries.loadCatalog()
                }.value
                if drafts.wordPackWorkspace.draft.revision == revision {
                    drafts.wordPackWorkspace = LibraryWorkspace(catalog: catalog)
                }
                onChanged()
            }
        }
    }

    func isAdding(_ entry: SettingsDraftEntry) -> Bool {
        entriesBeingAdded.contains(entry)
    }

    /// Claims the add of `entry` from these drafts: true when none is in flight, and then one is until
    /// `finishAdding(_:)`. It runs on the main actor with no suspension, so of two models that ask, one gets it.
    func beginAdding(_ entry: SettingsDraftEntry) -> Bool {
        entriesBeingAdded.insert(entry).inserted
    }

    func finishAdding(_ entry: SettingsDraftEntry) {
        entriesBeingAdded.remove(entry)
        resumeIdleWaiters()
    }

    func waitUntilIdle() async {
        guard isBusy else { return }
        await withCheckedContinuation { idleWaiters.append($0) }
    }

    private func resumeIdleWaiters() {
        guard !isBusy else { return }
        let waiters = idleWaiters
        idleWaiters.removeAll()
        for waiter in waiters { waiter.resume() }
    }
}
