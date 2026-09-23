import Foundation

/// One Diagnostics window as the tab shows it.
struct DiagnosticsWindowSummary: Sendable {
    /// Nil when nothing in the window qualifies.
    let stats: DictationStats.Snapshot?
    /// True when the window holds more dictations than one read covers, so the numbers describe only the newest.
    let capped: Bool
}

/// How the Diagnostics tab reaches storage. `live(_:)` reads the window's newest dictations with the store's
/// asynchronous form and computes the stats there too, off the main actor. Tests pass their own.
struct DiagnosticsSettingsAccess: Sendable {
    var loadWindow: @Sendable (_ since: Date) async throws -> DiagnosticsWindowSummary
}

extension DiagnosticsSettingsAccess {
    /// Newest dictations one window covers, Windows' `GetRecent(1000)`.
    static let readLimit = PersistenceStore.defaultHistoryReadLimit

    static func live(_ store: PersistenceStore) -> DiagnosticsSettingsAccess {
        DiagnosticsSettingsAccess(loadWindow: { since in
            let limit = DiagnosticsSettingsAccess.readLimit
            // One row past the cap: that extra row is what shows the window holds more.
            let records = try await store.loadDictationHistory(since: since, limit: limit + 1)
            return DiagnosticsWindowSummary(
                stats: DictationStats.compute(entries: Array(records.suffix(limit)), since: since),
                capped: records.count > limit)
        })
    }
}

/// The Diagnostics tab: latency and real-time-factor figures for the chosen window. The read is asynchronous, and only
/// the newest read may replace the figures or report an error, so a slow read of the previous window can never
/// overwrite the one now chosen.
@MainActor
final class DiagnosticsSettingsModel: ObservableObject {
    @Published private(set) var stats: DictationStats.Snapshot?
    @Published private(set) var capped = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var load = SettingsSectionLoad()
    /// How many days the window reaches back. The tab reads the window again when this changes.
    @Published var windowDays: Double = 7

    private let access: DiagnosticsSettingsAccess
    private let now: @MainActor () -> Date

    init(access: DiagnosticsSettingsAccess, now: @escaping @MainActor () -> Date = { Date() }) {
        self.access = access
        self.now = now
    }

    /// Shown under the figures when the window held more dictations than one read covers.
    var coverageNote: String? {
        guard capped else {
            return nil
        }
        return "Covers the newest \(DiagnosticsSettingsAccess.readLimit.formatted()) dictations in this window."
    }

    func reload() async {
        let ticket = load.begin()
        let since = now().addingTimeInterval(-windowDays * 86_400)
        do {
            let window = try await access.loadWindow(since)
            guard load.publish(ticket) else {
                return
            }
            stats = window.stats
            capped = window.capped
            errorMessage = nil
        } catch {
            guard load.fail(ticket) else {
                return
            }
            errorMessage = error.localizedDescription
        }
    }
}
