import Foundation

/// How Scribe authenticates to Microsoft Foundry cloud. Mirrors Windows'
/// `Scribe.Core.Models.AzureAuthMode`.
enum AzureAuthMode: String {
    /// The user's own `az login` session (default): no secrets for Scribe to hold at all.
    case azureCli
    /// An Entra app registration, for a pinned identity independent of whichever account `az` has
    /// active, or on a machine where the Azure CLI isn't installed.
    case servicePrincipal
}

/// Entra ID app registration credentials for the Microsoft Foundry provider. The secret is only
/// ever read from the Keychain (see `KeychainStore`), never from an environment variable, a
/// `.env` file, or a script on disk.
struct AzureServicePrincipal {
    let tenantId: String
    let clientId: String
    let clientSecret: String

    /// Builds a service principal from the pieces, or `nil` when any of them is blank, so callers
    /// can fall back to Azure CLI auth rather than failing with an opaque Entra error from a
    /// half-filled configuration.
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
}

/// One access token for a resource scope, plus when it stops being usable. Mirrors the shape of
/// `az account get-access-token`'s JSON output and an OAuth2 client-credentials token response.
struct AzureAccessToken {
    let token: String
    let expiresAt: Date
}

enum AzureCredentialError: Error, LocalizedError {
    case cliNotFound
    case cliFailed(String)
    case cliOutputUnparseable
    case tokenRequestFailed(String)
    case tokenResponseUnparseable

    var errorDescription: String? {
        switch self {
        case .cliNotFound:
            return "Azure CLI ('az') was not found. Install it, run 'az login', or switch to service principal authentication."
        case .cliFailed(let message):
            return "Azure CLI failed to produce an access token: \(message)"
        case .cliOutputUnparseable:
            return "Azure CLI returned a response Scribe could not parse."
        case .tokenRequestFailed(let message):
            return "Entra token request failed: \(message)"
        case .tokenResponseUnparseable:
            return "Entra returned a response Scribe could not parse."
        }
    }
}

/// Resolves a bearer token for a resource scope (e.g. `https://cognitiveservices.azure.com/.default`).
/// Two implementations mirror Windows' two `AzureAuthMode` values; there is deliberately no
/// `DefaultAzureCredential`-equivalent fallback chain here for the same reason Windows avoids one:
/// which credential wins in a chain can't be guaranteed ahead of time, and a single concrete choice
/// the user picked is more predictable than letting one get silently probed and skipped at runtime.
protocol AzureCredentialProvider {
    func accessToken(scope: String) async throws -> AzureAccessToken
}

