import Foundation

struct StoredDictation: Identifiable, Equatable, Sendable {
    let id: Int64
    let record: DictationHistoryRecord
}

struct HistoryListAccess: Sendable {
    var read: @Sendable (String) async throws -> [StoredDictation]
    var delete: @Sendable (Int64) async throws -> Void

    static func live(_ store: PersistenceStore) -> Self {
        Self(
            read: { try await store.loadHistoryRows(query: $0) },
            delete: { try await store.removeHistoryRow(id: $0) })
    }
}

@MainActor
final class HistoryListModel: ObservableObject {
    @Published var query = "" {
        didSet {
            if query != oldValue { refresh(debounce: true) }
        }
    }
    @Published private(set) var rows: [StoredDictation] = []
    @Published private(set) var isLoading = false
    @Published private(set) var isDeleting = false
    @Published private(set) var errorMessage: String?
    private(set) var inFlight: Task<Void, Never>?
    private var revision: UInt64 = 0
    private var active = false
    private let access: HistoryListAccess
    private let delay: @Sendable () async throws -> Void

    init(
        access: HistoryListAccess,
        delay: @escaping @Sendable () async throws -> Void = {
            try await Task.sleep(for: .milliseconds(250))
        }
    ) {
        self.access = access
        self.delay = delay
    }

    func appear() {
        active = true
        refresh()
    }

    func stop() {
        active = false
        revision &+= 1
        inFlight?.cancel()
        inFlight = nil
        isLoading = false
    }

    /// Invalidate the previous read immediately, even while the debounce or a database operation cannot cancel.
    func refresh(debounce: Bool = false) {
        revision &+= 1
        inFlight?.cancel()
        guard active else { return }
        let ticket = revision
        let capturedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        rows = []
        isLoading = true
        errorMessage = nil
        let access = self.access
        let delay = self.delay
        inFlight = Task { [weak self] in
            do {
                if debounce && !capturedQuery.isEmpty { try await delay() }
                try Task.checkCancellation()
                let result = try await access.read(capturedQuery)
                try Task.checkCancellation()
                guard let self, self.active, self.revision == ticket else { return }
                self.rows = result
                self.isLoading = false
            } catch {
                guard let self, self.active, self.revision == ticket, !Task.isCancelled else { return }
                self.isLoading = false
                self.errorMessage = "Couldn't read your dictation history. Try again."
            }
        }
    }

    func delete(_ row: StoredDictation, onDeleted: @MainActor () -> Void) async {
        guard !isDeleting else { return }
        isDeleting = true
        defer { isDeleting = false }
        do {
            try await access.delete(row.id)
            onDeleted()
            refresh()
        } catch {
            errorMessage = "Couldn't delete that dictation. Try again."
        }
    }
}
