import Foundation
import OSLog

/// How long dictation text is kept: a number of days, or forever. Mirrors Windows
/// `AppSettings.HistoryRetentionDays`, which stores forever as 0.
enum HistoryRetention: Equatable, Sendable {
    case keepForever
    case days(Int)

    /// What a history this build creates starts with, as on Windows.
    static let defaultDays = 90

    /// Windows' ceiling (`StorageMaintenance.MaxRetentionDays`), about a century.
    static let maximumDays = 36_500

    /// Choices a Settings picker can offer, shortest first.
    static let presets: [HistoryRetention] = [.days(7), .days(30), .days(90), .days(365), .keepForever]

    /// Settings hint under the retention picker.
    static let hint =
        "Dictation text older than this is removed automatically while Scribe runs. Choose Forever to keep it all."

    /// Shown while no limit is on record, which is how every history that predates the setting starts.
    static let notChosenHint = "No limit has been chosen, so Scribe keeps all of your dictation history."

    /// 0 or less keeps forever; anything past `maximumDays` is clamped to it.
    init(days: Int) {
        self = days <= 0 ? .keepForever : .days(min(days, Self.maximumDays))
    }

    /// The stored form: 0 keeps forever.
    var storedDays: Int {
        switch self {
        case .keepForever:
            return 0
        case .days(let days):
            return days
        }
    }
}

/// The retention choice as read from storage. Only a choice actually on record can delete anything:
/// a missing or unreadable value keeps every entry, so losing the setting can never cost the user
/// their history. Windows reaches the same rule through `StorageMaintenance.KeepAllTextThisSession`.
enum HistoryRetentionSetting: Equatable, Sendable {
    case chosen(HistoryRetention)
    /// No value is stored: a history that predates the setting.
    case notChosen
    /// A value is stored but is not a whole number of days.
    case unreadable

    init(storedValue: String?) {
        guard let storedValue,
              let days = Int(storedValue.trimmingCharacters(in: .whitespaces)),
              days >= 0
        else {
            self = .unreadable
            return
        }
        self = .chosen(HistoryRetention(days: days))
    }

    /// What applies now: anything but an explicit choice keeps everything.
    var effective: HistoryRetention {
        if case .chosen(let retention) = self {
            return retention
        }
        return .keepForever
    }

    /// Entries that started before this moment are past the limit; nil when nothing may be deleted.
    func cutoff(before now: Date) -> Date? {
        guard case .chosen(.days(let days)) = self else {
            return nil
        }
        return now.addingTimeInterval(-Double(days) * 86_400)
    }
}

/// What one maintenance pass did, by shape only.
struct StorageMaintenanceReport: Equatable, Sendable {
    enum KeepReason: String, Equatable, Sendable {
        case keepForever
        case notChosen
        case unreadable
    }

    enum Retention: Equatable, Sendable {
        /// Not attempted: the pass stopped first.
        case notRun
        /// Entries older than `days` were removed.
        case applied(days: Int, removed: Int)
        /// Nothing may be deleted.
        case kept(KeepReason)
        /// A deletion failed; `removed` entries went before it.
        case failed(removed: Int, code: Int32)
    }

    enum ReclaimMethod: String, Equatable, Sendable {
        case incremental
        case convertedToIncremental
    }

    enum Reclaim: Equatable, Sendable {
        case notRun
        case notNeeded
        /// Owed, but the app has used the database too recently; a later pass retries.
        case deferredForActivity
        case completed(ReclaimMethod, freedPages: Int64)
        /// Stopped for a foreground caller, Clear history or shutdown; SQLite rolled it back.
        case yielded
        /// Stopped at its time budget; the next daily pass tries again.
        case outOfTime
        case failed(code: Int32)
    }

    var retention: Retention = .notRun
    var reclaim: Reclaim = .notRun
    var stopped = false
}

