import XCTest

@testable import Scribe

final class PillLevelBarsTests: XCTestCase {
    private let barDip = 16.0

    func testTheFloorKeepsEveryBarAtFourDipInSilence() {
        XCTAssertEqual(PillLevelBars.floor, 0.25, accuracy: 1e-12)
        for bar in 0..<PillLevelBars.count {
            XCTAssertEqual(PillLevelBars.scale(of: bar, level: 0) * barDip, 4, accuracy: 1e-9)
        }
    }

    func testTheFullLevelMatchesTheIconProportions() {
        let expected = [7.12, 10.72, 16.0, 10.72, 7.12]
        for bar in 0..<PillLevelBars.count {
            XCTAssertEqual(PillLevelBars.scale(of: bar, level: 1) * barDip, expected[bar], accuracy: 1e-9)
            let travel = (PillLevelBars.scale(of: bar, level: 1) - PillLevelBars.floor) / (1 - PillLevelBars.floor)
            XCTAssertEqual(travel, PillLevelBars.proportion(bar), accuracy: 1e-12)
        }
        XCTAssertEqual((0..<PillLevelBars.count).map(PillLevelBars.proportion), [0.26, 0.56, 1.0, 0.56, 0.26])
    }

    func testEveryBarMovesSymmetricallyWithinTheRange() {
        for bar in 0..<PillLevelBars.count {
            XCTAssertEqual(
                PillLevelBars.scale(of: bar, level: 0.5),
                PillLevelBars.scale(of: PillLevelBars.count - 1 - bar, level: 0.5),
                accuracy: 1e-12)
        }
        for level in stride(from: -0.5, through: 1.5, by: 0.1) {
            for bar in 0..<PillLevelBars.count {
                let scale = PillLevelBars.scale(of: bar, level: level)
                XCTAssertGreaterThanOrEqual(scale, PillLevelBars.floor)
                XCTAssertLessThanOrEqual(scale, 1)
            }
        }
    }
}
