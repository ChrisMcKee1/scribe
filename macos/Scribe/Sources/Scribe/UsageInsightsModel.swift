import Foundation

/// How the Usage Insights tab reaches storage. `live(_:)` reads the period with the store's asynchronous form and
/// computes the report there too, off the main actor. Tests pass their own.
struct UsageInsightsAccess: Sendable {
    /// Reads the period that starts at `since` and computes its report.
    var loadReport: @Sendable (_ since: Date, _ now: Date) async throws -> UsageAnalyzer.Report
    /// Adds a recurring term as a rule unless one for it is stored already. Returns whether it was added.
    var addTerm: @Sendable (_ text: String) async throws -> Bool
}

extension UsageInsightsAccess {
    static func live(_ store: PersistenceStore) -> UsageInsightsAccess {
        UsageInsightsAccess(
            loadReport: { since, now in
                // One row past the cap: that extra row is what shows the period holds more.
                let period = try await store.loadUsagePeriod(since: since, limit: UsageAnalyzer.historyLimit + 1)
                return UsageAnalyzer.report(
                    records: period.records, knownTerms: period.knownTerms, sinceUtc: since, nowUtc: now)
            },
            addTerm: { text in
                let added = try await store.addDictionaryEntriesIfAbsent(
                    [DictionaryEntry(pattern: text, replacement: text, wholeWord: true, enabled: true)])
                return !added.isEmpty
            })
    }
}

/// The local part of the Usage Insights tab: totals, trend, top apps and recurring terms for the chosen period. The
/// AI summary is `UsageSummaryModel`'s. The read is asynchronous, and only the newest read may replace the report or
/// report an error, so a slow read of the previous period can never overwrite the one now chosen.
@MainActor
final class UsageInsightsModel: ObservableObject {
    @Published private(set) var snapshot: UsageAnalyzer.Snapshot?
    @Published private(set) var periodCapped = false
    /// Why the newest read failed. Kept apart from `errorMessage`, so a read that finishes after an add failed
    /// never clears the add's failure.
    @Published private(set) var loadError: String?
    /// Why the last "Add to Dictionary" failed.
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    @Published private(set) var load = SettingsSectionLoad()
    /// How many days the period reaches back. The tab reads the period again when this changes.
    @Published var windowDays: Double = 30

    private let access: UsageInsightsAccess
    private let onChanged: @MainActor () -> Void
    private let now: @MainActor () -> Date

    init(
        access: UsageInsightsAccess,
        onChanged: @escaping @MainActor () -> Void,
        now: @escaping @MainActor () -> Date = { Date() }
    ) {
        self.access = access
        self.onChanged = onChanged
        self.now = now
    }

    /// Shown above the figures when the period held more dictations than one report covers.
    var coverageNote: String? {
        guard periodCapped else {
            return nil
        }
        return "Covers the newest \(UsageAnalyzer.historyLimit.formatted()) dictations in this period."
    }

    func reload() async {
        let ticket = load.begin()
        let now = self.now()
        let since = now.addingTimeInterval(-windowDays * 86_400)
        do {
            let report = try await access.loadReport(since, now)
            guard load.publish(ticket) else {
                return
            }
            snapshot = report.snapshot
            periodCapped = report.periodCapped
            loadError = nil
        } catch {
            guard load.fail(ticket) else {
                return
            }
            loadError = error.localizedDescription
        }
    }

    /// Adds a recurring term as a rule, unless the dictionary gained one for it since the period was read.
    func addTermToDictionary(_ term: UsageAnalyzer.TermUsage) async {
        errorMessage = nil
        statusMessage = nil
        do {
            if try await access.addTerm(term.text) {
                onChanged()
            } else {
                statusMessage = "\u{201C}\(term.text)\u{201D} is already in your dictionary."
            }
        } catch {
            errorMessage = error.localizedDescription
            return
        }
        await reload()
    }
}
