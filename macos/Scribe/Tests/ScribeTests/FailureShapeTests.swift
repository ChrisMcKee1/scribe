import Foundation
import XCTest

@testable import Scribe

final class FailureShapeTests: XCTestCase {
    /// Carries a privacy canary in every place an error can hold text: its payload, its description,
    /// its domain, its user info and the error it wraps.
    private enum LeakingError: Error, LocalizedError, CustomNSError, CustomStringConvertible, FailureShapeDetailing {
        case leaked(secret: String, path: String, url: URL, status: Int32)

        static var errorDomain: String { PrivacyCanary.url }
        var errorCode: Int { 7 }
        var errorUserInfo: [String: Any] {
            [
                NSLocalizedDescriptionKey: PrivacyCanary.secret,
                NSLocalizedFailureReasonErrorKey: PrivacyCanary.transcript,
                NSFilePathErrorKey: PrivacyCanary.path,
                NSURLErrorKey: URL(string: PrivacyCanary.url)!,
                NSUnderlyingErrorKey: NSError(
                    domain: PrivacyCanary.path, code: 2,
                    userInfo: [NSLocalizedDescriptionKey: PrivacyCanary.transcript]),
            ]
        }
        var errorDescription: String? { "\(PrivacyCanary.secret) at \(PrivacyCanary.path) via \(PrivacyCanary.url)" }
        var failureReason: String? { PrivacyCanary.transcript }
        var description: String { errorDescription ?? "" }
        var failureHTTPStatus: Int? { 403 }
        var failureServiceCode: String? { PrivacyCanary.url }
    }

    private enum PlainError: Error {
        case bare
        case wrapping(any Error)
    }

    private enum DescribedError: Error, CustomStringConvertible {
        case bare
        var description: String { PrivacyCanary.secret }
    }

    private struct ServiceFailure: FailureShapeDetailing {
        let failureHTTPStatus: Int?
        let failureServiceCode: String?
    }

    func testAnErrorCarryingCanariesEverywhereYieldsAShapeWithNoneOfThem() {
        let error = LeakingError.leaked(
            secret: PrivacyCanary.secret, path: PrivacyCanary.path, url: URL(string: PrivacyCanary.url)!,
            status: -25299)

        let shape = FailureShape(error)

        XCTAssertEqual(shape.description, "LeakingError.leaked(? 7) values=-25299 http=403 inner=NSError(? 2)")
        PrivacyCanary.assertAbsent(from: shape.description)
        PrivacyCanary.assertAbsent(from: String(describing: shape))
        XCTAssertNil(shape.serviceCode)
    }

    /// Foundation's error structs (`URLError`, `POSIXError`, `CocoaError`) travel inside `any Error` as
    /// the `NSError` they wrap, so they read as `NSError` with their domain.
    func testAURLErrorKeepsItsDiagnosisAndLosesTheURL() {
        let underlying = NSError(
            domain: "kCFErrorDomainCFNetwork", code: -1001,
            userInfo: [NSLocalizedDescriptionKey: PrivacyCanary.url])
        let error = URLError(
            .timedOut,
            userInfo: [
                NSURLErrorFailingURLErrorKey: URL(string: PrivacyCanary.url)!,
                NSURLErrorFailingURLStringErrorKey: PrivacyCanary.url,
                NSLocalizedDescriptionKey: "The request to \(PrivacyCanary.url) timed out.",
                NSUnderlyingErrorKey: underlying,
            ])

        let shape = FailureShape(error)

        XCTAssertEqual(
            shape.description,
            "NSError(NSURLErrorDomain -1001) url=timedOut inner=NSError(kCFErrorDomainCFNetwork -1001)")
        XCTAssertEqual(shape.urlErrorCode, -1001)
        PrivacyCanary.assertAbsent(from: shape.description)
    }

    func testFrameworkErrorsKeepTheirDomainAndCode() {
        let status = NSError(
            domain: NSOSStatusErrorDomain, code: -25299, userInfo: [NSFilePathErrorKey: PrivacyCanary.path])
        let appleDomain = NSError(domain: "com.apple.coreaudio.avfaudio", code: -10868)
        let posix = POSIXError(.EACCES, userInfo: [NSFilePathErrorKey: PrivacyCanary.path])

        XCTAssertEqual(FailureShape(status).description, "NSError(NSOSStatusErrorDomain -25299)")
        XCTAssertEqual(FailureShape(appleDomain).description, "NSError(com.apple.coreaudio.avfaudio -10868)")
        XCTAssertEqual(FailureShape(posix).description, "NSError(NSPOSIXErrorDomain 13)")
    }

