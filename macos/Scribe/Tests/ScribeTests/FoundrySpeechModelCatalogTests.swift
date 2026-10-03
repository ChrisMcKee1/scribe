import XCTest

@testable import Scribe

final class FoundrySpeechModelCatalogTests: XCTestCase {
    private func makeScript(_ body: String, in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("foundry")
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: url)
        XCTAssertEqual(chmod(url.path(percentEncoded: false), 0o755), 0)
        return url
    }

    func testModelListDiscoversSpeechAliasesAndCacheStateWithoutDownloading() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-list")
        let script = try makeScript(
            """
            test "$1" = model && test "$2" = list && test "$3" = --type && test "$4" = speech || exit 9
            printf '{"models":[{"alias":"parakeet-tdt-0.6b-v2","displayName":"Parakeet TDT v2","type":"Speech","cached":true},{"alias":"whisper-base","displayName":"Whisper Base","type":"Speech","cached":false},{"alias":"qwen2.5-7b","type":"Chat","cached":true},{"alias":"older-runtime-speech","type":"Speech"}]}'
            """, in: directory)

        let choices = try await FoundrySpeechModelCatalog.list(cliURL: script)

        XCTAssertEqual(
            choices,
            [
                FoundrySpeechModelChoice(alias: "parakeet-tdt-0.6b-v2", title: "Parakeet TDT v2", isCached: true),
                FoundrySpeechModelChoice(alias: "whisper-base", title: "Whisper Base", isCached: false),
                FoundrySpeechModelChoice(
                    alias: "older-runtime-speech", title: "older-runtime-speech", isCached: nil),
            ])
    }

    func testModelListFailureDoesNotReturnAnEmptyCatalogAsSuccess() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-list-failure")
        let script = try makeScript("printf 'not json'; exit 0", in: directory)

        do {
            _ = try await FoundrySpeechModelCatalog.list(cliURL: script)
            XCTFail("unreadable model-list output must be reported")
        } catch {
            XCTAssertEqual(error as? FoundrySpeechModelError, .catalogUnavailable)
        }
    }

    func testDamagedCatalogOutputNeverReportsAvailableModels() async throws {
        for ending in ["kill -TERM \"$$\"", "head -c 1048577 /dev/zero | tr '\\000' ' '"] {
            let directory = try makeTemporaryDirectory(label: "speech-model-damaged-catalog")
            let script = try makeScript(
                """
                echo '{"models":[{"alias":"whisper-base","type":"Speech","cached":true}]}'
                \(ending)
                """, in: directory)
            do {
                _ = try await FoundrySpeechModelCatalog.list(cliURL: script)
                XCTFail("A damaged catalog cannot authorize model choices")
            } catch {
                XCTAssertEqual(error as? FoundrySpeechModelError, .catalogUnavailable)
            }
        }
    }

    func testCatalogExcludesAliasesThatWouldBecomeOptionsOrInvalidArguments() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-invalid-catalog-aliases")
        let script = try makeScript(
            """
            printf '%s' '{"models":[{"alias":"--help"},{"alias":""},{"alias":"nul\\u0000suffix"},{"alias":"speech-model"}]}'
            """, in: directory)
        let choices = try await FoundrySpeechModelCatalog.list(cliURL: script)
        XCTAssertEqual(choices.map(\.alias), ["speech-model"])
    }

    func testSignalTerminatedDownloadNeverReportsSuccessButLongProgressCan() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-download-integrity")
        let script = try makeScript("echo 'Downloaded'; kill -TERM \"$$\"", in: directory)
        do {
            try await FoundrySpeechModelCatalog.download(alias: "whisper-base", cliURL: script)
            XCTFail("A signal-terminated download must not report success")
        } catch {
            XCTAssertEqual(error as? FoundrySpeechModelError, .downloadFailed)
        }
        let successful = try makeScript(
            "head -c 20000 /dev/zero | tr '\\000' ' '; exit 0", in: directory)
        try await FoundrySpeechModelCatalog.download(alias: "whisper-base", cliURL: successful)
    }

    func testPickerChoicesKeepASavedAliasMissingFromTheInstalledCatalog() {
        let installed = [
            FoundrySpeechModelChoice(alias: "whisper-base", title: "Whisper Base", isCached: true)
        ]

        let choices = FoundrySpeechModelCatalog.choices(from: installed, preserving: "retired-speech-alias")

        XCTAssertEqual(
            choices.map(\.alias),
            [
                "whisper-base",
                TranscriptionEngine.defaultFoundryModelAlias,
                "retired-speech-alias",
            ])
        XCTAssertEqual(choices.last?.title, "Saved selection, not listed: retired-speech-alias")
    }

    func testCancelledModelSetupStartsNeitherListingNorDownload() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-cancelled-admission")
        let marker = directory.appendingPathComponent("started")
        let script = try makeScript("touch '\(marker.path)'", in: directory)
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            do {
                _ = try await FoundrySpeechModelCatalog.list(cliURL: script)
                XCTFail("Cancelled listing must not run")
            } catch is CancellationError {
            }
            do {
                try await FoundrySpeechModelCatalog.download(alias: "whisper-base", cliURL: script)
                XCTFail("Cancelled download must not run")
            } catch is CancellationError {
            }
        }
        try await task.value
        XCTAssertFalse(FileManager.default.fileExists(atPath: marker.path))
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

    @MainActor
    func testCancellingTheSettingsTaskStopsAndReapsAnExplicitDownload() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-download-cancel")
        let ready = directory.appendingPathComponent("ready")
        let pidFile = directory.appendingPathComponent("pid")
        let script = try makeScript(
            """
            trap '' TERM
            echo $$ > '\(pidFile.path(percentEncoded: false))'
            : > '\(ready.path(percentEncoded: false))'
            exec sleep 30
            """, in: directory)
        let operations = AuxiliaryOperations()
        let download = Task { @MainActor in
            try await operations.run {
                try await FoundrySpeechModelCatalog.download(alias: "whisper-base", cliURL: script)
            }
        }

        let started = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(started)
        let pid = try XCTUnwrap(
            pid_t(try String(contentsOf: pidFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        download.cancel()
        do {
            try await download.value
            XCTFail("a cancelled Settings task must not report a completed download")
        } catch is CancellationError {
        }

        XCTAssertTrue(ProcessResources.waitForExit(of: pid, timeout: .seconds(10)))
        XCTAssertEqual(operations.runningCount, 0)
    }

    func testDownloadRejectsAnEmptyAliasWithoutStartingTheCommand() async throws {
        let directory = try makeTemporaryDirectory(label: "speech-model-invalid-download")
        let script = try makeScript("touch '\(directory.path(percentEncoded: false))/started'", in: directory)

        for alias in ["", "--help", "-model", "nul\0suffix"] {
            do {
                try await FoundrySpeechModelCatalog.download(alias: alias, cliURL: script)
                XCTFail("An invalid alias must not become a command argument")
            } catch {
                XCTAssertEqual(error as? FoundrySpeechModelError, .invalidAlias)
            }
        }
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: directory.appendingPathComponent("started").path(percentEncoded: false)))
    }
}
