import XCTest
@testable import Scribe

final class SemanticVersionTests: XCTestCase {
    func testEqualVersionsCompareEqual() {
        XCTAssertEqual(SemanticVersion("0.1.0"), SemanticVersion("0.1.0"))
    }

    func testMissingTrailingComponentIsTreatedAsZero() {
        XCTAssertEqual(SemanticVersion("0.2"), SemanticVersion("0.2.0"))
    }

    func testLeadingVPrefixIsStripped() {
        XCTAssertEqual(SemanticVersion("v0.2.9"), SemanticVersion("0.2.9"))
    }

    func testPatchBumpIsGreater() {
        XCTAssertLessThan(SemanticVersion("0.1.0")!, SemanticVersion("0.1.1")!)
    }

    func testMinorBumpOutranksPatch() {
        XCTAssertLessThan(SemanticVersion("0.1.9")!, SemanticVersion("0.2.0")!)
    }

    func testMajorBumpOutranksMinorAndPatch() {
        XCTAssertLessThan(SemanticVersion("0.9.9")!, SemanticVersion("1.0.0")!)
    }

    func testNonNumericComponentFailsToParse() {
        XCTAssertNil(SemanticVersion("0.1.0-beta"))
    }

    func testEmptyStringFailsToParse() {
        XCTAssertNil(SemanticVersion(""))
        XCTAssertNil(SemanticVersion("v"))
    }
}

final class UpdateCheckerTests: XCTestCase {
    override func tearDown() {
        StubURLProtocol.responseProvider = nil
        super.tearDown()
    }

    private func stubbedSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    func testCompareReportsUpdateAvailableWhenReleaseIsNewer() {
        let release = GitHubRelease(
            tagName: "v0.2.0",
            htmlURL: URL(string: "https://github.com/x3nc0n/scribe/releases/tag/v0.2.0")!,
            name: "0.2.0")
        let result = UpdateChecker.compare(currentVersion: "0.1.0", release: release)
        XCTAssertEqual(result, .updateAvailable(current: "0.1.0", latest: "v0.2.0", url: release.htmlURL))
    }

    func testCompareReportsUpToDateWhenReleaseIsSameVersion() {
        let release = GitHubRelease(
            tagName: "v0.1.0",
            htmlURL: URL(string: "https://github.com/x3nc0n/scribe/releases/tag/v0.1.0")!,
            name: nil)
        let result = UpdateChecker.compare(currentVersion: "0.1.0", release: release)
        XCTAssertEqual(result, .upToDate(current: "0.1.0"))
    }

    func testCompareReportsUpToDateWhenCurrentIsNewerThanLatestTag() {
        // Guards against ever telling a dev build ahead of the last tag that it's out of date.
        let release = GitHubRelease(
            tagName: "v0.1.0",
            htmlURL: URL(string: "https://github.com/x3nc0n/scribe/releases/tag/v0.1.0")!,
            name: nil)
        let result = UpdateChecker.compare(currentVersion: "0.2.0", release: release)
        XCTAssertEqual(result, .upToDate(current: "0.2.0"))
    }

    func testCompareFailsWhenCurrentVersionIsUnparsable() {
        let release = GitHubRelease(
            tagName: "v0.1.0",
            htmlURL: URL(string: "https://github.com/x3nc0n/scribe/releases/tag/v0.1.0")!,
            name: nil)
        let result = UpdateChecker.compare(currentVersion: "unknown", release: release)
        if case .failed = result {
            // expected
        } else {
            XCTFail("expected .failed, got \(result)")
        }
    }

    func testCheckForUpdateParsesRealisticGitHubResponse() async {
        StubURLProtocol.responseProvider = { request in
            XCTAssertEqual(request.url?.host, "api.github.com")
            XCTAssertEqual(request.url?.path, "/repos/x3nc0n/scribe/releases/latest")
            let json = """
            {
                "tag_name": "v9.9.9",
                "html_url": "https://github.com/x3nc0n/scribe/releases/tag/v9.9.9",
                "name": "Scribe 9.9.9"
            }
            """.data(using: .utf8)!
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, json)
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        let checker = UpdateChecker(session: URLSession(configuration: configuration))
        let result = await checker.checkForUpdate(currentVersion: "0.1.0")
        XCTAssertEqual(
            result,
            .updateAvailable(
                current: "0.1.0",
                latest: "v9.9.9",
                url: URL(string: "https://github.com/x3nc0n/scribe/releases/tag/v9.9.9")!))
    }

    func testCheckForUpdateFailsOnNonSuccessStatus() async {
        StubURLProtocol.responseProvider = { request in
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 404, httpVersion: nil, headerFields: nil)!
            return (response, Data())
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        let checker = UpdateChecker(session: URLSession(configuration: configuration))
        let result = await checker.checkForUpdate(currentVersion: "0.1.0")
        if case .failed = result {
            // expected
        } else {
            XCTFail("expected .failed, got \(result)")
        }
    }

    func testCheckForUpdateFailsOnUndecodableBody() async {
        StubURLProtocol.responseProvider = { request in
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, "not json".data(using: .utf8)!)
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        let checker = UpdateChecker(session: URLSession(configuration: configuration))
        let result = await checker.checkForUpdate(currentVersion: "0.1.0")
        if case .failed = result {
            // expected
        } else {
            XCTFail("expected .failed, got \(result)")
        }
    }
}
