import Foundation

/// Explicit model downloads through Ollama's `/api/pull`, to Ollama's own address on this Mac only. A port of Windows'
/// `OllamaModelDownloader`: it sends only a validated model name, never a dictation, writing style or vocabulary; it
/// downloads without loading or choosing the model; and a stream that stops without Ollama's `success` is a failure.
/// Cancelling the task stops the request and the read. Ollama keeps the parts already downloaded and resumes them on
/// the next request for the same model.
final class OllamaModelDownloader: @unchecked Sendable {
    /// The whole download's limit, Windows' two hours. Ollama sends progress while it downloads, but checking a large
    /// file's digest can be quiet for minutes, so no shorter wait between lines is imposed either.
    static let overallTimeout: TimeInterval = 2 * 60 * 60
    /// Ollama's progress lines are a few hundred bytes. A longer line is not one, and is not kept growing.
    static let maximumLineBytes = 64 * 1024

    private let session: URLSession
    private let delegate: LocalServerClient.RedirectRefusingURLSessionDelegate
    private let lock = NSLock()
    private var downloading = false

    init(configuration: URLSessionConfiguration = OllamaModelDownloader.makeConfiguration()) {
        let delegate = LocalServerClient.RedirectRefusingURLSessionDelegate()
        self.delegate = delegate
        session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
    }

    deinit {
        session.invalidateAndCancel()
    }

    /// `LocalServerClient`'s session rules (no proxy, no cookies, no cache), with the download's own limits.
    static func makeConfiguration(
        _ configuration: URLSessionConfiguration = .ephemeral
    ) -> URLSessionConfiguration {
        let safe = LocalServerClient.makeConfiguration(configuration)
        safe.timeoutIntervalForRequest = overallTimeout
        safe.timeoutIntervalForResource = overallTimeout
        return safe
    }

    /// The pull address for an Ollama address, or `nil` for any address that is not Ollama's own on this Mac.
    static func pullURL(for endpoint: String) -> URL? {
        guard LocalAiServer.appAt(endpoint) == .ollama,
            let address = URL(string: endpoint.trimmingCharacters(in: .whitespacesAndNewlines)),
            let host = address.host(percentEncoded: false)?.lowercased(),
            let port = address.port
        else {
            return nil
        }
        let authority = host.contains(":") ? "[\(host)]" : host
        return URL(string: "http://\(authority):\(port)/api/pull")
    }

    /// Downloads `model` through the Ollama at `endpoint`, reporting each progress line in order. Throws
    /// `CancellationError` when the task is cancelled and `OllamaDownloadError` for anything else.
    func download(
        _ model: String,
        from endpoint: String = LocalAiServer.ollamaAddress,
        progress: @escaping @Sendable (OllamaDownloadProgress) async -> Void = { _ in }
    ) async throws {
        guard let name = OllamaModelDownload.validate(model).model else {
            throw OllamaDownloadError.invalidName
        }
        guard let url = Self.pullURL(for: endpoint) else {
            throw OllamaDownloadError.notOllama
        }
        try Task.checkCancellation()
        guard admit() else {
            throw OllamaDownloadError.alreadyDownloading
        }
        defer { release() }

        ScribeLog.info(
            .cleanup, "Ollama model download started",
            .flag("publisher", name.contains("/")), .flag("tagged", name.contains(":")))
        do {
            try await pull(name, from: url, progress: progress)
            ScribeLog.info(.cleanup, "Ollama model download finished", .label("outcome", "completed"))
        } catch {
            let failure = Self.classify(error)
            if let reason = failure as? OllamaDownloadError {
                var status = 0
                if case .httpStatus(let code) = reason {
                    status = code
                }
                ScribeLog.warning(
                    .cleanup, "Ollama model download finished", .label("outcome", "failed"),
                    .name("reason", reason), .integer("status", status))
            } else {
                ScribeLog.info(.cleanup, "Ollama model download finished", .label("outcome", "canceled"))
            }
            throw failure
        }
    }

