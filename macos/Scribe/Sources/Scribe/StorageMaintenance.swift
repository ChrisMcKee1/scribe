import Foundation
import OSLog

/// How long dictation text is kept: a number of days, or forever. Mirrors Windows
/// `AppSettings.HistoryRetentionDays`, which stores forever as 0.
enum HistoryRetention: Hashable, Sendable {
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

    /// Shown while the stored limit cannot be read, which also keeps everything.
    static let unreadableHint = """
        The saved limit could not be read, so Scribe keeps all of your dictation history. Choose a limit to \
        replace it.
        """

    /// How a Settings picker names this choice.
    var label: String {
        switch self {
        case .keepForever:
            return "Forever"
        case .days(1):
            return "1 day"
        case .days(365):
            return "1 year"
        case .days(let days):
            return "\(days) days"
        }
    }

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

/// Counts the foreground work storage housekeeping must stay out of the way of: today, each dictation
/// from capture start until its pipeline finishes. This, not a quiet database, is what "idle" means
/// for maintenance: a dictation that is capturing or transcribing makes no database traffic at all.
///
/// Thread-safe with a lock of its own and nothing else, so SQLite's progress handler can ask it from
/// inside the storage queue without touching the main actor or re-entering the store.
///
/// `@unchecked Sendable` because the compiler cannot see the locking: `count` and `changes` are only
/// read or written while `lock` is held.
final class ForegroundActivity: @unchecked Sendable {
    /// One piece of foreground work. `end()` is idempotent, and a lease released without calling it
    /// ends itself, so a forgotten path can hold housekeeping off for a while but not forever.
    ///
    /// `@unchecked Sendable` because the compiler cannot see the locking: `ended` is only read or
    /// written while `lock` is held.
    final class Lease: @unchecked Sendable {
        private let activity: ForegroundActivity
        private let lock = NSLock()
        private var ended = false

        fileprivate init(activity: ForegroundActivity) {
            self.activity = activity
        }

        deinit {
            end()
        }

        func end() {
            lock.lock()
            let wasActive = !ended
            ended = true
            lock.unlock()
            if wasActive {
                activity.leaseEnded()
            }
        }
    }

    private let lock = NSLock()
    private var count = 0
    private var changes: UInt64 = 0

    init() {}

    /// Starts one piece of foreground work. Housekeeping holds off until every lease has ended.
    func begin() -> Lease {
        lock.lock()
        count += 1
        changes &+= 1
        lock.unlock()
        return Lease(activity: self)
    }

    var isActive: Bool {
        lock.lock()
        defer { lock.unlock() }
        return count > 0
    }

    /// Changes on every begin and end, so maintenance can tell whether anything happened since it
    /// last looked, even work that began and ended in between.
    var changeCount: UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return changes
    }

    fileprivate func leaseEnded() {
        lock.lock()
        count -= 1
        changes &+= 1
        lock.unlock()
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
        /// The choice changed, went missing or became unreadable while the sweep ran, so it stopped
        /// with `removed` gone rather than keep deleting against the old limit.
        case superseded(removed: Int)
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
        /// Owed, but a dictation is running or the app was busy too recently; a later pass retries.
        case deferredForActivity
        case completed(ReclaimMethod, freedPages: Int64)
        /// Stopped for a dictation, a foreground caller, Clear history or shutdown; SQLite rolled it back.
        case yielded
        /// Stopped at its time budget; the next daily pass tries again.
        case outOfTime
        case failed(code: Int32)
    }

    enum Checkpoint: Equatable, Sendable {
        case notRun
        /// No deletion or reclamation has left the WAL holding old pages.
        case notNeeded
        case completed
        /// A reader elsewhere still needs the WAL; retried after `retryIn` seconds, and by later passes.
        case busy(retryIn: TimeInterval)
        /// A dictation is running; retried later.
        case deferredForActivity
        case failed(code: Int32)
    }

    var retention: Retention = .notRun
    var reclaim: Reclaim = .notRun
    var checkpoint: Checkpoint = .notRun
    var stopped = false
}

