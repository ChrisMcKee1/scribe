import Foundation
import XCTest
import os

@testable import Scribe

// The scenario suite's own support. SwiftPM cannot share code between test targets, so these follow the patterns of
// ScribeTests' support files rather than import them: every wait a regression could strand goes through
// `underWatchdog`, steps are ordered by explicit signals (`ScenarioCounter`, `ScenarioLatch`), never by sleeping or by
// counting yields, and whatever a test holds is let go for good when it ends.

/// What `underWatchdog` throws when its deadline passes first.
struct ScenarioTimeout: Error, CustomStringConvertible {
    let description: String
}

enum ScenarioLimits {
    /// How long any one step may take before a scenario fails instead of hanging. Generous, because the sanitizer jobs
    /// run everything several times slower; a passing run never comes near it.
    static let watchdogSeconds: Double = 120

    /// How far a resampled capture's length may stray from the exact count, in 16 kHz samples: 1 ms. The runners measure
    /// no difference at all through both converters; a lost or repeated buffer shows in the buffer counts and the
    /// correlation as well.
    static let resamplerSlack = 16

    /// The least correlation a resampled capture may have with its fixture. The round trip through two rate converters
    /// measures 0.996 or more on the runners; a capture that lost or repeated a buffer, took the wrong channel or ran at
    /// the wrong rate falls far below it.
    static let minimumCorrelation = 0.99

    /// How far a capture's level may stray from the level its channel layout implies, in dB. The runners measure 0.07
    /// at most.
    static let levelSlackDb = 0.25
}

/// Awaits `operation` under a watchdog: returns its value or rethrows its error, and fails the test and throws
/// `ScenarioTimeout` when it has not finished within `seconds`. The deadline is something to fail on, never a way to
/// order steps. An operation that outlives it is cancelled and left behind.
func underWatchdog<Value: Sendable>(
    _ description: String,
    seconds: Double = ScenarioLimits.watchdogSeconds,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ operation: @escaping @Sendable () async throws -> Value
) async throws -> Value {
    let outcome = ScenarioFirstResult<Value>()
    let work = Task {
        do {
            let value = try await operation()
            outcome.settle(.success(value))
        } catch {
            outcome.settle(.failure(error))
        }
    }
    let deadline = Task {
        try? await Task.sleep(for: .seconds(seconds))
        outcome.settle(.failure(ScenarioTimeout(description: "Timed out after \(Int(seconds)) s: \(description)")))
    }
    let result = await outcome.value
    deadline.cancel()
    if case .failure(let error) = result, let timeout = error as? ScenarioTimeout {
        work.cancel()
        XCTFail(timeout.description, file: file, line: line)
    }
    return try result.get()
}

/// Keeps the first result it is given and hands it to its one reader.
final class ScenarioFirstResult<Value: Sendable>: Sendable {
    private struct State: Sendable {
        var result: Result<Value, any Error>?
        var reader: CheckedContinuation<Result<Value, any Error>, Never>?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func settle(_ result: Result<Value, any Error>) {
        let reader = state.withLock { current -> CheckedContinuation<Result<Value, any Error>, Never>? in
            guard current.result == nil else { return nil }
            current.result = result
            defer { current.reader = nil }
            return current.reader
        }
        reader?.resume(returning: result)
    }

    var value: Result<Value, any Error> {
        get async {
            await withCheckedContinuation { (continuation: CheckedContinuation<Result<Value, any Error>, Never>) in
                let settled = state.withLock { current -> Result<Value, any Error>? in
                    if let result = current.result {
                        return result
                    }
                    current.reader = continuation
                    return nil
                }
                if let settled {
                    continuation.resume(returning: settled)
                }
            }
        }
    }
}

/// A count any thread can advance and any task can wait on: the explicit signal the scenarios order their steps by.
final class ScenarioCounter: Sendable {
    private struct Waiter: Sendable {
        let threshold: Int
        let continuation: CheckedContinuation<Void, any Error>
    }

