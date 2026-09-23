import Foundation

/// What a log line may say about a failure: its shape, never its text.
///
/// An error's words are not safe to log. `localizedDescription` and user info carry exactly what the
/// privacy contract keeps out of logs: a URL error's user info holds the failing URL, a file error the
/// path (and with it the account name), and this app's own errors carry `az` output, HTTP response
/// bodies and endpoints in their payloads. A shape keeps what diagnoses a failure (the type and enum
/// case, the framework's domain and code, integer codes an enum case carries, the HTTP status, the
/// URL error code and the errors it wraps) and never reads a message, a string payload or any user
/// info except the wrapped errors. It mirrors Windows' `Scribe.Core.Diagnostics.FailureShape`.
///
/// Every piece of text in a shape comes from Scribe or the compiler, never from the error: type and
/// case names are the compiler's, a domain or a service code (an Entra `AADSTS` code included) is
/// written only when it matches an entry in a fixed list here, and numbers (codes and statuses) are
/// written from their values. Matching a pattern would not be enough: a string that merely looks like
/// an identifier can still be a secret or a word the user dictated, and the digits after `AADSTS` can be
/// a phone number as easily as a code. Anything unlisted is written as `other`, and an unlisted Entra
/// code as `AADSTS` alone.
///
///     NSError(NSURLErrorDomain -1001) url=timedOut inner=NSError(kCFErrorDomainCFNetwork -1001)
///     KeychainError.unhandled values=-25299
///     ProcessRunnerError.launchFailed values=2
///
/// Foundation's error structs (`URLError`, `POSIXError`, `CocoaError`) travel inside `any Error` as the
/// `NSError` they wrap, so they read as `NSError` with their domain, which names them just as well.
struct FailureShape: Sendable, Equatable, CustomStringConvertible {
    /// The error's Swift type and, for an enum, its case: `NSError`, `TranscriptionEngineError.processFailed`.
    let typeName: String
    /// The `NSError` domain when it is in `frameworkDomains`; `nil` when it only repeats the Swift type
    /// (every Swift error's default), and `other` for any other domain.
    let domain: String?
    /// The `NSError` code; `nil` for a Swift error that does not choose its own, whose default code
    /// only numbers its cases.
    let code: Int?
    /// Integer values an enum case carries, such as an `OSStatus` or an `errno`, at most four. Every
    /// other payload (strings, URLs, data) is skipped unread.
    let payloadIntegers: [Int64]
    /// From the first error in the chain that reports one through `FailureShapeDetailing`.
    let httpStatus: Int?
    /// From the first error in the chain that reports one through `FailureShapeDetailing`: the code
    /// itself when it is in `knownServiceCodes`, `AADSTS` for an Entra code that is not, and `other` for
    /// anything else.
    let serviceCode: String?
    /// The code of the first `NSURLErrorDomain` error in the chain.
    let urlErrorCode: Int?
    /// The errors this one wraps, outermost first, each as `Type(domain code)`, at most eight.
    let underlying: [String]

    /// Framework error domains, each a constant its framework defines. A Swift error's own default
    /// domain (its type name, which Scribe computes rather than reads) is recognized separately and
    /// omitted. Scribe's errors are Swift types and use that default; one that adopts `CustomNSError`
    /// with a domain of its own has to add the domain here to have it written.
    static let frameworkDomains: Set<String> = [
        NSURLErrorDomain,
        NSPOSIXErrorDomain,
        NSCocoaErrorDomain,
        NSOSStatusErrorDomain,
        NSMachErrorDomain,
        "kCFErrorDomainCFNetwork",
        "AVFoundationErrorDomain",
        "com.apple.coreaudio.avfaudio",
        "SMAppServiceErrorDomain",
        "UNErrorDomain",
    ]

