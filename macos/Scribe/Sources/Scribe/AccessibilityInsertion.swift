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
///
/// Each request can take up to the messaging timeout, so the calling task's cancellation is checked before
/// every question and before the first write, returning `cancelled` with nothing written. Once any write
/// has been sent there are no more checks: the insertion finishes the path it started, so a cancellation
/// can never turn a write that may still land into a report that nothing was delivered.
enum AccessibilityInsertion {
    @MainActor
    static func insert(
        _ text: String,
        into element: AXUIElement,
        using access: some AccessibilityAttributeAccess
    ) -> AccessibilityInsertionOutcome {
        if Task.isCancelled {
            return .cancelled
        }

        let selectedText = kAXSelectedTextAttribute as CFString
        let settable = access.isSettable(selectedText, on: element)
        if settable.result == .cannotComplete {
            return .unresponsive
        }

        guard settable.result == .success, settable.settable else {
            return spliceIntoValue(text, of: element, using: access, cancellable: true)
        }

        if Task.isCancelled {
            return .cancelled
        }

        let result = access.setValue(text as CFString, of: selectedText, on: element)
        if result == .success {
            return .inserted
        }
        if result == .cannotComplete {
            return .unconfirmed
        }

        // The application refused the write, so nothing was inserted, but a write has been sent: from here
        // the path runs to its end without cancellation checks.
        return spliceIntoValue(text, of: element, using: access, cancellable: false)
    }

    /// For elements whose selected text cannot be set: replaces the selection inside the whole value, then
    /// puts the caret after the inserted text. `cancellable` is false once a write has been sent.
    @MainActor
    private static func spliceIntoValue(
        _ text: String,
        of element: AXUIElement,
        using access: some AccessibilityAttributeAccess,
        cancellable: Bool
    ) -> AccessibilityInsertionOutcome {
        let valueAttribute = kAXValueAttribute as CFString
        let rangeAttribute = kAXSelectedTextRangeAttribute as CFString
        func cancelled() -> Bool {
            cancellable && Task.isCancelled
        }

        if cancelled() {
            return .cancelled
        }
        let settable = access.isSettable(valueAttribute, on: element)
        if settable.result == .cannotComplete {
            return .unresponsive
        }
        guard settable.result == .success, settable.settable else {
            return .notInserted
        }

        if cancelled() {
            return .cancelled
        }
        let current = access.value(of: valueAttribute, on: element)
        if current.result == .cannotComplete {
            return .unresponsive
        }
        guard current.result == .success, let currentValue = current.value as? String else {
            return .notInserted
        }

        if cancelled() {
            return .cancelled
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

        if cancelled() {
            return .cancelled
        }
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