    private func admit() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !downloading else { return false }
        downloading = true
        return true
    }

    private func release() {
        lock.lock()
        downloading = false
        lock.unlock()
    }

    private func pull(
        _ model: String,
        from url: URL,
        progress: @escaping @Sendable (OllamaDownloadProgress) async -> Void
    ) async throws {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = Self.overallTimeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/x-ndjson", forHTTPHeaderField: "Accept")
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        request.httpBody = try encoder.encode(PullRequest(model: model, stream: true))

        let relay = CancellationRelay()
        try await withTaskCancellationHandler {
            let (bytes, response) = try await session.bytes(for: request)
            relay.attach(bytes.task)
            defer { bytes.task.cancel() }
            guard let http = response as? HTTPURLResponse else {
                throw OllamaDownloadError.unreadableAnswer
            }
            guard (200..<300).contains(http.statusCode) else {
                throw OllamaDownloadError.httpStatus(http.statusCode)
            }

            var line: [UInt8] = []
            for try await byte in bytes {
                if byte == UInt8(ascii: "\n") {
                    if try await Self.handle(line, progress: progress) {
                        return
                    }
                    line.removeAll(keepingCapacity: true)
                } else {
                    guard line.count < Self.maximumLineBytes else {
                        throw OllamaDownloadError.unreadableAnswer
                    }
                    line.append(byte)
                }
            }
            if try await Self.handle(line, progress: progress) {
                return
            }
            try Task.checkCancellation()
            throw OllamaDownloadError.notConfirmed
        } onCancel: {
            relay.cancel()
        }
    }

    /// Reports one line and says whether it was Ollama's `success`.
    private static func handle(
        _ line: [UInt8],
        progress: @Sendable (OllamaDownloadProgress) async -> Void
    ) async throws -> Bool {
        try Task.checkCancellation()
        guard let value = try parse(line) else {
            return false
        }
        await progress(value)
        return value.stage == .completed
    }

    /// One progress line, or `nil` for a blank one. An `error` member, or anything that is not a JSON object, ends the
    /// download.
    static func parse(_ line: [UInt8]) throws -> OllamaDownloadProgress? {
        let text = String(decoding: line, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            return nil
        }
        guard let object = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any] else {
            throw OllamaDownloadError.unreadableAnswer
        }
        if let error = object["error"], !(error is NSNull) {
            throw OllamaDownloadError.serverError(
                missingManifest: (error as? String) == OllamaModelDownload.missingManifestMessage)
        }

        let status = object["status"] as? String ?? ""
        let total = max(0, number(object["total"]))
        let completed = max(0, number(object["completed"]))
        let stage: OllamaDownloadStage
        switch status {
        case "success":
            stage = .completed
        case "verifying sha256 digest":
            stage = .verifying
        case "writing manifest", "removing any unused layers":
            stage = .installing
        default:
            stage = total > 0 ? .downloading : .preparing
        }
        return OllamaDownloadProgress(
            stage: stage,
            completedBytes: completed,
            totalBytes: total,
            percent: total > 0 ? Double(completed) * 100 / Double(total) : nil)
    }

    private static func number(_ value: Any?) -> Int64 {
        guard let number = value as? NSNumber, number !== kCFBooleanTrue, number !== kCFBooleanFalse else {
            return 0
        }
        // Only shown, never used to decide anything, so a size that is not a whole byte count is shown as near as it can.
        let bytes = number.doubleValue
        guard bytes.isFinite, bytes > 0 else {
            return 0
        }
        return bytes >= 9.0e18 ? Int64.max : Int64(bytes)
    }

    /// What a failure was, as `CancellationError` or `OllamaDownloadError`, with no text from the server kept.
    static func classify(_ error: any Error) -> any Error {
        if error is CancellationError || Task.isCancelled {
            return CancellationError()
        }
        if let error = error as? OllamaDownloadError {
            return error
        }
        if let error = error as? URLError {
            switch error.code {
            case .timedOut:
                return OllamaDownloadError.timedOut
            case .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .notConnectedToInternet,
                .networkConnectionLost:
                return OllamaDownloadError.unreachable
            default:
                return OllamaDownloadError.interrupted
            }
        }
        return OllamaDownloadError.interrupted
    }

    private struct PullRequest: Encodable {
        let model: String
        let stream: Bool
    }

    /// Cancels the request once its task exists, whichever comes first.
    private final class CancellationRelay: @unchecked Sendable {
        private let lock = NSLock()
        private var task: URLSessionTask?
        private var cancelled = false

        func attach(_ task: URLSessionTask) {
            lock.lock()
            self.task = task
            let cancel = cancelled
            lock.unlock()
            if cancel {
                task.cancel()
            }
        }

        func cancel() {
            lock.lock()
            cancelled = true
            let task = self.task
            lock.unlock()
            task?.cancel()
        }
    }
}