    /// Error codes the cleanup providers and Entra sign-in can return that Scribe writes as themselves: the
    /// OAuth 2.0 and OpenID Connect errors, the OpenAI-style `error.code` and `error.type` values, the
    /// Azure data plane codes, and the Entra `AADSTS` codes a user or an administrator can act on, as
    /// Microsoft documents them in "Microsoft Entra authentication and authorization error codes". An
    /// Entra code is matched exactly as written, so a leading zero or an extra digit makes it unlisted.
    static let knownServiceCodes: Set<String> = [
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client", "unsupported_grant_type",
        "invalid_scope", "invalid_resource", "temporarily_unavailable", "interaction_required",
        "consent_required", "login_required", "access_denied",
        "invalid_api_key", "invalid_request_error", "authentication_error", "permission_error",
        "not_found_error", "model_not_found", "context_length_exceeded", "content_filter",
        "rate_limit_exceeded", "rate_limit_error", "insufficient_quota", "server_error",
        "unsupported_parameter", "unsupported_value",
        "DeploymentNotFound", "Unauthorized", "PermissionDenied", "AuthenticationTypeDisabled",
        "OperationNotSupported", "TooManyRequests", "InternalServerError", "ServiceUnavailable", "429",
        // Entra: sign-in required, or a session or refresh token that expired or was revoked.
        "AADSTS50058", "AADSTS50089", "AADSTS50132", "AADSTS50133", "AADSTS50173", "AADSTS70008",
        "AADSTS70043", "AADSTS700020", "AADSTS700082",
        // Entra: multifactor authentication, Conditional Access and security defaults.
        "AADSTS50005", "AADSTS50072", "AADSTS50074", "AADSTS50076", "AADSTS50078", "AADSTS50079",
        "AADSTS50097", "AADSTS50131", "AADSTS50158", "AADSTS53000", "AADSTS53001", "AADSTS53002",
        "AADSTS53003", "AADSTS53004", "AADSTS530032", "AADSTS530035",
        // Entra: consent and role assignment.
        "AADSTS50105", "AADSTS65001", "AADSTS65004", "AADSTS90094", "AADSTS650057",
        // Entra: the service principal's client secret is wrong, expired or missing.
        "AADSTS70002", "AADSTS7000215", "AADSTS7000218", "AADSTS7000222",
        // Entra: the application is not in the tenant, or is disabled.
        "AADSTS70001", "AADSTS700011", "AADSTS700016", "AADSTS7000112",
        // Entra: the wrong tenant, cloud, account or resource.
        "AADSTS50001", "AADSTS50020", "AADSTS50034", "AADSTS50059", "AADSTS50128", "AADSTS50194",
        "AADSTS70011", "AADSTS90002", "AADSTS90019", "AADSTS90038", "AADSTS90072", "AADSTS500011",
        "AADSTS500014",
        // Entra: the account is locked, disabled, or its password expired or was mistyped.
        "AADSTS50053", "AADSTS50055", "AADSTS50057", "AADSTS50126",
        // Entra: too many requests, from this app or from the tenant.
        "AADSTS50196", "AADSTS90055",
    ]

    private static let maximumVisited = 32
    private static let maximumUnderlying = 8
    private static let maximumPayloadIntegers = 4

    init(_ error: any Error) {
        var typeName = ""
        var domain: String?
        var code: Int?
        var httpStatus: Int?
        var serviceCode: String?
        var urlErrorCode: Int?
        var underlying: [String] = []

        // One depth-first walk with one budget, so a pathological chain costs a fixed amount of work
        // while a failure is already being reported.
        var pending: [any Error] = [error]
        var visited = 0
        while let current = pending.popLast(), visited < Self.maximumVisited {
            visited += 1
            let head = Head(current)
            if visited == 1 {
                typeName = head.typeName
                domain = head.domain
                code = head.code
            } else if underlying.count < Self.maximumUnderlying {
                underlying.append(head.rendered)
            }

            if let detailing = current as? any FailureShapeDetailing {
                if httpStatus == nil, let status = detailing.failureHTTPStatus, (100...599).contains(status) {
                    httpStatus = status
                }
                if serviceCode == nil, let candidate = detailing.failureServiceCode, !candidate.isEmpty {
                    serviceCode = Self.serviceCodeIdentifier(candidate)
                }
            }
            if urlErrorCode == nil, head.bridgedDomain == NSURLErrorDomain {
                urlErrorCode = head.bridgedCode
            }

            pending.append(contentsOf: Self.wrappedErrors(of: current).reversed())
        }

        self.typeName = typeName
        self.domain = domain
        self.code = code
        self.payloadIntegers = Array(
            Self.payloadValues(of: error).compactMap(Self.integerValue).prefix(Self.maximumPayloadIntegers))
        self.httpStatus = httpStatus
        self.serviceCode = serviceCode
        self.urlErrorCode = urlErrorCode
        self.underlying = underlying
    }