/// Keeps `scribe.db` bounded: deletes dictation text past the user's retention choice, and gives the
/// freed pages back to the disk while the app is idle. The macOS side of Windows `StorageMaintenance`,
/// for text only (macOS stores no audio).
///
/// One pass at a time, on a private serial queue, never on the caller's thread: 30 seconds after
/// `start()`, then daily on the wall clock (so a Mac that slept through the deadline runs it on wake),
/// when the retention choice changes, and after Clear history.
///
/// Retention deletes in short batches. Only a choice on record deletes anything (see
/// `HistoryRetentionSetting`), so a missing or unreadable setting keeps every entry.
///
/// Reclamation is the heavy part and gets out of the way. It runs only once the free space is worth
/// it and nothing in the foreground has used the database for `quietPeriod` (right after Clear history
/// it runs at once, because the user asked for the text to be gone and little live data is left). Each
/// statement stops as soon as a foreground caller waits for the connection, at shutdown or at its time
/// budget, and SQLite rolls it back. A database created before this build has auto_vacuum NONE, which
/// never shrinks, so the first reclaim converts it with one VACUUM; later ones use bounded
/// `incremental_vacuum` steps. A WAL checkpoint then truncates the WAL, so deleted text leaves that
/// file too.
///
/// Logs counts and outcome names only.
final class StorageMaintenance: @unchecked Sendable {
    struct Options: Sendable {
        /// First pass after `start()`: soon enough that a short session still applies retention, late
        /// enough to stay out of launch.
        var initialDelay: TimeInterval = 30
        var interval: TimeInterval = 24 * 60 * 60
        /// Reclaiming waits until nothing in the foreground has used the database for this long.
        var quietPeriod: TimeInterval = 60
        /// Free space worth reclaiming after an ordinary sweep.
        var reclaimThresholdBytes: Int64 = 1 << 20
        /// Longest one reclaim attempt may run before it stops itself.
        var reclaimTimeBudget: TimeInterval = 5
        /// Pages one incremental_vacuum step returns: 4 MiB of 4 KiB pages.
        var incrementalStepPages = 1_024
        /// Rows one retention transaction deletes.
        var retentionBatchSize = 500
        /// How long Clear history waits for writes accepted before it.
        var writerBarrierTimeout: TimeInterval = 10
        /// Ceiling on the backoff between deferred reclaim attempts.
        var maximumRetryDelay: TimeInterval = 60 * 60
    }

    private let store: PersistenceStore
    private let historyWriter: HistoryWriter?
    private let options: Options
    private let now: @Sendable () -> Date
    private let uptime: @Sendable () -> TimeInterval
    private let onPassFinished: (@Sendable (StorageMaintenanceReport) -> Void)?
    private let queue = DispatchQueue(label: "com.scribe.macos.storage-maintenance", qos: .utility)
    private let logger = Logger(subsystem: "com.scribe.macos", category: "StorageMaintenance")

    // `@unchecked Sendable` because the compiler cannot see the discipline: the fields below are only
    // touched while `stateLock` is held...
    private let stateLock = NSLock()
    private var started = false
    private var stopped = false
    private var yieldRequested = false
    private var passPending = false
    private var followUpPending = false
    private var reclaimEverythingRequested = false
    private var timer: DispatchSourceTimer?

    // ...and these only on `queue`, by the one pass or clear running there.
    private var observedActivity: UInt64
    private var quietSince: TimeInterval
    private var reclaimRetries = 0
    private var conversionBlocked = false

    /// - Parameters:
    ///   - now: wall clock for retention cutoffs, because that is what the stored timestamps are.
    ///   - uptime: monotonic clock for the quiet period and time budgets.
    ///   - onPassFinished: called on the maintenance queue after every pass, with its report.
    init(
        store: PersistenceStore,
        historyWriter: HistoryWriter? = nil,
        options: Options = Options(),
        now: @escaping @Sendable () -> Date = { Date() },
        uptime: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
        onPassFinished: (@Sendable (StorageMaintenanceReport) -> Void)? = nil
    ) {
        self.store = store
        self.historyWriter = historyWriter
        self.options = options
        self.now = now
        self.uptime = uptime
        self.onPassFinished = onPassFinished
        observedActivity = store.foregroundActivityCount
        quietSince = uptime()
    }

    deinit {
        timer?.cancel()
    }

