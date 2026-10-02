import Foundation

enum LocalServerReach: Sendable, Equatable {
    case reached
    case notRunning
    case failed
    case needsKey
}

struct LocalServerModel: Sendable, Equatable, Identifiable {
    let id: String
    let displayName: String
    let sizeBytes: Int64
    var maxContextTokens: Int

    init(_ id: String, _ displayName: String, _ sizeBytes: Int64, maxContextTokens: Int = 0) {
        self.id = id
        self.displayName = displayName
        self.sizeBytes = sizeBytes
        self.maxContextTokens = maxContextTokens
    }
}

struct LocalServerLoadedModel: Sendable, Equatable, Identifiable {
    let id: String
    let memoryBytes: Int64
    var contextTokens: Int
    var instanceID: String?
    var remainingTTLSeconds: Int64?

    init(
        _ id: String,
        _ memoryBytes: Int64,
        contextTokens: Int = 0,
        instanceID: String? = nil,
        remainingTTLSeconds: Int64? = nil
    ) {
        self.id = id
        self.memoryBytes = memoryBytes
        self.contextTokens = contextTokens
        self.instanceID = instanceID
        self.remainingTTLSeconds = remainingTTLSeconds
    }
}

struct LocalServerState: Sendable, Equatable {
    let reach: LocalServerReach
    let models: [LocalServerModel]
    let loaded: [LocalServerLoadedModel]
    var failureDetail: String?

    static let notRunning = LocalServerState(reach: .notRunning, models: [], loaded: [], failureDetail: nil)
    static let failed = LocalServerState(reach: .failed, models: [], loaded: [], failureDetail: nil)
    static let needsKey = LocalServerState(reach: .needsKey, models: [], loaded: [], failureDetail: nil)

    func loaded(for modelID: String?) -> LocalServerLoadedModel? {
        guard let modelID, !modelID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        return loaded.first { LocalServerClient.sameModel($0.id, modelID) }
    }
}

/// What Scribe asks Ollama and LM Studio on this Mac, and how it reads their answers.
final class LocalServerClient: @unchecked Sendable {
    static let readTimeout: TimeInterval = 4
    static let listTimeout: TimeInterval = 10
    static let loadedTimeout: TimeInterval = 3
    static let unloadTimeout: TimeInterval = 15
    static let loadTimeout: TimeInterval = 120

    private static let loopbackHosts = ["localhost", "127.0.0.1", "::1"]

    private let session: URLSession
    private let invalidator: (() -> Void)?

    init(session: URLSession) {
        self.session = session
        invalidator = nil
    }

    init(configuration: URLSessionConfiguration = LocalServerClient.makeConfiguration()) {
        let delegate = RedirectRefusingURLSessionDelegate()
        let session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
        self.session = session
        invalidator = {
            session.invalidateAndCancel()
            _ = delegate
        }
    }

    deinit {
        invalidator?()
    }