/// Keeps `scribe.db` bounded: deletes dictation text past the user's retention choice, gives the
/// freed pages back to the disk while the app is idle, and empties the WAL after deletions. The macOS
/// side of Windows `StorageMaintenance`, for text only (macOS stores no audio).
///
/// One pass at a time, on a private serial queue, never on the caller's thread: 30 seconds after
/// `start()`, then daily on the wall clock (so a Mac that slept through the deadline runs it on wake),
/// when the retention choice changes, and after Clear history.
///
/// Retention deletes in short batches, and every batch checks the stored choice in its own
/// transaction, so a choice changed, removed or made unreadable mid-sweep stops it. Only a choice on
/// record deletes anything (see `HistoryRetentionSetting`).
///
/// Reclamation is the heavy part and gets out of the way. It runs only when the free space is worth it,
/// no dictation holds a `ForegroundActivity` lease, and neither a dictation nor a foreground database
/// call has happened for `quietPeriod` (right after Clear history only a running dictation defers it,
/// because the user asked for the text to be gone). Each statement stops as soon as a dictation starts,
/// a foreground caller waits for the connection, Clear or shutdown asks, or its time budget runs out,
/// and SQLite rolls it back. A database created before this build has auto_vacuum NONE, which never
/// shrinks, so the first reclaim converts it with one VACUUM; later ones use bounded `incremental_vacuum`
/// steps.
///
/// Text deleted now does not wait for either step to leave the database: every connection sets
/// `secure_delete` to ON (`PersistenceStore`), so SQLite overwrites a deleted row with zeros as it
/// deletes it, pages freed with it included, and reclaiming is about size, not erasure. It does not
/// reach back, though: text deleted before it was set can stay in free pages until a later write
/// reuses them or a reclaim gives them back to the disk, and no setting reaches copies outside the
/// file, such as backups or APFS snapshots. In WAL mode the zeroed pages go to the WAL first: until a
/// checkpoint copies them back, the database file keeps the old page images, and earlier frames of the
/// WAL keep the text until it is truncated. So any deletion or reclamation leaves a WAL checkpoint
/// owed, whatever the freelist says, and the checkpoint truncates the WAL. A checkpoint a reader keeps
/// busy is retried with a doubling backoff.
///
/// Logs counts and outcome names only.
///
/// `@unchecked Sendable` because the compiler cannot see the discipline: the fields grouped under
/// `stateLock` are only touched while it is held, and the fields grouped under "Queue-confined" only
/// on `queue`, by the one pass or clear step running there.
final class StorageMaintenance: @unchecked Sendable {
    struct Options: Sendable {
        /// First pass after `start()`: soon enough that a short session still applies retention, late
        /// enough to stay out of launch.
        var initialDelay: TimeInterval = 30
        var interval: TimeInterval = 24 * 60 * 60
        /// Reclaiming waits until no dictation has run and nothing has used the database for this long.
        var quietPeriod: TimeInterval = 60
        /// Free space worth reclaiming after an ordinary sweep.
        var reclaimThresholdBytes: Int64 = 1 << 20
        /// Longest one reclaim attempt may run before it stops itself.
        var reclaimTimeBudget: TimeInterval = 5
        /// Pages one incremental_vacuum step returns: 4 MiB of 4 KiB pages.
        var incrementalStepPages = 1_024
        /// Rows one retention transaction deletes.
        var retentionBatchSize = 500
        /// How long Clear history waits for its turn behind earlier history writes before it is
        /// withdrawn and fails, having cleared nothing.
        var clearTimeout: TimeInterval = 10
        /// First wait before retrying a WAL checkpoint a reader kept busy; it doubles each time.
        var checkpointRetryDelay: TimeInterval = 10
        /// Ceiling on every retry backoff.
        var maximumRetryDelay: TimeInterval = 60 * 60
    }

    /// Test seams. The app uses none.
    struct Hooks: Sendable {
        /// Called on the maintenance queue after each committed retention batch, with the entries
        /// removed so far this pass.
        var onRetentionBatch: (@Sendable (_ removedSoFar: Int) -> Void)?
        /// Called from inside SQLite's progress handler, on the storage queue, each time a reclaim
        /// statement asks whether to stop. Must not call into the store.
        var onReclaimCheck: (@Sendable () -> Void)?
        /// Called on the maintenance queue after every pass, with its report.
        var onPassFinished: (@Sendable (StorageMaintenanceReport) -> Void)?
    }

    private let store: PersistenceStore
    private let historyWriter: HistoryWriter?
    private let activity: ForegroundActivity?
    private let options: Options
    private let hooks: Hooks
    private let now: @Sendable () -> Date
    private let uptime: @Sendable () -> TimeInterval
    private let queue = DispatchQueue(label: "com.scribe.macos.storage-maintenance", qos: .utility)
    private let logger = Logger(subsystem: "com.scribe.macos", category: "StorageMaintenance")

