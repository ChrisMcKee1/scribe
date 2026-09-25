import Foundation

/// How the Snippets tab reaches storage. `live(_:)` uses the store's asynchronous forms, so no call waits on the main
/// actor. Tests pass their own.
struct SnippetSettingsAccess: Sendable {
    var loadSnippets: @Sendable () async throws -> [Snippet]
    var addSnippet: @Sendable (Snippet) async throws -> Void
    var setEnabled: @Sendable (_ id: Int64, _ enabled: Bool) async throws -> Void
    var deleteSnippet: @Sendable (_ id: Int64) async throws -> Void
}

extension SnippetSettingsAccess {
    static func live(_ store: PersistenceStore) -> SnippetSettingsAccess {
        SnippetSettingsAccess(
            loadSnippets: { try await store.loadAllSnippets() },
            addSnippet: { snippet in _ = try await store.addSnippet(snippet) },
            setEnabled: { id, enabled in try await store.saveSnippetEnabled(id: id, enabled: enabled) },
            deleteSnippet: { id in try await store.removeSnippet(id: id) })
    }
}

/// The Snippets tab: the stored voice snippets, adding, switching and deleting them. Storage calls are asynchronous,
/// only the newest read may replace the rows or report an error, and after each change the app's rules are refreshed
/// (`onChanged`) and the rows read again.
@MainActor
final class SnippetSettingsModel: ObservableObject {
    @Published private(set) var snippets: [Snippet] = []
    /// Why the newest read failed. Kept apart from `errorMessage`, so a read that finishes after an action failed
    /// never clears the action's failure.
    @Published private(set) var loadError: String?
    /// Why the last action (an add, switch, delete, import and the like) failed.
    @Published private(set) var errorMessage: String?
    @Published private(set) var load = SettingsSectionLoad()

    let drafts: SettingsDrafts
    private let access: SnippetSettingsAccess
    private let onChanged: @MainActor () -> Void

    init(access: SnippetSettingsAccess, drafts: SettingsDrafts, onChanged: @escaping @MainActor () -> Void) {
        self.access = access
        self.drafts = drafts
        self.onChanged = onChanged
    }

    /// Whether the snippet in the drafts is being added, by this model or by one built earlier for the same drafts.
    var isAdding: Bool {
        drafts.isAdding(.snippet)
    }

    var canAdd: Bool {
        !isAdding && !Self.isBlank(drafts.snippetPhrase) && !Self.isBlank(drafts.snippetTemplate)
    }

    func reload() async {
        let ticket = load.begin()
        do {
            let loaded = try await access.loadSnippets()
            guard load.publish(ticket) else {
                return
            }
            snippets = loaded
            loadError = nil
        } catch {
            guard load.fail(ticket) else {
                return
            }
            loadError = error.localizedDescription
        }
    }

    /// Adds the snippet typed into the drafts, which are emptied only if they still hold what was added.
    func addFromDrafts() async {
        let phrase = drafts.snippetPhrase
        let template = drafts.snippetTemplate
        guard canAdd, drafts.beginAdding(.snippet) else {
            return
        }
        defer { drafts.finishAdding(.snippet) }

        let added = await write {
            try await self.access.addSnippet(Snippet(phrase: phrase, template: template))
        }
        if added, drafts.snippetPhrase == phrase, drafts.snippetTemplate == template {
            drafts.snippetPhrase = ""
            drafts.snippetTemplate = ""
        }
    }

    func setEnabled(_ snippet: Snippet, enabled: Bool) async {
        await write {
            try await self.access.setEnabled(snippet.id, enabled)
        }
    }

    func delete(_ snippet: Snippet) async {
        await write {
            try await self.access.deleteSnippet(snippet.id)
        }
    }

    /// Runs one write. On success the app's rules are refreshed and the rows read again. Returns whether it succeeded.
    @discardableResult
    private func write(_ operation: @MainActor () async throws -> Void) async -> Bool {
        errorMessage = nil
        do {
            try await operation()
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
        onChanged()
        await reload()
        return true
    }

    private static func isBlank(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespaces).isEmpty
    }
}
