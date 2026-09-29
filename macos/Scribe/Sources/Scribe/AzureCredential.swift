import Foundation
import os

/// How Scribe authenticates to Microsoft Foundry cloud. Mirrors Windows' `Scribe.Core.Models.AzureAuthMode`.
enum AzureAuthMode: String, Sendable {
    /// The user's own `az login` session (default): no secrets for Scribe to hold at all.
    case azureCli
    /// An Entra app registration, for a pinned identity independent of whichever account `az` has active, or on a
    /// machine where the Azure CLI isn't installed.
    case servicePrincipal
}

/// Entra ID app registration credentials for the Microsoft Foundry provider. The secret is only ever read from the
/// Keychain (see `SecretStore`), never from an environment variable, a `.env` file, or a script on disk. Printing one
/// shows neither the secret nor the ids.
struct AzureServicePrincipal: Sendable, CustomStringConvertible, CustomReflectable {
    let tenantId: String
    let clientId: String
    let clientSecret: String

    /// Builds a service principal from the pieces, or `nil` when any of them is blank, so a half-filled configuration
    /// becomes a Settings message rather than an opaque Entra error.
    static func tryCreate(tenantId: String?, clientId: String?, clientSecret: String?) -> AzureServicePrincipal? {
        guard
            let tenantId = tenantId?.trimmingCharacters(in: .whitespacesAndNewlines), !tenantId.isEmpty,
            let clientId = clientId?.trimmingCharacters(in: .whitespacesAndNewlines), !clientId.isEmpty,
            let clientSecret, !clientSecret.isEmpty
        else {
            return nil
        }
        return AzureServicePrincipal(tenantId: tenantId, clientId: clientId, clientSecret: clientSecret)
    }

    var description: String { "AzureServicePrincipal" }
    var customMirror: Mirror { Mirror(self, children: [:]) }
}

/// One access token for a resource scope, and when it stops being usable. Printing one shows only the expiry.
struct AzureAccessToken: Sendable, Equatable, CustomStringConvertible, CustomReflectable {
    /// A token closer than this to its expiry is fetched again, so a request never starts with one that lapses in
    /// flight.
    static let expirySlack: TimeInterval = 60

    let token: String
    let expiresAt: Date

    var description: String { "AzureAccessToken(expiresAt: \(expiresAt))" }
    var customMirror: Mirror { Mirror(self, children: ["expiresAt": expiresAt]) }
}

/// An Entra `AADSTS` error number, as Entra or `az` reported it.
///
/// Printing, interpolating or dumping one shows the code only when `FailureShape` lists that exact code, and `AADSTS`
/// alone otherwise, just as a failure shape does: the digits of an unlisted code are the service's to choose, so they
/// reach nothing but the Settings text (`AzureCredentialError.settingsDetail`). Not an integer either, so a failure
/// shape never writes it among an error's values.
struct EntraErrorCode: Sendable, Hashable, ExpressibleByIntegerLiteral, CustomStringConvertible,
    CustomDebugStringConvertible, CustomReflectable
{
    let number: Int

    init(_ number: Int) {
        self.number = number
    }

    init(integerLiteral value: Int) {
        self.init(value)
    }

    /// `AADSTS` and the number, as Entra writes it.
    var code: String { "AADSTS\(number)" }

    /// Whether `FailureShape` lists this exact code, which is what makes it safe to write anywhere.
    var isListed: Bool { FailureShape.knownServiceCodes.contains(code) }

    var description: String { isListed ? code : "AADSTS" }
    var debugDescription: String { description }
    var customMirror: Mirror { Mirror(self, children: [:]) }
}

