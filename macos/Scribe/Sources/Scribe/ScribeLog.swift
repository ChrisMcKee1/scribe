import Darwin
import Foundation
import os

/// Scribe's logging facade. One call writes an event to the unified log (subsystem `com.scribe.macos`,
/// one category per area) and, for every level above debug, one line to standard error, which is what
/// a Terminal launch shows.
///
/// Logs hold shapes, never content: no transcripts or dictation text, dictionary entries, snippet
/// bodies, prompts, clipboard content, endpoints, keys or AI output (see PRIVACY.md). This API makes
/// that the easy path. A message is a `StaticString`, so it cannot interpolate a value, and values
/// travel as `Field`s, each built from a shape: a count or an integer code, a number, a flag, an enum
/// case name, a static label or a `FailureShape`. The one field that takes a dynamic string,
/// `Field.sensitive`, is for a user-chosen device or profile name or a path, when a diagnosis needs
/// one: standard error shows it as `<private>` and the unified log marks it `.private`, which redacts
/// it unless the Mac has a logging profile that enables private data. Never give it content.
///
///     ScribeLog.info(.transcription, "Decoded", .count("characters", text.count), .milliseconds("decode", ms))
///     ScribeLog.warning(.cleanup, "Cleanup failed, using the raw text", .failure(error))
///
/// Debug events reach the unified log only:
/// `log stream --level debug --predicate 'subsystem == "com.scribe.macos"'`.
enum ScribeLog {
    static let subsystem = "com.scribe.macos"

    /// The unified log category of each area.
    enum Category: String, CaseIterable, Sendable {
        case app = "App"
        case audio = "AudioCapture"
        case cleanup = "Cleanup"
        case dictation = "Dictation"
        case hotkey = "Hotkey"
        case injection = "TextInjection"
        case overlay = "Overlay"
        case persistence = "Persistence"
        case process = "Process"
        case settings = "Settings"
        case transcription = "Transcription"
    }

    enum Level: String, CaseIterable, Sendable {
        case debug
        case info
        case notice
        case warning
        case error
        case fault
    }

    /// One named value of an event. Each factory takes a shape, never text a user or a service wrote.
    struct Field: Sendable {
        fileprivate enum Value: Sendable {
            case shape(String)
            case sensitive(String)
        }

        fileprivate let key: StaticString
        fileprivate let value: Value

        private init(_ key: StaticString, _ value: Value) {
            self.key = key
            self.value = value
        }

        static func count(_ key: StaticString, _ value: Int) -> Field {
            Field(key, .shape(String(value)))
        }

        /// A status, an `OSStatus`, an `errno`, a pid: any integer that is a code rather than a count.
        static func integer<Integer: BinaryInteger>(_ key: StaticString, _ value: Integer) -> Field {
            Field(key, .shape(String(value)))
        }

        /// A measurement such as a level in dBFS or a ratio, with `precision` digits after the point.
        static func decimal(_ key: StaticString, _ value: Double, precision: Int = 1) -> Field {
            Field(key, .shape(String(format: "%.\(min(max(precision, 0), 6))f", value)))
        }

        static func milliseconds(_ key: StaticString, _ value: Double) -> Field {
            Field(key, .shape(String(format: "%.1fms", value)))
        }

        static func duration(_ key: StaticString, _ value: Duration) -> Field {
            let (seconds, attoseconds) = value.components
            return milliseconds(key, Double(seconds) * 1_000 + Double(attoseconds) / 1_000_000_000_000_000)
        }

        static func flag(_ key: StaticString, _ value: Bool) -> Field {
            Field(key, .shape(value ? "true" : "false"))
        }

        /// The case name of an enum value, without its payload: `.name("result", injection)` logs
        /// `result=fallbackUsed`. An optional is unwrapped, and `nil` logs `none`. Anything that is not
        /// an enum logs `?`, so text passed here by mistake is never written.
        static func name<Subject>(_ key: StaticString, _ value: Subject) -> Field {
            Field(key, .shape(caseName(of: value)))
        }

        static func label(_ key: StaticString, _ value: StaticString) -> Field {
            Field(key, .shape(value.description))
        }

        /// The failure's `FailureShape`, under the key `failure`.
        static func failure(_ error: any Error) -> Field {
            failure(FailureShape(error))
        }

        static func failure(_ shape: FailureShape) -> Field {
            Field("failure", .shape("[\(shape.description)]"))
        }

        /// A user-chosen name or a path, and only when a diagnosis needs it: never a transcript,
        /// dictionary or snippet text, a prompt, clipboard content, an endpoint, a key or AI output.
        /// Standard error gets `<private>`; the unified log gets the value in its private argument,
        /// marked `.private`.
        static func sensitive(_ key: StaticString, _ value: String) -> Field {
            Field(key, .sensitive(value))
        }

        private static func caseName(of value: Any) -> String {
            let mirror = Mirror(reflecting: value)
            if mirror.displayStyle == .optional {
                guard let wrapped = mirror.children.first?.value else { return "none" }
                return caseName(of: wrapped)
            }
            return EnumCaseName.of(value) ?? "?"
        }
    }

    static func debug(_ category: Category, _ message: StaticString, _ fields: Field...) {
        log(.debug, category, message, fields)
    }

    static func info(_ category: Category, _ message: StaticString, _ fields: Field...) {
        log(.info, category, message, fields)
    }

    static func notice(_ category: Category, _ message: StaticString, _ fields: Field...) {
        log(.notice, category, message, fields)
    }

    static func warning(_ category: Category, _ message: StaticString, _ fields: Field...) {
        log(.warning, category, message, fields)
    }