    static func makeConfiguration() -> URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.connectionProxyDictionary = [:] as [AnyHashable: Any]
        configuration.httpShouldSetCookies = false
        configuration.httpCookieAcceptPolicy = .never
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = loadTimeout
        configuration.timeoutIntervalForResource = loadTimeout
        return configuration
    }

    func read(_ endpoint: String, apiKey: String? = nil) async -> LocalServerState {
        let roots = roots(for: endpoint)
        guard !roots.isEmpty else {
            return .failed
        }

        do {
            return try await firstAnswer(roots) { root in
                switch root.app {
                case .ollama:
                    return try await self.readOllama(root.url, apiKey: apiKey)
                case .lmStudio:
                    return try await self.readLMStudio(root.url, apiKey: apiKey)
                case .none:
                    return .failed
                }
            }
        } catch FirstAnswerError.notRunning {
            return .notRunning
        } catch is CancellationError {
            return .failed
        } catch {
            return LocalServerState(
                reach: .failed,
                models: [],
                loaded: [],
                failureDetail: FailureShape.describe(error))
        }
    }

    func unload(_ endpoint: String, modelID: String, apiKey: String? = nil) async -> Bool {
        let roots = roots(for: endpoint)
        guard !roots.isEmpty, !modelID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }

        do {
            return try await firstAnswer(roots) { root in
                switch root.app {
                case .ollama:
                    var request = self.request(
                        .post, url: Self.relativeURL("/api/generate", to: root.url), apiKey: apiKey,
                        json: ["model": modelID.trimmingCharacters(in: .whitespacesAndNewlines), "keep_alive": 0])
                    request.timeoutInterval = Self.unloadTimeout
                    let (_, response) = try await self.send(request)
                    return response.statusCode >= 200 && response.statusCode < 300
                case .lmStudio:
                    return try await self.unloadLMStudioModel(root.url, modelID: modelID, apiKey: apiKey)
                case .none:
                    return false
                }
            }
        } catch {
            return false
        }
    }

    func loadWithContext(
        _ endpoint: String,
        modelID: String,
        contextTokens: Int,
        apiKey: String? = nil
    ) async -> String? {
        let roots = roots(for: endpoint).filter { $0.app == .lmStudio }
        guard !roots.isEmpty,
            !modelID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
            contextTokens > 0
        else {
            return nil
        }

        do {
            return try await firstAnswer(roots) { root in
                var request = self.request(
                    .post, url: Self.relativeURL("/api/v1/chat", to: root.url), apiKey: apiKey,
                    json: [
                        "model": modelID.trimmingCharacters(in: .whitespacesAndNewlines),
                        "input": "ok",
                        "max_output_tokens": 1,
                        "temperature": 0,
                        "context_length": contextTokens,
                        "store": false,
                    ])
                request.timeoutInterval = Self.loadTimeout
                let (data, response) = try await self.send(request)
                guard response.statusCode >= 200 && response.statusCode < 300 else {
                    return nil
                }
                let body = try Self.jsonObject(from: data)
                return Self.text(body["model_instance_id"]) ?? modelID.trimmingCharacters(in: .whitespacesAndNewlines)
            }
        } catch {
            return nil
        }
    }

    func unloadInstance(_ endpoint: String, instanceID: String, apiKey: String? = nil) async -> Bool {
        let roots = roots(for: endpoint).filter { $0.app == .lmStudio }
        guard !roots.isEmpty, !instanceID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }

        do {
            return try await firstAnswer(roots) { root in
                var request = self.request(
                    .post, url: Self.relativeURL("/api/v1/models/unload", to: root.url), apiKey: apiKey,
                    json: ["instance_id": instanceID.trimmingCharacters(in: .whitespacesAndNewlines)])
                request.timeoutInterval = Self.unloadTimeout
                let (_, response) = try await self.send(request)
                return response.statusCode >= 200 && response.statusCode < 300
            }
        } catch {
            return false
        }
    }

    func readMaxContext(_ endpoint: String, modelID: String, apiKey: String? = nil) async -> Int {
        let roots = roots(for: endpoint).filter { $0.app == .ollama }
        guard !roots.isEmpty, !modelID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return 0
        }

        do {
            return try await firstAnswer(roots) { root in
                var request = self.request(
                    .post, url: Self.relativeURL("/api/show", to: root.url), apiKey: apiKey,
                    json: ["model": modelID.trimmingCharacters(in: .whitespacesAndNewlines)])
                request.timeoutInterval = Self.readTimeout
                let (data, response) = try await self.send(request)
                guard response.statusCode >= 200 && response.statusCode < 300 else {
                    return 0
                }
                let body = try Self.jsonObject(from: data)
                guard let info = body["model_info"] as? [String: Any] else {
                    return 0
                }
                for (key, value) in info where key.hasSuffix(".context_length") {
                    let length = Self.int64(value)
                    if length > 0 {
                        return min(Int(length), Int(Int32.max))
                    }
                }
                return 0
            }
        } catch {
            return 0
        }
    }

    static func sameModel(_ first: String?, _ second: String?) -> Bool {
        guard let first = trimmed(first), let second = trimmed(second) else {
            return false
        }
        return first.caseInsensitiveCompare(second) == .orderedSame
            || tagged(first).caseInsensitiveCompare(tagged(second)) == .orderedSame
    }

    private static func tagged(_ name: String) -> String {
        let lastSegment = name[(name.lastIndex(of: "/").map { name.index(after: $0) } ?? name.startIndex)...]
        return lastSegment.contains(":") ? name : name + ":latest"
    }

    private func unloadLMStudioModel(_ root: URL, modelID: String, apiKey: String?) async throws -> Bool {
        var readRequest = request(.get, url: Self.relativeURL("/api/v1/models", to: root), apiKey: apiKey)
        readRequest.timeoutInterval = Self.unloadTimeout
        let (data, response) = try await send(readRequest)
        guard response.statusCode >= 200 && response.statusCode < 300 else {
            return false
        }

        let body = try Self.jsonObject(from: data)
        var instances: [String] = []
        for model in Self.array(body["models"]) {
            let key = Self.text(model["key"])
            for instance in Self.array(model["loaded_instances"]) {
                if let id = Self.text(instance["id"]),
                    Self.sameModel(key, modelID) || Self.sameModel(id, modelID)
                {
                    instances.append(id)
                }
            }
        }

        var allSucceeded = true
        for instanceID in instances {
            var unloadRequest = request(
                .post, url: Self.relativeURL("/api/v1/models/unload", to: root), apiKey: apiKey,
                json: ["instance_id": instanceID])
            unloadRequest.timeoutInterval = Self.unloadTimeout
            let (_, unloadResponse) = try await send(unloadRequest)
            allSucceeded = allSucceeded && unloadResponse.statusCode >= 200 && unloadResponse.statusCode < 300
        }
        return allSucceeded
    }

    private func readOllama(_ root: URL, apiKey: String?) async throws -> LocalServerState {
        var models: [LocalServerModel] = []
        do {
            var tagsRequest = request(.get, url: Self.relativeURL("/api/tags", to: root), apiKey: apiKey)
            tagsRequest.timeoutInterval = Self.listTimeout
            let (data, response) = try await send(tagsRequest)
            if Self.refused(response) {
                return .needsKey
            }
            guard response.statusCode >= 200 && response.statusCode < 300 else {
                return LocalServerState(
                    reach: .failed,
                    models: [],
                    loaded: [],
                    failureDetail: "HTTP \(response.statusCode) from the model list")
            }

            let decoded = try JSONDecoder().decode(OllamaTagsResponse.self, from: data)
            for model in decoded.models ?? [] where Self.canChat(model.capabilities) {
                if let name = Self.trimmed(model.name) {
                    models.append(LocalServerModel(name, name, max(0, model.size ?? 0)))
                }
            }
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw error
        }

        var loaded: [LocalServerLoadedModel] = []
        do {
            var psRequest = request(.get, url: Self.relativeURL("/api/ps", to: root), apiKey: apiKey)
            psRequest.timeoutInterval = Self.loadedTimeout
            let (data, response) = try await send(psRequest)
            if response.statusCode >= 200 && response.statusCode < 300 {
                let decoded = try JSONDecoder().decode(OllamaLoadedResponse.self, from: data)
                for model in decoded.models ?? [] {
                    if let name = Self.trimmed(model.name) ?? Self.trimmed(model.model) {
                        loaded.append(
                            LocalServerLoadedModel(
                                name,
                                max(0, model.size ?? 0),
                                contextTokens: min(max(model.contextLength ?? 0, 0), Int(Int32.max))))
                    }
                }
            }
        } catch {
            loaded.removeAll()
        }

        return LocalServerState(reach: .reached, models: Self.sorted(models), loaded: loaded, failureDetail: nil)
    }

    private func readLMStudio(_ root: URL, apiKey: String?) async throws -> LocalServerState {
        var request = self.request(.get, url: Self.relativeURL("/api/v1/models", to: root), apiKey: apiKey)
        request.timeoutInterval = Self.listTimeout
        let (data, response) = try await send(request)
        if Self.refused(response) {
            return .needsKey
        }
        guard response.statusCode >= 200 && response.statusCode < 300 else {
            return LocalServerState(
                reach: .failed,
                models: [],
                loaded: [],
                failureDetail: "HTTP \(response.statusCode) from the model list")
        }

        let decoded = try JSONDecoder().decode(LMStudioModelsResponse.self, from: data)
        var models: [LocalServerModel] = []
        var loaded: [LocalServerLoadedModel] = []
        for model in decoded.models ?? [] {
            guard model.type?.caseInsensitiveCompare("llm") == .orderedSame,
                let key = Self.trimmed(model.key)
            else {
                continue
            }

            let size = max(0, model.sizeBytes ?? 0)
            models.append(
                LocalServerModel(
                    key,
                    Self.trimmed(model.displayName) ?? key,
                    size,
                    maxContextTokens: min(max(model.maxContextLength ?? 0, 0), Int(Int32.max))))

            let instances = model.loadedInstances ?? []
            if let first = instances.first {
                loaded.append(Self.instance(for: key, size: size, instance: first))
            }
            for instance in instances {
                if let id = Self.trimmed(instance.id), !Self.sameModel(id, key) {
                    loaded.append(Self.instance(for: id, size: size, instance: instance))
                }
            }
        }

        return LocalServerState(reach: .reached, models: Self.sorted(models), loaded: loaded, failureDetail: nil)
    }

    private func request(_ method: HTTPMethod, url: URL, apiKey: String?, json: Any? = nil) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if json != nil {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try? JSONSerialization.data(withJSONObject: json as Any)
        }
        if let apiKey = Self.trimmed(apiKey), !apiKey.isEmpty {
            let bearer = "Bearer " + apiKey
            request.setValue(bearer, forHTTPHeaderField: "Authorization")
        }
        return request
    }

    private func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let response: URLResponse
        let data: Data
        do {
            (data, response) = try await CleanupHTTP.send(
                request, session: session, receipt: CleanupSendContext.receipt)
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as URLError {
            throw error
        } catch {
            throw error
        }

        guard let http = response as? HTTPURLResponse else {
            throw URLError(.badServerResponse)
        }
        return (data, http)
    }

    private struct Root: Sendable {
        let app: LocalServerApp
        let url: URL
    }

    private func roots(for endpoint: String) -> [Root] {
        let app = LocalAiServer.appAt(endpoint)
        guard app != .none,
            let uri = URL(string: endpoint.trimmingCharacters(in: .whitespacesAndNewlines)),
            let scheme = uri.scheme?.lowercased(),
            let port = uri.port
        else {
            return []
        }

        let originalHost = uri.host(percentEncoded: false)?.lowercased()
        var hosts: [String] = []
        let orderedHosts = [originalHost, Self.loopbackHosts[0], Self.loopbackHosts[1], "[::1]"]
            .compactMap { $0 }
        for host in orderedHosts {
            if !hosts.contains(where: {
                Self.normalizedHost($0).caseInsensitiveCompare(Self.normalizedHost(host)) == .orderedSame
            }) {
                hosts.append(host)
            }
        }

        return hosts.compactMap { host in
            let authority = host.contains(":") ? host : host.lowercased()
            guard let url = URL(string: "\(scheme)://\(authority):\(port)") else {
                return nil
            }
            return Root(app: app, url: url)
        }
    }

    private enum FirstAnswerError: Error {
        case notRunning
    }

    private func firstAnswer<Value: Sendable>(
        _ roots: [Root],
        request: @escaping @Sendable (Root) async throws -> Value
    ) async throws -> Value {
        try await withThrowingTaskGroup(of: Value?.self) { group in
            for root in roots {
                group.addTask {
                    do {
                        return try await request(root)
                    } catch let error as URLError where Self.isConnectionFailure(error) {
                        return nil
                    } catch is CancellationError {
                        throw CancellationError()
                    } catch {
                        throw error
                    }
                }
            }

            while let result = try await group.next() {
                if let result {
                    group.cancelAll()
                    return result
                }
            }
            throw FirstAnswerError.notRunning
        }
    }

    private static func isConnectionFailure(_ error: URLError) -> Bool {
        switch error.code {
        case .cannotConnectToHost, .networkConnectionLost, .cannotFindHost, .dnsLookupFailed, .notConnectedToInternet,
            .timedOut:
            return true
        default:
            return false
        }
    }

    private static func sorted(_ models: [LocalServerModel]) -> [LocalServerModel] {
        models.sorted { left, right in
            let byName = left.displayName.localizedCaseInsensitiveCompare(right.displayName)
            if byName == .orderedSame {
                return left.id < right.id
            }
            return byName == .orderedAscending
        }
    }

    private static func canChat(_ capabilities: [String]?) -> Bool {
        guard let capabilities else {
            return true
        }
        return capabilities.contains { $0.caseInsensitiveCompare("completion") == .orderedSame }
    }

    private static func instance(for name: String, size: Int64, instance: LMStudioModelsResponse.Model.LoadedInstance)
        -> LocalServerLoadedModel
    {
        LocalServerLoadedModel(
            name,
            size,
            contextTokens: min(max(instance.config?.contextLength ?? 0, 0), Int(Int32.max)),
            instanceID: trimmed(instance.id),
            remainingTTLSeconds: instance.remainingTTLSeconds)
    }

    private static func jsonObject(from data: Data) throws -> [String: Any] {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw URLError(.cannotParseResponse)
        }
        return object
    }

    private static func array(_ value: Any?) -> [[String: Any]] {
        value as? [[String: Any]] ?? []
    }

    private static func text(_ value: Any?) -> String? {
        guard let value = value as? String else {
            return nil
        }
        return trimmed(value)
    }

    private static func int64(_ value: Any?) -> Int64 {
        switch value {
        case let number as NSNumber:
            return number.int64Value
        case let value as Int64:
            return value
        case let value as Int:
            return Int64(value)
        default:
            return 0
        }
    }

    private static func trimmed(_ value: String?) -> String? {
        guard let value else {
            return nil
        }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    private static func relativeURL(_ path: String, to root: URL) -> URL {
        URL(string: path, relativeTo: root)?.absoluteURL ?? root
    }

    private static func normalizedHost(_ host: String) -> String {
        host.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
    }
}