/// Why no access token could be had, in a form that is safe to log: no `az` output, no Entra response text, no tenant
/// or client id. What `az` said is reduced to an `AzureCliFailureReason` and an Entra error number, and the rest is
/// dropped unread. The number reaches this description, like a failure shape, only when `FailureShape` lists its
/// `AADSTS` code; an unlisted code's digits are the service's to choose, so only the Settings text shows them
/// (`settingsDetail`).
enum AzureCredentialError: Error, LocalizedError, FailureShapeDetailing, Equatable {
    /// `az` is not in Homebrew's prefixes or on `PATH`.
    case cliNotFound
    /// `az` could not be started. `errno` is the POSIX error.
    case cliLaunchFailed(errno: Int32)
    /// `az` did not answer within `AzureCliCredentialProvider.timeout` and was stopped.
    case cliTimedOut
    /// `az` exited without a token.
    case cliFailed(exitStatus: Int32?, reason: AzureCliFailureReason, aadsts: EntraErrorCode?)
    /// `az` printed something other than a token.
    case cliOutputUnparseable
    /// The tenant id cannot name a tenant, so nothing was sent.
    case tenantInvalid
    /// Entra's token endpoint could not be reached. The URL error carries only its code.
    case tokenEndpointUnreachable(URLError)
    case tokenEndpointTimedOut
    /// Entra refused the service principal, with its `AADSTS` code when it sent one.
    case tokenRejected(status: Int, aadsts: EntraErrorCode?, reply: CleanupServiceReply)
    /// Entra answered with something other than a token.
    case tokenResponseUnparseable

    var failureHTTPStatus: Int? {
        guard case .tokenRejected(let status, _, _) = self else { return nil }
        return status
    }

    /// The code as the service sent it; `FailureShape` writes it only when it lists it, and an unlisted Entra code as
    /// `AADSTS` alone.
    var failureServiceCode: String? {
        switch self {
        case .cliFailed(_, _, let aadsts?):
            return aadsts.code
        case .tokenRejected(_, let aadsts, let reply):
            return aadsts?.code ?? reply.code
        default:
            return nil
        }
    }

    var errorDescription: String? {
        switch self {
        case .cliNotFound:
            return "Azure CLI ('az') was not found. Install it with 'brew install azure-cli' and run 'az login', or "
                + "switch to service principal authentication."
        case .cliLaunchFailed:
            return "Scribe could not start Azure CLI ('az')."
        case .cliTimedOut:
            return "Azure CLI did not return a token within a minute."
        case .cliFailed(let exitStatus, let reason, let aadsts):
            switch reason {
            case .notSignedIn:
                return "Azure CLI is not signed in. Run 'az login' in Terminal, then try again."
            case .signInRejected:
                let code = Self.listedCode(aadsts).map { " (\($0))" } ?? ""
                return "Azure CLI's sign-in was refused by Microsoft Entra\(code). Run 'az login' in Terminal to sign "
                    + "in again, then try again."
            case .other:
                let status = exitStatus.map { " (exit status \($0))" } ?? ""
                return "Azure CLI could not get an access token\(status). Run 'az account get-access-token "
                    + "--resource https://ai.azure.com' in Terminal to see why."
            }
        case .cliOutputUnparseable:
            return "Azure CLI returned a response Scribe could not read."
        case .tenantInvalid:
            return "The tenant ID must be a directory (tenant) ID or a domain name."
        case .tokenEndpointUnreachable:
            return "Scribe could not reach Microsoft Entra to get a token. Check the network connection."
        case .tokenEndpointTimedOut:
            return "Microsoft Entra did not answer in time."
        case .tokenRejected(let status, let aadsts, let reply):
            let code = Self.codeText(status: status, aadsts: aadsts, reply: reply)
            return "Microsoft Entra rejected the service principal (\(code)): "
                + Self.entraHint(aadsts: aadsts, code: reply.code)
        case .tokenResponseUnparseable:
            return "Microsoft Entra returned a response Scribe could not read."
        }
    }

    /// An Entra code as Scribe may write it anywhere: the code when `FailureShape` lists it, otherwise nothing.
    static func listedCode(_ aadsts: EntraErrorCode?) -> String? {
        guard let aadsts, aadsts.isListed else { return nil }
        return aadsts.code
    }