    static func error(_ category: Category, _ message: StaticString, _ fields: Field...) {
        log(.error, category, message, fields)
    }

    /// For a state that means Scribe has a defect, as opposed to a failure the world caused.
    static func fault(_ category: Category, _ message: StaticString, _ fields: Field...) {
        log(.fault, category, message, fields)
    }

    static func log(_ level: Level, _ category: Category, _ message: StaticString, _ fields: [Field]) {
        let rendering = render(level, category, message, fields)
        let publicText = rendering.publicText ?? ""
        let logger = loggers[category] ?? Logger(subsystem: subsystem, category: category.rawValue)
        // The public text is built only from static strings and shapes (see Field), so marking it
        // public exposes nothing; the sensitive values travel alone, in the private argument.
        if let privateText = rendering.privateText {
            logger.log(level: level.osLogType, "\(publicText, privacy: .public) \(privateText, privacy: .private)")
        } else {
            logger.log(level: level.osLogType, "\(publicText, privacy: .public)")
        }
        publish(rendering, toStandardError: level != .debug)
    }

    /// Writes `line` to standard error as given. Kept for `AppDelegate.writeLogLine`, whose lines predate
    /// this facade and are not all shapes; new code logs through the functions above instead.
    static func legacyUnshapedLine(_ line: String) {
        publish(Rendering(line: line, publicText: nil, privateText: nil), toStandardError: true)
    }

    // MARK: - Rendering

    /// What one event becomes.
    struct Rendering: Sendable, Equatable {
        /// The standard error line: `[Cleanup] warning: Cleanup failed, using the raw text failure=[...]`.
        let line: String
        /// The unified log's public argument: the message and every field except the sensitive ones.
        /// `nil` for a legacy line, which never reaches the unified log.
        let publicText: String?
        /// The unified log's private argument, the sensitive fields, or `nil` when there are none.
        let privateText: String?
    }

    static func render(_ level: Level, _ category: Category, _ message: StaticString, _ fields: [Field]) -> Rendering {
        var publicText = message.description
        var lineText = publicText
        var privateFields: [String] = []
        for field in fields {
            let key = field.key.description
            switch field.value {
            case .shape(let shape):
                publicText += " \(key)=\(shape)"
                lineText += " \(key)=\(shape)"
            case .sensitive(let value):
                lineText += " \(key)=<private>"
                privateFields.append("\(key)=\(value)")
            }
        }
        return Rendering(
            line: "[\(category.rawValue)] \(level.rawValue): \(lineText)",
            publicText: publicText,
            privateText: privateFields.isEmpty ? nil : privateFields.joined(separator: " "))
    }

    // MARK: - Observers

    /// Calls `observer` with every event from now on, until the returned observation is cancelled, with
    /// exactly what `log` passed on: the standard error line and the unified log's public and private
    /// arguments. Debug events are included, although standard error never gets them, and so are
    /// legacy lines. For tests that check what a code path logs.
    static func addObserver(_ observer: @escaping @Sendable (Rendering) -> Void) -> Observation {
        let id = observers.withLock { current -> UInt64 in
            current.nextID += 1
            current.handlers[current.nextID] = observer
            return current.nextID
        }
        return Observation(id: id)
    }

    final class Observation: Sendable {
        private let id: UInt64

        fileprivate init(id: UInt64) {
            self.id = id
        }

        func cancel() {
            let id = id
            ScribeLog.observers.withLock { current in
                current.handlers[id] = nil
            }
        }
    }

    private struct Observers: Sendable {
        var nextID: UInt64 = 0
        var handlers: [UInt64: @Sendable (Rendering) -> Void] = [:]
    }

    private static let observers = OSAllocatedUnfairLock(initialState: Observers())

    private static let loggers: [Category: Logger] = Dictionary(
        uniqueKeysWithValues: Category.allCases.map { category in
            (category, Logger(subsystem: ScribeLog.subsystem, category: category.rawValue))
        })

    /// The one place an event reaches standard error or an observer.
    private static func publish(_ rendering: Rendering, toStandardError: Bool) {
        if toStandardError {
            _ = StandardErrorSink.emit(rendering.line)
        }
        let handlers = observers.withLock { current in Array(current.handlers.values) }
        for handler in handlers {
            handler(rendering)
        }
    }
}

extension ScribeLog.Level {
    fileprivate var osLogType: OSLogType {
        switch self {
        case .debug: return .debug
        case .info: return .info
        case .notice: return .default
        case .warning, .error: return .error
        case .fault: return .fault
        }
    }
}

/// Writes whole lines to standard error, one at a time.
enum StandardErrorSink {
    private static let writing = OSAllocatedUnfairLock()

    /// Writes `line` and a newline to `descriptor`, standard error unless a test passes another, and
    /// says whether all of it was written. The descriptor is marked, before every write, to fail a
    /// write whose reader has gone away (`Scribe 2>&1 | head`) with EPIPE instead of raising SIGPIPE,
    /// which would end the app; such a line is lost and nothing else happens. Marking it every time
    /// also covers a standard error that was replaced after launch.
    static func emit(_ line: String, to descriptor: Int32 = STDERR_FILENO) -> Bool {
        let bytes = Array((line + "\n").utf8)
        return writing.withLock {
            _ = fcntl(descriptor, F_SETNOSIGPIPE, 1)
            return bytes.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) -> Bool in
                guard let base = buffer.baseAddress else { return true }
                var offset = 0
                while offset < buffer.count {
                    let written = Darwin.write(descriptor, base + offset, buffer.count - offset)
                    if written > 0 {
                        offset += written
                    } else if written < 0 && errno == EINTR {
                        continue
                    } else {
                        return false
                    }
                }
                return true
            }
        }
    }
}
