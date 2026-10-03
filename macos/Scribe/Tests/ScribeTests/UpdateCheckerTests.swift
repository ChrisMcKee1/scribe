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

    func testSignedNegativeAndNonAsciiComponentsAreRefused() {
        for value in ["-1.0", "0.-1", "+1.0", "0.+1", "١.0", "0..1", "1. 0"] {
            XCTAssertNil(SemanticVersion(value), value)
        }
    }
}

final class UpdateCheckerTests: XCTestCase {
    func testCancelledAdmissionStartsNoNetworkRequest() async {
        let session = makeStubSession { request in
            XCTFail("A cancelled update check must not start a request")
            return (
                HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                Data("[]".utf8)
            )
        }
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await UpdateChecker(session: session).checkForUpdate(currentVersion: "1.0.0")
        }
        let result = await task.value
        XCTAssertEqual(result, .failed(message: "The update check was cancelled."))
    }

    func testCompareReportsUpdateAvailableWhenReleaseIsNewer() {
        let release = GitHubRelease(
            tagName: "v0.2.0",
            htmlURL: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v0.2.0")!,
            name: "0.2.0", draft: false, prerelease: false, assets: [])
        let result = UpdateChecker.compare(currentVersion: "0.1.0", release: release)
        XCTAssertEqual(result, .updateAvailable(current: "0.1.0", latest: "v0.2.0", url: release.htmlURL))
    }

    func testCompareReportsUpToDateWhenReleaseIsSameVersion() {
        let release = GitHubRelease(
            tagName: "v0.1.0",
            htmlURL: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v0.1.0")!,
            name: nil, draft: false, prerelease: false, assets: [])
        let result = UpdateChecker.compare(currentVersion: "0.1.0", release: release)
        XCTAssertEqual(result, .upToDate(current: "0.1.0"))
    }

    func testCompareReportsUpToDateWhenCurrentIsNewerThanLatestTag() {
        // Guards against ever telling a dev build ahead of the last tag that it's out of date.
        let release = GitHubRelease(
            tagName: "v0.1.0",
            htmlURL: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v0.1.0")!,
            name: nil, draft: false, prerelease: false, assets: [])
        let result = UpdateChecker.compare(currentVersion: "0.2.0", release: release)
        XCTAssertEqual(result, .upToDate(current: "0.2.0"))
    }

    func testCompareFailsWhenCurrentVersionIsUnparsable() {
        let release = GitHubRelease(
            tagName: "v0.1.0",
            htmlURL: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v0.1.0")!,
            name: nil, draft: false, prerelease: false, assets: [])
        let result = UpdateChecker.compare(currentVersion: "unknown", release: release)
        if case .failed = result {
            // expected
        } else {
            XCTFail("expected .failed, got \(result)")
        }
    }

    func testCheckForUpdateParsesRealisticGitHubResponse() async {
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            XCTAssertEqual(request.url?.query, "per_page=100")
            let json = """
                [{
                    "tag_name": "v9.9.9",
                    "html_url": "https://github.com/ChrisMcKee1/scribe/releases/tag/v9.9.9",
                    "name": "Scribe 9.9.9",
                    "draft": false,
                    "prerelease": false,
                    "assets": [{"name": "Scribe-macOS-9.9.9.dmg"}]
                }]
                """.data(using: .utf8)!
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, json)
        }
        let checker = UpdateChecker(session: session)
        let result = await checker.checkForUpdate(currentVersion: "0.1.0")
        XCTAssertEqual(
            result,
            .updateAvailable(
                current: "0.1.0",
                latest: "v9.9.9",
                url: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v9.9.9")!))
        XCTAssertEqual(requests.all.first?.host, "api.github.com")
        XCTAssertEqual(requests.all.first?.path, "/repos/ChrisMcKee1/scribe/releases")
    }

    func testSelectsHighestStableMacVersionRatherThanNewestWindowsRelease() async {
        let body = """
            [
                \(releaseJSON("v9.0.0", asset: "Scribe-win-x64-Setup.exe")),
                \(releaseJSON("v1.1.0", asset: "Scribe-macOS-1.1.0.dmg")),
                \(releaseJSON("v1.3.0", asset: "Scribe-macOS-1.3.0.dmg")),
                \(releaseJSON("v8.0.0", asset: "Scribe-macOS-8.0.0.dmg", prerelease: true)),
                \(releaseJSON("v7.0.0", asset: "Scribe-macOS-7.0.0.dmg", draft: true))
            ]
            """
        let result = await check(body)
        XCTAssertEqual(
            result,
            .updateAvailable(
                current: "1.0.0", latest: "v1.3.0",
                url: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v1.3.0")!))
    }

    func testMissingOrWrongVersionMacArtifactIsNotAnUpdate() async {
        for body in [
            "[]",
            "[\(releaseJSON("v9.0.0", asset: "Scribe-win-arm64-Portable.zip"))]",
            "[\(releaseJSON("v9.0.0", asset: "Scribe-macOS-8.0.0.dmg"))]",
            "[\(releaseJSON("v9.0.0", asset: "Scribe-macOS-9.0.0.dmg", prerelease: true))]",
        ] {
            let result = await check(body)
            XCTAssertEqual(result, .noMacRelease)
        }
    }

    func testOlderStableMacReleaseReportsCurrentVersionNotWindowsVersion() async {
        let body = """
            [\(releaseJSON("v9.0.0", asset: "Scribe-win-x64-Setup.exe")),
             \(releaseJSON("v0.9.0", asset: "Scribe-macOS-0.9.0.dmg"))]
            """
        let result = await check(body)
        XCTAssertEqual(result, .upToDate(current: "1.0.0"))
    }

    func testReleaseLinkMustBelongToUpstreamGitHubTag() async {
        for url in [
            "http://github.com/ChrisMcKee1/scribe/releases/tag/v9.0.0",
            "https://example.test/ChrisMcKee1/scribe/releases/tag/v9.0.0",
            "https://github.com/x3nc0n/scribe/releases/tag/v9.0.0",
            "https://github.com/ChrisMcKee1/scribe/releases/tag/v8.0.0",
        ] {
            let body = """
                [{"tag_name":"v9.0.0","html_url":"\(url)","draft":false,"prerelease":false,
                  "assets":[{"name":"Scribe-macOS-9.0.0.dmg"}]}]
                """
            let result = await check(body)
            XCTAssertEqual(result, .noMacRelease)
        }
    }

    func testSupportSourcePrivacyAndUpdatesUseTheMaintainerRepository() {
        XCTAssertEqual(ScribeRepository.slug, "ChrisMcKee1/scribe")
        XCTAssertEqual(ScribeRepository.url.absoluteString, "https://github.com/ChrisMcKee1/scribe")
        XCTAssertEqual(
            ScribeRepository.privacyURL.absoluteString,
            "https://github.com/ChrisMcKee1/scribe/blob/main/PRIVACY.md")
        XCTAssertEqual(
            ScribeRepository.newIssueURL.absoluteString,
            "https://github.com/ChrisMcKee1/scribe/issues/new")
    }

    private func releaseJSON(
        _ tag: String, asset: String, draft: Bool = false, prerelease: Bool = false
    ) -> String {
        """
        {"tag_name":"\(tag)","html_url":"https://github.com/ChrisMcKee1/scribe/releases/tag/\(tag)",
         "draft":\(draft),"prerelease":\(prerelease),"assets":[{"name":"\(asset)"}]}
        """
    }

    private func check(_ body: String) async -> UpdateCheckResult {
        let data = Data(body.utf8)
        let session = makeStubSession { request in
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, data)
        }
        return await UpdateChecker(session: session).checkForUpdate(currentVersion: "1.0.0")
    }

    func testCheckForUpdateFailsOnNonSuccessStatus() async {
        let session = makeStubSession { request in
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 404, httpVersion: nil, headerFields: nil)!
            return (response, Data())
        }
        let checker = UpdateChecker(session: session)
        let result = await checker.checkForUpdate(currentVersion: "0.1.0")
        if case .failed = result {
            // expected
        } else {
            XCTFail("expected .failed, got \(result)")
        }
    }

    func testCheckForUpdateFailsOnUndecodableBody() async {
        let session = makeStubSession { request in
            let response = HTTPURLResponse(
                url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, "not json".data(using: .utf8)!)
        }
        let checker = UpdateChecker(session: session)
        let result = await checker.checkForUpdate(currentVersion: "0.1.0")
        if case .failed = result {
            // expected
        } else {
            XCTFail("expected .failed, got \(result)")
        }
    }
}