    /// The Entra code behind a refused sign-in when Scribe does not list it, such as `AADSTS1234567`, for
    /// `CleanupFailureText.forSettings` and nothing else: the user can look it up, and the screen is not a log. A
    /// listed code is in the description already.
    var settingsDetail: String? {
        let aadsts: EntraErrorCode?
        switch self {
        case .cliFailed(_, _, let code), .tokenRejected(_, let code, _):
            aadsts = code
        default:
            aadsts = nil
        }
        guard let aadsts, !aadsts.isListed else { return nil }
        return aadsts.code
    }

    /// The Entra code when Scribe lists it, else the OAuth error code when Scribe lists it, else the HTTP status.
    private static func codeText(status: Int, aadsts: EntraErrorCode?, reply: CleanupServiceReply) -> String {
        if let code = listedCode(aadsts) {
            return code
        }
        if let code = reply.code, FailureShape.knownServiceCodes.contains(code) {
            return code
        }
        return "HTTP \(status)"
    }

    /// What to do about the refusal, chosen by its Entra number, following Windows' `AzureSignInDiagnostics`. The
    /// words are Scribe's whichever number chose them. AADSTS900023 is not in Microsoft's error code reference, so
    /// `FailureShape` does not list it, but Entra sends it for a tenant id that is neither a GUID nor a domain.
    private static func entraHint(aadsts: EntraErrorCode?, code: String?) -> String {
        switch aadsts?.number ?? 0 {
        case 7_000_215:
            // Windows saw a secret created seconds before fail with this code, then work about thirty seconds later.
            return "the client secret was not accepted. A secret created in the last minute or two can take a "
                + "moment to become active, so wait and try again; otherwise check that you saved the secret's "
                + "Value, not its Secret ID."
        case 7_000_222:
            return "the client secret has expired. Create a new one for the app registration and save its Value."
        case 700_016:
            return "the application was not found in this tenant. Check the directory (tenant) ID and the "
                + "application (client) ID, and that the app registration lives in that directory."
        case 90_002:
            return "the tenant was not found. Check the directory (tenant) ID."
        case 900_023:
            return "the tenant ID is neither a directory (tenant) ID nor a domain name. Copy the directory (tenant) "
                + "ID from the app registration's overview page."
        case 50_034:
            return "no service principal exists for the application in this tenant. The app registration may have "
                + "been created in a different directory."
        default:
            break
        }
        if code == "unauthorized_client" {
            return "the app registration is not allowed to use client credentials."
        }
        return "check the tenant ID, client ID and client secret, and that the secret has not expired."
    }
}

/// What `az` said when it gave no token, reduced to what Scribe can act on. Its words are read once, in memory, and
/// dropped. An `Error` of its own, so a failure shape names it after the error that carries it
/// (`inner=AzureCliFailureReason.notSignedIn`).
enum AzureCliFailureReason: Error, Equatable, Sendable {
    /// "Please run 'az login' to setup account."
    case notSignedIn
    /// An Entra error (`AADSTS...`): a sign-in that expired, or one that needs multi-factor authentication again.
    case signInRejected
    case other

    static func classify(standardError text: String) -> (reason: AzureCliFailureReason, aadsts: EntraErrorCode?) {
        if let number = aadstsCode(in: text) {
            return (.signInRejected, EntraErrorCode(number))
        }
        if text.range(of: "az login", options: .caseInsensitive) != nil {
            return (.notSignedIn, nil)
        }
        return (.other, nil)
    }

    /// The number of the first `AADSTS` code in `text`, taken only as Entra writes one: a word of its own with no
    /// leading zero, so a longer run of digits or letters is never cut down to a code it was not.
    static func aadstsCode(in text: String) -> Int? {
        guard let range = text.range(of: "\\bAADSTS[1-9][0-9]{3,8}\\b", options: .regularExpression) else {
            return nil
        }
        return Int(text[range].dropFirst("AADSTS".count))
    }
}

