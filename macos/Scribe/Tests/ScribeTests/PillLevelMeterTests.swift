import XCTest

@testable import Scribe

final class PillLevelMeterTests: XCTestCase {
    private let callback = Duration.milliseconds(10)

    func testTargetMapsTheDbfsWindowToZeroOne() {
        XCTAssertEqual(PillLevelMeter.target(of: linear(PillLevelMeter.quietDbfs)), 0, accuracy: 1e-6)
        XCTAssertEqual(PillLevelMeter.target(of: linear(PillLevelMeter.fullDbfs)), 1, accuracy: 1e-6)
        XCTAssertEqual(
            PillLevelMeter.target(of: linear((PillLevelMeter.quietDbfs + PillLevelMeter.fullDbfs) / 2)),
            0.5,
            accuracy: 1e-6)
        XCTAssertEqual(PillLevelMeter.target(of: 1), 1, accuracy: 1e-6)
        XCTAssertEqual(PillLevelMeter.target(of: 4), 1, accuracy: 1e-6)
        XCTAssertEqual(PillLevelMeter.target(of: .infinity), 1, accuracy: 1e-6)
        XCTAssertEqual(PillLevelMeter.target(of: 0), 0, accuracy: 1e-6)
    }

    func testTheTypicalSpeechWindowFillsMostOfTheBars() {
        let median = PillLevelMeter.target(of: linear(-17))
        let loud = PillLevelMeter.target(of: linear(-12))
        let raised = PillLevelMeter.target(of: linear(-6))

        XCTAssertGreaterThan(median, 0.7)
        XCTAssertGreaterThan(loud, median)
        XCTAssertEqual(raised, 1, accuracy: 1e-6)
        XCTAssertGreaterThanOrEqual(PillLevelMeter.target(of: linear(-29)), 0.45)
        XCTAssertLessThanOrEqual(PillLevelMeter.target(of: linear(-29)), 0.6)
    }

    func testUpdateMovesTowardTheTargetWithTheAttackAndReleaseTimes() {
        var meter = PillLevelMeter()
        let target = PillLevelMeter.target(of: linear(-12))
        let rising = meter.update(linear(-12), sincePrevious: callback)

        XCTAssertGreaterThan(rising, 0)
        XCTAssertLessThan(rising, target)

        let peak = meter.update(linear(-12), sincePrevious: callback)
        XCTAssertGreaterThan(peak, rising)

        let falling = meter.update(0, sincePrevious: callback)
        XCTAssertLessThan(falling, peak)
        XCTAssertGreaterThan(falling, 0)
    }

    func testTheFirstUpdateUsesTheNominalStepAndResetReturnsToSilence() {
        var stalled = PillLevelMeter()
        var bounded = PillLevelMeter()
        let peak = linear(-40)

        let first = stalled.update(peak, sincePrevious: .seconds(2))
        let boundedFirst = bounded.update(peak, sincePrevious: .milliseconds(Int(PillLevelMeter.maxStepMs)))
        XCTAssertEqual(first, boundedFirst, accuracy: 1e-9)

        stalled.reset()
        XCTAssertEqual(stalled.level, 0, accuracy: 1e-9)
        let fresh = stalled.update(linear(-12), sincePrevious: callback)
        var control = PillLevelMeter()
        let freshMeter = control.update(linear(-12), sincePrevious: callback)
        XCTAssertEqual(fresh, freshMeter, accuracy: 1e-9)
    }

    private func linear(_ dbfs: Double) -> Float {
        Float(pow(10, dbfs / 20))
    }
}
