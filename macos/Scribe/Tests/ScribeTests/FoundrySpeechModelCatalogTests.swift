import XCTest

@testable import Scribe

final class FoundrySpeechModelCatalogTests: XCTestCase {
    private func makeScript(_ body: String, in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("foundry")
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: url)
        XCTAssertEqual(chmod(url.path(percentEncoded: false), 0o755), 0)
        return url
    }

    func testCacheQueryReturnsCachedAliasesAndDoesNotDownload() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-cache")
        let script = try makeScript(
            """
            test "$1" = cache && test "$2" = list || exit 9
            printf '{"models":[{"alias":"parakeet-tdt-0.6b-v2","cached":true},{"alias":"whisper-base","cached":false}]}'
            """, in: directory)

        let aliases = try await FoundrySpeechModelCatalog.cachedAliases(cliURL: script)

        XCTAssertEqual(aliases, ["parakeet-tdt-0.6b-v2"])
    }

    func testDownloadRunsOnlyAfterExplicitRequestForSelectedAlias() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-download")
        let argumentsURL = directory.appendingPathComponent("arguments")
        let argumentsPath = argumentsURL.path(percentEncoded: false)
        let script = try makeScript(
            """
            printf '%s %s %s' "$1" "$2" "$3" > '\(argumentsPath)'
            """, in: directory)

        try await FoundrySpeechModelCatalog.download(alias: "whisper-base", cliURL: script)

        XCTAssertEqual(try String(contentsOf: argumentsURL, encoding: .utf8), "model download whisper-base")
    }

    func testDownloadRejectsAnAliasOutsideTheSpeechChoices() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-invalid-download")
        let script = try makeScript("touch '\(directory.path(percentEncoded: false))/started'", in: directory)

        do {
            try await FoundrySpeechModelCatalog.download(alias: "qwen2.5-7b", cliURL: script)
            XCTFail("an unsupported model must not be downloaded")
        } catch {
            XCTAssertEqual(error as? FoundrySpeechModelError, .unsupportedModel)
        }
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: directory.appendingPathComponent("started").path(percentEncoded: false)))
    }
}