/// Resolves a bearer token for a resource scope. Two implementations mirror Windows' two `AzureAuthMode` values;
/// there is deliberately no `DefaultAzureCredential`-style chain here, for the same reason Windows avoids one: which
/// credential wins in a chain can't be guaranteed ahead of time, and the one concrete choice the user made is more
/// predictable than credentials probed and skipped at run time.
protocol AzureCredentialProvider: Sendable {
    func accessToken(scope: String) async throws -> AzureAccessToken
}

// MARK: - One lane for Azure CLI

/// Runs one piece of work at a time, in the order callers arrive.
///
/// Every `az` launch in the process goes through one lane, `AzureCliCredentialProvider.processLane`, the macOS side of
/// Windows' `AzureCliProcessCoordinator`: `az` keeps one token cache, and processes racing on it time out on machines
/// signed in to several tenants. An actor alone would not serialize them, because an actor takes its next call whenever
/// the one it runs is suspended, and a launch is exactly such a suspension.
///
/// A caller cancelled while it waits leaves the queue at once with `CancellationError`. A caller that `leave()` has
/// already handed the lane to is no longer waiting: it runs its work even when its cancellation arrives a moment later,
/// so that work has to observe cancellation itself. The `az` launch does, through `ProcessRunner`, which stops the
/// child when the task is cancelled; anything else put through a lane must do the same.
final class AsyncLane: Sendable {
    private struct Waiter: Sendable {
        let ticket: UInt64
        let continuation: CheckedContinuation<Void, any Error>
    }

    private struct Observer: Sendable {
        let count: Int
        let continuation: CheckedContinuation<Void, Never>
    }

    private struct State: Sendable {
        var isHeld = false
        var waiters: [Waiter] = []
        var observers: [Observer] = []
        var nextTicket: UInt64 = 0
        /// Tickets handed out whose caller has not queued yet, and those of them cancelled in the meantime.
        var arriving: Set<UInt64> = []
        var cancelledOnArrival: Set<UInt64> = []
    }

    private enum Arrival: Sendable {
        case acquired
        case cancelled
        case queued(ready: [Observer])
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    init() {}

    func run<Value: Sendable>(_ work: @Sendable () async throws -> Value) async throws -> Value {
        try await enter()
        defer { leave() }
        return try await work()
    }

    /// How many callers are waiting for the lane now, not counting the one holding it.
    var waitingCount: Int {
        state.withLock { $0.waiters.count }
    }

    /// Returns once at least `count` callers are waiting, so a test can order its steps without sleeping.
    func waitUntilWaiting(atLeast count: Int) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let alreadyThere = state.withLock { current -> Bool in
                guard current.waiters.count < count else { return true }
                current.observers.append(Observer(count: count, continuation: continuation))
                return false
            }
            if alreadyThere {
                continuation.resume()
            }
        }
    }

    private func enter() async throws {
        try Task.checkCancellation()
        let ticket = state.withLock { current -> UInt64 in
            current.nextTicket += 1
            current.arriving.insert(current.nextTicket)
            return current.nextTicket
        }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                // Deciding and queueing under one lock: a holder leaving between the two could otherwise find no one
                // to hand the lane to and leave this caller waiting for good.
                let arrival = state.withLock { current -> Arrival in
                    current.arriving.remove(ticket)
                    if current.cancelledOnArrival.remove(ticket) != nil {
                        return .cancelled
                    }
                    guard current.isHeld else {
                        current.isHeld = true
                        return .acquired
                    }
                    current.waiters.append(Waiter(ticket: ticket, continuation: continuation))
                    let waiting = current.waiters.count
                    let ready = current.observers.filter { $0.count <= waiting }
                    current.observers.removeAll { $0.count <= waiting }
                    return .queued(ready: ready)
                }
                switch arrival {
                case .acquired:
                    continuation.resume()
                case .cancelled:
                    continuation.resume(throwing: CancellationError())
                case .queued(let ready):
                    for observer in ready {
                        observer.continuation.resume()
                    }
                }
            }
        } onCancel: {
            let waiter = state.withLock { current -> Waiter? in
                if let index = current.waiters.firstIndex(where: { $0.ticket == ticket }) {
                    return current.waiters.remove(at: index)
                }
                if current.arriving.contains(ticket) {
                    current.cancelledOnArrival.insert(ticket)
                }
                // Otherwise the lane was handed to this caller already, and its work sees the cancellation itself.
                return nil
            }
            waiter?.continuation.resume(throwing: CancellationError())
        }
    }

    /// Hands the lane straight to the next caller, so no one arriving later can take it in between.
    private func leave() {
        let next = state.withLock { current -> Waiter? in
            guard !current.waiters.isEmpty else {
                current.isHeld = false
                return nil
            }
            return current.waiters.removeFirst()
        }
        next?.continuation.resume()
    }
}

