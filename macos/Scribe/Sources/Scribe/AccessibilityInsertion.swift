import ApplicationServices
import Foundation

/// The Accessibility requests an insertion makes, separate from `InjectionSystem` so which request timed
/// out, and so whether anything was written, can be tested without Accessibility permission.
@MainActor
protocol AccessibilityAttributeAccess {
    func isSettable(_ attribute: CFString, on element: AXUIElement) -> (result: AXError, settable: Bool)
    func value(of attribute: CFString, on element: AXUIElement) -> (result: AXError, value: CFTypeRef?)
    func setValue(_ value: CFTypeRef, of attribute: CFString, on element: AXUIElement) -> AXError
}

/// Inserts text into a focused element through Accessibility: through its selected text when that can be
/// set, otherwise by splicing the text into its whole value at the selection.
///
/// A request that times out (`kAXErrorCannotComplete`) is sorted by what it was. A question that goes
/// unanswered means nothing was written: `unresponsive`. A write that goes unanswered was still sent, and
/// the application may apply it whenever it catches up: `unconfirmed`, which must never be followed by a
/// paste or keystrokes, since those could insert the text a second time.
enum AccessibilityInsertion {
    @MainActor
    static func insert(
        _ text: String,
        into element: AXUIElement,
        using access: some AccessibilityAttributeAccess
    ) -> AccessibilityInsertionOutcome {
        let selectedText = kAXSelectedTextAttribute as CFString
        let settable = access.isSettable(selectedText, on: element)
        if settable.result == .cannotComplete {
            return .unresponsive
        }

        if settable.result == .success, settable.settable {
            let result = access.setValue(text as CFString, of: selectedText, on: element)
            if result == .success {
                return .inserted
            }
            if result == .cannotComplete {
                return .unconfirmed
            }
        }

        return spliceIntoValue(text, of: element, using: access)
    }

    /// For elements whose selected text cannot be set: replaces the selection inside the whole value, then
    /// puts the caret after the inserted text.
    @MainActor
    private static func spliceIntoValue(
        _ text: String,
        of element: AXUIElement,
        using access: some AccessibilityAttributeAccess
    ) -> AccessibilityInsertionOutcome {
        let valueAttribute = kAXValueAttribute as CFString
        let rangeAttribute = kAXSelectedTextRangeAttribute as CFString

        let settable = access.isSettable(valueAttribute, on: element)
        if settable.result == .cannotComplete {
            return .unresponsive
        }
        guard settable.result == .success, settable.settable else {
            return .notInserted
        }

        let current = access.value(of: valueAttribute, on: element)
        if current.result == .cannotComplete {
            return .unresponsive
        }
        guard current.result == .success, let currentValue = current.value as? String else {
            return .notInserted
        }

        let selection = access.value(of: rangeAttribute, on: element)
        if selection.result == .cannotComplete {
            return .unresponsive
        }
        guard
            selection.result == .success,
            let selectionValue = selection.value,
            CFGetTypeID(selectionValue) == AXValueGetTypeID()
        else {
            return .notInserted
        }

        let rangeValue = selectionValue as! AXValue
        var range = CFRange()
        guard AXValueGetType(rangeValue) == .cfRange, AXValueGetValue(rangeValue, .cfRange, &range) else {
            return .notInserted
        }

        let value = currentValue as NSString
        let location = max(0, min(range.location, value.length))
        let length = max(0, min(range.length, value.length - location))
        let updated = value.replacingCharacters(in: NSRange(location: location, length: length), with: text)

        let result = access.setValue(updated as CFString, of: valueAttribute, on: element)
        if result == .cannotComplete {
            return .unconfirmed
        }
        guard result == .success else {
            return .notInserted
        }

        // Best effort: the text is in, and only the caret position is left to put right.
        var caret = CFRange(location: location + (text as NSString).length, length: 0)
        if let caretValue = AXValueCreate(.cfRange, &caret) {
            _ = access.setValue(caretValue, of: rangeAttribute, on: element)
        }
        return .inserted
    }
}
