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
///     NSError(NSURLErrorDomain -1001) url=timedOut inner=NSError(kCFErrorDomainCFNetwork -1001)
///     KeychainError.unhandled values=-25299
///     ProcessRunnerError.launchFailed values=2
///
/// Foundation's error structs (`URLError`, `POSIXError`, `CocoaError`) travel inside `any Error` as the
/// `NSError` they wrap, so they read as `NSError` with their domain, which names them just as well.
struct FailureShape: Sendable, Equatable, CustomStringConvertible {
    /// The error's Swift type and, for an enum, its case: `NSError`, `TranscriptionEngineError.processFailed`.
    let typeName: String
    /// The `NSError` domain; `nil` when it only repeats the Swift type (every Swift error's default),
    /// and `?` when it does not look like a framework constant, such as a host name or a URL.
    let domain: String?
    /// The `NSError` code; `nil` for a Swift error that does not choose its own, whose default code
    /// only numbers its cases.
    let code: Int?
    /// Integer values an enum case carries, such as an `OSStatus` or an `errno`, at most four. Every
    /// other payload (strings, URLs, data) is skipped unread.
    let payloadIntegers: [Int64]
    /// From the first error in the chain that reports one through `FailureShapeDetailing`.
    let httpStatus: Int?
    /// From the first error in the chain that reports one through `FailureShapeDetailing`, and only
    /// when it looks like an identifier.
    let serviceCode: String?
    /// The code of the first `NSURLErrorDomain` error in the chain.
    let urlErrorCode: Int?
    /// The errors this one wraps, outermost first, each as `Type(domain code)`, at most eight.
    let underlying: [String]

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
                if serviceCode == nil, let candidate = detailing.failureServiceCode {
                    serviceCode = Self.sanitizedServiceCode(candidate)
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

    /// Framework domains are identifiers (`NSURLErrorDomain`, `kCFErrorDomainCFNetwork`) or Apple
    /// reverse-DNS names (`com.apple.coreaudio.avfaudio`). Anything else is not trusted to be one.
    private static func frameworkDomain(_ domain: String) -> String {
        if EnumCaseName.identifier(domain, maximumLength: 96) != nil {
            return domain
        }
        let labels = domain.split(separator: ".", omittingEmptySubsequences: false)
        if domain.hasPrefix("com.apple."), domain.utf8.count <= 96,
            labels.allSatisfy({ EnumCaseName.identifier(String($0), maximumLength: 64) != nil })
        {
            return domain
        }
        return "?"
    }

    /// Windows' rule for a service's error code: a letter, then up to 63 letters, digits, `_` or `-`.
    /// No dot, colon, slash or space gets through, so a host, a URL or a sentence cannot pass as a code.
    private static func sanitizedServiceCode(_ candidate: String) -> String? {
        let scalars = candidate.unicodeScalars
        guard let first = scalars.first, first.isASCIILetter, scalars.count <= 64,
            scalars.allSatisfy({ $0.isASCIILetter || $0.isASCIIDigit || $0 == "_" || $0 == "-" })
        else {
            return nil
        }
        return candidate
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
    /// The service's own error code, such as `DeploymentNotFound`, `invalid_api_key` or
    /// `AADSTS7000215`. Kept only when it looks like an identifier, so a sentence, a host or a URL is
    /// dropped even when the service put one there.
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