// MARK: - Azure CLI

/// The `az` invocation for one token: the executable and the arguments after it, resolved together, so the lookup can
/// never find one program while the arguments assume another (a `/usr/bin/env` fallback once ran
/// `env account get-access-token`, which never names `az`).
struct AzureCliCommand: Equatable, Sendable {
    let executableURL: URL
    let arguments: [String]

    /// `az account get-access-token` for `resource`, with `az` found by name in `searchPath` (Homebrew's prefixes,
    /// then `PATH`; see `ProcessRunner.defaultSearchPath`).
    static func accessToken(resource: String, tenantId: String?, searchPath: [String]) throws -> AzureCliCommand {
        guard let az = ProcessRunner.locateExecutable(named: "az", searchPath: searchPath) else {
            throw AzureCredentialError.cliNotFound
        }
        var arguments = ["account", "get-access-token", "--resource", resource, "--output", "json"]
        if let tenantId {
            arguments += ["--tenant", tenantId]
        }
        return AzureCliCommand(executableURL: az, arguments: arguments)
    }
}

/// Authenticates with the user's own `az login` session by running `az account get-access-token` through
/// `ProcessRunner` (no blocked thread, a deadline, stopped with its task) and through `processLane`. The token is kept
/// until `AzureAccessToken.expirySlack` before it expires, and `CleanupProviderCache` keeps this provider while the
/// tenant stays the same, so `az` runs about once an hour rather than once per dictation.
actor AzureCliCredentialProvider: AzureCredentialProvider {
    typealias Launch = @Sendable (AzureCliCommand) async throws -> ProcessRunner.Outcome

    /// Every `az` launch in Scribe waits its turn here.
    static let processLane = AsyncLane()
    static let timeout: Duration = .seconds(60)
    static let launchThroughProcessRunner: Launch = { command in
        try await ProcessRunner.run(
            command.executableURL, arguments: command.arguments, timeout: AzureCliCredentialProvider.timeout)
    }

    private struct CachedToken: Sendable {
        let scope: String
        let token: AzureAccessToken
    }

    private let tenantId: String?
    private let searchPath: [String]
    private let lane: AsyncLane
    private let launch: Launch
    private let now: @Sendable () -> Date
    private var cached: CachedToken?

    init(
        tenantId: String? = nil,
        searchPath: [String] = ProcessRunner.defaultSearchPath(),
        lane: AsyncLane = AzureCliCredentialProvider.processLane,
        launch: @escaping Launch = AzureCliCredentialProvider.launchThroughProcessRunner,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.tenantId = tenantId
        self.searchPath = searchPath
        self.lane = lane
        self.launch = launch
        self.now = now
    }

    func accessToken(scope: String) async throws -> AzureAccessToken {
        if let token = freshToken(for: scope) {
            return token
        }
        return try await lane.run {
            // Another request may have fetched a token while this one waited for the lane.
            if let token = await self.freshToken(for: scope) {
                return token
            }
            let token = try await self.requestToken(scope: scope)
            await self.remember(token, for: scope)
            return token
        }
    }

    private func freshToken(for scope: String) -> AzureAccessToken? {
        guard let cached, cached.scope == scope,
            cached.token.expiresAt > now().addingTimeInterval(AzureAccessToken.expirySlack)
        else {
            return nil
        }
        return cached.token
    }

    private func remember(_ token: AzureAccessToken, for scope: String) {
        cached = CachedToken(scope: scope, token: token)
    }

    private func requestToken(scope: String) async throws -> AzureAccessToken {
        let command = try AzureCliCommand.accessToken(
            resource: Self.resource(fromScope: scope), tenantId: tenantId, searchPath: searchPath)
        let outcome: ProcessRunner.Outcome
        do {
            outcome = try await launch(command)
        } catch is CancellationError {
            throw CancellationError()
        } catch ProcessRunnerError.launchFailed(errno: let code) {
            throw AzureCredentialError.cliLaunchFailed(errno: code)
        } catch {
            throw AzureCredentialError.cliLaunchFailed(errno: 0)
        }

        switch outcome.terminationReason {
        case .cancelled:
            throw CancellationError()
        case .timedOut:
            throw AzureCredentialError.cliTimedOut
        case .finished:
            break
        }
        guard outcome.exitStatus == 0 else {
            let failure = AzureCliFailureReason.classify(standardError: outcome.standardError.text)
            throw AzureCredentialError.cliFailed(
                exitStatus: outcome.exitStatus, reason: failure.reason, aadsts: failure.aadsts)
        }
        guard let token = AzureCliAccessTokenParser.parse(outcome.standardOutput.data, now: now()) else {
            throw AzureCredentialError.cliOutputUnparseable
        }
        ScribeLog.debug(.cleanup, "Azure CLI returned a token", .duration("elapsed", outcome.duration))
        return token
    }

    /// Azure CLI's `--resource` flag wants the bare resource URL, not the OAuth 2.0 `.default` scope suffix.
    static func resource(fromScope scope: String) -> String {
        scope.hasSuffix("/.default") ? String(scope.dropLast("/.default".count)) : scope
    }
}