    private struct State: Sendable {
        var value = 0
        var nextID: UInt64 = 0
        var waiters: [UInt64: Waiter] = [:]
        /// Waits cancelled before they were registered, so their registration resumes them at once.
        var cancelled: Set<UInt64> = []
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var value: Int {
        state.withLock { $0.value }
    }

    func increment() {
        let due = state.withLock { current -> [CheckedContinuation<Void, any Error>] in
            current.value += 1
            let reached = current.value
            let ready = current.waiters.filter { $0.value.threshold <= reached }
            for id in ready.keys {
                current.waiters[id] = nil
            }
            return ready.values.map(\.continuation)
        }
        for continuation in due {
            continuation.resume()
        }
    }

    /// Returns once the count has reached `threshold`, at once if it already has. Throws `CancellationError` when the
    /// waiting task is cancelled first.
    func wait(atLeast threshold: Int) async throws {
        let id = state.withLock { current -> UInt64 in
            current.nextID += 1
            return current.nextID
        }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                let reachedOrCancelled = state.withLock { current -> Bool? in
                    if current.value >= threshold {
                        return true
                    }
                    if current.cancelled.remove(id) != nil {
                        return false
                    }
                    current.waiters[id] = Waiter(threshold: threshold, continuation: continuation)
                    return nil
                }
                if let reached = reachedOrCancelled {
                    if reached {
                        continuation.resume()
                    } else {
                        continuation.resume(throwing: CancellationError())
                    }
                }
            }
        } onCancel: {
            let waiter = state.withLock { current -> Waiter? in
                if let waiter = current.waiters.removeValue(forKey: id) {
                    return waiter
                }
                current.cancelled.insert(id)
                return nil
            }
            waiter?.continuation.resume(throwing: CancellationError())
        }
    }
}

/// A one-way gate: `open()` from any thread lets every waiter through, now and later.
final class ScenarioLatch: Sendable {
    private let counter = ScenarioCounter()
    private let opened = OSAllocatedUnfairLock(initialState: false)

    func open() {
        let first = opened.withLock { isOpen -> Bool in
            defer { isOpen = true }
            return !isOpen
        }
        if first {
            counter.increment()
        }
    }

    var isOpen: Bool {
        opened.withLock { $0 }
    }

    /// Returns once the latch is open; throws `CancellationError` when the waiting task is cancelled first.
    func wait() async throws {
        try await counter.wait(atLeast: 1)
    }
}

/// Measurements a scenario reports without gating on them: timings, levels and counts. Printed as one line and, when
/// `SCRIBE_SCENARIO_REPORT_DIR` names a directory, written there as a file of its own, because `swift test --parallel`
/// shows only a failing test's output. The workflow prints those files and uploads them.
final class ScenarioReport: Sendable {
    let name: String
    private let entries = OSAllocatedUnfairLock<[String]>(initialState: [])

    init(_ name: String) {
        self.name = name
    }

    func note(_ key: String, _ value: String) {
        entries.withLock { $0.append("\(key)=\(value)") }
    }

    func note(_ key: String, count: Int) {
        note(key, String(count))
    }

    func note(_ key: String, value: Double, digits: Int = 3) {
        note(key, String(format: "%.\(digits)f", value))
    }

    func note(_ key: String, duration: Duration) {
        note("\(key)Ms", value: Self.milliseconds(duration), digits: 1)
    }

    static func milliseconds(_ duration: Duration) -> Double {
        let (seconds, attoseconds) = duration.components
        return Double(seconds) * 1_000 + Double(attoseconds) / 1e15
    }

    /// Prints the report and writes it to the report directory, if there is one.
    func write() {
        let line = "SCENARIO \(name) " + entries.withLock { $0 }.joined(separator: " ")
        print(line)
        guard let directory = ProcessInfo.processInfo.environment["SCRIBE_SCENARIO_REPORT_DIR"], !directory.isEmpty
        else {
            return
        }
        let folder = URL(fileURLWithPath: directory, isDirectory: true)
        let fileName = String(name.map { $0.isLetter || $0.isNumber ? $0 : "-" }) + ".txt"
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? (line + "\n").write(to: folder.appendingPathComponent(fileName), atomically: true, encoding: .utf8)
    }
}