    /// The shape's one-line description, or `none` when there is no error.
    static func describe(_ error: (any Error)?) -> String {
        guard let error else { return "none" }
        return FailureShape(error).description
    }

    var description: String {
        var text = Head.render(typeName: typeName, domain: domain, code: code)
        if !payloadIntegers.isEmpty {
            text += " values=" + payloadIntegers.map { String($0) }.joined(separator: ",")
        }
        if let httpStatus {
            text += " http=\(httpStatus)"
        }
        if let serviceCode {
            text += " service=\(serviceCode)"
        }
        if let urlErrorCode {
            text += " url=" + (Self.urlErrorNames[urlErrorCode] ?? String(urlErrorCode))
        }
        if !underlying.isEmpty {
            text += " inner=" + underlying.joined(separator: ">")
        }
        return text
    }

    // MARK: - One error

    /// The type, case, domain and code of one error in the chain.
    private struct Head {
        let typeName: String
        let domain: String?
        let code: Int?
        let bridgedDomain: String
        let bridgedCode: Int

        init(_ error: any Error) {
            let dynamicType = type(of: error)
            var typeName = String(describing: dynamicType)
            if let caseName = EnumCaseName.of(error) {
                typeName += "." + caseName
            }

            let bridged = error as NSError
            let isSwiftDefaultDomain = bridged.domain == String(reflecting: dynamicType)
            self.typeName = typeName
            self.domain = isSwiftDefaultDomain ? nil : FailureShape.frameworkDomain(bridged.domain)
            self.code = isSwiftDefaultDomain && !(error is any CustomNSError) ? nil : bridged.code
            self.bridgedDomain = bridged.domain
            self.bridgedCode = bridged.code
        }

        var rendered: String { Head.render(typeName: typeName, domain: domain, code: code) }

        static func render(typeName: String, domain: String?, code: Int?) -> String {
            switch (domain, code) {
            case (let domain?, let code?):
                return "\(typeName)(\(domain) \(code))"
            case (nil, let code?):
                return "\(typeName)(\(code))"
            case (let domain?, nil):
                return "\(typeName)(\(domain))"
            case (nil, nil):
                return typeName
            }
        }
    }

    /// A listed framework domain as itself, anything else as `other`.
    static func frameworkDomain(_ domain: String) -> String {
        frameworkDomains.contains(domain) ? domain : "other"
    }

    /// A listed service code as itself; `AADSTS` for any other Entra code (`AADSTS` and ASCII digits),
    /// which keeps where the failure came from but none of its digits; anything else as `other`.
    static func serviceCodeIdentifier(_ candidate: String) -> String {
        if knownServiceCodes.contains(candidate) {
            return candidate
        }
        let prefix = "AADSTS"
        let digits = candidate.unicodeScalars.dropFirst(prefix.unicodeScalars.count)
        if candidate.hasPrefix(prefix), !digits.isEmpty, digits.allSatisfy(\.isASCIIDigit) {
            return prefix
        }
        return "other"
    }

    // MARK: - Payloads

    /// The values an enum case carries, read by reflection so no error type has to opt in. Never a
    /// custom mirror, which could present anything.
    private static func payloadValues(of error: any Error) -> [Any] {
        guard !(error is any CustomReflectable) else { return [] }
        let mirror = Mirror(reflecting: error)
        guard mirror.displayStyle == .enum, let payload = mirror.children.first?.value else { return [] }
        let tuple = Mirror(reflecting: payload)
        return tuple.displayStyle == .tuple ? tuple.children.map { $0.value } : [payload]
    }