    // Guarded by `stateLock`.
    private let stateLock = NSLock()
    private var started = false
    private var stopped = false
    private var yieldRequested = false
    private var passPending = false
    private var followUpDue: DispatchTime?
    private var reclaimEverythingRequested = false
    private var timer: DispatchSourceTimer?

    // Queue-confined.
    private var observedStoreActivity: UInt64
    private var observedActivityChanges: UInt64
    private var quietSince: TimeInterval
    private var reclaimRetries = 0
    private var checkpointRetries = 0
    private var conversionBlocked = false
    private var reclaimEverythingOwed = false
    private var checkpointOwed = false

    /// - Parameters:
    ///   - activity: the dictation lease holder; nil only where nothing dictates (tests, tools).
    ///   - now: wall clock for retention cutoffs, because that is what the stored timestamps are.
    ///   - uptime: monotonic clock for the quiet period and time budgets.
    init(
        store: PersistenceStore,
        historyWriter: HistoryWriter? = nil,
        activity: ForegroundActivity? = nil,
        options: Options = Options(),
        hooks: Hooks = Hooks(),
        now: @escaping @Sendable () -> Date = { Date() },
        uptime: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }
    ) {
        self.store = store
        self.historyWriter = historyWriter
        self.activity = activity
        self.options = options
        self.hooks = hooks
        self.now = now
        self.uptime = uptime
        observedStoreActivity = store.foregroundActivityCount
        observedActivityChanges = activity?.changeCount ?? 0
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
    ///   the quiet period (a running dictation still defers it).
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
    /// waiting a day. A sweep already running against the old choice stops at its next batch.
    func setRetention(_ retention: HistoryRetention) async throws {
        try await store.saveHistoryRetention(retention)
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

    /// Deletes all dictation history and returns how many entries went, then gives the freed space
    /// back and empties the WAL.
    ///
    /// The deletion runs as an ordered step of the history writer, so it happens after every history
    /// write accepted before this call and before any accepted after it: no entry dictated before the
    /// request can land once this has returned. If the step has not started within
    /// `Options.clearTimeout` it is withdrawn and this throws, having deleted nothing; it never runs
    /// later. A running maintenance pass is asked to stop so the space comes back sooner.
    ///
    /// A dictation still being processed when Clear is requested (transcribing, cleaning up or being
    /// inserted) has not handed its entry to the writer yet, so it is recorded after the Clear, as on
    /// Windows, where Clear deletes what the database holds at that moment. The caller empties the
    /// tray's in-memory recent dictations itself (`LastTranscriptStore.removeAll()`).
    func clearHistory() async throws -> Int {
        setYieldRequested(true)
        let result: Result<Int, Error>
        do {
            if let historyWriter {
                result = .success(try await historyWriter.clearHistory(timeout: options.clearTimeout))
            } else {
                let store = self.store
                result = .success(try await computeOnQueue { try store.clearHistory() })
            }
        } catch {
            result = .failure(error)
        }

        await runOnQueue { [self] in
            finishClear(result)
        }
        return try result.get()
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
        let everythingRequested = reclaimEverythingRequested
        reclaimEverythingRequested = false
        stateLock.unlock()

        var report = StorageMaintenanceReport()
        guard !isStopped else {
            report.stopped = true
            return report
        }
        if everythingRequested {
            reclaimEverythingOwed = true
        }

        report.retention = applyRetention()
        if !shouldYield {
            report.reclaim = reclaim()
            report.checkpoint = checkpointIfOwed()
        }
        report.stopped = isStoppedNow
        log(report)
        hooks.onPassFinished?(report)
        return report
    }

    private func finishClear(_ result: Result<Int, Error>) {
        setYieldRequested(false)
        switch result {
        case .success(let removed):
            reclaimEverythingOwed = true
            checkpointOwed = true
            let reclaimOutcome = reclaim()
            let checkpointOutcome = checkpointIfOwed()
            logger.info(
                """
                Cleared \(removed) history entries; reclaim \(Self.describe(reclaimOutcome), privacy: .public); \
                checkpoint \(Self.describe(checkpointOutcome), privacy: .public).
                """
            )
        case .failure(let error):
            let code = Self.code(of: error)
            let errorType = String(describing: type(of: error))
            logger.warning(
                "Clear history failed (\(errorType, privacy: .public), SQLite \(code)); nothing was cleared.")
        }
    }

    private func applyRetention() -> StorageMaintenanceReport.Retention {
        let setting: HistoryRetentionSetting
        do {
            setting = try store.historyRetention(purpose: .maintenance)
        } catch {
            // A setting that cannot be read is never taken for a choice.
            return .kept(.unreadable)
        }

        let days: Int
        switch setting {
        case .notChosen:
            return .kept(.notChosen)
        case .unreadable:
            return .kept(.unreadable)
        case .chosen(.keepForever):
            return .kept(.keepForever)
        case .chosen(.days(let chosen)):
            days = chosen
        }

        let passNow = now()
        var removed = 0
        while !shouldYield {
            let outcome: RetentionBatchOutcome
            do {
                outcome = try store.deleteExpiredHistoryBatch(
                    authorizedDays: days, now: passNow, limit: options.retentionBatchSize)
            } catch {
                return .failed(removed: removed, code: Self.code(of: error))
            }

            switch outcome {
            case .authorizationChanged:
                return .superseded(removed: removed)
            case .deleted(let count):
                removed += count
                if count > 0 {
                    checkpointOwed = true
                }
                hooks.onRetentionBatch?(removed)
                if count < options.retentionBatchSize {
                    return .applied(days: days, removed: removed)
                }
            }
        }
        return .applied(days: days, removed: removed)
    }

    private func reclaim() -> StorageMaintenanceReport.Reclaim {
        let everything = reclaimEverythingOwed
        let stats: PersistencePageStats
        do {
            stats = try store.pageStats()
        } catch {
            return .failed(code: Self.code(of: error))
        }

        let owed = everything ? stats.freePages > 0 : stats.freeBytes >= options.reclaimThresholdBytes
        // FULL auto_vacuum already truncates at every commit, so there is nothing to do for it.
        guard owed, stats.autoVacuum == 0 || stats.autoVacuum == 2, !(stats.autoVacuum == 0 && conversionBlocked)
        else {
            reclaimRetries = 0
            reclaimEverythingOwed = false
            return .notNeeded
        }

        // A dictation in progress always defers it. Otherwise the app must have been idle, with no
        // dictation and no foreground database call, for the quiet period; right after Clear the
        // user asked for the space back, so only a running dictation holds it off.
        let quiet = quietFor()
        if foregroundBusy || (!everything && quiet < options.quietPeriod) {
            scheduleRetry(after: nextReclaimRetryDelay())
            return .deferredForActivity
        }

        let deadline = uptime() + options.reclaimTimeBudget
        let shouldStop: @Sendable () -> Bool = { [weak self] in
            guard let self else {
                return true
            }
            self.hooks.onReclaimCheck?()
            return self.shouldYield || self.foregroundBusy || self.uptime() >= deadline
        }

        let outcome: StorageMaintenanceReport.Reclaim
        do {
            if stats.autoVacuum == 2 {
                outcome = try reclaimIncrementally(
                    freePages: stats.freePages, deadline: deadline, shouldStop: shouldStop)
            } else {
                outcome = try convertAndVacuum(freePages: stats.freePages, deadline: deadline, shouldStop: shouldStop)
            }
        } catch {
            return .failed(code: Self.code(of: error))
        }

        switch outcome {
        case .completed:
            reclaimRetries = 0
            reclaimEverythingOwed = false
            checkpointOwed = true
        case .yielded:
            scheduleRetry(after: nextReclaimRetryDelay())
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

    /// Owed after any deletion or reclamation, whatever the freelist says: a small delete frees no
    /// whole page yet still leaves the deleted text in the WAL until a checkpoint copies it back and
    /// truncates the file.
    private func checkpointIfOwed() -> StorageMaintenanceReport.Checkpoint {
        guard checkpointOwed else {
            return .notNeeded
        }
        guard !foregroundBusy else {
            scheduleRetry(after: nextCheckpointRetryDelay())
            return .deferredForActivity
        }

        do {
            if try store.checkpointWal() {
                checkpointOwed = false
                checkpointRetries = 0
                return .completed
            }
            let delay = nextCheckpointRetryDelay()
            scheduleRetry(after: delay)
            return .busy(retryIn: delay)
        } catch {
            // Still owed: the next pass tries again.
            return .failed(code: Self.code(of: error))
        }
    }

    /// How long the app has been idle: no dictation began or ended and no foreground database call
    /// happened. A change restarts the window from when maintenance noticed it, which only ever makes
    /// the window longer than the truth, never shorter.
    private func quietFor() -> TimeInterval {
        let storeActivity = store.foregroundActivityCount
        let activityChanges = activity?.changeCount ?? 0
        let current = uptime()
        if storeActivity != observedStoreActivity || activityChanges != observedActivityChanges {
            observedStoreActivity = storeActivity
            observedActivityChanges = activityChanges
            quietSince = current
        }
        return current - quietSince
    }

    // Bounded retries with no storm: each consecutive deferral doubles the wait, never past
    // `maximumRetryDelay`, and a success resets it.
    private func nextReclaimRetryDelay() -> TimeInterval {
        defer { reclaimRetries += 1 }
        return min(options.quietPeriod * Double(1 << min(reclaimRetries, 16)), options.maximumRetryDelay)
    }

    private func nextCheckpointRetryDelay() -> TimeInterval {
        defer { checkpointRetries += 1 }
        return min(options.checkpointRetryDelay * Double(1 << min(checkpointRetries, 16)), options.maximumRetryDelay)
    }

    /// Runs one more pass after `delay`. Only a started schedule retries: passes run directly (tests,
    /// Clear history) never leave work behind. An earlier retry already pending covers a later one.
    private func scheduleRetry(after delay: TimeInterval) {
        let due = DispatchTime.now() + delay
        stateLock.lock()
        guard started, !stopped else {
            stateLock.unlock()
            return
        }
        if let pending = followUpDue, pending <= due {
            stateLock.unlock()
            return
        }
        followUpDue = due
        stateLock.unlock()

        queue.asyncAfter(deadline: due) { [weak self] in
            guard let self else {
                return
            }
            self.stateLock.lock()
            if self.followUpDue == due {
                self.followUpDue = nil
            }
            self.stateLock.unlock()
            self.performPass()
        }
    }

    // MARK: - State

    /// Whether a dictation holds a lease right now. Lock-only, so safe from the progress handler.
    private var foregroundBusy: Bool {
        activity?.isActive ?? false
    }

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

    /// Runs `work` on the maintenance queue and resumes the caller with its result.
    private func computeOnQueue<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<T, Error>) in
            queue.async {
                continuation.resume(with: Result { try work() })
            }
        }
    }

    /// Runs `work` on the maintenance queue and resumes the caller once it has run.
    private func runOnQueue(_ work: @escaping @Sendable () -> Void) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            queue.async {
                work()
                continuation.resume()
            }
        }
    }

    // MARK: - Logging (shapes only)

    private func log(_ report: StorageMaintenanceReport) {
        let retentionText = Self.describe(report.retention)
        let reclaimText = Self.describe(report.reclaim)
        let checkpointText = Self.describe(report.checkpoint)
        let eventful: Bool
        switch (report.retention, report.reclaim, report.checkpoint) {
        case (.applied(_, let removed), _, _) where removed > 0:
            eventful = true
        case (.superseded, _, _), (.failed, _, _), (_, .completed, _), (_, .failed, _), (_, .outOfTime, _),
            (_, _, .busy), (_, _, .failed):
            eventful = true
        default:
            eventful = false
        }

        if eventful {
            logger.info(
                """
                Storage maintenance: retention \(retentionText, privacy: .public); \
                reclaim \(reclaimText, privacy: .public); checkpoint \(checkpointText, privacy: .public).
                """
            )
        } else {
            logger.debug(
                """
                Storage maintenance: retention \(retentionText, privacy: .public); \
                reclaim \(reclaimText, privacy: .public); checkpoint \(checkpointText, privacy: .public).
                """
            )
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
        case .superseded(let removed):
            return "stopped for a changed choice after removing \(removed)"
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

    private static func describe(_ checkpoint: StorageMaintenanceReport.Checkpoint) -> String {
        switch checkpoint {
        case .notRun:
            return "not run"
        case .notNeeded:
            return "not needed"
        case .completed:
            return "completed"
        case .busy(let retryIn):
            return "busy, retry in \(Int(retryIn)) s"
        case .deferredForActivity:
            return "deferred for activity"
        case .failed(let code):
            return "failed with SQLite \(code)"
        }
    }

    private static func code(of error: Error) -> Int32 {
        (error as? PersistenceError)?.sqliteCode ?? -1
    }
}