/// Authenticates via the user's own `az login` session by shelling out to `az account
/// get-access-token`. An `actor` so concurrent cleanup requests naturally serialize on the one `az`
/// process rather than racing to launch several against `az`'s single shared token cache, mirroring
/// Windows' `AzureCliProcessCoordinator`.
actor AzureCliCredentialProvider: AzureCredentialProvider {
    private let tenantId: String?
    private var cached: AzureAccessToken?
    private var cachedScope: String?

    init(tenantId: String? = nil) {
        self.tenantId = tenantId
    }

    func accessToken(scope: String) async throws -> AzureAccessToken {
        // A minute of slack avoids handing back a token that expires mid-request.
        if let cached, cachedScope == scope, cached.expiresAt > Date().addingTimeInterval(60) {
            return cached
        }

        let token = try await requestToken(scope: scope)
        cached = token
        cachedScope = scope
        return token
    }

    private func requestToken(scope: String) async throws -> AzureAccessToken {
        var arguments = ["account", "get-access-token", "--resource", Self.resource(from: scope), "--output", "json"]
        if let tenantId {
            arguments += ["--tenant", tenantId]
        }

        let process = Process()
        process.executableURL = try Self.locateAzExecutable()
        process.arguments = arguments

        let stdout = Pipe()
        let stderrPipe = Pipe()
        process.standardOutput = stdout
        process.standardError = stderrPipe

        try process.run()
        process.waitUntilExit()

        let outputData = stdout.fileHandleForReading.readDataToEndOfFile()
        let errorData = stderrPipe.fileHandleForReading.readDataToEndOfFile()

        guard process.terminationStatus == 0 else {
            let message = String(data: errorData, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
            throw AzureCredentialError.cliFailed(message?.isEmpty == false ? message! : "exit code \(process.terminationStatus)")
        }

        guard let token = AzureCliAccessTokenParser.parse(outputData) else {
            throw AzureCredentialError.cliOutputUnparseable
        }
        return token
    }

    /// `az` isn't guaranteed to be on `PATH` for a GUI app launched from Finder/LaunchServices
    /// (unlike a Terminal-launched process, which inherits the shell's `PATH`), so the common
    /// Homebrew install locations are checked directly before falling back to `PATH` lookup via
    /// `/usr/bin/env`.
    private static func locateAzExecutable() throws -> URL {
        let candidates = ["/opt/homebrew/bin/az", "/usr/local/bin/az"]
        for candidate in candidates where FileManager.default.isExecutableFile(atPath: candidate) {
            return URL(fileURLWithPath: candidate)
        }
        // Fall back to PATH lookup for a Terminal-launched process or a non-Homebrew install.
        return URL(fileURLWithPath: "/usr/bin/env")
    }

    /// Azure CLI's `--resource` flag wants the bare resource URL, not the OAuth2 `.default` scope
    /// suffix used elsewhere in this file.
    private static func resource(from scope: String) -> String {
        scope.hasSuffix("/.default") ? String(scope.dropLast("/.default".count)) : scope
    }
}

/// Parses `az account get-access-token --output json`'s response. A standalone enum (rather than
/// inline in `AzureCliCredentialProvider`) so it can be unit tested against fixed JSON without
/// shelling out to a real `az` binary.
enum AzureCliAccessTokenParser {
    static func parse(_ data: Data) -> AzureAccessToken? {
        guard
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let token = object["accessToken"] as? String, !token.isEmpty
        else {
            return nil
        }

        // az emits either "expiresOn" ("2024-01-01 12:00:00.000000", local time, no timezone) or
        // "expires_on" (a Unix epoch second count), depending on version. Both are read for
        // resilience; an unparseable expiry falls back to a conservative 5-minute assumption rather
        // than treating the whole response as invalid over a cosmetic field.
        if let expiresOnText = object["expiresOn"] as? String {
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSSSSS"
            formatter.timeZone = TimeZone.current
            if let date = formatter.date(from: expiresOnText) {
                return AzureAccessToken(token: token, expiresAt: date)
            }
        }
        if let expiresOnEpoch = object["expires_on"] as? Int {
            return AzureAccessToken(token: token, expiresAt: Date(timeIntervalSince1970: TimeInterval(expiresOnEpoch)))
        }

        return AzureAccessToken(token: token, expiresAt: Date().addingTimeInterval(300))
    }
}

/// Authenticates as an Entra app registration via the OAuth2 client-credentials grant, called
/// directly over REST rather than through the Azure Identity SDK (no Swift equivalent exists).
/// Also an `actor`, purely to serialize cache reads/writes; unlike the CLI path there is no shared
/// external process to protect from concurrent launches.
actor AzureServicePrincipalCredentialProvider: AzureCredentialProvider {
    private let principal: AzureServicePrincipal
    private let session: URLSession
    private var cached: AzureAccessToken?
    private var cachedScope: String?

    init(principal: AzureServicePrincipal, session: URLSession = .shared) {
        self.principal = principal
        self.session = session
    }

    func accessToken(scope: String) async throws -> AzureAccessToken {
        if let cached, cachedScope == scope, cached.expiresAt > Date().addingTimeInterval(60) {
            return cached
        }

        let token = try await requestToken(scope: scope)
        cached = token
        cachedScope = scope
        return token
    }

    private func requestToken(scope: String) async throws -> AzureAccessToken {
        guard let url = URL(string: "https://login.microsoftonline.com/\(principal.tenantId)/oauth2/v2.0/token") else {
            throw AzureCredentialError.tokenRequestFailed("Invalid tenant id")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        var components = URLComponents()
        components.queryItems = [
            URLQueryItem(name: "grant_type", value: "client_credentials"),
            URLQueryItem(name: "client_id", value: principal.clientId),
            URLQueryItem(name: "client_secret", value: principal.clientSecret),
            URLQueryItem(name: "scope", value: scope)
        ]
        request.httpBody = components.percentEncodedQuery?.data(using: .utf8)

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw AzureCredentialError.tokenRequestFailed(error.localizedDescription)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw AzureCredentialError.tokenRequestFailed("No HTTP response")
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            let bodyText = String(data: data, encoding: .utf8) ?? "<undecodable body>"
            throw AzureCredentialError.tokenRequestFailed("HTTP \(httpResponse.statusCode): \(bodyText)")
        }

        guard
            let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let token = object["access_token"] as? String, !token.isEmpty
        else {
            throw AzureCredentialError.tokenResponseUnparseable
        }

        let expiresIn = (object["expires_in"] as? Int).map(TimeInterval.init) ?? 3600
        return AzureAccessToken(token: token, expiresAt: Date().addingTimeInterval(expiresIn))
    }
}
