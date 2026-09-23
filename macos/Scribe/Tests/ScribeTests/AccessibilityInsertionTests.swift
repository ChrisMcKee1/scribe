import ApplicationServices
import XCTest
@testable import Scribe

/// Stands in for the Accessibility attribute requests of one element, so a test chooses which request
/// times out and sees every write that was sent.
@MainActor
final class ScriptedAccessibilityAttributes: AccessibilityAttributeAccess {
    struct Write {
        let attribute: String
        let value: CFTypeRef
    }

    var settable: Set<String> = []
    var values: [String: CFTypeRef] = [:]
    /// Requests that time out, named like "isSettable AXValue", "value AXValue" or "setValue AXValue".
    var timeouts: Set<String> = []
    /// Writes that are refused outright, named like `timeouts`.
    var refusals: Set<String> = []
    private(set) var writes: [Write] = []

    func isSettable(_ attribute: CFString, on element: AXUIElement) -> (result: AXError, settable: Bool) {
        let name = attribute as String
        if timeouts.contains("isSettable \(name)") {
            return (.cannotComplete, false)
        }
        return (.success, settable.contains(name))
    }

    func value(of attribute: CFString, on element: AXUIElement) -> (result: AXError, value: CFTypeRef?) {
        let name = attribute as String
        if timeouts.contains("value \(name)") {
            return (.cannotComplete, nil)
        }
        guard let value = values[name] else {
            return (.noValue, nil)
        }
        return (.success, value)
    }

    func setValue(_ value: CFTypeRef, of attribute: CFString, on element: AXUIElement) -> AXError {
        let name = attribute as String
        // Recorded even when it times out: the request was sent, which is what makes it uncertain.
        writes.append(Write(attribute: name, value: value))
        if timeouts.contains("setValue \(name)") {
            return .cannotComplete
        }
        if refusals.contains("setValue \(name)") {
            return .failure
        }
        values[name] = value
        return .success
    }
}

final class AccessibilityInsertionTests: XCTestCase {
    private let selectedText = kAXSelectedTextAttribute as String
    private let value = kAXValueAttribute as String
    private let selectedRange = kAXSelectedTextRangeAttribute as String
    private let element = AXUIElementCreateApplication(4_003)

    @MainActor
    func testSettableSelectedTextIsSetDirectly() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [selectedText, value]

        let outcome = AccessibilityInsertion.insert("dictated", into: element, using: attributes)

        XCTAssertEqual(outcome, .inserted)
        XCTAssertEqual(attributes.writes.map(\.attribute), [selectedText])
        XCTAssertEqual(attributes.writes.first?.value as? String, "dictated")
    }

    @MainActor
    func testATimedOutQuestionBeforeAnyWriteIsUnresponsive() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [selectedText]
        attributes.timeouts = ["isSettable \(selectedText)"]

        XCTAssertEqual(AccessibilityInsertion.insert("dictated", into: element, using: attributes), .unresponsive)
        XCTAssertTrue(attributes.writes.isEmpty)
    }

    @MainActor
    func testATimedOutSelectedTextWriteIsUnconfirmedAndNothingElseIsTried() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [selectedText, value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(0, 0)]
        attributes.timeouts = ["setValue \(selectedText)"]

        XCTAssertEqual(AccessibilityInsertion.insert("dictated", into: element, using: attributes), .unconfirmed)
        XCTAssertEqual(attributes.writes.map(\.attribute), [selectedText])
    }

    @MainActor
    func testARefusedSelectedTextWriteFallsBackToTheValue() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [selectedText, value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]
        attributes.refusals = ["setValue \(selectedText)"]

        XCTAssertEqual(AccessibilityInsertion.insert("there", into: element, using: attributes), .inserted)
        XCTAssertEqual(attributes.values[value] as? String, "Hello there")
    }

    @MainActor
    func testWithoutSettableSelectedTextTheValueIsSplicedAtTheSelection() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]

        let outcome = AccessibilityInsertion.insert("there", into: element, using: attributes)

        XCTAssertEqual(outcome, .inserted)
        XCTAssertEqual(attributes.writes.map(\.attribute), [value, selectedRange])
        XCTAssertEqual(attributes.values[value] as? String, "Hello there")
        XCTAssertEqual(decodedRange(attributes.values[selectedRange])?.location, 11)
        XCTAssertEqual(decodedRange(attributes.values[selectedRange])?.length, 0)
    }

    @MainActor
    func testASelectionPastTheEndIsClampedToTheValue() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [value]
        attributes.values = [value: "abc" as CFString, selectedRange: range(50, 10)]

        XCTAssertEqual(AccessibilityInsertion.insert("def", into: element, using: attributes), .inserted)
        XCTAssertEqual(attributes.values[value] as? String, "abcdef")
        XCTAssertEqual(decodedRange(attributes.values[selectedRange])?.location, 6)
        XCTAssertEqual(decodedRange(attributes.values[selectedRange])?.length, 0)
    }

    @MainActor
    func testATimedOutValueReadWritesNothing() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]
        attributes.timeouts = ["value \(value)"]

        XCTAssertEqual(AccessibilityInsertion.insert("there", into: element, using: attributes), .unresponsive)
        XCTAssertTrue(attributes.writes.isEmpty)
    }

    @MainActor
    func testATimedOutSelectionReadWritesNothing() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]
        attributes.timeouts = ["value \(selectedRange)"]

        XCTAssertEqual(AccessibilityInsertion.insert("there", into: element, using: attributes), .unresponsive)
        XCTAssertTrue(attributes.writes.isEmpty)
    }

    @MainActor
    func testATimedOutValueWriteIsUnconfirmed() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]
        attributes.timeouts = ["setValue \(value)"]

        XCTAssertEqual(AccessibilityInsertion.insert("there", into: element, using: attributes), .unconfirmed)
        XCTAssertEqual(attributes.writes.map(\.attribute), [value])
    }

    @MainActor
    func testATimedOutCaretMoveAfterTheTextIsInStillCountsAsInserted() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.settable = [value]
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]
        attributes.timeouts = ["setValue \(selectedRange)"]

        XCTAssertEqual(AccessibilityInsertion.insert("there", into: element, using: attributes), .inserted)
        XCTAssertEqual(attributes.values[value] as? String, "Hello there")
    }

    @MainActor
    func testAnElementWithNothingSettableGetsNothing() {
        let attributes = ScriptedAccessibilityAttributes()
        attributes.values = [value: "Hello world" as CFString, selectedRange: range(6, 5)]

        XCTAssertEqual(AccessibilityInsertion.insert("there", into: element, using: attributes), .notInserted)
        XCTAssertTrue(attributes.writes.isEmpty)
    }

    private func range(_ location: Int, _ length: Int) -> CFTypeRef {
        var range = CFRange(location: location, length: length)
        return AXValueCreate(.cfRange, &range)!
    }

    private func decodedRange(_ reference: CFTypeRef?) -> CFRange? {
        guard let reference, CFGetTypeID(reference) == AXValueGetTypeID() else {
            return nil
        }
        var range = CFRange()
        guard AXValueGetValue(reference as! AXValue, .cfRange, &range) else {
            return nil
        }
        return range
    }
}