    func testADomainThatIsNotAFrameworkConstantIsHidden() {
        for domain in ["contoso-ai.openai.azure.com", "contoso.openai.azure.com", PrivacyCanary.path, "two words"] {
            XCTAssertEqual(FailureShape(NSError(domain: domain, code: 1)).description, "NSError(? 1)", domain)
        }
    }

    func testSwiftErrorsAreNamedByTheirCaseAndCarriedIntegers() {
        XCTAssertEqual(
            FailureShape(TranscriptionEngineError.missingFoundryCli).description,
            "TranscriptionEngineError.missingFoundryCli")
        XCTAssertEqual(
            FailureShape(TranscriptionEngineError.processFailed(PrivacyCanary.transcript)).description,
            "TranscriptionEngineError.processFailed")
        XCTAssertEqual(
            FailureShape(KeychainStore.KeychainError.unhandled(-25299)).description,
            "KeychainError.unhandled values=-25299")
        XCTAssertEqual(
            FailureShape(ProcessRunnerError.launchFailed(errno: ENOENT)).description,
            "ProcessRunnerError.launchFailed values=2")
        XCTAssertEqual(FailureShape(CancellationError()).description, "CancellationError")
        XCTAssertEqual(FailureShape(PlainError.bare).description, "PlainError.bare")
    }

    func testACustomDescriptionIsNeverUsedToNameACase() {
        let shape = FailureShape(DescribedError.bare)

        XCTAssertEqual(shape.description, "DescribedError")
        PrivacyCanary.assertAbsent(from: shape.description)
    }

    func testErrorsCarriedInAPayloadJoinTheChain() {
        let error = PlainError.wrapping(POSIXError(.ENOENT, userInfo: [NSFilePathErrorKey: PrivacyCanary.path]))

        XCTAssertEqual(FailureShape(error).description, "PlainError.wrapping inner=NSError(NSPOSIXErrorDomain 2)")
    }

    func testADecodingErrorDoesNotRepeatTheBodyItFailedOn() throws {
        let body = Data(#"{"secret": \#(PrivacyCanary.secret), "note": "\#(PrivacyCanary.transcript)"#.utf8)

        let error: any Error
        do {
            _ = try JSONDecoder().decode([String: String].self, from: body)
            return XCTFail("Expected the body to be rejected")
        } catch let decodingError {
            error = decodingError
        }

        let shape = FailureShape(error)
        XCTAssertEqual(shape.typeName, "DecodingError.dataCorrupted")
        PrivacyCanary.assertAbsent(from: shape.description)
    }

    func testAServiceCodeIsKeptOnlyWhenItLooksLikeAnIdentifier() {
        let accepted = ["invalid_api_key", "DeploymentNotFound", "AADSTS7000215", "content-filter"]
        for code in accepted {
            let shape = FailureShape(ServiceFailure(failureHTTPStatus: 401, failureServiceCode: code))
            XCTAssertEqual(shape.description, "ServiceFailure http=401 service=\(code)")
        }

        let rejected = [
            "not a code", "https://example.invalid", "contoso.openai.azure.com", "7starts-with-a-digit", "",
            String(repeating: "a", count: 65),
        ]
        for code in rejected {
            let shape = FailureShape(ServiceFailure(failureHTTPStatus: nil, failureServiceCode: code))
            XCTAssertNil(shape.serviceCode, code)
        }
    }

    func testAnHTTPStatusOutsideTheValidRangeIsDropped() {
        XCTAssertNil(FailureShape(ServiceFailure(failureHTTPStatus: 0, failureServiceCode: nil)).httpStatus)
        XCTAssertNil(FailureShape(ServiceFailure(failureHTTPStatus: 1000, failureServiceCode: nil)).httpStatus)
        XCTAssertEqual(FailureShape(ServiceFailure(failureHTTPStatus: 599, failureServiceCode: nil)).httpStatus, 599)
    }

    func testADeepChainIsBounded() {
        var error = NSError(domain: NSPOSIXErrorDomain, code: 0)
        for depth in 1...200 {
            error = NSError(domain: NSPOSIXErrorDomain, code: depth, userInfo: [NSUnderlyingErrorKey: error])
        }

        let shape = FailureShape(error)

        XCTAssertEqual(shape.underlying.count, 8)
        XCTAssertEqual(shape.underlying.first, "NSError(NSPOSIXErrorDomain 199)")
    }

    func testDescribeSaysNoneWithoutAnError() {
        XCTAssertEqual(FailureShape.describe(nil), "none")
        XCTAssertEqual(FailureShape.describe(CancellationError()), "CancellationError")
    }
}
