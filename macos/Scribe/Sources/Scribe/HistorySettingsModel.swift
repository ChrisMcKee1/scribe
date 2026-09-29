import Foundation

/// The retention choice and how many dictations are stored, as the History tab shows them.
struct HistoryStorageState: Equatable, Sendable {
    let retention: HistoryRetentionSetting
    let storedCount: Int
}

/// How the History tab reaches storage. `live(store:maintenance:)` reads through the store's asynchronous forms and
/// writes through `StorageMaintenance`, so a new limit is applied at once and Clear runs in line with the history
/// writer. Tests pass their own.
struct HistorySettingsAccess: Sendable {
    var load: @Sendable () async throws -> HistoryStorageState
    var setRetention: @Sendable (HistoryRetention) async throws -> Void
    var clearHistory: @Sendable () async throws -> Int
}

extension HistorySettingsAccess {
    static func live(store: PersistenceStore, maintenance: StorageMaintenance) -> HistorySettingsAccess {
        HistorySettingsAccess(
            load: {
                let retention = try await store.loadHistoryRetention()
                let storedCount = try await store.loadHistoryCount()
                return HistoryStorageState(retention: retention, storedCount: storedCount)
            },
            setRetention: { retention in try await maintenance.setRetention(retention) },
            clearHistory: { try await maintenance.clearHistory() })
    }
}

/// The History tab: how long dictation text is kept, and Clear history.
///
/// The picker offers the presets and Forever. A history with no limit on record (every history that predates the
/// setting) or an unreadable one keeps everything, so the picker shows Forever, which is what applies, with a hint
/// that says no limit was chosen. A database this build created starts at 90 days. Saving goes through
/// `StorageMaintenance.setRetention`, which applies a shorter limit at once.
///
/// Clear asks for confirmation first and is unavailable while nothing is stored, before the count has loaded, or
/// while a Clear runs. A successful Clear also empties the tray's recent dictations (`onCleared`), which goes beyond
/// Windows, whose tray list survives a Clear: text the user asked to delete should not stay one click away. Failures
/// are shown in words that never carry a path, SQL or dictated text.
@MainActor
final class HistorySettingsModel: ObservableObject {
    @Published private(set) var state: HistoryStorageState?
    /// A limit being saved, shown by the picker until the save settles.
    @Published private(set) var pendingRetention: HistoryRetention?
    @Published private(set) var isClearing = false
    /// Whether the Clear confirmation is showing. The view binds its dialog to this.
    @Published var isConfirmingClear = false
    @Published private(set) var statusMessage: String?
    @Published private(set) var errorMessage: String?
    /// Why the stored state could not be read, kept apart from an action's own message.
    @Published private(set) var loadError: String?
    @Published private(set) var load = SettingsSectionLoad()
    /// The save a picker change started, for tests to await.
    private(set) var inFlight: Task<Void, Never>?

    private let access: HistorySettingsAccess
    private let onCleared: @MainActor () -> Void

    init(access: HistorySettingsAccess, onCleared: @escaping @MainActor () -> Void) {
        self.access = access
        self.onCleared = onCleared
    }

    /// What the picker shows: a limit being saved, the stored choice, or Forever while none is on record.
    var selection: HistoryRetention {
        pendingRetention ?? state?.retention.effective ?? .keepForever
    }

    /// The presets and Forever, plus a stored limit that is not a preset, so the picker can always show what applies.
    var options: [HistoryRetention] {
        let current = selection
        guard !HistoryRetention.presets.contains(current) else {
            return HistoryRetention.presets
        }
        let limits = (HistoryRetention.presets.filter { $0 != .keepForever } + [current])
            .sorted { $0.storedDays < $1.storedDays }
        return limits + [.keepForever]
    }

    var hint: String {
        switch state?.retention {
        case .notChosen?:
            return HistoryRetention.notChosenHint
        case .unreadable?:
            return HistoryRetention.unreadableHint
        default:
            return HistoryRetention.hint
        }
    }

    var storedCountText: String? {
        guard let count = state?.storedCount else {
            return nil
        }
        return count == 1 ? "1 dictation is stored." : "\(count.formatted()) dictations are stored."
    }

    var canChooseRetention: Bool {
        load.isLoaded && pendingRetention == nil && !isClearing
    }

    var canClear: Bool {
        load.isLoaded && (state?.storedCount ?? 0) > 0 && !isClearing
    }

    func reload() async {
        let ticket = load.begin()
        do {
            let loaded = try await access.load()
            guard load.publish(ticket) else {
                return
            }
            state = loaded
            loadError = nil
        } catch {
            guard load.fail(ticket) else {
                return
            }
            loadError = "Couldn't read your history settings. \(Self.shapeOnly(error))"
        }
    }

    /// Starts saving `retention` and returns the save, or nil when there is nothing to do. The picker shows the new
    /// choice at once, before the save settles.
    @discardableResult
    func choose(_ retention: HistoryRetention) -> Task<Void, Never>? {
        guard canChooseRetention, state?.retention != .chosen(retention) else {
            return nil
        }
        pendingRetention = retention
        errorMessage = nil
        statusMessage = nil
        let task = Task { [weak self] in
            guard let self else {
                return
            }
            await self.save(retention)
        }
        inFlight = task
        return task
    }

    /// Opens the Clear confirmation, if there is anything to clear.
    func requestClear() {
        guard canClear else {
            return
        }
        isConfirmingClear = true
    }

    /// Clears every stored dictation after the user confirmed. On success the tray's recent dictations are emptied too.
    func confirmClear() async {
        isConfirmingClear = false
        guard canClear else {
            return
        }
        isClearing = true
        errorMessage = nil
        statusMessage = nil

        do {
            let removed = try await access.clearHistory()
            onCleared()
            statusMessage = removed == 1 ? "Cleared 1 dictation." : "Cleared \(removed.formatted()) dictations."
        } catch {
            errorMessage = Self.clearFailureMessage(error)
        }
        await reload()
        isClearing = false
    }

    /// Clear's failure, in words that never carry a path, SQL or dictated text.
    static func clearFailureMessage(_ error: any Error) -> String {
        if let writerError = error as? HistoryWriterError, let description = writerError.errorDescription {
            return description
        }
        return "History was not cleared. \(shapeOnly(error))"
    }

    private func save(_ retention: HistoryRetention) async {
        do {
            try await access.setRetention(retention)
        } catch {
            errorMessage = "Couldn't save the history limit. \(Self.shapeOnly(error))"
        }
        await reload()
        pendingRetention = nil
        inFlight = nil
    }

    /// An error's own description only for the types known to describe shapes (the store's and the history writer's
    /// errors); anything else gets a fixed sentence.
    private static func shapeOnly(_ error: any Error) -> String {
        if let storeError = error as? PersistenceError, let description = storeError.errorDescription {
            return description
        }
        if let writerError = error as? HistoryWriterError, let description = writerError.errorDescription {
            return description
        }
        return "Try again in a moment."
    }
}
