import XCTest

@testable import Scribe

@MainActor
final class LocalAppSettingsModelTests: XCTestCase {
    private let current = LocalServerState(
        reach: .reached,
        models: [LocalServerModel("current", "Current", 10, maxContextTokens: 4096)],
        loaded: [LocalServerLoadedModel("current", 10, contextTokens: 4096)])

    func testOldModelListCannotOverwriteTheNewerReply() async {
        let oldGate = SettingsTestGate()
        let expected = current
        let model = LocalAppSettingsModel(read: { endpoint in
            if endpoint == "old" {
                await oldGate.pass()
                return .failed
            }
            return expected
        })
        let old = Task { await model.refresh(for: .ollama, endpoint: "old") }
        await oldGate.waitForArrival()
        await model.refresh(for: .ollama, endpoint: "new")
        XCTAssertEqual(model.state(for: .ollama), current)
        await oldGate.open()
        await old.value
        XCTAssertEqual(model.state(for: .ollama), current)
    }

    func testOldReplyCannotClearTheNewerRefreshLoadingState() async {
        let oldGate = SettingsTestGate()
        let newGate = SettingsTestGate()
        let expected = current
        let model = LocalAppSettingsModel(read: { endpoint in
            if endpoint == "initial" { return expected }
            await (endpoint == "old" ? oldGate : newGate).pass()
            return expected
        })
        await model.refresh(for: .lmStudio, endpoint: "initial")
        XCTAssertEqual(model.state(for: .lmStudio), current)
        let old = Task { await model.refresh(for: .lmStudio, endpoint: "old") }
        await oldGate.waitForArrival()
        let newer = Task { await model.refresh(for: .lmStudio, endpoint: "new") }
        await newGate.waitForArrival()
        await oldGate.open()
        await old.value
        XCTAssertNil(model.state(for: .lmStudio))
        await newGate.open()
        await newer.value
        XCTAssertEqual(model.state(for: .lmStudio), current)
    }

    func testCancelledReadCannotPublishLateModelsOrCapacity() async {
        let gate = SettingsTestGate()
        let expected = current
        let model = LocalAppSettingsModel(read: { endpoint in
            if endpoint == "cancelled" {
                await gate.pass()
                return expected
            }
            return .notRunning
        })
        await model.refresh(for: .ollama, endpoint: "initial")
        let task = Task { await model.refresh(for: .ollama, endpoint: "cancelled") }
        await gate.waitForArrival()
        task.cancel()
        await gate.open()
        await task.value
        XCTAssertEqual(model.state(for: .ollama), .notRunning)
    }

    func testAppRefreshesAreIndependentAndCancelledAdmissionReadsNothing() async {
        let gate = SettingsTestGate()
        let expected = current
        let model = LocalAppSettingsModel(read: { endpoint in
            if endpoint == "held" {
                await gate.pass()
                return expected
            }
            return .needsKey
        })
        let held = Task { await model.refresh(for: .ollama, endpoint: "held") }
        await gate.waitForArrival()
        await model.refresh(for: .lmStudio, endpoint: "independent")
        XCTAssertNil(model.state(for: .ollama))
        XCTAssertEqual(model.state(for: .lmStudio), .needsKey)
        let cancelled = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            await model.refresh(for: .ollama, endpoint: "must-not-read")
        }
        await cancelled.value
        await gate.open()
        await held.value
        XCTAssertEqual(model.state(for: .ollama), current)
    }
}
