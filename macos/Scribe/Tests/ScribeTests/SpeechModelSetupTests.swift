import XCTest

@testable import Scribe

@MainActor
final class SpeechModelSetupTests: XCTestCase {
    private let cached = FoundrySpeechModelChoice(alias: "speech", title: "Speech", isCached: true)
    private let uncached = FoundrySpeechModelChoice(alias: "speech", title: "Speech", isCached: false)

    func testObsoleteRefreshCannotOverwriteOrClearANewerDownload() async {
        for fails in [false, true] {
            let oldGate = SettingsTestGate()
            let downloadGate = SettingsTestGate()
            let stale = uncached
            let current = cached
            let model = SpeechModelSetup(
                selectedAlias: "speech",
                operations: .init(
                    list: {
                        await oldGate.pass()
                        if fails { throw FoundrySpeechModelError.catalogUnavailable }
                        return [stale]
                    },
                    download: { _ in
                        await downloadGate.pass()
                        return [current]
                    }))
            let old = model.refresh()
            await oldGate.waitForArrival()
            model.cancel()
            let newer = model.download()
            await downloadGate.waitForArrival()
            let status = model.status
            await oldGate.open()
            await old.value
            XCTAssertTrue(model.isDownloading)
            XCTAssertEqual(model.status, status)
            XCTAssertTrue(model.models.isEmpty)
            XCTAssertFalse(model.catalogLoaded)
            await downloadGate.open()
            await newer.value
            XCTAssertFalse(model.isDownloading)
            XCTAssertEqual(model.models, [cached])
            XCTAssertEqual(model.status, "This model is downloaded and ready.")
            XCTAssertFalse(model.canDownload)
        }
    }

    func testCancelledDownloadCannotOverwriteANewerCatalog() async {
        let gate = SettingsTestGate()
        let oldChoice = uncached
        let newChoice = cached
        let model = SpeechModelSetup(
            selectedAlias: "speech",
            operations: .init(
                list: { [newChoice] },
                download: { _ in
                    await gate.pass()
                    return [oldChoice]
                }))
        let download = model.download()
        await gate.waitForArrival()
        model.cancel()
        XCTAssertFalse(model.isDownloading)
        await model.refresh().value
        await gate.open()
        await download.value
        XCTAssertEqual(model.models, [cached])
        XCTAssertTrue(model.catalogLoaded)
        XCTAssertFalse(model.isDownloading)
        XCTAssertEqual(model.status, "This model is downloaded and ready.")
    }

    func testDownloadCapturesTheClickedAliasBeforeItsTaskStarts() async {
        let aliases = SpeechSetupAliases()
        let choice = cached
        let model = SpeechModelSetup(
            selectedAlias: "speech",
            operations: .init(
                list: { [choice] },
                download: { alias in
                    await aliases.record(alias)
                    return [choice]
                }))
        let task = model.download()
        model.select("other")
        await task.value
        let received = await aliases.values
        XCTAssertEqual(received, ["speech"])
        XCTAssertTrue(model.status.contains("does not list this saved alias"))
        XCTAssertFalse(model.isDownloading)
    }

    func testCancelBeforeTaskStartsDoesNoWorkAndRetainsTheCatalog() async {
        let aliases = SpeechSetupAliases()
        let choice = uncached
        let model = SpeechModelSetup(
            selectedAlias: "speech",
            operations: .init(
                list: { [choice] },
                download: { alias in
                    await aliases.record(alias)
                    return []
                }))
        await model.refresh().value
        XCTAssertTrue(model.canDownload)
        let task = model.download()
        model.cancel()
        await task.value
        let received = await aliases.values
        XCTAssertTrue(received.isEmpty)
        XCTAssertEqual(model.models, [uncached])
        XCTAssertTrue(model.catalogLoaded)
        XCTAssertTrue(model.canDownload)
    }

    func testCurrentSetupFailuresAreVisibleAndDoNotClaimAnEmptySuccess() async {
        let model = SpeechModelSetup(
            selectedAlias: "speech",
            operations: .init(
                list: { throw FoundrySpeechModelError.catalogUnavailable },
                download: { _ in throw FoundrySpeechModelError.downloadFailed }))
        await model.refresh().value
        XCTAssertFalse(model.catalogLoaded)
        XCTAssertTrue(model.status.contains("could not list"))
        await model.download().value
        XCTAssertFalse(model.isDownloading)
        XCTAssertTrue(model.status.contains("could not download"))
    }

    func testCurrentDownloadCancellationClearsBusyStateWithoutAcceptingLateSuccess() async {
        let gate = SettingsTestGate()
        let choice = cached
        let model = SpeechModelSetup(
            selectedAlias: "speech",
            operations: .init(
                list: { [choice] },
                download: { _ in
                    await gate.pass()
                    return [choice]
                }))
        let task = model.download()
        await gate.waitForArrival()
        task.cancel()
        await gate.open()
        await task.value
        XCTAssertFalse(model.isDownloading)
        XCTAssertFalse(model.catalogLoaded)
        XCTAssertTrue(model.models.isEmpty)
        XCTAssertEqual(model.status, "The model download was cancelled.")
    }

    func testReleasingThePageModelCancelsItsOutstandingWork() async throws {
        let gate = SettingsTestGate()
        let observed = SpeechSetupAliases()
        var model: SpeechModelSetup? = SpeechModelSetup(
            selectedAlias: "speech",
            operations: .init(
                list: {
                    await gate.pass()
                    if Task.isCancelled { await observed.record("cancelled") }
                    return []
                },
                download: { _ in [] }))
        weak var reference: SpeechModelSetup?
        reference = model
        let task = try XCTUnwrap(model).refresh()
        await gate.waitForArrival()
        model = nil
        XCTAssertNil(reference)
        await gate.open()
        await task.value
        let received = await observed.values
        XCTAssertEqual(received, ["cancelled"])
    }
}

private actor SpeechSetupAliases {
    private(set) var values: [String] = []
    func record(_ alias: String) { values.append(alias) }
}