extension LocalServerClient {
    fileprivate enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
    }

    fileprivate final class RedirectRefusingURLSessionDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
        func urlSession(
            _ session: URLSession,
            task: URLSessionTask,
            willPerformHTTPRedirection response: HTTPURLResponse,
            newRequest request: URLRequest,
            completionHandler: @escaping (URLRequest?) -> Void
        ) {
            completionHandler(nil)
        }
    }

    fileprivate struct OllamaTagsResponse: Decodable {
        fileprivate struct Model: Decodable {
            let name: String?
            let size: Int64?
            let capabilities: [String]?
        }

        let models: [Model]?
    }

    fileprivate struct OllamaLoadedResponse: Decodable {
        fileprivate struct Model: Decodable {
            let name: String?
            let model: String?
            let size: Int64?
            let contextLength: Int?

            enum CodingKeys: String, CodingKey {
                case name
                case model
                case size
                case contextLength = "context_length"
            }
        }

        let models: [Model]?
    }

    fileprivate struct LMStudioModelsResponse: Decodable {
        fileprivate struct Model: Decodable {
            fileprivate struct LoadedInstance: Decodable {
                fileprivate struct Config: Decodable {
                    let contextLength: Int?

                    enum CodingKeys: String, CodingKey {
                        case contextLength = "context_length"
                    }
                }

                let id: String?
                let config: Config?
                let remainingTTLSeconds: Int64?

                enum CodingKeys: String, CodingKey {
                    case id
                    case config
                    case remainingTTLSeconds = "remaining_ttl_seconds"
                }
            }

            let type: String?
            let key: String?
            let displayName: String?
            let sizeBytes: Int64?
            let maxContextLength: Int?
            let loadedInstances: [LoadedInstance]?

            enum CodingKeys: String, CodingKey {
                case type
                case key
                case displayName = "display_name"
                case sizeBytes = "size_bytes"
                case maxContextLength = "max_context_length"
                case loadedInstances = "loaded_instances"
            }
        }

        let models: [Model]?
    }

    fileprivate static func refused(_ response: HTTPURLResponse) -> Bool {
        response.statusCode == 401 || response.statusCode == 403
    }
}