    private static func integerValue(_ value: Any) -> Int64? {
        guard let integer = value as? any BinaryInteger else { return nil }
        return Int64(clamping: integer)
    }

    private static func wrappedErrors(of error: any Error) -> [any Error] {
        payloadValues(of: error).compactMap { $0 as? any Error } + (error as NSError).underlyingErrors
    }

    private static let urlErrorNames: [Int: String] = [
        URLError.Code.cancelled.rawValue: "cancelled",
        URLError.Code.badURL.rawValue: "badURL",
        URLError.Code.timedOut.rawValue: "timedOut",
        URLError.Code.unsupportedURL.rawValue: "unsupportedURL",
        URLError.Code.cannotFindHost.rawValue: "cannotFindHost",
        URLError.Code.cannotConnectToHost.rawValue: "cannotConnectToHost",
        URLError.Code.networkConnectionLost.rawValue: "networkConnectionLost",
        URLError.Code.dnsLookupFailed.rawValue: "dnsLookupFailed",
        URLError.Code.notConnectedToInternet.rawValue: "notConnectedToInternet",
        URLError.Code.badServerResponse.rawValue: "badServerResponse",
        URLError.Code.userAuthenticationRequired.rawValue: "userAuthenticationRequired",
        URLError.Code.appTransportSecurityRequiresSecureConnection.rawValue:
            "appTransportSecurityRequiresSecureConnection",
        URLError.Code.secureConnectionFailed.rawValue: "secureConnectionFailed",
        URLError.Code.serverCertificateUntrusted.rawValue: "serverCertificateUntrusted",
    ]
}

/// Adopted by an error that knows facts about its failure `NSError` cannot carry, so its shape can
/// keep them. Return `nil` for what does not apply; both default to `nil`.
protocol FailureShapeDetailing: Error {
    /// The HTTP status of the response behind the failure.
    var failureHTTPStatus: Int? { get }
    /// The service's own error code, as the response carried it, such as `DeploymentNotFound`,
    /// `invalid_api_key` or `AADSTS7000215`. The shape writes it only when Scribe lists it (see
    /// `FailureShape.knownServiceCodes`); an unlisted Entra code becomes `AADSTS`, and any other value,
    /// whatever it looks like, `other`.
    var failureServiceCode: String? { get }
}

extension FailureShapeDetailing {
    var failureHTTPStatus: Int? { nil }
    var failureServiceCode: String? { nil }
}

/// The case name of an enum value, and nothing else about it. Shared by `FailureShape` and
/// `ScribeLog.Field.name` so both read the same, safe part of a value.
enum EnumCaseName {
    /// The case name of `value` when it is an enum, or `nil`. A case with a payload is named by its
    /// reflection label, which the compiler writes. A case without one is named by the runtime's
    /// default rendering, which is the bare case name, but only when the type has no description of
    /// its own, since a custom description could say anything.
    static func of(_ value: Any) -> String? {
        guard !(value is any CustomReflectable) else { return nil }
        let mirror = Mirror(reflecting: value)
        guard mirror.displayStyle == .enum else { return nil }
        if let label = mirror.children.first?.label {
            return identifier(label)
        }
        guard !(value is any CustomStringConvertible), !(value is any CustomDebugStringConvertible),
            !(value is any TextOutputStreamable)
        else {
            return nil
        }
        return identifier(String(describing: value))
    }

    /// `text` when it is an ASCII identifier no longer than `maximumLength`, otherwise `nil`.
    static func identifier(_ text: String, maximumLength: Int = 64) -> String? {
        let scalars = text.unicodeScalars
        guard let first = scalars.first, first.isASCIILetter || first == "_", scalars.count <= maximumLength,
            scalars.allSatisfy({ $0.isASCIILetter || $0.isASCIIDigit || $0 == "_" })
        else {
            return nil
        }
        return text
    }
}

extension Unicode.Scalar {
    fileprivate var isASCIILetter: Bool { ("a"..."z").contains(self) || ("A"..."Z").contains(self) }
    fileprivate var isASCIIDigit: Bool { ("0"..."9").contains(self) }
}
