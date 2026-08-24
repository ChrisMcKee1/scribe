import AppKit
import XCTest
@testable import Scribe

final class OverlayAnchorTests: XCTestCase {
    private let screen = NSRect(x: 0, y: 0, width: 1000, height: 800)
    private let pillSize = NSSize(width: 200, height: 40)

    func testTopLeftOrigin() {
        let origin = OverlayAnchor.topLeft.origin(for: pillSize, in: screen, margin: 10)
        XCTAssertEqual(origin.x, 10, accuracy: 0.01)
        XCTAssertEqual(origin.y, 750, accuracy: 0.01) // 800 - 40 - 10
    }

    func testBottomRightOrigin() {
        let origin = OverlayAnchor.bottomRight.origin(for: pillSize, in: screen, margin: 10)
        XCTAssertEqual(origin.x, 790, accuracy: 0.01) // 1000 - 200 - 10
        XCTAssertEqual(origin.y, 10, accuracy: 0.01)
    }

    func testCenterOrigin() {
        let origin = OverlayAnchor.center.origin(for: pillSize, in: screen, margin: 10)
        XCTAssertEqual(origin.x, 400, accuracy: 0.01) // (1000 - 200) / 2
        XCTAssertEqual(origin.y, 380, accuracy: 0.01) // (800 - 40) / 2
    }

    func testBottomCenterOrigin() {
        let origin = OverlayAnchor.bottomCenter.origin(for: pillSize, in: screen, margin: 10)
        XCTAssertEqual(origin.x, 400, accuracy: 0.01)
        XCTAssertEqual(origin.y, 10, accuracy: 0.01)
    }

    func testAllNineAnchorsProduceOriginsWithinScreenBounds() {
        for anchor in OverlayAnchor.allCases {
            let origin = anchor.origin(for: pillSize, in: screen, margin: 10)
            XCTAssertGreaterThanOrEqual(origin.x, screen.minX, "\(anchor) x below screen bounds")
            XCTAssertLessThanOrEqual(origin.x + pillSize.width, screen.maxX, "\(anchor) x exceeds screen bounds")
            XCTAssertGreaterThanOrEqual(origin.y, screen.minY, "\(anchor) y below screen bounds")
            XCTAssertLessThanOrEqual(origin.y + pillSize.height, screen.maxY, "\(anchor) y exceeds screen bounds")
        }
    }

    func testDisplayNamesAreNonEmptyAndUnique() {
        let names = OverlayAnchor.allCases.map(\.displayName)
        XCTAssertEqual(Set(names).count, names.count)
        XCTAssertTrue(names.allSatisfy { !$0.isEmpty })
    }
}
