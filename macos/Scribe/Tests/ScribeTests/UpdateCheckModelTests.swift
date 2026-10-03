import XCTest

@testable import Scribe

@MainActor
final class UpdateCheckModelTests: XCTestCase {
    func testOldResultsCannotOverwriteOrClearANewerCheck() async {
        for oldResult in [
            UpdateCheckResult.upToDate(current: "old"),
            .failed(message: "old failure"),
            .updateAvailable(
                current: "old", latest: "99",
                url: URL(string: "https://github.com/ChrisMcKee1/scribe/releases/tag/v99")!),
        ] {
            let oldGate = SettingsTestGate()
            let newGate = SettingsTestGate()
            let model = UpdateCheckModel(check: { version in
                if version == "old" {
                    await oldGate.pass()
                    return oldResult
                }
                await newGate.pass()
                return .noMacRelease
            })
            let old = model.start(currentVersion: "old")
            await oldGate.waitForArrival()
            model.cancel()
            let newer = model.start(currentVersion: "new")
            await newGate.waitForArrival()
            await oldGate.open()
            await old.value
            XCTAssertTrue(model.isChecking)
            XCTAssertNil(model.result)
            await newGate.open()
            await newer.value
            XCTAssertFalse(model.isChecking)
            XCTAssertEqual(model.result, .noMacRelease)
        }
    }

    func testLeavingAboutRejectsLateSuccessAndAllowsNextCheck() async {
        let gate = SettingsTestGate()
        let model = UpdateCheckModel(check: { version in
            if version == "old" { await gate.pass() }
            return .upToDate(current: version)
        })
        let old = model.start(currentVersion: "old")
        await gate.waitForArrival()
        model.cancel()
        XCTAssertFalse(model.isChecking)
        await model.start(currentVersion: "new").value
        await gate.open()
        await old.value
        XCTAssertEqual(model.result, .upToDate(current: "new"))
        model.cancel()
        XCTAssertEqual(model.result, .upToDate(current: "new"))
    }

    func testCancelBeforeTaskStartsDoesNotCheckTheNetwork() async {
        let model = UpdateCheckModel(check: { _ in
            XCTFail("Cancelled work must not invoke the check")
            return .noMacRelease
        })
        let task = model.start(currentVersion: "1.0.0")
        model.cancel()
        await task.value
        XCTAssertFalse(model.isChecking)
        XCTAssertNil(model.result)
    }

    func testCurrentCancellationRefusesNoncooperativeSuccess() async {
        let gate = SettingsTestGate()
        let model = UpdateCheckModel(check: { _ in
            await gate.pass()
            return .noMacRelease
        })
        let task = model.start(currentVersion: "1.0.0")
        await gate.waitForArrival()
        task.cancel()
        await gate.open()
        await task.value
        XCTAssertFalse(model.isChecking)
        XCTAssertEqual(model.result, .failed(message: "The update check was cancelled."))
    }

    func testVersionIsCapturedAndCurrentFailureIsVisible() async {
        let model = UpdateCheckModel(check: { version in .failed(message: version) })
        await model.start(currentVersion: "clicked-version").value
        XCTAssertEqual(model.result, .failed(message: "clicked-version"))
        XCTAssertFalse(model.isChecking)
    }

    func testReleasingThePageModelCancelsOutstandingWork() async {
        let gate = SettingsTestGate()
        var model: UpdateCheckModel? = UpdateCheckModel(check: { _ in
            await gate.pass()
            XCTAssertTrue(Task.isCancelled)
            return .noMacRelease
        })
        weak var weakModel: UpdateCheckModel?
        weakModel = model
        let task = model!.start(currentVersion: "1.0.0")
        await gate.waitForArrival()
        model = nil
        XCTAssertNil(weakModel)
        await gate.open()
        await task.value
    }
}