/// Parses `az account get-access-token --output json`. A standalone enum so it can be tested against fixed JSON, a
/// fixed clock and a fixed time zone without a real `az`.
enum AzureCliAccessTokenParser {
    static func parse(_ data: Data, now: Date = Date(), timeZone: TimeZone = .current) -> AzureAccessToken? {
        guard
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let token = object["accessToken"] as? String, !token.isEmpty
        else {
            return nil
        }

        // `expires_on` (seconds since 1970, in newer Azure CLI versions) says what `expiresOn` (local time without a
        // zone) says, without the ambiguity, so it wins when both are there.
        if let epoch = epochSeconds(object["expires_on"]) {
            return AzureAccessToken(token: token, expiresAt: Date(timeIntervalSince1970: epoch))
        }
        if let text = object["expiresOn"] as? String, let date = localDate(text, timeZone: timeZone) {
            return AzureAccessToken(token: token, expiresAt: date)
        }
        // An expiry Scribe cannot read gets a conservative five minutes rather than failing a working sign-in.
        return AzureAccessToken(token: token, expiresAt: now.addingTimeInterval(300))
    }

    private static func epochSeconds(_ value: Any?) -> TimeInterval? {
        switch value {
        case let number as NSNumber:
            return number.doubleValue
        case let text as String:
            return TimeInterval(text)
        default:
            return nil
        }
    }

    private static func localDate(_ text: String, timeZone: TimeZone) -> Date? {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        for format in ["yyyy-MM-dd HH:mm:ss.SSSSSS", "yyyy-MM-dd HH:mm:ss"] {
            formatter.dateFormat = format
            if let date = formatter.date(from: text) {
                return date
            }
        }
        return nil
    }
}

// MARK: - Service principal

/// A tenant id Scribe will put into a URL path or pass to `az`: a directory (tenant) GUID or a domain name, made of
/// ASCII letters, digits, dots and hyphens. Anything else could change the path it goes into, or reach `az` as an
/// option, so it is refused before anything is sent.
enum AzureTenant {
    static func isValid(_ tenantId: String) -> Bool {
        let scalars = tenantId.unicodeScalars
        guard !scalars.isEmpty, scalars.count <= 253, !tenantId.hasPrefix("-"), !tenantId.hasPrefix(".") else {
            return false
        }
        return scalars.allSatisfy { scalar in
            switch scalar {
            case "a"..."z", "A"..."Z", "0"..."9", ".", "-":
                return true
            default:
                return false
            }
        }
    }
}