    /// Starts the schedule. Calling it again, or after `stop`, does nothing.
    func start() {
        stateLock.lock()
        defer { stateLock.unlock() }
        guard !started, !stopped else {
            return
        }

        started = true
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(wallDeadline: .now() + options.initialDelay, repeating: options.interval, leeway: .seconds(60))
        timer.setEventHandler { [weak self] in
            _ = self?.performPass()
        }
        timer.resume()
        self.timer = timer
    }

    /// Asks for a pass soon. Coalesced: requests made before the pass starts become one pass.
    /// - Parameter reclaimEverything: return every free page, not only a worthwhile amount, and skip
    ///   the quiet period.
    func requestPass(reclaimEverything: Bool = false) {
        stateLock.lock()
        guard started, !stopped else {
            stateLock.unlock()
            return
        }
        reclaimEverythingRequested = reclaimEverythingRequested || reclaimEverything
        let enqueue = !passPending
        passPending = true
        stateLock.unlock()

        if enqueue {
            queue.async { [weak self] in
                _ = self?.performPass()
            }
        }
    }

    /// Stores a new retention choice and applies it soon, so a shorter limit takes effect without
    /// waiting a day.
    func setRetention(_ retention: HistoryRetention) throws {
        try store.setHistoryRetention(retention)
        requestPass()
    }

    /// Runs one pass on the maintenance queue and returns its report. Scheduled passes use the same
    /// queue, so this waits for any pass already running.
    @discardableResult
    func runPass() -> StorageMaintenanceReport {
        queue.sync {
            performPass()
        }
    }