/// Everything `ScribeLog` passes on while a scenario runs: each standard error line and both unified log arguments.
final class ScenarioLogRecorder: Sendable {
    private let recorded: OSAllocatedUnfairLock<[ScribeLog.Rendering]>
    private let observation: ScribeLog.Observation

    init() {
        let recorded = OSAllocatedUnfairLock<[ScribeLog.Rendering]>(initialState: [])
        self.recorded = recorded
        observation = ScribeLog.addObserver { rendering in
            recorded.withLock { $0.append(rendering) }
        }
    }

    var renderings: [ScribeLog.Rendering] {
        recorded.withLock { $0 }
    }

    /// Every line and every public and private argument, which is everything any log reader could see.
    var everyText: String {
        renderings.flatMap { [$0.line, $0.publicText ?? "", $0.privateText ?? ""] }.joined(separator: "\n")
    }

    func stop() {
        observation.cancel()
    }
}

/// Checks that dictated content never reaches a log.
enum ScenarioPrivacy {
    /// The lowercased words of `text`, so a fragment is found whatever case or punctuation a log line put around it.
    static func words(_ text: String) -> [String] {
        text.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
    }

    /// What a leak of `text` would show: the whole text when it is four words or fewer, otherwise every run of four
    /// consecutive words. Four words is specific enough that no fixed log message says it by chance.
    static func fragments(of text: String) -> [String] {
        let tokens = words(text)
        guard tokens.count > 4 else {
            return tokens.isEmpty ? [] : [tokens.joined(separator: " ")]
        }
        return (0...(tokens.count - 4)).map { tokens[$0..<($0 + 4)].joined(separator: " ") }
    }

    /// The fragments of `texts` that appear anywhere in what `recorder` saw.
    static func leaks(of texts: [String], in recorder: ScenarioLogRecorder) -> [String] {
        let logged = " " + words(recorder.everyText).joined(separator: " ") + " "
        var found: [String] = []
        for text in texts {
            for fragment in fragments(of: text) where logged.contains(" \(fragment) ") {
                found.append(fragment)
            }
        }
        return found
    }
}

/// Word overlap as the Windows suite measures it (`tools/Scribe.AsrCheck`, `WordOverlap`): the share of the expected
/// words that appear in the transcript, whatever their order, case or punctuation. It asks whether a recognizer decoded
/// the speech at all, not how accurately.
enum ScenarioText {
    static let minimumOverlap = 0.6

    static func wordOverlap(expected: String, actual: String) -> Double {
        let expectedWords = ScenarioPrivacy.words(expected)
        guard !expectedWords.isEmpty else { return 0 }
        let actualWords = Set(ScenarioPrivacy.words(actual))
        return Double(expectedWords.filter { actualWords.contains($0) }.count) / Double(expectedWords.count)
    }
}

extension XCTestCase {
    /// A new, empty directory for this test, removed with its contents when the test ends.
    func makeScenarioDirectory(_ label: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScribeScenarios-\(label)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        addTeardownBlock {
            try? FileManager.default.removeItem(at: url)
        }
        return url
    }

    /// Records every event `ScribeLog` renders until the test ends.
    func recordScenarioLog() -> ScenarioLogRecorder {
        let recorder = ScenarioLogRecorder()
        addTeardownBlock {
            recorder.stop()
        }
        return recorder
    }

    /// A latch this test's teardown opens for good, so nothing the test left waiting at it stays stranded.
    func makeScenarioLatch() -> ScenarioLatch {
        let latch = ScenarioLatch()
        addTeardownBlock {
            latch.open()
        }
        return latch
    }
}