/// `application/x-www-form-urlencoded` bodies, for the OAuth 2.0 token request.
///
/// Every byte outside ALPHA, DIGIT and `-._~` is percent-encoded, space and `+` included, so a secret holding `+`,
/// `&`, `=`, `%` or a space reaches Entra exactly as typed. `URLComponents.percentEncodedQuery` leaves `+` alone,
/// and a form decoder reads that as a space.
enum FormURLEncoding {
    static func body(_ fields: [(name: String, value: String)]) -> Data {
        Data(fields.map { encode($0.name) + "=" + encode($0.value) }.joined(separator: "&").utf8)
    }

    static func encode(_ text: String) -> String {
        var encoded = ""
        encoded.reserveCapacity(text.utf8.count)
        for byte in text.utf8 {
            if isUnreserved(byte) {
                encoded.unicodeScalars.append(Unicode.Scalar(byte))
            } else {
                encoded.unicodeScalars.append("%")
                encoded.unicodeScalars.append(Unicode.Scalar(hexDigits[Int(byte >> 4)]))
                encoded.unicodeScalars.append(Unicode.Scalar(hexDigits[Int(byte & 0x0F)]))
            }
        }
        return encoded
    }

    private static let hexDigits: [UInt8] = Array("0123456789ABCDEF".utf8)

    private static func isUnreserved(_ byte: UInt8) -> Bool {
        switch byte {
        case UInt8(ascii: "A")...UInt8(ascii: "Z"), UInt8(ascii: "a")...UInt8(ascii: "z"),
            UInt8(ascii: "0")...UInt8(ascii: "9"):
            return true
        case UInt8(ascii: "-"), UInt8(ascii: "."), UInt8(ascii: "_"), UInt8(ascii: "~"):
            return true
        default:
            return false
        }
    }
}

/// What Entra's token endpoint says when it refuses: `{"error": "invalid_client", "error_description": "...",
/// "error_codes": [7000215]}`. Only the error code and the number are kept. The description repeats the app and
/// tenant ids and carries trace ids, so it is dropped unread.
struct EntraTokenError: Sendable {
    let aadsts: EntraErrorCode?
    let reply: CleanupServiceReply

    init(body data: Data) {
        let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        aadsts = (object?["error_codes"] as? [Int])?.first.map { EntraErrorCode($0) }
        reply = CleanupServiceReply(code: object?["error"] as? String, message: nil)
    }
}