    /// Deletes all dictation history and returns how many entries went. Waits, bounded, for history
    /// writes accepted before the call, so a dictation that finished just before the click is removed
    /// too, then gives the freed pages back at once. Runs on the maintenance queue, never the caller's
    /// thread; a running pass is asked to stop so the clear does not wait behind it.
    func clearHistory() async throws -> Int {
        setYieldRequested(true)
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Int, Error>) in
            queue.async { [self] in
                continuation.resume(with: Result { try performClear() })
            }
        }
    }

    /// Stops maintenance for shutdown: no pass starts afterwards, and a running reclaim statement is
    /// interrupted (SQLite rolls it back). Waits at most `timeout` for a pass in flight. Returns true
    /// once nothing is running. Safe to call more than once.
    @discardableResult
    func stop(timeout: TimeInterval) -> Bool {
        stateLock.lock()
        stopped = true
        let timer = self.timer
        self.timer = nil
        stateLock.unlock()
        timer?.cancel()

        let idle = DispatchSemaphore(value: 0)
        queue.async {
            idle.signal()
        }
        return idle.wait(timeout: .now() + timeout) == .success
    }

    // MARK: - Passes (on `queue`)

    @discardableResult
    private func performPass() -> StorageMaintenanceReport {
        stateLock.lock()
        passPending = false
        let isStopped = stopped
        let everything = reclaimEverythingRequested
        reclaimEverythingRequested = false
        stateLock.unlock()

        var report = StorageMaintenanceReport()
        guard !isStopped else {
            report.stopped = true
            return report
        }

        report.retention = applyRetention()
        if !shouldYield {
            report.reclaim = reclaim(everything: everything)
        }
        report.stopped = isStoppedNow
        log(report)
        onPassFinished?(report)
        return report
    }

    private func performClear() throws -> Int {
        setYieldRequested(false)
        if let historyWriter, !historyWriter.waitForAcceptedWrites(timeout: options.writerBarrierTimeout) {
            logger.warning(
                "Clear history went ahead while an earlier dictation was still being saved; that entry may remain.")
        }

        let removed = try store.clearHistory()
        let reclaimOutcome = reclaim(everything: true)
        logger.info(
            "Cleared \(removed) history entries; space reclamation \(Self.describe(reclaimOutcome), privacy: .public).")
        return removed
    }

    private func applyRetention() -> StorageMaintenanceReport.Retention {
        let setting: HistoryRetentionSetting
        do {
            setting = try store.historyRetention(purpose: .maintenance)
        } catch {
            // A setting that cannot be read is never taken for a choice.
            return .kept(.unreadable)
        }

        let retention: HistoryRetention
        switch setting {
        case .notChosen:
            return .kept(.notChosen)
        case .unreadable:
            return .kept(.unreadable)
        case .chosen(let chosen):
            retention = chosen
        }

        guard let cutoff = setting.cutoff(before: now()) else {
            return .kept(.keepForever)
        }

        var removed = 0
        while !shouldYield {
            do {
                let batch = try store.deleteHistoryBatch(startedBefore: cutoff, limit: options.retentionBatchSize)
                removed += batch
                if batch < options.retentionBatchSize {
                    break
                }
            } catch {
                return .failed(removed: removed, code: Self.code(of: error))
            }
        }
        return .applied(days: retention.storedDays, removed: removed)
    }

    private func reclaim(everything: Bool) -> StorageMaintenanceReport.Reclaim {
        let stats: PersistencePageStats
        do {
            stats = try store.pageStats()
        } catch {
            return .failed(code: Self.code(of: error))
        }

        let owed = everything ? stats.freePages > 0 : stats.freeBytes >= options.reclaimThresholdBytes
        // FULL auto_vacuum already truncates at every commit, so there is nothing to do for it.
        guard owed, stats.autoVacuum == 0 || stats.autoVacuum == 2 else {
            reclaimRetries = 0
            return .notNeeded
        }
        if stats.autoVacuum == 0, conversionBlocked {
            return .notNeeded
        }

        if !everything, quietFor() < options.quietPeriod {
            scheduleRetry()
            return .deferredForActivity
        }

        let deadline = uptime() + options.reclaimTimeBudget
        let shouldStop: @Sendable () -> Bool = { [weak self] in
            guard let self else {
                return true
            }
            return self.shouldYield || self.uptime() >= deadline
        }

        let outcome: StorageMaintenanceReport.Reclaim
        do {
            if stats.autoVacuum == 2 {
                outcome = try reclaimIncrementally(freePages: stats.freePages, deadline: deadline, shouldStop: shouldStop)
            } else {
                outcome = try convertAndVacuum(freePages: stats.freePages, deadline: deadline, shouldStop: shouldStop)
            }
        } catch {
            return .failed(code: Self.code(of: error))
        }

        switch outcome {
        case .completed:
            reclaimRetries = 0
            checkpoint()
        case .yielded:
            scheduleRetry()
        default:
            break
        }
        return outcome
    }

    private func reclaimIncrementally(
        freePages: Int64,
        deadline: TimeInterval,
        shouldStop: @escaping @Sendable () -> Bool
    ) throws -> StorageMaintenanceReport.Reclaim {
        var remaining = freePages
        while remaining > 0 {
            let step = "PRAGMA incremental_vacuum(\(max(1, options.incrementalStepPages)));"
            switch try store.runYieldingMaintenance(step, shouldStop: shouldStop) {
            case .completed:
                break
            case .yieldedToForeground:
                return .yielded
            case .stopped:
                return uptime() >= deadline ? .outOfTime : .yielded
            }

            let after = try store.pageStats().freePages
            guard after < remaining else {
                // No progress: stop rather than spin.
                break
            }
            remaining = after
        }
        return .completed(.incremental, freedPages: freePages - remaining)
    }

    private func convertAndVacuum(
        freePages: Int64,
        deadline: TimeInterval,
        shouldStop: @escaping @Sendable () -> Bool
    ) throws -> StorageMaintenanceReport.Reclaim {
        let outcome: YieldingStatementOutcome
        do {
            // The pragma only takes effect through the VACUUM that follows, which consumes it, so it is
            // set again before every attempt.
            outcome = try store.runYieldingMaintenance(
                "VACUUM;", prelude: "PRAGMA auto_vacuum = INCREMENTAL;", shouldStop: shouldStop)
        } catch {
            // Out of space, I/O or the like: a full rewrite is not worth retrying every day this session.
            conversionBlocked = true
            throw error
        }

        switch outcome {
        case .completed:
            break
        case .yieldedToForeground:
            return .yielded
        case .stopped:
            return uptime() >= deadline ? .outOfTime : .yielded
        }

        let after = try store.pageStats()
        if after.autoVacuum != 2 {
            conversionBlocked = true
            logger.warning(
                "Storage maintenance compacted the database, but it did not switch to incremental auto-vacuum.")
        }
        return .completed(.convertedToIncremental, freedPages: max(0, freePages - after.freePages))
    }

    private func checkpoint() {
        do {
            if try !store.checkpointWal() {
                logger.info("The WAL checkpoint after reclaiming space was busy; a later pass retries it.")
            }
        } catch {
            logger.error("The WAL checkpoint after reclaiming space failed (SQLite \(Self.code(of: error))).")
        }
    }

    /// How long nothing in the foreground has used the database. A change restarts the window from when
    /// maintenance noticed it, which only ever makes the window longer than the truth, never shorter.
    private func quietFor() -> TimeInterval {
        let count = store.foregroundActivityCount
        let current = uptime()
        if count != observedActivity {
            observedActivity = count
            quietSince = current
        }
        return current - quietSince
    }

    /// Bounded retries with no storm: each consecutive deferral or yield doubles the wait (one quiet
    /// period, then two, four ... never more than `maximumRetryDelay`). Only a running schedule retries;
    /// passes run directly (tests, Clear history) never leave work behind.
    private func scheduleRetry() {
        let delay = min(options.quietPeriod * Double(1 << min(reclaimRetries, 16)), options.maximumRetryDelay)
        reclaimRetries += 1

        stateLock.lock()
        guard started, !stopped, !followUpPending else {
            stateLock.unlock()
            return
        }
        followUpPending = true
        stateLock.unlock()

        queue.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self else {
                return
            }
            self.stateLock.lock()
            self.followUpPending = false
            self.stateLock.unlock()
            self.performPass()
        }
    }

    // MARK: - State

    private var shouldYield: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return stopped || yieldRequested
    }

    private var isStoppedNow: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return stopped
    }

    private func setYieldRequested(_ value: Bool) {
        stateLock.lock()
        yieldRequested = value
        stateLock.unlock()
    }

    // MARK: - Logging (shapes only)

    private func log(_ report: StorageMaintenanceReport) {
        let retentionText = Self.describe(report.retention)
        let reclaimText = Self.describe(report.reclaim)
        let eventful: Bool
        switch (report.retention, report.reclaim) {
        case (.applied(_, let removed), _) where removed > 0:
            eventful = true
        case (.failed, _), (_, .completed), (_, .failed), (_, .outOfTime):
            eventful = true
        default:
            eventful = false
        }

        if eventful {
            logger.info(
                "Storage maintenance: retention \(retentionText, privacy: .public); reclaim \(reclaimText, privacy: .public).")
        } else {
            logger.debug(
                "Storage maintenance: retention \(retentionText, privacy: .public); reclaim \(reclaimText, privacy: .public).")
        }
    }

    private static func describe(_ retention: StorageMaintenanceReport.Retention) -> String {
        switch retention {
        case .notRun:
            return "not run"
        case .applied(let days, let removed):
            return "\(days) days, removed \(removed)"
        case .kept(let reason):
            return "kept everything (\(reason.rawValue))"
        case .failed(let removed, let code):
            return "failed with SQLite \(code) after removing \(removed)"
        }
    }

    private static func describe(_ reclaim: StorageMaintenanceReport.Reclaim) -> String {
        switch reclaim {
        case .notRun:
            return "not run"
        case .notNeeded:
            return "not needed"
        case .deferredForActivity:
            return "deferred for activity"
        case .completed(let method, let freedPages):
            return "\(method.rawValue), freed \(freedPages) pages"
        case .yielded:
            return "yielded"
        case .outOfTime:
            return "stopped at its time budget"
        case .failed(let code):
            return "failed with SQLite \(code)"
        }
    }

    private static func code(of error: Error) -> Int32 {
        (error as? PersistenceError)?.sqliteCode ?? -1
    }
}