/// Authenticates as an Entra app registration with the OAuth 2.0 client-credentials grant, called over REST (there is
/// no Swift Azure Identity SDK). One request at a time goes to Entra, and the token is kept until
/// `AzureAccessToken.expirySlack` before it expires; `CleanupProviderCache` keeps the provider while the identity stays
/// the same, so Entra is asked about once an hour, not once per dictation. Microsoft warns that an app which does not
/// reuse credentials draws HTTP 429 throttling from Entra.
actor AzureServicePrincipalCredentialProvider: AzureCredentialProvider {
    static let authorityHost = "login.microsoftonline.com"

    private struct CachedToken: Sendable {
        let scope: String
        let token: AzureAccessToken
    }

    private let principal: AzureServicePrincipal
    private let session: URLSession
    private let now: @Sendable () -> Date
    private let lane = AsyncLane()
    private var cached: CachedToken?

    init(
        principal: AzureServicePrincipal,
        session: URLSession = CleanupProviderFactory.cleanupSession,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.principal = principal
        self.session = session
        self.now = now
    }

    func accessToken(scope: String) async throws -> AzureAccessToken {
        if let token = freshToken(for: scope) {
            return token
        }
        return try await lane.run {
            if let token = await self.freshToken(for: scope) {
                return token
            }
            let token = try await self.requestToken(scope: scope)
            await self.remember(token, for: scope)
            return token
        }
    }

    /// `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`, or `nil` for a tenant id that is not valid.
    static func tokenURL(tenantId: String) -> URL? {
        guard AzureTenant.isValid(tenantId) else { return nil }
        var components = URLComponents()
        components.scheme = "https"
        components.host = authorityHost
        components.path = "/\(tenantId)/oauth2/v2.0/token"
        return components.url
    }

    private func freshToken(for scope: String) -> AzureAccessToken? {
        guard let cached, cached.scope == scope,
            cached.token.expiresAt > now().addingTimeInterval(AzureAccessToken.expirySlack)
        else {
            return nil
        }
        return cached.token
    }

    private func remember(_ token: AzureAccessToken, for scope: String) {
        cached = CachedToken(scope: scope, token: token)
    }

    private func requestToken(scope: String) async throws -> AzureAccessToken {
        guard let url = Self.tokenURL(tenantId: principal.tenantId) else {
            throw AzureCredentialError.tenantInvalid
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = FormURLEncoding.body([
            (name: "grant_type", value: "client_credentials"),
            (name: "client_id", value: principal.clientId),
            (name: "client_secret", value: principal.clientSecret),
            (name: "scope", value: scope),
        ])

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw Self.transportFailure(error)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw AzureCredentialError.tokenResponseUnparseable
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            let refusal = EntraTokenError(body: data)
            throw AzureCredentialError.tokenRejected(
                status: httpResponse.statusCode, aadsts: refusal.aadsts, reply: refusal.reply)
        }
        guard
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let token = object["access_token"] as? String, !token.isEmpty
        else {
            throw AzureCredentialError.tokenResponseUnparseable
        }
        let lifetime = Self.seconds(object["expires_in"]) ?? 3600
        ScribeLog.debug(.cleanup, "Microsoft Entra returned a token")
        return AzureAccessToken(token: token, expiresAt: now().addingTimeInterval(lifetime))
    }

    private static func seconds(_ value: Any?) -> TimeInterval? {
        switch value {
        case let number as NSNumber:
            return number.doubleValue
        case let text as String:
            return TimeInterval(text)
        default:
            return nil
        }
    }

    /// A cancelled task stays a `CancellationError`, and a URL error keeps only its code, never the failing URL.
    private static func transportFailure(_ error: any Error) -> any Error {
        if error is CancellationError {
            return error
        }
        guard let urlError = error as? URLError else {
            return Task.isCancelled
                ? CancellationError() : AzureCredentialError.tokenEndpointUnreachable(URLError(.unknown))
        }
        if urlError.code == .cancelled, Task.isCancelled {
            return CancellationError()
        }
        if urlError.code == .timedOut {
            return AzureCredentialError.tokenEndpointTimedOut
        }
        return AzureCredentialError.tokenEndpointUnreachable(URLError(urlError.code))
    }
}

// MARK: - One credential per identity

/// Who Scribe signs in to Microsoft Foundry as. Equal identities share one credential and its token, so the provider
/// cache keeps the credential for one identity beside its provider (`CleanupProviderCacheState`), the macOS side of
/// Windows' `AzureCredentialFactory`: building a credential per dictation would ask `az` or Entra again every time. A
/// service principal's secret is represented by the store's `secretRevision`, never by its value. Printing or dumping
/// one shows only the auth mode, never the tenant or client id.
enum AzureIdentity: Hashable, Sendable, CustomStringConvertible, CustomReflectable {
    case azureCli(tenantId: String?)
    case servicePrincipal(tenantId: String, clientId: String, secretRevision: String)

    var description: String {
        switch self {
        case .azureCli: return "AzureIdentity(azureCli)"
        case .servicePrincipal: return "AzureIdentity(servicePrincipal)"
        }
    }

    var customMirror: Mirror { Mirror(self, children: [:]) }
}
